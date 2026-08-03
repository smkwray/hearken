// Native CoreAudio capture + accept-first TCP server.
// Replaces the glitchy `ffmpeg -f avfoundation` capture. Captures the given
// audio device (by UID) via an input AudioQueue and streams raw s16le 48k
// stereo to a single connected TCP client. Re-accepts on disconnect.
//   usage: hear-capture <port> <deviceUID>
import Foundation
import AudioToolbox

let args = CommandLine.arguments
let PORT = UInt16(args.count > 1 ? args[1] : "45000") ?? 45000
let DEVUID = (args.count > 2 ? args[2] : "BlackHole2ch_UID") as CFString

signal(SIGPIPE, SIG_IGN)   // writing to a closed client must not kill us

var clientFD: Int32 = -1

// --- silence suppression (squelch) ---------------------------------------
// Don't ship a constant ~1.5 Mbps of digital silence. Stop sending once the
// captured audio has been silent for `squelchHold`; resume instantly on the
// first real sample. While suppressed, trickle one buffer every
// `squelchKeepalive` s to keep the TCP/Tailscale path warm and the receiver
// primed (~a few kbps vs 1.5 Mbps). BRIDGE_SQUELCH=0 disables (old behavior).
let squelchThreshold = Int16(ProcessInfo.processInfo.environment["BRIDGE_SQUELCH"] ?? "16") ?? 16  // peak |s16| (~-66 dBFS)
let squelchHold = 0.25        // s of continuous silence before we stop sending (tail)
let squelchKeepalive = 2.0    // max gap between transmitted buffers while silent
var lastSoundTime = CFAbsoluteTimeGetCurrent()
var lastSentTime = 0.0

// bufferIsSilent: is every s16le sample at/below the squelch threshold? thr<=0 disables.
func bufferIsSilent(_ p: UnsafeRawPointer, _ len: Int, _ thr: Int16) -> Bool {
    if thr <= 0 { return false }
    let n = len / 2
    let s = p.bindMemory(to: Int16.self, capacity: n)
    for i in 0..<n {
        let v = Int(s[i])
        if (v < 0 ? -v : v) > Int(thr) { return false }
    }
    return true
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
// callback only appends to this queue; a dedicated thread does the blocking I/O.
// On overflow (sustained stall) we drop the OLDEST audio, frame-aligned, so the
// stream stays fresh rather than building latency.
let sendCond = NSCondition()
var sendPending = Data()
var senderDead = false
let sendPendingMax = 96 * 1024      // ~0.5s ceiling during a stall, then drop-oldest
let mainLoop = CFRunLoopGetCurrent()
var pendingSince = 0.0
var captureFrames: UInt64 = 0
var lastCallbackTime = 0.0
var maxCallbackGapMs = 0.0
var pendingHighWater = 0
var dropEvents: UInt64 = 0
var dropBytes: UInt64 = 0
var maxDropAgeMs = 0.0
var inFlightAgeMs = 0.0
var writeCalls: UInt64 = 0
var writeBytes: UInt64 = 0
var maxWriteMs = 0.0
var writesOver20: UInt64 = 0
var writesOver100: UInt64 = 0
var writesOver250: UInt64 = 0
var effectiveSendBuffer = 0
let dropEventCapacity = 128
var dropEventBytes = [Int](repeating: 0, count: dropEventCapacity)
var dropEventAgeMs = [Double](repeating: 0, count: dropEventCapacity)
var dropEventTime = [Double](repeating: 0, count: dropEventCapacity)
var dropEventRead = 0
var dropEventWrite = 0
var dropEventLost: UInt64 = 0

// Callback metrics are recorded under the existing short queue lock. Formatting
// and log I/O remain on the sender thread, never on the AudioQueue callback.
func recordCaptureCallback(_ len: Int, _ now: Double) {
    sendCond.lock()
    captureFrames += UInt64(len / 4)
    if lastCallbackTime > 0 {
        let gap = (now - lastCallbackTime) * 1000
        if gap > maxCallbackGapMs { maxCallbackGapMs = gap }
    }
    lastCallbackTime = now
    sendCond.unlock()
}

func enqueueSend(_ p: UnsafeRawPointer, _ len: Int, _ now: Double) {
    sendCond.lock()
    if !senderDead {
        if sendPending.isEmpty { pendingSince = now }
        let over = sendPending.count + len - sendPendingMax
        if over > 0 {
            let removed = (over + 3) / 4 * 4
            let ageMs = pendingSince > 0 ? (now - pendingSince) * 1000 : 0
            sendPending.removeFirst(removed)  // frame-aligned drop-oldest
            dropEvents += 1
            dropBytes += UInt64(removed)
            if ageMs > maxDropAgeMs { maxDropAgeMs = ageMs }
            let next = (dropEventWrite + 1) % dropEventCapacity
            if next == dropEventRead {
                dropEventLost += 1
            } else {
                dropEventBytes[dropEventWrite] = removed
                dropEventAgeMs[dropEventWrite] = ageMs
                dropEventTime[dropEventWrite] = now
                dropEventWrite = next
            }
        }
        sendPending.append(UnsafeRawBufferPointer(start: p, count: len).bindMemory(to: UInt8.self))
        if sendPending.count > pendingHighWater { pendingHighWater = sendPending.count }
        sendCond.signal()
    }
    sendCond.unlock()
}

// Drain the fixed numeric event ring on the sender thread. The callback only
// stores numbers in preallocated slots; formatting and logging happen here.
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
        let bytes = dropEventBytes[dropEventRead]
        let ageMs = dropEventAgeMs[dropEventRead]
        let happened = dropEventTime[dropEventRead]
        dropEventRead = (dropEventRead + 1) % dropEventCapacity
        sendCond.unlock()
        let delayMs = (CFAbsoluteTimeGetCurrent() - happened) * 1000
        let line = String(format: "event=sender_drop frames=%d bytes=%d oldest_age_ms=%.1f report_delay_ms=%.1f\n",
            bytes / 4, bytes, ageMs, delayMs)
        FileHandle.standardError.write(line.data(using: .utf8)!)
    }
}

