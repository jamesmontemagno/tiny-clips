import AppKit
import Carbon.HIToolbox

// MARK: - HotKeyManager

/// Registers system-wide keyboard shortcuts via Carbon `RegisterEventHotKey`.
/// Actions are always dispatched on the main thread.
final class HotKeyManager {
    private static let hotKeySignature: OSType = 0x54434C50 // 'TCLP'

    // MARK: - Types

    enum HotKeyID: UInt32 {
        case screenshot = 1
        case recordVideo = 2
        case recordGif = 3
        case stopRecording = 4
    }

    private struct RegisteredHotKey {
        let reference: EventHotKeyRef
        let keyCode: UInt32
        let modifiers: UInt32
        let action: () -> Void
    }

    struct RegistrationFailure {
        let name: String
        let status: OSStatus
    }

    struct RegistrationResult {
        let failures: [RegistrationFailure]
        let restorationFailures: [RegistrationFailure]

        static let success = RegistrationResult(failures: [], restorationFailures: [])

        var isSuccess: Bool {
            failures.isEmpty
        }

        var errorMessage: String? {
            guard !failures.isEmpty else { return nil }

            let rejectedNames = failures.map(\.name).joined(separator: ", ")
            var message = "macOS could not register \(rejectedNames). Another app may already use this shortcut. Choose a different combination."

            if !restorationFailures.isEmpty {
                let restoredNames = restorationFailures.map(\.name).joined(separator: ", ")
                message += " The previous shortcut remains in Settings, but macOS could not reactivate \(restoredNames). Close the competing app or restart TinyClips."
            }

            return message
        }
    }

    private struct HotKeyRegistration {
        let id: HotKeyID
        let name: String
        let keyCode: UInt32
        let modifiers: UInt32
        let action: () -> Void
    }

    // MARK: - Properties

    private var eventHandlerRef: EventHandlerRef?
    private var registeredHotKeys: [UInt32: RegisteredHotKey] = [:]

    // MARK: - Lifecycle

    init() {
        installEventHandlerIfNeeded()
    }

    deinit {
        unregisterAll()

        if let eventHandlerRef {
            RemoveEventHandler(eventHandlerRef)
        }
    }

    // MARK: - Public

    func registerCaptureHotKeys(
        screenshotKeyCode: UInt32,
        screenshotModifiers: UInt32,
        onScreenshot: @escaping () -> Void,
        videoKeyCode: UInt32,
        videoModifiers: UInt32,
        onRecordVideo: @escaping () -> Void,
        gifKeyCode: UInt32,
        gifModifiers: UInt32,
        onRecordGif: @escaping () -> Void
    ) -> RegistrationResult {
        replace(
            hotKeys: [
                HotKeyRegistration(
                    id: .screenshot,
                    name: "Screenshot",
                    keyCode: screenshotKeyCode,
                    modifiers: screenshotModifiers,
                    action: onScreenshot
                ),
                HotKeyRegistration(
                    id: .recordVideo,
                    name: "Record Video",
                    keyCode: videoKeyCode,
                    modifiers: videoModifiers,
                    action: onRecordVideo
                ),
                HotKeyRegistration(
                    id: .recordGif,
                    name: "Record GIF",
                    keyCode: gifKeyCode,
                    modifiers: gifModifiers,
                    action: onRecordGif
                )
            ]
        )
    }

    func registerStopHotKey(onStopRecording: @escaping () -> Void) -> RegistrationResult {
        replace(
            hotKeys: [
                HotKeyRegistration(
                    id: .stopRecording,
                    name: "Stop Recording",
                    keyCode: UInt32(HotKeyBinding.stopRecording.keyCode),
                    modifiers: UInt32(HotKeyBinding.stopRecording.carbonModifiers),
                    action: onStopRecording
                )
            ]
        )
    }

    func unregisterStopHotKey() {
        unregister(id: .stopRecording)
    }

    func unregisterAll() {
        let hotKeyRefs = registeredHotKeys.values.map(\.reference)
        registeredHotKeys.removeAll()

        for hotKeyRef in hotKeyRefs {
            UnregisterEventHotKey(hotKeyRef)
        }
    }

