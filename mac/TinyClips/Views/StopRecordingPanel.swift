import AppKit
import AVFoundation
import SwiftUI

final class WebcamPreviewPanel: NSPanel {
    private let captureFrame: CGRect
    private let onCornerChange: (String) -> Void
    private var corner: String
    private let margin: CGFloat

    init(
        session: AVCaptureSession,
        selection: StartRecordingPanel.WebcamSelection,
        region: CaptureRegion,
        onCornerChange: @escaping (String) -> Void
    ) {
        let captureFrame = Self.captureFrame(for: region)
        let margin = min(16, min(captureFrame.width, captureFrame.height) * 0.1)
        let previewSize = Self.previewSize(for: selection, in: captureFrame, session: session, margin: margin)
        self.captureFrame = captureFrame
        self.corner = selection.corner
        self.onCornerChange = onCornerChange
        self.margin = margin

        super.init(
            contentRect: NSRect(origin: .zero, size: previewSize),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )

        isReleasedWhenClosed = false
        level = .floating
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]

        let preview = WebcamPreviewView(session: session, size: previewSize)
        preview.onDragEnd = { [weak self] in self?.finishDrag() }
        let radius: CGFloat
        switch selection.shape.lowercased() {
        case "circle": radius = previewSize.width / 2
        case "rounded", "roundedrectangle": radius = min(previewSize.width, previewSize.height) * 0.12
        default: radius = 0
        }
        preview.layer?.cornerRadius = radius
        preview.layer?.masksToBounds = true
        preview.layer?.borderColor = NSColor.white.withAlphaComponent(0.8).cgColor
        preview.layer?.borderWidth = 2
        preview.setAccessibilityElement(true)
        preview.setAccessibilityLabel("Webcam preview. Drag to change its corner in the recording.")
        preview.setAccessibilityCustomActions([
            accessibilityAction(named: "Move webcam to top left", corner: "topLeft"),
            accessibilityAction(named: "Move webcam to top right", corner: "topRight"),
            accessibilityAction(named: "Move webcam to bottom left", corner: "bottomLeft"),
            accessibilityAction(named: "Move webcam to bottom right", corner: "bottomRight")
        ])
        contentView = preview

#if DEBUG
        assert(Self.nearestCorner(for: CGPoint(x: 9, y: 9), in: CGRect(x: 0, y: 0, width: 10, height: 10)) == "topRight")
#endif
    }

    func show() {
        snap(to: corner)
        orderFront(nil)
    }

    private func finishDrag() {
        changeCorner(to: Self.nearestCorner(
            for: CGPoint(x: frame.midX, y: frame.midY),
            in: captureFrame
        ))
    }

    private func changeCorner(to corner: String) {
        self.corner = corner
        snap(to: corner)
        onCornerChange(corner)
    }

    private func accessibilityAction(named name: String, corner: String) -> NSAccessibilityCustomAction {
        NSAccessibilityCustomAction(name: name) { [weak self] in
            guard let self else { return false }
            self.changeCorner(to: corner)
            return true
        }
    }

    private func snap(to corner: String) {
        let isLeft = corner.lowercased().hasSuffix("left")
        let isTop = corner.lowercased().hasPrefix("top")
        setFrameOrigin(NSPoint(
            x: isLeft ? captureFrame.minX + margin : captureFrame.maxX - frame.width - margin,
            y: isTop ? captureFrame.maxY - frame.height - margin : captureFrame.minY + margin
        ))
    }

    private static func nearestCorner(for point: CGPoint, in frame: CGRect) -> String {
        let vertical = point.y >= frame.midY ? "top" : "bottom"
        let horizontal = point.x >= frame.midX ? "Right" : "Left"
        return vertical + horizontal
    }

    private static func captureFrame(for region: CaptureRegion) -> CGRect {
        guard let screen = NSScreen.screens.first(where: {
            ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID) == region.displayID
        }) else {
            return NSScreen.main?.frame ?? .zero
        }

        return CGRect(
            x: screen.frame.minX + region.sourceRect.minX,
            y: screen.frame.maxY - region.sourceRect.maxY,
            width: region.sourceRect.width,
            height: region.sourceRect.height
        )
    }

    private static func previewSize(
        for selection: StartRecordingPanel.WebcamSelection,
        in captureFrame: CGRect,
        session: AVCaptureSession,
        margin: CGFloat
    ) -> CGSize {
        let scale: CGFloat
        switch selection.size.lowercased() {
        case "small": scale = 0.18
        case "large": scale = 0.30
        default: scale = 0.24
        }
        let minDimension = min(captureFrame.width, captureFrame.height)
        let aspectRatio = selection.shape.lowercased() == "circle" ? 1 : cameraAspectRatio(for: session)
        let availableWidth = max(0, captureFrame.width - margin * 2)
        let availableHeight = max(0, captureFrame.height - margin * 2)
        let width = min(minDimension * scale, availableWidth, availableHeight * aspectRatio)
        return CGSize(
            width: width,
            height: width / aspectRatio
        )
    }

    private static func cameraAspectRatio(for session: AVCaptureSession) -> CGFloat {
        guard let input = session.inputs.compactMap({ $0 as? AVCaptureDeviceInput }).first else {
            return 16 / 9
        }
        let dimensions = CMVideoFormatDescriptionGetDimensions(input.device.activeFormat.formatDescription)
        guard dimensions.width > 0, dimensions.height > 0 else { return 16 / 9 }
        return CGFloat(dimensions.width) / CGFloat(dimensions.height)
    }
}

