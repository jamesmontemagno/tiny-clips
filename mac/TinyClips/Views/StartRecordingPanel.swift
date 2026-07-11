import AppKit
import SwiftUI
import AVFoundation

class StartRecordingPanel: NSPanel {
    struct MicrophoneSelection {
        let enabled: Bool
        let deviceID: String
    }

    struct WebcamSelection {
        let enabled: Bool
        let deviceID: String
        let shape: String
        var corner: String
        let size: String
    }

    private var onStart: ((Bool, MicrophoneSelection, WebcamSelection, Bool, Int) -> Void)?
    private var onCancel: (() -> Void)?

    convenience init(
        captureType: CaptureType,
        onStart: @escaping (Bool, MicrophoneSelection, WebcamSelection, Bool, Int) -> Void,
        onCancel: @escaping () -> Void
    ) {
        self.init(
            contentRect: NSRect(x: 0, y: 0, width: 520, height: 44),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        self.onStart = onStart
        self.onCancel = onCancel
        self.isReleasedWhenClosed = false
        self.level = .floating
        self.isOpaque = false
        self.backgroundColor = .clear
        self.hasShadow = true
        self.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        self.isMovableByWindowBackground = true

        let settings = CaptureSettings.shared
        let availableMicrophones = MicrophoneDeviceCatalog.availableOptions()
        let availableWebcams = WebcamDeviceCatalog.availableOptions()
        let resolvedMicrophoneID: String = {
            let saved = settings.selectedMicrophoneID
            guard !saved.isEmpty, availableMicrophones.contains(where: { $0.id == saved }) else {
                if !saved.isEmpty {
                    settings.selectedMicrophoneID = ""
                }
                return ""
            }
            return saved
        }()
        let resolvedWebcamID: String = {
            let saved = settings.selectedWebcamID
            guard !saved.isEmpty, availableWebcams.contains(where: { $0.id == saved }) else {
                if !saved.isEmpty {
                    settings.selectedWebcamID = ""
                }
                return ""
            }
            return saved
        }()
        let allowsMouseClickToggle: Bool
        let defaultMouseClicksEnabled: Bool
        
        allowsMouseClickToggle = true
        defaultMouseClicksEnabled = settings.shouldShowMouseClickVisuals(for: captureType)
        let hostingView = NSHostingView(rootView: StartRecordingView(
            captureType: captureType,
            systemAudio: settings.recordAudio,
            microphone: settings.recordMicrophone,
            selectedMicrophoneID: resolvedMicrophoneID,
            availableMicrophones: availableMicrophones,
            webcamEnabled: settings.webcamEnabled,
            selectedWebcamID: resolvedWebcamID,
            webcamShape: settings.webcamShape,
            webcamCorner: settings.webcamCorner,
            webcamSize: settings.webcamSize,
            availableWebcams: availableWebcams,
            mouseClicksEnabled: defaultMouseClicksEnabled,
            allowsMouseClickToggle: allowsMouseClickToggle,
            onStart: { [weak self] systemAudio, microphone, webcam, mouseClicksEnabled, videoTimeLimitMinutes in
                guard let panel = self, let onStart = panel.onStart else { return }
                panel.onStart = nil
                panel.onCancel = nil
                onStart(systemAudio, microphone, webcam, mouseClicksEnabled, videoTimeLimitMinutes)
            },
            onCancel: { [weak self] in
                guard let panel = self, let onCancel = panel.onCancel else { return }
                panel.onStart = nil
                panel.onCancel = nil
                onCancel()
            }
        ))
        let fittingSize = hostingView.fittingSize
        self.setContentSize(fittingSize)
        self.contentView = hostingView
    }

    func show(at position: NSPoint? = nil) {
        if let position {
            setFrameOrigin(position)
        } else if let screen = NSScreen.main {
            let x = screen.frame.midX - frame.width / 2
            let y = screen.frame.maxY - frame.height - 60
            setFrameOrigin(NSPoint(x: x, y: y))
        }
        orderFront(nil)
    }

    func dismiss() {
        orderOut(nil)
    }
}

private struct StartRecordingView: View {
    @Environment(\.colorScheme) private var colorScheme

    let captureType: CaptureType
    @State var systemAudio: Bool
    @State var microphone: Bool
    @State var selectedMicrophoneID: String
    let availableMicrophones: [MicrophoneDeviceOption]
    @State var webcamEnabled: Bool
    @State var selectedWebcamID: String
    @State var webcamShape: String
    @State var webcamCorner: String
    @State var webcamSize: String
    let availableWebcams: [WebcamDeviceOption]
    @State var mouseClicksEnabled: Bool
    let allowsMouseClickToggle: Bool
    let onStart: (Bool, StartRecordingPanel.MicrophoneSelection, StartRecordingPanel.WebcamSelection, Bool, Int) -> Void
    let onCancel: () -> Void

