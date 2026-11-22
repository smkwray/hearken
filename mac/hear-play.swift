// Receive-only playback on the Mac's default output. A sparse stream may pause
// between any two blocks; the player node renders silence until more arrive.
import AVFoundation
import Darwin
import Foundation

func finish(_ message: String, status: Int32 = 1) -> Never {
    FileHandle.standardError.write(Data((message + "\n").utf8))
    exit(status)
}

let args = CommandLine.arguments
guard args.count == 5, let port = UInt16(args[2]), port > 0,
      let timeoutMs = Int(args[3]), (2000...120000).contains(timeoutMs),
      let gain = Float(args[4]), (0...1).contains(gain) else {
    finish("usage: hear-play <local IPv4> <port> <read timeout ms> <gain 0..1>")
}

var address = sockaddr_in()
address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
address.sin_family = sa_family_t(AF_INET)
address.sin_port = port.bigEndian
guard inet_pton(AF_INET, args[1], &address.sin_addr) == 1 else { finish("invalid local IPv4") }
let listener = socket(AF_INET, SOCK_STREAM, 0)
guard listener >= 0 else { finish("socket: \(String(cString: strerror(errno)))") }
var yes: Int32 = 1
guard setsockopt(listener, SOL_SOCKET, SO_REUSEADDR, &yes, socklen_t(MemoryLayout.size(ofValue: yes))) == 0,
      withUnsafePointer(to: &address, { pointer in
          pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
              bind(listener, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
          }
      }) == 0, listen(listener, 1) == 0 else { finish("listen: \(String(cString: strerror(errno)))") }
let peer = accept(listener, nil, nil)
guard peer >= 0 else { finish("accept: \(String(cString: strerror(errno)))") }
close(listener)
var timeout = timeval(tv_sec: timeoutMs / 1000, tv_usec: Int32(timeoutMs % 1000 * 1000))
guard setsockopt(peer, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout.size(ofValue: timeout))) == 0
else { finish("read timeout: \(String(cString: strerror(errno)))") }

let engine = AVAudioEngine()
let player = AVAudioPlayerNode()
let format = AVAudioFormat(standardFormatWithSampleRate: 48000, channels: 2)!
engine.attach(player)
engine.connect(player, to: engine.mainMixerNode, format: format)
player.volume = gain
do { try engine.start() } catch { finish("output start: \(error)") }
let observer = NotificationCenter.default.addObserver(forName: .AVAudioEngineConfigurationChange,
                                                      object: engine, queue: nil) { _ in
    finish("output configuration changed; reopening")
}
player.play()

// Only this network thread allocates or converts. Each completion releases one
// fixed buffer after the player consumes it. TCP supplies backpressure.
let frames = 960
let buffers = (0..<4).map { _ in AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames))! }
let free = buffers.map { _ in DispatchSemaphore(value: 1) }
var samples = [Int16](repeating: 0, count: frames * 2)
var slot = 0
var totalFrames: UInt64 = 0
while true {
    var filled = 0
    let count = samples.count * MemoryLayout<Int16>.size
    let end = samples.withUnsafeMutableBytes { raw -> String? in
        while filled < count {
            let n = recv(peer, raw.baseAddress!.advanced(by: filled), count - filled, 0)
            if n > 0 { filled += n; continue }
            if n < 0 && errno == EINTR { continue }
            return n == 0 ? "peer closed" : String(cString: strerror(errno))
        }
        return nil
    }
    if filled >= 4 {
        let ready = free[slot]
        guard ready.wait(timeout: .now() + 2) == .success else { finish("render stalled; reopening") }
        let buffer = buffers[slot]
        let n = filled / 4
        buffer.frameLength = AVAudioFrameCount(n)
        let channels = buffer.floatChannelData!
        for i in 0..<n {
            channels[0][i] = Float(Int16(littleEndian: samples[2*i])) / 32768
            channels[1][i] = Float(Int16(littleEndian: samples[2*i + 1])) / 32768
        }
        player.scheduleBuffer(buffer, completionCallbackType: .dataConsumed) { _ in ready.signal() }
        totalFrames += UInt64(n)
        slot = (slot + 1) % buffers.count
    }
    if let reason = end {
        if reason == "peer closed" && totalFrames > 0 {
            // A FIN can arrive while the last buffers are still in the mixer.
            // Queue one silent frame after them and bound the device drain.
            let tail = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 1)!
            tail.frameLength = 1
            tail.floatChannelData![0][0] = 0
            tail.floatChannelData![1][0] = 0
            let drained = DispatchSemaphore(value: 0)
            player.scheduleBuffer(tail, completionCallbackType: .dataPlayedBack) { _ in drained.signal() }
            guard drained.wait(timeout: .now() + 2) == .success else { finish("output drain timed out") }
        }
        finish("\(reason); scheduled_frames=\(totalFrames) trailing_bytes=\(filled % 4)",
               status: reason == "peer closed" && filled % 4 == 0 ? 0 : 1)
    }
}