private final class WebcamPreviewView: NSView {
    private var dragStartMouse: NSPoint?
    private var dragStartOrigin: NSPoint?
    var onDragEnd: (() -> Void)?

    init(session: AVCaptureSession, size: CGSize) {
        super.init(frame: NSRect(origin: .zero, size: size))
        wantsLayer = true
        let previewLayer = AVCaptureVideoPreviewLayer(session: session)
        previewLayer.frame = bounds
        previewLayer.videoGravity = .resizeAspectFill
        layer?.addSublayer(previewLayer)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override func mouseDown(with event: NSEvent) {
        dragStartMouse = NSEvent.mouseLocation
        dragStartOrigin = window?.frame.origin
    }

    override func mouseDragged(with event: NSEvent) {
        guard let dragStartMouse, let dragStartOrigin else { return }
        let mouse = NSEvent.mouseLocation
        window?.setFrameOrigin(NSPoint(
            x: dragStartOrigin.x + mouse.x - dragStartMouse.x,
            y: dragStartOrigin.y + mouse.y - dragStartMouse.y
        ))
    }

    override func mouseUp(with event: NSEvent) {
        dragStartMouse = nil
        dragStartOrigin = nil
        onDragEnd?()
    }
}

class StopRecordingPanel: NSPanel {
    override var canBecomeKey: Bool { true }

    convenience init(
        captureManager: CaptureManager,
        onPauseResume: @escaping () -> Void,
        onRestart: @escaping () -> Void,
        onDiscard: @escaping () -> Void,
        onStop: @escaping () -> Void
    ) {
        self.init(
            contentRect: NSRect(x: 0, y: 0, width: 500, height: 44),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        self.isReleasedWhenClosed = false
        self.level = .floating
        self.isOpaque = false
        self.backgroundColor = .clear
        self.hasShadow = true
        self.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        self.isMovableByWindowBackground = true

        let hostingView = NSHostingView(rootView: StopRecordingView(
            captureManager: captureManager,
            onPauseResume: onPauseResume,
            onRestart: onRestart,
            onDiscard: onDiscard,
            onStop: onStop
        ))
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
        makeKeyAndOrderFront(nil)
        NSApp.activate()
    }
}

private struct StopRecordingView: View {
    @Environment(\.colorScheme) private var colorScheme

    @ObservedObject var captureManager: CaptureManager
    let onPauseResume: () -> Void
    let onRestart: () -> Void
    let onDiscard: () -> Void
    let onStop: () -> Void
    @State private var elapsed: TimeInterval = 0
    @State private var startDate = Date()
    @State private var pauseStartedAt: Date?

    private let timer = Timer.publish(every: 1, on: .main, in: .common).autoconnect()

