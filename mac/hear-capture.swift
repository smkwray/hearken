// Native CoreAudio capture + accept-first TCP server.
// Replaces the glitchy `ffmpeg -f avfoundation` capture. Captures the given
// audio device (by UID) via an input AudioQueue and streams raw s16le 48k
// stereo to a single connected TCP client. Re-accepts on disconnect.
//   usage: hear-capture <port> <deviceUID>
//          hear-capture --self-test
import Foundation
import AudioToolbox

let args = CommandLine.arguments
let PORT = UInt16(args.count > 1 ? args[1] : "45000") ?? 45000
let DEVUID = (args.count > 2 ? args[2] : "BlackHole2ch_UID") as CFString

var clientFD: Int32 = -1
var mainLoop: CFRunLoop! = nil

// --- silence suppression (squelch) ---------------------------------------
// Don't ship a constant ~1.5 Mbps of digital silence: stop sending once the source has been
// silent for the profile's confirmation duration, resume instantly on the first real frame, and
// trickle a heartbeat while suppressed so the path and the receiver's blocking read stay warm.
//
// The threshold and the duration are NOT tunable here. They are one shared contract with the
// receiver (profile/squelch.profile -> SquelchProfile.swift / SquelchProfile.cs), and the
// disagreement is unsafe in one direction: a receiver confirming silence sooner than this sender
// suppresses forgives a real transport stall as if it were source silence.
//
// The one remaining switch is a test-only continuous-PCM mode, and it is coordinated rather than
// tunable: BRIDGE_CONTINUOUS_PCM=1 disables suppression here, and the receiver must be started in
// the same mode so that its silence forgiveness is disabled with it. A mixed pair is the failure
// this whole subsystem exists to prevent.
let continuousPCM = (ProcessInfo.processInfo.environment["BRIDGE_CONTINUOUS_PCM"] ?? "0") == "1"

/// The sender's silence state machine, deliberately isolated from CoreAudio, sockets and the wall
/// clock so the falsification court can drive it directly.
///
///     TRANSMIT        silentFrames == 0
///     QUALIFYING(n)   1 <= n < SquelchProfile.confirmFrames
///     SUPPRESSED      the confirmFrames'th contiguous silent frame has been transmitted
///
/// Classification is per stereo frame at exact offsets inside the caller's buffer. One capture
/// buffer may contain active -> silent -> active runs and every transition lands on its own frame.
/// Whole-buffer classification is prohibited: it makes the transition frame a function of the
/// capture quantum, so the sender and receiver would disagree about where silence began whenever
/// BRIDGE_AQ_BUF_BYTES changed.
struct SenderSquelchState {
    enum State { case transmit, qualifying, suppressed }

    /// Length of the current contiguous silent run, capped at the confirmation duration.
    private(set) var silentFrames = 0
    private(set) var suppressed = false
    /// Last send-queue drop generation this state machine has observed. Qualification is only
    /// valid if the receiver can eventually see the tail, so a local drop-oldest voids it.
    private(set) var dropGeneration: UInt64 = 0
    /// One coordinated switch, not a threshold knob: false transmits every captured frame.
    let suppressionEnabled: Bool

    private(set) var enterCount: UInt64 = 0        // qualification completed
    private(set) var exitCount: UInt64 = 0         // first active frame after suppression
    private(set) var dropInvalidations: UInt64 = 0 // qualification revoked by a queue drop
    private(set) var transmittedFrames: UInt64 = 0
    private(set) var suppressedFrames: UInt64 = 0
    private(set) var heartbeats: UInt64 = 0
    /// Bytes handed in that did not complete a stereo frame. CoreAudio does not produce these for
    /// a packed 4-byte format; counted rather than silently discarded so that if it ever did, the
    /// telemetry would say so instead of the stream quietly losing frame alignment.
    private(set) var misalignedTailBytes: UInt64 = 0

    init(suppressionEnabled: Bool = true) { self.suppressionEnabled = suppressionEnabled }

    var state: State {
        if suppressed { return .suppressed }
        return silentFrames > 0 ? .qualifying : .transmit
    }

    var stateName: String {
        switch state {
        case .transmit: return "transmit"
        case .qualifying: return "qualifying"
        case .suppressed: return "suppressed"
        }
    }

    /// Every newly accepted TCP connection starts in TRANSMIT with silentFrames = 0, whatever the
    /// source was doing a moment earlier. A receiver that connects during silence must be given a
    /// fresh full qualifying tail; it has no other way to learn that the source is quiet.
    mutating func beginConnection(dropGeneration gen: UInt64) {
        suppressed = false
        silentFrames = 0
        dropGeneration = gen
    }

    mutating func recordHeartbeat() { heartbeats &+= 1 }

    /// Sender queue-drop rule. Any drop-oldest in the send queue invalidates qualification: the
    /// sender leaves SUPPRESSED and restarts the count, so a locally discarded tail can never
    /// manufacture a suppressed state the receiver was never told about. The enqueue that caused
    /// the drop does not count toward the fresh run — counting resumes on the next silent frame.
    mutating func observeQueueGeneration(_ gen: UInt64) {
        if gen == dropGeneration { return }
        dropGeneration = gen
        dropInvalidations &+= 1
        suppressed = false
        silentFrames = 0
    }

    /// Classify `byteCount` bytes of pcm-s16le stereo at `base` and hand the caller the spans that
    /// must go on the wire as (frameOffset, frameCount) into that same buffer. `emit` returns the
    /// send queue's drop generation after the enqueue, which is how a drop is fed back in stream
    /// order. `emit` is non-escaping and is never called with an empty span.
    mutating func process(_ base: UnsafeRawPointer, byteCount: Int, emit: (Int, Int) -> UInt64) {
        let frames = byteCount / SquelchProfile.frameBytes
        misalignedTailBytes &+= UInt64(byteCount - frames * SquelchProfile.frameBytes)
        if frames <= 0 { return }
        if !suppressionEnabled { flush(0, frames, emit); return }

        var spanStart = -1     // frame offset of the run currently destined for the wire
        var i = 0
        // scan: per frame, at exact offsets inside this buffer
        while i < frames {
            let off = i * SquelchProfile.frameBytes
            let left = base.loadUnaligned(fromByteOffset: off, as: Int16.self)
            let right = base.loadUnaligned(fromByteOffset: off + 2, as: Int16.self)
            if SquelchProfile.frameIsSilent(left, right) {
                if suppressed {
                    suppressedFrames &+= 1
                    i += 1
                    continue
                }
                if spanStart < 0 { spanStart = i }
                silentFrames += 1
                if silentFrames >= SquelchProfile.confirmFrames {
                    // The confirmation frame itself is transmitted; suppression begins with the
                    // NEXT silent frame. Flushing here is what puts the boundary between them.
                    suppressed = true
                    enterCount &+= 1
                    flush(spanStart, i - spanStart + 1, emit)
                    spanStart = -1
                }
                i += 1
            } else {
                if suppressed {
                    // SUPPRESSED -> TRANSMIT happens BEFORE this frame is queued, so the first
                    // active frame is never lost as a state-transition casualty.
                    suppressed = false
                    exitCount &+= 1
                }
                silentFrames = 0   // QUALIFYING -> TRANSMIT: reset before handling this frame
                if spanStart < 0 { spanStart = i }
                i += 1
            }
        }
        if spanStart >= 0 { flush(spanStart, frames - spanStart, emit) }
    }

