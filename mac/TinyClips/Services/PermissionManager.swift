import AppKit
import AVFoundation
import ScreenCaptureKit
import UserNotifications

struct NotificationPermissionState {
    let authorizationStatus: UNAuthorizationStatus
    let alertSetting: UNNotificationSetting
    let alertStyle: UNAlertStyle

    var canRequest: Bool { authorizationStatus == .notDetermined }

    var alertsEnabled: Bool {
        switch authorizationStatus {
        case .authorized, .ephemeral:
            return alertSetting == .enabled && alertStyle != .none
        case .notDetermined, .denied, .provisional:
            return false
        @unknown default:
            return false
        }
    }

    var status: String {
        switch authorizationStatus {
        case .notDetermined: return "notDetermined"
        case .denied: return "denied"
        case .authorized: return "authorized"
        case .provisional: return "provisional"
        case .ephemeral: return "ephemeral"
        @unknown default: return "unknown"
        }
    }

    var summary: String {
        switch authorizationStatus {
        case .notDetermined: return "Not requested"
        case .denied: return "Disabled in macOS"
        case .provisional: return "Delivered quietly"
        case .authorized, .ephemeral:
            if alertSetting != .enabled { return "Enabled without alerts" }
            return alertStyle == .none ? "Enabled without banners" : "Enabled"
        @unknown default: return "Unknown"
        }
    }
}

@MainActor
class PermissionManager: ObservableObject {
    static let shared = PermissionManager()

    @Published var hasPermission = false

    func checkPermission() async -> Bool {
        await checkScreenRecordingPermission(requestIfNeeded: true)
    }

    func hasScreenRecordingPermission() -> Bool {
        if hasPermission { return true }

        if CGPreflightScreenCaptureAccess() {
            hasPermission = true
            return true
        }

        return false
    }

    func requestMicrophonePermission() async -> Bool {
        let status = AVCaptureDevice.authorizationStatus(for: .audio)
        switch status {
        case .authorized:
            return true
        case .notDetermined:
            return await AVCaptureDevice.requestAccess(for: .audio)
        case .denied, .restricted:
            return false
        @unknown default:
            return false
        }
    }

    func microphonePermissionGranted() -> Bool {
        AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
    }

    func requestCameraPermission() async -> Bool {
        let status = AVCaptureDevice.authorizationStatus(for: .video)
        switch status {
        case .authorized:
            return true
        case .notDetermined:
            return await AVCaptureDevice.requestAccess(for: .video)
        case .denied, .restricted:
            return false
        @unknown default:
            return false
        }
    }

    func cameraPermissionGranted() -> Bool {
        AVCaptureDevice.authorizationStatus(for: .video) == .authorized
    }

    func requestNotificationPermission() async -> Bool {
        let center = UNUserNotificationCenter.current()
        let status = await notificationPermissionStatus()
        switch status {
        case .authorized, .provisional, .ephemeral:
            return true
        case .notDetermined:
            do {
                return try await center.requestAuthorization(options: [.alert, .sound])
            } catch {
                return false
            }
        case .denied:
            return false
        @unknown default:
            return false
        }
    }

    func notificationPermissionGranted() async -> Bool {
        let status = await notificationPermissionStatus()
        switch status {
        case .authorized, .provisional, .ephemeral:
            return true
        case .notDetermined, .denied:
            return false
        @unknown default:
            return false
        }
    }

    func openScreenRecordingSettings() {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") else { return }
        NSWorkspace.shared.open(url)
    }

    func openMicrophoneSettings() {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone") else { return }
        NSWorkspace.shared.open(url)
    }

    func openCameraSettings() {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Camera") else { return }
        NSWorkspace.shared.open(url)
    }

    @discardableResult
    func openNotificationSettings() -> Bool {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.preference.notifications") else { return false }
        return NSWorkspace.shared.open(url)
    }

    // MARK: - Internal

    private func checkScreenRecordingPermission(requestIfNeeded: Bool) async -> Bool {
        // CGPreflightScreenCaptureAccess is unreliable on macOS 15+ — it can
        // return false even when permission is granted. Try ScreenCaptureKit
        // first as the source of truth, then fall back to the CG APIs.
        if hasPermission { return true }

        // Quick CG check — if it says yes, trust it
        if CGPreflightScreenCaptureAccess() {
            hasPermission = true
            return true
        }

        guard requestIfNeeded else {
            return false
        }

        // CG said no, but it may be wrong. Do a real SCK query to confirm.
        do {
            _ = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
            hasPermission = true
            return true
        } catch {}

        // Genuinely not granted — request access
        _ = CGRequestScreenCaptureAccess()
        return false
    }

    func notificationPermissionState() async -> NotificationPermissionState {
        await withCheckedContinuation { continuation in
            UNUserNotificationCenter.current().getNotificationSettings { settings in
                continuation.resume(returning: NotificationPermissionState(
                    authorizationStatus: settings.authorizationStatus,
                    alertSetting: settings.alertSetting,
                    alertStyle: settings.alertStyle
                ))
            }
        }
    }

    private func notificationPermissionStatus() async -> UNAuthorizationStatus {
        await notificationPermissionState().authorizationStatus
    }

}
