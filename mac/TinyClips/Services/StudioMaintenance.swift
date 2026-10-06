import Foundation

// MARK: - Studio Maintenance

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

    /// Removes old project sources on a background task, following the storage settings. Projects
    /// open in an editor or being recorded are left alone.
    ///
    /// - Parameter completion: Called on the main actor with the number of projects removed, or
    ///   nil when a cleanup was already running.
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
        // The first name is made here, on the main actor, as every other saved video's is.
        let firstURL = SaveService.shared.generateURL(for: .video)
        let url = try await Task.detached(priority: .userInitiated) { () throws -> URL in
            var isFirstName = true
            return try StudioScreenRecording.save(store: StudioProjectStore.shared, id: projectID) {
                if isFirstName {
                    isFirstName = false
                    return firstURL
                }
                // Only when a file took the first name while the recording was being copied.
                return SaveService.shared.generateURL(for: .video)
            }
        }.value
        SaveService.shared.handleSavedFile(url: url, type: .video)
        return url
    }
}
