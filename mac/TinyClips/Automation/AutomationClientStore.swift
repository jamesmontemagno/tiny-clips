import CryptoKit
import Foundation
import Security

/// A client paired with Tiny Clips. Only a SHA-256 hash of its token is stored.
struct AutomationClient: Codable, Identifiable, Equatable {
    var id: String
    var name: String
    var scopes: [String]
    var tokenHash: String
    var createdAt: Date
    var lastUsedAt: Date?
}

enum AutomationStorage {
    /// `~/Library/Application Support/TinyClips` (inside the container for the App Store build).
    static var directory: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support")
        return base.appendingPathComponent("TinyClips", isDirectory: true)
    }
}

/// Paired clients, persisted as JSON in `automation-clients.json`.
final class AutomationClientStore {
    static let fileName = "automation-clients.json"

    let fileURL: URL
    private(set) var clients: [AutomationClient] = []

    init(directory: URL = AutomationStorage.directory) {
        fileURL = directory.appendingPathComponent(Self.fileName)
        reload()
    }

    func reload() {
        guard let data = try? Data(contentsOf: fileURL) else {
            clients = []
            return
        }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        clients = (try? decoder.decode([AutomationClient].self, from: data)) ?? []
    }

    /// Adds a client and returns it with its token. The token is not stored and cannot be read again.
    @discardableResult
    func addClient(name: String, scopes: [String], now: Date = Date()) throws -> (client: AutomationClient, token: String) {
        let token = try Self.generateToken()
        let client = AutomationClient(
            id: UUID().uuidString,
            name: name,
            scopes: Array(Set(scopes)).sorted(),
            tokenHash: Self.hash(token: token),
            createdAt: now,
            lastUsedAt: nil
        )
        clients.append(client)
        try persist()
        return (client, token)
    }

    func client(forToken token: String?) -> AutomationClient? {
        guard let token, !token.isEmpty else { return nil }
        let hash = Self.hash(token: token)
        return clients.first { Self.constantTimeEquals($0.tokenHash, hash) }
    }

    func isCurrent(_ client: AutomationClient) -> Bool {
        clients.contains { $0.id == client.id && Self.constantTimeEquals($0.tokenHash, client.tokenHash) }
    }

    func markUsed(id: String, at date: Date = Date()) {
        guard let index = clients.firstIndex(where: { $0.id == id }) else { return }
        clients[index].lastUsedAt = date
        try? persist()
    }

    func revoke(id: String) {
        clients.removeAll { $0.id == id }
        try? persist()
    }

    /// Replaces a client's scopes. Its token stays valid. A client needs at least one scope.
    func updateScopes(id: String, scopes: [String]) throws {
        let updated = Array(Set(scopes)).sorted()
        guard !updated.isEmpty else {
            throw AutomationError(.invalidParams, "A paired agent needs at least one scope. Revoke it instead.")
        }
        guard let index = clients.firstIndex(where: { $0.id == id }) else {
            throw AutomationError(.notFound, "That agent is no longer paired.")
        }
        guard clients[index].scopes != updated else { return }
        let previousScopes = clients[index].scopes
        clients[index].scopes = updated
        do {
            try persist()
        } catch {
            clients[index].scopes = previousScopes
            throw error
        }
    }

    func revokeAll() {
        clients.removeAll()
        try? persist()
    }

    private func persist() throws {
        try FileManager.default.createDirectory(
            at: fileURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(clients).write(to: fileURL, options: [.atomic])
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: fileURL.path)
    }

