import AppKit
import Combine
import Foundation

/// Opens SwiftUI windows (Settings, Clips Library) for automation. The menu bar label installs
/// the opener because it is the one view that always exists.
@MainActor
final class AutomationWindowOpener {
    static let shared = AutomationWindowOpener()
    private var opener: ((String) -> Void)?

    func install(_ opener: @escaping (String) -> Void) {
        self.opener = opener
    }

    func open(id: String, title: String) -> Bool {
        guard let opener else { return false }
        opener(id)
        for delay in [0.0, 0.1] {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) {
                NSRunningApplication.current.activate(options: [.activateAllWindows])
                if let window = NSApp.windows.first(where: { $0.identifier?.rawValue == id || $0.title == title }) {
                    window.makeKeyAndOrderFront(nil)
                    window.orderFrontRegardless()
                }
            }
        }
        return true
    }
}

/// Owns agent control: the socket server, paired clients, pairing, approvals, and the audit log.
@MainActor
final class AutomationController: ObservableObject {
    static let shared = AutomationController()
    nonisolated static let enabledKey = "automationEnabled"

    @Published private(set) var isEnabled = false
    @Published private(set) var isRunning = false
    @Published private(set) var lastError: String?
    @Published private(set) var clients: [AutomationClient] = []

    let contract: AutomationContract?
    let clientStore: AutomationClientStore
    let auditLog: AutomationAuditLog
    let approvals: AutomationApprovalCenter
    private var pairings = AutomationPairings()
    private var server: AutomationServer?
    private var defaultsObserver: NSKeyValueObservation?
    private(set) weak var captureManager: CaptureManager?
    private lazy var router = AutomationCommandRouter(controller: self)

    var endpointPath: String { AutomationEndpoint.socketPath }

    init(
        contract: AutomationContract? = .bundled,
        clientStore: AutomationClientStore = AutomationClientStore(),
        auditLog: AutomationAuditLog = AutomationAuditLog(),
        approvals: AutomationApprovalCenter? = nil
    ) {
        self.contract = contract
        self.clientStore = clientStore
        self.auditLog = auditLog
        self.approvals = approvals ?? AutomationApprovalCenter()
        clients = clientStore.clients
    }

    /// Called once at launch. Starts the server when the setting is on and follows later changes.
    func activate(captureManager: CaptureManager) {
        self.captureManager = captureManager
        if defaultsObserver == nil {
            // KVO also reports changes made outside the app, such as `defaults write`.
            defaultsObserver = UserDefaults.standard.observe(\.automationEnabled, options: [.new]) { _, _ in
                DispatchQueue.main.async {
                    MainActor.assumeIsolated {
                        AutomationController.shared.syncWithSetting()
                    }
                }
            }
        }
        syncWithSetting()
    }

    func setEnabled(_ enabled: Bool) {
        UserDefaults.standard.set(enabled, forKey: Self.enabledKey)
        syncWithSetting()
    }

    func shutdown() {
        stopServer()
    }

    func revoke(_ client: AutomationClient) {
        clientStore.revoke(id: client.id)
        approvals.deny(clientID: client.id)
        clients = clientStore.clients
        auditLog.record(client: client.name, command: "settings.revokeClient", outcome: "ok")
    }

    /// Changes a paired agent's scopes from Settings. The agent keeps its token.
    func updateScopes(_ client: AutomationClient, scopes: [String]) {
        do {
            try clientStore.updateScopes(id: client.id, scopes: scopes)
            auditLog.record(
                client: client.name,
                command: "settings.updateClientScopes",
                outcome: "ok",
                detail: clientStore.clients.first { $0.id == client.id }?.scopes.joined(separator: ",")
            )
        } catch {
            auditLog.record(client: client.name, command: "settings.updateClientScopes", outcome: "error", detail: "\(error)")
        }
        clients = clientStore.clients
    }

    func syncWithSetting() {
        let enabled = UserDefaults.standard.bool(forKey: Self.enabledKey)
        let turnedOn = enabled && !isEnabled
        if isEnabled != enabled { isEnabled = enabled }
        // After a failed start, only retry when the setting is switched on again or via retryStart().
        if enabled, server == nil, captureManager != nil, turnedOn || lastError == nil {
            startServer()
        } else if !enabled {
            if server != nil { stopServer() }
            lastError = nil
            approvals.denyAll()
        }
    }