    var body: some View {
        HStack(spacing: 10) {
            Circle()
                .fill(.red)
                .frame(width: 10, height: 10)

            Text(formattedTime)
                .monospacedDigit()
                .foregroundStyle(.primary)
                .font(.system(size: 13, weight: .medium))
                .accessibilityLabel("Elapsed recording time")
                .accessibilityValue(formattedTime)

            if captureManager.recordingSystemAudioEnabled {
                audioControlButton(
                    title: captureManager.isRecordingSystemAudioMuted ? "Unmute system audio" : "Mute system audio",
                    systemName: captureManager.isRecordingSystemAudioMuted ? "speaker.slash.fill" : "speaker.wave.2.fill",
                    isMuted: captureManager.isRecordingSystemAudioMuted,
                    action: captureManager.toggleRecordingSystemAudioMute
                )
            }

            if captureManager.recordingMicrophoneEnabled {
                audioControlButton(
                    title: captureManager.isRecordingMicrophoneMuted ? "Unmute microphone" : "Mute microphone",
                    systemName: captureManager.isRecordingMicrophoneMuted ? "mic.slash.fill" : "mic.fill",
                    isMuted: captureManager.isRecordingMicrophoneMuted,
                    action: captureManager.toggleRecordingMicrophoneMute
                )
                .help(captureManager.microphoneWarningMessage ?? captureManager.activeMicrophoneName ?? "Microphone is being recorded.")
            }

            controlButton(
                title: captureManager.isRecordingPaused ? "Resume" : "Pause",
                systemName: captureManager.isRecordingPaused ? "play.fill" : "pause.fill",
                tint: .blue,
                action: onPauseResume
            )
            .keyboardShortcut("p", modifiers: [])
            .accessibilityHint(captureManager.isRecordingPaused ? "Resumes the current recording." : "Pauses the current recording.")

            controlButton(title: "Restart", systemName: "arrow.clockwise", tint: .orange, action: onRestart)
                .keyboardShortcut("r", modifiers: [])
                .accessibilityHint("Discards this take and immediately starts a new recording with the same target.")

            controlButton(title: "Discard", systemName: "trash.fill", tint: .gray, action: onDiscard)
                .keyboardShortcut(.delete, modifiers: [])
                .accessibilityHint("Deletes the partial recording and exits.")

            Button(action: onStop) {
                Image(systemName: "stop.fill")
                    .foregroundStyle(.white)
                    .font(.system(size: 12))
                    .frame(width: 28, height: 28)
                    .background(.red)
                    .clipShape(RoundedRectangle(cornerRadius: 6))
            }
            .buttonStyle(.plain)
            .keyboardShortcut(".", modifiers: .command)
            .accessibilityLabel("Stop recording")
            .accessibilityHint("Stops the current recording.")
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .background {
            RoundedRectangle(cornerRadius: 10)
                .fill(colorScheme == .dark ? Color.black.opacity(0.8) : Color.white.opacity(0.9))
                .shadow(color: .black.opacity(0.15), radius: 8, x: 0, y: 2)
                .overlay {
                    RoundedRectangle(cornerRadius: 10)
                        .strokeBorder(.primary.opacity(0.15), lineWidth: 0.5)
                }
        }
        .onReceive(timer) { _ in
            guard !captureManager.isRecordingPaused else { return }
            elapsed = Date().timeIntervalSince(startDate)
        }
        .onChange(of: captureManager.isRecordingPaused) { _, paused in
            if paused {
                pauseStartedAt = Date()
            } else if let pauseStartedAt {
                startDate = startDate.addingTimeInterval(Date().timeIntervalSince(pauseStartedAt))
                self.pauseStartedAt = nil
            }
        }
    }

    private var formattedTime: String {
        let minutes = Int(elapsed) / 60
        let seconds = Int(elapsed) % 60
        return String(format: "%d:%02d", minutes, seconds)
    }

    private func controlButton(title: String, systemName: String, tint: Color, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: systemName)
                .foregroundStyle(.white)
                .font(.system(size: 12))
                .frame(width: 28, height: 28)
                .background(tint)
                .clipShape(RoundedRectangle(cornerRadius: 6))
        }
        .buttonStyle(.plain)
        .accessibilityLabel(title)
        .help(title)
    }

    private func audioControlButton(title: String, systemName: String, isMuted: Bool, action: @escaping () -> Void) -> some View {
        SwiftUI.Button(action: { action() }, label: {
            Image(systemName: systemName)
                .foregroundStyle(isMuted ? Color.primary.opacity(0.55) : Color.white)
                .font(.system(size: 12))
                .frame(width: 28, height: 28)
                .background(isMuted ? Color.primary.opacity(0.12) : Color.blue)
                .clipShape(RoundedRectangle(cornerRadius: 6))
        })
        .buttonStyle(.plain)
        .accessibilityLabel(title)
        .accessibilityValue(isMuted ? "Muted" : "Recording")
        .help(title)
    }
}
