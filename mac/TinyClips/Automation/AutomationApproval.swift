import AppKit
import SwiftUI

@MainActor
protocol AutomationApprovalPresenting: AnyObject {
    func present()
    func dismiss()
}

/// The permissions a user grants while approving a pairing. Starts with the agent's requested scopes.
@MainActor
final class AutomationScopeSelection: ObservableObject {
    struct Option: Identifiable {
        let name: String
        let description: String
        let isSensitive: Bool
        var id: String { name }
    }

    let options: [Option]
    @Published var selected: Set<String>

    init(contract: AutomationContract, requested: [String]) {
        options = contract.displayScopes.map {
            Option(name: $0, description: contract.scopes[$0] ?? "", isSensitive: contract.isSensitive(scope: $0))
        }
        selected = Set(requested).intersection(contract.scopes.keys)
    }

    /// The selected scopes in display order.
    var grantedScopes: [String] {
        options.map(\.name).filter(selected.contains)
    }

    func binding(for name: String) -> Binding<Bool> {
        Binding(
            get: { self.selected.contains(name) },
            set: { isOn in
                if isOn { self.selected.insert(name) } else { self.selected.remove(name) }
            }
        )
    }
}

/// Asks the user, inside the app, to allow an automation request. One prompt shows at a time;
/// a request without an answer within `timeout` counts as denied.
@MainActor
final class AutomationApprovalCenter {
    struct Request {
        var title: String
        var message: String
        var details: [String]
        var allowTitle: String
        /// When set, the prompt lists every scope as a toggle and Allow needs at least one selected.
        var scopeSelection: AutomationScopeSelection? = nil
    }

    static let timeout: TimeInterval = 120
    static let maximumQueued = 10

    typealias PanelFactory = @MainActor (Request, @escaping (Bool) -> Void) -> any AutomationApprovalPresenting

    private struct Entry {
        var id: UUID
        var clientID: String?
        var request: Request
        var continuation: CheckedContinuation<Bool, Never>
    }

    private var queue: [Entry] = []
    private var current: Entry?
    private var panel: (any AutomationApprovalPresenting)?
    private let makePanel: PanelFactory

    init(makePanel: PanelFactory? = nil) {
        self.makePanel = makePanel ?? { AutomationApprovalPanel(request: $0, onComplete: $1) }
    }

    func request(_ request: Request, clientID: String? = nil) async -> Bool {
        guard !Task.isCancelled, queue.count < Self.maximumQueued else { return false }
        let id = UUID()
        return await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                guard !Task.isCancelled else {
                    continuation.resume(returning: false)
                    return
                }
                queue.append(Entry(id: id, clientID: clientID, request: request, continuation: continuation))
                Task { @MainActor [weak self] in
                    try? await Task.sleep(nanoseconds: UInt64(Self.timeout * 1_000_000_000))
                    self?.finish(id: id, approved: false)
                }
                showNextIfNeeded()
            }
        } onCancel: { [weak self] in
            Task { @MainActor in self?.finish(id: id, approved: false) }
        }
    }

    /// Denies the current and queued requests belonging to a revoked client.
    func deny(clientID: String) {
        let pending = queue.filter { $0.clientID == clientID }.map(\.id)
            + (current.flatMap { $0.clientID == clientID ? [$0.id] : nil } ?? [])
        for id in pending {
            finish(id: id, approved: false)
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
        let panel = makePanel(entry.request) { [weak self] approved in
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
final class AutomationApprovalPanel: NSPanel, NSWindowDelegate, AutomationApprovalPresenting {
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
                    if let selection = request.scopeSelection {
                        AutomationScopeToggles(selection: selection)
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
                if let selection = request.scopeSelection {
                    ScopeAwareAllowButton(selection: selection, title: request.allowTitle, action: onAllow)
                } else {
                    Button(request.allowTitle, action: onAllow)
                        .accessibilityHint("Lets the agent continue.")
                }
            }
        }
        .padding(20)
        .frame(width: 420)
        .accessibilityElement(children: .contain)
    }
}

/// Toggles for every scope; the agent's requested scopes start on.
struct AutomationScopeToggles: View {
    @ObservedObject var selection: AutomationScopeSelection

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Permissions")
                .font(.subheadline.weight(.semibold))
                .accessibilityAddTraits(.isHeader)
            ForEach(selection.options) { option in
                Toggle(isOn: selection.binding(for: option.name)) {
                    VStack(alignment: .leading, spacing: 1) {
                        Text(option.isSensitive ? "\(option.name) (asks every time)" : option.name)
                        Text(option.description)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                .toggleStyle(.checkbox)
                .accessibilityLabel("\(option.name): \(option.description)")
            }
        }
        .padding(.top, 4)
    }
}

private struct ScopeAwareAllowButton: View {
    @ObservedObject var selection: AutomationScopeSelection
    let title: String
    let action: () -> Void

    var body: some View {
        Button(title, action: action)
            .disabled(selection.selected.isEmpty)
            .accessibilityHint(selection.selected.isEmpty
                ? "Select at least one permission to pair."
                : "Pairs the agent with the selected permissions.")
    }
}
