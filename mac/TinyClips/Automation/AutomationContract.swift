import Foundation

/// The shared automation contract (`shared/automation/commands.json`), bundled as a resource.
struct AutomationContract: Decodable {
    struct Param: Decodable {
        var type: String?
        /// True only for top-level `"required": true`. Object params carry a JSON Schema
        /// `required` array of property names instead, which does not make the param required.
        var required: Bool?
        var `enum`: [String]?

        private enum CodingKeys: String, CodingKey {
            case type, required, `enum`
        }

        init(type: String? = nil, required: Bool? = nil, enum values: [String]? = nil) {
            self.type = type
            self.required = required
            self.enum = values
        }

        init(from decoder: Decoder) throws {
            let container = try decoder.container(keyedBy: CodingKeys.self)
            type = try container.decodeIfPresent(String.self, forKey: .type)
            required = try? container.decodeIfPresent(Bool.self, forKey: .required)
            `enum` = try? container.decodeIfPresent([String].self, forKey: .enum)
        }
    }

    struct Command: Decodable {
        var name: String
        var scope: String
        var description: String
        var params: [String: Param]
    }

    var protocolVersion: Int
    var scopes: [String: String]
    var sensitiveScopes: [String]
    var sessionCommands: [String]
    var commands: [Command]
    var errors: [String: String]

    static let resourceName = "commands"

    static func load(from url: URL) throws -> AutomationContract {
        try JSONDecoder().decode(AutomationContract.self, from: Data(contentsOf: url))
    }

    /// The contract shipped inside the app bundle, or `nil` if the resource is missing.
    static let bundled: AutomationContract? = {
        guard let url = Bundle.main.url(forResource: resourceName, withExtension: "json") else { return nil }
        return try? load(from: url)
    }()

    func command(named name: String) -> Command? {
        commands.first { $0.name == name }
    }

    func isSensitive(scope: String) -> Bool {
        sensitiveScopes.contains(scope)
    }
}

/// Error codes from the contract's `errors` table.
enum AutomationErrorCode: String, Error, CaseIterable {
    case automationDisabled = "automation_disabled"
    case unauthorized
    case missingScope = "missing_scope"
    case approvalDenied = "approval_denied"
    case unknownCommand = "unknown_command"
    case notSupported = "not_supported"
    case invalidParams = "invalid_params"
    case busy
    case permissionRequired = "permission_required"
    case proRequired = "pro_required"
    case notFound = "not_found"
    case `internal`
}

/// A failure returned to an automation client.
struct AutomationError: Error, Equatable {
    var code: AutomationErrorCode
    var message: String

    init(_ code: AutomationErrorCode, _ message: String? = nil) {
        self.code = code
        self.message = message ?? AutomationContract.bundled?.errors[code.rawValue] ?? code.rawValue
    }
}
