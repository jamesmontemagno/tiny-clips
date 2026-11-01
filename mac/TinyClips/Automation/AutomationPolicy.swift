import Foundation

enum AutomationDecision: Equatable {
    case allow
    /// Allowed only after the user approves it in the app.
    case approve
    case deny(AutomationErrorCode)

    /// The decision as written in `shared/automation/fixtures/policy.json`.
    var fixtureValue: String {
        switch self {
        case .allow: return "allow"
        case .approve: return "approve"
        case .deny(let code): return code.rawValue
        }
    }
}

/// Decides whether a command may run. Follows the "Policy order" in docs/agent-integration.md.
enum AutomationPolicy {
    static let helloCommand = "session.hello"
    static let pairingCommands: Set<String> = ["session.pair", "session.pairStatus"]

    /// - Parameter clientScopes: the scopes granted to the calling client, or `nil` when the
    ///   request has no token or the token is unknown.
    static func decide(
        enabled: Bool,
        clientScopes: Set<String>?,
        command: String,
        contract: AutomationContract
    ) -> AutomationDecision {
        if command == helloCommand { return .allow }
        guard enabled else { return .deny(.automationDisabled) }
        if pairingCommands.contains(command) { return .allow }
        guard let clientScopes else { return .deny(.unauthorized) }
        guard let definition = contract.command(named: command) else { return .deny(.unknownCommand) }
        guard clientScopes.contains(definition.scope) else { return .deny(.missingScope) }
        return contract.isSensitive(scope: definition.scope) ? .approve : .allow
    }
}
