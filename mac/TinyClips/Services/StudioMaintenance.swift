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
}
