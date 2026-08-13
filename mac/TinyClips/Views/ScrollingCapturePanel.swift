import AppKit
import SwiftUI

@MainActor
final class ScrollingCapturePanel: NSPanel {
    private var didComplete = false
    private var localMonitor: Any?
    private var globalMonitor: Any?
    private let onStop: () -> Void
    private let onCancel: () -> Void

    init(onStop: @escaping () -> Void, onCancel: @escaping () -> Void) {
        self.onStop = onStop
        self.onCancel = onCancel
        super.init(
            contentRect: NSRect(x: 0, y: 0, width: 280, height: 88),
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
        contentView = NSHostingView(rootView: ScrollingCapturePanelView(
            onStop: { [weak self] in self?.finish(stop: true) },
            onCancel: { [weak self] in self?.finish(stop: false) }
        ))
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

    func dismiss() {
        removeMonitors()
        orderOut(nil)
    }

    private func finish(stop: Bool) {
        guard !didComplete else { return }
        didComplete = true
        removeMonitors()
        orderOut(nil)
        if stop {
            onStop()
        } else {
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
    let onStop: () -> Void
    let onCancel: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            ProgressView()
                .controlSize(.small)
            VStack(alignment: .leading, spacing: 2) {
                Text("Scrolling capture")
                    .font(.system(size: 13, weight: .semibold))
                Text("Scroll normally, then press Return to finish")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
            Button("Stop", action: onStop)
                .keyboardShortcut(.defaultAction)
                .accessibilityHint("Stops scrolling capture and stitches the frames.")
            Button("Cancel", action: onCancel)
                .keyboardShortcut(.cancelAction)
                .accessibilityHint("Cancels without saving a partial capture.")
        }
        .padding(12)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 10))
        .shadow(radius: 8)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Scrolling capture in progress")
    }
}