    private mutating func flush(_ offset: Int, _ count: Int, _ emit: (Int, Int) -> UInt64) {
        if count <= 0 { return }
        transmittedFrames &+= UInt64(count)
        observeQueueGeneration(emit(offset, count))
    }
}

var senderSquelch = SenderSquelchState(suppressionEnabled: !continuousPCM)
// A frame-aligned all-silent heartbeat. Its size carries no policy meaning — only the interval
// does, and only to the receiver's blocking-read timeout. Allocated once, outside the callback.
let heartbeatFrames = 64
let heartbeatBytes = heartbeatFrames * SquelchProfile.frameBytes
let heartbeatBuffer: UnsafeMutableRawPointer = {
    let p = UnsafeMutableRawPointer.allocate(byteCount: heartbeatBytes, alignment: 16)
    memset(p, 0, heartbeatBytes)
    return p
}()
let heartbeatInterval = Double(SquelchProfile.heartbeatMs) / 1000.0
var lastSentTime = 0.0

/// The heartbeat is connection liveness only: it exists so the receiver's blocking read does not
/// time out during genuine source silence. It is not a media record, carries no policy, and must
/// never fire while media is flowing.
@inline(__always)
func heartbeatDue(_ suppressed: Bool, _ now: Double, _ lastSent: Double) -> Bool {
    return suppressed && now - lastSent >= heartbeatInterval
}

@discardableResult
func writeAll(_ fd: Int32, _ p: UnsafeRawPointer, _ len: Int) -> Bool {
    var off = 0
    while off < len {
        let n = write(fd, p + off, len - off)
        if n <= 0 { return false }
        off += n
    }
    return true
}

// --- decoupled sender ------------------------------------------------------
// The AudioQueue callback must NEVER block: a blocking write() during a network
// hiccup stalls the callback, the (few) capture buffers overrun, and samples are
// lost at the source — audible crackle no receiver-side buffer can hide. So the
// callback only copies into this queue; a dedicated thread does the blocking I/O.
// On overflow (sustained stall) we drop the OLDEST audio, frame-aligned, so the
// stream stays fresh rather than building latency.

/// Fixed-capacity circular byte queue for the capture -> sender handoff.
///
/// It replaces a `Data` queue, which is forbidden on a capture callback: the first `append`
/// allocates, a growing `append` reallocates, `removeFirst` moves every retained byte down, and
/// once the sender thread holds a reference the copy-on-write copy lands on whichever thread
/// writes next — the callback. Reserved capacity is a hope there, not a guarantee.
///
/// This owns ONE allocation, made when the global is first touched (forced before
/// `AudioQueueStart`) and never repeated. `push` and `drain` are memcpy and integer arithmetic:
/// no allocation, no growth, no object per frame.
struct SendRing {
    let capacity: Int
    private let base: UnsafeMutableRawPointer
    private var head = 0            // offset of the oldest queued byte
    private(set) var count = 0      // bytes queued

    init(capacity: Int) {
        self.capacity = capacity
        base = UnsafeMutableRawPointer.allocate(byteCount: capacity, alignment: 16)
    }

    var isEmpty: Bool { count == 0 }

    mutating func reset() { head = 0; count = 0 }

    /// Copy `len` bytes in, discarding the OLDEST bytes — frame-aligned — when they do not fit.
    /// Returns the number of bytes discarded, which is exactly what the drop telemetry counts.
    @discardableResult
    mutating func push(_ p: UnsafeRawPointer, _ len: Int) -> Int {
        let over = count + len - capacity
        if over <= 0 { copyIn(p, len); return 0 }
        // Frame-aligned: the raw profile has no resync, so a queue that began mid-frame would
        // swap the channels for the rest of the connection.
        let frame = SquelchProfile.frameBytes
        let discard = (over + frame - 1) / frame * frame
        let fromQueue = min(count, discard)
        head = (head + fromQueue) % capacity
        count -= fromQueue
        // Reached only by a single buffer larger than the whole queue (BRIDGE_AQ_BUF_BYTES above
        // the ceiling): keep its newest bytes, which is the same drop-oldest rule one step later.
        let fromInput = discard - fromQueue
        copyIn(p + fromInput, len - fromInput)
        return discard
    }

    /// Copy the whole queue out to `dst` (which must have room for `capacity` bytes) and empty it.
    /// The sender thread does this under the lock, then writes from `dst` with the lock released.
    mutating func drain(into dst: UnsafeMutableRawPointer) -> Int {
        let n = count
        let first = min(n, capacity - head)
        memcpy(dst, base + head, first)
        if n > first { memcpy(dst + first, base, n - first) }
        head = (head + n) % capacity
        count = 0
        return n
    }

    private mutating func copyIn(_ p: UnsafeRawPointer, _ len: Int) {
        let tail = (head + count) % capacity
        let first = min(len, capacity - tail)
        memcpy(base + tail, p, first)
        if len > first { memcpy(base, p + first, len - first) }
        count += len
    }
}

let sendCond = NSCondition()
var senderDead = false
let sendPendingMax = 96 * 1024      // ~0.5s ceiling during a stall, then drop-oldest
var sendRing = SendRing(capacity: sendPendingMax)
var pendingSince = 0.0
var captureFrames: UInt64 = 0
var lastCallbackTime = 0.0
var maxCallbackGapMs = 0.0
var pendingHighWater = 0
var dropEvents: UInt64 = 0
var dropBytes: UInt64 = 0
var maxDropAgeMs = 0.0
// Monotonic across the process. Any drop-oldest bumps it; the squelch state machine treats a
// change as proof that the qualifying tail it counted may never reach the receiver.
var senderDropGeneration: UInt64 = 0
// Latest-wins copy of the squelch state, published under the queue lock. The state machine itself
// belongs to the capture callback and must not be read from the telemetry thread.
var senderSquelchPublished = SenderSquelchState()
// Audio handed to writeAll but not yet written: it has left the send ring and is not
// on the wire. Published before the blocking write so a stalled write is visible
// while it stalls, instead of only being counted after it finally returns.
var inflightBytes = 0
var inflightSince = 0.0
var senderGeneration: UInt64 = 0
var writeCalls: UInt64 = 0
var writeBytes: UInt64 = 0
var maxWriteMs = 0.0
var writesOver20: UInt64 = 0
var writesOver100: UInt64 = 0
var writesOver250: UInt64 = 0
var effectiveSendBuffer = 0
/// One drop event: fixed numbers, stored by the callback into a slot reserved at startup. Swift
/// Arrays were wrong here for the same reason `Data` was wrong for the queue — a subscript store
/// carries a copy-on-write branch that allocates, and static reachability from an audio callback is
/// the defect, not whether the branch happens to be taken today.
struct DropEvent { var bytes = 0; var ageMs = 0.0; var at = 0.0 }
let dropEventCapacity = 128
let dropEventRing: UnsafeMutablePointer<DropEvent> = {
    let p = UnsafeMutablePointer<DropEvent>.allocate(capacity: dropEventCapacity)
    p.initialize(repeating: DropEvent(), count: dropEventCapacity)
    return p
}()
var dropEventRead = 0
var dropEventWrite = 0
var dropEventLost: UInt64 = 0

// Callback metrics are recorded under the existing short queue lock. Formatting
// and log I/O stay on the sender and telemetry threads, never on the AudioQueue callback.
func publishCaptureMetrics(_ len: Int, _ now: Double) {
    sendCond.lock()
    captureFrames += UInt64(len / SquelchProfile.frameBytes)
    if lastCallbackTime > 0 {
        let gap = (now - lastCallbackTime) * 1000
        if gap > maxCallbackGapMs { maxCallbackGapMs = gap }
    }
    lastCallbackTime = now
    senderSquelchPublished = senderSquelch   // value type: one copy, no allocation
    sendCond.unlock()
}

