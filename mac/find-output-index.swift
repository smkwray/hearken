// Prints the kAudioHardwarePropertyDevices index of the best REAL output device
// for the talk side to play into — never BlackHole, never the Bridge Out aggregate.
// Preference: built-in headphones -> built-in speakers -> first other real output.
// ffmpeg's `-f audiotoolbox -audio_device_index N` uses this same raw-list order.
import Foundation
import CoreAudio

let sys = AudioObjectID(kAudioObjectSystemObject)
func addr(_ s: AudioObjectPropertySelector, _ sc: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> AudioObjectPropertyAddress {
    AudioObjectPropertyAddress(mSelector: s, mScope: sc, mElement: kAudioObjectPropertyElementMain)
}
func str(_ d: AudioObjectID, _ s: AudioObjectPropertySelector) -> String {
    var a = addr(s); var sz = UInt32(MemoryLayout<CFString?>.size); var cf: CFString? = nil
    return AudioObjectGetPropertyData(d, &a, 0, nil, &sz, &cf) == noErr ? (cf as String? ?? "") : ""
}
func outCh(_ d: AudioObjectID) -> Int {
    var a = addr(kAudioDevicePropertyStreamConfiguration, kAudioObjectPropertyScopeOutput); var sz: UInt32 = 0
    guard AudioObjectGetPropertyDataSize(d,&a,0,nil,&sz)==noErr, sz>0 else { return 0 }
    let p = UnsafeMutableRawPointer.allocate(byteCount: Int(sz), alignment: MemoryLayout<AudioBufferList>.alignment); defer{p.deallocate()}
    guard AudioObjectGetPropertyData(d,&a,0,nil,&sz,p)==noErr else { return 0 }
    return UnsafeMutableAudioBufferListPointer(p.assumingMemoryBound(to: AudioBufferList.self)).reduce(0){$0+Int($1.mNumberChannels)}
}

var a = addr(kAudioHardwarePropertyDevices); var sz: UInt32 = 0
AudioObjectGetPropertyDataSize(sys,&a,0,nil,&sz)
var ids = [AudioDeviceID](repeating:0, count: Int(sz)/MemoryLayout<AudioDeviceID>.size)
AudioObjectGetPropertyData(sys,&a,0,nil,&sz,&ids)

struct Cand { let idx: Int; let uid: String; let name: String }
var reals: [Cand] = []
for (i,d) in ids.enumerated() where outCh(d) > 0 {
    let uid = str(d, kAudioDevicePropertyDeviceUID)
    let name = str(d, kAudioObjectPropertyName)
    if uid == "BlackHole2ch_UID" || uid == "com.shane.bridgeout" { continue }
    if name.lowercased().contains("blackhole") || name == "Bridge Out" { continue }
    reals.append(Cand(idx: i, uid: uid, name: name))
}
func pick() -> Int? {
    if let h = reals.first(where: { $0.uid == "BuiltInHeadphoneOutputDevice" }) { return h.idx }
    if let s = reals.first(where: { $0.uid == "BuiltInSpeakerDevice" }) { return s.idx }
    return reals.first?.idx
}
if let idx = pick() { print(idx); exit(0) }
exit(1)
