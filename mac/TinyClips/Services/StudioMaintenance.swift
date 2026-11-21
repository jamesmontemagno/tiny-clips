import AppKit

// MARK: - Studio Maintenance

enum StudioLibraryActionError: LocalizedError {
    case inUse, cannotOpenFolder

    var errorDescription: String? {
        switch self {
        case .inUse: return "This project is in use. Finish recording or close its Studio editor before deleting it."
        case .cannotOpenFolder: return "Finder could not open the project folder."
        }
    }
}

/// Housekeeping for Tiny Clips Studio projects: removing old sources by the storage settings.
@MainActor
enum StudioMaintenance {
    private static var isCleaningUp = false
    private static var recordingProjectIDs: Set<String> = []

    /// Marks a project as being recorded into, so cleanup leaves its folder alone however long the
    /// recording runs.
    static func recordingDidBegin(projectID: String) {
        recordingProjectIDs.insert(projectID)
    }

    static func recordingDidEnd(projectID: String) {
        recordingProjectIDs.remove(projectID)
    }

    static func deleteLibraryProject(id: String) async throws {
        guard !recordingProjectIDs.contains(id), !StudioWindowRegistry.shared.openProjectIDs.contains(id) else {
            throw StudioLibraryActionError.inUse
        }
        try await Task.detached(priority: .userInitiated) {
            try StudioProjectStore.shared.deleteLibraryProject(id: id)
        }.value
        NotificationCenter.default.post(name: .studioProjectsDidChange, object: nil)
    }

    static func openLibraryFolder(id: String) throws {
        try StudioProjectStore.shared.withLibraryFolder(id: id) { folder in
            guard NSWorkspace.shared.selectFile(nil, inFileViewerRootedAtPath: folder.path) else {
                throw StudioLibraryActionError.cannotOpenFolder
            }
        }
    }

    /// Removes old project sources on a background task, following the storage settings. Projects
    /// open in an editor or being recorded are left alone.
    ///
    /// - Parameter completion: Called on the main actor with the number of projects removed, or
    ///   nil when nothing was started because a cleanup was already running.
    static func cleanUp(completion: (@MainActor (Int?) -> Void)? = nil) {
        guard !isCleaningUp else {
            completion?(nil)
            return
        }
        isCleaningUp = true

        // On the App Store build this is what opens a save folder the user chose. Cleanup looks
        // whether each exported video is still where it was saved, and could not see into that
        // folder otherwise.
        _ = SaveService.shared.outputDirectoryURL(for: .video)

        let options = CaptureSettings.shared.studioCleanupOptions
        let inUse = StudioWindowRegistry.shared.openProjectIDs.union(recordingProjectIDs)
        Task.detached(priority: .utility) {
            let removed = (try? StudioProjectStore.shared.cleanup(options: options, inUseProjectIDs: inUse)) ?? []
            await MainActor.run {
                isCleaningUp = false
                completion?(removed.count)
            }
        }
    }

    /// Saves a project's screen recording as an ordinary video, in the folder and under the
    /// name any saved video gets, and treats it as one from there: clipboard, notice, recent
    /// captures. The project is left as it is. This is the way out for a recording that Studio
    /// cannot show.
    ///
    /// - Returns: Where the video went.
    static func saveScreenRecording(projectID: String) async throws -> URL {
        let directory = SaveService.shared.outputDirectoryURL(for: .video)
        let stem = SaveService.shared.generatedFileName(for: .video, fileExtension: "")
        let url = try await Task.detached(priority: .userInitiated) { () throws -> URL in
            try StudioScreenRecording.save(store: StudioProjectStore.shared, id: projectID) { ext in
                SaveService.uniqueURL(in: directory, filename: "\(stem).\(ext)")
            }
        }.value
        SaveService.shared.handleSavedFile(url: url, type: .video)
        return url
    }
}