/// Returns the send queue's drop generation as of this enqueue, so the caller can tell whether
/// its audio pushed older audio off the queue.
@discardableResult
func enqueueSend(_ p: UnsafeRawPointer, _ len: Int, _ now: Double) -> UInt64 {
    sendCond.lock()
    if !senderDead {
        if sendRing.isEmpty { pendingSince = now }
        let removed = sendRing.push(p, len)   // frame-aligned drop-oldest into fixed storage
        if removed > 0 {
            let ageMs = pendingSince > 0 ? (now - pendingSince) * 1000 : 0
            senderDropGeneration &+= 1
            dropEvents += 1
            dropBytes += UInt64(removed)
            if ageMs > maxDropAgeMs { maxDropAgeMs = ageMs }
            let next = (dropEventWrite + 1) % dropEventCapacity
            if next == dropEventRead {
                dropEventLost += 1
            } else {
                dropEventRing[dropEventWrite] = DropEvent(bytes: removed, ageMs: ageMs, at: now)
                dropEventWrite = next
            }
        }
        if sendRing.count > pendingHighWater { pendingHighWater = sendRing.count }
        sendCond.signal()
    }
    let generation = senderDropGeneration
    sendCond.unlock()
    return generation
}

// Drain the fixed numeric event ring off the callback. The callback only stores
// numbers in preallocated slots; formatting and logging happen here. Safe from both
// the sender and telemetry threads: the read index only advances under the lock.
func emitDropEvents() {
    while true {
        sendCond.lock()
        if dropEventRead == dropEventWrite {
            let lost = dropEventLost
            dropEventLost = 0
            sendCond.unlock()
            if lost > 0 {
                FileHandle.standardError.write("event=sender_drop_event_overflow count=\(lost)\n".data(using: .utf8)!)
            }
            return
        }
        let ev = dropEventRing[dropEventRead]
        dropEventRead = (dropEventRead + 1) % dropEventCapacity
        sendCond.unlock()
        let delayMs = (CFAbsoluteTimeGetCurrent() - ev.at) * 1000
        let line = String(format: "event=sender_drop frames=%d bytes=%d oldest_age_ms=%.1f report_delay_ms=%.1f\n",
            ev.bytes / SquelchProfile.frameBytes, ev.bytes, ev.ageMs, delayMs)
        FileHandle.standardError.write(line.data(using: .utf8)!)
    }
}

func emitSenderTelemetry() {
    let now = CFAbsoluteTimeGetCurrent()
    sendCond.lock()
    let pending = sendRing.count
    let oldestMs = pending > 0 && pendingSince > 0 ? (now - pendingSince) * 1000 : 0
    let captureFramesNow = captureFrames
    let callbackGapNow = maxCallbackGapMs
    let pendingHighWaterNow = pendingHighWater
    let inflight = inflightBytes
    // Aged live: a write that has been blocked 250 ms must read as 250 ms now, not
    // as 0 until it returns. This is what made blocked writes invisible in the log.
    let inflightAgeNow = inflight > 0 && inflightSince > 0 ? (now - inflightSince) * 1000 : 0
    let dropEventsNow = dropEvents
    let dropBytesNow = dropBytes
    let maxDropAgeNow = maxDropAgeMs
    let writeCallsNow = writeCalls
    let writeBytesNow = writeBytes
    let maxWriteNow = maxWriteMs
    let writesOver20Now = writesOver20
    let writesOver100Now = writesOver100
    let writesOver250Now = writesOver250
    let effectiveSendBufferNow = effectiveSendBuffer
    let squelch = senderSquelchPublished
    maxCallbackGapMs = 0
    pendingHighWater = pending
    maxWriteMs = 0
    sendCond.unlock()
    // String construction and log I/O happen after releasing the queue lock, so
    // the AudioQueue callback cannot wait behind either operation.
    // pending_bytes keeps its meaning (queued, not yet handed to writeAll); unsent_bytes
    // is the backlog that actually matters to a listener: queued plus in flight.
    let line = String(format: "event=sender_metrics capture_frames=%llu callback_max_gap_ms=%.1f pending_bytes=%d pending_ms=%.1f pending_high_water=%d oldest_ms=%.1f inflight_bytes=%d inflight_age_ms=%.1f unsent_bytes=%d drop_events=%llu drop_bytes=%llu max_drop_age_ms=%.1f write_calls=%llu write_bytes=%llu max_write_ms=%.1f writes_over_20ms=%llu writes_over_100ms=%llu writes_over_250ms=%llu effective_sndbuf=%d",
        captureFramesNow, callbackGapNow, pending, Double(pending) / 192.0, pendingHighWaterNow, oldestMs,
        inflight, inflightAgeNow, pending + inflight, dropEventsNow, dropBytesNow, maxDropAgeNow, writeCallsNow, writeBytesNow, maxWriteNow,
        writesOver20Now, writesOver100Now, writesOver250Now, effectiveSendBufferNow)
    // Every silence transition is attributable: qualification completed, resumed, or revoked by a
    // local queue drop. squelch_drop_invalidate > 0 means a tail the receiver may never have seen.
    let squelchLine = " squelch_state=\(squelch.stateName) squelch_silent_frames=\(squelch.silentFrames)"
        + " squelch_enter=\(squelch.enterCount) squelch_exit=\(squelch.exitCount)"
        + " squelch_drop_invalidate=\(squelch.dropInvalidations)"
        + " squelch_suppressed_frames=\(squelch.suppressedFrames)"
        + " squelch_heartbeats=\(squelch.heartbeats)"
        + " squelch_misaligned_bytes=\(squelch.misalignedTailBytes)"
    FileHandle.standardError.write((line + squelchLine + "\n").data(using: .utf8)!)
}

