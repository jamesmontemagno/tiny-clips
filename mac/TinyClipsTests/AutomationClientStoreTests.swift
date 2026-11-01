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
        XCTAssertTrue(AutomationClientStore(directory: directory).clients.isEmpty)
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
