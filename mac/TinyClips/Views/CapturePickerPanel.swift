import AppKit
import SwiftUI

// MARK: - Capture Picker Mode

enum CapturePickerMode {
    case region
    case screen
    case window
    case scrolling
}

@MainActor
private final class CapturePickerState: ObservableObject {
    @Published var countdownEnabled: Bool
    @Published var countdownDuration: Int
    @Published var videoTimeLimitMinutes: Int

    init(countdownEnabled: Bool, countdownDuration: Int, videoTimeLimitMinutes: Int) {
        self.countdownEnabled = countdownEnabled
        self.countdownDuration = countdownDuration
        self.videoTimeLimitMinutes = videoTimeLimitMinutes
    }
}


// MARK: - Panel

@MainActor
class CapturePickerPanel: NSPanel {
    private var didComplete = false
    private var localMonitor: Any?
    private var globalMonitor: Any?
    private var onCapture: ((CapturePickerMode, Bool, Int, Int) -> Void)?
    private var onCancel: (() -> Void)?
    private let state: CapturePickerState
    private var captureType: CaptureType = .screenshot

    override var canBecomeKey: Bool { true }

    override init(contentRect: NSRect, styleMask style: NSWindow.StyleMask, backing bufferingType: NSWindow.BackingStoreType, defer flag: Bool) {
        self.state = CapturePickerState(countdownEnabled: false, countdownDuration: 0, videoTimeLimitMinutes: 0)
        super.init(contentRect: contentRect, styleMask: style, backing: bufferingType, defer: flag)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    convenience init(
        captureType: CaptureType = .screenshot,
        countdownEnabled: Bool,
        countdownDuration: Int,
        videoTimeLimitMinutes: Int = 0,
        onCapture: @escaping (CapturePickerMode, Bool, Int, Int) -> Void,
        onCancel: @escaping () -> Void
    ) {
        self.init(
            contentRect: NSRect(x: 0, y: 0, width: 100, height: 44),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        self.state.countdownEnabled = countdownEnabled
        self.state.countdownDuration = countdownDuration
        self.state.videoTimeLimitMinutes = videoTimeLimitMinutes
        self.captureType = captureType
        self.onCapture = onCapture
        self.onCancel = onCancel
        self.isReleasedWhenClosed = false
        self.level = .floating
        self.isOpaque = false
        self.backgroundColor = .clear
        self.hasShadow = true
        self.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        self.isMovableByWindowBackground = true

        let hostingView = NSHostingView(rootView: CapturePickerView(
            captureType: captureType,
            state: state,
            onCapture: { [weak self] mode, enabled, duration, timeLimitMinutes in
                self?.finishCapture(
                    mode: mode,
                    countdownEnabled: enabled,
                    countdownDuration: duration,
                    videoTimeLimitMinutes: timeLimitMinutes
                )
            },
            onCancel: { [weak self] in
                self?.finishCancel()
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
        makeKeyAndOrderFront(nil)
        NSApp.activate()
        installKeyboardMonitors()
    }

    func dismiss() {
        removeKeyboardMonitors()
        orderOut(nil)
    }

    override func keyDown(with event: NSEvent) {
        if handleKeyDown(event) {
            return
        }
        super.keyDown(with: event)
    }

    private func installKeyboardMonitors() {
        removeKeyboardMonitors()

        localMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self else { return event }
            if self.handleKeyDown(event) {
                return nil
            }
            return event
        }

        globalMonitor = NSEvent.addGlobalMonitorForEvents(matching: .keyDown) { [weak self] event in
            _ = self?.handleKeyDown(event)
        }
    }

    private func removeKeyboardMonitors() {
        if let localMonitor {
            NSEvent.removeMonitor(localMonitor)
            self.localMonitor = nil
        }

        if let globalMonitor {
            NSEvent.removeMonitor(globalMonitor)
            self.globalMonitor = nil
        }
    }

    private func handleKeyDown(_ event: NSEvent) -> Bool {
        guard !didComplete else { return true }

        if event.keyCode == 53 {
            finishCancel()
            return true
        }

        guard let key = event.charactersIgnoringModifiers?.lowercased() else {
            return false
        }

        switch key {
        case "r":
            finishCapture(
                mode: .region,
                countdownEnabled: state.countdownEnabled,
                countdownDuration: state.countdownDuration,
                videoTimeLimitMinutes: state.videoTimeLimitMinutes
            )
            return true
        case "s":
            finishCapture(
                mode: .screen,
                countdownEnabled: state.countdownEnabled,
                countdownDuration: state.countdownDuration,
                videoTimeLimitMinutes: state.videoTimeLimitMinutes
            )
            return true
        case "w":
            finishCapture(
                mode: .window,
                countdownEnabled: state.countdownEnabled,
                countdownDuration: state.countdownDuration,
                videoTimeLimitMinutes: state.videoTimeLimitMinutes
            )
            return true
        case "p":
            if captureType == .screenshot {
                finishCapture(
                    mode: .scrolling,
                    countdownEnabled: false,
                    countdownDuration: 0,
                    videoTimeLimitMinutes: 0
                )
                return true
            }
            return false
        default:
            return false
        }
    }

    private func finishCapture(
        mode: CapturePickerMode,
        countdownEnabled: Bool,
        countdownDuration: Int,
        videoTimeLimitMinutes: Int
    ) {
        guard !didComplete else { return }
        didComplete = true
        removeKeyboardMonitors()
        orderOut(nil)
        onCapture?(mode, countdownEnabled, countdownDuration, videoTimeLimitMinutes)
        onCapture = nil
        onCancel = nil
    }

    private func finishCancel() {
        guard !didComplete else { return }
        didComplete = true
        removeKeyboardMonitors()
        orderOut(nil)
        onCancel?()
        onCapture = nil
        onCancel = nil
    }
}

// MARK: - View

private struct CapturePickerView: View {
    @Environment(\.colorScheme) private var colorScheme
    let captureType: CaptureType
    @ObservedObject var state: CapturePickerState

    let onCapture: (CapturePickerMode, Bool, Int, Int) -> Void
    let onCancel: () -> Void

    init(
        captureType: CaptureType,
        state: CapturePickerState,
        onCapture: @escaping (CapturePickerMode, Bool, Int, Int) -> Void,
        onCancel: @escaping () -> Void
    ) {
        self.captureType = captureType
        self.state = state
        self.onCapture = onCapture
        self.onCancel = onCancel
    }

    private var timerLabel: String {
        state.countdownEnabled ? "\(state.countdownDuration)s" : "Off"
    }

    private var videoTimeLimitLabel: String {
        state.videoTimeLimitMinutes == 0 ? "Unlimited" : "\(state.videoTimeLimitMinutes)m"
    }

    private var modeIcon: String {
        switch captureType {
        case .screenshot: return "camera.fill"
        case .video: return "video.fill"
        case .gif: return "photo.stack"
        }
    }

    private var modeLabel: String {
        switch captureType {
        case .screenshot: return "Screenshot"
        case .video: return "Video"
        case .gif: return "GIF"
        }
    }

    var body: some View {
        HStack(spacing: 8) {
            HStack(spacing: 4) {
                Image(systemName: modeIcon)
                    .font(.system(size: 11))
                Text(modeLabel)
                    .font(.system(size: 12, weight: .semibold))
            }
            .foregroundStyle(.secondary)
            .accessibilityElement(children: .combine)
            .accessibilityLabel("\(modeLabel) mode")

            Divider()
                .frame(height: 20)
                .overlay(.primary.opacity(0.2))
            Button { onCapture(.region, state.countdownEnabled, state.countdownDuration, state.videoTimeLimitMinutes) } label: {
                HStack(spacing: 5) {
                    Image(systemName: "viewfinder.rectangular")
                        .font(.system(size: 12))
                    Text("Region")
                        .font(.system(size: 13, weight: .medium))
                }
                .foregroundStyle(.white)
                .padding(.horizontal, 12)
                .padding(.vertical, 7)
                .background(.blue.opacity(0.8))
                .clipShape(RoundedRectangle(cornerRadius: 6))
            }
            .buttonStyle(.plain)
            .help("Select a region (R)")
            .keyboardShortcut("r", modifiers: [])
            .accessibilityHint("Starts region capture.")

            Button { onCapture(.screen, state.countdownEnabled, state.countdownDuration, state.videoTimeLimitMinutes) } label: {
                HStack(spacing: 5) {
                    Image(systemName: "display")
                        .font(.system(size: 12))
                    Text("Screen")
                        .font(.system(size: 13, weight: .medium))
                }
                .foregroundStyle(.primary)
                .padding(.horizontal, 12)
                .padding(.vertical, 7)
                .background(.primary.opacity(0.1))
                .clipShape(RoundedRectangle(cornerRadius: 6))
            }
            .buttonStyle(.plain)
            .help("Full screen (S)")
            .keyboardShortcut("s", modifiers: [])
            .accessibilityHint("Starts full screen capture.")

            Button { onCapture(.window, state.countdownEnabled, state.countdownDuration, state.videoTimeLimitMinutes) } label: {
                HStack(spacing: 5) {
                    Image(systemName: "macwindow")
                        .font(.system(size: 12))
                    Text("Window")
                        .font(.system(size: 13, weight: .medium))
                }
                .foregroundStyle(.primary)
                .padding(.horizontal, 12)
                .padding(.vertical, 7)
                .background(.primary.opacity(0.1))
                .clipShape(RoundedRectangle(cornerRadius: 6))
            }
            .buttonStyle(.plain)
            .help("Select a window (W)")
            .keyboardShortcut("w", modifiers: [])
            .accessibilityHint("Starts window capture.")

            if captureType == .screenshot {
                Button { onCapture(.scrolling, false, 0, 0) } label: {
                    HStack(spacing: 5) {
                        Image(systemName: "arrow.down.doc")
                            .font(.system(size: 12))
                        Text("Scroll")
                            .font(.system(size: 13, weight: .medium))
                    }
                    .foregroundStyle(.primary)
                    .padding(.horizontal, 12)
                    .padding(.vertical, 7)
                    .background(.primary.opacity(0.1))
                    .clipShape(RoundedRectangle(cornerRadius: 6))
                }
                .buttonStyle(.plain)
                .help("Scrolling capture (P)")
                .keyboardShortcut("p", modifiers: [])
                .accessibilityLabel("Scrolling capture")
                .accessibilityHint("Select a region and stitch a long page while you scroll.")
            }

            Divider()
                .frame(height: 20)
                .overlay(.primary.opacity(0.2))

            Menu {
                Button("Off") {
                    state.countdownEnabled = false
                }
                Divider()
                ForEach([1, 2, 3, 5, 10], id: \.self) { seconds in
                    Button("\(seconds)s") {
                        state.countdownEnabled = true
                        state.countdownDuration = seconds
                    }
                }
            } label: {
                HStack(spacing: 4) {
                    Image(systemName: "timer")
                        .font(.system(size: 11))
                    Text(timerLabel)
                        .font(.system(size: 12, weight: .medium))
                    Image(systemName: "chevron.down")
                        .font(.system(size: 9))
                }
                .foregroundStyle(.primary)
                .padding(.horizontal, 8)
                .padding(.vertical, 6)
                .background(.primary.opacity(0.08))
                .clipShape(RoundedRectangle(cornerRadius: 6))
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .help("Countdown timer")
            .accessibilityLabel("Countdown timer")
            .accessibilityValue(state.countdownEnabled ? "\(state.countdownDuration) seconds" : "Off")
            .accessibilityHint("Choose a delay before capture starts.")

            if captureType == .video {
                Menu {
                    Button("Unlimited") {
                        state.videoTimeLimitMinutes = 0
                    }
                    Divider()
                    ForEach([1, 3, 5, 10, 15, 30, 45, 60], id: \.self) { minutes in
                        Button("\(minutes) minute\(minutes == 1 ? "" : "s")") {
                            state.videoTimeLimitMinutes = minutes
                        }
                    }
                } label: {
                    HStack(spacing: 4) {
                        Image(systemName: "hourglass")
                            .font(.system(size: 11))
                        Text(videoTimeLimitLabel)
                            .font(.system(size: 12, weight: .medium))
                        Image(systemName: "chevron.down")
                            .font(.system(size: 9))
                    }
                    .foregroundStyle(.primary)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 6)
                    .background(.primary.opacity(0.08))
                    .clipShape(RoundedRectangle(cornerRadius: 6))
                }
                .menuStyle(.borderlessButton)
                .fixedSize()
                .help("Auto-stop recording time limit")
                .accessibilityLabel("Recording time limit")
                .accessibilityValue(videoTimeLimitLabel)
                .accessibilityHint("Choose when recording should automatically stop.")
            }

            Button { onCancel() } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(.primary.opacity(0.5))
                    .frame(width: 24, height: 24)
                    .background(.primary.opacity(0.08))
                    .clipShape(RoundedRectangle(cornerRadius: 6))
            }
            .buttonStyle(.plain)
            .help("Cancel (Esc)")
            .keyboardShortcut(.cancelAction)
            .accessibilityLabel("Cancel capture")
            .accessibilityHint("Closes the capture picker.")
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .fixedSize()
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
}
