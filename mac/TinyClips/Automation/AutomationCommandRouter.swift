import AppKit
import AVFoundation
import CryptoKit
import Foundation

/// Runs contract commands against the app's services. Policy and approval have already passed.
@MainActor
final class AutomationCommandRouter {
    /// Contract commands this build implements. Everything else returns `not_supported`.
    static let supportedCommands: Set<String> = [
        "app.status", "app.open", "recording.status", "library.list", "settings.get",
        "capture.targets", "capture.screenshot", "capture.ocr",
        "capture.scrolling.start", "capture.scrolling.stop", "capture.scrolling.status", "capture.scrolling.cancel",
        "recording.startVideo", "recording.startGif", "recording.stop", "recording.togglePause",
        "recording.restart", "recording.discard",
        "editor.open", "editor.annotate", "editor.trim", "editor.saveFrame", "library.reveal", "library.copy", "settings.set",
        "library.delete", "library.upload", "studio.projects.list", "studio.project.get",
        "studio.project.edit", "studio.zoom.suggest", "studio.export",
    ]

    private unowned let controller: AutomationController

    init(controller: AutomationController) {
        self.controller = controller
    }

    private func captureManager() throws -> CaptureManager {
        guard let manager = controller.captureManager else {
            throw AutomationError(.busy, "Tiny Clips is still starting.")
        }
        return manager
    }

    func execute(command: String, params: [String: Any]) async throws -> [String: Any] {
        switch command {
        case "app.status": return try appStatus()
        case "app.open": return try openWindow(params)
        case "recording.status": return try recordingStatus()
        case "library.list": return try await listLibrary(params)
        case "settings.get": return try getSettings(params)
        case "settings.set": return try setSetting(params)
        case "capture.screenshot": return try await screenshot(params)
        case "capture.targets": return try await captureTargets(params)
        case "capture.ocr": return try await ocr(params)
        case "recording.startVideo": return try await startVideo(params)
        case "recording.startGif": return try await startGif(params)
        case "capture.scrolling.start": return try await startScrollingCapture(params)
        case "capture.scrolling.stop": return try await stopScrollingCapture()
        case "capture.scrolling.status": return try scrollingCaptureStatus()
        case "capture.scrolling.cancel": return try cancelScrollingCapture()
        case "recording.stop": return try stop()
        case "recording.togglePause": return try togglePause()
        case "recording.restart": return try restart()
        case "recording.discard": return try discard()
        case "editor.open": return try openEditor(params)
        case "studio.projects.list": return try listStudioProjects(params)
        case "studio.project.get": return try getStudioProject(params)
        case "studio.project.edit": return try editStudioProject(params)
        case "studio.zoom.suggest": return try suggestStudioZooms(params)
        case "studio.export": return try await exportStudioProject(params)
        case "editor.annotate": return try annotate(params)
        case "editor.trim": return try await trim(params)
        case "editor.saveFrame": return try await saveFrame(params)
        case "library.reveal": return try reveal(params)
        case "library.copy": return try copy(params)
        case "library.delete": return try await delete(params)
        case "library.upload": return try await upload(params)
        default: throw AutomationError(.notSupported)
        }
    }

    // MARK: - Read

    private func appStatus() throws -> [String: Any] {
        let manager = try captureManager()
        return [
            "app": "TinyClips",
            "version": AutomationController.appVersion,
            "platform": "macos",
            "build": AutomationController.buildFlavor,
            "recording": Self.recordingState(manager),
            "busy": Self.isBusy(manager),
            "automationEnabled": controller.isEnabled,
            "screenRecordingPermission": PermissionManager.shared.hasScreenRecordingPermission()
        ]
    }

    private func recordingStatus() throws -> [String: Any] {
        let manager = try captureManager()
        var result: [String: Any] = [
            "state": Self.recordingState(manager),
            "busy": Self.isBusy(manager)
        ]
        if let type = manager.activeRecordingType {
            result["kind"] = type == .gif ? "gif" : "video"
        }
        return result
    }

    static func recordingState(_ manager: CaptureManager) -> String {
        if manager.isRecording { return manager.isRecordingPaused ? "paused" : "recording" }
        if manager.isCountdownActive { return "countdown" }
        if manager.isFinalizingRecording { return "processing" }
        return "idle"
    }

    private static func isBusy(_ manager: CaptureManager) -> Bool {
        manager.isRecording || manager.isFinalizingRecording || manager.isCaptureActionInProgress
    }

