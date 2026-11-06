import XCTest
@testable import TinyClips

final class AutomationPolicyTests: XCTestCase {
    private struct PolicyFixture: Decodable {
        struct Case: Decodable {
            var name: String
            var enabled: Bool
            var scopes: [String]?
            var command: String
            var decision: String
        }
        var cases: [Case]
    }

    private static var repoRoot: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
    }

    private func loadContract() throws -> AutomationContract {
        try AutomationContract.load(from: Self.repoRoot.appendingPathComponent("shared/automation/commands.json"))
    }

    func testSharedPolicyFixture() throws {
        let contract = try loadContract()
        let data = try Data(contentsOf: Self.repoRoot.appendingPathComponent("shared/automation/fixtures/policy.json"))
        let fixture = try JSONDecoder().decode(PolicyFixture.self, from: data)
        XCTAssertFalse(fixture.cases.isEmpty)

        for testCase in fixture.cases {
            let decision = AutomationPolicy.decide(
                enabled: testCase.enabled,
                clientScopes: testCase.scopes.map(Set.init),
                command: testCase.command,
                contract: contract
            )
            XCTAssertEqual(decision.fixtureValue, testCase.decision, testCase.name)
        }
    }

    func testEveryCommandHasAKnownScope() throws {
        let contract = try loadContract()
        XCTAssertFalse(contract.commands.isEmpty)
        for command in contract.commands {
            XCTAssertNotNil(contract.scopes[command.scope], "\(command.name) uses unknown scope \(command.scope)")
        }
        for scope in contract.sensitiveScopes {
            XCTAssertNotNil(contract.scopes[scope], "Sensitive scope \(scope) is not a contract scope")
        }
        XCTAssertEqual(Set(contract.commands.map(\.name)).count, contract.commands.count, "Duplicate command names")
    }

    func testBundledContractMatchesSharedFile() throws {
        let bundled = try XCTUnwrap(AutomationContract.bundled, "commands.json is missing from the app bundle")
        let shared = try loadContract()
        XCTAssertEqual(bundled.commands.map(\.name), shared.commands.map(\.name))
        XCTAssertEqual(bundled.protocolVersion, shared.protocolVersion)
    }

    @MainActor
    func testRouterOnlyClaimsContractCommands() throws {
        let names = Set(try loadContract().commands.map(\.name))
        XCTAssertTrue(AutomationCommandRouter.supportedCommands.isSubset(of: names))
    }

    @MainActor
    func testStudioAutomationCommandsAreDeclaredWithLeastPrivilegeScopes() throws {
        let contract = try loadContract()
        let expected = [
            "studio.projects.list": "read",
            "studio.project.get": "read",
            "studio.project.edit": "edit",
            "studio.zoom.suggest": "read",
            "studio.export.options": "read",
            "editor.trim": "edit",
            "editor.saveFrame": "edit",
            "studio.export": "export"
        ]
        for (name, scope) in expected {
            XCTAssertEqual(contract.commands.first(where: { $0.name == name })?.scope, scope)
            XCTAssertTrue(AutomationCommandRouter.supportedCommands.contains(name))
        }
    }

    func testErrorCodesMatchContract() throws {
        let contract = try loadContract()
        for code in AutomationErrorCode.allCases {
            XCTAssertNotNil(contract.errors[code.rawValue], "\(code.rawValue) is not a contract error")
        }
    }

    func testJSONValueParsing() {
        XCTAssertEqual(AutomationValue.bool(true), true)
        XCTAssertNil(AutomationValue.bool(1))
        XCTAssertEqual(AutomationValue.int(30), 30)
        XCTAssertNil(AutomationValue.int(true))
        XCTAssertNil(AutomationValue.int(1.5))
        XCTAssertEqual(AutomationValue.double(0.5), 0.5)
    }
}
