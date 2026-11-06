import Foundation
import Combine

enum EditorThemeStoreError: LocalizedError, Equatable {
    case notFound(String)
    case limitReached

    var errorDescription: String? {
        switch self {
        case .notFound(let id):
            return "No editor theme with id \(id)."
        case .limitReached:
            return "You can keep at most \(EditorThemeStore.maximumThemes) editor themes. Delete one first."
        }
    }
}

/// Saved editor themes and the active theme for new editor windows, in `editor-themes.json`.
@MainActor
final class EditorThemeStore: ObservableObject {
    static let shared = EditorThemeStore()
    static let fileName = "editor-themes.json"
    static let backupFileName = "editor-themes.json.bak"
    nonisolated static let maximumThemes = 200

    @Published private(set) var themes: [EditorTheme] = []
    @Published private(set) var activeThemeID: String?

    let fileURL: URL
    let backupURL: URL

    init(directory: URL = AutomationStorage.directory) {
        fileURL = directory.appendingPathComponent(Self.fileName)
        backupURL = directory.appendingPathComponent(Self.backupFileName)
        load()
    }

    var activeTheme: EditorTheme? {
        activeThemeID.flatMap(theme(id:))
    }

    func theme(id: String) -> EditorTheme? {
        themes.first { $0.id == id }
    }

    /// Validates `theme`, then adds it or replaces the one with the same id.
    @discardableResult
    func save(_ theme: EditorTheme) throws -> EditorTheme {
        let validated = try theme.validated()
        var updated = themes
        if let index = updated.firstIndex(where: { $0.id == validated.id }) {
            updated[index] = validated
        } else {
            guard updated.count < Self.maximumThemes else { throw EditorThemeStoreError.limitReached }
            updated.append(validated)
        }
        try commit(themes: updated, activeThemeID: activeThemeID)
        return validated
    }

    func setActive(id: String?) throws {
        if let id, theme(id: id) == nil { throw EditorThemeStoreError.notFound(id) }
        guard id != activeThemeID else { return }
        try commit(themes: themes, activeThemeID: id)
    }

    func delete(id: String) throws {
        guard theme(id: id) != nil else { throw EditorThemeStoreError.notFound(id) }
        try commit(
            themes: themes.filter { $0.id != id },
            activeThemeID: activeThemeID == id ? nil : activeThemeID
        )
    }

    /// Adds a theme from a file. It never replaces one: a taken id gets a new id, and a taken name
    /// gets a suffix.
    @discardableResult
    func importTheme(from data: Data) throws -> EditorTheme {
        var theme = try EditorTheme.parse(data)
        if self.theme(id: theme.id) != nil {
            theme.id = UUID().uuidString
        }
        theme.name = uniqueName(for: theme.name)
        return try save(theme)
    }

    func exportData(id: String) throws -> Data {
        guard let theme = theme(id: id) else { throw EditorThemeStoreError.notFound(id) }
        return try theme.encodedData()
    }

    /// `name`, or `name (2)` and so on when it's taken, always within the format's name limit.
    func uniqueName(for name: String) -> String {
        let taken = Set(themes.map { $0.name.lowercased() })
        guard taken.contains(name.lowercased()) else { return name }
        var number = 2
        while true {
            let suffix = " (\(number))"
            let candidate = String(name.prefix(EditorTheme.maximumNameLength - suffix.count))
                .trimmingCharacters(in: .whitespacesAndNewlines) + suffix
            if !taken.contains(candidate.lowercased()) { return candidate }
            number += 1
        }
    }

    // MARK: - File

    private func load() {
        guard let data = try? Data(contentsOf: fileURL) else { return }
        guard let document = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              (document["version"] as? Int) == EditorTheme.currentVersion,
              let items = document["themes"] as? [Any] else {
            recover(themes: [], activeThemeID: nil)
            return
        }

        var loaded: [EditorTheme] = []
        var skippedAny = false
        for item in items {
            guard loaded.count < Self.maximumThemes,
                  let theme = try? EditorTheme.parse(jsonObject: item),
                  !loaded.contains(where: { $0.id == theme.id }) else {
                skippedAny = true
                continue
            }
            loaded.append(theme)
        }
        let storedActive = document["activeThemeId"] as? String
        let active = storedActive.flatMap { id in loaded.contains { $0.id == id } ? id : nil }
        if skippedAny {
            recover(themes: loaded, activeThemeID: active)
        } else {
            themes = loaded
            activeThemeID = active
        }
    }

    /// Keeps a copy of the unreadable file, and only then replaces it, so nothing is lost if the
    /// copy can't be made.
    private func recover(themes recovered: [EditorTheme], activeThemeID recoveredActive: String?) {
        themes = recovered
        activeThemeID = recoveredActive
        do {
            let manager = FileManager.default
            if manager.fileExists(atPath: backupURL.path) {
                try manager.removeItem(at: backupURL)
            }
            try manager.copyItem(at: fileURL, to: backupURL)
            try write(themes: recovered, activeThemeID: recoveredActive)
        } catch {
            NSLog("TinyClips could not back up \(Self.fileName): \(error.localizedDescription)")
        }
    }

    /// The published state changes only after the file is written, so a failed write changes nothing.
    private func commit(themes updated: [EditorTheme], activeThemeID updatedActive: String?) throws {
        try write(themes: updated, activeThemeID: updatedActive)
        themes = updated
        activeThemeID = updatedActive
    }

    private func write(themes: [EditorTheme], activeThemeID: String?) throws {
        try FileManager.default.createDirectory(
            at: fileURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let document = EditorThemeDocument(activeThemeId: activeThemeID, themes: themes)
        try encoder.encode(document).write(to: fileURL, options: [.atomic])
    }
}

private struct EditorThemeDocument: Encodable {
    let activeThemeId: String?
    let themes: [EditorTheme]

    private enum CodingKeys: String, CodingKey {
        case version, activeThemeId, themes
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(EditorTheme.currentVersion, forKey: .version)
        if let activeThemeId {
            try container.encode(activeThemeId, forKey: .activeThemeId)
        } else {
            try container.encodeNil(forKey: .activeThemeId)
        }
        try container.encode(themes, forKey: .themes)
    }
}