    private func openWindow(_ params: [String: Any]) throws -> [String: Any] {
        let window = try Self.requiredString(params, "window")
        switch window {
        case "settings":
            guard AutomationWindowOpener.shared.open(id: "settings-window", title: "Tiny Clips Settings") else {
                throw AutomationError(.busy, "Tiny Clips is still starting.")
            }
        case "library":
            guard AutomationWindowOpener.shared.open(id: "clips-manager", title: "Clips Library") else {
                throw AutomationError(.busy, "Tiny Clips is still starting.")
            }
        case "guide":
            try captureManager().showGuide()
        default:
            throw AutomationError(.invalidParams, "window must be settings, library, or guide.")
        }
        return ["opened": window]
    }

    private func listLibrary(_ params: [String: Any]) async throws -> [String: Any] {
        let limit: Int
        if let value = params["limit"] {
            guard let number = AutomationValue.int(value), (1...500).contains(number) else {
                throw AutomationError(.invalidParams, "limit must be a whole number from 1 to 500.")
            }
            limit = number
        } else {
            limit = 20
        }
        let kind = (params["kind"] as? String) ?? "all"
        guard ["screenshot", "video", "gif", "all"].contains(kind) else {
            throw AutomationError(.invalidParams, "kind must be screenshot, video, gif, or all.")
        }

        let directories = libraryDirectories()
        let recentPaths = RecentCaptureStore.shared.items.map(\.path)
        let tinyClipsOnly = CaptureSettings.shared.clipsManagerIgnoreNonTinyClipsFiles
        let items = await Task.detached(priority: .userInitiated) {
            AutomationLibrary.scan(
                directories: directories,
                extraPaths: recentPaths,
                tinyClipsOnly: tinyClipsOnly
            )
        }.value
        let filtered = items.filter { kind == "all" || $0.kind == kind }.prefix(limit)
        let formatter = ISO8601DateFormatter()
        return [
            "items": filtered.map { item in
                [
                    "path": item.path,
                    "name": (item.path as NSString).lastPathComponent,
                    "kind": item.kind,
                    "createdAt": formatter.string(from: item.createdAt),
                    "sizeBytes": item.sizeBytes
                ] as [String: Any]
            }
        ]
    }

    private func libraryDirectories() -> [URL] {
        var seen = Set<String>()
        return [CaptureType.screenshot, .video, .gif]
            .map { SaveService.shared.outputDirectoryURL(for: $0) }
            .filter { seen.insert($0.standardizedFileURL.path).inserted }
    }

    // MARK: - Settings

    private func getSettings(_ params: [String: Any]) throws -> [String: Any] {
        if let key = params["key"] as? String {
            guard let setting = AutomationSetting.named(key) else {
                throw AutomationError(.invalidParams, "Unknown setting \(key). Call settings.get without a key to list them.")
            }
            return ["key": key, "value": setting.read(), "writable": setting.writable]
        }
        var values: [String: Any] = [:]
        for setting in AutomationSetting.all {
            values[setting.key] = setting.read()
        }
        return ["settings": values, "writable": AutomationSetting.all.filter(\.writable).map(\.key)]
    }

    private func setSetting(_ params: [String: Any]) throws -> [String: Any] {
        let key = try Self.requiredString(params, "key")
        guard let setting = AutomationSetting.named(key) else {
            throw AutomationError(.invalidParams, "Unknown setting \(key).")
        }
        guard let value = params["value"], !(value is NSNull) else {
            throw AutomationError(.invalidParams, "value is required.")
        }
        try setting.write(value)
        return ["key": key, "value": setting.read()]
    }

    // MARK: - Capture and recording

    private func requireIdle(_ manager: CaptureManager) throws {
        guard !Self.isBusy(manager) else {
            throw AutomationError(.busy, "Tiny Clips is already capturing or recording.")
        }
    }

    private func requireScreenRecordingPermission() throws {
        guard PermissionManager.shared.hasScreenRecordingPermission() else {
            throw AutomationError(.permissionRequired, "Screen Recording permission is not granted. Grant it in System Settings → Privacy & Security → Screen & System Audio Recording.")
        }
    }

