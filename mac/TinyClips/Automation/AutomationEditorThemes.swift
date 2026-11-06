import Foundation

/// `editor.themes.*`. Theme JSON is the wire format, so get is export and save is import.
@MainActor
enum AutomationEditorThemes {
    static func list(_ params: [String: Any], store: EditorThemeStore) throws -> [String: Any] {
        try rejectUnknown(params, allowed: [])
        return [
            "activeThemeId": store.activeThemeID ?? NSNull(),
            "themes": store.themes.map { ["id": $0.id, "name": $0.name] },
        ]
    }

    static func get(_ params: [String: Any], store: EditorThemeStore) throws -> [String: Any] {
        try rejectUnknown(params, allowed: ["id"])
        return ["theme": try existingTheme(try requiredID(params), store: store).jsonObject()]
    }

    static func save(_ params: [String: Any], store: EditorThemeStore) throws -> [String: Any] {
        try rejectUnknown(params, allowed: ["theme"])
        guard let object = params["theme"] as? [String: Any] else {
            throw AutomationError(.invalidParams, "theme is required and must be an object.")
        }
        return try mapped {
            let theme = try EditorTheme.parse(jsonObject: object)
            return ["theme": try store.save(theme).jsonObject()]
        }
    }

    static func setActive(_ params: [String: Any], store: EditorThemeStore) throws -> [String: Any] {
        try rejectUnknown(params, allowed: ["id"])
        let id: String?
        switch params["id"] {
        case nil, is NSNull: id = nil
        case let value as String where !value.isEmpty: id = value
        default: throw AutomationError(.invalidParams, "id must be a theme id. Omit it for None.")
        }
        return try mapped {
            try store.setActive(id: id)
            return ["activeThemeId": store.activeThemeID ?? NSNull()]
        }
    }

    static func delete(_ params: [String: Any], store: EditorThemeStore) throws -> [String: Any] {
        try rejectUnknown(params, allowed: ["id"])
        let id = try requiredID(params)
        return try mapped {
            try store.delete(id: id)
            return ["deleted": true]
        }
    }

    /// The theme for `themeId` on `editor.open` and `editor.annotate`; nil when it wasn't given.
    static func theme(forParam value: Any?, store: EditorThemeStore) throws -> EditorTheme? {
        switch value {
        case nil, is NSNull:
            return nil
        case let id as String where !id.isEmpty:
            return try existingTheme(id, store: store)
        default:
            throw AutomationError(.invalidParams, "themeId must be a theme id.")
        }
    }

    private static func existingTheme(_ id: String, store: EditorThemeStore) throws -> EditorTheme {
        guard let theme = store.theme(id: id) else {
            throw AutomationError(.notFound, "No editor theme with id \(id).")
        }
        return theme
    }

    private static func requiredID(_ params: [String: Any]) throws -> String {
        guard let id = params["id"] as? String, !id.isEmpty else {
            throw AutomationError(.invalidParams, "id is required.")
        }
        return id
    }

    private static func rejectUnknown(_ params: [String: Any], allowed: Set<String>) throws {
        if let unknown = params.keys.sorted().first(where: { !allowed.contains($0) }) {
            throw AutomationError(.invalidParams, "Unknown parameter \(unknown).")
        }
    }

    private static func mapped(_ work: () throws -> [String: Any]) throws -> [String: Any] {
        do {
            return try work()
        } catch let error as EditorThemeValidationError {
            throw AutomationError(.invalidParams, error.localizedDescription)
        } catch EditorThemeStoreError.notFound(let id) {
            throw AutomationError(.notFound, "No editor theme with id \(id).")
        } catch EditorThemeStoreError.limitReached {
            throw AutomationError(.invalidParams, EditorThemeStoreError.limitReached.localizedDescription)
        } catch {
            throw AutomationError(.internal, "Could not save editor themes: \(error.localizedDescription)")
        }
    }
}
