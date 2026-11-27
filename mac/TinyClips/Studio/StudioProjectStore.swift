import AVFoundation
import Darwin
import Foundation
import ImageIO

// MARK: - Store Types

struct StudioCameraCreationInfo: Equatable, Sendable {
    var width: Int
    var height: Int
    var duration: Double
    var startOffset: Double

    init(width: Int, height: Int, duration: Double, startOffset: Double = 0) {
        self.width = width
        self.height = height
        self.duration = duration
        self.startOffset = startOffset
    }
}

/// The styling a new project starts with: the `canvas`, `screen`, and `camera` objects of
/// project.json. A look is shared by every new project, so it never keeps a crop.
struct StudioLook: Codable, Equatable, Sendable {
    var canvas: StudioCanvas
    var screen: StudioScreenStyle
    var camera: StudioCameraStyle

    init(
        canvas: StudioCanvas = StudioCanvas(),
        screen: StudioScreenStyle = StudioScreenStyle(),
        camera: StudioCameraStyle = StudioCameraStyle()
    ) {
        self.canvas = canvas
        self.screen = screen
        self.camera = camera
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        canvas = try container.decodeIfPresent(StudioCanvas.self, forKey: .canvas) ?? StudioCanvas()
        screen = try container.decodeIfPresent(StudioScreenStyle.self, forKey: .screen) ?? StudioScreenStyle()
        camera = try container.decodeIfPresent(StudioCameraStyle.self, forKey: .camera) ?? StudioCameraStyle()
    }

    private enum CodingKeys: String, CodingKey {
        case canvas
        case screen
        case camera
    }

    var withoutCrops: StudioLook {
        var copy = self
        copy.screen.crop = nil
        copy.camera.crop = nil
        return copy
    }

    /// The look as JSON text, for keeping in app settings.
    func settingsText() -> String? {
        guard let data = try? JSONEncoder().encode(withoutCrops) else { return nil }
        return String(data: data, encoding: .utf8)
    }

    /// Reads text written by `settingsText()`. Nil when there is no text or it is not a look.
    init?(settingsText: String) {
        guard !settingsText.isEmpty,
              let look = try? StudioJSON.makeDecoder().decode(StudioLook.self, from: Data(settingsText.utf8))
        else {
            return nil
        }
        self = look.withoutCrops
    }
}

struct StudioProjectCreationRequest: Equatable, Sendable {
    var name: String
    var screenWidth: Int
    var screenHeight: Int
    var screenDuration: Double
    var screenFrameRate: Double

    /// What each sound track of the screen file holds, in the file's order, or nil when the
    /// recorder cannot say (`sources.screen.audioTracks`).
    var screenAudioTracks: [String]?
    var camera: StudioCameraCreationInfo?
    var bubbleAnchor: StudioAnchor
    var clickOverlay: StudioClickOverlay
    var branding: Bool
    var appVersion: String
    var look: StudioLook?

    /// The corners the camera was in while recording, with the time it got to each. With
    /// `layoutMarkers` they become the project's scenes (section 9.1 of the format).
    var cameraCorners: [StudioCameraCornerEvent]

    /// The layouts chosen while recording, with the time of each.
    var layoutMarkers: [StudioLayoutMarker]

    init(
        name: String = "",
        screenWidth: Int,
        screenHeight: Int,
        screenDuration: Double,
        screenFrameRate: Double = 30,
        screenAudioTracks: [String]? = nil,
        camera: StudioCameraCreationInfo? = nil,
        bubbleAnchor: StudioAnchor = .bottomRight,
        clickOverlay: StudioClickOverlay = StudioClickOverlay(),
        branding: Bool = false,
        appVersion: String,
        look: StudioLook? = nil,
        cameraCorners: [StudioCameraCornerEvent] = [],
        layoutMarkers: [StudioLayoutMarker] = []
    ) {
        self.name = name
        self.screenWidth = screenWidth
        self.screenHeight = screenHeight
        self.screenDuration = screenDuration
        self.screenFrameRate = screenFrameRate
        self.screenAudioTracks = screenAudioTracks
        self.camera = camera
        self.bubbleAnchor = bubbleAnchor
        self.clickOverlay = clickOverlay
        self.branding = branding
        self.appVersion = appVersion
        self.look = look
        self.cameraCorners = cameraCorners
        self.layoutMarkers = layoutMarkers
    }
}

struct StudioFlatProjectRequest: Equatable, Sendable {
    var videoURL: URL
    var name: String
    var width: Int
    var height: Int
    var duration: Double
    var frameRate: Double
    var appVersion: String
    var copyVideo = false
    var identityBeforeReadingMetadata: StudioVideoSourceIdentity?

    init(videoURL: URL, name: String = "", width: Int, height: Int, duration: Double, frameRate: Double = 30, appVersion: String) {
        self.videoURL = videoURL
        self.name = name
        self.width = width
        self.height = height
        self.duration = duration
        self.frameRate = frameRate
        self.appVersion = appVersion
    }
}

struct StudioVideoSourceIdentity: Codable, Equatable, Sendable {
    var path: String
    var size: UInt64
    var modifiedAt: Date
    // Older copy records only have path, size and date.
    var volume: UInt64?
    var fileNumber: UInt64?

    func matches(_ current: Self) -> Bool {
        path == current.path && size == current.size && modifiedAt == current.modifiedAt
            && (volume == nil || volume == current.volume)
            && (fileNumber == nil || fileNumber == current.fileNumber)
    }
}

/// Why a project could not be saved as a folder, or opened from one.
enum StudioProjectFolderError: Error, Equatable, LocalizedError {
    /// The project is built around a video that is kept elsewhere, which a folder would not hold.
    case externalSource

    /// A file the project names is not in the folder. The name of the file.
    case missingFile(String)

    /// Something that is not a saved project is already where the folder would go.
    case destinationExists

    /// What was chosen is not a `.tinyclips` file, or a folder with exactly one in it.
    case notAProjectFile

    case staleEdit

    var errorDescription: String? {
        switch self {
        case .externalSource:
            return "A portable project must keep its media in the project folder. Import the missing video and save a new copy."
        case .missingFile(let name):
            return "\(name) is not in the project's folder. A .tinyclips file opens only next to the recordings it was saved with."
        case .destinationExists:
            return "There is already something with that name, and it is not a saved Tiny Clips project. Choose another name."
        case .notAProjectFile:
            return "Choose a .tinyclips file, or the folder that holds one."
        case .staleEdit:
            return "The project changed before this edit could be saved. Reopen it and try again."
        }
    }
}

struct StudioProjectPaths: Equatable, Sendable {
    var id: String
    var projectDirectory: URL
    var projectJSONURL: URL
    var screenURL: URL
    var cameraURL: URL?
    var eventsURL: URL
    var posterURL: URL
}

struct StudioProjectSummary: Equatable, Sendable {
    var id: String
    var name: String
    var createdAt: Date
    var lastOpenedAt: Date

    /// No video was ever exported from the project.
    var isDraft: Bool
    var isFlat: Bool
    var keepSources: Bool
    var sizeOnDisk: Int64
    var sourceExists: Bool

    /// Videos were exported and none of them is where it was saved any more. The project is
    /// then the only copy of the recording, as a draft is, and is treated as one: listed with
    /// the drafts, and never removed by cleanup.
    var exportMissing: Bool = false

    /// Whether cleanup may remove the project: a video exported from it is still where it was
    /// saved, the project is not pinned, it is not built around a video kept elsewhere, and it
    /// says when it was last opened. A project file without that time reads as the start of
    /// 1970, which would make the project the oldest there is and the first to go.
    var isRemovableByCleanup: Bool {
        !isDraft && !exportMissing && !keepSources && !isFlat
            && lastOpenedAt != Date(timeIntervalSince1970: 0)
    }
}

/// A project folder whose `project.json` is there and cannot be read.
extension StudioProjectSummary {
    var libraryName: String {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? "Untitled Recording" : trimmed
    }

    static func libraryProjects(from summaries: [StudioProjectSummary], matching search: String) -> [StudioProjectSummary] {
        let query = search.trimmingCharacters(in: .whitespacesAndNewlines)
        return summaries
            .filter { query.isEmpty || $0.libraryName.localizedStandardContains(query) }
            .sorted { $0.lastUsedAt == $1.lastUsedAt ? $0.id < $1.id : $0.lastUsedAt > $1.lastUsedAt }
    }

    /// When the project was last worked on: when it was last open, or failing that recorded.
    var lastUsedAt: Date { max(createdAt, lastOpenedAt) }

    /// Projects without an available export, including edits to an external video. Settings
    /// lists these as drafts even when the original video is already in the Clips Library.
    var isDraftOrLostItsExport: Bool { isDraft || exportMissing }

    /// The projects the Recent Captures menu lists beside the saved captures: those that hold
    /// the only copy of a recording that is still there to open. A project with an exported
    /// video is in that menu already, as the video, which opens its project.
    static func menuDrafts(from summaries: [StudioProjectSummary]) -> [StudioProjectSummary] {
        summaries.filter { $0.isDraftOrLostItsExport && !$0.isFlat && $0.sourceExists }
    }

    /// The projects an Open Recent menu lists: those that still have their recording, the one
    /// opened last first, without the project the menu belongs to. Projects opened at the same
    /// instant are in the order of their ids, so the menu does not shuffle.
    static func recent(
        from summaries: [StudioProjectSummary],
        excluding currentID: String?,
        limit: Int
    ) -> [StudioProjectSummary] {
        let listed = summaries
            .filter { $0.id != currentID && $0.sourceExists }
            .sorted { first, second in
                if first.lastOpenedAt != second.lastOpenedAt {
                    return first.lastOpenedAt > second.lastOpenedAt
                }
                return first.id < second.id
            }
        return Array(listed.prefix(max(0, limit)))
    }
}

struct StudioUnreadableProject: Equatable, Sendable {
    var id: String

    /// When the folder was made, which is when the recording started.
    var createdAt: Date
    var sizeOnDisk: Int64

    /// Whether a screen recording is in the folder to be saved out of it.
    var hasScreenRecording: Bool
}

struct StudioStorageSummary: Equatable, Sendable {
    var projectCount: Int
    var totalBytes: Int64
}

struct StudioProjectListing: Equatable, Sendable {
    var summaries: [StudioProjectSummary] = []
    var unreadable: [StudioUnreadableProject] = []
}

/// A prepared edit owns only its newly installed, uniquely named files. Once accepted by the
/// editor those files belong to the project, even while an undo removes them from the manifest.
class StudioPreparedTimelineBase: @unchecked Sendable {
    let base: StudioProject
    private let lock = NSLock()
    private var finish: ((_ rollback: Bool) -> Void)?

    fileprivate init(base: StudioProject, finish: @escaping (Bool) -> Void) {
        self.base = base
        self.finish = finish
    }