    private func requireDevicePermissionIfUsed(_ settings: CaptureSettings) throws {
        let blocked: (AVMediaType) -> Bool = { type in
            let status = AVCaptureDevice.authorizationStatus(for: type)
            return status == .denied || status == .restricted
        }
        if settings.recordMicrophone, blocked(.audio) {
            throw AutomationError(.permissionRequired, "Microphone recording is on but Microphone permission is denied.")
        }
        if settings.webcamEnabled, blocked(.video) {
            throw AutomationError(.permissionRequired, "Camera overlay is on but Camera permission is denied.")
        }
    }

    private func captureTargets(_ params: [String: Any]) async throws -> [String: Any] {
        let kind: String
        switch params["kind"] {
        case nil, is NSNull: kind = "all"
        case let value as String where ["all", "displays", "windows"].contains(value): kind = value
        default: throw AutomationError(.invalidParams, "kind must be all, displays, or windows.")
        }
        var result: [String: Any] = [:]
        if kind != "windows" {
            result["displays"] = AutomationCaptureEnvironment.displays().map(AutomationCaptureTargets.displayJSON)
        }
        if kind != "displays" {
            try requireScreenRecordingPermission()
            result["windows"] = try await AutomationCaptureEnvironment.windows().map(AutomationCaptureEnvironment.windowJSON)
        }
        return result
    }

    /// Captures an agent target with no picker, countdown, or editor.
    private func captureTargetImage(_ params: [String: Any]) async throws -> (image: CGImage, scaleFactor: CGFloat, target: AutomationCaptureTarget) {
        let target = try AutomationCaptureTargets.parse(params)
        let manager = try captureManager()
        try requireIdle(manager)
        try requireScreenRecordingPermission()
        do {
            let captured = try await manager.performAutomationCapture {
                try await AutomationCaptureEnvironment.captureImage(target)
            }
            return (captured.image, captured.scaleFactor, target)
        } catch let error as AutomationError {
            throw error
        } catch {
            throw AutomationError(.internal, "Capture failed: \(error.localizedDescription)")
        }
    }

    private func screenshot(_ params: [String: Any]) async throws -> [String: Any] {
        let captured = try await captureTargetImage(params)
        let url: URL
        do {
            url = try ScreenshotCapture.saveImage(
                captured.image,
                to: SaveService.shared.generateURL(for: .screenshot),
                scaleFactor: captured.scaleFactor
            )
        } catch {
            throw AutomationError(.internal, "Could not save the screenshot: \(error.localizedDescription)")
        }
        CaptureAnalyticsStore.shared.recordCapture(.screenshot)
        SaveService.shared.handleSavedFile(url: url, type: .screenshot)
        return [
            "path": url.path,
            "mode": captured.target.modeName,
            "width": captured.image.width,
            "height": captured.image.height,
        ]
    }

    private func ocr(_ params: [String: Any]) async throws -> [String: Any] {
        let captured = try await captureTargetImage(params)
        do {
            switch try await TextRecognitionService.recognizeText(in: captured.image) {
            case .success(let text): return ["text": text]
            case .noTextFound: return ["text": ""]
            }
        } catch {
            throw AutomationError(.internal, "Text recognition failed: \(error.localizedDescription)")
        }
    }

    private func annotate(_ params: [String: Any]) throws -> [String: Any] {
        if let unknown = params.keys.first(where: { $0 != "path" && $0 != "annotations" }) {
            throw AutomationError(.invalidParams, "Unknown parameter \(unknown).")
        }
        let source = try existingFile(params)
        guard AutomationLibrary.kind(forPath: source.path) == "screenshot" else {
            throw AutomationError(.invalidParams, "editor.annotate works on screenshot images only.")
        }
        let rendered = try AutomationAnnotations.render(sourceURL: source, annotationsJSON: params["annotations"]) { fileExtension in
            SaveService.shared.generateURL(for: .screenshot, fileExtension: fileExtension, stemSuffix: "annotated")
        }
        guard rendered.url.standardizedFileURL != source else {
            throw AutomationError(.internal, "Refusing to overwrite the source screenshot.")
        }
        SaveService.shared.handleSavedFile(url: rendered.url, type: .screenshot)
        return [
            "path": rendered.url.path,
            "sourcePath": source.path,
            "annotationsApplied": rendered.count,
        ]
    }