    /// Tracks that the user tried to enable an input but was blocked by a denied
    /// permission. When the app regains focus (e.g. after granting access in System
    /// Settings) the toggle is re-applied automatically.
    @State private var pendingMicrophoneEnable = false
    @State private var pendingWebcamEnable = false

    private var webcamShapeLabel: String {
        switch webcamShape.lowercased() {
        case "rectangle":
            return "Rectangle"
        case "rounded", "roundedrectangle":
            return "Rounded"
        default:
            return "Circle"
        }
    }

    private var webcamCornerLabel: String {
        switch webcamCorner.lowercased() {
        case "topleft":
            return "Top Left"
        case "topright":
            return "Top Right"
        case "bottomleft":
            return "Bottom Left"
        default:
            return "Bottom Right"
        }
    }

    private var webcamSizeLabel: String {
        switch webcamSize.lowercased() {
        case "small":
            return "Small"
        case "large":
            return "Large"
        default:
            return "Medium"
        }
    }

    var body: some View {
        HStack(spacing: 8) {
            if captureType != .gif {
                // System audio toggle
                Button {
                    systemAudio.toggle()
                } label: {
                    Image(systemName: systemAudio ? "speaker.wave.2.fill" : "speaker.slash.fill")
                        .font(.system(size: 13))
                        .foregroundStyle(systemAudio ? .white : .primary.opacity(0.5))
                        .frame(width: 28, height: 28)
                        .background(systemAudio ? .blue : .primary.opacity(0.12))
                        .clipShape(RoundedRectangle(cornerRadius: 6))
                }
                .buttonStyle(.plain)
                .help(systemAudio ? "Output audio: ON" : "Output audio: OFF")
                .accessibilityLabel("Output audio")
                .accessibilityValue(systemAudio ? "On" : "Off")
                .accessibilityHint("Toggles recording output audio.")

                // Microphone toggle
                Button {
                    if microphone {
                        microphone = false
                    } else {
                        enableMicrophoneRequestingPermission(openSettingsIfDenied: true)
                    }
                } label: {
                    Image(systemName: microphone ? "mic.fill" : "mic.slash.fill")
                        .font(.system(size: 13))
                        .foregroundStyle(microphone ? .white : .primary.opacity(0.5))
                        .frame(width: 28, height: 28)
                        .background(microphone ? .blue : .primary.opacity(0.12))
                        .clipShape(RoundedRectangle(cornerRadius: 6))
                }
                .buttonStyle(.plain)
                .help(microphone ? "Microphone: ON" : "Microphone: OFF")
                .accessibilityLabel("Microphone")
                .accessibilityValue(microphone ? "On" : "Off")
                .accessibilityHint("Toggles microphone recording.")

                if microphone {
                    Picker("Mic", selection: $selectedMicrophoneID) {
                        Text("System Default").tag("")
                        ForEach(availableMicrophones) { device in
                            Text(device.name).tag(device.id)
                        }
                    }
                    .labelsHidden()
                    .frame(width: 170)
                    .help("Choose microphone input device.")
                }
            }

            if captureType == .video {
                if webcamEnabled {
                    WebcamSetupPreview(deviceID: selectedWebcamID)
                        .frame(width: 80, height: 45)
                        .clipShape(RoundedRectangle(cornerRadius: 6))
                        .accessibilityLabel("Webcam preview")
                }

                Button {
                    if webcamEnabled {
                        webcamEnabled = false
                    } else {
                        enableWebcamRequestingPermission()
                    }
                } label: {
                    Image(systemName: webcamEnabled ? "video.fill.badge.checkmark" : "video.slash.fill")
                        .font(.system(size: 13))
                        .foregroundStyle(webcamEnabled ? .white : .primary.opacity(0.5))
                        .frame(width: 28, height: 28)
                        .background(webcamEnabled ? .blue : .primary.opacity(0.12))
                        .clipShape(RoundedRectangle(cornerRadius: 6))
                }
                .buttonStyle(.plain)
                .help(webcamEnabled ? "Webcam overlay: ON" : "Webcam overlay: OFF")
                .accessibilityLabel("Webcam overlay")
                .accessibilityValue(webcamEnabled ? "On" : "Off")
                .accessibilityHint("Toggles webcam overlay for this recording.")

                if webcamEnabled {
                    Menu {
                        Picker("Webcam device", selection: $selectedWebcamID) {
                            Text("System Default").tag("")
                            ForEach(availableWebcams) { device in
                                Text(device.name).tag(device.id)
                            }
                        }

                        Divider()

                        Picker("Shape", selection: $webcamShape) {
                            Text("Circle").tag("circle")
                            Text("Rounded rectangle").tag("rounded")
                            Text("Rectangle").tag("rectangle")
                        }

                        Picker("Corner", selection: $webcamCorner) {
                            Text("Top left").tag("topLeft")
                            Text("Top right").tag("topRight")
                            Text("Bottom left").tag("bottomLeft")
                            Text("Bottom right").tag("bottomRight")
                        }

                        Picker("Size", selection: $webcamSize) {
                            Text("Small").tag("small")
                            Text("Medium").tag("medium")
                            Text("Large").tag("large")
                        }
                    } label: {
                        Image(systemName: "gearshape.fill")
                            .font(.system(size: 13))
                            .foregroundStyle(.primary)
                            .frame(width: 28, height: 28)
                            .background(.primary.opacity(0.12))
                            .clipShape(RoundedRectangle(cornerRadius: 6))
                    }
                    .menuStyle(.borderlessButton)
                    .fixedSize()
                    .help("Choose webcam overlay settings.")
                    .accessibilityLabel("Webcam settings")
                    .accessibilityValue("\(webcamShapeLabel), \(webcamCornerLabel), \(webcamSizeLabel)")
                    .accessibilityHint("Opens webcam device, shape, corner, and size settings.")
                }
            }

            if allowsMouseClickToggle {
                // Mouse click visuals toggle (Pro only)
                Button {
                    mouseClicksEnabled.toggle()
                } label: {
                    Image(systemName: "cursorarrow.rays")
                        .font(.system(size: 13))
                        .foregroundStyle(mouseClicksEnabled ? .white : .primary.opacity(0.5))
                        .frame(width: 28, height: 28)
                        .background(mouseClicksEnabled ? .blue : .primary.opacity(0.12))
                        .clipShape(RoundedRectangle(cornerRadius: 6))
                }
                .buttonStyle(.plain)
                .help(mouseClicksEnabled ? "Mouse clicks in recording: ON" : "Mouse clicks in recording: OFF")
                .accessibilityLabel("Mouse click visuals")
                .accessibilityValue(mouseClicksEnabled ? "On" : "Off")
                .accessibilityHint("Toggles mouse click visuals for this recording.")
            }

            Divider()
                .frame(height: 20)
                .overlay(.primary.opacity(0.2))

            // Start button
            Button {
                onStart(
                    systemAudio,
                    .init(enabled: microphone, deviceID: selectedMicrophoneID),
                    .init(
                        enabled: webcamEnabled,
                        deviceID: selectedWebcamID,
                        shape: webcamShape,
                        corner: webcamCorner,
                        size: webcamSize
                    ),
                    mouseClicksEnabled,
                    CaptureSettings.shared.videoRecordingTimeLimitMinutes
                )
            } label: {
                HStack(spacing: 5) {
                    Circle()
                        .fill(.red)
                        .frame(width: 10, height: 10)
                    Text("Record")
                        .font(.system(size: 13, weight: .medium))
                        .foregroundStyle(.white)
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 8)
                .background(.red.opacity(0.8))
                .clipShape(RoundedRectangle(cornerRadius: 6))
            }
            .buttonStyle(.plain)
            .keyboardShortcut(.defaultAction)
            .accessibilityHint("Starts recording with the selected audio options.")

            // Cancel button
            Button {
                onCancel()
            } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(.primary.opacity(0.6))
                    .frame(width: 24, height: 24)
                    .background(.primary.opacity(0.1))
                    .clipShape(RoundedRectangle(cornerRadius: 6))
            }
            .buttonStyle(.plain)
            .help("Cancel")
            .keyboardShortcut(.cancelAction)
            .accessibilityLabel("Cancel recording setup")
            .accessibilityHint("Closes this panel without recording.")
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .fixedSize()
        .onAppear {
            // Pre-warm permissions for any inputs that are already enabled (e.g. from
            // saved settings) so the system prompt doesn't interrupt the countdown.
            if microphone {
                prewarmMicrophonePermission()
            }
            if webcamEnabled {
                prewarmCameraPermission()
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            // The user may have flipped a permission in System Settings; re-apply any
            // toggle they intended to enable once access has been granted.
            reapplyPendingPermissionsOnReturn()
        }
        .background {
            RoundedRectangle(cornerRadius: 10)
                .fill(colorScheme == .dark ? Color.black.opacity(0.8) : Color.white.opacity(0.9))
                .shadow(color: .black.opacity(0.15), radius: 8, x: 0, y: 2)
                .overlay {
                    RoundedRectangle(cornerRadius: 10)
                        .strokeBorder(.primary.opacity(0.15), lineWidth: 0.5)
                }
        }
    }

    // MARK: - Permission Handling

    /// Enables the microphone toggle, requesting permission first. If access was
    /// previously denied, optionally routes the user to System Settings since the
    /// system can no longer present the prompt.
    private func enableMicrophoneRequestingPermission(openSettingsIfDenied: Bool) {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized:
            microphone = true
        case .notDetermined:
            Task { @MainActor in
                microphone = await PermissionManager.shared.requestMicrophonePermission()
            }
        case .denied, .restricted:
            microphone = false
            if openSettingsIfDenied {
                pendingMicrophoneEnable = true
                PermissionManager.shared.openMicrophoneSettings()
            }
        @unknown default:
            break
        }
    }

