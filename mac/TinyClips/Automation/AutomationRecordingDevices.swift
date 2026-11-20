import AVFoundation
import Foundation

/// Recording-local choices. Resolving and checking them never writes preferences or requests consent.
struct AutomationRecordingDevices: Equatable {
    let microphone: Bool
    let camera: Bool

    static func resolve(
        _ params: [String: Any],
        savedMicrophone: Bool,
        savedCamera: Bool,
        microphoneAuthorized: Bool,
        cameraAuthorized: Bool
    ) throws -> Self {
        let devices = Self(
            microphone: try optionalBoolean(params, "microphone") ?? savedMicrophone,
            camera: try optionalBoolean(params, "camera") ?? savedCamera
        )
        try devices.requireAuthorization(
            savedMicrophone: savedMicrophone,
            savedCamera: savedCamera,
            microphoneAuthorized: microphoneAuthorized,
            cameraAuthorized: cameraAuthorized
        )
        return devices
    }

    func requireAuthorization(
        savedMicrophone: Bool,
        savedCamera: Bool,
        microphoneAuthorized: Bool,
        cameraAuthorized: Bool
    ) throws {
        if microphone && (!savedMicrophone || !microphoneAuthorized) {
            throw AutomationError(.permissionRequired,
                "The user must enable microphone recording in Tiny Clips and grant Microphone access in macOS System Settings before an agent can use it.")
        }
        if camera && (!savedCamera || !cameraAuthorized) {
            throw AutomationError(.permissionRequired,
                "The user must enable the camera in Tiny Clips and grant Camera access in macOS System Settings before an agent can use it.")
        }
    }

    @MainActor
    func requireCurrentAuthorization() throws {
        let settings = CaptureSettings.shared
        try requireAuthorization(
            savedMicrophone: settings.recordMicrophone,
            savedCamera: settings.webcamEnabled,
            microphoneAuthorized: AVCaptureDevice.authorizationStatus(for: .audio) == .authorized,
            cameraAuthorized: AVCaptureDevice.authorizationStatus(for: .video) == .authorized
        )
    }

    static func targetParameters(from params: [String: Any]) -> [String: Any] {
        params.filter { !["studio", "microphone", "camera"].contains($0.key) }
    }

    private static func optionalBoolean(_ params: [String: Any], _ name: String) throws -> Bool? {
        guard let value = params[name], !(value is NSNull) else { return nil }
        guard let boolean = AutomationValue.bool(value) else {
            throw AutomationError(.invalidParams, "\(name) must be true or false.")
        }
        return boolean
    }
}