    private func trim(_ params: [String: Any]) async throws -> [String: Any] {
        if let unknown = params.keys.first(where: { !["path", "start", "end", "removeAudio"].contains($0) }) {
            throw AutomationError(.invalidParams, "Unknown parameter \(unknown).")
        }
        let source = try existingFile(params)
        guard let start = AutomationValue.double(params["start"]),
              let end = AutomationValue.double(params["end"]) else {
            throw AutomationError(.invalidParams, "start and end are required numbers of seconds.")
        }
        var removeAudio = false
        if let value = params["removeAudio"] {
            guard let bool = AutomationValue.bool(value) else {
                throw AutomationError(.invalidParams, "removeAudio must be true or false.")
            }
            removeAudio = bool
        }

        let result: AutomationMediaEditing.TrimResult
        let type: CaptureType
        switch AutomationLibrary.kind(forPath: source.path) {
        case "video":
            result = try await AutomationMediaEditing.trimVideo(source: source, start: start, end: end, removeAudio: removeAudio)
            type = .video
        case "gif":
            guard !removeAudio else {
                throw AutomationError(.invalidParams, "removeAudio applies to videos only.")
            }
            result = try AutomationMediaEditing.trimGif(source: source, start: start, end: end)
            type = .gif
        default:
            throw AutomationError(.invalidParams, "editor.trim works on videos and GIFs only.")
        }
        SaveService.shared.handleSavedFile(url: result.url, type: type)
        var response: [String: Any] = [
            "path": result.url.path,
            "sourcePath": source.path,
            "start": result.range.start,
            "end": result.range.end,
        ]
        if let frameCount = result.frameCount {
            response["frameCount"] = frameCount
        }
        return response
    }

    private func saveFrame(_ params: [String: Any]) async throws -> [String: Any] {
        if let unknown = params.keys.first(where: { !["path", "time", "frame"].contains($0) }) {
            throw AutomationError(.invalidParams, "Unknown parameter \(unknown).")
        }
        let source = try existingFile(params)
        let time = try params["time"].map { value -> Double in
            guard let number = AutomationValue.double(value) else {
                throw AutomationError(.invalidParams, "time must be a number of seconds.")
            }
            return number
        }
        let frame = try params["frame"].map { value -> Int in
            guard let number = AutomationValue.int(value) else {
                throw AutomationError(.invalidParams, "frame must be an integer.")
            }
            return number
        }
        guard (time == nil) != (frame == nil) else {
            throw AutomationError(.invalidParams, "Pass exactly one of time or frame.")
        }

        let result: AutomationMediaEditing.FrameResult
        switch AutomationLibrary.kind(forPath: source.path) {
        case "video":
            guard let time else {
                throw AutomationError(.invalidParams, "Videos take time in seconds; frame applies to GIFs only.")
            }
            result = try await AutomationMediaEditing.saveVideoFrame(source: source, time: time)
        case "gif":
            result = try AutomationMediaEditing.saveGifFrame(source: source, time: time, frame: frame)
        default:
            throw AutomationError(.invalidParams, "editor.saveFrame works on videos and GIFs only.")
        }
        SaveService.shared.handleSavedFile(url: result.url, type: .screenshot)
        var response: [String: Any] = [
            "path": result.url.path,
            "sourcePath": source.path,
            "time": result.time,
        ]
        if let index = result.frame {
            response["frame"] = index
        }
        return response
    }

    private func startVideo(_ params: [String: Any]) async throws -> [String: Any] {
        var studio = false
        if let value = params["studio"] {
            guard let bool = AutomationValue.bool(value) else {
                throw AutomationError(.invalidParams, "studio must be true or false.")
            }
            studio = bool
        }
        if studio, !CaptureSettings.shared.studioPreviewEnabled {
            throw AutomationError(.notSupported, "Tiny Clips Studio is turned off. Turn it on in Settings → Studio.")
        }
        let manager = try captureManager()
        try requireIdle(manager)
        try requireScreenRecordingPermission()
        try requireDevicePermissionIfUsed(CaptureSettings.shared)
        let target = try await manager.resolveAutomationCaptureTarget(targetParams(from: params))
        try await manager.startAutomationVideoRecording(forStudio: studio, target: target)
        return ["started": true, "studio": studio]
    }

    private func startGif(_ params: [String: Any]) async throws -> [String: Any] {
        let manager = try captureManager()
        try requireIdle(manager)
        try requireScreenRecordingPermission()
        let target = try await manager.resolveAutomationCaptureTarget(AutomationCaptureTargets.parse(params))
        try await manager.startAutomationGifRecording(target: target)
        return ["started": true]
    }

