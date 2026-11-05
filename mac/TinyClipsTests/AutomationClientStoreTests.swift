import XCTest
@testable import TinyClips

final class AutomationClientStoreTests: XCTestCase {
    private var directory: URL!

    override func setUpWithError() throws {
        directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("TinyClipsAutomationTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: directory)
    }

    func testTokenHashIsSHA256Hex() {
        XCTAssertEqual(
            AutomationClientStore.hash(token: "abc"),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        )
    }

    func testGeneratedTokensAreBase64URLAndUnique() throws {
        let first = try AutomationClientStore.generateToken()
        let second = try AutomationClientStore.generateToken()
        XCTAssertNotEqual(first, second)
        XCTAssertEqual(first.count, 43)
        XCTAssertNil(first.rangeOfCharacter(from: CharacterSet(charactersIn: "+/=")))
    }

    func testAddLookupPersistAndRevoke() throws {
        let store = AutomationClientStore(directory: directory)
        let (client, token) = try store.addClient(name: "Agent", scopes: ["read", "capture"])
        XCTAssertEqual(client.tokenHash, AutomationClientStore.hash(token: token))
        XCTAssertEqual(store.client(forToken: token)?.id, client.id)
        XCTAssertTrue(store.isCurrent(client))
        XCTAssertNil(store.client(forToken: "wrong"))
        XCTAssertNil(store.client(forToken: nil))

        let fileData = try String(contentsOf: store.fileURL, encoding: .utf8)
        XCTAssertFalse(fileData.contains(token), "The raw token must never be stored")

        store.markUsed(id: client.id)
        let reloaded = AutomationClientStore(directory: directory)
        XCTAssertEqual(reloaded.clients.count, 1)
        XCTAssertEqual(reloaded.clients.first?.scopes, client.scopes)
        XCTAssertNotNil(reloaded.clients.first?.lastUsedAt)
        XCTAssertEqual(reloaded.client(forToken: token)?.name, "Agent")

        reloaded.revoke(id: client.id)
        XCTAssertNil(reloaded.client(forToken: token))
        XCTAssertFalse(reloaded.isCurrent(client))
        XCTAssertTrue(AutomationClientStore(directory: directory).clients.isEmpty)
    }

    @MainActor
    func testRevocationDeniesOnlyThatClientsCurrentAndQueuedApprovals() async throws {
        let store = AutomationClientStore(directory: directory)
        let (revoked, _) = try store.addClient(name: "Revoked", scopes: ["delete"])
        let (other, _) = try store.addClient(name: "Other", scopes: ["delete"])
        let firstPresented = expectation(description: "first approval presented")
        let otherPresented = expectation(description: "other approval presented")
        var panels: [TestApprovalPanel] = []
        let approvals = AutomationApprovalCenter { request, completion in
            let panel = TestApprovalPanel(onComplete: completion) {
                (request.title == "first" ? firstPresented : otherPresented).fulfill()
            }
            panels.append(panel)
            return panel
        }
        let controller = AutomationController(
            clientStore: store,
            auditLog: AutomationAuditLog(directory: directory),
            approvals: approvals
        )
        func request(_ title: String) -> AutomationApprovalCenter.Request {
            .init(title: title, message: "", details: [], allowTitle: "Allow")
        }

        let first = Task { @MainActor in
            await approvals.request(request("first"), clientID: revoked.id)
        }
        await fulfillment(of: [firstPresented], timeout: 2)

        let revokedQueued = expectation(description: "revoked request queued")
        let queued = Task { @MainActor in
            revokedQueued.fulfill()
            return await approvals.request(request("queued"), clientID: revoked.id)
        }
        await fulfillment(of: [revokedQueued], timeout: 2)

        let otherQueued = expectation(description: "other request queued")
        let unaffected = Task { @MainActor in
            otherQueued.fulfill()
            return await approvals.request(request("other"), clientID: other.id)
        }
        await fulfillment(of: [otherQueued], timeout: 2)

        controller.revoke(revoked)
        let firstResult = await first.value
        let queuedResult = await queued.value
        XCTAssertFalse(firstResult)
        XCTAssertFalse(queuedResult)
        XCTAssertTrue(panels[0].dismissed)
        XCTAssertNil(store.clients.first { $0.id == revoked.id })
        await fulfillment(of: [otherPresented], timeout: 2)
        panels[1].complete(true)
        let otherResult = await unaffected.value
        XCTAssertTrue(otherResult)
    }

    func testPairingHandsOutTokenOnce() throws {
        let store = AutomationClientStore(directory: directory)
        var pairings = AutomationPairings()
        let pairing = try pairings.begin(clientName: "Agent", scopes: ["read", "read"])
        XCTAssertEqual(pairing.scopes, ["read"])
        XCTAssertEqual(pairings.status(id: pairing.id)?.status, .pending)

        try pairings.resolve(id: pairing.id, approved: true, store: store)
        let approved = try XCTUnwrap(pairings.status(id: pairing.id))
        XCTAssertEqual(approved.status, .approved)
        let token = try XCTUnwrap(approved.token)
        XCTAssertNotNil(store.client(forToken: token))
        XCTAssertNil(pairings.status(id: pairing.id), "The token must only be returned once")
    }

    func testPairingGrantsTheScopesTheUserChose() throws {
        let store = AutomationClientStore(directory: directory)
        var pairings = AutomationPairings()
        let pairing = try pairings.begin(clientName: "Agent", scopes: ["read", "record"])

        try pairings.resolve(id: pairing.id, approved: true, scopes: ["read", "delete"], store: store)
        let approved = try XCTUnwrap(pairings.status(id: pairing.id))
        XCTAssertEqual(approved.scopes, ["delete", "read"])
        XCTAssertEqual(store.client(forToken: approved.token)?.scopes, ["delete", "read"])
    }

    func testPairingWithNoScopesChosenIsDenied() throws {
        let store = AutomationClientStore(directory: directory)
        var pairings = AutomationPairings()
        let pairing = try pairings.begin(clientName: "Agent", scopes: ["read"])
        try pairings.resolve(id: pairing.id, approved: true, scopes: [], store: store)
        XCTAssertEqual(pairings.status(id: pairing.id)?.status, .denied)
        XCTAssertTrue(store.clients.isEmpty)
    }

    func testUpdateScopesKeepsTokenAndPersists() throws {
        let store = AutomationClientStore(directory: directory)
        let (client, token) = try store.addClient(name: "Agent", scopes: ["read"])

        try store.updateScopes(id: client.id, scopes: ["read", "delete", "delete"])
        XCTAssertEqual(store.client(forToken: token)?.scopes, ["delete", "read"])
        XCTAssertEqual(AutomationClientStore(directory: directory).client(forToken: token)?.scopes, ["delete", "read"])
        XCTAssertThrowsError(try store.updateScopes(id: client.id, scopes: []))
        XCTAssertThrowsError(try store.updateScopes(id: "missing", scopes: ["read"]))
    }

    func testUpdateScopesRestoresOriginalScopesWhenPersistenceFails() throws {
        let blockedDirectory = directory.appendingPathComponent("blocked", isDirectory: true)
        let store = AutomationClientStore(directory: blockedDirectory)
        let (client, _) = try store.addClient(name: "Agent", scopes: ["read"])

        try FileManager.default.removeItem(at: blockedDirectory)
        try Data().write(to: blockedDirectory)

        XCTAssertThrowsError(try store.updateScopes(id: client.id, scopes: ["delete"]))
        XCTAssertEqual(store.clients.first?.scopes, ["read"])

        try FileManager.default.removeItem(at: blockedDirectory)
        try FileManager.default.createDirectory(at: blockedDirectory, withIntermediateDirectories: true)
        try store.updateScopes(id: client.id, scopes: ["delete"])
        XCTAssertEqual(AutomationClientStore(directory: blockedDirectory).clients.first?.scopes, ["delete"])
    }

    @MainActor
    func testScopeSelectionStartsWithRequestedScopesInDisplayOrder() throws {
        let repoRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let contract = try AutomationContract.load(from: repoRoot.appendingPathComponent("shared/automation/commands.json"))
        XCTAssertEqual(Array(contract.displayScopes.prefix(2)), ["read", "capture"])
        XCTAssertEqual(Array(contract.displayScopes.suffix(3)), contract.sensitiveScopes)
        let selection = AutomationScopeSelection(contract: contract, requested: ["delete", "read", "bogus"])
        XCTAssertEqual(selection.grantedScopes, ["read", "delete"])
    }

    func testDeniedPairingAddsNoClient() throws {
        let store = AutomationClientStore(directory: directory)
        var pairings = AutomationPairings()
        let pairing = try pairings.begin(clientName: "Agent", scopes: ["read"])
        try pairings.resolve(id: pairing.id, approved: false, store: store)
        XCTAssertEqual(pairings.status(id: pairing.id)?.status, .denied)
        XCTAssertNil(pairings.status(id: pairing.id)?.token)
        XCTAssertTrue(store.clients.isEmpty)
    }

    func testPendingPairingsAreLimitedAndExpire() throws {
        var pairings = AutomationPairings()
        let start = Date()
        for _ in 0..<AutomationPairings.maximumPending {
            _ = try pairings.begin(clientName: "Agent", scopes: ["read"], now: start)
        }
        XCTAssertThrowsError(try pairings.begin(clientName: "Agent", scopes: ["read"], now: start))
        XCTAssertNoThrow(try pairings.begin(
            clientName: "Agent",
            scopes: ["read"],
            now: start.addingTimeInterval(AutomationPairings.lifetime + 1)
        ))
    }

    func testAuditLogAppendsAndRotates() throws {
        let log = AutomationAuditLog(directory: directory, maximumBytes: 400)
        for index in 0..<10 {
            log.record(client: "Agent", command: "app.status", outcome: "ok", detail: "entry \(index)", synchronously: true)
        }
        let current = try String(contentsOf: log.fileURL, encoding: .utf8)
        XCTAssertTrue(FileManager.default.fileExists(atPath: log.rotatedFileURL.path))
        XCTAssertLessThanOrEqual(current.utf8.count, 400)

        let lines = current.split(separator: "\n")
        XCTAssertFalse(lines.isEmpty)
        let entry = try XCTUnwrap(
            JSONSerialization.jsonObject(with: Data(lines.last!.utf8)) as? [String: String]
        )
        XCTAssertEqual(entry["command"], "app.status")
        XCTAssertEqual(entry["detail"], "entry 9")
        XCTAssertEqual(entry["client"], "Agent")
    }

    func testLibraryKindsAndScan() throws {
        XCTAssertEqual(AutomationLibrary.kind(forPath: "/a/TinyClips 1.PNG"), "screenshot")
        XCTAssertEqual(AutomationLibrary.kind(forPath: "/a/b.mp4"), "video")
        XCTAssertEqual(AutomationLibrary.kind(forPath: "/a/b.gif"), "gif")
        XCTAssertNil(AutomationLibrary.kind(forPath: "/a/b.txt"))

        let shot = directory.appendingPathComponent("TinyClips shot.png")
        let other = directory.appendingPathComponent("other.gif")
        let text = directory.appendingPathComponent("TinyClips notes.txt")
        for url in [shot, other, text] {
            try Data([1, 2, 3]).write(to: url)
        }

        let all = AutomationLibrary.scan(directories: [directory, directory], extraPaths: [shot.path], tinyClipsOnly: false)
        XCTAssertEqual(Set(all.map(\.kind)), ["screenshot", "gif"])
        XCTAssertEqual(all.count, 2)

        let tinyClipsOnly = AutomationLibrary.scan(directories: [directory], extraPaths: [], tinyClipsOnly: true)
        XCTAssertEqual(tinyClipsOnly.map(\.path), [shot.standardizedFileURL.path])
    }

    func testSocketPathFitsSunPath() {
        XCTAssertLessThanOrEqual(AutomationEndpoint.socketPath.utf8.count, AutomationEndpoint.maximumPathLength)
    }
}

@MainActor
private final class TestApprovalPanel: AutomationApprovalPresenting {
    private let onComplete: (Bool) -> Void
    private let onPresent: () -> Void
    private(set) var dismissed = false

    init(onComplete: @escaping (Bool) -> Void, onPresent: @escaping () -> Void) {
        self.onComplete = onComplete
        self.onPresent = onPresent
    }

    func present() { onPresent() }
    func dismiss() { dismissed = true }
    func complete(_ approved: Bool) { onComplete(approved) }
}
