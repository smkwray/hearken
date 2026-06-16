import Foundation
import CoreAudio

let sys = AudioObjectID(kAudioObjectSystemObject)
let BRIDGE_UID = "com.shane.bridgeout"
let BRIDGE_NAME = "Bridge Out"

func addr(_ sel: AudioObjectPropertySelector,
          _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> AudioObjectPropertyAddress {
    AudioObjectPropertyAddress(mSelector: sel, mScope: scope, mElement: kAudioObjectPropertyElementMain)
}

func devices() -> [AudioDeviceID] {
    var a = addr(kAudioHardwarePropertyDevices)
    var size: UInt32 = 0
    guard AudioObjectGetPropertyDataSize(sys, &a, 0, nil, &size) == noErr else { return [] }
    var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
    AudioObjectGetPropertyData(sys, &a, 0, nil, &size, &ids)
    return ids
}

func str(_ dev: AudioObjectID, _ sel: AudioObjectPropertySelector) -> String? {
    var a = addr(sel)
    var size = UInt32(MemoryLayout<CFString?>.size)
    var cf: CFString? = nil
    let st = AudioObjectGetPropertyData(dev, &a, 0, nil, &size, &cf)
    if st == noErr, let cf = cf { return cf as String }
    return nil
}

func u32(_ dev: AudioObjectID, _ sel: AudioObjectPropertySelector) -> UInt32? {
    var a = addr(sel)
    var v: UInt32 = 0
    var size = UInt32(MemoryLayout<UInt32>.size)
    return AudioObjectGetPropertyData(dev, &a, 0, nil, &size, &v) == noErr ? v : nil
}

func outChannels(_ dev: AudioObjectID) -> Int {
    var a = addr(kAudioDevicePropertyStreamConfiguration, kAudioObjectPropertyScopeOutput)
    var size: UInt32 = 0
    guard AudioObjectGetPropertyDataSize(dev, &a, 0, nil, &size) == noErr, size > 0 else { return 0 }
    let p = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: MemoryLayout<AudioBufferList>.alignment)
    defer { p.deallocate() }
    guard AudioObjectGetPropertyData(dev, &a, 0, nil, &size, p) == noErr else { return 0 }
    let abl = UnsafeMutableAudioBufferListPointer(p.assumingMemoryBound(to: AudioBufferList.self))
    return abl.reduce(0) { $0 + Int($1.mNumberChannels) }
}

func defaultOutput() -> AudioDeviceID {
    var a = addr(kAudioHardwarePropertyDefaultOutputDevice)
    var dev: AudioDeviceID = 0
    var size = UInt32(MemoryLayout<AudioDeviceID>.size)
    AudioObjectGetPropertyData(sys, &a, 0, nil, &size, &dev)
    return dev
}

func setDefaultOutput(_ dev: AudioDeviceID) -> OSStatus {
    var a = addr(kAudioHardwarePropertyDefaultOutputDevice)
    var d = dev
    return AudioObjectSetPropertyData(sys, &a, 0, nil, UInt32(MemoryLayout<AudioDeviceID>.size), &d)
}

// 1) Destroy any prior Bridge Out so this is idempotent.
for d in devices() where str(d, kAudioDevicePropertyDeviceUID) == BRIDGE_UID {
    let st = AudioHardwareDestroyAggregateDevice(d)
    print("destroyed prior Bridge Out id=\(d) status=\(st)")
}

// 2) Discover BlackHole + a real built-in output.
var blackholeUID: String? = nil
var builtinUID: String? = nil
var builtinName = ""
for d in devices() where outChannels(d) > 0 {
    let name = str(d, kAudioObjectPropertyName) ?? "?"
    let uid = str(d, kAudioDevicePropertyDeviceUID) ?? ""
    if name.lowercased().contains("blackhole") { blackholeUID = uid }
    if u32(d, kAudioDevicePropertyTransportType) == kAudioDeviceTransportTypeBuiltIn {
        builtinUID = uid; builtinName = name
    }
}

// 3) Master/clock = current real default output (what the human hears); fall back to built-in.
let defOut = defaultOutput()
let defUID = str(defOut, kAudioDevicePropertyDeviceUID)
let defName = str(defOut, kAudioObjectPropertyName) ?? "?"
var masterUID = builtinUID
var masterName = builtinName
if let d = defUID, d != blackholeUID, d != BRIDGE_UID, outChannels(defOut) > 0 {
    masterUID = d; masterName = defName
}

guard let bh = blackholeUID else { print("ERROR: BlackHole 2ch not found"); exit(2) }
guard let mst = masterUID else { print("ERROR: no real output device to use as clock master"); exit(3) }
print("master(clock)=BlackHole [\(bh)]   drift-corrected=\(masterName) [\(mst)]")

// 4) Create the stacked multi-output device (persistent: private=0; stacked=1).
let desc: [String: Any] = [
    "name": BRIDGE_NAME,
    "uid": BRIDGE_UID,
    "private": 0,
    "stacked": 1,
    "master": bh,
    "subdevices": [
        ["uid": bh],                // BlackHole is the clock master -> steady 48k, glitch-free capture
        ["uid": mst, "drift": 1],   // drift-correct the real output (headphones) instead
    ],
]
var newID: AudioDeviceID = 0
let cst = AudioHardwareCreateAggregateDevice(desc as CFDictionary, &newID)
print("create status=\(cst) newID=\(newID)")
guard cst == noErr, newID != 0 else { exit(4) }

// 5) Make it the system default output.
let sst = setDefaultOutput(newID)
print("set-default-output status=\(sst)")

// 6) Report final state.
let finalOut = defaultOutput()
print("FINAL default output = \(str(finalOut, kAudioObjectPropertyName) ?? "?") [\(str(finalOut, kAudioDevicePropertyDeviceUID) ?? "?")]")
print(cst == noErr && sst == noErr && finalOut == newID ? "OK" : "PARTIAL")