    func finalize() { complete(rollback: false) }
    func rollback() { complete(rollback: true) }

    private func complete(rollback: Bool) {
        let action = lock.withLock {
            defer { finish = nil }
            return finish
        }
        action?(rollback)
    }

    deinit { rollback() }
}

final class StudioPreparedTimelineImport: StudioPreparedTimelineBase, @unchecked Sendable {
    let source: StudioMediaSource
    let clip: StudioTimelineClip

    fileprivate init(base: StudioProject, source: StudioMediaSource, clip: StudioTimelineClip,
                     finish: @escaping (Bool) -> Void) {
        self.source = source
        self.clip = clip
        super.init(base: base, finish: finish)
    }
}

// MARK: - Store

final class StudioProjectStore {
    /// The store the app uses. One shared instance means its lock covers every caller.
    static let shared = StudioProjectStore()

    let rootURL: URL
    private let now: () -> Date
    private let fileManager: FileManager
    private let folderDateProvider: ((URL) -> Date?)?
    private let lock = NSLock()

    /// Projects open in an editor at this moment. Read and changed only with `lock` held.
    private var idsInUse: Set<String> = []
    /// A closed draft or a linked source can be in use by one or more in-flight imports.
    private var timelineImportUses: [UUID: Set<String>] = [:]

    init(
        rootURL: URL? = nil,
        now: @escaping () -> Date = Date.init,
        fileManager: FileManager = .default
    ) {
        self.rootURL = rootURL ?? Self.defaultRootURL(fileManager: fileManager)
        self.now = now
        self.fileManager = fileManager
        self.folderDateProvider = nil
    }

    init(
        rootURL: URL? = nil,
        now: @escaping () -> Date = Date.init,
        fileManager: FileManager = .default,
        folderDateProvider: @escaping (URL) -> Date?
    ) {
        self.rootURL = rootURL ?? Self.defaultRootURL(fileManager: fileManager)
        self.now = now
        self.fileManager = fileManager
        self.folderDateProvider = folderDateProvider
    }

    static func defaultRootURL(fileManager: FileManager = .default) -> URL {
        let base = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? fileManager.temporaryDirectory
        return base
            .appendingPathComponent("TinyClips", isDirectory: true)
            .appendingPathComponent("Projects", isDirectory: true)
    }

    func beginRecording() throws -> StudioProjectPaths {
        try withLock {
            let id = UUID().uuidString.lowercased()
            let paths = try pathsUnlocked(forID: id)
            try fileManager.createDirectory(at: paths.projectDirectory, withIntermediateDirectories: true)
            return paths
        }
    }

    func completeRecording(id: String, request: StudioProjectCreationRequest) throws -> StudioProject {
        try withLock {
            let project = try buildDefaultProject(id: id, request: request)
            return try saveUnlocked(project, updatingModifiedAt: false)
        }
    }

    func paths(forID id: String) throws -> StudioProjectPaths {
        try withLock {
            try pathsUnlocked(forID: id)
        }
    }

    func paths(for project: StudioProject) throws -> StudioProjectPaths {
        try withLock {
            try pathsUnlocked(for: project)
        }
    }

    func exists(id: String) -> Bool {
        guard Self.isValidProjectID(id) else { return false }
        return withLock {
            fileManager.fileExists(atPath: projectDirectoryUnchecked(for: id).path)
        }
    }

    func load(id: String) throws -> StudioProject {
        try withLock {
            try loadUnlocked(id: id)
        }
    }

    @discardableResult
    func save(_ project: StudioProject) throws -> StudioProject {
        try withLock {
            try saveUnlocked(project, updatingModifiedAt: true)
        }
    }

    /// A prepared edit to a closed project must not overwrite a newer save or bypass an
    /// editor that opened during preparation.
    func save(_ project: StudioProject, replacing expected: StudioProject) throws -> StudioProject? {
        try withLock {
            guard project.id == expected.id, !idsInUse.contains(project.id),
                  try loadUnlocked(id: project.id) == expected else { return nil }
            return try saveUnlocked(project, updatingModifiedAt: true)
        }
    }

    func saveEdits(_ state: StudioEditableState, id: String, expected: StudioEditableState) throws -> StudioProject {
        try withLock {
            let live = try loadUnlocked(id: id)
            guard StudioEditableState(project: live) == expected else { throw StudioProjectFolderError.staleEdit }
            return try saveUnlocked(state.applied(to: live), updatingModifiedAt: true)
        }
    }

    func delete(id: String) throws {
        try withLock {
            try deleteUnlocked(id: id)
        }
    }

    func deleteLibraryProject(id: String) throws {
        try withLock {
            let paths = try pathsUnlocked(forID: id)
            guard !idsInUse.contains(id) else { throw StudioLibraryActionError.inUse }
            // A recording that has not finished has a directory, but is not a library project.
            guard fileManager.fileExists(atPath: paths.projectJSONURL.path) else { throw CocoaError(.fileNoSuchFile) }
            try deleteUnlocked(id: id)
        }
    }

    /// Keep the saved folder's security scope alive until Finder has received the request.
    func withLibraryFolder<T>(id: String, perform: (URL) throws -> T) throws -> T {
        let paths = try paths(forID: id)
        guard fileManager.fileExists(atPath: paths.projectJSONURL.path) else { throw CocoaError(.fileNoSuchFile) }
        // A missing or unreadable destination record must not prevent opening the working folder.
        if let destination = try? savedDestination(id: id) {
            #if APPSTORE
            var stale = false
            if let bookmark = destination.bookmark,
               let folder = try? URL(resolvingBookmarkData: bookmark, options: [.withSecurityScope, .withoutUI],
                                     relativeTo: nil, bookmarkDataIsStale: &stale),
               folder.startAccessingSecurityScopedResource() {
                defer { folder.stopAccessingSecurityScopedResource() }
                if isAvailableFolder(folder) {
                    return try perform(folder)
                }
            }
            #else
            let folder = destination.file.deletingLastPathComponent()
            if isAvailableFolder(folder) { return try perform(folder) }
            #endif
        }
        return try perform(paths.projectDirectory)
    }

    private func isAvailableFolder(_ url: URL) -> Bool {
        var isDirectory: ObjCBool = false
        return fileManager.fileExists(atPath: url.path, isDirectory: &isDirectory)
            && isDirectory.boolValue && fileManager.isReadableFile(atPath: url.path)
    }

    @discardableResult
    func markOpened(id: String) throws -> StudioProject {
        try withLock {
            var project = try loadUnlocked(id: id)
            project.lastOpenedAt = now()
            return try saveUnlocked(project, updatingModifiedAt: false)
        }
    }

    /// Pins a project against automatic cleanup, or lets go of it again. It is written into the
    /// project on disk at once and is not one of the editor's edits: nothing undoes it.
    @discardableResult
    func setKeepSources(id: String, keepSources: Bool) throws -> StudioProject {
        try withLock {
            var project = try loadUnlocked(id: id)
            guard project.keepSources != keepSources else { return project }
            project.keepSources = keepSources
            return try saveUnlocked(project, updatingModifiedAt: false)
        }
    }

    func listSummaries() throws -> [StudioProjectSummary] {
        try withLock {
            try listSummariesUnlocked()
        }
    }

    /// Both sections of the Library, from the same directory walk and decode of each project.
    func listLibraryProjects() throws -> StudioProjectListing {
        try withLock {
            try listProjectsUnlocked(includingSummaries: true, includingUnreadable: true)
        }
    }

    /// The project folders whose `project.json` is there and cannot be read: damaged, or written
    /// by a newer version. `listSummaries()` leaves them out and cleanup leaves them alone, so
    /// this list is the only place they show up.
    func listUnreadableProjects() throws -> [StudioUnreadableProject] {
        try withLock {
            try listProjectsUnlocked(includingSummaries: false, includingUnreadable: true).unreadable
        }
    }

    /// The screen recording a project keeps in its own folder, or nil when it has none there:
    /// the project is built around a video kept elsewhere, the file is gone, or there is no
    /// such project. Without readable metadata only an unambiguous conventional name is used.
    func screenRecordingURL(id: String) -> URL? {
        guard Self.isValidProjectID(id) else { return nil }
        return withLock {
            let directory = projectDirectoryUnchecked(for: id)
            return recoveryRecording(in: directory, project: try? loadUnlocked(id: id))
        }
    }