    private func targetParams(from params: [String: Any]) throws -> AutomationCaptureTarget {
        var targetParams = params
        targetParams.removeValue(forKey: "studio")
        return try AutomationCaptureTargets.parse(targetParams)
    }

    private func startScrollingCapture(_ params: [String: Any]) async throws -> [String: Any] {
        let manager = try captureManager()
        try requireIdle(manager)
        try requireScreenRecordingPermission()
        let target = try await manager.resolveAutomationCaptureTarget(AutomationCaptureTargets.parse(params))
        try await manager.startAutomationScrollingCapture(region: target.region)
        return ["started": true]
    }

    private func stopScrollingCapture() async throws -> [String: Any] {
        let manager = try captureManager()
        let url = try await manager.stopAutomationScrollingCapture()
        return ["path": url.path]
    }

    private func scrollingCaptureStatus() throws -> [String: Any] {
        let status = try captureManager().automationScrollingCaptureStatus
        return ["active": status.active, "stopping": status.stopping, "frameCount": status.frameCount]
    }

    private func cancelScrollingCapture() throws -> [String: Any] {
        try captureManager().cancelAutomationScrollingCapture()
        return ["cancelled": true]
    }

    private func requireRecording(_ manager: CaptureManager) throws {
        guard manager.isRecording else {
            throw AutomationError(.busy, "No recording is in progress.")
        }
    }

    private func stop() throws -> [String: Any] {
        let manager = try captureManager()
        try requireRecording(manager)
        manager.stopRecording()
        return ["stopped": true]
    }

    private func togglePause() throws -> [String: Any] {
        let manager = try captureManager()
        try requireRecording(manager)
        manager.togglePauseRecording()
        return ["paused": manager.isRecordingPaused]
    }

    private func restart() throws -> [String: Any] {
        let manager = try captureManager()
        try requireRecording(manager)
        manager.restartRecording()
        return ["restarted": true]
    }

    private func discard() throws -> [String: Any] {
        let manager = try captureManager()
        try requireRecording(manager)
        manager.discardRecording()
        return ["discarded": true]
    }

    // MARK: - Files

    private func existingFile(_ params: [String: Any]) throws -> URL {
        let path = try Self.requiredString(params, "path")
        let url = URL(fileURLWithPath: (path as NSString).expandingTildeInPath).standardizedFileURL
        guard FileManager.default.fileExists(atPath: url.path) else {
            throw AutomationError(.notFound, "No file at \(url.path).")
        }
        return url
    }

    /// Delete and upload act only on captures the library shows, never on arbitrary files.
    private func libraryFile(_ params: [String: Any]) throws -> URL {
        let url = try existingFile(params)
        let resolved = url.resolvingSymlinksInPath().path
        let inLibraryFolder = libraryDirectories().contains { directory in
            let folder = directory.resolvingSymlinksInPath().path
            return (resolved as NSString).deletingLastPathComponent == folder
        }
        let isRecent = RecentCaptureStore.shared.items.contains {
            URL(fileURLWithPath: $0.path).resolvingSymlinksInPath().path == resolved
        }
        guard (inLibraryFolder || isRecent), AutomationLibrary.kind(forPath: resolved) != nil else {
            throw AutomationError(.notFound, "\(url.lastPathComponent) is not a capture in the Tiny Clips library.")
        }
        return url
    }

    private func openEditor(_ params: [String: Any]) throws -> [String: Any] {
        let url = try existingFile(params)
        let manager = try captureManager()
        if url.pathExtension.lowercased() == StudioProjectStore.projectFileExtension
            || StudioProjectStore.shared.isSavedProjectFolder(url) {
            guard CaptureSettings.shared.studioPreviewEnabled else {
                throw AutomationError(.notSupported, "Tiny Clips Studio is turned off. Turn it on in Settings → Studio.")
            }
            StudioWindowRegistry.shared.openProjectFile(at: url)
            return ["opened": url.path, "editor": "studio"]
        }
        let editor: String
        let type: CaptureType
        switch AutomationLibrary.kind(forPath: url.path) {
        case "screenshot": type = .screenshot; editor = "screenshot"
        case "video": type = .video; editor = "videoTrimmer"
        case "gif": type = .gif; editor = "gifTrimmer"
        default:
            throw AutomationError(.invalidParams, "Tiny Clips cannot edit .\(url.pathExtension) files.")
        }
        manager.openRecentCapture(RecentCaptureItem(path: url.path, type: type))
        return ["opened": url.path, "editor": editor]
    }

