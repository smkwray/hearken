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

// AudioQueue input callback: ship the captured bytes to the client, recycle buffer.
let cb: AudioQueueInputCallback = { _, queue, bufRef, _, _, _ in
    let b = bufRef.pointee
    let len = Int(b.mAudioDataByteSize)
    if clientFD >= 0, len > 0 {
        let now = CFAbsoluteTimeGetCurrent()
        let silent = bufferIsSilent(b.mAudioData, len, squelchThreshold)
        if !silent { lastSoundTime = now }
        let inHold = (now - lastSoundTime) < squelchHold
        let keepalive = (now - lastSentTime) >= squelchKeepalive
        if !silent || inHold || keepalive {       // squelch: skip pure-silence buffers
            lastSentTime = now
            if !writeAll(clientFD, b.mAudioData, len) {
                close(clientFD); clientFD = -1
                CFRunLoopStop(CFRunLoopGetCurrent())
            }
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
    clientFD = fd
    lastSoundTime = CFAbsoluteTimeGetCurrent()   // fresh hold window so the new client gets audio at once
    FileHandle.standardError.write("client connected; capturing \(DEVUID); squelch=\(squelchThreshold) (0=off)\n".data(using: .utf8)!)

    var queue: AudioQueueRef?
    var st = AudioQueueNewInput(&fmt, cb, nil, CFRunLoopGetCurrent(), CFRunLoopMode.commonModes.rawValue, 0, &queue)
    if st != noErr || queue == nil { FileHandle.standardError.write("AudioQueueNewInput \(st)\n".data(using: .utf8)!); close(fd); clientFD = -1; continue }
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
    CFRunLoopRun()                       // runs until callback stops it (client gone)
    AudioQueueStop(queue!, true)
    AudioQueueDispose(queue!, true)
    if clientFD >= 0 { close(clientFD); clientFD = -1 }
    FileHandle.standardError.write("client gone; ready\n".data(using: .utf8)!)
}
