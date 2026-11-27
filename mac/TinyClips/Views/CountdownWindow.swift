import AppKit
import SwiftUI

class CountdownWindow: NSPanel {
    private var completion: (() -> Void)?
    private var onCancel: (() -> Void)?
    private var localEscapeMonitor: Any?
    private var globalEscapeMonitor: Any?
    private var countdownTimer: Timer?
    private let countdownState: CountdownState
    private var remaining: Int
    private var didComplete = false

    init(duration: Int, onCancel: (() -> Void)? = nil, completion: @escaping () -> Void) {
        let initialRemaining = max(1, duration)
        self.remaining = initialRemaining
        self.countdownState = CountdownState(remaining: initialRemaining)
        self.onCancel = onCancel
        self.completion = completion
        let size = NSSize(width: 120, height: onCancel == nil ? 120 : 152)
        super.init(
            contentRect: NSRect(origin: .zero, size: size),
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

        let hostingView = NSHostingView(rootView: CountdownView(
            state: countdownState,
            cancel: onCancel == nil ? nil : { [weak self] in self?.requestCancel() }
        ))
        hostingView.frame = NSRect(origin: .zero, size: size)
        self.contentView = hostingView
        updateDisplay()
    }

    func show() {
        if let screen = NSScreen.main {
            let x = screen.frame.midX - frame.width / 2
            let y = screen.frame.midY - frame.height / 2
            setFrameOrigin(NSPoint(x: x, y: y))
        }
        alphaValue = 0
        orderFront(nil)
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.18
            animator().alphaValue = 1
        }
        startCountdown()
        if onCancel != nil {
            localEscapeMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
                guard event.keyCode == 53 else { return event }
                self?.requestCancel()
                return nil
            }
            globalEscapeMonitor = NSEvent.addGlobalMonitorForEvents(matching: .keyDown) { [weak self] event in
                if event.keyCode == 53 { self?.requestCancel() }
            }
        }
    }

    private func startCountdown() {
        countdownTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] timer in
            guard let self else { timer.invalidate(); return }
            self.remaining -= 1
            if self.remaining <= 0 {
                timer.invalidate()
                self.countdownTimer = nil
                NSAnimationContext.runAnimationGroup { context in
                    context.duration = 0.16
                    self.animator().alphaValue = 0
                } completionHandler: {
                    guard !self.didComplete else { return }
                    self.didComplete = true
                    let completion = self.completion
                    self.completion = nil
                    self.onCancel = nil
                    self.removeEscapeMonitors()
                    self.orderOut(nil)
                    completion?()
                }
            } else {
                self.updateDisplay()
            }
        }
    }

    private func updateDisplay() {
        countdownState.remaining = remaining
    }

    private func requestCancel() {
        guard !didComplete, let callback = onCancel else { return }
        cancel()
        callback()
    }

    private func removeEscapeMonitors() {
        if let localEscapeMonitor { NSEvent.removeMonitor(localEscapeMonitor) }
        if let globalEscapeMonitor { NSEvent.removeMonitor(globalEscapeMonitor) }
        localEscapeMonitor = nil
        globalEscapeMonitor = nil
    }

    func cancel() {
        didComplete = true
        countdownTimer?.invalidate()
        countdownTimer = nil
        completion = nil
        onCancel = nil
        removeEscapeMonitors()
        orderOut(nil)
    }
}

private final class CountdownState: ObservableObject {
    @Published var remaining: Int

    init(remaining: Int) {
        self.remaining = remaining
    }
}

private struct CountdownView: View {
    @ObservedObject var state: CountdownState
    var cancel: (() -> Void)?

    var body: some View {
        let remaining = state.remaining

        VStack(spacing: 0) {
            countdown(remaining)
            if let cancel {
                Button("Cancel", action: cancel)
                    .keyboardShortcut(.cancelAction)
                    .accessibilityLabel("Cancel segment recording")
                    .frame(height: 32)
            }
        }
    }

    private func countdown(_ remaining: Int) -> some View {
        ZStack {
            Circle()
                .fill(.black.opacity(0.75))
                .frame(width: 100, height: 100)
                .overlay {
                    Circle()
                        .strokeBorder(.white.opacity(0.3), lineWidth: 2)
                }

            Text("\(remaining)")
                .font(.system(size: 48, weight: .bold, design: .rounded))
                .foregroundStyle(.white)
                .contentTransition(.numericText())
                .scaleEffect(remaining == 1 ? 1.25 : 1)
                .opacity(remaining == 1 ? 1 : 0.92)
                .animation(.spring(response: 0.28, dampingFraction: 0.72), value: remaining)
                .accessibilityLabel("Countdown")
                .accessibilityValue("\(remaining) seconds")
        }
        .frame(width: 120, height: 120)
        .transition(.opacity.combined(with: .scale(scale: 0.92)))
    }
}
