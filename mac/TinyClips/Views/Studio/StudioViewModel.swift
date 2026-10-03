import AppKit
import AVFoundation
import SwiftUI

// MARK: - Studio View Model

/// Drives one Studio editor window: the project being edited, the live preview, autosave, and export.
///
/// The preview always plays the whole source recording, so `playhead` is in source time. Trimming
/// and muting are applied by the transport here and by the exporter, never by rebuilding the player.
@MainActor
final class StudioViewModel: ObservableObject {
    enum LoadState: Equatable {
        case loading
        case ready
        case unavailable(String)
    }

    static let previewLongSide = 1600.0
    static let exportLongSide = 3840.0
    static let posterLongSide = 640.0

    let projectID: String

    @Published private(set) var state: LoadState = .loading
    @Published private(set) var editor: StudioEditorModel?
    @Published private(set) var player: AVPlayer?
    @Published private(set) var playhead: Double = 0
    @Published private(set) var isPlaying = false
    @Published private(set) var isExporting = false
    @Published private(set) var exportProgress: Double = 0

    /// Set by the window. Called to close it once an export started from the close prompt is done.
    var requestClose: (() -> Void)?

    private let store: StudioProjectStore
    private var events = StudioEvents()
    private var paths: StudioProjectPaths?
    private var playback: StudioPlayback?
    private var previewSize = CGSize.zero
    private var timeObserver: Any?
    private var rateObservation: NSKeyValueObservation?
    private var saveTask: Task<Void, Never>?
    private var previewTask: Task<Void, Never>?
    private var exportTask: Task<Void, Never>?
    private var hasUnsavedEdits = false
    private var closesAfterExport = false
    private var deletesOnClose = false
    private var isTornDown = false

    init(projectID: String, store: StudioProjectStore = .shared) {
        self.projectID = projectID
        self.store = store
    }

    // MARK: - Derived State

    var project: StudioProject? { editor?.project }
    var isReady: Bool { state == .ready }
    var isEditable: Bool { state == .ready && !isExporting }
    var canUndo: Bool { isEditable && (editor?.canUndo ?? false) }
    var canRedo: Bool { isEditable && (editor?.canRedo ?? false) }
    var canExport: Bool { isEditable && (editor?.outputDuration ?? 0) > 0 }
    var hasCamera: Bool { editor?.hasCamera ?? false }

    var clipName: String {
        let name = project?.name.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return name.isEmpty ? "Untitled Recording" : name
    }

    var previewRenderSize: CGSize {
        guard let project else { return CGSize(width: 1280, height: 720) }
        return Self.renderSize(for: project, longSide: Self.previewLongSide)
    }

    var exportRenderSize: CGSize {
        guard let project else { return CGSize(width: 1920, height: 1080) }
        return Self.renderSize(for: project, longSide: Self.exportLongSide)
    }

    var timeText: String {
        guard let editor else { return "0:00.0 / 0:00.0" }
        let position = StudioEditorModel.formattedTime(editor.outputTime(forSourceTime: playhead))
        return "\(position) / \(StudioEditorModel.formattedTime(editor.outputDuration))"
    }

    /// The camera bubble on a canvas of `canvasSize`, when the bubble layout is showing.
    func bubbleRect(canvasSize: CGSize) -> StudioRect? {
        guard let editor, editor.effectiveLayout == .bubble else { return nil }
        return editor.bubbleRect(canvasSize: canvasSize)
    }

    // MARK: - Loading

