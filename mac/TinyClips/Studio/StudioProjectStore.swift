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

struct StudioProjectCreationRequest: Equatable, Sendable {
    var name: String
    var screenWidth: Int
    var screenHeight: Int
    var screenDuration: Double
    var screenFrameRate: Double
    var camera: StudioCameraCreationInfo?
    var bubbleAnchor: StudioAnchor
    var clickOverlay: StudioClickOverlay
    var branding: Bool
    var appVersion: String

    init(
        name: String = "",
        screenWidth: Int,
        screenHeight: Int,
        screenDuration: Double,
        screenFrameRate: Double = 30,
        camera: StudioCameraCreationInfo? = nil,
        bubbleAnchor: StudioAnchor = .bottomRight,
        clickOverlay: StudioClickOverlay = StudioClickOverlay(),
        branding: Bool = false,
        appVersion: String
    ) {
        self.name = name
        self.screenWidth = screenWidth
        self.screenHeight = screenHeight
        self.screenDuration = screenDuration
        self.screenFrameRate = screenFrameRate
        self.camera = camera
        self.bubbleAnchor = bubbleAnchor
        self.clickOverlay = clickOverlay
        self.branding = branding
        self.appVersion = appVersion
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

struct StudioRecordingProjectURLs: Equatable, Sendable {
    var id: String
    var projectDirectory: URL
    var screenURL: URL
    var cameraURL: URL
    var eventsURL: URL
}

struct StudioProjectSummary: Equatable, Sendable {
    var id: String
    var name: String
    var createdAt: Date
    var lastOpenedAt: Date
    var isDraft: Bool
    var isFlat: Bool
    var keepSources: Bool
    var sizeOnDisk: Int64
    var sourceExists: Bool
}

struct StudioStorageSummary: Equatable, Sendable {
    var projectCount: Int
    var totalBytes: Int64
}

// MARK: - Store

final class StudioProjectStore {
    let rootURL: URL
    private let now: () -> Date
    private let fileManager: FileManager

    init(
        rootURL: URL? = nil,
        now: @escaping () -> Date = Date.init,
        fileManager: FileManager = .default
    ) {
        self.rootURL = rootURL ?? Self.defaultRootURL(fileManager: fileManager)
        self.now = now
        self.fileManager = fileManager
    }

    static func defaultRootURL(fileManager: FileManager = .default) -> URL {
        let base = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? fileManager.temporaryDirectory
        return base
            .appendingPathComponent("TinyClips", isDirectory: true)
            .appendingPathComponent("Projects", isDirectory: true)
    }

    func createRecordingProjectFolder(id: String = UUID().uuidString.lowercased()) throws -> StudioRecordingProjectURLs {
        let directory = projectDirectory(for: id)
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        return StudioRecordingProjectURLs(
            id: id,
            projectDirectory: directory,
            screenURL: directory.appendingPathComponent("screen.mp4"),
            cameraURL: directory.appendingPathComponent("camera.mp4"),
            eventsURL: directory.appendingPathComponent("events.json")
        )
    }

    func buildDefaultProject(id: String, request: StudioProjectCreationRequest) -> StudioProject {
        let date = now()
        let cameraSource = request.camera.map {
            StudioCameraSource(width: $0.width, height: $0.height, duration: $0.duration, startOffset: $0.startOffset)
        }
        let layout: StudioLayout = cameraSource == nil ? .screen : .bubble
        let trimStart = max(0, request.camera?.startOffset ?? 0)
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
                    duration: request.screenDuration
                ),
                camera: cameraSource,
                events: "events.json"
            ),
            scenes: [
                StudioScene(
                    start: 0,
                    layout: layout,
                    bubble: StudioBubble(anchor: request.bubbleAnchor)
                )
            ],
            edits: StudioEdits(trimStart: trimStart),
            overlays: StudioOverlays(clicks: request.clickOverlay, branding: request.branding)
        )
    }

    func createProject(request: StudioProjectCreationRequest) throws -> StudioRecordingProjectURLs {
        let urls = try createRecordingProjectFolder()
        try save(buildDefaultProject(id: urls.id, request: request), updatingModifiedAt: false)
        return urls
    }

    func load(id: String) throws -> StudioProject {
        try load(at: projectDirectory(for: id).appendingPathComponent("project.json"))
    }

    func save(_ project: StudioProject, updatingModifiedAt: Bool = true) throws {
        var projectToSave = project
        if updatingModifiedAt {
            projectToSave.modifiedAt = now()
        }
        let directory = projectDirectory(for: projectToSave.id)
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        let data = try StudioJSON.makeEncoder().encode(projectToSave)
        try data.write(to: directory.appendingPathComponent("project.json"), options: .atomic)
    }

    func listSummaries() throws -> [StudioProjectSummary] {
        guard fileManager.fileExists(atPath: rootURL.path) else { return [] }
        let contents = try fileManager.contentsOfDirectory(
            at: rootURL,
            includingPropertiesForKeys: [.isDirectoryKey],
            options: [.skipsHiddenFiles]
        )
        var summaries: [StudioProjectSummary] = []
        for directory in contents {
            let values = try directory.resourceValues(forKeys: [.isDirectoryKey])
            guard values.isDirectory == true else { continue }
            let projectURL = directory.appendingPathComponent("project.json")
            guard fileManager.fileExists(atPath: projectURL.path),
                  let project = try? load(at: projectURL) else {
                continue
            }
            summaries.append(summary(for: project, directory: directory))
        }
        return summaries.sorted { $0.createdAt == $1.createdAt ? $0.id < $1.id : $0.createdAt < $1.createdAt }
    }

    func delete(id: String) throws {
        let directory = projectDirectory(for: id)
        if fileManager.fileExists(atPath: directory.path) {
            try fileManager.removeItem(at: directory)
        }
    }

    func markOpened(id: String) throws {
        var project = try load(id: id)
        project.lastOpenedAt = now()
        try save(project)
    }

    func recordExport(id: String, path: String, exportedAt: Date? = nil) throws {
        var project = try load(id: id)
        project.exports.append(StudioExport(path: path, exportedAt: exportedAt ?? now()))
        try save(project)
    }

    func findProject(exportedPath: String) throws -> StudioProject? {
        let needle = normalizedPath(exportedPath)
        for summary in try listSummaries() {
            let project = try load(id: summary.id)
            if project.exports.contains(where: { normalizedPath($0.path) == needle }) {
                return project
            }
        }
        return nil
    }

    func updateExportPath(from oldPath: String, to newPath: String) throws {
        let needle = normalizedPath(oldPath)
        for summary in try listSummaries() {
            var project = try load(id: summary.id)
            var changed = false
            for index in project.exports.indices where normalizedPath(project.exports[index].path) == needle {
                project.exports[index].path = newPath
                changed = true
            }
            if changed {
                try save(project)
            }
        }
    }

    func removeExportPath(_ path: String) throws {
        let needle = normalizedPath(path)
        for summary in try listSummaries() {
            var project = try load(id: summary.id)
            let oldCount = project.exports.count
            project.exports.removeAll { normalizedPath($0.path) == needle }
            if project.exports.count != oldCount {
                try save(project)
            }
        }
    }

    func getOrCreateFlatProject(request: StudioFlatProjectRequest) throws -> StudioProject {
        let needle = normalizedPath(request.videoURL.path)
        for summary in try listSummaries() {
            let project = try load(id: summary.id)
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
        try save(project, updatingModifiedAt: false)
        return project
    }

    func storageSummary() throws -> StudioStorageSummary {
        let summaries = try listSummaries()
        return StudioStorageSummary(
            projectCount: summaries.count,
            totalBytes: summaries.reduce(Int64(0)) { $0 + $1.sizeOnDisk }
        )
    }

    func loadEvents(id: String) throws -> StudioEvents {
        let data = try Data(contentsOf: projectDirectory(for: id).appendingPathComponent("events.json"))
        return try StudioJSON.makeDecoder().decode(StudioEvents.self, from: data)
    }

    func saveEvents(_ events: StudioEvents, id: String) throws {
        let directory = projectDirectory(for: id)
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        let data = try StudioJSON.makeEncoder().encode(events)
        try data.write(to: directory.appendingPathComponent("events.json"), options: .atomic)
    }

    @discardableResult
    func cleanup(options: StudioCleanupOptions = StudioCleanupOptions()) throws -> [String] {
        let ids = StudioCleanupPolicy.plan(
            summaries: try listSummaries(),
            currentDate: now(),
            options: options
        )
        for id in ids {
            try delete(id: id)
        }
        return ids
    }

    // MARK: - Private

    private func load(at url: URL) throws -> StudioProject {
        let data = try Data(contentsOf: url)
        return try StudioJSON.makeDecoder().decode(StudioProject.self, from: data)
    }

    private func projectDirectory(for id: String) -> URL {
        rootURL.appendingPathComponent(id, isDirectory: true)
    }

    private func summary(for project: StudioProject, directory: URL) -> StudioProjectSummary {
        let isFlat = project.sources.screen.external
        let sourceExists: Bool
        if isFlat {
            sourceExists = fileManager.fileExists(atPath: project.sources.screen.file)
        } else {
            sourceExists = fileManager.fileExists(atPath: directory.appendingPathComponent(project.sources.screen.file).path)
        }
        return StudioProjectSummary(
            id: project.id,
            name: project.name,
            createdAt: project.createdAt,
            lastOpenedAt: project.lastOpenedAt,
            isDraft: project.exports.isEmpty,
            isFlat: isFlat,
            keepSources: project.keepSources,
            sizeOnDisk: sizeOnDisk(at: directory),
            sourceExists: sourceExists
        )
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
        path.lowercased()
    }
}