    // MARK: - Studio

    private func requireStudioEnabled() throws {
        guard CaptureSettings.shared.studioPreviewEnabled else {
            throw AutomationError(.notSupported, "Tiny Clips Studio is turned off. Turn it on in Settings → Studio.")
        }
    }

    private func studioProject(_ id: String) throws -> StudioProject {
        let store = StudioProjectStore.shared
        guard store.exists(id: id) else {
            throw AutomationError(.notFound, "No Studio project with ID \(id).")
        }
        return try store.load(id: id)
    }

    private func currentStudioProject(_ id: String) throws -> StudioProject {
        if let viewModel = StudioWindowRegistry.shared.automationViewModel(forProjectID: id) {
            guard let project = viewModel.automationProjectSnapshot() else {
                throw AutomationError(.busy, "The Studio project is not ready for agent control.")
            }
            return project
        }
        return try studioProject(id)
    }

    private func listStudioProjects(_ params: [String: Any]) throws -> [String: Any] {
        try requireStudioEnabled()
        let limit = params["limit"] as? Int ?? 20
        guard (1...100).contains(limit) else {
            throw AutomationError(.invalidParams, "limit must be a whole number from 1 to 100.")
        }
        let projects = try StudioProjectStore.shared.listSummaries()
            .sorted { max($0.createdAt, $0.lastOpenedAt) > max($1.createdAt, $1.lastOpenedAt) }
            .prefix(limit)
            .map { summary in
                [
                    "projectId": summary.id,
                    "name": summary.name,
                    "createdAt": StudioJSON.timestampString(from: summary.createdAt),
                    "lastOpenedAt": StudioJSON.timestampString(from: summary.lastOpenedAt),
                    "isDraft": summary.isDraft,
                    "isFlat": summary.isFlat,
                    "keepSources": summary.keepSources,
                    "sourceExists": summary.sourceExists
                ] as [String: Any]
            }
        return ["projects": projects]
    }

    private func getStudioProject(_ params: [String: Any]) throws -> [String: Any] {
        try requireStudioEnabled()
        let id = try Self.requiredString(params, "projectId")
        let project = try currentStudioProject(id)
        return [
            "project": try studioProjectObject(project),
            "revision": try studioProjectRevision(project)
        ]
    }

    private func editStudioProject(_ params: [String: Any]) throws -> [String: Any] {
        try requireStudioEnabled()
        let id = try Self.requiredString(params, "projectId")
        let expectedModifiedAt = try Self.requiredString(params, "expectedModifiedAt")
        let expectedRevision = try Self.requiredString(params, "expectedRevision")
        guard let patch = params["patch"] as? [String: Any],
              !patch.isEmpty,
              Set(patch.keys).isSubset(of: Self.studioEditableProjectKeys)
        else {
            throw AutomationError(.invalidParams, "patch must contain editable Studio project properties only.")
        }

        let current = try currentStudioProject(id)
        guard StudioJSON.timestampString(from: current.modifiedAt) == expectedModifiedAt,
              try studioProjectRevision(current) == expectedRevision
        else {
            throw AutomationError(.conflict, "The Studio project changed since it was read. Get it again before editing.")
        }

        let updated: StudioProject
        do {
            updated = try applyingStudioPatch(patch, to: current)
        } catch {
            throw AutomationError(.invalidParams, "The Studio patch is invalid: \(error.localizedDescription)")
        }
        let saved: StudioProject
        if let viewModel = StudioWindowRegistry.shared.automationViewModel(forProjectID: id) {
            guard viewModel.applyAutomationProject(updated, expectedModifiedAt: current.modifiedAt),
                  let latest = viewModel.automationProjectSnapshot()
            else {
                throw AutomationError(.conflict, "The Studio project changed or became unavailable while applying the edit.")
            }
            saved = latest
        } else {
            saved = try StudioProjectStore.shared.save(updated)
        }
        return [
            "project": try studioProjectObject(saved),
            "revision": try studioProjectRevision(saved)
        ]
    }