// startSender drains the queue to fd on its own thread; on write failure it marks
// the link dead and stops the main run loop so we tear down and re-accept. A second
// thread emits telemetry, because a sender blocked in writeAll cannot report that it
// is blocked: metrics used to stop entirely for the duration of the stall, which read
// as a clean sender.
func startSender(_ fd: Int32) {
    sendCond.lock()
    senderGeneration &+= 1
    let generation = senderGeneration
    sendCond.unlock()
    Thread.detachNewThread {
        // One drain buffer per sender thread, allocated here — on the sender thread, never on the
        // callback — and big enough for the whole ring, so a drain is one memcpy that empties the
        // queue under the lock exactly as `removeAll` emptied the Data queue.
        let staging = UnsafeMutableRawPointer.allocate(byteCount: sendPendingMax, alignment: 16)
        defer { staging.deallocate() }
        while true {
            sendCond.lock()
            while sendRing.isEmpty && !senderDead {
                _ = sendCond.wait(until: Date(timeIntervalSinceNow: 1.0))
            }
            // Retire on death OR on a newer connection. Without the generation check a
            // sender still blocked in writeAll when the run loop stopped could wake after
            // the accept loop had cleared senderDead for a NEW connection, and drain that
            // connection's audio into the old, closed fd.
            if senderDead || senderGeneration != generation { sendCond.unlock(); return }
            let chunkBytes = sendRing.drain(into: staging)
            pendingSince = 0
            // Hand-off and in-flight publication are one critical section: this audio must
            // never be absent from both counters, or a stall looks like an idle link.
            let writeStart = CFAbsoluteTimeGetCurrent()
            inflightBytes = chunkBytes
            inflightSince = writeStart
            sendCond.unlock()
            let ok = writeAll(fd, staging, chunkBytes)
            let writeMs = (CFAbsoluteTimeGetCurrent() - writeStart) * 1000
            sendCond.lock()
            inflightBytes = 0
            inflightSince = 0
            writeCalls += 1
            writeBytes += UInt64(chunkBytes)
            if writeMs > maxWriteMs { maxWriteMs = writeMs }
            if writeMs > 20 { writesOver20 += 1 }
            if writeMs > 100 { writesOver100 += 1 }
            if writeMs > 250 { writesOver250 += 1 }
            sendCond.unlock()
            if writeMs > 20 {
                FileHandle.standardError.write(String(format: "event=sender_slow_write bytes=%d duration_ms=%.1f\n", chunkBytes, writeMs).data(using: .utf8)!)
            }
            emitDropEvents()
            if !ok {
                sendCond.lock(); senderDead = true; sendCond.unlock()
                CFRunLoopStop(mainLoop)
                return
            }
        }
    }
    // Telemetry clock. It sleeps rather than waiting on sendCond: a second waiter there
    // could swallow the signal() meant for the sender and delay real audio. The
    // generation check retires this thread when the connection ends — a reconnect inside
    // one tick would otherwise leave two threads emitting the same metrics.
    Thread.detachNewThread {
        while true {
            Thread.sleep(forTimeInterval: 1.0)
            sendCond.lock()
            let retired = senderDead || senderGeneration != generation
            sendCond.unlock()
            if retired { return }
            emitDropEvents()
            emitSenderTelemetry()
        }
    }
}

// AudioQueue input callback: classify the captured frames, hand the transmit spans to the sender
// thread, recycle the buffer. Allocation-free and log-free end to end: it classifies in place,
// copies spans of the capture buffer into storage reserved before AudioQueueStart, and creates no
// per-frame object. Everything it touches — the send ring, the drop-event ring, the heartbeat
// buffer — is fixed-size and preallocated.
let cb: AudioQueueInputCallback = { _, queue, bufRef, _, _, _ in
    let b = bufRef.pointee
    let len = Int(b.mAudioDataByteSize)
    if clientFD >= 0, len > 0 {
        let now = CFAbsoluteTimeGetCurrent()
        let base = UnsafeRawPointer(b.mAudioData)
        senderSquelch.process(base, byteCount: len) { frameOffset, frameCount in
            lastSentTime = now
            return enqueueSend(base + frameOffset * SquelchProfile.frameBytes,
                               frameCount * SquelchProfile.frameBytes, now)
        }
        // While suppressed, keep the connection and the receiver's blocking read alive with a
        // frame-aligned all-silent heartbeat. It is not a media record and carries no policy.
        if heartbeatDue(senderSquelch.suppressed, now, lastSentTime) {
            lastSentTime = now
            senderSquelch.recordHeartbeat()
            senderSquelch.observeQueueGeneration(enqueueSend(heartbeatBuffer, heartbeatBytes, now))
        }
        publishCaptureMetrics(len, now)
    }
    AudioQueueEnqueueBuffer(queue, bufRef, 0, nil)
}

// --- self-test -------------------------------------------------------------
// House rule: a check is worth nothing until the paired sabotage has been shown to fail it. Each
// case below names the sabotage that must break it (falsification matrix S1-S5).

func selfTestFail(_ message: String) -> Never {
    FileHandle.standardError.write("self-test FAILED: \(message)\n".data(using: .utf8)!)
    exit(1)
}

func require(_ ok: Bool, _ message: @autoclosure () -> String) {
    if !ok { selfTestFail(message()) }
}

/// Silent at the inclusive boundary on both channels: +silencePeak / -silencePeak must classify as
/// silence, so a strictly-less-than comparison would fail every case below.
func silence(_ frames: Int) -> [Int16] {
    var out = [Int16]()
    out.reserveCapacity(frames * 2)
    let peak = Int16(SquelchProfile.silencePeak)
    for _ in 0..<frames { out.append(peak); out.append(-peak) }
    return out
}

func tone(_ frames: Int) -> [Int16] {
    var out = [Int16]()
    out.reserveCapacity(frames * 2)
    for _ in 0..<frames { out.append(2000); out.append(-2000) }
    return out
}

/// One sample past the inclusive threshold, and only on the RIGHT channel: a classifier that looks
/// at one channel, or that treats the threshold as exclusive, misreads this frame.
func justActive() -> [Int16] { [0, Int16(SquelchProfile.silencePeak) + 1] }

func fixedSizes(_ total: Int, _ size: Int) -> [Int] {
    var out = [Int](); var left = total
    while left > 0 { let n = min(size, left); out.append(n); left -= n }
    return out
}

/// Deterministic irregular partition: callback sizes from 1 to 2000 frames, so no run boundary
/// lines up with a buffer boundary.
func irregularSizes(_ total: Int) -> [Int] {
    var out = [Int](); var left = total; var seed: UInt64 = 0x9E37_79B9_7F4A_7C15
    while left > 0 {
        seed = seed &* 6364136223846793005 &+ 1442695040888963407
        let n = min(left, 1 + Int((seed >> 33) % 2000))
        out.append(n); left -= n
    }
    return out
}

/// Deterministic pseudo-random bytes. Byte-exactness through the ring is a property of the
/// arithmetic rather than of the data, so the payload only has to be something that an off-by-one
/// or a lost wrapped half cannot accidentally reproduce.
func pseudoBytes(_ n: Int) -> [UInt8] {
    var out = [UInt8](); out.reserveCapacity(n)
    var seed: UInt64 = 0xD1B5_4A32_D192_ED03
    for _ in 0..<n {
        seed = seed &* 6364136223846793005 &+ 1442695040888963407
        out.append(UInt8((seed >> 40) & 0xFF))
    }
    return out
}

/// Drives the production state machine over synthetic PCM and records exactly which absolute
/// frames reached the queue. Nothing here touches CoreAudio, a socket or the clock.
final class SquelchDriver {
    var state: SenderSquelchState
    var transmitted: [Bool]
    var spanStart = [Int]()
    var spanCount = [Int]()
    var generation: UInt64 = 0
    var dropOnEmit = -1        // simulate a queue drop-oldest on this 1-based emit
    var emits = 0
    var cursor = 0             // absolute frame index of the next callback

    init(capacity: Int, suppressionEnabled: Bool = true) {
        state = SenderSquelchState(suppressionEnabled: suppressionEnabled)
        transmitted = [Bool](repeating: false, count: capacity)
    }

    func feed(_ pcm: [Int16], _ sizes: [Int]) {
        var local = 0
        var st = state
        for n in sizes {
            let absBase = cursor + local
            pcm.withUnsafeBytes { raw in
                let p = raw.baseAddress!.advanced(by: local * SquelchProfile.frameBytes)
                st.process(p, byteCount: n * SquelchProfile.frameBytes) { off, count in
                    self.emits += 1
                    self.spanStart.append(absBase + off)
                    self.spanCount.append(count)
                    for k in 0..<count { self.transmitted[absBase + off + k] = true }
                    if self.emits == self.dropOnEmit { self.generation &+= 1 }
                    return self.generation
                }
            }
            local += n
        }
        state = st
        cursor += local
    }

    func feed(_ pcm: [Int16]) { feed(pcm, [pcm.count / 2]) }