    private func recoveryRecording(in directory: URL, project: StudioProject?) -> URL? {
        guard (try? directory.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == false else { return nil }
        let names: [String]
        if let source = project?.sources.screen {
            guard !source.external, StudioJSON.isPlainFileName(source.file) else { return nil }
            names = [source.file]
        } else {
            names = ["screen.mp4", "screen.mov"]
        }
        let files = names.map { directory.appendingPathComponent($0) }.filter {
            ["mp4", "mov"].contains($0.pathExtension.lowercased()) && (try? isPlainFile($0)) == true
        }
        return files.count == 1 ? files[0] : nil
    }

    func storageSummary() throws -> StudioStorageSummary {
        try withLock {
            let summaries = try listSummariesUnlocked()
            return StudioStorageSummary(
                projectCount: summaries.count,
                totalBytes: summaries.reduce(Int64(0)) { $0 + $1.sizeOnDisk }
            )
        }
    }

    @discardableResult
    func recordExport(id: String, path: String) throws -> StudioProject {
        try withLock {
            var targetProject = try loadUnlocked(id: id)
            let normalized = normalizedPath(path)
            for summary in try listSummariesUnlocked() {
                guard summary.id != id else { continue }
                var project = try loadUnlocked(id: summary.id)
                let oldCount = project.exports.count
                project.exports.removeAll { normalizedPath($0.path) == normalized }
                if project.exports.count != oldCount {
                    _ = try saveUnlocked(project, updatingModifiedAt: true)
                }
            }

            targetProject.exports.removeAll { normalizedPath($0.path) == normalized }
            targetProject.exports.append(StudioExport(
                path: path,
                exportedAt: now(),
                bytes: fileSize(of: URL(fileURLWithPath: path).standardizedFileURL)
            ))
            return try saveUnlocked(targetProject, updatingModifiedAt: true)
        }
    }

    func findProjectID(exportedPath: String) throws -> String? {
        try withLock {
            let needle = normalizedPath(exportedPath)
            for summary in try listSummariesUnlocked() {
                let project = try loadUnlocked(id: summary.id)
                if project.exports.contains(where: { normalizedPath($0.path) == needle }) {
                    return project.id
                }
            }
            return nil
        }
    }

    /// Every exported video the store knows about, keyed by `exportKey(forPath:)`, with the id of
    /// the project it came from. Use this instead of `findProjectID` when checking many videos.
    func exportedPathIndex() throws -> [String: String] {
        try withLock {
            var index: [String: String] = [:]
            for summary in try listSummariesUnlocked() {
                guard let project = try? loadUnlocked(id: summary.id) else { continue }
                for export in project.exports {
                    index[normalizedPath(export.path)] = project.id
                }
            }
            return index
        }
    }

    /// The form of a path that export links are compared in.
    static func exportKey(forPath path: String) -> String {
        URL(fileURLWithPath: path).standardizedFileURL.path.lowercased()
    }

    @discardableResult
    func updateExportPath(from oldPath: String, to newPath: String) throws -> Bool {
        try withLock {
            let oldNeedle = normalizedPath(oldPath)
            let newNeedle = normalizedPath(newPath)
            let movedIdentity = try? videoSourceIdentity(at: URL(fileURLWithPath: newPath))
            var changed = false
            for summary in try listSummariesUnlocked() {
                var project = try loadUnlocked(id: summary.id)
                var movedSource = project.sources.screen.external
                    && normalizedPath(project.sources.screen.file) == oldNeedle
                if var identity = sourceIdentity(for: project), identity.path == oldNeedle {
                    identity.path = newNeedle
                    if let movedIdentity, identity.matches(movedIdentity) {
                        try saveSourceIdentity(movedIdentity, for: project)
                        changed = true
                    } else {
                        // The file that moved was a replacement, not this draft's source.
                        movedSource = false
                    }
                }
                if movedSource {
                    project.sources.screen.file = newPath
                }
                var matchedExport: StudioExport?
                var exports: [StudioExport] = []
                for export in project.exports {
                    let normalized = normalizedPath(export.path)
                    if normalized == oldNeedle {
                        matchedExport = StudioExport(
                            path: newPath,
                            exportedAt: export.exportedAt,
                            bytes: export.bytes,
                            extra: export.extra
                        )
                    } else if normalized != newNeedle {
                        exports.append(export)
                    }
                }
                if let matchedExport {
                    exports.append(matchedExport)
                    project.exports = exports
                    _ = try saveUnlocked(project, updatingModifiedAt: true)
                    changed = true
                } else if movedSource || exports.count != project.exports.count {
                    project.exports = exports
                    _ = try saveUnlocked(project, updatingModifiedAt: true)
                    changed = true
                }
            }
            return changed
        }
    }

    @discardableResult
    func removeExportPath(_ path: String) throws -> Bool {
        try withLock {
            let needle = normalizedPath(path)
            var changed = false
            for summary in try listSummariesUnlocked() {
                var project = try loadUnlocked(id: summary.id)
                let oldCount = project.exports.count
                project.exports.removeAll { normalizedPath($0.path) == needle }
                if project.exports.count != oldCount {
                    _ = try saveUnlocked(project, updatingModifiedAt: true)
                    changed = true
                }
            }
            return changed
        }
    }

    /// Preparation can copy a large video off the main thread. Nothing becomes a project until
    /// commit, where the caller can check its current authorization without an intervening await.
    final class PreparedFlatVideo: @unchecked Sendable {
        fileprivate let request: StudioFlatProjectRequest
        fileprivate let source: StudioVideoSourceIdentity?
        fileprivate let staging: URL?
        private let discard: () -> Void

        fileprivate init(_ request: StudioFlatProjectRequest, source: StudioVideoSourceIdentity?,
                         staging: URL? = nil, discard: @escaping () -> Void = {}) {
            self.request = request
            self.source = source
            self.staging = staging
            self.discard = discard
        }

        deinit { discard() }
    }

    func prepareFlatVideo(request: StudioFlatProjectRequest) throws -> PreparedFlatVideo {
        _ = try StudioJSON.requirePositive(request.width, "sources.screen.width")
        _ = try StudioJSON.requirePositive(request.height, "sources.screen.height")
        _ = try StudioJSON.requireNonNegative(request.duration, "sources.screen.duration")
        let source = try importIdentity(for: request)
        guard request.copyVideo else { return PreparedFlatVideo(request, source: source) }
        guard let source else { throw CocoaError(.fileReadUnknown) }
        let staging = try beginRecording()
        beginUse(id: staging.id)
        let prepared = PreparedFlatVideo(request, source: source, staging: staging.projectDirectory) {
            try? self.fileManager.removeItem(at: staging.projectDirectory)
            self.endUse(id: staging.id)
        }
        let file = staging.projectDirectory.appendingPathComponent(Self.copiedScreenFileName(for: request.videoURL))
        try fileManager.copyItem(at: request.videoURL.resolvingSymlinksInPath(), to: file)
        guard try isPlainFile(file), try videoSourceIdentity(at: request.videoURL) == source else {
            throw CocoaError(.fileReadUnknown)
        }
        return prepared
    }

    func getOrCreateFlatProject(
        request: StudioFlatProjectRequest,
        validateAccess: () throws -> Void = {}
    ) throws -> StudioProject {
        try validateAccess()
        if let owned = try existingOwnedFlatVideo(request: request, validateAccess: validateAccess) { return owned }
        return try commitFlatVideo(prepareFlatVideo(request: request), validateAccess: validateAccess)
    }

    func existingOwnedFlatVideo(request: StudioFlatProjectRequest, preferredID: String? = nil,
                                validateAccess: () throws -> Void) throws -> StudioProject? {
        guard request.copyVideo else { return nil }
        return try withLock {
            let source = try importIdentity(for: request)
            let match = try flatProjectUnlocked(request: request, source: source, preferredID: preferredID)
            if let source, try videoSourceIdentity(at: request.videoURL) != source {
                throw CocoaError(.fileReadUnknown)
            }
            try validateAccess()
            return match?.sources.screen.external == false ? match : nil
        }
    }

    func commitFlatVideo(_ prepared: PreparedFlatVideo, preferredID: String? = nil,
                         validateAccess: () throws -> Void) throws -> StudioProject {
        defer { withExtendedLifetime(prepared) {} } // Discard staging only after releasing the store lock.
        let request = prepared.request
        let source = prepared.source
        let screenFile = request.copyVideo ? Self.copiedScreenFileName(for: request.videoURL) : request.videoURL.path
        return try withLock {
            try validateAccess()
            let existing = try flatProjectUnlocked(request: request, source: source, preferredID: preferredID)
            if let source, try videoSourceIdentity(at: request.videoURL) != source {
                throw CocoaError(.fileReadUnknown)
            }
            try validateAccess()
            if let existing {
                if request.copyVideo && existing.sources.screen.external {
                    // Re-read under this lock, not from preparation: even an editor opened or
                    // changed during the copy must keep its identity, edits and bookkeeping.
                    guard let staging = prepared.staging, let source else { throw CocoaError(.fileReadUnknown) }
                    var promoted = existing
                    let directory = projectDirectoryUnchecked(for: existing.id)
                    let file = directory.appendingPathComponent(screenFile)
                    guard !ownedFiles(existing).contains(where: { $0.caseInsensitiveCompare(screenFile) == .orderedSame })
                    else { throw StudioProjectFolderError.destinationExists }
                    try fileManager.moveItem(at: staging.appendingPathComponent(screenFile), to: file)
                    do {
                        try saveSourceIdentity(source, for: existing)
                        promoted.sources.screen.external = false
                        promoted.sources.screen.file = screenFile
                        return try saveUnlocked(promoted, updatingModifiedAt: false)
                    } catch {
                        try? fileManager.removeItem(at: file)
                        throw error
                    }
                }
                if existing.sources.screen.external, let source, sourceIdentity(for: existing) == nil {
                    try saveSourceIdentity(source, for: existing)
                }
                return existing
            }

            let id = UUID().uuidString.lowercased()
            let date = now()
            let directory = projectDirectoryUnchecked(for: id)
            let project = StudioProject(
                id: id,
                name: request.name.isEmpty ? request.videoURL.deletingPathExtension().lastPathComponent : request.name,
                createdAt: date,
                modifiedAt: date,
                lastOpenedAt: date,
                app: StudioAppInfo(platform: "macos", version: request.appVersion),
                sources: StudioSources(
                    screen: StudioScreenSource(
                        file: screenFile,
                        width: request.width,
                        height: request.height,
                        frameRate: request.frameRate,
                        duration: request.duration,
                        external: !request.copyVideo
                    ),
                    camera: nil,
                    events: nil
                ),
                scenes: [StudioScene(start: 0, layout: .screen)]
            )
            if let staging = prepared.staging {
                try fileManager.moveItem(at: staging, to: directory)
            } else {
                try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
            }
            do {
                if let source { try saveSourceIdentity(source, for: project) }
                return try saveUnlocked(project, updatingModifiedAt: false)
            } catch {
                try? fileManager.removeItem(at: directory)
                throw error
            }
        }
    }

    private func importIdentity(for request: StudioFlatProjectRequest) throws -> StudioVideoSourceIdentity? {
        // Offline reference-only requests historically match by path. UI opens always have a
        // version from before AVFoundation read dimensions/duration; never use those for a replacement.
        if !request.copyVideo && request.identityBeforeReadingMetadata == nil {
            return try? videoSourceIdentity(at: request.videoURL)
        }
        let current = try videoSourceIdentity(at: request.videoURL)
        if let expected = request.identityBeforeReadingMetadata, current != expected { throw CocoaError(.fileReadUnknown) }
        return current
    }

    func videoSourceIdentity(at url: URL) throws -> StudioVideoSourceIdentity {
        let attributes = try fileManager.attributesOfItem(atPath: url.resolvingSymlinksInPath().path)
        guard attributes[.type] as? FileAttributeType == .typeRegular,
              let size = attributes[.size] as? NSNumber,
              let modifiedAt = attributes[.modificationDate] as? Date else {
            throw CocoaError(.fileReadUnknown)
        }
        return StudioVideoSourceIdentity(path: normalizedPath(url.path), size: size.uint64Value, modifiedAt: modifiedAt,
                                         volume: (attributes[.systemNumber] as? NSNumber)?.uint64Value,
                                         fileNumber: (attributes[.systemFileNumber] as? NSNumber)?.uint64Value)
    }

    private static let sourceRecord = "imported-video-source.json"

    private func sourceIdentity(for project: StudioProject) -> StudioVideoSourceIdentity? {
        guard !ownedFiles(project).contains(where: { $0.caseInsensitiveCompare(Self.sourceRecord) == .orderedSame })
        else { return nil }
        let url = projectDirectoryUnchecked(for: project.id).appendingPathComponent(Self.sourceRecord)
        guard (try? isPlainFile(url)) == true, let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(StudioVideoSourceIdentity.self, from: data)
    }

    private func saveSourceIdentity(_ source: StudioVideoSourceIdentity, for project: StudioProject) throws {
        let url = projectDirectoryUnchecked(for: project.id).appendingPathComponent(Self.sourceRecord)
        try JSONEncoder().encode(source).write(to: url, options: .atomic)
    }

    private func flatProjectUnlocked(request: StudioFlatProjectRequest, source: StudioVideoSourceIdentity?,
                                     preferredID: String?) throws -> StudioProject? {
        guard fileManager.fileExists(atPath: rootURL.path) else { return nil }
        let needle = normalizedPath(request.videoURL.path)
        var best: StudioProject?
        var bestRank = -1
        // Matching never needs a recursive size calculation or export checks. In particular,
        // the final authorization/identity check runs on the editor's actor.
        let directories = try fileManager.contentsOfDirectory(at: rootURL, includingPropertiesForKeys: [.isSymbolicLinkKey],
                                                              options: [.skipsHiddenFiles])
        for directory in directories.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }) {
            let id = directory.lastPathComponent
            guard Self.isValidProjectID(id), (try? directory.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == false,
                  let project = try? loadUnlocked(id: id) else { continue }
            let screen = project.sources.screen
            guard !ownedFiles(project).contains(where: { $0.caseInsensitiveCompare(Self.sourceRecord) == .orderedSame })
            else { continue }
            if !screen.external {
                // A portable copy imported as its own project has no local import receipt.
                guard let source, screen.file == Self.copiedScreenFileName(for: request.videoURL),
                      sourceIdentity(for: project)?.matches(source) == true,
                      (try? isPlainFile(directory.appendingPathComponent(screen.file))) == true else { continue }
            } else if normalizedPath(screen.file) == needle {
                if let source {
                    if let recorded = sourceIdentity(for: project) {
                        guard recorded.matches(source) else { continue }
                    } else {
                        // Pre-identity references: never attach obviously different footage or
                        // reinterpret a damaged receipt as permission to match by path alone.
                        let record = projectDirectoryUnchecked(for: project.id).appendingPathComponent(Self.sourceRecord)
                        guard !fileManager.fileExists(atPath: record.path),
                              screen.width == request.width, screen.height == request.height,
                              abs(screen.duration - request.duration) < 0.001,
                              source.modifiedAt < project.createdAt.addingTimeInterval(1) else { continue }
                    }
                }
            } else {
                continue
            }
            // Legacy installations can have two separately edited drafts. Never merge them.
            // Keep the frontmost matching session, then any open match; otherwise prefer a
            // verified copy over a reference. Sorted IDs break ties deterministically.
            let rank = (project.id == preferredID ? 4 : 0) + (idsInUse.contains(project.id) ? 2 : 0)
                + (screen.external ? 0 : 1)
            if rank > bestRank {
                best = project
                bestRank = rank
            }
        }
        return best
    }

