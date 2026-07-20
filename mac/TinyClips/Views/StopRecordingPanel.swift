import AppKit
import AVFoundation
import SwiftUI

final class WebcamPreviewPanel: NSPanel {
    private let captureFrame: CGRect
    private let onCornerChange: (String) -> Void
    private var corner: String
    private let margin: CGFloat = 16

    init(
        session: AVCaptureSession,
        selection: StartRecordingPanel.WebcamSelection,
        region: CaptureRegion,
        onCornerChange: @escaping (String) -> Void
    ) {
        let captureFrame = Self.captureFrame(for: region)
        let previewSize = Self.previewSize(for: selection, in: captureFrame)
        self.captureFrame = captureFrame
        self.corner = selection.corner
        self.onCornerChange = onCornerChange

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
        corner = Self.nearestCorner(
            for: CGPoint(x: frame.midX, y: frame.midY),
            in: captureFrame
        )
        snap(to: corner)
        onCornerChange(corner)
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
        in captureFrame: CGRect
    ) -> CGSize {
        let scale: CGFloat
        switch selection.size.lowercased() {
        case "small": scale = 0.18
        case "large": scale = 0.30
        default: scale = 0.24
        }
        let minDimension = min(captureFrame.width, captureFrame.height)
        let width = min(max(120, minDimension * scale), max(80, minDimension - 32))
        return CGSize(
            width: width,
            height: selection.shape.lowercased() == "circle" ? width : width * 9 / 16
        )
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
            contentRect: NSRect(x: 0, y: 0, width: 430, height: 44),
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

            if captureManager.recordingMicrophoneEnabled {
                RecordingStatusIcon(
                    systemName: "mic.fill",
                    tint: captureManager.microphoneWarningMessage == nil ? .green : .yellow,
                    accessibilityLabel: "Microphone recording",
                    accessibilityValue: captureManager.microphoneWarningMessage ?? (captureManager.activeMicrophoneName ?? "Active")
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
}

private struct RecordingStatusIcon: View {
    let systemName: String
    let tint: Color
    let accessibilityLabel: String
    let accessibilityValue: String

    var body: some View {
        Image(systemName: systemName)
            .font(.system(size: 12, weight: .semibold))
            .foregroundStyle(tint)
            .frame(width: 24, height: 24)
            .background(.primary.opacity(0.08))
            .clipShape(RoundedRectangle(cornerRadius: 6))
            .accessibilityLabel(accessibilityLabel)
            .accessibilityValue(accessibilityValue)
    }
}
