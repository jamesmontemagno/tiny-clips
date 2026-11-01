import AppKit
import SwiftUI

/// Asks the user, inside the app, to allow an automation request. One prompt shows at a time;
/// a request without an answer within `timeout` counts as denied.
@MainActor
final class AutomationApprovalCenter {
    struct Request {
        var title: String
        var message: String
        var details: [String]
        var allowTitle: String
    }

    static let timeout: TimeInterval = 120
    static let maximumQueued = 10

    private struct Entry {
        var id: UUID
        var request: Request
        var continuation: CheckedContinuation<Bool, Never>
    }

    private var queue: [Entry] = []
    private var current: Entry?
    private var panel: AutomationApprovalPanel?

    func request(_ request: Request) async -> Bool {
        guard queue.count < Self.maximumQueued else { return false }
        let id = UUID()
        return await withCheckedContinuation { continuation in
            queue.append(Entry(id: id, request: request, continuation: continuation))
            Task { @MainActor [weak self] in
                try? await Task.sleep(nanoseconds: UInt64(Self.timeout * 1_000_000_000))
                self?.finish(id: id, approved: false)
            }
            showNextIfNeeded()
        }
    }

    /// Denies everything waiting, for example when agent control is turned off.
    func denyAll() {
        let pending = queue.map(\.id) + (current.map { [$0.id] } ?? [])
        for id in pending {
            finish(id: id, approved: false)
        }
    }

    private func showNextIfNeeded() {
        guard current == nil, !queue.isEmpty else { return }
        let entry = queue.removeFirst()
        current = entry
        let id = entry.id
        let panel = AutomationApprovalPanel(request: entry.request) { [weak self] approved in
            self?.finish(id: id, approved: approved)
        }
        self.panel = panel
        panel.present()
    }

    private func finish(id: UUID, approved: Bool) {
        if let index = queue.firstIndex(where: { $0.id == id }) {
            let entry = queue.remove(at: index)
            entry.continuation.resume(returning: false)
            return
        }
        guard let entry = current, entry.id == id else { return }
        current = nil
        panel?.dismiss()
        panel = nil
        entry.continuation.resume(returning: approved)
        showNextIfNeeded()
    }
}

/// A small floating window with the request and Allow / Deny buttons.
final class AutomationApprovalPanel: NSPanel, NSWindowDelegate {
    private var onComplete: ((Bool) -> Void)?
    private var didComplete = false
    private let request: AutomationApprovalCenter.Request

    init(request: AutomationApprovalCenter.Request, onComplete: @escaping (Bool) -> Void) {
        self.request = request
        self.onComplete = onComplete
        super.init(
            contentRect: NSRect(x: 0, y: 0, width: 420, height: 220),
            styleMask: [.titled, .closable],
            backing: .buffered,
            defer: false
        )
        isReleasedWhenClosed = false
        level = .floating
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        title = "Tiny Clips Agent Control"
        delegate = self
        let view = AutomationApprovalView(
            request: request,
            onAllow: { [weak self] in self?.complete(true) },
            onDeny: { [weak self] in self?.complete(false) }
        )
        let hosting = NSHostingView(rootView: view)
        contentView = hosting
        setContentSize(hosting.fittingSize)
        center()
    }

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }

    func present() {
        NSApp.activate(ignoringOtherApps: true)
        makeKeyAndOrderFront(nil)
        orderFrontRegardless()
        NSAccessibility.post(
            element: NSApp as Any,
            notification: .announcementRequested,
            userInfo: [
                .announcement: "\(request.title) \(request.message)",
                .priority: NSAccessibilityPriorityLevel.high.rawValue
            ]
        )
    }

    func dismiss() {
        onComplete = nil
        didComplete = true
        orderOut(nil)
        close()
    }

    private func complete(_ approved: Bool) {
        guard !didComplete, let callback = onComplete else { return }
        didComplete = true
        onComplete = nil
        callback(approved)
        orderOut(nil)
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        complete(false)
        return true
    }

    override func cancelOperation(_ sender: Any?) {
        complete(false)
    }
}

private struct AutomationApprovalView: View {
    let request: AutomationApprovalCenter.Request
    let onAllow: () -> Void
    let onDeny: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .top, spacing: 12) {
                Image(systemName: "exclamationmark.shield")
                    .font(.system(size: 28))
                    .foregroundStyle(.orange)
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 6) {
                    Text(request.title)
                        .font(.headline)
                        .fixedSize(horizontal: false, vertical: true)
                        .accessibilityAddTraits(.isHeader)
                    Text(request.message)
                        .fixedSize(horizontal: false, vertical: true)
                    ForEach(request.details, id: \.self) { detail in
                        Text(detail)
                            .font(.callout.monospaced())
                            .foregroundStyle(.secondary)
                            .textSelection(.enabled)
                            .lineLimit(3)
                            .truncationMode(.middle)
                    }
                    Text("No answer within 2 minutes denies the request.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            HStack {
                Spacer()
                Button("Deny", role: .cancel, action: onDeny)
                    .keyboardShortcut(.cancelAction)
                    .accessibilityHint("Refuses this agent request.")
                Button(request.allowTitle, action: onAllow)
                    .keyboardShortcut(.defaultAction)
                    .accessibilityHint("Lets the agent continue.")
            }
        }
        .padding(20)
        .frame(width: 420)
        .accessibilityElement(children: .contain)
    }
}