    // MARK: - Timeline media

    func prepareTimelineBase(destination: StudioProject) async throws -> StudioPreparedTimelineBase {
        let (base, finish) = try await stageTimelineEdit(destination) { base, _ in base }
        return StudioPreparedTimelineBase(base: base, finish: finish)
    }

    /// `destination` and `recording` are editor snapshots, not necessarily what's on disk.
    /// Passing a linked single-recording project imports its layers, never its rendered export.
    func prepareTimelineImport(destination: StudioProject, videoURL: URL, recording: StudioProject? = nil,
                               expectedVideo: StudioVideoSourceIdentity? = nil)
        async throws -> StudioPreparedTimelineImport {
        if let recording, recording.schemaVersion != 1 {
            throw StudioProjectError.invalidProject("Choose a single recording or import its exported video instead.")
        }
        let ((base, source, clip), finish) = try await stageTimelineEdit(destination, recordingID: recording?.id) { base, staging in
            let (source, clip) = try await self.stageTimelineSource(videoURL: videoURL, recording: recording,
                                                                    destination: base, staging: staging, expected: expectedVideo)
            _ = try StudioTimelineMap(timeline: StudioTimeline(clips: [clip]), sources: [source])
            return (base, source, clip)
        }
        return StudioPreparedTimelineImport(base: base, source: source, clip: clip, finish: finish)
    }

    private func stageTimelineEdit<T>(_ destination: StudioProject, recordingID: String? = nil,
                                      prepare: (StudioProject, URL) async throws -> T) async throws -> (T, (Bool) -> Void) {
        try Task.checkCancellation()
        let token = UUID()
        withLock { timelineImportUses[token] = Set([destination.id, recordingID].compactMap { $0 }) }
        var handedOff = false
        defer { if !handedOff { withLock { timelineImportUses[token] = nil } } }
        let directory = try paths(for: destination).projectDirectory
        guard try directory.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink == false,
              try isPlainFile(directory.appendingPathComponent("project.json")) else {
            throw StudioProjectFolderError.notAProjectFile
        }
        let staging = directory.appendingPathComponent(".import-\(UUID().uuidString)", isDirectory: true)
        try fileManager.createDirectory(at: staging, withIntermediateDirectories: false)
        defer { try? fileManager.removeItem(at: staging) }

        let base = try await upgradedTimelineBase(destination, staging: staging)
        guard let timeline = base.timeline, let registry = base.mediaSources else { throw StudioProjectFolderError.notAProjectFile }
        _ = try StudioTimelineMap(timeline: timeline, sources: registry)
        _ = try folderAssets(for: base, in: directory, staging: staging)
        let value = try await prepare(base, staging)
        let names = try fileManager.contentsOfDirectory(atPath: staging.path)
        let versions = try withLock {
            try Task.checkCancellation()
            var installed: [URL] = []
            do {
                for name in names {
                    let file = directory.appendingPathComponent(name)
                    try fileManager.moveItem(at: staging.appendingPathComponent(name), to: file)
                    installed.append(file)
                }
                try Task.checkCancellation()
                return try installed.map { ($0, try videoSourceIdentity(at: $0)) }
            } catch {
                for file in installed { try? fileManager.removeItem(at: file) }
                throw error
            }
        }
        handedOff = true
        return (value, { rollback in
            self.withLock {
                defer { self.timelineImportUses[token] = nil }
                // Be conservative if a newer manifest already adopted a file or is unreadable.
                guard rollback, !versions.isEmpty, let current = try? self.loadUnlocked(id: destination.id) else { return }
                let inUse = Set(self.ownedFiles(current).map { $0.lowercased() })
                for (file, version) in versions where !inUse.contains(file.lastPathComponent.lowercased()) {
                    guard (try? self.isPlainFile(file)) == true, (try? self.videoSourceIdentity(at: file)) == version else { continue }
                    try? self.fileManager.removeItem(at: file)
                }
            }
        })
    }

    private func upgradedTimelineBase(_ destination: StudioProject, staging: URL) async throws -> StudioProject {
        var base = StudioTimelineEditing.timelineProject(from: destination)
        if destination.schemaVersion == 1 {
            let flat = isKnownFlat(destination)
            var sources = destination.sources
            if flat {
                let original = try paths(for: destination).screenURL
                if sources.screen.external {
                    sources.screen = try await stageScreen(original, in: staging, metadata: sources.screen, flat: true,
                                                           expected: sourceIdentity(for: destination))
                } else {
                    guard (try? isPlainFile(original)) == true else { throw StudioProjectFolderError.missingFile(sources.screen.file) }
                    sources.screen = try await Self.measuredScreen(at: original, metadata: sources.screen, flat: true)
                }
                sources.camera = nil
                sources.events = nil
                sources.extra["mediaKind"] = .string("flatVideo")
                base.timeline?.clips[0].sourceEnd = sources.screen.duration
            } else {
                sources.events = try eventsName(in: destination)
            }
            base.sources = sources
            // V1 used display dimensions as its canvas anchor. The registry needs coded
            // dimensions and rotation, but conversion (and undo back to v1) must not resize it.
            base.sources.screen = destination.sources.screen
            base.sources.screen.file = sources.screen.file
            base.sources.screen.external = false
            base.mediaSources?[0].sources = sources
            base.mediaSources?[0].kind = flat ? .flatVideo : .nativeRecording
        }
        return base
    }

    private func stageTimelineSource(videoURL: URL, recording: StudioProject?, destination: StudioProject, staging: URL,
                                     expected: StudioVideoSourceIdentity?)
        async throws -> (StudioMediaSource, StudioTimelineClip) {
        let sourceID = UUID().uuidString.lowercased()
        let clipID = UUID().uuidString.lowercased()
        var source: StudioMediaSource
        var clip: StudioTimelineClip
        if let recording {
            let paths = try paths(for: recording)
            let flat = isKnownFlat(recording)
            guard try paths.projectDirectory.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink == false else {
                throw StudioProjectFolderError.notAProjectFile
            }
            var sources = recording.sources
            sources.screen = try await stageScreen(paths.screenURL, in: staging, metadata: sources.screen, flat: flat,
                                                   expected: sources.screen.external ? sourceIdentity(for: recording) : nil)
            sources.camera = nil
            sources.events = nil
            if !flat, var camera = recording.sources.camera, let url = paths.cameraURL {
                let file = try stageFile(url, in: staging)
                let measured = try await Self.probeVideo(at: file)
                camera.file = file.lastPathComponent
                camera.width = measured.width
                camera.height = measured.height
                camera.duration = measured.duration
                sources.camera = camera
            }
            if !flat, let name = try eventsName(in: recording) {
                let file = try stageFile(paths.projectDirectory.appendingPathComponent(name), in: staging)
                _ = try StudioJSON.makeDecoder().decode(StudioEvents.self, from: Data(contentsOf: file))
                sources.events = file.lastPathComponent
            }
            source = StudioMediaSource(id: sourceID, kind: flat ? .flatVideo : .nativeRecording, sources: sources)
            if let image = validBackgroundImage(recording.canvas.background.image) {
                let file = try stageFile(paths.projectDirectory.appendingPathComponent(image), in: staging)
                guard let imageSource = CGImageSourceCreateWithURL(file as CFURL, nil),
                      CGImageSourceCreateThumbnailAtIndex(imageSource, 0, [
                        kCGImageSourceCreateThumbnailFromImageAlways: true,
                        kCGImageSourceThumbnailMaxPixelSize: 16
                      ] as CFDictionary) != nil else {
                    throw StudioProjectError.invalidProject("The recording's background image could not be read.")
                }
                // The canvas is project-wide. Keep the recording's referenced image without
                // replacing the destination's canvas or changing its preview at a cut.
                source.extra[Self.recordingBackgroundImage] = .string(file.lastPathComponent)
            }
            var snapshot = recording
            snapshot.sources = sources
            // Snapshot every v1 clip-local layer with the same conversion used for the first clip.
            guard var converted = StudioTimelineEditing.timelineProject(from: snapshot).timeline?.clips.first else {
                throw StudioProjectFolderError.notAProjectFile
            }
            converted.id = clipID
            converted.sourceId = sourceID
            clip = converted
        } else {
            let screen = try await stageScreen(videoURL, in: staging, metadata: nil, flat: true, expected: expected)
            source = StudioMediaSource(id: sourceID, kind: .flatVideo, sources: StudioSources(screen: screen))
            var style = destination.screen
            style.crop = nil
            clip = StudioTimelineClip(id: clipID, sourceId: sourceID, name: videoURL.deletingPathExtension().lastPathComponent,
                                      sourceStart: 0, sourceEnd: screen.duration, screen: style,
                                      scenes: [StudioScene(layout: .screen)], zooms: [], audio: StudioAudio(),
                                      overlays: StudioOverlays(branding: destination.overlays.branding), captions: StudioCaptions())
        }

        return (source, clip)
    }