func emitSenderTelemetry() {
    let now = CFAbsoluteTimeGetCurrent()
    sendCond.lock()
    let pending = sendPending.count
    let oldestMs = pending > 0 && pendingSince > 0 ? (now - pendingSince) * 1000 : 0
    let captureFramesNow = captureFrames
    let callbackGapNow = maxCallbackGapMs
    let pendingHighWaterNow = pendingHighWater
    let inFlightAgeNow = inFlightAgeMs
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
    maxCallbackGapMs = 0
    pendingHighWater = pending
    maxWriteMs = 0
    sendCond.unlock()
    // String construction and log I/O happen after releasing the queue lock, so
    // the AudioQueue callback cannot wait behind either operation.
    let line = String(format: "event=sender_metrics capture_frames=%llu callback_max_gap_ms=%.1f pending_bytes=%d pending_ms=%.1f pending_high_water=%d oldest_ms=%.1f inflight_age_ms=%.1f drop_events=%llu drop_bytes=%llu max_drop_age_ms=%.1f write_calls=%llu write_bytes=%llu max_write_ms=%.1f writes_over_20ms=%llu writes_over_100ms=%llu writes_over_250ms=%llu effective_sndbuf=%d",
        captureFramesNow, callbackGapNow, pending, Double(pending) / 192.0, pendingHighWaterNow, oldestMs,
        inFlightAgeNow, dropEventsNow, dropBytesNow, maxDropAgeNow, writeCallsNow, writeBytesNow, maxWriteNow,
        writesOver20Now, writesOver100Now, writesOver250Now, effectiveSendBufferNow)
    FileHandle.standardError.write((line + "\n").data(using: .utf8)!)
}

