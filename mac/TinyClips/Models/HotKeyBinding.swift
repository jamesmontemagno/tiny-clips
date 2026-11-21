import Carbon.HIToolbox
import SwiftUI
import AppKit

enum HotKeyAction: UInt32, CaseIterable, Hashable {
    case screenshot = 1
    case recordVideo = 2
    case recordGif = 3
    case copyTextFromRegion = 4
    case screenshotRegion = 5
    case screenshotWindow = 6
    case screenshotScreen = 7
    case stopRecording = 8
    case studioRecording = 9

    var displayName: String {
        switch self {
        case .screenshot:
            return "Screenshot"
        case .recordVideo:
            return "Record Video"
        case .studioRecording:
            return "Studio Recording"
        case .recordGif:
            return "Record GIF"
        case .copyTextFromRegion:
            return "Copy Text from Region"
        case .screenshotRegion:
            return "Screenshot Region"
        case .screenshotWindow:
            return "Screenshot Window"
        case .screenshotScreen:
            return "Screenshot Screen"
        case .stopRecording:
            return "Stop Recording"
        }
    }

    var iconName: String {
        switch self {
        case .screenshot:
            return "camera"
        case .recordVideo:
            return "video"
        case .studioRecording:
            return "square.stack.3d.up"
        case .recordGif:
            return "photo.on.rectangle.angled"
        case .copyTextFromRegion:
            return "text.viewfinder"
        case .screenshotRegion:
            return "viewfinder.rectangular"
        case .screenshotWindow:
            return "macwindow"
        case .screenshotScreen:
            return "display"
        case .stopRecording:
            return "stop.circle"
        }
    }

    func isGloballyActive(isRecording: Bool) -> Bool {
        self != .stopRecording || isRecording
    }
}

// MARK: - HotKeyBinding

/// Represents a global keyboard shortcut as a Carbon keyCode + modifiers bitmask.
/// Provides display string and SwiftUI `KeyboardShortcut` conversion helpers.
struct HotKeyBinding: Hashable {
    let keyCode: Int
    let carbonModifiers: Int

    // Carbon key code zero is the A key, so optional bindings need a negative sentinel.
    private static let unboundKeyCode = -1

    static let unbound = HotKeyBinding(keyCode: unboundKeyCode, carbonModifiers: 0)

    var isUnbound: Bool {
        keyCode == Self.unboundKeyCode && carbonModifiers == 0
    }

    // MARK: - Defaults

    /// ⌃⌥⌘ modifier mask shared by all default capture shortcuts.
    static let defaultCaptureModifiers: Int = Int(controlKey | optionKey | cmdKey)

    private static let supportedModifierMask = Int(controlKey | optionKey | shiftKey | cmdKey)

    static func defaultBinding(for action: HotKeyAction) -> HotKeyBinding {
        switch action {
        case .screenshot:
            return HotKeyBinding(keyCode: 23, carbonModifiers: defaultCaptureModifiers) // ⌃⌥⌘5
        case .recordVideo, .studioRecording, .recordGif, .copyTextFromRegion,
             .screenshotRegion, .screenshotWindow, .screenshotScreen, .stopRecording:
            return .unbound
        }
    }

    // MARK: - Validation

    static func validationError(
        for proposedBinding: HotKeyBinding,
        action: HotKeyAction,
        bindings: [HotKeyAction: HotKeyBinding]
    ) -> String? {
        if proposedBinding.isUnbound {
            return nil
        }

        guard proposedBinding.carbonModifiers & supportedModifierMask != 0 else {
            return "Add at least one modifier key (Control, Option, Shift, or Command)."
        }

        if let conflictingAction = bindings.first(where: {
            $0.key != action && $0.value == proposedBinding
        })?.key {
            return "That shortcut is already assigned to \(conflictingAction.displayName)."
        }

        return nil
    }

    // MARK: - Display

    /// Human-readable shortcut string, e.g. "⌃⌥⌘5".
    var displayString: String {
        isUnbound ? "Not set" : modifiersString + keyString
    }

    var modifiersString: String {
        var s = ""
        if carbonModifiers & Int(controlKey) != 0 { s += "⌃" }
        if carbonModifiers & Int(optionKey)  != 0 { s += "⌥" }
        if carbonModifiers & Int(shiftKey)   != 0 { s += "⇧" }
        if carbonModifiers & Int(cmdKey)     != 0 { s += "⌘" }
        return s
    }

    var keyString: String {
        Self.keyCodeToDisplayString(keyCode) ?? "?"
    }

    // MARK: - SwiftUI Conversion

    /// Returns the `Character` used by SwiftUI's `KeyboardShortcut`, if convertible.
    var swiftUICharacter: Character? {
        guard !isUnbound,
              let str = Self.keyCodeToDisplayString(keyCode),
              str.count == 1,
              let first = str.lowercased().first else { return nil }
        return first
    }