    private func suggestStudioZooms(_ params: [String: Any]) throws -> [String: Any] {
        try requireStudioEnabled()
        let id = try Self.requiredString(params, "projectId")
        let project = try currentStudioProject(id)
        let events = try StudioProjectStore.shared.loadEvents(id: id)
        let suggestions = StudioLayoutResolver.suggestZooms(project: project, events: events)
        return [
            "projectId": id,
            "suggestions": try studioJSONValue(suggestions),
            "count": suggestions.count
        ]
    }

    private func exportStudioProject(_ params: [String: Any]) async throws -> [String: Any] {
        try requireStudioEnabled()
        let id = try Self.requiredString(params, "projectId")
        let rawPath = try Self.requiredString(params, "outputPath")
        let outputURL = URL(fileURLWithPath: (rawPath as NSString).expandingTildeInPath).standardizedFileURL
        let outputDirectory = SaveService.shared.outputDirectoryURL(for: .video).resolvingSymlinksInPath()
        guard outputURL.pathExtension.lowercased() == "mp4",
              outputURL.deletingLastPathComponent().resolvingSymlinksInPath() == outputDirectory
        else {
            throw AutomationError(.invalidParams, "outputPath must be an MP4 directly inside the configured video folder.")
        }
        guard FileManager.default.fileExists(atPath: outputDirectory.path) else {
            throw AutomationError(.internal, "The configured video-save folder does not exist.")
        }
        guard !FileManager.default.fileExists(atPath: outputURL.path) else {
            throw AutomationError(.invalidParams, "The export destination already exists; choose a new filename.")
        }

        let project: StudioProject
        let events: StudioEvents
        if let viewModel = StudioWindowRegistry.shared.automationViewModel(forProjectID: id) {
            guard let prepared = viewModel.prepareAutomationExport() else {
                throw AutomationError(.busy, "The Studio project is not ready to export.")
            }
            project = prepared.0
            events = prepared.1
        } else {
            project = try studioProject(id)
            events = try StudioProjectStore.shared.loadEvents(id: id)
        }
        let store = StudioProjectStore.shared
        let paths = try store.paths(for: project)
        let resolvedOutput = outputURL.resolvingSymlinksInPath()
        let sourcePaths = [paths.screenURL.resolvingSymlinksInPath().path]
            + [paths.cameraURL].compactMap { $0?.resolvingSymlinksInPath().path }
        guard !sourcePaths.contains(resolvedOutput.path),
              !resolvedOutput.path.hasPrefix(paths.projectDirectory.resolvingSymlinksInPath().path + "/")
        else {
            throw AutomationError(.invalidParams, "The export destination cannot be inside the Studio project or overwrite a source recording.")
        }

        let size = StudioCanvasMath.exportSize(project: project, longSideLimit: 3840)
        do {
            _ = try await StudioExporter.export(
                project: project,
                events: events,
                paths: paths,
                outputURL: outputURL,
                renderSize: CGSize(width: size.width, height: size.height),
                codec: CaptureSettings.shared.videoCodec
            )
            _ = try store.recordExport(id: id, path: outputURL.path)
            SaveService.shared.handleSavedFile(url: outputURL, type: .video)
            return ["projectId": id, "path": outputURL.path]
        } catch {
            throw AutomationError(.internal, "Studio export failed: \(error.localizedDescription)")
        }
    }

    private static let studioEditableProjectKeys: Set<String> = [
        "canvas", "screen", "camera", "scenes", "zooms", "edits", "audio", "overlays"
    ]

    private func applyingStudioPatch(_ patch: [String: Any], to project: StudioProject) throws -> StudioProject {
        var object = try studioProjectObject(project)
        Self.mergeStudioJSON(patch, into: &object)
        let data = try JSONSerialization.data(withJSONObject: object)
        return try StudioJSON.makeDecoder().decode(StudioProject.self, from: data)
    }