    func transmittedCount(_ range: Range<Int>) -> Int {
        var n = 0
        for i in range where transmitted[i] { n += 1 }
        return n
    }

    /// How many separate emitted spans covered this absolute frame. Must be 1 for any frame that
    /// went on the wire: 0 means it was lost, 2 means it was duplicated.
    func timesEmitted(_ frame: Int) -> Int {
        var n = 0
        for (i, s) in spanStart.enumerated() where frame >= s && frame < s + spanCount[i] { n += 1 }
        return n
    }
}

let confirm = SquelchProfile.confirmFrames

func selfTestProfile() {
    // The threshold is inclusive on both channels, and one sample past it is program audio.
    require(SquelchProfile.frameIsSilent(Int16(SquelchProfile.silencePeak), Int16(-SquelchProfile.silencePeak)),
            "the silence threshold must be inclusive on both channels")
    require(!SquelchProfile.frameIsSilent(justActive()[0], justActive()[1]),
            "one sample past the threshold must classify as active")
}

func selfTestS1() {
    // S1 exact sender boundary. The transition frame is a property of the media, not of the
    // capture quantum: the SAME PCM cut into 3 ms, default 21 ms, 30 ms and irregular callbacks
    // must transmit exactly the same frames and suppress at exactly the same absolute frame.
    // SABOTAGE: restore whole-buffer qualification — the transition frame then moves with the
    // callback shape.
    let lead = 500, silentRun = 14000, trail = 400
    let s1pcm = tone(lead) + silence(silentRun) + tone(trail)
    let s1total = lead + silentRun + trail
    let threshold = lead + confirm - 1          // the 12,000th contiguous silent frame
    let firstSuppressed = lead + confirm        // silent frame 12,001
    var reference = [Bool]()
    for (name, sizes) in [("3 ms", fixedSizes(s1total, 144)),
                          ("default 21 ms", fixedSizes(s1total, 1024)),
                          ("30 ms", fixedSizes(s1total, 1440)),
                          ("irregular", irregularSizes(s1total))] {
        let d = SquelchDriver(capacity: s1total)
        d.feed(s1pcm, sizes)
        require(d.transmitted[threshold],
                "the 12,000th contiguous silent frame must be transmitted (\(name) callbacks)")
        require(!d.transmitted[firstSuppressed],
                "silent frame 12,001 must be suppressed (\(name) callbacks)")
        require(!d.transmitted[lead + silentRun - 1],
                "silence must stay suppressed to the end of the run (\(name) callbacks)")
        require(d.transmitted[lead + silentRun],
                "the first active frame after the silent run must be transmitted (\(name) callbacks)")
        require(d.state.enterCount == 1 && d.state.exitCount == 1,
                "exactly one suppression enter and one exit per silent run (\(name) callbacks)")
        if reference.isEmpty {
            reference = d.transmitted
        } else {
            require(d.transmitted == reference,
                    "the transmitted frames must not depend on the capture quantum (\(name) vs 3 ms callbacks)")
        }
    }
}

func selfTestS2() {
    // S2 sender active reset. 6,000 silent frames, one active frame, 6,000 silent frames is not a
    // 12,000-frame contiguous run and must not suppress; a fresh full run must.
    // SABOTAGE: do not reset the count on the active frame.
    let s2 = SquelchDriver(capacity: 3 * confirm)
    s2.feed(silence(6000), fixedSizes(6000, 1024))
    s2.feed(justActive())
    s2.feed(silence(6000), fixedSizes(6000, 1024))
    require(s2.state.state != .suppressed,
            "an active frame must reset qualification: 6,000 silent + 1 active + 6,000 silent is not a 12,000-frame run")
    require(s2.state.silentFrames == 6000,
            "the silent run must restart at the active frame")
    require(s2.transmittedCount(0..<s2.cursor) == s2.cursor,
            "every frame before suppression must be transmitted")
    s2.feed(silence(confirm - 6001), fixedSizes(confirm - 6001, 1024))
    require(s2.state.state == .qualifying && s2.state.silentFrames == confirm - 1,
            "11,999 contiguous silent frames must still be qualifying, not suppressed")
    let s2threshold = s2.cursor
    s2.feed(silence(2))
    require(s2.transmitted[s2threshold], "the 12,000th silent frame of the fresh run must be transmitted")
    require(!s2.transmitted[s2threshold + 1], "the 12,001st silent frame of the fresh run must be suppressed")
    require(s2.state.state == .suppressed, "a fresh 12,000-frame run must suppress")
}

func selfTestS3() {
    // S3 mixed-callback first-active preservation. One callback carries the last qualifying
    // silence, the threshold frame, suppressed silence, and then resumed program. The first active
    // frame must go on the wire exactly once.
    // SABOTAGE: change state after slicing/enqueueing, dropping the first active frame.
    let s3pre = 11000, s3silent = 1300, s3active = 200
    let s3 = SquelchDriver(capacity: s3pre + s3silent + s3active)
    s3.feed(silence(s3pre), fixedSizes(s3pre, 1024))
    s3.feed(silence(s3silent) + tone(s3active))     // one mixed callback
    let s3threshold = confirm - 1
    let s3firstActive = s3pre + s3silent
    require(s3.transmitted[s3threshold], "the threshold frame inside a mixed callback must be transmitted")
    require(!s3.transmitted[s3threshold + 1], "suppression must begin inside the same callback")
    require(!s3.transmitted[s3firstActive - 1], "suppressed silence must not be transmitted")
    require(s3.timesEmitted(s3firstActive) == 1,
            "the first active frame after suppression must be transmitted exactly once")
    require(s3.transmittedCount(s3firstActive..<(s3firstActive + s3active)) == s3active,
            "the whole resumed span must be transmitted")
    require(s3.state.exitCount == 1 && s3.state.state == .transmit,
            "the resumed callback must leave the state machine in TRANSMIT")
}

func selfTestS4() {
    // S4 new-client qualification. A receiver that connects while the source is already silent has
    // no way to learn that, so it must be given its own full 12,000-frame tail.
    // SABOTAGE: preserve SUPPRESSED across accepted clients.
    let s4pre = confirm + 1000
    let s4 = SquelchDriver(capacity: s4pre + 2 * confirm)
    s4.feed(silence(s4pre), fixedSizes(s4pre, 1024))
    require(s4.state.state == .suppressed, "the first client must reach suppression, else S4 proves nothing")
    s4.state.beginConnection(dropGeneration: s4.generation)
    require(s4.state.state == .transmit && s4.state.silentFrames == 0,
            "a newly accepted connection must start in TRANSMIT with no silent run")
    let s4base = s4.cursor
    s4.feed(silence(confirm + 1), fixedSizes(confirm + 1, 1024))
    require(s4.transmittedCount(s4base..<(s4base + confirm)) == confirm,
            "a receiver connecting during silence must receive a fresh full 12,000-frame qualifying tail")
    require(!s4.transmitted[s4base + confirm],
            "the new client's qualifying tail must be exactly 12,000 frames")
}