    func retryStart() {
        guard isEnabled, server == nil, captureManager != nil else { return }
        startServer()
    }

    private func startServer() {
        let server = AutomationServer(path: endpointPath) { line in
            await AutomationController.shared.handle(line: line)
        }
        do {
            try server.start()
            self.server = server
            isRunning = true
            lastError = nil
            auditLog.record(client: nil, command: "server.start", outcome: "ok", detail: endpointPath)
        } catch {
            isRunning = false
            lastError = (error as? AutomationError)?.message ?? error.localizedDescription
            auditLog.record(client: nil, command: "server.start", outcome: "internal", detail: lastError)
        }
    }

    private func stopServer() {
        guard let server else { return }
        server.stop()
        self.server = nil
        isRunning = false
        auditLog.record(client: nil, command: "server.stop", outcome: "ok", synchronously: true)
    }

    // MARK: - Requests

    func handle(line: Data) async -> Data {
        guard let object = try? JSONSerialization.jsonObject(with: line),
              let request = object as? [String: Any] else {
            return AutomationResponse.failure(id: nil, error: AutomationError(.invalidParams, "Each line must be one JSON object."))
        }
        let id = request["id"].flatMap { $0 is String || $0 is NSNumber ? $0 : nil }
        guard let command = request["command"] as? String, !command.isEmpty else {
            return AutomationResponse.failure(id: id, error: AutomationError(.invalidParams, "The request has no command."))
        }
        let params: [String: Any]
        switch request["params"] {
        case nil, is NSNull: params = [:]
        case let value as [String: Any]: params = value
        default:
            return AutomationResponse.failure(id: id, error: AutomationError(.invalidParams, "params must be an object."))
        }

        let client = clientStore.client(forToken: request["token"] as? String)
        do {
            let result = try await execute(command: command, params: params, client: client)
            if let client { clientStore.markUsed(id: client.id) }
            auditLog.record(client: client?.name, command: command, outcome: "ok", detail: Self.auditDetail(params))
            return AutomationResponse.success(id: id, result: result)
        } catch let error as AutomationError {
            auditLog.record(client: client?.name, command: command, outcome: error.code.rawValue, detail: Self.auditDetail(params))
            return AutomationResponse.failure(id: id, error: error)
        } catch {
            auditLog.record(client: client?.name, command: command, outcome: "internal", detail: error.localizedDescription)
            return AutomationResponse.failure(id: id, error: AutomationError(.internal, error.localizedDescription))
        }
    }

    private func execute(command: String, params: [String: Any], client: AutomationClient?) async throws -> [String: Any] {
        guard let contract else { throw AutomationError(.internal, "The automation contract is missing from the app.") }
        let decision = AutomationPolicy.decide(
            enabled: isEnabled && isRunning,
            clientScopes: client.map { Set($0.scopes) },
            command: command,
            contract: contract
        )
        switch decision {
        case .deny(let code):
            throw AutomationError(code)
        case .approve:
            let approved = await approvals.request(
                approvalRequest(command: command, params: params, client: client, contract: contract),
                clientID: client?.id
            )
            try Task.checkCancellation()
            guard isEnabled else { throw AutomationError(.automationDisabled) }
            try requireCurrentClient(client)
            guard approved else { throw AutomationError(.approvalDenied) }
            // Scopes can be edited in Settings while an approval prompt is open.
            let currentScopes = client.flatMap { current in clientStore.clients.first { $0.id == current.id } }.map { Set($0.scopes) }
            if case .deny(let code) = AutomationPolicy.decide(
                enabled: true, clientScopes: currentScopes, command: command, contract: contract
            ) {
                throw AutomationError(code)
            }
        case .allow:
            break
        }

        try Task.checkCancellation()
        try requireCurrentClient(client)
        switch command {
        case "session.hello": return hello(contract: contract)
        case "session.pair": return try pair(params: params, contract: contract)
        case "session.pairStatus": return try pairStatus(params: params)
        default: return try await router.execute(command: command, params: params)
        }
    }

    private func requireCurrentClient(_ client: AutomationClient?) throws {
        guard let client else { return }
        guard clientStore.isCurrent(client) else {
            throw AutomationError(.unauthorized)
        }
    }