    private func studioProjectObject(_ project: StudioProject) throws -> [String: Any] {
        let data = try StudioJSON.makeEncoder().encode(project)
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw AutomationError(.internal, "Studio project serialization did not produce an object.")
        }
        return object
    }

    private func studioProjectRevision(_ project: StudioProject) throws -> String {
        let data = try StudioJSON.makeEncoder().encode(project)
        return SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    private func studioJSONValue<T: Encodable>(_ value: T) throws -> Any {
        let data = try StudioJSON.makeEncoder().encode(value)
        return try JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed])
    }

    private static func mergeStudioJSON(_ patch: [String: Any], into target: inout [String: Any]) {
        for (key, value) in patch {
            if let patchObject = value as? [String: Any], var targetObject = target[key] as? [String: Any] {
                mergeStudioJSON(patchObject, into: &targetObject)
                target[key] = targetObject
            } else {
                target[key] = value
            }
        }
    }

    private func reveal(_ params: [String: Any]) throws -> [String: Any] {
        let url = try existingFile(params)
        NSWorkspace.shared.activateFileViewerSelecting([url])
        return ["revealed": url.path]
    }

    private func copy(_ params: [String: Any]) throws -> [String: Any] {
        let url = try existingFile(params)
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        guard pasteboard.writeObjects([url as NSURL]) else {
            throw AutomationError(.internal, "Could not copy the file to the clipboard.")
        }
        return ["copied": url.path]
    }

    private func delete(_ params: [String: Any]) async throws -> [String: Any] {
        let url = try libraryFile(params)
        do {
            let trashed = try await NSWorkspace.shared.recycle([url])
            RecentCaptureStore.shared.remove(url: url)
            ClipMetadataStore.shared.remove(path: url.path)
            return ["deleted": url.path, "trashPath": trashed[url]?.path ?? NSNull()]
        } catch {
            throw AutomationError(.internal, "Could not move \(url.lastPathComponent) to the Trash: \(error.localizedDescription)")
        }
    }

    private func upload(_ params: [String: Any]) async throws -> [String: Any] {
        let url = try libraryFile(params)
        let settings = CaptureSettings.shared
        let credentials = UploadcareCredentialsStore.shared.credentials()
        let publicKey = credentials.publicKey.trimmingCharacters(in: .whitespacesAndNewlines)
        let secretKey = credentials.secretKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard settings.uploadcareEnabled, !publicKey.isEmpty, !secretKey.isEmpty else {
            throw AutomationError(.notSupported, "No upload service is set up. Configure Uploadcare in the Clips Library settings.")
        }
        do {
            let result = try await UploadcareService.shared.upload(
                fileURL: url,
                publicKey: publicKey,
                secretKey: secretKey
            )
            ClipMetadataStore.shared.upsert(path: url.path) { metadata in
                metadata.uploadcareURL = result.fileURL.absoluteString
            }
            return ["url": result.fileURL.absoluteString, "path": url.path]
        } catch {
            throw AutomationError(.internal, "Upload failed: \(error.localizedDescription)")
        }
    }

    private static func requiredString(_ params: [String: Any], _ key: String) throws -> String {
        guard let value = params[key] as? String, !value.isEmpty else {
            throw AutomationError(.invalidParams, "\(key) is required.")
        }
        return value
    }
}

/// Finds capture files the same way the Clips Library does.
enum AutomationLibrary {
    struct Item: Equatable {
        var path: String
        var kind: String
        var createdAt: Date
        var sizeBytes: Int
    }

    static func kind(forPath path: String) -> String? {
        switch (path as NSString).pathExtension.lowercased() {
        case "png", "jpg", "jpeg", "webp", "heic": return "screenshot"
        case "mp4", "mov": return "video"
        case "gif": return "gif"
        default: return nil
        }
    }

    /// Newest first, without duplicates.
    static func scan(directories: [URL], extraPaths: [String], tinyClipsOnly: Bool) -> [Item] {
        let keys: [URLResourceKey] = [.isRegularFileKey, .fileSizeKey, .creationDateKey]
        var urls: [URL] = []
        for directory in directories {
            let contents = (try? FileManager.default.contentsOfDirectory(
                at: directory,
                includingPropertiesForKeys: keys,
                options: .skipsHiddenFiles
            )) ?? []
            urls.append(contentsOf: contents.filter {
                !tinyClipsOnly || $0.lastPathComponent.hasPrefix("TinyClips ")
            })
        }
        urls.append(contentsOf: extraPaths.map { URL(fileURLWithPath: $0) })

        var seen = Set<String>()
        var items: [Item] = []
        for url in urls {
            let path = url.standardizedFileURL.path
            guard seen.insert(path).inserted, let kind = kind(forPath: path),
                  let values = try? url.resourceValues(forKeys: Set(keys)),
                  values.isRegularFile == true,
                  let size = values.fileSize, size > 0 else { continue }
            items.append(Item(path: path, kind: kind, createdAt: values.creationDate ?? .distantPast, sizeBytes: size))
        }
        return items.sorted { $0.createdAt > $1.createdAt }
    }
}