func selfTestS5() {
    // S5 sender-drop invalidation. Qualification is only valid if the receiver can eventually
    // observe the tail, so any local drop-oldest voids it and a fresh full run is required.
    // SABOTAGE: count enqueued frames without observing the drop generation.
    let s5drop = 11000, s5total = s5drop + 2 * confirm
    let s5 = SquelchDriver(capacity: s5total)
    s5.dropOnEmit = s5drop / 1000                 // the emit that carries the 11,000th silent frame
    s5.feed(silence(s5total), fixedSizes(s5total, 1000))
    require(s5.state.dropInvalidations == 1, "a send-queue drop must be observed exactly once")
    require(s5.transmitted[s5drop - 1], "frames enqueued before the drop still went on the wire")
    require(s5.transmitted[confirm], "a drop must void the pre-drop run: silent frame 12,001 must still be transmitted")
    require(s5.transmitted[s5drop + confirm - 1],
            "the 12,000th silent frame after the drop must be transmitted")
    require(!s5.transmitted[s5drop + confirm],
            "suppression must require a fresh 12,000 frames after a send-queue drop")

    // The rule above is only real if the production queue reports its drops. This drives the
    // shipped enqueueSend, not a stand-in.
    var junk = [UInt8](repeating: 0, count: 64 * 1024)
    let genBefore = senderDropGeneration
    let genUnderCap = junk.withUnsafeBytes { enqueueSend($0.baseAddress!, $0.count, 0) }
    require(genUnderCap == genBefore, "an enqueue under the queue ceiling must not report a drop")
    let genOverCap = junk.withUnsafeBytes { enqueueSend($0.baseAddress!, $0.count, 0) }
    require(genOverCap != genBefore, "a drop-oldest in the send queue must be reported to the state machine")
    junk.removeAll()
    sendCond.lock(); sendRing.reset(); pendingSince = 0; sendCond.unlock()
}

/// Not part of the S1-S5 matrix: the heartbeat's timing is the one part of the sender's silence
/// behaviour that needs a clock, so it is tested as a pure decision with the clock passed in.
func selfTestHeartbeat() {
    require(!heartbeatDue(false, 1000.0, 0.0), "a transmitting sender must not emit heartbeats")
    require(!heartbeatDue(true, heartbeatInterval - 0.001, 0.0),
            "a heartbeat must not fire before the profile interval")
    require(heartbeatDue(true, heartbeatInterval, 0.0), "a heartbeat must fire at the profile interval")

    // A heartbeat enqueue that pushes older audio off the queue is still a drop, and a drop still
    // voids qualification: otherwise the sender stays suppressed on a tail nobody received.
    let hb = SquelchDriver(capacity: confirm + 100)
    hb.feed(silence(confirm + 100), fixedSizes(confirm + 100, 1024))
    require(hb.state.state == .suppressed, "the heartbeat case must start from suppression")
    hb.state.observeQueueGeneration(hb.generation &+ 1)
    require(hb.state.state == .transmit && hb.state.silentFrames == 0,
            "a drop on the heartbeat enqueue must void qualification too")
}

func selfTestContinuous() {
    // Continuous PCM is one coordinated mode, not a threshold knob: it must disable suppression
    // outright. (The receiver must be started in the same mode; a mixed pair is unsafe.)
    let cont = SquelchDriver(capacity: 2 * confirm, suppressionEnabled: false)
    cont.feed(silence(2 * confirm), fixedSizes(2 * confirm, 1024))
    require(cont.state.state == .transmit && cont.transmittedCount(0..<(2 * confirm)) == 2 * confirm,
            "continuous-PCM mode must transmit every frame and never suppress")
}

// Q1-Q4 cover the send queue itself: the callback hands audio to the sender through a fixed
// circular buffer, and every one of these cases is a way that buffer can silently corrupt or lose
// the stream while the build stays green and the counters look plausible.

func selfTestQ1() {
    // Q1 wrap-around. The ring is drained in place, so the cursors walk off the end of the storage
    // and back to the front; in the live sender that happens roughly every 96 KB, forever. A push
    // that straddles the boundary must come back as one contiguous, correctly ordered piece.
    // SABOTAGE: drop the wrapped half of the copy in SendRing.copyIn.
    let cap = 64
    var ring = SendRing(capacity: cap)
    var out = [UInt8](repeating: 0, count: cap)

    let head = pseudoBytes(48)
    head.withUnsafeBytes { _ = ring.push($0.baseAddress!, 48) }
    var n = out.withUnsafeMutableBytes { ring.drain(into: $0.baseAddress!) }
    require(n == 48 && Array(out[0..<48]) == head, "a push that fits before the end must drain back byte-identical")
    require(ring.isEmpty, "a drain must empty the queue")

    // The cursor now sits at 48 of 64, so this push takes 16 bytes before the end and 16 after it.
    let wrapped = pseudoBytes(32)
    wrapped.withUnsafeBytes { _ = ring.push($0.baseAddress!, 32) }
    require(ring.count == 32, "a wrapped push must queue every byte")
    n = out.withUnsafeMutableBytes { ring.drain(into: $0.baseAddress!) }
    require(n == 32, "a wrapped drain must return every queued byte")
    require(Array(out[0..<32]) == wrapped,
            "a push that crosses the end of the ring must come back in order, byte for byte")

    // And again from a different offset (cursor at 16), so the case cannot pass on one lucky split.
    let second = pseudoBytes(56)
    second.withUnsafeBytes { _ = ring.push($0.baseAddress!, 56) }
    n = out.withUnsafeMutableBytes { ring.drain(into: $0.baseAddress!) }
    require(n == 56 && Array(out[0..<56]) == second,
            "a second wrap at a different offset must come back in order, byte for byte")
}

func selfTestQ2() {
    // Q2 the ceiling and the drop it causes. Filling to exactly 96 KB must not drop; one more frame
    // must discard exactly the OLDEST frame and bump the monotonic drop generation, because that
    // generation is what revokes squelch qualification — a tail discarded here must never be able
    // to manufacture a suppressed state the receiver was never told about.
    // SABOTAGE (a): remove the `senderDropGeneration &+= 1` in enqueueSend.
    // SABOTAGE (b): discard `over` instead of rounding it up to a whole frame.
    // SABOTAGE (c): discard the newest queued bytes instead of the oldest.
    let frameBytes = SquelchProfile.frameBytes
    let frames = sendPendingMax / frameBytes
    sendCond.lock(); sendRing.reset(); pendingSince = 0; sendCond.unlock()
    let genBefore = senderDropGeneration
    let dropEventsBefore = dropEvents
    let dropBytesBefore = dropBytes
    let eventSlot = dropEventWrite

    // Every frame carries its own index, so which frames survived a drop is checkable rather than
    // inferred from a byte count.
    var pcm = [UInt8](repeating: 0, count: sendPendingMax)
    for f in 0..<frames {
        var v = UInt32(f).littleEndian
        withUnsafeBytes(of: &v) { src in
            for k in 0..<frameBytes { pcm[f * frameBytes + k] = src[k] }
        }
    }
    var pushed = 0
    while pushed < sendPendingMax {      // production-shaped chunks: the default 21 ms quantum
        let n = min(4096, sendPendingMax - pushed)
        pcm.withUnsafeBytes { _ = enqueueSend($0.baseAddress! + pushed, n, 0) }
        pushed += n
    }
    require(sendRing.count == sendPendingMax, "an exact-capacity fill must queue every byte")
    require(senderDropGeneration == genBefore, "filling to exactly the ceiling must not report a drop")
    require(dropEvents == dropEventsBefore, "filling to exactly the ceiling must not record a drop event")

    let sentinel: [UInt8] = [0xDE, 0xAD, 0xBE, 0xEF]
    let reported = sentinel.withUnsafeBytes { enqueueSend($0.baseAddress!, frameBytes, 0) }
    require(senderDropGeneration == genBefore &+ 1,
            "one frame past the ceiling must bump the drop generation exactly once")
    require(reported == senderDropGeneration,
            "enqueueSend must report the post-drop generation to its caller")
    require(dropEvents == dropEventsBefore + 1, "one frame past the ceiling must record one drop event")
    require(dropBytes - dropBytesBefore == UInt64(frameBytes), "exactly one frame must be dropped")
    require(sendRing.count == sendPendingMax, "the queue must stay at its ceiling, never above it")
    // The drop must also reach the numeric event ring the sender thread formats and logs: a drop
    // nobody can attribute to a leg and a clock is the same as an unreported drop.
    // SABOTAGE (d): store a zero byte count into the drop-event slot.
    require(dropEventWrite == (eventSlot + 1) % dropEventCapacity,
            "the drop must occupy exactly one slot of the drop-event ring")
    require(dropEventRing[eventSlot].bytes == frameBytes,
            "the drop-event ring must record the dropped byte count")

    var out = [UInt8](repeating: 0, count: sendPendingMax)
    let n = out.withUnsafeMutableBytes { sendRing.drain(into: $0.baseAddress!) }
    require(n == sendPendingMax, "the queue must drain its whole ceiling")
    require(Array(out[0..<frameBytes]) == Array(pcm[frameBytes..<(2 * frameBytes)]),
            "the drop must discard the OLDEST frame: the queue must now begin at frame 1, on a frame boundary")
    require(Array(out[(sendPendingMax - frameBytes)...]) == sentinel,
            "the frame that caused the drop must itself be queued, as the newest frame")

    // Frame alignment is the ring's own contract, not an accident of frame-sized callers: a partial
    // frame of overflow must still cost a whole frame, or everything after it is channel-swapped.
    var mis = SendRing(capacity: 64)
    let filler = pseudoBytes(64)
    filler.withUnsafeBytes { _ = mis.push($0.baseAddress!, 64) }
    let odd = pseudoBytes(1)
    let removed = odd.withUnsafeBytes { mis.push($0.baseAddress!, 1) }
    require(removed == frameBytes, "a one-byte overflow must discard a whole frame, not one byte")
    require(mis.count == 64 - frameBytes + 1, "the queue must hold what the frame-aligned drop left room for")

    sendCond.lock(); sendRing.reset(); pendingSince = 0; sendCond.unlock()
}