    static func hash(token: String) -> String {
        SHA256.hash(data: Data(token.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    /// 32 random bytes, base64url without padding.
    static func generateToken() throws -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        guard SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes) == errSecSuccess else {
            throw AutomationError(.internal, "Could not generate a client token.")
        }
        return Data(bytes).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    private static func constantTimeEquals(_ lhs: String, _ rhs: String) -> Bool {
        let a = Array(lhs.utf8), b = Array(rhs.utf8)
        guard a.count == b.count else { return false }
        var difference: UInt8 = 0
        for index in a.indices { difference |= a[index] ^ b[index] }
        return difference == 0
    }
}

/// Pairing requests waiting for, or just past, the user's answer. Kept in memory only.
struct AutomationPairings {
    enum Status: String {
        case pending, approved, denied
    }

    struct Pairing {
        var id: String
        var clientName: String
        var scopes: [String]
        var status: Status
        var token: String?
        var createdAt: Date
    }

    static let maximumPending = 3
    /// Answered pairings that were never collected are forgotten after this long.
    static let lifetime: TimeInterval = 10 * 60

    private(set) var pairings: [String: Pairing] = [:]

    var pendingCount: Int {
        pairings.values.filter { $0.status == .pending }.count
    }

    mutating func begin(clientName: String, scopes: [String], now: Date = Date()) throws -> Pairing {
        prune(now: now)
        guard pendingCount < Self.maximumPending else {
            throw AutomationError(.busy, "Too many pairing requests are waiting for an answer.")
        }
        let pairing = Pairing(
            id: UUID().uuidString,
            clientName: clientName,
            scopes: Array(Set(scopes)).sorted(),
            status: .pending,
            token: nil,
            createdAt: now
        )
        pairings[pairing.id] = pairing
        return pairing
    }

    /// Records the user's answer. Approval adds the client to `store` with `scopes` (the scopes the user
    /// granted, or the requested ones when nil) and holds its token for one read.
    mutating func resolve(id: String, approved: Bool, scopes: [String]? = nil, store: AutomationClientStore) throws {
        guard var pairing = pairings[id], pairing.status == .pending else { return }
        let granted = Array(Set(scopes ?? pairing.scopes)).sorted()
        if approved, !granted.isEmpty {
            let (_, token) = try store.addClient(name: pairing.clientName, scopes: granted)
            pairing.scopes = granted
            pairing.status = .approved
            pairing.token = token
        } else {
            pairing.status = .denied
        }
        pairings[id] = pairing
    }

    /// Returns the pairing's state. An answered pairing is removed once read, so a token is handed out once.
    mutating func status(id: String, now: Date = Date()) -> Pairing? {
        prune(now: now)
        guard let pairing = pairings[id] else { return nil }
        if pairing.status != .pending {
            pairings.removeValue(forKey: id)
        }
        return pairing
    }

    private mutating func prune(now: Date) {
        pairings = pairings.filter { now.timeIntervalSince($0.value.createdAt) < Self.lifetime }
    }
}

/// Appends one JSON object per line to `automation-audit.jsonl`, rotating it at about 1 MB.
final class AutomationAuditLog {
    static let fileName = "automation-audit.jsonl"
    static let maximumBytes = 1_000_000

    let fileURL: URL
    private let maximumBytes: Int
    private let queue = DispatchQueue(label: "com.tinyclips.automation.audit")

    init(directory: URL = AutomationStorage.directory, maximumBytes: Int = AutomationAuditLog.maximumBytes) {
        fileURL = directory.appendingPathComponent(Self.fileName)
        self.maximumBytes = maximumBytes
    }

    var rotatedFileURL: URL {
        fileURL.deletingPathExtension().appendingPathExtension("1.jsonl")
    }

    func record(
        client: String?,
        command: String,
        outcome: String,
        detail: String? = nil,
        date: Date = Date(),
        synchronously: Bool = false
    ) {
        var entry: [String: String] = [
            "time": ISO8601DateFormatter().string(from: date),
            "command": command,
            "outcome": outcome
        ]
        if let client { entry["client"] = client }
        if let detail { entry["detail"] = detail }
        guard var line = try? JSONSerialization.data(withJSONObject: entry, options: [.sortedKeys]) else { return }
        line.append(0x0A)
        let write = { [fileURL, rotatedFileURL, maximumBytes] in
            let manager = FileManager.default
            try? manager.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            if let size = (try? manager.attributesOfItem(atPath: fileURL.path))?[.size] as? Int,
               size + line.count > maximumBytes {
                try? manager.removeItem(at: rotatedFileURL)
                try? manager.moveItem(at: fileURL, to: rotatedFileURL)
            }
            if let handle = try? FileHandle(forWritingTo: fileURL) {
                defer { try? handle.close() }
                _ = try? handle.seekToEnd()
                try? handle.write(contentsOf: line)
            } else {
                manager.createFile(atPath: fileURL.path, contents: line, attributes: [.posixPermissions: 0o600])
            }
        }
        if synchronously {
            queue.sync(execute: write)
        } else {
            queue.async(execute: write)
        }
    }
}