// startSender drains the queue to fd on its own thread; on write failure it marks
// the link dead and stops the main run loop so we tear down and re-accept.
func startSender(_ fd: Int32) {
    Thread.detachNewThread {
        var lastTelemetry = CFAbsoluteTimeGetCurrent()
        while true {
            sendCond.lock()
            while sendPending.isEmpty && !senderDead {
                _ = sendCond.wait(until: Date(timeIntervalSinceNow: 1.0))
                if CFAbsoluteTimeGetCurrent() - lastTelemetry >= 1.0 { break }
            }
            if senderDead { sendCond.unlock(); return }
            if sendPending.isEmpty {
                sendCond.unlock()
                emitDropEvents()
                emitSenderTelemetry()
                lastTelemetry = CFAbsoluteTimeGetCurrent()
                continue
            }
            let now = CFAbsoluteTimeGetCurrent()
            inFlightAgeMs = pendingSince > 0 ? (now - pendingSince) * 1000 : 0
            let chunk = sendPending
            sendPending.removeAll(keepingCapacity: true)
            pendingSince = 0
            sendCond.unlock()
            let writeStart = CFAbsoluteTimeGetCurrent()
            let ok = chunk.withUnsafeBytes { writeAll(fd, $0.baseAddress!, chunk.count) }
            let writeMs = (CFAbsoluteTimeGetCurrent() - writeStart) * 1000
            sendCond.lock()
            writeCalls += 1
            writeBytes += UInt64(chunk.count)
            if writeMs > maxWriteMs { maxWriteMs = writeMs }
            if writeMs > 20 { writesOver20 += 1 }
            if writeMs > 100 { writesOver100 += 1 }
            if writeMs > 250 { writesOver250 += 1 }
            inFlightAgeMs = 0
            sendCond.unlock()
            if writeMs > 20 {
                FileHandle.standardError.write(String(format: "event=sender_slow_write bytes=%d duration_ms=%.1f\n", chunk.count, writeMs).data(using: .utf8)!)
            }
            emitDropEvents()
            if !ok {
                sendCond.lock(); senderDead = true; sendCond.unlock()
                CFRunLoopStop(mainLoop)
                return
            }
            if CFAbsoluteTimeGetCurrent() - lastTelemetry >= 1.0 {
                emitSenderTelemetry()
                lastTelemetry = CFAbsoluteTimeGetCurrent()
            }
        }
    }
}

// AudioQueue input callback: hand the captured bytes to the sender thread, recycle buffer.
let cb: AudioQueueInputCallback = { _, queue, bufRef, _, _, _ in
    let b = bufRef.pointee
    let len = Int(b.mAudioDataByteSize)
    if clientFD >= 0, len > 0 {
        let now = CFAbsoluteTimeGetCurrent()
        recordCaptureCallback(len, now)
        let silent = bufferIsSilent(b.mAudioData, len, squelchThreshold)
        if !silent { lastSoundTime = now }
        let inHold = (now - lastSoundTime) < squelchHold
        let keepalive = (now - lastSentTime) >= squelchKeepalive
        if !silent || inHold || keepalive {       // squelch: skip pure-silence buffers
            lastSentTime = now
            enqueueSend(b.mAudioData, len, now)
        }
    }
    AudioQueueEnqueueBuffer(queue, bufRef, 0, nil)
}

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
    mSampleRate: 48000,
    mFormatID: kAudioFormatLinearPCM,
    mFormatFlags: kLinearPCMFormatFlagIsSignedInteger | kLinearPCMFormatFlagIsPacked,
    mBytesPerPacket: 4, mFramesPerPacket: 1, mBytesPerFrame: 4,
    mChannelsPerFrame: 2, mBitsPerChannel: 16, mReserved: 0)

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
    sendPending.removeAll()
    pendingSince = 0
    senderDead = false
    effectiveSendBuffer = Int(effective)
    dropEventRead = 0
    dropEventWrite = 0
    dropEventLost = 0
    sendCond.unlock()
    startSender(fd)
    lastSoundTime = CFAbsoluteTimeGetCurrent()   // fresh hold window so the new client gets audio at once
    FileHandle.standardError.write("client connected; capturing \(DEVUID); squelch=\(squelchThreshold) (0=off)\n".data(using: .utf8)!)

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
    // Capture quantum, GUI-tunable via BRIDGE_AQ_BUF_BYTES (default 4096 B = ~21 ms).
    let bufBytes = UInt32(ProcessInfo.processInfo.environment["BRIDGE_AQ_BUF_BYTES"] ?? "4096") ?? 4096
    for _ in 0..<6 {
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