func selfTestQ3() {
    // Q3 interleaved partial drains. The sender wakes and takes whatever is queued at that instant,
    // which is almost never a buffer boundary, and the callback goes on pushing from wherever that
    // left the cursors. Nothing may be lost, duplicated or reordered across a drain, and a wake
    // with an empty queue must return nothing rather than replay what was just written.
    // SABOTAGE: leave `count` unchanged in SendRing.drain.
    var ring = SendRing(capacity: 256)
    let payload = pseudoBytes(4000)
    var scratch = [UInt8](repeating: 0, count: 256)
    var got = [UInt8](); got.reserveCapacity(payload.count)
    let chunks = [40, 7, 90, 13, 60, 3, 77, 21]   // max of any three consecutive: 163 of 256
    var pos = 0, queued = 0, i = 0
    while pos < payload.count {
        let n = min(chunks[i % chunks.count], payload.count - pos)
        let discarded = payload.withUnsafeBytes { ring.push($0.baseAddress! + pos, n) }
        require(discarded == 0, "Q3 must stay under the ceiling: it tests the drain path, not the drop path")
        pos += n; queued += n; i += 1
        require(ring.count == queued, "the queue must account for every pushed byte between drains")
        if i % 3 == 0 {
            let m = scratch.withUnsafeMutableBytes { ring.drain(into: $0.baseAddress!) }
            require(m == queued, "a drain must return exactly what was queued since the last drain")
            require(ring.count == 0 && ring.isEmpty, "a drain must leave the queue empty")
            got.append(contentsOf: scratch[0..<m])
            queued = 0
            let empty = scratch.withUnsafeMutableBytes { ring.drain(into: $0.baseAddress!) }
            require(empty == 0, "draining an empty queue must return nothing")
        }
    }
    let m = scratch.withUnsafeMutableBytes { ring.drain(into: $0.baseAddress!) }
    require(m == queued, "the final drain must return exactly what was left queued")
    got.append(contentsOf: scratch[0..<m])
    require(got == payload,
            "interleaved partial drains must return every byte exactly once, in order")
}

func selfTestQ4() {
    // Q4 byte-exactness at production scale. A pseudo-random payload pushed in irregular,
    // deliberately frame-unaligned chunks through the real 96 KB ceiling, drained at irregular
    // points, must come out identical. This is the property the whole change is judged on: the
    // callback's only job is to move bytes, and this rewrote how they are stored.
    // SABOTAGE: copy in at `head` instead of at the tail in SendRing.copyIn.
    var ring = SendRing(capacity: sendPendingMax)
    let payload = pseudoBytes(512 * 1024)
    var scratch = [UInt8](repeating: 0, count: sendPendingMax)
    var got = [UInt8](); got.reserveCapacity(payload.count)
    var pos = 0
    var drains = 0
    for n in irregularSizes(payload.count) {      // 1..2000 bytes, no chunk aligned to anything
        let discarded = payload.withUnsafeBytes { ring.push($0.baseAddress! + pos, n) }
        require(discarded == 0, "Q4 must stay under the ceiling: it tests the copy, not the drop")
        pos += n
        if ring.count > sendPendingMax - 2000 {   // drain before the next chunk could overflow
            let m = scratch.withUnsafeMutableBytes { ring.drain(into: $0.baseAddress!) }
            got.append(contentsOf: scratch[0..<m])
            drains += 1
        }
    }
    let m = scratch.withUnsafeMutableBytes { ring.drain(into: $0.baseAddress!) }
    got.append(contentsOf: scratch[0..<m])
    require(drains >= 4, "Q4 must wrap the ring several times, else it proves nothing about wrapping")
    require(got.count == payload.count, "every pushed byte must be drained exactly once")
    require(got == payload, "a payload pushed in irregular chunks must leave the ring byte-identical")
}

/// `--case=S3` runs one case alone. That is how each sabotage is shown to fail its OWN assertion
/// rather than an earlier case's: several of these fixtures cross the same transitions.
func runSelfTest(only: String?) {
    let cases: [(String, () -> Void)] = [
        ("profile", selfTestProfile), ("S1", selfTestS1), ("S2", selfTestS2), ("S3", selfTestS3),
        ("S4", selfTestS4), ("S5", selfTestS5), ("heartbeat", selfTestHeartbeat),
        ("continuous", selfTestContinuous),
        ("Q1", selfTestQ1), ("Q2", selfTestQ2), ("Q3", selfTestQ3), ("Q4", selfTestQ4),
    ]
    if let only = only, !cases.contains(where: { $0.0 == only }) {
        selfTestFail("unknown self-test case \(only)")
    }
    for (name, body) in cases where only == nil || only == name { body() }
    print("hear-capture self-test ok\(only.map { " [\($0)]" } ?? "") (profile \(SquelchProfile.id) \(SquelchProfile.hash))")
}