    func load() async {
        guard state == .loading, editor == nil else { return }
        do {
            let opened = try store.markOpened(id: projectID)
            let openedPaths = try store.paths(for: opened)
            let model = StudioEditorModel(project: opened)
            paths = openedPaths
            editor = model

            guard FileManager.default.fileExists(atPath: openedPaths.screenURL.path) else {
                state = .unavailable(
                    "The original recording for this project is no longer on this Mac, so it cannot be previewed or exported here."
                )
                return
            }

            events = (try? store.loadEvents(id: projectID)) ?? StudioEvents()
            let size = Self.renderSize(for: model.project, longSide: Self.previewLongSide)
            let newPlayback = StudioPlayback(
                project: model.previewProject,
                events: events,
                paths: openedPaths,
                renderSize: size
            )
            previewSize = size
            playback = newPlayback
            try await newPlayback.makePlayerItem()
            guard !isTornDown else { return }

            newPlayback.player.isMuted = model.project.audio.muted
            newPlayback.player.actionAtItemEnd = .pause
            observe(newPlayback.player)
            player = newPlayback.player
            state = .ready
            seek(to: model.trimStart)
        } catch {
            state = .unavailable(error.localizedDescription)
        }
    }

    // MARK: - Edits

    /// Starts a gesture such as a drag, so everything until `endGesture()` is one undo step.
    func beginGesture() {
        guard isEditable else { return }
        editor?.beginEditingGroup()
    }

    func endGesture() {
        editor?.commitEditingGroup()
    }

    func undo() {
        edit { $0.undo() }
    }

    func redo() {
        edit { $0.redo() }
    }

    func setLayout(_ layout: StudioLayout) {
        edit { $0.setLayout(layout) }
    }

    func setCanvasAspect(_ aspect: StudioCanvasAspect) {
        edit { $0.setCanvasAspect(aspect) }
    }

    func setCanvasPadding(_ value: Double) {
        edit { $0.setCanvasPadding(value) }
    }

    func applyBackgroundPreset(_ preset: ExportBackgroundPreset) {
        let primary = Self.hexString(for: preset.primary)
        let secondary = preset.secondary.map { Self.hexString(for: $0) }
        switch preset.style {
        case .solid:
            edit { $0.setBackground(style: .solid, preset: preset.id, primary: primary, secondary: nil) }
        case .gradient:
            edit { $0.setBackground(style: .gradient, preset: preset.id, primary: primary, secondary: secondary) }
        case .transparent, .wallpaper:
            break
        }
    }

    /// No background: the canvas is black, as in a recording made without Studio.
    func removeBackground() {
        edit {
            let current = $0.project.canvas.background
            $0.setBackground(style: .none, preset: nil, primary: current.primary, secondary: current.secondary)
        }
    }

    func setScreenCornerRadius(_ value: Double) {
        edit { $0.setScreenCornerRadius(value) }
    }

    func setScreenShadow(_ value: Double) {
        edit { $0.setScreenShadow(value) }
    }

    func setCameraShape(_ shape: StudioCameraShape) {
        edit { $0.setCameraShape(shape) }
    }

    func setCameraCornerRadius(_ value: Double) {
        edit { $0.setCameraCornerRadius(value) }
    }

    func setCameraBubbleSize(_ value: Double) {
        edit { $0.setCameraBubbleSize(value) }
    }

    func setCameraAnchor(_ anchor: StudioAnchor) {
        edit { $0.setCameraAnchor(anchor) }
    }

    func setCameraBubbleOffset(x: Double, y: Double) {
        edit { $0.setCameraBubbleOffsets(x: x, y: y) }
    }

    func moveBubble(topLeft: CGPoint, canvasSize: CGSize) {
        edit { $0.moveBubble(topLeft: topLeft, canvasSize: canvasSize) }
    }

    func setCameraMirror(_ value: Bool) {
        edit { $0.setCameraMirror(value) }
    }

    func setCameraBorderWidth(_ value: Double) {
        edit { $0.setCameraBorderWidth(value) }
    }

    func setCameraShadow(_ value: Double) {
        edit { $0.setCameraShadow(value) }
    }

    func setSideBySide(side: StudioCameraSide, fraction: Double) {
        edit { $0.setSideBySide(cameraSide: side, fraction: fraction) }
    }

    func setMuted(_ value: Bool) {
        edit { $0.setMuted(value) }
    }

    func setClickRingsEnabled(_ value: Bool) {
        edit { $0.setClickRingsEnabled(value) }
    }

    func setBrandingEnabled(_ value: Bool) {
        edit { $0.setBrandingEnabled(value) }
    }