    /// Enables the webcam overlay, requesting camera permission first. Webcam
    /// recordings include the microphone by default, so this also ensures
    /// microphone permission once the camera is authorized.
    private func enableWebcamRequestingPermission() {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized:
            webcamEnabled = true
            enableMicrophoneRequestingPermission(openSettingsIfDenied: false)
        case .notDetermined:
            Task { @MainActor in
                guard await PermissionManager.shared.requestCameraPermission() else { return }
                webcamEnabled = true
                enableMicrophoneRequestingPermission(openSettingsIfDenied: false)
            }
        case .denied, .restricted:
            webcamEnabled = false
            pendingWebcamEnable = true
            PermissionManager.shared.openCameraSettings()
        @unknown default:
            break
        }
    }

    /// Re-applies any input the user intended to enable but that was blocked by a
    /// denied permission, now that the app is active again and the permission may
    /// have been granted in System Settings.
    private func reapplyPendingPermissionsOnReturn() {
        if pendingWebcamEnable,
           AVCaptureDevice.authorizationStatus(for: .video) == .authorized {
            pendingWebcamEnable = false
            webcamEnabled = true
            enableMicrophoneRequestingPermission(openSettingsIfDenied: false)
        }

        if pendingMicrophoneEnable,
           AVCaptureDevice.authorizationStatus(for: .audio) == .authorized {
            pendingMicrophoneEnable = false
            microphone = true
        }
    }