@main
struct HearCapture {
    static func main() {
        if args.contains("--self-test") {
            runSelfTest(only: args.first { $0.hasPrefix("--case=") }.map { String($0.dropFirst(7)) })
            exit(0)
        }

        signal(SIGPIPE, SIG_IGN)   // writing to a closed client must not kill us
        mainLoop = CFRunLoopGetCurrent()
        // Force every lazy global the capture callback touches, so its one-time initialization —
        // the only allocation anywhere on that path — happens exactly here, before any
        // AudioQueueStart below, and never inside an audio callback.
        _ = (heartbeatBuffer, heartbeatBytes, heartbeatInterval, senderSquelch, continuousPCM,
             sendCond, sendRing.capacity, dropEventRing, dropEventCapacity)

        // TCP listen socket
        let srv = socket(AF_INET, SOCK_STREAM, 0)
        var yes: Int32 = 1
        setsockopt(srv, SOL_SOCKET, SO_REUSEADDR, &yes, socklen_t(MemoryLayout<Int32>.size))
        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = PORT.bigEndian
        addr.sin_addr.s_addr = INADDR_ANY
        let bindRC = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(srv, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        guard bindRC == 0 else { FileHandle.standardError.write("bind failed\n".data(using: .utf8)!); exit(1) }
        listen(srv, 1)
        FileHandle.standardError.write("hear-capture listening :\(PORT)\n".data(using: .utf8)!)

        var fmt = AudioStreamBasicDescription(
            mSampleRate: Float64(SquelchProfile.sampleRate),
            mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kLinearPCMFormatFlagIsSignedInteger | kLinearPCMFormatFlagIsPacked,
            mBytesPerPacket: UInt32(SquelchProfile.frameBytes), mFramesPerPacket: 1,
            mBytesPerFrame: UInt32(SquelchProfile.frameBytes),
            mChannelsPerFrame: UInt32(SquelchProfile.channels), mBitsPerChannel: 16, mReserved: 0)

        while true {
            let fd = accept(srv, nil, nil)
            if fd < 0 { continue }
            var one: Int32 = 1
            setsockopt(fd, Int32(IPPROTO_TCP), TCP_NODELAY, &one, socklen_t(MemoryLayout<Int32>.size))
            // Cap the kernel send buffer so the OS can't hoard ~0.7s of audio (default 128KB).
            // Small buffer = tight coupling to the receiver = low latency. Tunable via env.
            var sndbuf = Int32(ProcessInfo.processInfo.environment["BRIDGE_SNDBUF"] ?? "16384") ?? 16384
            setsockopt(fd, SOL_SOCKET, SO_SNDBUF, &sndbuf, socklen_t(MemoryLayout<Int32>.size))
            var effective = Int32(0)
            var effectiveLen = socklen_t(MemoryLayout<Int32>.size)
            getsockopt(fd, SOL_SOCKET, SO_SNDBUF, &effective, &effectiveLen)
            // Drop a client that vanished without a clean FIN (peer slept / lost power / crashed)
            // so we re-accept instead of streaming forever into a dead socket. TCP keepalive
            // probes the peer; SO_SNDTIMEO makes a stalled write() fail fast (the small SO_SNDBUF
            // fills in ~0.1s against a dead peer) -> writeAll returns false -> we close + re-accept.
            var ka: Int32 = 1
            setsockopt(fd, SOL_SOCKET, SO_KEEPALIVE, &ka, socklen_t(MemoryLayout<Int32>.size))
            var kaIdle: Int32 = 10  // seconds idle before first probe
            setsockopt(fd, Int32(IPPROTO_TCP), TCP_KEEPALIVE, &kaIdle, socklen_t(MemoryLayout<Int32>.size))
            var kaIntvl: Int32 = 2  // seconds between probes
            setsockopt(fd, Int32(IPPROTO_TCP), TCP_KEEPINTVL, &kaIntvl, socklen_t(MemoryLayout<Int32>.size))
            var kaCnt: Int32 = 4    // probes before giving up
            setsockopt(fd, Int32(IPPROTO_TCP), TCP_KEEPCNT, &kaCnt, socklen_t(MemoryLayout<Int32>.size))
            var sndTimeout = timeval(tv_sec: 5, tv_usec: 0)
            setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &sndTimeout, socklen_t(MemoryLayout<timeval>.size))
            clientFD = fd
            sendCond.lock()
            sendRing.reset()
            pendingSince = 0
            inflightBytes = 0     // a previous connection's stalled write must not be reported against this one
            inflightSince = 0
            senderDead = false
            effectiveSendBuffer = Int(effective)
            dropEventRead = 0
            dropEventWrite = 0
            dropEventLost = 0
            let generation = senderDropGeneration
            sendCond.unlock()
            startSender(fd)
            // This connection qualifies from scratch: whatever the source was doing, this receiver
            // must see a full silent tail before the sender stops sending.
            senderSquelch.beginConnection(dropGeneration: generation)
            lastSentTime = CFAbsoluteTimeGetCurrent()
            // The profile is deployment evidence, not negotiation: raw v1 has no greeting, so a
            // mismatched pair can only be caught by comparing these two lines across machines.
            FileHandle.standardError.write("client connected; capturing \(DEVUID); profile=\(SquelchProfile.id) hash=\(SquelchProfile.hash) confirm_frames=\(SquelchProfile.confirmFrames) silence_peak=\(SquelchProfile.silencePeak) suppression=\(continuousPCM ? "off (continuous PCM)" : "on")\n".data(using: .utf8)!)

            var queue: AudioQueueRef?
            var st = AudioQueueNewInput(&fmt, cb, nil, CFRunLoopGetCurrent(), CFRunLoopMode.commonModes.rawValue, 0, &queue)
            if st != noErr || queue == nil {
                FileHandle.standardError.write("AudioQueueNewInput \(st)\n".data(using: .utf8)!)
                sendCond.lock(); senderDead = true; sendCond.signal(); sendCond.unlock()
                close(fd); clientFD = -1; continue
            }
            var uid = DEVUID
            st = AudioQueueSetProperty(queue!, kAudioQueueProperty_CurrentDevice, &uid, UInt32(MemoryLayout<CFString>.size))
            if st != noErr { FileHandle.standardError.write("set device \(st)\n".data(using: .utf8)!) }
            // Capture quantum, GUI-tunable via BRIDGE_AQ_BUF_BYTES (default 4096 B = ~21 ms). It
            // changes the callback shape only: the squelch boundary is a frame index, not a
            // buffer count, so it must not move when this changes.
            let bufBytes = UInt32(ProcessInfo.processInfo.environment["BRIDGE_AQ_BUF_BYTES"] ?? "4096") ?? 4096
            // Hold a fixed ~125 ms of capture depth whatever the quantum is. A hardcoded count of
            // 6 meant halving the quantum to buy latency also halved the headroom that absorbs a
            // callback scheduling delay, and samples lost at the SOURCE are audible behind a
            // receiver buffer of any size. At the 21 ms default this still evaluates to 6.
            let captureDepthBytes = 48000 * 4 * 125 / 1000
            let bufCount = max(6, (captureDepthBytes + Int(bufBytes) - 1) / Int(bufBytes))
            for _ in 0..<bufCount {
                var buf: AudioQueueBufferRef?
                AudioQueueAllocateBuffer(queue!, bufBytes, &buf)
                if let buf = buf { AudioQueueEnqueueBuffer(queue!, buf, 0, nil) }
            }
            AudioQueueStart(queue!, nil)
            CFRunLoopRun()                       // runs until the sender stops it (client gone)
            AudioQueueStop(queue!, true)
            AudioQueueDispose(queue!, true)
            sendCond.lock(); senderDead = true; sendCond.signal(); sendCond.unlock()  // stop sender thread
            if clientFD >= 0 { close(clientFD); clientFD = -1 }
            FileHandle.standardError.write("client gone; ready\n".data(using: .utf8)!)
        }
    }
}