    private func approvalRequest(
        command: String,
        params: [String: Any],
        client: AutomationClient?,
        contract: AutomationContract
    ) -> AutomationApprovalCenter.Request {
        let name = client?.name ?? "An agent"
        let description = contract.command(named: command)?.description ?? command
        var details = ["Command: \(command)"]
        if let path = params["path"] as? String { details.append(path) }
        let themeID = command == "editor.themes.delete" ? params["id"] as? String : nil
        if let themeID {
            details.append("Theme: \(EditorThemeStore.shared.theme(id: themeID)?.name ?? themeID)")
        }
        let allowTitle: String
        switch contract.command(named: command)?.scope {
        case "delete": allowTitle = themeID != nil ? "Delete Theme" : "Move to Trash"
        case "upload": allowTitle = "Upload"
        case "discard": allowTitle = "Discard"
        default: allowTitle = "Allow"
        }
        return .init(
            title: "Allow “\(name)” to run \(command)?",
            message: description,
            details: details,
            allowTitle: allowTitle
        )
    }

    private func hello(contract: AutomationContract) -> [String: Any] {
        let supported = contract.commands.map(\.name).filter { AutomationCommandRouter.supportedCommands.contains($0) }
        return [
            "app": "TinyClips",
            "version": Self.appVersion,
            "platform": "macos",
            "build": Self.buildFlavor,
            "protocolVersion": contract.protocolVersion,
            "automationEnabled": isEnabled && isRunning,
            "commands": contract.sessionCommands + supported
        ]
    }

    private func pair(params: [String: Any], contract: AutomationContract) throws -> [String: Any] {
        let name = (params["clientName"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard !name.isEmpty, name.count <= 100 else {
            throw AutomationError(.invalidParams, "clientName must be 1 to 100 characters.")
        }
        guard let scopes = params["scopes"] as? [String], !scopes.isEmpty,
              scopes.allSatisfy({ contract.scopes[$0] != nil }) else {
            throw AutomationError(.invalidParams, "scopes must list one or more of: \(contract.scopes.keys.sorted().joined(separator: ", ")).")
        }
        let pairing = try pairings.begin(clientName: name, scopes: scopes)
        let selection = AutomationScopeSelection(contract: contract, requested: pairing.scopes)
        let request = AutomationApprovalCenter.Request(
            title: "Allow “\(name)” to control Tiny Clips?",
            message: "An agent on this Mac asks to pair with Tiny Clips. Its requested permissions are checked; change them if you like. Delete, upload, and discard still ask you each time. You can change permissions or revoke access in Settings → Agent Control.",
            details: [],
            allowTitle: "Pair",
            scopeSelection: selection
        )
        let pairingID = pairing.id
        Task { @MainActor [weak self] in
            guard let self else { return }
            let answered = await self.approvals.request(request) && self.isEnabled
            let granted = answered ? selection.grantedScopes : []
            let approved = !granted.isEmpty
            do {
                try self.pairings.resolve(id: pairingID, approved: approved, scopes: granted, store: self.clientStore)
            } catch {
                try? self.pairings.resolve(id: pairingID, approved: false, store: self.clientStore)
            }
            self.clients = self.clientStore.clients
            self.auditLog.record(
                client: name,
                command: "session.pair",
                outcome: approved ? "approved" : "denied",
                detail: approved ? granted.joined(separator: ",") : nil
            )
        }
        return ["pairingId": pairingID]
    }

    private func pairStatus(params: [String: Any]) throws -> [String: Any] {
        guard let id = params["pairingId"] as? String else {
            throw AutomationError(.invalidParams, "pairingId is required.")
        }
        guard let pairing = pairings.status(id: id) else {
            throw AutomationError(.notFound, "No pairing request with that id. Tokens are returned only once.")
        }
        var result: [String: Any] = ["status": pairing.status.rawValue]
        if pairing.status == .approved, let token = pairing.token {
            result["token"] = token
            result["scopes"] = pairing.scopes
        }
        return result
    }

    private static func auditDetail(_ params: [String: Any]) -> String? {
        if let path = params["path"] as? String { return path }
        if let key = params["key"] as? String { return key }
        if let mode = params["mode"] as? String { return mode }
        if let window = params["window"] as? String { return window }
        if let id = params["id"] as? String { return id }
        return nil
    }

    static var appVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "Unknown"
    }

    static var buildFlavor: String {
#if APPSTORE
        return "store"
#else
        return "direct"
#endif
    }
}

extension UserDefaults {
    /// Backs KVO for `AutomationController.enabledKey`.
    @objc dynamic var automationEnabled: Bool {
        bool(forKey: "automationEnabled")
    }
}
