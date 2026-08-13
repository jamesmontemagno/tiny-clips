import AppKit
import SwiftUI

@MainActor
final class ScrollingCaptureState: ObservableObject {
    @Published var frameCount = 0
    @Published var statusMessage: String?
    @Published var isFinishing = false
}

@MainActor
final class ScrollingCapturePanel: NSPanel {
    private var didComplete = false
    private var localMonitor: Any?
    private var globalMonitor: Any?
    private let onStop: () -> Void
    private let onCancel: () -> Void
    private let state = ScrollingCaptureState()

    init(onStop: @escaping () -> Void, onCancel: @escaping () -> Void) {
        self.onStop = onStop
        self.onCancel = onCancel
        super.init(
            contentRect: NSRect(x: 0, y: 0, width: 100, height: 44),
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
        isMovableByWindowBackground = true

        let hostingView = NSHostingView(rootView: ScrollingCapturePanelView(
            state: state,
            onStop: { [weak self] in self?.finish(stop: true) },
            onCancel: { [weak self] in self?.finish(stop: false) }
        ))
        setContentSize(hostingView.fittingSize)
        contentView = hostingView
    }

    override var canBecomeKey: Bool { true }

    func show(at position: NSPoint? = nil) {
        if let position {
            setFrameOrigin(position)
        } else {
            guard let screen = NSScreen.screens.first(where: { $0.frame.contains(NSEvent.mouseLocation) }) ?? NSScreen.main else {
                return
            }
            setFrameOrigin(NSPoint(x: screen.visibleFrame.midX - frame.width / 2, y: screen.visibleFrame.minY + 32))
        }
        makeKeyAndOrderFront(nil)
        NSApp.activate()
        installMonitors()
    }

    func updateFrameCount(_ count: Int) {
        state.frameCount = count
    }

    func showStatus(_ message: String) {
        state.statusMessage = message
    }

    func markCompleted() {
        guard !didComplete else { return }
        didComplete = true
        state.isFinishing = true
        removeMonitors()
    }

    func dismiss() {
        removeMonitors()
        orderOut(nil)
    }

    private func finish(stop: Bool) {
        guard !didComplete else { return }
        if stop {
            markCompleted()
            onStop()
        } else {
            didComplete = true
            removeMonitors()
            orderOut(nil)
            onCancel()
        }
    }

    private func installMonitors() {
        localMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self else { return event }
            if event.keyCode == 53 {
                self.finish(stop: false)
                return nil
            }
            if event.keyCode == 36 || event.keyCode == 76 {
                self.finish(stop: true)
                return nil
            }
            return event
        }
        globalMonitor = NSEvent.addGlobalMonitorForEvents(matching: .keyDown) { [weak self] event in
            if event.keyCode == 53 {
                self?.finish(stop: false)
            } else if event.keyCode == 36 || event.keyCode == 76 {
                self?.finish(stop: true)
            }
        }
    }

    private func removeMonitors() {
        if let localMonitor {
            NSEvent.removeMonitor(localMonitor)
            self.localMonitor = nil
        }
        if let globalMonitor {
            NSEvent.removeMonitor(globalMonitor)
            self.globalMonitor = nil
        }
    }
}

private struct ScrollingCapturePanelView: View {
    @Environment(\.colorScheme) private var colorScheme
    @ObservedObject var state: ScrollingCaptureState
    let onStop: () -> Void
    let onCancel: () -> Void

    @State private var isPulsing = false

    private var frameLabel: String {
        state.frameCount == 1 ? "1 frame" : "\(state.frameCount) frames"
    }

    private var statusText: String {
        state.statusMessage ?? (state.isFinishing ? "Saving..." : "Scroll the page, then press Return")
    }

    var body: some View {
        HStack(spacing: 8) {
            HStack(spacing: 4) {
                Image(systemName: "arrow.down.doc")
                    .font(.system(size: 11))
                Text("Scrolling")
                    .font(.system(size: 12, weight: .semibold))
            }
            .foregroundStyle(.secondary)
            .accessibilityElement(children: .combine)
            .accessibilityLabel("Scrolling capture mode")

            Divider()
                .frame(height: 20)
                .overlay(.primary.opacity(0.2))

            HStack(spacing: 6) {
                Circle()
                    .fill(.red)
                    .frame(width: 8, height: 8)
                    .opacity(isPulsing ? 0.35 : 1)
                    .animation(.easeInOut(duration: 0.8).repeatForever(autoreverses: true), value: isPulsing)
                Text(frameLabel)
                    .font(.system(size: 13, weight: .medium))
                    .monospacedDigit()
                    .foregroundStyle(.primary)
            }
            .accessibilityElement(children: .combine)
            .accessibilityLabel("Captured frames")
            .accessibilityValue(frameLabel)

            Text(statusText)
                .font(.system(size: 12))
                .foregroundStyle(state.statusMessage == nil ? .secondary : Color.orange)
                .lineLimit(1)
                .truncationMode(.tail)
                .frame(width: 260, alignment: .leading)
                .accessibilityLabel(state.statusMessage ?? (state.isFinishing ? "Saving scrolling capture." : "Scroll the page, then press Return to finish."))

            Divider()
                .frame(height: 20)
                .overlay(.primary.opacity(0.2))

            Button(action: onStop) {
                HStack(spacing: 5) {
                    Image(systemName: "checkmark")
                        .font(.system(size: 12))
                    Text("Done")
                        .font(.system(size: 13, weight: .medium))
                }
                .foregroundStyle(.white)
                .padding(.horizontal, 12)
                .padding(.vertical, 7)
                .background(.blue.opacity(0.8))
                .clipShape(RoundedRectangle(cornerRadius: 6))
            }
            .buttonStyle(.plain)
            .help("Finish scrolling capture (Return)")
            .keyboardShortcut(.defaultAction)
            .disabled(state.isFinishing)
            .opacity(state.isFinishing ? 0.45 : 1)
            .accessibilityLabel("Finish scrolling capture")
            .accessibilityHint(state.isFinishing ? "Saving captured frames." : "Stops scrolling capture and stitches the captured frames.")

            Button(action: onCancel) {
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
            .disabled(state.isFinishing)
            .opacity(state.isFinishing ? 0.45 : 1)
            .accessibilityLabel("Cancel scrolling capture")
            .accessibilityHint(state.isFinishing ? "Saving captured frames." : "Discards the capture without saving.")
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
        .onAppear { isPulsing = true }
        .accessibilityElement(children: .contain)
        .accessibilityLabel(state.isFinishing ? "Scrolling capture saving" : "Scrolling capture in progress")
    }
}