    /// Requests microphone permission ahead of time when the toggle is already on,
    /// without nudging the user to System Settings on appear.
    private func prewarmMicrophonePermission() {
        if AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined {
            Task { @MainActor in
                microphone = await PermissionManager.shared.requestMicrophonePermission()
            }
        }
    }

    /// Requests camera permission ahead of time when the webcam toggle is already on.
    private func prewarmCameraPermission() {
        if AVCaptureDevice.authorizationStatus(for: .video) == .notDetermined {
            Task { @MainActor in
                webcamEnabled = await PermissionManager.shared.requestCameraPermission()
                if webcamEnabled {
                    prewarmMicrophonePermission()
                }
            }
        }
    }

}

private struct WebcamSetupPreview: NSViewRepresentable {
    let deviceID: String

    func makeNSView(context: Context) -> WebcamSetupPreviewView {
        WebcamSetupPreviewView(deviceID: deviceID)
    }

    func updateNSView(_ view: WebcamSetupPreviewView, context: Context) {
        view.useCamera(deviceID: deviceID)
    }

    static func dismantleNSView(_ view: WebcamSetupPreviewView, coordinator: ()) {
        view.stop()
    }
}

private final class WebcamSetupPreviewView: NSView {
    private let session = AVCaptureSession()
    private let captureQueue = DispatchQueue(label: "com.tinyclips.webcam-setup-preview")
    private var requestedDeviceID: String?

    init(deviceID: String) {
        super.init(frame: .zero)
        wantsLayer = true
        let previewLayer = AVCaptureVideoPreviewLayer(session: session)
        previewLayer.videoGravity = .resizeAspectFill
        layer = previewLayer
        useCamera(deviceID: deviceID)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override func layout() {
        super.layout()
        layer?.frame = bounds
    }

    func useCamera(deviceID: String) {
        guard requestedDeviceID != deviceID else { return }
        requestedDeviceID = deviceID
        Task { @MainActor [weak self] in
            guard let self, await PermissionManager.shared.requestCameraPermission() else { return }
            captureQueue.async { [weak self] in
                guard let self else { return }
                let device = WebcamDeviceCatalog.device(for: deviceID) ?? AVCaptureDevice.default(for: .video)
                guard let device, let input = try? AVCaptureDeviceInput(device: device) else { return }
                session.beginConfiguration()
                session.inputs.forEach(session.removeInput)
                if session.canAddInput(input) {
                    session.addInput(input)
                }
                session.commitConfiguration()
                if !session.isRunning {
                    session.startRunning()
                }
            }
        }
    }

    func stop() {
        captureQueue.async { [session] in
            if session.isRunning {
                session.stopRunning()
            }
        }
    }
}