    var swiftUIShortcut: KeyboardShortcut? {
        guard !isUnbound else { return nil }
        let key: KeyEquivalent
        switch keyCode {
        case kVK_Return: key = .return
        case kVK_Tab: key = .tab
        case kVK_Space: key = .space
        case kVK_Delete: key = .delete
        case kVK_Escape: key = .escape
        case kVK_ForwardDelete: key = .deleteForward
        case kVK_LeftArrow: key = .leftArrow
        case kVK_RightArrow: key = .rightArrow
        case kVK_DownArrow: key = .downArrow
        case kVK_UpArrow: key = .upArrow
        case kVK_PageUp: key = .pageUp
        case kVK_PageDown: key = .pageDown
        case kVK_Home: key = .home
        case kVK_End: key = .end
        default:
            guard let character = swiftUICharacter else { return nil }
            key = KeyEquivalent(character)
        }
        return KeyboardShortcut(key, modifiers: swiftUIModifiers)
    }

    /// Returns SwiftUI `EventModifiers` equivalent to the stored Carbon modifiers.
    var swiftUIModifiers: SwiftUI.EventModifiers {
        var mods: SwiftUI.EventModifiers = []
        if carbonModifiers & Int(cmdKey)     != 0 { mods.insert(.command) }
        if carbonModifiers & Int(shiftKey)   != 0 { mods.insert(.shift)   }
        if carbonModifiers & Int(optionKey)  != 0 { mods.insert(.option)  }
        if carbonModifiers & Int(controlKey) != 0 { mods.insert(.control) }
        return mods
    }

    // MARK: - Carbon Modifiers from NSEvent

    /// Converts `NSEvent.ModifierFlags` to a Carbon modifiers bitmask.
    static func carbonModifiers(from nsModifiers: NSEvent.ModifierFlags) -> Int {
        var mods = 0
        if nsModifiers.contains(.command) { mods |= Int(cmdKey)     }
        if nsModifiers.contains(.shift)   { mods |= Int(shiftKey)   }
        if nsModifiers.contains(.option)  { mods |= Int(optionKey)  }
        if nsModifiers.contains(.control) { mods |= Int(controlKey) }
        return mods
    }

    // MARK: - Key Code → Display String

    /// Converts a Carbon/CGKeyCode to a display string using the current keyboard layout.
    static func keyCodeToDisplayString(_ code: Int) -> String? {
        if let fallback = fallbackKeyCodeString(code) {
            return fallback
        }
        guard let keyCode = UInt16(exactly: code) else {
            return nil
        }
        guard let rawSource = TISCopyCurrentKeyboardLayoutInputSource() else {
            return fallbackKeyCodeString(code)
        }
        let source = rawSource.takeRetainedValue()
        guard let dataPtr = TISGetInputSourceProperty(source, kTISPropertyUnicodeKeyLayoutData) else {
            return fallbackKeyCodeString(code)
        }
        let layoutData = Unmanaged<CFData>.fromOpaque(dataPtr).takeUnretainedValue()
        guard let bytePtr = CFDataGetBytePtr(layoutData) else {
            return fallbackKeyCodeString(code)
        }
        return bytePtr.withMemoryRebound(to: UCKeyboardLayout.self, capacity: 1) { layout -> String? in
            var deadKeyState: UInt32 = 0
            var chars = [UniChar](repeating: 0, count: 4)
            var length = 0
            let result = UCKeyTranslate(
                layout,
                keyCode,
                UInt16(kUCKeyActionDisplay),
                0,
                UInt32(LMGetKbdType()),
                UInt32(kUCKeyTranslateNoDeadKeysMask),
                &deadKeyState,
                4,
                &length,
                &chars
            )
            guard result == noErr, length > 0 else { return nil }
            return String(utf16CodeUnits: Array(chars.prefix(length)), count: length).uppercased()
        } ?? fallbackKeyCodeString(code)
    }

    /// Fallback lookup for special / non-printable keys.
    private static func fallbackKeyCodeString(_ code: Int) -> String? {
        switch code {
        case kVK_Return:        return "↩"
        case kVK_Tab:           return "⇥"
        case kVK_Space:         return "Space"
        case kVK_Delete:        return "⌫"
        case kVK_Escape:        return "Esc"
        case kVK_ForwardDelete: return "⌦"
        case kVK_LeftArrow:     return "←"
        case kVK_RightArrow:    return "→"
        case kVK_DownArrow:     return "↓"
        case kVK_UpArrow:       return "↑"
        case kVK_PageUp:        return "⇞"
        case kVK_PageDown:      return "⇟"
        case kVK_Home:          return "↖"
        case kVK_End:           return "↘"
        case kVK_F1:            return "F1"
        case kVK_F2:            return "F2"
        case kVK_F3:            return "F3"
        case kVK_F4:            return "F4"
        case kVK_F5:            return "F5"
        case kVK_F6:            return "F6"
        case kVK_F7:            return "F7"
        case kVK_F8:            return "F8"
        case kVK_F9:            return "F9"
        case kVK_F10:           return "F10"
        case kVK_F11:           return "F11"
        case kVK_F12:           return "F12"
        default:                return nil
        }
    }

}