    private static let recordingBackgroundImage = "recordingBackgroundImage"

    func isKnownFlat(_ project: StudioProject) -> Bool {
        guard Self.isValidProjectID(project.id), project.schemaVersion == 1 else { return false }
        return project.sources.screen.external || project.sources.extra["mediaKind"] == .string("flatVideo")
            || sourceIdentity(for: project) != nil
    }

    private func eventsName(in project: StudioProject) throws -> String? {
        let paths = try paths(for: project)
        guard fileManager.fileExists(atPath: paths.eventsURL.path) else {
            // Old capture manifests name the optional default file even when there are no events.
            if let name = project.sources.events, name != "events.json" { throw StudioProjectFolderError.missingFile(name) }
            return nil
        }
        return paths.eventsURL.lastPathComponent
    }

    private func stageFile(_ url: URL, in staging: URL, external: Bool = false, expected: StudioVideoSourceIdentity? = nil) throws -> URL {
        try Task.checkCancellation()
        guard url.isFileURL else { throw CocoaError(.fileReadUnsupportedScheme) }
        let input = external ? url.resolvingSymlinksInPath() : url
        guard (try? isPlainFile(input)) == true else { throw StudioProjectFolderError.missingFile(url.lastPathComponent) }
        if !external { try Self.validateAssetName(url.lastPathComponent) }
        let identity = try videoSourceIdentity(at: url)
        if let expected, !expected.matches(identity) { throw CocoaError(.fileReadUnknown) }
        let ext = url.pathExtension.lowercased()
        let safeExtension = ext.count <= 12 && ext.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber) }
            && ext != Self.projectFileExtension
        let output = staging.appendingPathComponent("media-\(UUID().uuidString.lowercased())")
            .appendingPathExtension(safeExtension && !ext.isEmpty ? ext : "bin")
        if expected != nil {
            try copyAuthorizedFile(input, to: output, version: identity)
        } else {
            try fileManager.copyItem(at: input, to: output)
        }
        guard try isPlainFile(output), try videoSourceIdentity(at: url) == identity else { throw CocoaError(.fileReadUnknown) }
        try Task.checkCancellation()
        return output
    }

    /// A path (including a symlink target) can change between preflight and copy. When the
    /// caller authorized a specific Library file, verify and read the same open descriptor.
    private func copyAuthorizedFile(_ input: URL, to output: URL, version: StudioVideoSourceIdentity) throws {
        let fd = Darwin.open(input.path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK)
        guard fd >= 0 else { throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
        let reader = FileHandle(fileDescriptor: fd, closeOnDealloc: true)
        var before = stat()
        guard fstat(fd, &before) == 0, before.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG),
              before.st_ino == version.fileNumber, UInt64(truncatingIfNeeded: before.st_dev) == version.volume,
              before.st_size >= 0, UInt64(before.st_size) == version.size else { throw CocoaError(.fileReadUnknown) }

        // APFS can make a private copy immediately. Across volumes stream in cancellable chunks.
        if fclonefileat(fd, AT_FDCWD, output.path, 0) != 0 {
            let target = Darwin.open(output.path, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, 0o600)
            guard target >= 0 else { throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
            let writer = FileHandle(fileDescriptor: target, closeOnDealloc: true)
            while let data = try reader.read(upToCount: 1024 * 1024), !data.isEmpty {
                try Task.checkCancellation()
                try writer.write(contentsOf: data)
            }
            try writer.close()
        }
        var after = stat()
        guard fstat(fd, &after) == 0, after.st_ctimespec.tv_sec == before.st_ctimespec.tv_sec,
              after.st_ctimespec.tv_nsec == before.st_ctimespec.tv_nsec else { throw CocoaError(.fileReadUnknown) }
        try reader.close()
    }

    private func stageScreen(_ input: URL, in staging: URL, metadata: StudioScreenSource?, flat: Bool,
                             expected: StudioVideoSourceIdentity? = nil)
        async throws -> StudioScreenSource {
        let file = try stageFile(input, in: staging, external: metadata?.external ?? true, expected: expected)
        return try await Self.measuredScreen(at: file, metadata: metadata, flat: flat)
    }

    private static func measuredScreen(at file: URL, metadata: StudioScreenSource?, flat: Bool) async throws -> StudioScreenSource {
        var screen = try await probeVideo(at: file)
        screen.audioTracks = metadata?.audioTracks
        screen.extra = metadata?.extra ?? [:]
        // Capture manifests know the recorder's target rate even for a variable-rate screen.
        if !flat, let rate = metadata?.frameRate, rate.isFinite, rate > 0 { screen.frameRate = rate }
        return screen
    }

    private static func probeVideo(at url: URL) async throws -> StudioScreenSource {
        let asset = AVURLAsset(url: url, options: [
            AVURLAssetReferenceRestrictionsKey: AVAssetReferenceRestrictions.forbidAll.rawValue,
            AVURLAssetPreferPreciseDurationAndTimingKey: true
        ])
        let (playable, composable, duration) = try await asset.load(.isPlayable, .isComposable, .duration)
        guard playable, composable, duration.seconds.isFinite, duration.seconds > 0,
              let track = try await asset.loadTracks(withMediaType: .video).first else {
            throw StudioProjectError.invalidProject("Choose a playable video file.")
        }
        let (size, transform, rate) = try await track.load(.naturalSize, .preferredTransform, .nominalFrameRate)
        guard let width = Int(exactly: size.width.rounded()), width > 0,
              let height = Int(exactly: size.height.rounded()), height > 0 else {
            throw StudioProjectError.invalidProject("The video has no usable picture.")
        }
        // A plausible header isn't enough: make sure the local decoder can actually read a frame.
        let generator = AVAssetImageGenerator(asset: asset)
        generator.maximumSize = CGSize(width: 32, height: 32)
        _ = try await generator.image(at: .zero)
        try Task.checkCancellation()
        let angle = (atan2(transform.b, transform.a) * 180 / .pi).rounded()
        return StudioScreenSource(file: url.lastPathComponent, width: width, height: height,
                                  frameRate: rate.isFinite && rate > 0 ? Double(rate) : 30,
                                  duration: duration.seconds, rotationDegrees: (angle + 360).truncatingRemainder(dividingBy: 360))
    }

    func loadEvents(id: String) throws -> StudioEvents {
        try withLock {
            let url = try pathsUnlocked(forID: id).eventsURL
            guard fileManager.fileExists(atPath: url.path) else { return StudioEvents() }
            let data = try Data(contentsOf: url)
            return try StudioJSON.makeDecoder().decode(StudioEvents.self, from: data)
        }
    }

    func saveEvents(_ events: StudioEvents, id: String) throws {
        try withLock {
            let paths = try pathsUnlocked(forID: id)
            try fileManager.createDirectory(at: paths.projectDirectory, withIntermediateDirectories: true)
            let data = try StudioJSON.makeEncoder().encode(events)
            try data.write(to: paths.eventsURL, options: .atomic)
        }
    }

    @discardableResult
    func cleanup(options: StudioCleanupOptions = StudioCleanupOptions(), inUseProjectIDs: Set<String> = []) throws -> [String] {
        try withLock {
            let inUse = Set(inUseProjectIDs.filter(Self.isValidProjectID)).union(timelineImportUses.values.flatMap { $0 })
            var candidateIDs = StudioCleanupPolicy.plan(
                summaries: try listSummariesUnlocked(),
                currentDate: now(),
                options: options,
                inUseProjectIDs: inUse
            )
            if options.retentionDays > 0 || options.sizeCapBytes > 0 {
                candidateIDs.append(contentsOf: try unfinishedRecordingIDsUnlocked(inUseProjectIDs: inUse))
            }

            var deletedIDs: [String] = []
            for id in candidateIDs where !deletedIDs.contains(id) {
                // Looked at now, with the store locked. The list above was made before the
                // cleanup started, and an editor may have opened the project since.
                guard !idsInUse.contains(id) else { continue }
                do {
                    try deleteUnlocked(id: id)
                    deletedIDs.append(id)
                } catch {
                    continue
                }
            }
            return deletedIDs
        }
    }

    // MARK: - Project Folders

    /// The extension of the file that carries the metadata of a project saved as a folder
    /// (section 14 of the project format).
    static let projectFileExtension = "tinyclips"

    /// A name the folder of a saved project can have: the project's, without what a path cannot
    /// hold, and not one that would hide the folder.
    static func folderName(for name: String) -> String {
        var cleaned = name
            .components(separatedBy: CharacterSet(charactersIn: "/:\\").union(.newlines))
            .joined(separator: "-")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        while cleaned.hasPrefix(".") {
            cleaned.removeFirst()
        }
        return cleaned.isEmpty ? "Tiny Clips Project" : cleaned
    }

    /// The largest `.tinyclips` file that is read. A project's own is a few kilobytes.
    private static let largestProjectFile = 16 << 20

    struct SavedDestination: Codable {
        var file: URL
        var bookmark: Data?
    }

    func savedDestination(id: String) throws -> SavedDestination? {
        let record = try destinationRecordURL(id: id)
        guard fileManager.fileExists(atPath: record.path) else { return nil }
        let destination = try JSONDecoder().decode(SavedDestination.self, from: Data(contentsOf: record))
        guard destination.file.isFileURL,
              destination.file.pathExtension.lowercased() == Self.projectFileExtension else {
            throw CocoaError(.fileReadCorruptFile)
        }
        return destination
    }

    /// Returns whether this is the project's first saved destination, including a first Save As.
    @discardableResult
    func rememberDestination(id: String, file: URL, bookmark: Data? = nil) throws -> Bool {
        guard file.isFileURL, file.pathExtension.lowercased() == Self.projectFileExtension else {
            throw CocoaError(.fileWriteInvalidFileName)
        }
        let record = try destinationRecordURL(id: id)
        let firstSave = !fileManager.fileExists(atPath: record.path)
        try JSONEncoder().encode(SavedDestination(file: file, bookmark: bookmark)).write(to: record, options: .atomic)
        return firstSave
    }

    private func destinationRecordURL(id: String) throws -> URL {
        guard !ownedFiles(try load(id: id)).contains(where: {
            $0.caseInsensitiveCompare("saved-project.json") == .orderedSame
        }) else {
            throw StudioProjectFolderError.destinationExists
        }
        return try paths(forID: id).projectDirectory.appendingPathComponent("saved-project.json")
    }

    private static let linkedSaveLock = NSLock()

    /// The name a video kept elsewhere gets in a saved project folder: `screen`, with the
    /// video's own extension so it opens as the same kind of movie.
    static func copiedScreenFileName(for video: URL) -> String {
        let ext = video.pathExtension.lowercased()
        let plain = !ext.isEmpty && ext.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber) }
        return "screen." + (plain ? ext : "mp4")
    }

    /// One inventory for Save As, linked Save, opening a portable folder, and ownership checks.
    /// Include the entire registry, not just today's clips: another edit can reuse a source.
    private func assetNames(_ project: StudioProject) -> (required: Set<String>, optional: Set<String>) {
        var required: Set<String> = [project.sources.screen.file]
        var optional: Set<String> = [project.sources.events ?? "events.json", "poster.jpg"]
        if let camera = project.sources.camera { required.insert(camera.file) }
        if let image = validBackgroundImage(project.canvas.background.image) {
            if project.schemaVersion == 1 { optional.insert(image) } else { required.insert(image) }
        }
        for media in project.mediaSources ?? [] {
            required.insert(media.sources.screen.file)
            if let camera = media.sources.camera { required.insert(camera.file) }
            if let events = media.sources.events { required.insert(events) }
            if case .string(let image) = media.extra[Self.recordingBackgroundImage] { required.insert(image) }
        }
        return (required, optional.subtracting(required))
    }

    private func ownedFiles(_ project: StudioProject) -> Set<String> {
        let (required, optional) = assetNames(project)
        return required.union(optional)
    }

    private static func validateAssetName(_ name: String) throws {
        _ = try StudioJSON.requirePlainFileName(name, "project asset")
        guard !name.contains("\0"), name.lowercased() != "project.json",
              name.lowercased() != "saved-project.json", name.lowercased() != Self.sourceRecord,
              (name as NSString).pathExtension.lowercased() != Self.projectFileExtension else {
            throw StudioProjectFolderError.destinationExists
        }
    }

    private func folderAssets(for project: StudioProject, in directory: URL, externalScreen: URL? = nil, staging: URL? = nil)
        throws -> [(source: URL, name: String)] {
        guard !project.sources.screen.external, !(project.mediaSources ?? []).contains(where: { $0.sources.screen.external })
        else { throw StudioProjectFolderError.externalSource }
        let (required, optional) = assetNames(project)
        var result: [(source: URL, name: String)] = []
        var seen = Set<String>()
        for name in required.union(optional).sorted() {
            try Self.validateAssetName(name)
            guard seen.insert(name.lowercased()).inserted else { throw StudioProjectFolderError.destinationExists }
            let url: URL
            if let externalScreen, name == project.sources.screen.file {
                url = externalScreen.resolvingSymlinksInPath()
            } else if let staged = staging?.appendingPathComponent(name), fileManager.fileExists(atPath: staged.path) {
                url = staged
            } else {
                url = directory.appendingPathComponent(name)
            }
            if !fileManager.fileExists(atPath: url.path), !required.contains(name) { continue }
            guard (try? isPlainFile(url)) == true else { throw StudioProjectFolderError.missingFile(name) }
            result.append((url, name))
        }
        return result
    }

    func savedProjectAssets(at file: URL) throws -> [URL] {
        let project = try StudioJSON.makeDecoder().decode(StudioProject.self, from: Data(contentsOf: file))
        return ownedFiles(project).filter { StudioJSON.isPlainFileName($0) }
            .map { file.deletingLastPathComponent().appendingPathComponent($0) }
    }

    private func isPlainFile(_ url: URL) throws -> Bool {
        let values = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
        return values.isRegularFile == true && values.isSymbolicLink != true
    }

    func updateSavedProject(id: String, projectFile: URL, beforeCommit: (() throws -> Void)? = nil) throws {
        Self.linkedSaveLock.lock()
        defer { Self.linkedSaveLock.unlock() }
        let folder = projectFile.deletingLastPathComponent()
        guard try isPlainFile(projectFile),
              try folder.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink != true else {
            throw StudioProjectFolderError.destinationExists
        }
        let originalData = try Data(contentsOf: projectFile)
        let oldProject = try StudioJSON.makeDecoder().decode(StudioProject.self, from: originalData)
        var owned = ownedFiles(oldProject)
        owned.insert(projectFile.lastPathComponent)
        let staging = folder.appendingPathComponent(".tinyclips-update-\(UUID().uuidString)", isDirectory: true)
        let backups = staging.appendingPathComponent("backups", isDirectory: true)
        var installed: [String] = []
        var moved: [String] = []
        var keepBackup = false
        defer {
            if !keepBackup {
                do { try fileManager.removeItem(at: staging) }
                catch { NSLog("Studio could not remove save staging folder %@: %@", staging.path, error.localizedDescription) }
            }
        }
        do {
            try exportProjectFolder(id: id, to: staging)
            let stagedProject = staging.appendingPathComponent(staging.lastPathComponent).appendingPathExtension(Self.projectFileExtension)
            try fileManager.moveItem(at: stagedProject, to: staging.appendingPathComponent(projectFile.lastPathComponent))
            let names = try fileManager.contentsOfDirectory(atPath: staging.path)
                .filter { $0 != projectFile.lastPathComponent } + [projectFile.lastPathComponent]
            var reusable = Set<String>()
            for name in names {
                let destination = folder.appendingPathComponent(name)
                if fileManager.fileExists(atPath: destination.path) {
                    let plain = try isPlainFile(destination)
                    guard plain else { throw StudioProjectFolderError.destinationExists }
                    if !owned.contains(name) {
                        // Undo can leave an immutable import beside a linked save. Redo may
                        // reference the same bytes again, but must never overwrite this file.
                        guard name.hasPrefix("media-"),
                              fileManager.contentsEqual(atPath: destination.path, andPath: staging.appendingPathComponent(name).path)
                        else { throw StudioProjectFolderError.destinationExists }
                        reusable.insert(name)
                    }
                }
            }

            guard try Data(contentsOf: projectFile) == originalData else {
                throw StudioProjectFolderError.destinationExists
            }
            try beforeCommit?()
            try fileManager.createDirectory(at: backups, withIntermediateDirectories: false)
            for name in names {
                let destination = folder.appendingPathComponent(name)
                if reusable.contains(name) {
                    guard try isPlainFile(destination),
                          fileManager.contentsEqual(atPath: destination.path, andPath: staging.appendingPathComponent(name).path)
                    else { throw StudioProjectFolderError.destinationExists }
                    continue
                }
                if owned.contains(name) && fileManager.fileExists(atPath: destination.path) {
                    try fileManager.moveItem(at: destination, to: backups.appendingPathComponent(name))
                    moved.append(name)
                }
                try fileManager.moveItem(at: staging.appendingPathComponent(name), to: destination)
                installed.append(name)
            }
        } catch {
            let failure = error
            do {
                for name in installed.reversed() { try fileManager.removeItem(at: folder.appendingPathComponent(name)) }
                for name in moved.reversed() {
                    try fileManager.moveItem(at: backups.appendingPathComponent(name), to: folder.appendingPathComponent(name))
                }
            } catch {
                keepBackup = true
                throw NSError(domain: "TinyClipsStudioSave", code: 1, userInfo: [
                    NSLocalizedDescriptionKey: "Saving failed and the original files could not all be restored. Backups are in \(backups.path).",
                    NSUnderlyingErrorKey: failure
                ])
            }
            throw failure
        }
    }

    private func canReplaceSavedFolder(_ folder: URL) -> Bool {
        do {
            let file = try projectFile(at: folder)
            let project = try StudioJSON.makeDecoder().decode(StudioProject.self, from: Data(contentsOf: file))
            var allowed = Set(ownedFiles(project).map { $0.lowercased() })
            allowed.formUnion([file.lastPathComponent.lowercased(), ".ds_store", "thumbs.db", "desktop.ini"])
            return try fileManager.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.isRegularFileKey, .isSymbolicLinkKey])
                .allSatisfy { url in
                    try isPlainFile(url) && (allowed.contains(url.lastPathComponent.lowercased()) || url.lastPathComponent.hasPrefix("._"))
                }
        } catch {
            return false
        }
    }

    /// Saves a copy of a project as a folder that can be taken to another computer: the files
    /// the project names, and beside them a `.tinyclips` file named after the folder. That file
    /// is the project as `project.json` has it, without its list of exported videos. The project
    /// in the store is not changed.
    ///
    /// Where `folder` is already there it is replaced only when asked to, and only when it is a
    /// project saved this way before: anything else is left alone and the save is refused. A
    /// folder that is replaced is whole until the new one is, and nothing is left of a new
    /// folder when the copy fails.
    ///
    /// The files are copied after the store's lock is let go, so that an editor saving an edit
    /// does not wait for gigabytes to be copied. They are the recordings, which nothing rewrites.
    func exportProjectFolder(id: String, to folder: URL, replacingSavedProject: Bool = false) throws {
        let (files, data): ([(source: URL, name: String)], Data) = try withLock {
            var project = try loadUnlocked(id: id)
            let paths = try pathsUnlocked(for: project)
            var external: URL?
            // The local import receipt doesn't travel. Keep this v1 recording's provenance
            // when the external flag is cleared, so a later timeline edit doesn't invent layers.
            if isKnownFlat(project) { project.sources.extra["mediaKind"] = .string("flatVideo") }
            if project.sources.screen.external {
                external = paths.screenURL
                project.sources.screen.file = Self.copiedScreenFileName(for: paths.screenURL)
                project.sources.screen.external = false
            }
            let files = try folderAssets(for: project, in: paths.projectDirectory, externalScreen: external)
            // Where a video was exported to is a path on this computer, with its user's name in
            // it, and means nothing on another one.
            project.exports = []
            return (files, try StudioJSON.makeEncoder().encode(project))
        }

        // Filled where it will stay, or beside what it replaces and then put in its place.
        func fill(_ target: URL) throws {
            for file in files {
                let copy = target.appendingPathComponent(file.name)
                try fileManager.copyItem(at: file.source, to: copy)
                guard try isPlainFile(copy) else { throw StudioProjectFolderError.missingFile(file.name) }
            }
            // Written last: a folder with its `.tinyclips` file in it is a whole copy. The file
            // has the name the folder will have.
            let projectFile = target
                .appendingPathComponent(folder.lastPathComponent)
                .appendingPathExtension(Self.projectFileExtension)
            try data.write(to: projectFile, options: .atomic)
        }

        if mkdir(folder.path, 0o777) == 0 {
            do {
                try fill(folder)
            } catch {
                try? fileManager.removeItem(at: folder)
                throw error
            }
            return
        }
        let creationError = errno
        guard creationError == EEXIST else {
            throw NSError(domain: NSPOSIXErrorDomain, code: Int(creationError))
        }

        guard replacingSavedProject, canReplaceSavedFolder(folder) else {
            throw StudioProjectFolderError.destinationExists
        }
        let scratch = try fileManager.url(
            for: .itemReplacementDirectory,
            in: .userDomainMask,
            appropriateFor: folder,
            create: true
        )
        defer { try? fileManager.removeItem(at: scratch) }
        let replacement = scratch.appendingPathComponent(folder.lastPathComponent, isDirectory: true)
        try fileManager.createDirectory(at: replacement, withIntermediateDirectories: true)
        try fill(replacement)
        _ = try fileManager.replaceItemAt(folder, withItemAt: replacement)
    }

    /// Whether a folder is a project saved by `exportProjectFolder`: a folder with exactly one
    /// `.tinyclips` file in it.
    func isSavedProjectFolder(_ folder: URL) -> Bool {
        var isDirectory: ObjCBool = false
        guard fileManager.fileExists(atPath: folder.path, isDirectory: &isDirectory), isDirectory.boolValue else {
            return false
        }
        return (try? projectFile(at: folder)) != nil
    }

    /// The `.tinyclips` file that was chosen: itself, or the only one in a folder that was.
    func projectFile(at url: URL) throws -> URL {
        var isDirectory: ObjCBool = false
        guard fileManager.fileExists(atPath: url.path, isDirectory: &isDirectory) else {
            throw StudioProjectFolderError.notAProjectFile
        }
        guard isDirectory.boolValue else {
            guard url.pathExtension.lowercased() == Self.projectFileExtension else {
                throw StudioProjectFolderError.notAProjectFile
            }
            return url
        }
        let found = try fileManager
            .contentsOfDirectory(at: url, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])
            .filter { $0.pathExtension.lowercased() == Self.projectFileExtension }
        guard found.count == 1 else { throw StudioProjectFolderError.notAProjectFile }
        return found[0]
    }

    /// Makes a new project in the store from a `.tinyclips` file and the recordings in its
    /// folder, and returns it. The folder is only read. The project gets an id of its own, so
    /// opening the same file twice makes two projects, and it starts as a draft: it has exported
    /// nothing on this computer, whatever its file says.
    func importProjectFolder(projectFile: URL) throws -> StudioProject {
        guard (try? isPlainFile(projectFile)) == true else { throw StudioProjectFolderError.notAProjectFile }
        let size = (try fileManager.attributesOfItem(atPath: projectFile.path)[.size] as? NSNumber)?.intValue ?? 0
        guard size <= Self.largestProjectFile else { throw StudioProjectFolderError.notAProjectFile }
        let data = try Data(contentsOf: projectFile)
        var project = try StudioJSON.makeDecoder().decode(StudioProject.self, from: data)
        let folder = projectFile.deletingLastPathComponent()
        let files = try folderAssets(for: project, in: folder)

        // The new folder has no `project.json` until the files are in it. Cleanup takes such a
        // folder for a recording that is still being made, and leaves it alone for a day.
        let paths = try beginRecording()
        do {
            for file in files {
                let copy = paths.projectDirectory.appendingPathComponent(file.name)
                try fileManager.copyItem(at: file.source, to: copy)
                guard try isPlainFile(copy) else { throw StudioProjectFolderError.missingFile(file.name) }
            }
            project.id = paths.id
            project.exports = []
            let saved = try withLock {
                try saveUnlocked(project, updatingModifiedAt: false)
            }
            try rememberDestination(id: project.id, file: projectFile)
            return saved
        } catch {
            try? fileManager.removeItem(at: paths.projectDirectory)
            throw error
        }
    }

    // MARK: - In Use

    /// Says that an editor has the project open from now on, until `endUse(id:)`. `cleanup`
    /// leaves such a project alone whatever the list it was given says. That list is made on
    /// another thread before the cleanup starts, and reading every project takes a moment: a
    /// project that is opened in that moment is not in the list, and would lose its folder from
    /// under its editor.
    ///
    /// An editor calls this before it reads its project, and reading waits for the same lock
    /// the cleanup holds. So a project that is not in use when the cleanup looks is gone before
    /// an editor can have a file of it open, and the editor says that it cannot be opened.
    func beginUse(id: String) {
        withLock {
            _ = idsInUse.insert(id)
        }
    }

    func endUse(id: String) {
        withLock {
            _ = idsInUse.remove(id)
        }
    }

    // MARK: - Private

    private func withLock<T>(_ body: () throws -> T) rethrows -> T {
        lock.lock()
        defer { lock.unlock() }
        return try body()
    }

    private static func isValidProjectID(_ id: String) -> Bool {
        guard id.count == 36 else { return false }
        for (index, character) in id.enumerated() {
            switch index {
            case 8, 13, 18, 23:
                guard character == "-" else { return false }
            default:
                guard ("0"..."9").contains(character) || ("a"..."f").contains(character) else { return false }
            }
        }
        return true
    }

    private static func validateProjectID(_ id: String) throws {
        guard isValidProjectID(id) else {
            throw StudioProjectError.invalidProject("Invalid project id")
        }
    }

    private func buildDefaultProject(id: String, request: StudioProjectCreationRequest) throws -> StudioProject {
        try Self.validateProjectID(id)
        _ = try StudioJSON.requirePositive(request.screenWidth, "sources.screen.width")
        _ = try StudioJSON.requirePositive(request.screenHeight, "sources.screen.height")
        _ = try StudioJSON.requireNonNegative(request.screenDuration, "sources.screen.duration")
        if let camera = request.camera {
            _ = try StudioJSON.requirePositive(camera.width, "sources.camera.width")
            _ = try StudioJSON.requirePositive(camera.height, "sources.camera.height")
            _ = try StudioJSON.requireNonNegative(camera.duration, "sources.camera.duration")
        }
        let date = now()
        let cameraSource = request.camera.map {
            StudioCameraSource(width: $0.width, height: $0.height, duration: $0.duration, startOffset: $0.startOffset)
        }
        let layout: StudioLayout = cameraSource == nil ? .screen : .bubble
        let trimStart = max(0, request.camera?.startOffset ?? 0)

        var canvas = request.look?.canvas ?? StudioCanvas()
        var screen = request.look?.screen ?? StudioScreenStyle()
        var camera = request.look?.camera ?? StudioCameraStyle()
        screen.crop = nil
        camera.crop = nil
        canvas.background.image = validBackgroundImage(canvas.background.image)

        // With a camera, what was changed while recording becomes scenes (section 9.1).
        let firstScene = StudioScene(start: 0, layout: layout, bubble: StudioBubble(anchor: request.bubbleAnchor))
        let scenes = cameraSource == nil
            ? [firstScene]
            : StudioCaptureEvents.scenes(
                first: firstScene,
                corners: request.cameraCorners,
                markers: request.layoutMarkers,
                duration: request.screenDuration
            )

        return StudioProject(
            id: id,
            name: request.name,
            createdAt: date,
            modifiedAt: date,
            lastOpenedAt: date,
            app: StudioAppInfo(platform: "macos", version: request.appVersion),
            sources: StudioSources(
                screen: StudioScreenSource(
                    width: request.screenWidth,
                    height: request.screenHeight,
                    frameRate: request.screenFrameRate,
                    duration: request.screenDuration,
                    audioTracks: request.screenAudioTracks
                ),
                camera: cameraSource,
                events: "events.json"
            ),
            canvas: canvas,
            screen: screen,
            camera: camera,
            scenes: scenes,
            edits: StudioEdits(trimStart: trimStart),
            overlays: StudioOverlays(clicks: request.clickOverlay, branding: request.branding)
        )
    }

    private func pathsUnlocked(forID id: String) throws -> StudioProjectPaths {
        try Self.validateProjectID(id)
        let directory = projectDirectoryUnchecked(for: id)
        return StudioProjectPaths(
            id: id,
            projectDirectory: directory,
            projectJSONURL: directory.appendingPathComponent("project.json"),
            screenURL: directory.appendingPathComponent("screen.mp4"),
            cameraURL: directory.appendingPathComponent("camera.mp4"),
            eventsURL: directory.appendingPathComponent("events.json"),
            posterURL: directory.appendingPathComponent("poster.jpg")
        )
    }

    private func pathsUnlocked(for project: StudioProject) throws -> StudioProjectPaths {
        try Self.validateProjectID(project.id)
        let directory = projectDirectoryUnchecked(for: project.id)
        let screenURL: URL
        if project.sources.screen.external {
            screenURL = URL(fileURLWithPath: project.sources.screen.file).standardizedFileURL
        } else {
            let file = try StudioJSON.requirePlainFileName(project.sources.screen.file, "sources.screen.file")
            screenURL = directory.appendingPathComponent(file)
        }
        let cameraURL = try project.sources.camera.map {
            directory.appendingPathComponent(try StudioJSON.requirePlainFileName($0.file, "sources.camera.file"))
        }
        let eventsFile = try StudioJSON.requirePlainFileName(project.sources.events ?? "events.json", "sources.events")
        return StudioProjectPaths(
            id: project.id,
            projectDirectory: directory,
            projectJSONURL: directory.appendingPathComponent("project.json"),
            screenURL: screenURL,
            cameraURL: cameraURL,
            eventsURL: directory.appendingPathComponent(eventsFile),
            posterURL: directory.appendingPathComponent("poster.jpg")
        )
    }

    private func loadUnlocked(id: String) throws -> StudioProject {
        let paths = try pathsUnlocked(forID: id)
        let data = try Data(contentsOf: paths.projectJSONURL)
        var project = try StudioJSON.makeDecoder().decode(StudioProject.self, from: data)
        project.id = id
        return project
    }

    private func saveUnlocked(_ project: StudioProject, updatingModifiedAt: Bool) throws -> StudioProject {
        var projectToSave = project
        try Self.validateProjectID(projectToSave.id)
        if updatingModifiedAt {
            projectToSave.modifiedAt = now()
        }
        let paths = try pathsUnlocked(for: projectToSave)
        try fileManager.createDirectory(at: paths.projectDirectory, withIntermediateDirectories: true)
        let data = try StudioJSON.makeEncoder().encode(projectToSave)
        try data.write(to: paths.projectJSONURL, options: .atomic)
        return projectToSave
    }

    private func listSummariesUnlocked() throws -> [StudioProjectSummary] {
        try listProjectsUnlocked(includingSummaries: true, includingUnreadable: false).summaries
    }

    private func listProjectsUnlocked(includingSummaries: Bool, includingUnreadable: Bool) throws -> StudioProjectListing {
        var listing = StudioProjectListing()
        guard fileManager.fileExists(atPath: rootURL.path) else { return listing }
        let keys: Set<URLResourceKey> = includingUnreadable ? [.isDirectoryKey, .creationDateKey] : [.isDirectoryKey]
        let contents = try fileManager.contentsOfDirectory(
            at: rootURL,
            includingPropertiesForKeys: Array(keys),
            options: [.skipsHiddenFiles]
        )
        for directory in contents {
            let values = try directory.resourceValues(forKeys: keys)
            let id = directory.lastPathComponent
            guard values.isDirectory == true, Self.isValidProjectID(id) else { continue }
            // A recording that hasn't finished has no manifest and belongs in neither list.
            let projectURL = directory.appendingPathComponent("project.json")
            guard fileManager.fileExists(atPath: projectURL.path) else { continue }
            if let project = try? loadUnlocked(id: id) {
                if includingSummaries {
                    listing.summaries.append(summary(for: project, directory: directory))
                }
            } else if includingUnreadable {
                listing.unreadable.append(StudioUnreadableProject(
                    id: id,
                    createdAt: folderDateProvider?(directory) ?? values.creationDate ?? Date(timeIntervalSince1970: 0),
                    sizeOnDisk: sizeOnDisk(at: directory),
                    hasScreenRecording: recoveryRecording(in: directory, project: nil) != nil
                ))
            }
        }
        listing.summaries.sort { $0.createdAt == $1.createdAt ? $0.id < $1.id : $0.createdAt < $1.createdAt }
        listing.unreadable.sort { $0.createdAt == $1.createdAt ? $0.id < $1.id : $0.createdAt < $1.createdAt }
        return listing
    }

    private func deleteUnlocked(id: String) throws {
        try Self.validateProjectID(id)
        let directory = projectDirectoryUnchecked(for: id)
        if fileManager.fileExists(atPath: directory.path) {
            try fileManager.removeItem(at: directory)
        }
    }

    private func unfinishedRecordingIDsUnlocked(inUseProjectIDs: Set<String>) throws -> [String] {
        guard fileManager.fileExists(atPath: rootURL.path) else { return [] }
        let contents = try fileManager.contentsOfDirectory(
            at: rootURL,
            includingPropertiesForKeys: [.creationDateKey, .contentModificationDateKey, .isDirectoryKey],
            options: [.skipsHiddenFiles]
        )
        let cutoff = now().addingTimeInterval(-24 * 60 * 60)
        var ids: [String] = []
        for directory in contents {
            let id = directory.lastPathComponent
            let values = try directory.resourceValues(forKeys: [.creationDateKey, .contentModificationDateKey, .isDirectoryKey])
            guard values.isDirectory == true,
                  Self.isValidProjectID(id),
                  !inUseProjectIDs.contains(id) else {
                continue
            }
            let projectJSONURL = directory.appendingPathComponent("project.json")
            guard !fileManager.fileExists(atPath: projectJSONURL.path) else { continue }
            let folderDate = folderDateProvider?(directory)
                ?? values.creationDate
                ?? values.contentModificationDate
                ?? Date(timeIntervalSince1970: 0)
            if folderDate < cutoff {
                ids.append(id)
            }
        }
        return ids.sorted()
    }

    private func projectDirectoryUnchecked(for id: String) -> URL {
        rootURL.appendingPathComponent(id, isDirectory: true)
    }

    private func summary(for project: StudioProject, directory: URL) -> StudioProjectSummary {
        let isFlat = project.sources.screen.external
        let sourceExists: Bool
        if isFlat {
            sourceExists = fileManager.fileExists(atPath: URL(fileURLWithPath: project.sources.screen.file).standardizedFileURL.path)
        } else {
            sourceExists = fileManager.fileExists(atPath: directory.appendingPathComponent(project.sources.screen.file).path)
        }
        // Looked at where each video was saved. One on a drive that is not connected, or in a
        // folder the app may no longer read, counts as not there, which keeps the project: the
        // safe side of not knowing.
        let exportMissing = !project.exports.isEmpty && !project.exports.contains { isStillWhereItWasSaved($0) }
        return StudioProjectSummary(
            id: project.id,
            name: project.name,
            createdAt: project.createdAt,
            lastOpenedAt: project.lastOpenedAt,
            isDraft: project.exports.isEmpty,
            isFlat: isFlat,
            keepSources: project.keepSources,
            sizeOnDisk: sizeOnDisk(at: directory),
            sourceExists: sourceExists,
            exportMissing: exportMissing
        )
    }

    /// Whether the video of an export is still where it was saved: a file is at its path and,
    /// where the export says how large the video was, the file is that large. A file of another
    /// size is another video that has taken the name, or the same one changed since. Either way
    /// it is not what the project exported, and counting it would let cleanup remove a project
    /// that holds the only copy of its recording.
    private func isStillWhereItWasSaved(_ export: StudioExport) -> Bool {
        guard !export.path.isEmpty else { return false }
        let url = URL(fileURLWithPath: export.path).standardizedFileURL
        // A folder that has taken the video's name is not the video.
        var isDirectory: ObjCBool = false
        guard fileManager.fileExists(atPath: url.path, isDirectory: &isDirectory), !isDirectory.boolValue else {
            return false
        }
        guard let bytes = export.bytes, bytes > 0 else { return true }
        return fileSize(of: url) == bytes
    }

    /// The size of a file in bytes, or nil where there is no file or its size cannot be read.
    private func fileSize(of url: URL) -> Int64? {
        guard let size = (try? url.resourceValues(forKeys: [.fileSizeKey]))?.fileSize else { return nil }
        return Int64(size)
    }

    private func sizeOnDisk(at url: URL) -> Int64 {
        guard let enumerator = fileManager.enumerator(
            at: url,
            includingPropertiesForKeys: [.totalFileAllocatedSizeKey, .fileAllocatedSizeKey, .isRegularFileKey],
            options: [.skipsHiddenFiles]
        ) else {
            return 0
        }

        var total: Int64 = 0
        for case let fileURL as URL in enumerator {
            guard let values = try? fileURL.resourceValues(forKeys: [.totalFileAllocatedSizeKey, .fileAllocatedSizeKey, .isRegularFileKey]),
                  values.isRegularFile == true else {
                continue
            }
            total += Int64(values.totalFileAllocatedSize ?? values.fileAllocatedSize ?? 0)
        }
        return total
    }

    private func normalizedPath(_ path: String) -> String {
        Self.exportKey(forPath: path)
    }

    private func validBackgroundImage(_ image: String?) -> String? {
        guard let image else { return nil }
        return StudioJSON.isPlainFileName(image) ? image : nil
    }
}