    /// Moves the trim start and shows the frame it now starts on.
    func setTrimStart(_ sourceTime: Double) {
        edit { $0.setTrimStart(sourceTime) }
        if let editor, isEditable {
            scrub(to: editor.trimStart)
        }
    }

    /// Moves the trim end and shows the frame it now ends on.
    func setTrimEnd(_ sourceTime: Double) {
        edit { $0.setTrimEnd(sourceTime) }
        if let editor, isEditable {
            scrub(to: editor.trimEnd)
        }
    }

    func setTrimStartAtPlayhead() {
        let time = playhead
        edit { $0.setTrimStart(time) }
    }

    func setTrimEndAtPlayhead() {
        let time = playhead
        edit { $0.setTrimEnd(time) }
    }

    func saveDefaultLook() {
        guard let editor else { return }
        CaptureSettings.shared.studioDefaultLook = editor.currentLook
        SaveService.shared.showNotice("Saved. New Studio recordings will start with this look.")
    }

    // MARK: - Transport

    func togglePlayback() {
        guard isReady, !isExporting, let player, let editor else { return }
        if isPlaying {
            pause()
            return
        }
        let start = editor.playbackStart(from: playhead)
        if abs(start - playhead) > editor.frameDuration / 2 {
            seek(to: start)
        }
        player.play()
        isPlaying = true
    }

    func pause() {
        player?.pause()
        isPlaying = false
    }

    func stepFrame(by count: Int) {
        guard isReady, !isExporting, let editor else { return }
        pause()
        seek(to: playhead + Double(count) * editor.frameDuration)
    }

    /// Pauses and moves the playhead, for dragging along the timeline.
    func scrub(to sourceTime: Double) {
        guard isReady, !isExporting else { return }
        pause()
        seek(to: sourceTime)
    }

    // MARK: - Export

    func export() {
        startExport(closingAfterward: false)
    }

    func cancelExport() {
        exportTask?.cancel()
    }

    private func startExport(closingAfterward: Bool) {
        guard canExport, let paths else { return }
        pause()
        // The export link is written into the saved project, so the edits have to be on disk first.
        guard saveNow(), let editor else { return }

        isExporting = true
        exportProgress = 0
        closesAfterExport = closingAfterward

        let project = editor.project
        let rendered = editor.editableState
        let projectEvents = events
        let projectStore = store
        let outputURL = SaveService.shared.generateURL(for: .video)
        let renderSize = Self.renderSize(for: project, longSide: Self.exportLongSide)
        let posterSize = Self.renderSize(for: project, longSide: Self.posterLongSide)
        let codec = CaptureSettings.shared.videoCodec
        let reportProgress: @Sendable (Double) -> Void = { [weak self] progress in
            Task { @MainActor in
                self?.exportProgress = progress
            }
        }

        exportTask = Task { [weak self] in
            do {
                let url = try await StudioExporter.export(
                    project: project,
                    events: projectEvents,
                    paths: paths,
                    outputURL: outputURL,
                    renderSize: renderSize,
                    codec: codec,
                    onProgress: reportProgress
                )
                try? await StudioExporter.writePoster(
                    project: project,
                    events: projectEvents,
                    paths: paths,
                    posterURL: paths.posterURL,
                    renderSize: posterSize,
                    time: 0
                )
                let saved = try projectStore.recordExport(id: project.id, path: url.path)
                guard let self else { return }
                self.editor?.refreshBookkeeping(from: saved)
                self.editor?.markExported(rendered)
                self.isExporting = false
                self.exportProgress = 1
                SaveService.shared.handleSavedFile(url: url, type: .video)
                if self.closesAfterExport {
                    self.closesAfterExport = false
                    self.requestClose?()
                }
            } catch is CancellationError {
                self?.exportDidNotFinish(message: nil)
            } catch {
                self?.exportDidNotFinish(message: error.localizedDescription)
            }
        }
    }

