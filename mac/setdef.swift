import CoreAudio
import Foundation
let sys = AudioObjectID(kAudioObjectSystemObject)
let want = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : ""
func addr(_ s: AudioObjectPropertySelector) -> AudioObjectPropertyAddress {
    AudioObjectPropertyAddress(mSelector: s, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
}
var a = addr(kAudioHardwarePropertyDevices); var sz: UInt32 = 0
AudioObjectGetPropertyDataSize(sys,&a,0,nil,&sz)
var ids=[AudioDeviceID](repeating:0,count:Int(sz)/MemoryLayout<AudioDeviceID>.size)
AudioObjectGetPropertyData(sys,&a,0,nil,&sz,&ids)
for d in ids {
    var ua = addr(kAudioDevicePropertyDeviceUID); var s2=UInt32(MemoryLayout<CFString?>.size); var cf:CFString?=nil
    AudioObjectGetPropertyData(d,&ua,0,nil,&s2,&cf)
    if (cf as String?) == want {
        var da = addr(kAudioHardwarePropertyDefaultOutputDevice); var dev=d
        let st = AudioObjectSetPropertyData(sys,&da,0,nil,UInt32(MemoryLayout<AudioDeviceID>.size),&dev)
        print("set default -> \(want) status=\(st)"); exit(0)
    }
}
print("device \(want) not found"); exit(1)