struct StudioExportDestination: Equatable {
    let url: URL
    let size: UInt64?
    let modified: Date?
    private let version: FileVersion?

    private struct FileVersion: Equatable {
        let device: Int32
        let inode: UInt64
        let changedSeconds: Int64
        let changedNanoseconds: Int64
    }

    init(url: URL) throws {
        self.url = url
        if FileManager.default.fileExists(atPath: url.path) {
            var info = stat()
            try url.withUnsafeFileSystemRepresentation { path in
                guard let path else { throw CocoaError(.fileWriteInvalidFileName) }
                guard Darwin.lstat(path, &info) == 0 else {
                    throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno))
                }
            }
            guard info.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG) else {
                throw CocoaError(.fileWriteInvalidFileName)
            }
            size = UInt64(info.st_size)
            modified = Date(timeIntervalSince1970: Double(info.st_mtimespec.tv_sec)
                + Double(info.st_mtimespec.tv_nsec) / 1_000_000_000)
            version = FileVersion(device: info.st_dev, inode: info.st_ino,
                changedSeconds: Int64(info.st_ctimespec.tv_sec), changedNanoseconds: Int64(info.st_ctimespec.tv_nsec))
        } else {
            size = nil
            modified = nil
            version = nil
        }
    }

    func commit(staged: URL) throws {
        guard try StudioExportDestination(url: url) == self else {
            throw NSError(domain: "TinyClipsStudioExport", code: 2, userInfo: [
                NSLocalizedDescriptionKey: "The selected output file changed while exporting. Export again to confirm its replacement."
            ])
        }
        if size != nil {
            _ = try FileManager.default.replaceItemAt(url, withItemAt: staged)
        } else {
            try FileManager.default.moveItem(at: staged, to: url)
        }
    }
}