    private func exportDidNotFinish(message: String?) {
        isExporting = false
        exportProgress = 0
        closesAfterExport = false
        if let message, !isTornDown {
            SaveService.shared.showError("Studio export failed: \(message)")
        }
    }

    // MARK: - Closing

    /// Asked when Esc is pressed. Returns false to keep the window open.
    ///
    /// Edits are saved with the project, so there is nothing to lose; the question only guards
    /// against a stray Esc, and follows the setting shared by the other editors. A project that was
    /// never exported is not asked here, because closing it asks what to do with it anyway.
    func confirmEscapeClose() -> Bool {
        guard CaptureSettings.shared.confirmEditorEscape,
              isReady, let editor, !editor.hasNeverExported
        else {
            return true
        }
        pause()

        let alert = NSAlert()
        alert.messageText = "Close Studio?"
        alert.informativeText = "Your edits are saved with the project, and you can reopen it from the Clips Manager. You can turn off this confirmation in General settings."
        alert.addButton(withTitle: "Close Studio")
        alert.addButton(withTitle: "Cancel")
        return alert.runModal() == .alertFirstButtonReturn
    }

    /// Asked when the user closes the window. Returns false to keep it open.
    func shouldClose() -> Bool {
        if isExporting {
            let alert = NSAlert()
            alert.messageText = "An export is still running."
            alert.informativeText = "Closing the window stops the export. Your edits are kept."
            alert.addButton(withTitle: "Keep Exporting")
            alert.addButton(withTitle: "Stop and Close")
            guard alert.runModal() == .alertSecondButtonReturn else { return false }
            closesAfterExport = false
            exportTask?.cancel()
            return true
        }

        guard isReady, let editor, editor.hasNeverExported else { return true }
        pause()

        let alert = NSAlert()
        alert.messageText = "Export this recording before closing?"
        alert.informativeText = "It has not been exported yet. Export it now, keep it as a draft to finish later, or delete it."
        alert.addButton(withTitle: "Export")
        alert.addButton(withTitle: "Keep as Draft")
        let deleteButton = alert.addButton(withTitle: "Delete")
        deleteButton.hasDestructiveAction = true

        switch alert.runModal() {
        case .alertFirstButtonReturn:
            startExport(closingAfterward: true)
            return false
        case .alertThirdButtonReturn:
            deletesOnClose = true
            return true
        default:
            return true
        }
    }

    /// Called once, when the window is closing for good.
    func tearDown() {
        guard !isTornDown else { return }
        isTornDown = true
        saveTask?.cancel()
        previewTask?.cancel()
        exportTask?.cancel()
        rateObservation?.invalidate()
        rateObservation = nil
        if let timeObserver, let player {
            player.removeTimeObserver(timeObserver)
        }
        timeObserver = nil
        player?.pause()
        player?.replaceCurrentItem(with: nil)
        isPlaying = false

        if deletesOnClose {
            try? store.delete(id: projectID)
            return
        }

        saveNow()
        if isReady, let project = editor?.project, let paths {
            let projectEvents = events
            let posterSize = Self.renderSize(for: project, longSide: Self.posterLongSide)
            // The Clips Library thumbnail. It outlives the window on purpose.
            Task {
                try? await StudioExporter.writePoster(
                    project: project,
                    events: projectEvents,
                    paths: paths,
                    posterURL: paths.posterURL,
                    renderSize: posterSize,
                    time: 0
                )
            }
        }
    }

    // MARK: - Private: Edits

    private func edit(_ change: (inout StudioEditorModel) -> Void) {
        guard isEditable, var model = editor else { return }
        let before = model.editableState
        change(&model)
        editor = model
        guard model.editableState != before else { return }
        hasUnsavedEdits = true
        scheduleSave()
        player?.isMuted = model.project.audio.muted
        if isPlaying, model.isAtPlaybackEnd(playhead) {
            pause()
        }
        refreshPreview()
    }

