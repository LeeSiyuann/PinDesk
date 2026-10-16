import Carbon
import Foundation
import PinDeskCore

/// Carbon's registered hot key receives only this combination; it does not monitor typed keys.
@MainActor
final class GlobalHotKey {
    private var hotKey: EventHotKeyRef?
    private var handler: EventHandlerRef?
    private let action: () -> Void
    private let identifier: EventHotKeyID
    private static var nextIdentifier: UInt32 = 1
    var isRegistered: Bool { hotKey != nil }

    init(preference: HotKeyPreference, action: @escaping () -> Void) {
        self.action = action
        self.identifier = EventHotKeyID(signature: OSType(0x50444B50), id: Self.nextIdentifier)
        Self.nextIdentifier &+= 1
        guard (try? preference.validate()) != nil, let keyCode = Self.keyCodes[preference.letter] else { return }
        var type = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        let callback: EventHandlerUPP = { _, event, context in
            guard let context, let event else { return OSStatus(eventNotHandledErr) }
            // Application event targets run on the main thread; no other key event data is read.
            let object = Unmanaged<GlobalHotKey>.fromOpaque(context).takeUnretainedValue()
            var received = EventHotKeyID(signature: 0, id: 0)
            guard GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                                    nil, MemoryLayout<EventHotKeyID>.size, nil, &received) == noErr else {
                return OSStatus(eventNotHandledErr)
            }
            // Saving settings briefly holds both registrations. Each handler must process only its own ID.
            return MainActor.assumeIsolated {
                guard received.signature == object.identifier.signature, received.id == object.identifier.id else {
                    return OSStatus(eventNotHandledErr)
                }
                object.action()
                return noErr
            }
        }
        let status = InstallEventHandler(GetApplicationEventTarget(), callback, 1, &type,
                                        Unmanaged.passUnretained(self).toOpaque(), &handler)
        guard status == noErr else { return }
        var modifiers: UInt32 = 0
        if preference.modifiers.contains(.control) { modifiers |= UInt32(controlKey) }
        if preference.modifiers.contains(.option) { modifiers |= UInt32(optionKey) }
        if preference.modifiers.contains(.shift) { modifiers |= UInt32(shiftKey) }
        if preference.modifiers.contains(.command) { modifiers |= UInt32(cmdKey) }
        if RegisterEventHotKey(keyCode, modifiers, identifier,
                               GetApplicationEventTarget(), 0, &hotKey) != noErr {
            hotKey = nil
        }
    }

    deinit {
        if let hotKey { UnregisterEventHotKey(hotKey) }
        if let handler { RemoveEventHandler(handler) }
    }

    private static let keyCodes: [String: UInt32] = [
        "A": UInt32(kVK_ANSI_A), "B": UInt32(kVK_ANSI_B), "C": UInt32(kVK_ANSI_C), "D": UInt32(kVK_ANSI_D),
        "E": UInt32(kVK_ANSI_E), "F": UInt32(kVK_ANSI_F), "G": UInt32(kVK_ANSI_G), "H": UInt32(kVK_ANSI_H),
        "I": UInt32(kVK_ANSI_I), "J": UInt32(kVK_ANSI_J), "K": UInt32(kVK_ANSI_K), "L": UInt32(kVK_ANSI_L),
        "M": UInt32(kVK_ANSI_M), "N": UInt32(kVK_ANSI_N), "O": UInt32(kVK_ANSI_O), "P": UInt32(kVK_ANSI_P),
        "Q": UInt32(kVK_ANSI_Q), "R": UInt32(kVK_ANSI_R), "S": UInt32(kVK_ANSI_S), "T": UInt32(kVK_ANSI_T),
        "U": UInt32(kVK_ANSI_U), "V": UInt32(kVK_ANSI_V), "W": UInt32(kVK_ANSI_W), "X": UInt32(kVK_ANSI_X),
        "Y": UInt32(kVK_ANSI_Y), "Z": UInt32(kVK_ANSI_Z)
    ]
}
