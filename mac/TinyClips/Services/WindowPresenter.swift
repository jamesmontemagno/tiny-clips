import AppKit
import Combine
import SwiftUI

struct UserWindowPresentationObserver: NSViewRepresentable {
    final class ObserverView: NSView {
        private var observers: [AnyCancellable] = []

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            observers.removeAll()
            guard let window else { return }
            _ = WindowPresenter.shared
            for name in [NSWindow.didBecomeKeyNotification, NSWindow.didBecomeMainNotification] {
                observers.append(NotificationCenter.default.publisher(for: name, object: window).sink { [weak window] _ in
                    MainActor.assumeIsolated {
                        Self.registerVisibleWindow(window)
                    }
                })
            }
            Self.registerVisibleWindow(window)
        }

        private static func registerVisibleWindow(_ window: NSWindow?) {
            // SwiftUI can briefly attach content to a window that it never presents.
            DispatchQueue.main.async { [weak window] in
                guard let window, window.isVisible else { return }
                TinyClipsActivationPolicy.beginPresentation(of: window)
            }
        }
    }

    func makeNSView(context: Context) -> ObserverView { ObserverView() }
    func updateNSView(_ nsView: ObserverView, context: Context) {}
}

@MainActor
protocol WindowPresentationTarget: AnyObject {
    var isVisible: Bool { get }
    var isMiniaturized: Bool { get }
    var isKeyWindow: Bool { get }
    var hasAttachedSheet: Bool { get }

    func prepareForPresentation()
    func deminiaturize(_ sender: Any?)
    func makeKeyAndOrderFront(_ sender: Any?)
    func orderFrontRegardless()
}

extension NSWindow: WindowPresentationTarget {
    var hasAttachedSheet: Bool {
        attachedSheet != nil || NSApp.modalWindow != nil
    }

    func prepareForPresentation() {
        collectionBehavior.insert(.moveToActiveSpace)
    }
}

@MainActor
final class WindowPresenter {
    typealias RetryScheduler = (TimeInterval, @escaping @MainActor () -> Void) -> Void

    static let shared: WindowPresenter = {
        let presenter = WindowPresenter(
            processIdentifier: ProcessInfo.processInfo.processIdentifier,
            activate: {
                if NSApp.isHidden {
                    NSApp.unhide(nil)
                }
                // Menu-bar apps need explicit activation when another app has focus.
                NSApp.activate(ignoringOtherApps: true)
            },
            isApplicationActive: { NSApp.isActive },
            hasKeyWindow: { NSApp.keyWindow != nil },
            frontmostProcessIdentifier: { NSWorkspace.shared.frontmostApplication?.processIdentifier },
            scheduleRetry: { delay, action in
                DispatchQueue.main.asyncAfter(deadline: .now() + delay) {
                    action()
                }
            }
        )
        presenter.resignObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didResignActiveNotification,
            object: nil,
            queue: .main
        ) { [weak presenter] _ in
            MainActor.assumeIsolated {
                presenter?.applicationDidResignActive()
            }
        }
        presenter.workspaceObserver = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didActivateApplicationNotification,
            object: nil,
            queue: .main
        ) { [weak presenter] notification in
            guard let application = notification.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication else {
                return
            }
            let processIdentifier = application.processIdentifier
            MainActor.assumeIsolated {
                presenter?.frontmostApplicationDidChange(to: processIdentifier)
            }
        }
        presenter.closeObserver = NotificationCenter.default.addObserver(
            forName: NSWindow.willCloseNotification,
            object: nil,
            queue: .main
        ) { notification in
            guard let window = notification.object as? NSWindow else { return }
            MainActor.assumeIsolated {
                TinyClipsActivationPolicy.endPresentation(of: window)
            }
        }
        return presenter
    }()

    private struct Attempt {
        let id: UUID
        let originalProcessIdentifier: pid_t?
        var wasFrontmost: Bool
    }

    private let processIdentifier: pid_t
    private let activate: () -> Void
    private let isApplicationActive: () -> Bool
    private let hasKeyWindow: () -> Bool
    private let frontmostProcessIdentifier: () -> pid_t?
    private let scheduleRetry: RetryScheduler
    private var pendingAttempt: Attempt?
    private var resignObserver: NSObjectProtocol?
    private var workspaceObserver: NSObjectProtocol?
    private var closeObserver: NSObjectProtocol?

    init(
        processIdentifier: pid_t,
        activate: @escaping () -> Void,
        isApplicationActive: @escaping () -> Bool,
        hasKeyWindow: @escaping () -> Bool,
        frontmostProcessIdentifier: @escaping () -> pid_t?,
        scheduleRetry: @escaping RetryScheduler
    ) {
        self.processIdentifier = processIdentifier
        self.activate = activate
        self.isApplicationActive = isApplicationActive
        self.hasKeyWindow = hasKeyWindow
        self.frontmostProcessIdentifier = frontmostProcessIdentifier
        self.scheduleRetry = scheduleRetry
    }

    func present(_ window: any WindowPresentationTarget) {
        let attempt = Attempt(
            id: UUID(),
            originalProcessIdentifier: frontmostProcessIdentifier(),
            wasFrontmost: isApplicationActive()
        )
        pendingAttempt = attempt

        if let nsWindow = window as? NSWindow {
            TinyClipsActivationPolicy.beginPresentation(of: nsWindow)
        }
        window.prepareForPresentation()
        activate()
        if window.isMiniaturized {
            window.deminiaturize(nil)
        }
        window.makeKeyAndOrderFront(nil)
        window.orderFrontRegardless()

        scheduleRetry(0.1) { [weak self, weak window] in
            guard let self, let pending = self.pendingAttempt, pending.id == attempt.id else { return }
            self.pendingAttempt = nil
            guard let window, window.isVisible, !window.isMiniaturized else { return }

            if self.isApplicationActive() {
                guard !window.isKeyWindow else { return }
            } else {
                guard let original = pending.originalProcessIdentifier,
                      self.frontmostProcessIdentifier() == original else { return }
            }

            self.activate()
            window.makeKeyAndOrderFront(nil)
            window.orderFrontRegardless()
        }
    }

    func applicationDidResignActive() {
        pendingAttempt = nil
    }

    func frontmostApplicationDidChange(to processIdentifier: pid_t) {
        guard var pending = pendingAttempt else { return }
        if processIdentifier == self.processIdentifier {
            pending.wasFrontmost = true
            pendingAttempt = pending
        } else if pending.wasFrontmost || processIdentifier != pending.originalProcessIdentifier {
            pendingAttempt = nil
        }
    }
}