    /// Redraws the preview for the current project. Only a new canvas size rebuilds the player item.
    private func refreshPreview() {
        guard let model = editor, let playback else { return }
        let project = model.previewProject
        let size = Self.renderSize(for: model.project, longSide: Self.previewLongSide)
        let newSize: CGSize? = size == previewSize ? nil : size
        previewSize = size
        let resumeTime = playhead
        let wasPlaying = isPlaying

        previewTask = Task { [weak self] in
            do {
                let previousItem = playback.player.currentItem
                let item = try await playback.apply(project: project, renderSize: newSize)
                guard let self, !self.isTornDown else { return }
                if item !== previousItem {
                    // A new player item starts at zero, so put the playhead back.
                    self.seek(to: resumeTime)
                    if wasPlaying {
                        playback.player.play()
                    }
                }
            } catch {
                guard let self, !self.isTornDown else { return }
                SaveService.shared.showError("Studio preview failed: \(error.localizedDescription)")
            }
        }
    }

    private func scheduleSave() {
        saveTask?.cancel()
        saveTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 600_000_000)
            guard !Task.isCancelled else { return }
            self?.saveNow()
        }
    }

    /// Writes the edits into the project on disk. Only the edited parts are replaced, so export
    /// links or names changed elsewhere (the Clips Library, for example) are not overwritten.
    @discardableResult
    private func saveNow() -> Bool {
        saveTask?.cancel()
        saveTask = nil
        guard hasUnsavedEdits, let model = editor else { return true }
        do {
            let onDisk = try store.load(id: projectID)
            let saved = try store.save(model.editableState.applied(to: onDisk))
            editor?.refreshBookkeeping(from: saved)
            hasUnsavedEdits = false
            return true
        } catch {
            SaveService.shared.showError("Studio could not save this project: \(error.localizedDescription)")
            return false
        }
    }

    // MARK: - Private: Playback

    private func seek(to sourceTime: Double) {
        guard let editor else { return }
        let time = editor.clampedSourceTime(sourceTime)
        playhead = time
        player?.seek(
            to: CMTime(seconds: time, preferredTimescale: 600),
            toleranceBefore: .zero,
            toleranceAfter: .zero
        )
    }

    private func observe(_ player: AVPlayer) {
        timeObserver = player.addPeriodicTimeObserver(
            forInterval: CMTime(value: 1, timescale: 30),
            queue: .main
        ) { [weak self] time in
            let seconds = time.seconds
            MainActor.assumeIsolated {
                guard let self else { return }
                self.playerDidReach(seconds)
            }
        }
        rateObservation = player.observe(\.rate, options: [.new]) { [weak self] _, _ in
            Task { @MainActor in
                guard let self else { return }
                self.playerRateDidChange()
            }
        }
    }

    private func playerDidReach(_ seconds: Double) {
        guard isPlaying, seconds.isFinite, let editor else { return }
        playhead = editor.clampedSourceTime(seconds)
        if editor.isAtPlaybackEnd(seconds) {
            pause()
            seek(to: editor.trimEnd)
        }
    }

    private func playerRateDidChange() {
        guard isPlaying, let player, player.rate == 0 else { return }
        // The player stopped by itself: the end of the recording, or an interruption.
        isPlaying = false
        let seconds = player.currentTime().seconds
        if seconds.isFinite, let editor {
            playhead = editor.clampedSourceTime(seconds)
        }
    }

    // MARK: - Private: Helpers

    private static func renderSize(for project: StudioProject, longSide: Double) -> CGSize {
        let size = StudioCanvasMath.exportSize(project: project, longSideLimit: longSide)
        return CGSize(width: max(2, size.width), height: max(2, size.height))
    }

    private static func hexString(for color: Color) -> String {
        let nsColor = NSColor(color)
        guard let resolved = nsColor.usingColorSpace(.sRGB) ?? nsColor.usingColorSpace(.deviceRGB) else {
            return "#000000"
        }
        return String(
            format: "#%02X%02X%02X",
            Int((min(max(resolved.redComponent, 0), 1) * 255).rounded()),
            Int((min(max(resolved.greenComponent, 0), 1) * 255).rounded()),
            Int((min(max(resolved.blueComponent, 0), 1) * 255).rounded())
        )
    }
}