    // MARK: - Registration

    private func replace(hotKeys: [HotKeyRegistration]) -> RegistrationResult {
        let ids = hotKeys.map(\.id)
        let previousHotKeys = ids.compactMap { id in
            registeredHotKeys[id.rawValue].map {
                HotKeyRegistration(
                    id: id,
                    name: name(for: id),
                    keyCode: $0.keyCode,
                    modifiers: $0.modifiers,
                    action: $0.action
                )
            }
        }

        ids.forEach(unregister)

        let failures = hotKeys.compactMap { hotKey -> RegistrationFailure? in
            register(hotKey) ? nil : RegistrationFailure(name: hotKey.name, status: lastRegistrationStatus)
        }
        guard failures.isEmpty else {
            ids.forEach(unregister)

            let restorationFailures = previousHotKeys.compactMap { hotKey -> RegistrationFailure? in
                register(hotKey) ? nil : RegistrationFailure(name: hotKey.name, status: lastRegistrationStatus)
            }
            return RegistrationResult(failures: failures, restorationFailures: restorationFailures)
        }

        return .success
    }

    private var lastRegistrationStatus: OSStatus = noErr

    private func register(_ hotKey: HotKeyRegistration) -> Bool {
        var hotKeyRef: EventHotKeyRef?
        let hotKeyID = EventHotKeyID(signature: Self.hotKeySignature, id: hotKey.id.rawValue)

        lastRegistrationStatus = RegisterEventHotKey(
            hotKey.keyCode,
            hotKey.modifiers,
            hotKeyID,
            GetApplicationEventTarget(),
            0,
            &hotKeyRef
        )

        guard lastRegistrationStatus == noErr, let hotKeyRef else { return false }

        registeredHotKeys[hotKey.id.rawValue] = RegisteredHotKey(
            reference: hotKeyRef,
            keyCode: hotKey.keyCode,
            modifiers: hotKey.modifiers,
            action: hotKey.action
        )
        return true
    }

    private func name(for id: HotKeyID) -> String {
        switch id {
        case .screenshot:
            return "Screenshot"
        case .recordVideo:
            return "Record Video"
        case .recordGif:
            return "Record GIF"
        case .stopRecording:
            return "Stop Recording"
        }
    }

    private func unregister(id: HotKeyID) {
        guard let registeredHotKey = registeredHotKeys.removeValue(forKey: id.rawValue) else { return }
        UnregisterEventHotKey(registeredHotKey.reference)
    }

    // MARK: - Carbon Event Handler

    private func installEventHandlerIfNeeded() {
        guard eventHandlerRef == nil else { return }

        var eventType = EventTypeSpec(
            eventClass: OSType(kEventClassKeyboard),
            eventKind: OSType(kEventHotKeyPressed)
        )

        let userData = UnsafeMutableRawPointer(Unmanaged.passUnretained(self).toOpaque())

        InstallEventHandler(
            GetApplicationEventTarget(),
            Self.handleHotKeyEvent,
            1,
            &eventType,
            userData,
            &eventHandlerRef
        )
    }

    private func handleHotKey(id: UInt32) {
        let action = registeredHotKeys[id]?.action
        // Ensure main-actor isolation for callers (future-proofs for Swift 6)
        DispatchQueue.main.async { action?() }
    }

    private static let handleHotKeyEvent: EventHandlerUPP = { _, eventRef, userData in
        guard let eventRef, let userData else { return OSStatus(eventNotHandledErr) }

        var hotKeyID = EventHotKeyID()
        let status = GetEventParameter(
            eventRef,
            EventParamName(kEventParamDirectObject),
            EventParamType(typeEventHotKeyID),
            nil,
            MemoryLayout<EventHotKeyID>.size,
            nil,
            &hotKeyID
        )

        guard status == noErr, hotKeyID.signature == hotKeySignature else {
            return OSStatus(eventNotHandledErr)
        }

        let manager = Unmanaged<HotKeyManager>.fromOpaque(userData).takeUnretainedValue()
        manager.handleHotKey(id: hotKeyID.id)
        return noErr
    }
}
