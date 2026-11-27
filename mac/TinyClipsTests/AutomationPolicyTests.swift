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
            "studio.project.open": "edit",
            "studio.project.openFolder": "read",
            "studio.project.delete": "delete",
            "studio.project.get": "read",
            "studio.project.saveCopy": "export",
            "studio.project.edit": "edit",
            "studio.zoom.suggest": "read",
            "studio.export.options": "read",
            "studio.captions.check": "read",
            "studio.captions.import": "edit",
            "studio.captions.fix": "edit",
            "studio.captions.export": "export",
            "studio.captions.models": "read",
            "studio.captions.generate": "edit",
            "editor.trim": "edit",
            "editor.saveFrame": "edit",
            "studio.export": "export"
        ]
        for (name, scope) in expected {
            XCTAssertEqual(contract.commands.first(where: { $0.name == name })?.scope, scope)
            // These two are only advertised on a Mac that can generate captions.
            if !["studio.captions.models", "studio.captions.generate"].contains(name) {
                XCTAssertTrue(AutomationCommandRouter.supportedCommands.contains(name))
            }
        }
        let projectList = try XCTUnwrap(contract.commands.first(where: { $0.name == "studio.projects.list" }))
        XCTAssertTrue(projectList.description.contains("projectId and isReadable"))
        XCTAssertTrue(projectList.description.contains("studio.project.delete"))
    }

    @MainActor
    func testStudioPreviewSwitchIsNotAnAgentSetting() {
        XCTAssertNil(AutomationSetting.named("studioPreviewEnabled"))
    }

    func testRecordingIntoTimelineRequiresBothGrants() throws {
        let contract = try loadContract()
        for scope in ["edit", "record"] {
            XCTAssertEqual(AutomationPolicy.decide(enabled: true, clientScopes: [scope],
                                                   command: "studio.timeline.record", contract: contract), .deny(.missingScope))
        }
        XCTAssertEqual(AutomationPolicy.decide(enabled: true, clientScopes: ["edit", "record"],
                                               command: "studio.timeline.record", contract: contract), .allow)
    }

    @MainActor
    func testTimelineCommitRechecksRevisionAndAuthorization() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let fixture = Self.repoRoot.appendingPathComponent("shared/studio/fixtures/timeline/two-clips-native-flat.json")
        let project = try StudioJSON.makeDecoder().decode(StudioProject.self, from: Data(contentsOf: fixture))
        let store = StudioProjectStore(rootURL: root, now: { Date(timeIntervalSince1970: 42) })
        try store.save(project)
        let controller = AutomationController(contract: try loadContract())
        let router = AutomationCommandRouter(controller: controller, studioStore: store)
        let get = try await router.execute(command: "studio.timeline.get", params: ["projectId": project.id])
        XCTAssertEqual(get["duration"] as? Double, 11)
        let params: [String: Any] = ["projectId": project.id, "clipId": "flat-clip", "index": 0,
                                    "expectedModifiedAt": try XCTUnwrap(get["modifiedAt"] as? String),
                                    "expectedRevision": try XCTUnwrap(get["revision"] as? String)]
        do {
            _ = try await router.execute(command: "studio.timeline.move", params: params) {
                throw AutomationError(.missingScope)
            }
            XCTFail("An edit survived revocation.")
        } catch let error as AutomationError {
            XCTAssertEqual(error.code, .missingScope)
        }
        do {
            _ = try await router.execute(command: "studio.timeline.move", params: params) {
                var concurrent = try store.load(id: project.id)
                concurrent.canvas.padding = 0.123
                try store.save(concurrent)
            }
            XCTFail("An edit survived a same-timestamp concurrent write.")
        } catch let error as AutomationError {
            XCTAssertEqual(error.code, .conflict)
        }
        let kept = try store.load(id: project.id)
        XCTAssertEqual(kept.canvas.padding, 0.123)
        XCTAssertEqual(kept.timeline, project.timeline)
        let fresh = try await router.execute(command: "studio.timeline.get", params: ["projectId": project.id])
        var insert = params
        insert["expectedRevision"] = fresh["revision"]
        insert["index"] = nil
        insert["projectTime"] = 2.0
        _ = try await router.execute(command: "studio.timeline.insert", params: insert)
        let saved = try store.load(id: project.id)
        XCTAssertEqual(saved.timeline?.clips.map(\.sourceId), ["native-source", "flat-source", "native-source", "flat-source"])
        XCTAssertEqual(saved.mediaSources, project.mediaSources)
    }

    @MainActor
    func testTimelineImportRejectsOutsideFilesAndLinks() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let library = root.appendingPathComponent("Library")
        try FileManager.default.createDirectory(at: library, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let external = root.appendingPathComponent("Outside.mp4")
        try Data([0]).write(to: external)
        func allowed(_ url: URL, recent: [String] = []) throws -> URL {
            try AutomationCommandRouter.studioImportURL(path: url.path, directories: [library], recentPaths: recent)
        }
        XCTAssertThrowsError(try allowed(external))
        XCTAssertEqual(try allowed(external, recent: [external.path]), external.resolvingSymlinksInPath())
        let link = library.appendingPathComponent("Link.mp4")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: external)
        XCTAssertThrowsError(try allowed(link, recent: [link.path]))
    }

    @MainActor
    func testFinishedTakeRechecksDestinationAfterStagingAndKeepsItsDraft() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = StudioProjectStore(rootURL: root, now: { Date(timeIntervalSince1970: 42) })
        func makeTake() throws -> StudioProject {
            let project = StudioProject(id: UUID().uuidString.lowercased(), sources: StudioSources(
                screen: StudioScreenSource(width: 64, height: 48, frameRate: 24, duration: 1)
            ))
            try store.save(project)
            try FileManager.default.copyItem(
                at: Self.repoRoot.appendingPathComponent("mac/TinyClipsTests/Fixtures/StudioRendering/native.mp4"),
                to: store.paths(for: project).screenURL
            )
            return try store.load(id: project.id)
        }
        let destination = try makeTake(), take = try makeTake()
        let controller = AutomationController(contract: try loadContract())
        let router = AutomationCommandRouter(controller: controller, studioStore: store)
        func request() async throws -> [String: Any] {
            let get = try await router.execute(command: "studio.timeline.get", params: ["projectId": destination.id])
            return ["projectId": destination.id, "expectedModifiedAt": try XCTUnwrap(get["modifiedAt"] as? String),
                    "expectedRevision": try XCTUnwrap(get["revision"] as? String), "projectTime": 0.5]
        }
        let original = try await request()
        let directory = try store.paths(for: destination).projectDirectory
        let before = try FileManager.default.contentsOfDirectory(atPath: directory.path).sorted()
        var checks = 0
        do {
            _ = try await router.attachStudioTake(take.id, params: original) {
                checks += 1
                if checks == 2 {
                    var concurrent = destination
                    concurrent.name = "Renamed while preparing the take"
                    try store.save(concurrent)
                }
            }
            XCTFail("A stale recording was attached.")
        } catch let error as AutomationError {
            XCTAssertEqual(error.code, .conflict)
        }
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: directory.path).sorted(), before)
        XCTAssertNil(try store.load(id: destination.id).timeline)
        XCTAssertEqual(try store.load(id: take.id), take)
        let fresh = try await request()
        let saved = try await router.attachStudioTake(take.id, params: fresh, validateAccess: {})
        XCTAssertEqual(saved.timeline?.clips.count, 3)
        XCTAssertEqual(saved.timeline?.clips.first?.sourceEnd, 0.5)
        XCTAssertEqual(saved.timeline?.clips.last?.sourceStart, 0.5)
        XCTAssertEqual(saved.mediaSources?.count, 2)
        XCTAssertEqual(try store.load(id: take.id), take)
    }

    @MainActor
    func testScreenshotOutputSettingsRejectDisablingTheLastOutput() throws {
        let suite = "TinyClipsAutomationTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let settings = CaptureSettings(defaults: defaults)
        XCTAssertTrue(settings.setScreenshotOutput(.saveToDisk, enabled: false))
        XCTAssertTrue(settings.setScreenshotOutput(.openEditor, enabled: false))
        let copy = AutomationSetting.screenshotOutput("copyScreenshotToClipboard", .copyToClipboard, settings: settings)
        let editor = AutomationSetting.screenshotOutput("showScreenshotEditor", .openEditor, settings: settings)

        XCTAssertThrowsError(try copy.write(false)) {
            XCTAssertEqual(($0 as? AutomationError)?.code, .conflict)
        }
        XCTAssertTrue(settings.copyScreenshotToClipboard)

        try editor.write(true)
        try copy.write(false)
        XCTAssertThrowsError(try editor.write(false)) {
            XCTAssertEqual(($0 as? AutomationError)?.code, .conflict)
        }
        XCTAssertTrue(settings.showScreenshotEditor)
        XCTAssertFalse(settings.copyScreenshotToClipboard)

        try copy.write(true)
        try editor.write(false)
        XCTAssertTrue(settings.copyScreenshotToClipboard)
        XCTAssertFalse(settings.showScreenshotEditor)
        XCTAssertThrowsError(try copy.write(1)) {
            XCTAssertEqual(($0 as? AutomationError)?.code, .invalidParams)
        }
        XCTAssertNil(AutomationSetting.named("saveScreenshotToDisk"))
    }

    @MainActor
    func testStudioProjectFolderIsReadOnlyForAgents() throws {
        let setting = try XCTUnwrap(AutomationSetting.named("studioProjectFolder"))
        XCTAssertFalse(setting.writable)
    }

    func testErrorCodesMatchContract() throws {
        let contract = try loadContract()
        XCTAssertEqual(Set(AutomationErrorCode.allCases.map(\.rawValue)), Set(contract.errors.keys))
        XCTAssertNil(contract.errors["pro_required"])
    }

    @MainActor
    func testNotificationCommandsUseLeastPrivilegeScopes() throws {
        let contract = try loadContract()
        for (name, scope) in [
            "notifications.status": "read",
            "notifications.requestPermission": "settings",
            "notifications.openSettings": "settings"
        ] {
            XCTAssertEqual(contract.commands.first(where: { $0.name == name })?.scope, scope)
            XCTAssertTrue(AutomationCommandRouter.supportedCommands.contains(name))
            XCTAssertEqual(
                AutomationPolicy.decide(enabled: true, clientScopes: ["capture"], command: name, contract: contract).fixtureValue,
                "missing_scope"
            )
        }
    }

    @MainActor
    func testScreenshotWindowCommandsUseReadAndEditWithoutDiscardAccess() throws {
        let contract = try loadContract()
        for (command, scope) in [("editor.list", "read"), ("editor.closeAll", "edit")] {
            XCTAssertTrue(AutomationCommandRouter.supportedCommands.contains(command))
            XCTAssertEqual(contract.command(named: command)?.scope, scope)
            XCTAssertEqual(
                AutomationPolicy.decide(enabled: true, clientScopes: [scope], command: command, contract: contract),
                .allow
            )
            XCTAssertEqual(
                AutomationPolicy.decide(enabled: true, clientScopes: ["capture"], command: command, contract: contract),
                .deny(.missingScope)
            )
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