// MARK: - Screen Recording

/// The way out for a recording that Studio cannot show: its screen recording, copied to where
/// saved videos go, as an ordinary video. The project is left as it is.
enum StudioScreenRecording {
    enum SaveError: LocalizedError, Equatable {
        /// The project has no screen recording in its folder.
        case nothingToSave

        /// Every name the copy was tried under had been taken by another file.
        case noFreeName(String)

        var errorDescription: String? {
            switch self {
            case .nothingToSave:
                return "This project has no screen recording to save."
            case .noFreeName(let name):
                return "Another file was saved as \(name) while the recording was being copied, and no free name was found for the video."
            }
        }
    }

    /// How many names a finished copy is tried under before it is given up.
    static let attempts = 5

    /// Copies a project's screen recording to a new video file and returns where it went. The
    /// copy never takes the place of a file that is there. This reads and writes the whole
    /// recording, so it is for a background task.
    ///
    /// - Parameter makeOutputURL: Returns the URL to write, in the folder and with the name the
    ///   app gives a saved video. Asked once before the copy, and again when a file has taken
    ///   that name by the time the copy is finished.
    static func save(
        store: StudioProjectStore,
        id: String,
        fileManager: FileManager = .default,
        makeOutputURL: (_ fileExtension: String) -> URL
    ) throws -> URL {
        guard let sourceURL = store.screenRecordingURL(id: id) else {
            throw SaveError.nothingToSave
        }

        let fileExtension = sourceURL.pathExtension.lowercased()
        var targetURL = makeOutputURL(fileExtension)
        let folderURL = targetURL.deletingLastPathComponent()
        try fileManager.createDirectory(at: folderURL, withIntermediateDirectories: true)

        // Copied under a name of its own first, in the same folder, so that no half-written
        // video is ever to be seen under a video's name.
        let stagedURL = folderURL.appendingPathComponent(".studio-copy.\(UUID().uuidString.lowercased()).tmp")
        do {
            try fileManager.copyItem(at: sourceURL, to: stagedURL)
            let values = try stagedURL.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
            guard values.isRegularFile == true, values.isSymbolicLink != true else { throw SaveError.nothingToSave }
            // A copy keeps the dates of what it was copied from. The Clips Library sorts by
            // the day a file was made and archives the old ones, and this video is made now.
            let savedAt = Date()
            try? fileManager.setAttributes(
                [.creationDate: savedAt, .modificationDate: savedAt],
                ofItemAtPath: stagedURL.path
            )
            for attempt in 1...attempts {
                do {
                    // A move never replaces: it fails where a file already has the name.
                    try fileManager.moveItem(at: stagedURL, to: targetURL)
                    return targetURL
                } catch let error as CocoaError where error.code == .fileWriteFileExists {
                    guard attempt < attempts else {
                        throw SaveError.noFreeName(targetURL.lastPathComponent)
                    }
                    targetURL = makeOutputURL(fileExtension)
                }
            }
            throw SaveError.noFreeName(targetURL.lastPathComponent)
        } catch {
            try? fileManager.removeItem(at: stagedURL)
            throw error
        }
    }
}
