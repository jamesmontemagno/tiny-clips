import Foundation

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

    func delete(id: String) throws {
        try withLock {
            try deleteUnlocked(id: id)
        }
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

    /// The project folders whose `project.json` is there and cannot be read: damaged, or written
    /// by a newer version. `listSummaries()` leaves them out and cleanup leaves them alone, so
    /// this list is the only place they show up.
    func listUnreadableProjects() throws -> [StudioUnreadableProject] {
        try withLock {
            guard fileManager.fileExists(atPath: rootURL.path) else { return [] }
            let contents = try fileManager.contentsOfDirectory(
                at: rootURL,
                includingPropertiesForKeys: [.isDirectoryKey, .creationDateKey],
                options: [.skipsHiddenFiles]
            )
            var unreadable: [StudioUnreadableProject] = []
            for directory in contents {
                let values = try directory.resourceValues(forKeys: [.isDirectoryKey, .creationDateKey])
                let id = directory.lastPathComponent
                guard values.isDirectory == true, Self.isValidProjectID(id) else { continue }
                // Without the file it is a recording that never finished, which cleanup removes
                // after a day.
                let projectURL = directory.appendingPathComponent("project.json")
                guard fileManager.fileExists(atPath: projectURL.path),
                      (try? loadUnlocked(id: id)) == nil else {
                    continue
                }
                unreadable.append(StudioUnreadableProject(
                    id: id,
                    createdAt: folderDateProvider?(directory) ?? values.creationDate ?? Date(timeIntervalSince1970: 0),
                    sizeOnDisk: sizeOnDisk(at: directory),
                    hasScreenRecording: fileManager.fileExists(atPath: directory.appendingPathComponent("screen.mp4").path)
                ))
            }
            return unreadable.sorted { $0.createdAt == $1.createdAt ? $0.id < $1.id : $0.createdAt < $1.createdAt }
        }
    }

    /// The screen recording a project keeps in its own folder, or nil when it has none there:
    /// the project is built around a video kept elsewhere, the file is gone, or there is no
    /// such project. A project whose `project.json` cannot be read is looked for under the name
    /// every recording gets.
    func screenRecordingURL(id: String) -> URL? {
        guard Self.isValidProjectID(id) else { return nil }
        return withLock {
            let directory = projectDirectoryUnchecked(for: id)
            var file = "screen.mp4"
            if let project = try? loadUnlocked(id: id) {
                guard !project.sources.screen.external else { return nil }
                if StudioJSON.isPlainFileName(project.sources.screen.file) {
                    file = project.sources.screen.file
                }
            }
            let url = directory.appendingPathComponent(file)
            return fileManager.fileExists(atPath: url.path) ? url : nil
        }
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
            var changed = false
            for summary in try listSummariesUnlocked() {
                var project = try loadUnlocked(id: summary.id)
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
                } else if exports.count != project.exports.count {
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

    func getOrCreateFlatProject(request: StudioFlatProjectRequest) throws -> StudioProject {
        try withLock {
            _ = try StudioJSON.requirePositive(request.width, "sources.screen.width")
            _ = try StudioJSON.requirePositive(request.height, "sources.screen.height")
            _ = try StudioJSON.requireNonNegative(request.duration, "sources.screen.duration")
            let needle = normalizedPath(request.videoURL.path)
            for summary in try listSummariesUnlocked() {
                let project = try loadUnlocked(id: summary.id)
                if project.sources.screen.external && normalizedPath(project.sources.screen.file) == needle {
                    return project
                }
            }

            let id = UUID().uuidString.lowercased()
            let date = now()
            let project = StudioProject(
                id: id,
                name: request.name.isEmpty ? request.videoURL.deletingPathExtension().lastPathComponent : request.name,
                createdAt: date,
                modifiedAt: date,
                lastOpenedAt: date,
                app: StudioAppInfo(platform: "macos", version: request.appVersion),
                sources: StudioSources(
                    screen: StudioScreenSource(
                        file: request.videoURL.path,
                        width: request.width,
                        height: request.height,
                        frameRate: request.frameRate,
                        duration: request.duration,
                        external: true
                    ),
                    camera: nil,
                    events: nil
                ),
                scenes: [StudioScene(start: 0, layout: .screen)]
            )
            return try saveUnlocked(project, updatingModifiedAt: false)
        }
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
            let inUse = Set(inUseProjectIDs.filter(Self.isValidProjectID))
            var candidateIDs = StudioCleanupPolicy.plan(
                summaries: try listSummariesUnlocked(),
                currentDate: now(),
                options: options,
                inUseProjectIDs: inUse
            )
            candidateIDs.append(contentsOf: try unfinishedRecordingIDsUnlocked(inUseProjectIDs: inUse))

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
        guard fileManager.fileExists(atPath: rootURL.path) else { return [] }
        let contents = try fileManager.contentsOfDirectory(
            at: rootURL,
            includingPropertiesForKeys: [.isDirectoryKey],
            options: [.skipsHiddenFiles]
        )
        var summaries: [StudioProjectSummary] = []
        for directory in contents {
            let values = try directory.resourceValues(forKeys: [.isDirectoryKey])
            let id = directory.lastPathComponent
            guard values.isDirectory == true, Self.isValidProjectID(id) else { continue }
            let projectURL = directory.appendingPathComponent("project.json")
            guard fileManager.fileExists(atPath: projectURL.path),
                  let project = try? loadUnlocked(id: id) else {
                continue
            }
            summaries.append(summary(for: project, directory: directory))
        }
        return summaries.sorted { $0.createdAt == $1.createdAt ? $0.id < $1.id : $0.createdAt < $1.createdAt }
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
        makeOutputURL: () -> URL
    ) throws -> URL {
        guard let sourceURL = store.screenRecordingURL(id: id) else {
            throw SaveError.nothingToSave
        }

        var targetURL = makeOutputURL()
        let folderURL = targetURL.deletingLastPathComponent()
        try fileManager.createDirectory(at: folderURL, withIntermediateDirectories: true)

        // Copied under a name of its own first, in the same folder, so that no half-written
        // video is ever to be seen under a video's name.
        let stagedURL = folderURL.appendingPathComponent(".studio-copy.\(UUID().uuidString.lowercased()).tmp")
        do {
            try fileManager.copyItem(at: sourceURL, to: stagedURL)
            // A copy keeps the dates of what it was copied from. The Clips Manager sorts by
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
                    targetURL = makeOutputURL()
                }
            }
            throw SaveError.noFreeName(targetURL.lastPathComponent)
        } catch {
            try? fileManager.removeItem(at: stagedURL)
            throw error
        }
    }
}
