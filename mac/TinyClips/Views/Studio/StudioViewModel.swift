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
    @Published private(set) var playhead: Double = 0 {
        didSet { followPlayheadIntoScene() }
    }
    @Published private(set) var isPlaying = false
    @Published private(set) var isExporting = false
    @Published private(set) var exportProgress: Double = 0

    /// The zoom the inspector shows, as its place in the project's zooms, or nil. It stays on its
    /// zoom while edits, undo, and redo change the list around it.
    @Published private(set) var selectedZoomIndex: Int?

    /// The cut the inspector shows, as its place in the project's cuts, or nil. A zoom, a cut, or
    /// a speed change is selected, never two of them: selecting one lets go of the others.
    @Published private(set) var selectedCutIndex: Int?

    /// The speed change the inspector shows, as its place in the project's speed changes, or nil.
    @Published private(set) var selectedSpeedIndex: Int?

    /// The inspector panel on show. Taking hold of a zoom, a cut, a speed change, a scene, or the
    /// camera shows the panel that edits it.
    @Published private(set) var inspectorPanel: StudioInspectorPanel = .background

    /// The size the preview is drawn at. It follows the project, except during a drag, when a new
    /// size waits for the drag to end so the player is not rebuilt for every step of it.
    @Published private(set) var previewSize = CGSize.zero

    /// Whether a project that cannot be shown still has its screen recording, to be saved as a
    /// video of its own. False unless the state is `unavailable`.
    @Published private(set) var canSaveScreenRecording = false

    /// True while the screen recording is being copied.
    @Published private(set) var isSavingScreenRecording = false

    /// What came of saving the screen recording: the name it got, or why it was not saved.
    @Published private(set) var screenRecordingStatus: String?

    /// What the window is waiting for while the project is copied to a folder, or nil. Nothing
    /// is edited meanwhile.
    @Published private(set) var busyMessage: String?

    /// The other projects in the store, the one opened last first, for Open Recent.
    @Published private(set) var recentProjects: [StudioProjectSummary] = []

    static let recentProjectLimit = 8

    /// Set by the window. Called to close it once an export started from the close prompt is done.
    var requestClose: (() -> Void)?

    private let store: StudioProjectStore
    private var events = StudioEvents()
    private var paths: StudioProjectPaths?
    private var playback: StudioPlayback?
    private var isInGesture = false
    private var needsPreviewRefresh = false
    private var isRefreshingPreview = false
    private var timeObserver: Any?
    private var rateObservation: NSKeyValueObservation?
    private var saveTask: Task<Void, Never>?
    private var previewTask: Task<Void, Never>?
    private var exportTask: Task<Void, Never>?
    private var hasUnsavedEdits = false
    private var closesAfterExport = false
    private var deletesOnClose = false
    private var isTornDown = false

    /// Where playback was last sent to get over a cut. Playback only goes forward, so it comes to
    /// the same cut again only after Play has been pressed, which forgets this.
    private var cutSkipTarget: Double?

    /// The rate the player was last asked to play at. It is compared with this and not with what
    /// the player says, which need not be the number it was given.
    private var requestedRate: Float = 1

    /// When the playhead was last moved by the playing player, on the system's clock.
    private var lastPlayheadMove: TimeInterval = 0

    init(projectID: String, store: StudioProjectStore = .shared) {
        self.projectID = projectID
        self.store = store
    }

    // MARK: - Derived State

    var project: StudioProject? { editor?.project }
    var isReady: Bool { state == .ready }
    var isEditable: Bool { state == .ready && !isExporting && busyMessage == nil }
    var isBusy: Bool { busyMessage != nil }

    /// Whether the project can be saved as a folder now.
    var canSaveProjectFolder: Bool { isEditable }

    /// Whether the project can be deleted now. One that cannot be shown can be: that is the
    /// project there is least reason to keep.
    var canDeleteProject: Bool { state != .loading && !isBusy }
    var canUndo: Bool { isEditable && (editor?.canUndo ?? false) }
    var canRedo: Bool { isEditable && (editor?.canRedo ?? false) }
    var canExport: Bool { isEditable && (editor?.outputDuration ?? 0) > 0 }
    var hasCamera: Bool { editor?.hasCamera ?? false }

    var clipName: String {
        let name = project?.name.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return name.isEmpty ? "Untitled Recording" : name
    }

    /// The canvas the preview shows, in pixels. This is the size the player was last built for, so
    /// what is laid out over the preview matches what is drawn in it.
    var previewRenderSize: CGSize {
        if previewSize.width > 0, previewSize.height > 0 {
            return previewSize
        }
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

    var zooms: [StudioZoom] { project?.zooms ?? [] }

    var selectedZoom: StudioZoom? {
        guard let index = selectedZoomIndex, zooms.indices.contains(index) else { return nil }
        return zooms[index]
    }

    /// Whether the recording has clicks to suggest zooms from. A recording of a window has none.
    var hasClicks: Bool { !events.clicks.isEmpty }

    /// Whether the recording has pointer positions for a zoom to follow. A recording of a window has none.
    var hasPointerPositions: Bool { !events.cursor.isEmpty }

    /// Whether adding a zoom at the playhead has a zoom to answer with: one fits there, or one is
    /// already there to select.
    var canAddZoomAtPlayhead: Bool { isEditable && (editor?.canAddZoom(at: playhead) ?? false) }

    var canSuggestZooms: Bool { isEditable && hasClicks }

    var hasSuggestedZooms: Bool { (editor?.suggestedZoomCount ?? 0) > 0 }

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
                becomeUnavailable(
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
            inspectorPanel = StudioInspectorPanel.initial(hasCamera: model.hasCamera)
            state = .ready
            refreshRecentProjects()
            seek(to: model.trimStart)
        } catch {
            becomeUnavailable(error.localizedDescription)
        }
    }

    /// The project cannot be shown. Whether its screen recording is still there is looked up
    /// now, so that the message can offer to save it.
    private func becomeUnavailable(_ message: String) {
        canSaveScreenRecording = store.screenRecordingURL(id: projectID) != nil
        state = .unavailable(message)
    }

    /// Saves the screen recording of a project that cannot be shown as an ordinary video, and
    /// says in `screenRecordingStatus` what came of it. The project is left as it is.
    func saveScreenRecording() {
        guard canSaveScreenRecording, !isSavingScreenRecording, !isTornDown else { return }
        isSavingScreenRecording = true
        screenRecordingStatus = nil
        let id = projectID
        Task { [weak self] in
            do {
                // A saved video is announced the way every saved video is, window or no window.
                let url = try await StudioMaintenance.saveScreenRecording(projectID: id)
                self?.screenRecordingStatus = "Saved as \(url.lastPathComponent)."
            } catch {
                let message = "The screen recording could not be saved: \(error.localizedDescription)"
                if let self, !self.isTornDown {
                    self.screenRecordingStatus = message
                    AccessibilityAnnouncementService.shared.announce(message, priority: .high)
                } else {
                    // The window closed while the recording was being copied, and the line
                    // that would have said this went with it.
                    SaveService.shared.showError(message)
                }
            }
            self?.isSavingScreenRecording = false
        }
    }

    // MARK: - Keeping

    /// Whether the project is pinned against automatic cleanup.
    var keepsSources: Bool { project?.keepSources ?? false }

    /// Pins the project against automatic cleanup, or lets go of it. It is written into the
    /// project on disk at once. It is not an edit: Undo leaves it alone, and it does not have
    /// to wait for an export to end.
    func setKeepsSources(_ keepSources: Bool) {
        guard !isTornDown, editor != nil, keepsSources != keepSources else { return }
        do {
            let saved = try store.setKeepSources(id: projectID, keepSources: keepSources)
            editor?.refreshBookkeeping(from: saved)
        } catch {
            // Nothing changed, so nothing would redraw the checkbox, which has already turned.
            objectWillChange.send()
            SaveService.shared.showError("Studio could not change whether this project is kept: \(error.localizedDescription)")
        }
    }

    // MARK: - Edits

    /// Starts a gesture such as a drag, so everything until `endGesture()` is one undo step.
    func beginGesture() {
        guard isEditable else { return }
        isInGesture = true
        editor?.beginEditingGroup()
    }

    func endGesture() {
        editor?.commitEditingGroup()
        guard isInGesture else { return }
        isInGesture = false
        // A canvas size that waited for the drag to end is applied now.
        if let project, Self.renderSize(for: project, longSide: Self.previewLongSide) != previewSize {
            refreshPreview()
        }
    }

    func undo() {
        edit { $0.undo() }
    }

    func redo() {
        edit { $0.redo() }
    }

    func setLayout(_ layout: StudioLayout) {
        editCurrentScene { $0.setLayout(layout) }
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
        editCurrentScene { $0.setCameraBubbleSize(value) }
    }

    func setCameraAnchor(_ anchor: StudioAnchor) {
        editCurrentScene { $0.setCameraAnchor(anchor) }
    }

    /// Moves the bubble sideways from its corner. The other offset stays as the scene has it.
    func setCameraBubbleOffsetX(_ x: Double) {
        editCurrentScene { model in
            let y = model.currentScene.bubble.offsetY
            model.setCameraBubbleOffsets(x: x, y: y)
        }
    }

    /// Moves the bubble up or down from its corner. The other offset stays as the scene has it.
    func setCameraBubbleOffsetY(_ y: Double) {
        editCurrentScene { model in
            let x = model.currentScene.bubble.offsetX
            model.setCameraBubbleOffsets(x: x, y: y)
        }
    }

    func moveBubble(topLeft: CGPoint, canvasSize: CGSize) {
        editCurrentScene { $0.moveBubble(topLeft: topLeft, canvasSize: canvasSize) }
    }

    func setCameraMirror(_ value: Bool) {
        edit { $0.setCameraMirror(value) }
    }

    func setCameraCutout(_ value: StudioCameraCutout) {
        edit { $0.setCameraCutout(value) }
    }

    func setCameraBorderWidth(_ value: Double) {
        edit { $0.setCameraBorderWidth(value) }
    }

    func setCameraShadow(_ value: Double) {
        edit { $0.setCameraShadow(value) }
    }

    /// Puts the camera on the other side. Its share stays as the scene has it.
    func setCameraSide(_ side: StudioCameraSide) {
        editCurrentScene { model in
            let fraction = model.currentScene.split.cameraFraction
            model.setSideBySide(cameraSide: side, fraction: fraction)
        }
    }

    /// Sets how much of the canvas the camera takes side by side. Its side stays as the scene has it.
    func setCameraShare(_ fraction: Double) {
        editCurrentScene { model in
            let side = model.currentScene.split.cameraSide
            model.setSideBySide(cameraSide: side, fraction: fraction)
        }
    }

    func setMuted(_ value: Bool) {
        edit { $0.setMuted(value) }
    }

    func setSystemVolume(_ value: Double) {
        edit { $0.setSystemVolume(value) }
    }

    func setMicrophoneVolume(_ value: Double) {
        edit { $0.setMicrophoneVolume(value) }
    }

    func setClickRingsEnabled(_ value: Bool) {
        edit { $0.setClickRingsEnabled(value) }
    }

    func setBrandingEnabled(_ value: Bool) {
        edit { $0.setBrandingEnabled(value) }
    }

    // MARK: - Crops

    func setScreenCropInset(_ edge: StudioCropEdge, to value: Double) {
        edit { $0.setScreenCropInset(edge, to: value) }
    }

    func clearScreenCrop() {
        edit { $0.clearScreenCrop() }
    }

    func setCameraCropInset(_ edge: StudioCropEdge, to value: Double) {
        edit { $0.setCameraCropInset(edge, to: value) }
    }

    func clearCameraCrop() {
        edit { $0.clearCameraCrop() }
    }

    // MARK: - Zooms
    //
    // An index is a zoom's place in `project.zooms`, which is kept in time order. One zoom can be
    // selected. An edit to the selected zoom takes the selection with it, and so does adding a
    // zoom. Through every other edit, and undo and redo, the selection follows its zoom as
    // `StudioEditorModel.zoomIndex(following:from:to:)` finds it, or lets go when the zoom is gone.

    /// Selects a zoom, or none with nil or a place that has no zoom. The playhead stays. A
    /// selected cut or speed change is let go when a zoom is selected.
    func selectZoom(_ index: Int?) {
        setSelectedZoomIndex(index)
        if selectedZoomIndex != nil {
            setSelectedCutIndex(nil)
            setSelectedSpeedIndex(nil)
            showInspectorPanel(.zoom)
        }
    }

    /// Shows an inspector panel, or the nearest one this recording has. The rail's buttons do
    /// this, and so does taking hold of something a panel edits. Not an edit: Undo leaves it.
    func showInspectorPanel(_ panel: StudioInspectorPanel) {
        let resolved = StudioInspectorPanel.resolved(panel, hasCamera: hasCamera)
        if inspectorPanel != resolved {
            inspectorPanel = resolved
        }
    }

    /// Lets go of whatever is selected: a zoom, a cut, or a speed change. The playhead stays. A
    /// press on an empty part of a lane does this: selecting no zoom alone would leave a selected
    /// cut as it is.
    func selectNothing() {
        setSelectedZoomIndex(nil)
        setSelectedCutIndex(nil)
        setSelectedSpeedIndex(nil)
    }

    /// Selects a zoom and moves the playhead to where it has moved in.
    @discardableResult
    func selectAndShowZoom(_ index: Int?) -> Bool {
        guard isEditable, let index, let time = editor?.zoomLookTime(at: index) else { return false }
        selectZoom(index)
        scrub(to: time)
        return true
    }

    /// Selects the zoom after the selected one and shows it. With nothing selected, the zoom at the
    /// playhead or the first one after it. False when there is none.
    @discardableResult
    func selectNextZoom() -> Bool {
        selectAndShowZoom(editor?.zoomIndex(after: selectedZoomIndex, playhead: playhead))
    }

    /// Selects the zoom before the selected one and shows it. With nothing selected, the zoom at
    /// the playhead or the last one before it. False when there is none.
    @discardableResult
    func selectPreviousZoom() -> Bool {
        selectAndShowZoom(editor?.zoomIndex(before: selectedZoomIndex, playhead: playhead))
    }

    /// The Previous zoom button and menu item. The zoom it lands on is read out: neither says
    /// anything of where it lands by itself.
    func showPreviousZoom() {
        if selectPreviousZoom() {
            announceSelectedZoom()
        }
    }

    /// The Next zoom button and menu item. The zoom it lands on is read out.
    func showNextZoom() {
        if selectNextZoom() {
            announceSelectedZoom()
        }
    }

    private func announceSelectedZoom() {
        guard let index = selectedZoomIndex, let zoom = selectedZoom else { return }
        announce(StudioEditorModel.zoomStepText(index: index, count: zooms.count, zoom: zoom))
    }

    /// Adds a zoom at the playhead and selects it. Where a zoom already is, that one is selected
    /// instead. A zoom starts unzoomed, so a paused playhead then moves to where the new zoom has
    /// moved in, which shows what it looks at.
    func addZoomAtPlayhead() {
        guard isEditable else { return }
        let time = playhead
        let recordedEvents = events
        var result = StudioZoomEditResult(changed: false, index: nil)
        editAndSelect { model in
            result = model.addZoom(at: time, events: recordedEvents)
            if let index = result.index {
                return .zoom(index)
            }
            return .follow
        }

        if result.changed {
            announce(StudioEditorModel.zoomAddedMessage)
            if !isPlaying, let index = result.index, let lookTime = editor?.zoomLookTime(at: index) {
                scrub(to: lookTime)
            }
        } else if result.index != nil {
            announce(StudioEditorModel.zoomAlreadyThereMessage)
        } else {
            announce(StudioEditorModel.noRoomForZoomMessage)
        }
    }

    func removeSelectedZoom() {
        guard let index = selectedZoomIndex else { return }
        let result = editZoom(at: index) { $0.removeZoom(at: index) }
        if result.changed {
            announce(StudioEditorModel.zoomDeletedMessage)
        }
    }

    /// Moves a zoom's start, from the lane or the inspector, and shows the frame it now starts on.
    /// Returns where the zoom is afterwards.
    @discardableResult
    func setZoomStart(at index: Int, to sourceTime: Double) -> Int? {
        let result = editZoom(at: index) { $0.setZoomStart(at: index, to: sourceTime) }
        if isEditable, let place = result.index, zooms.indices.contains(place) {
            scrub(to: zooms[place].start)
        }
        return result.index
    }

    /// Moves a zoom's end and shows the frame it now ends on. Returns where the zoom is afterwards.
    @discardableResult
    func setZoomEnd(at index: Int, to sourceTime: Double) -> Int? {
        let result = editZoom(at: index) { $0.setZoomEnd(at: index, to: sourceTime) }
        if isEditable, let place = result.index, zooms.indices.contains(place) {
            scrub(to: zooms[place].end)
        }
        return result.index
    }

    /// Moves a whole zoom so it starts at `sourceTime`, keeping its length. The playhead stays.
    /// Returns where the zoom is afterwards.
    @discardableResult
    func moveZoom(at index: Int, to sourceTime: Double) -> Int? {
        editZoom(at: index) { $0.moveZoom(at: index, to: sourceTime) }.index
    }

    func setSelectedZoomStartAtPlayhead() {
        guard let index = selectedZoomIndex else { return }
        setZoomStart(at: index, to: playhead)
    }

    func setSelectedZoomEndAtPlayhead() {
        guard let index = selectedZoomIndex else { return }
        setZoomEnd(at: index, to: playhead)
    }

    /// Moves the selected zoom's start by `seconds`, for the inspector's step buttons.
    func nudgeSelectedZoomStart(by seconds: Double) {
        guard let index = selectedZoomIndex, let zoom = selectedZoom else { return }
        setZoomStart(at: index, to: zoom.start + seconds)
    }

    /// Moves the selected zoom's end by `seconds`, for the inspector's step buttons.
    func nudgeSelectedZoomEnd(by seconds: Double) {
        guard let index = selectedZoomIndex, let zoom = selectedZoom else { return }
        setZoomEnd(at: index, to: zoom.end + seconds)
    }

    func setSelectedZoomScale(_ value: Double) {
        guard let index = selectedZoomIndex else { return }
        editZoom(at: index) { $0.setZoomScale(at: index, to: value) }
    }

    func setSelectedZoomFocusMode(_ mode: StudioZoomFocusMode) {
        guard let index = selectedZoomIndex else { return }
        editZoom(at: index) { $0.setZoomFocusMode(at: index, to: mode) }
    }

    /// Points the selected zoom at a place on its focus pad, from 0 to 1 across and down the pad.
    func setSelectedZoomFocusOnPad(x: Double, y: Double) {
        guard let index = selectedZoomIndex else { return }
        editZoom(at: index) { $0.setZoomFocusOnPad(at: index, x: x, y: y) }
    }

    func setSelectedZoomEaseIn(_ seconds: Double) {
        guard let index = selectedZoomIndex else { return }
        editZoom(at: index) { $0.setZoomEaseIn(at: index, to: seconds) }
    }

    func setSelectedZoomEaseOut(_ seconds: Double) {
        guard let index = selectedZoomIndex else { return }
        editZoom(at: index) { $0.setZoomEaseOut(at: index, to: seconds) }
    }

    /// Replaces the suggested zooms with new ones worked out from the recording's clicks, as one
    /// undo step, and says how many there are. Zooms the user made or changed stay.
    func suggestZooms() {
        guard canSuggestZooms else { return }
        showInspectorPanel(.zoom)
        let recordedEvents = events
        edit { model in
            let suggestions = StudioLayoutResolver.suggestZooms(project: model.project, events: recordedEvents)
            model.applyZoomSuggestions(suggestions)
        }
        announce(StudioEditorModel.zoomSuggestionsText(count: editor?.suggestedZoomCount ?? 0))
    }

    /// Removes the suggested zooms, as one undo step.
    func removeZoomSuggestions() {
        guard isEditable, hasSuggestedZooms else { return }
        edit { $0.applyZoomSuggestions([]) }
        announce(StudioEditorModel.zoomSuggestionsRemovedMessage)
    }

    // MARK: - Cuts
    //
    // An index is a cut's place in `project.edits.cuts`, which is kept in time order. One cut can
    // be selected, in place of a zoom. An edit to the selected cut takes the selection with it,
    // and so does adding a cut. Through every other edit, and undo and redo, the selection follows
    // its cut as `StudioEditorModel.cutIndex(following:from:to:)` finds it, or lets go when the
    // cut is gone.

    var cuts: [StudioTimeRange] { project?.edits.cuts ?? [] }

    var selectedCut: StudioTimeRange? {
        guard let index = selectedCutIndex, cuts.indices.contains(index) else { return nil }
        return cuts[index]
    }

    /// Whether adding a cut at the playhead has a cut to answer with: one fits there, or one is
    /// already there to select.
    var canAddCutAtPlayhead: Bool { isEditable && (editor?.canAddCut(at: playhead) ?? false) }

    /// Selects a cut, or none with nil or a place that has no cut. The playhead stays. A selected
    /// zoom or speed change is let go when a cut is selected.
    func selectCut(_ index: Int?) {
        setSelectedCutIndex(index)
        if selectedCutIndex != nil {
            setSelectedZoomIndex(nil)
            setSelectedSpeedIndex(nil)
            showInspectorPanel(.cut)
        }
    }

    /// Selects a cut and moves the playhead to where it starts, on the first picture the video
    /// leaves out.
    @discardableResult
    func selectAndShowCut(_ index: Int?) -> Bool {
        guard isEditable, let index, cuts.indices.contains(index) else { return false }
        selectCut(index)
        scrub(to: cuts[index].start)
        return true
    }

    /// Selects the cut after the selected one and shows it. With nothing selected, the cut at the
    /// playhead or the first one after it. False when there is none.
    @discardableResult
    func selectNextCut() -> Bool {
        selectAndShowCut(editor?.cutIndex(after: selectedCutIndex, playhead: playhead))
    }

    /// Selects the cut before the selected one and shows it. With nothing selected, the cut at the
    /// playhead or the last one before it. False when there is none.
    @discardableResult
    func selectPreviousCut() -> Bool {
        selectAndShowCut(editor?.cutIndex(before: selectedCutIndex, playhead: playhead))
    }

    /// The Previous cut button and menu item. The cut it lands on is read out: neither says
    /// anything of where it lands by itself.
    func showPreviousCut() {
        if selectPreviousCut() {
            announceSelectedCut()
        }
    }

    /// The Next cut button and menu item. The cut it lands on is read out.
    func showNextCut() {
        if selectNextCut() {
            announceSelectedCut()
        }
    }

    private func announceSelectedCut() {
        guard let index = selectedCutIndex, let cut = selectedCut else { return }
        announce(StudioEditorModel.cutStepText(index: index, count: cuts.count, cut: cut))
    }

    /// Adds a cut at the playhead and selects it. Where a cut already is, that one is selected
    /// instead. The playhead stays where the cut starts.
    func addCutAtPlayhead() {
        guard isEditable else { return }
        let time = playhead
        var result = StudioCutEditResult(changed: false, index: nil)
        editAndSelect { model in
            result = model.addCut(at: time)
            if let index = result.index {
                return .cut(index)
            }
            return .follow
        }

        if result.changed {
            announce(StudioEditorModel.cutAddedMessage)
        } else if result.index != nil {
            announce(StudioEditorModel.cutAlreadyThereMessage)
        } else {
            announce(StudioEditorModel.noRoomForCutMessage)
        }
    }

    /// Deletes the selected cut, which puts its stretch back into the video.
    func removeSelectedCut() {
        guard let index = selectedCutIndex else { return }
        let result = editCut(at: index) { $0.removeCut(at: index) }
        if result.changed {
            announce(StudioEditorModel.cutDeletedMessage)
        }
    }

    /// Moves a cut's start, from the lane or the inspector, and shows the picture there, which is
    /// the first one the video leaves out. Returns where the cut is afterwards.
    @discardableResult
    func setCutStart(at index: Int, to sourceTime: Double) -> Int? {
        let result = editCut(at: index) { $0.setCutStart(at: index, to: sourceTime) }
        if isEditable, let place = result.index, cuts.indices.contains(place) {
            scrub(to: cuts[place].start)
        }
        return result.index
    }

    /// Moves a cut's end and shows the picture there, which is the one the video picks up again
    /// with. Returns where the cut is afterwards.
    @discardableResult
    func setCutEnd(at index: Int, to sourceTime: Double) -> Int? {
        let result = editCut(at: index) { $0.setCutEnd(at: index, to: sourceTime) }
        if isEditable, let place = result.index, cuts.indices.contains(place) {
            scrub(to: cuts[place].end)
        }
        return result.index
    }

    /// Moves a whole cut so it starts at `sourceTime`, keeping its length. The playhead stays.
    /// Returns where the cut is afterwards.
    @discardableResult
    func moveCut(at index: Int, to sourceTime: Double) -> Int? {
        editCut(at: index) { $0.moveCut(at: index, to: sourceTime) }.index
    }

    /// Starts the selected cut at the playhead. The playhead stays where it is.
    func setSelectedCutStartAtPlayhead() {
        guard let index = selectedCutIndex else { return }
        let time = playhead
        editCut(at: index) { $0.setCutStart(at: index, to: time) }
    }

    /// Ends the selected cut at the playhead. The playhead stays where it is.
    func setSelectedCutEndAtPlayhead() {
        guard let index = selectedCutIndex else { return }
        let time = playhead
        editCut(at: index) { $0.setCutEnd(at: index, to: time) }
    }

    /// Moves the selected cut's start by `seconds`, for the inspector's step buttons.
    func nudgeSelectedCutStart(by seconds: Double) {
        guard let index = selectedCutIndex, let cut = selectedCut else { return }
        setCutStart(at: index, to: cut.start + seconds)
    }

    /// Moves the selected cut's end by `seconds`, for the inspector's step buttons.
    func nudgeSelectedCutEnd(by seconds: Double) {
        guard let index = selectedCutIndex, let cut = selectedCut else { return }
        setCutEnd(at: index, to: cut.end + seconds)
    }

    // MARK: - Speed
    //
    // An index is a speed change's place in `project.edits.speed`, which is kept in time order.
    // One speed change can be selected, in place of a zoom or a cut. An edit to the selected one
    // takes the selection with it, and so does adding one. Through every other edit, and undo and
    // redo, the selection follows its speed change as
    // `StudioEditorModel.speedIndex(following:from:to:)` finds it, or lets go when it is gone.
    //
    // While playing, the preview goes through a speed change at its rate and without sound, as
    // the export does. See `applySpeedAndSound(at:)`.

    var speedChanges: [StudioSpeedRange] { project?.edits.speed ?? [] }

    var selectedSpeed: StudioSpeedRange? {
        guard let index = selectedSpeedIndex, speedChanges.indices.contains(index) else { return nil }
        return speedChanges[index]
    }

    /// Whether adding a speed change at the playhead has one to answer with: one fits there, or
    /// one is already there to select.
    var canAddSpeedAtPlayhead: Bool { isEditable && (editor?.canAddSpeed(at: playhead) ?? false) }

    /// Selects a speed change, or none with nil or a place that has none. The playhead stays. A
    /// selected zoom or cut is let go when a speed change is selected.
    func selectSpeed(_ index: Int?) {
        setSelectedSpeedIndex(index)
        if selectedSpeedIndex != nil {
            setSelectedZoomIndex(nil)
            setSelectedCutIndex(nil)
            showInspectorPanel(.speed)
        }
    }

    /// Selects a speed change and moves the playhead to where it starts, on the first picture
    /// that plays at the other speed.
    @discardableResult
    func selectAndShowSpeed(_ index: Int?) -> Bool {
        guard isEditable, let index, speedChanges.indices.contains(index) else { return false }
        selectSpeed(index)
        scrub(to: speedChanges[index].start)
        return true
    }

    /// Selects the speed change after the selected one and shows it. With nothing selected, the
    /// one at the playhead or the first one after it. False when there is none.
    @discardableResult
    func selectNextSpeed() -> Bool {
        selectAndShowSpeed(editor?.speedIndex(after: selectedSpeedIndex, playhead: playhead))
    }

    /// Selects the speed change before the selected one and shows it. With nothing selected, the
    /// one at the playhead or the last one before it. False when there is none.
    @discardableResult
    func selectPreviousSpeed() -> Bool {
        selectAndShowSpeed(editor?.speedIndex(before: selectedSpeedIndex, playhead: playhead))
    }

    /// The Previous speed change button and menu item. The one it lands on is read out: neither
    /// says anything of where it lands by itself.
    func showPreviousSpeed() {
        if selectPreviousSpeed() {
            announceSelectedSpeed()
        }
    }

    /// The Next speed change button and menu item. The one it lands on is read out.
    func showNextSpeed() {
        if selectNextSpeed() {
            announceSelectedSpeed()
        }
    }

    private func announceSelectedSpeed() {
        guard let index = selectedSpeedIndex, let speed = selectedSpeed else { return }
        announce(StudioEditorModel.speedStepText(index: index, count: speedChanges.count, speed: speed))
    }

    /// Adds a speed change at the playhead and selects it. Where one already is, that one is
    /// selected instead. The playhead stays where it starts.
    func addSpeedAtPlayhead() {
        guard isEditable else { return }
        let time = playhead
        var result = StudioSpeedEditResult(changed: false, index: nil)
        editAndSelect { model in
            result = model.addSpeed(at: time)
            if let index = result.index {
                return .speed(index)
            }
            return .follow
        }

        if result.changed {
            announce(StudioEditorModel.speedAddedMessage)
        } else if result.index != nil {
            announce(StudioEditorModel.speedAlreadyThereMessage)
        } else {
            announce(StudioEditorModel.noRoomForSpeedMessage)
        }
    }

    /// Deletes the selected speed change, so its stretch plays at the recording's own speed again.
    func removeSelectedSpeed() {
        guard let index = selectedSpeedIndex else { return }
        let result = editSpeed(at: index) { $0.removeSpeed(at: index) }
        if result.changed {
            announce(StudioEditorModel.speedDeletedMessage)
        }
    }

    /// Moves a speed change's start, from the lane or the inspector, and shows the picture there,
    /// which is the first one that plays at the other speed. Returns where it is afterwards.
    @discardableResult
    func setSpeedStart(at index: Int, to sourceTime: Double) -> Int? {
        let result = editSpeed(at: index) { $0.setSpeedStart(at: index, to: sourceTime) }
        if isEditable, let place = result.index, speedChanges.indices.contains(place) {
            scrub(to: speedChanges[place].start)
        }
        return result.index
    }

    /// Moves a speed change's end and shows the picture there, which is the first one at the
    /// recording's own speed again. Returns where it is afterwards.
    @discardableResult
    func setSpeedEnd(at index: Int, to sourceTime: Double) -> Int? {
        let result = editSpeed(at: index) { $0.setSpeedEnd(at: index, to: sourceTime) }
        if isEditable, let place = result.index, speedChanges.indices.contains(place) {
            scrub(to: speedChanges[place].end)
        }
        return result.index
    }

    /// Moves a whole speed change so it starts at `sourceTime`, keeping its length and its rate.
    /// The playhead stays. Returns where it is afterwards.
    @discardableResult
    func moveSpeed(at index: Int, to sourceTime: Double) -> Int? {
        editSpeed(at: index) { $0.moveSpeed(at: index, to: sourceTime) }.index
    }

    /// Sets how fast the selected speed change plays. The playhead stays.
    func setSelectedSpeedRate(_ rate: Double) {
        guard let index = selectedSpeedIndex else { return }
        editSpeed(at: index) { $0.setSpeedRate(at: index, to: rate) }
    }

    /// Starts the selected speed change at the playhead. The playhead stays where it is.
    func setSelectedSpeedStartAtPlayhead() {
        guard let index = selectedSpeedIndex else { return }
        let time = playhead
        editSpeed(at: index) { $0.setSpeedStart(at: index, to: time) }
    }

    /// Ends the selected speed change at the playhead. The playhead stays where it is.
    func setSelectedSpeedEndAtPlayhead() {
        guard let index = selectedSpeedIndex else { return }
        let time = playhead
        editSpeed(at: index) { $0.setSpeedEnd(at: index, to: time) }
    }

    /// Moves the selected speed change's start by `seconds`, for the inspector's step buttons.
    func nudgeSelectedSpeedStart(by seconds: Double) {
        guard let index = selectedSpeedIndex, let speed = selectedSpeed else { return }
        setSpeedStart(at: index, to: speed.start + seconds)
    }

    /// Moves the selected speed change's end by `seconds`, for the inspector's step buttons.
    func nudgeSelectedSpeedEnd(by seconds: Double) {
        guard let index = selectedSpeedIndex, let speed = selectedSpeed else { return }
        setSpeedEnd(at: index, to: speed.end + seconds)
    }

    // MARK: - Scenes
    //
    // Every moment of the recording is in one scene, so a scene is not selected the way a zoom is:
    // the current scene is the one the playhead is in. The layout controls, the bubble in the
    // preview, and the keys 1 to 4 change that scene.

    var scenes: [StudioScene] { project?.scenes ?? [] }

    /// The scene the playhead is in, as its place in the project's scenes.
    var currentSceneIndex: Int { editor?.currentSceneIndex ?? 0 }

    /// Whether Split would start a new scene at the playhead.
    var canSplitSceneAtPlayhead: Bool { isEditable && (editor?.canSplitScene(at: playhead) ?? false) }

    /// Why the scene cannot be split at the playhead, in words, or nil when it can be.
    var splitSceneExplanation: String? { editor?.splitSceneExplanation(at: playhead) }

    /// Whether the current scene can be deleted: any scene but the only one.
    var canRemoveCurrentScene: Bool { isEditable && (editor?.canRemoveScene(at: currentSceneIndex) ?? false) }

    /// Starts a new scene at the playhead, a copy of the current one that is entered by moving.
    /// At its first instant a scene still looks like the one before, so a paused playhead then
    /// moves to where the new scene has been entered, which shows what is changed in it next.
    func splitSceneAtPlayhead() {
        guard isEditable else { return }
        // Shown for a split that is refused as well: the panel says why.
        showInspectorPanel(.scene)
        let time = playhead
        var result = StudioSceneEditResult(changed: false, index: currentSceneIndex)
        edit { model in
            result = model.splitScene(at: time)
        }

        guard result.changed else {
            if let explanation = splitSceneExplanation {
                announce(explanation)
            }
            return
        }
        announce(StudioEditorModel.sceneSplitMessage)
        if !isPlaying, let lookTime = editor?.sceneLookTime(at: result.index) {
            scrub(to: lookTime)
        }
    }

    /// Deletes the scene the playhead is in. The scene before it then lasts until the next one,
    /// and deleting the first scene hands its time to the second.
    func removeCurrentScene() {
        guard isEditable else { return }
        let index = currentSceneIndex
        var result = StudioSceneEditResult(changed: false, index: index)
        edit { model in
            result = model.removeScene(at: index)
        }
        announce(result.changed ? StudioEditorModel.sceneDeletedMessage : StudioEditorModel.onlySceneExplanation)
    }

    /// Moves where a scene starts, from the lane or the inspector, and shows the picture there:
    /// the playhead follows the start as it does a trim handle. The first scene starts with the
    /// recording and is left alone.
    func setSceneStart(at index: Int, to sourceTime: Double) {
        edit { model in
            model.setSceneStart(at: index, to: sourceTime)
        }
        if isEditable, index >= 1, let range = editor?.sceneRange(at: index) {
            scrub(to: range.start)
        }
    }

    /// Makes the current scene start at the playhead. The playhead stays where it is.
    func setCurrentSceneStartAtPlayhead() {
        let index = currentSceneIndex
        let time = playhead
        edit { model in
            model.setSceneStart(at: index, to: time)
        }
    }

    /// Moves the current scene's start by `seconds`, for the inspector's step buttons.
    func nudgeCurrentSceneStart(by seconds: Double) {
        let index = currentSceneIndex
        guard let range = editor?.sceneRange(at: index) else { return }
        setSceneStart(at: index, to: range.start + seconds)
    }

    /// Sets whether the current scene is cut to or entered by moving. Not for the first scene.
    func setCurrentSceneTransitionKind(_ kind: StudioTransitionKind) {
        editCurrentScene { model in
            let index = model.currentSceneIndex
            model.setSceneTransitionKind(at: index, to: kind)
        }
    }

    /// Sets how long the move into the current scene takes. Not for the first scene.
    func setCurrentSceneTransitionDuration(_ seconds: Double) {
        editCurrentScene { model in
            let index = model.currentSceneIndex
            model.setSceneTransitionDuration(at: index, to: seconds)
        }
    }

    /// Moves the playhead to where a scene has been entered. False when there is no such scene.
    @discardableResult
    func showScene(_ index: Int) -> Bool {
        guard isEditable, let time = editor?.sceneLookTime(at: index) else { return false }
        scrub(to: time)
        return true
    }

    /// Moves the playhead into the scene after the current one. False in the last scene.
    @discardableResult
    func showNextScene() -> Bool {
        showScene(currentSceneIndex + 1)
    }

    /// Moves the playhead into the scene before the current one. False in the first scene.
    @discardableResult
    func showPreviousScene() -> Bool {
        showScene(currentSceneIndex - 1)
    }

    /// The Previous scene button and menu item. The scene it lands on is read out: neither says
    /// anything of where it lands by itself.
    func stepToPreviousScene() {
        if showPreviousScene() {
            showInspectorPanel(.scene)
            announceCurrentScene()
        }
    }

    /// The Next scene button and menu item. The scene it lands on is read out.
    func stepToNextScene() {
        if showNextScene() {
            showInspectorPanel(.scene)
            announceCurrentScene()
        }
    }

    private func announceCurrentScene() {
        guard let editor else { return }
        announce(editor.sceneAccessibilityText(at: editor.currentSceneIndex))
    }

    // MARK: - Trim

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
        cutSkipTarget = nil
        isPlaying = true
        play(player, from: start)
    }

    func pause() {
        player?.pause()
        isPlaying = false
        applySpeedAndSound(at: playhead)
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
                // The project has a video now and is no draft any more, wherever it is listed.
                NotificationCenter.default.post(name: .studioProjectsDidChange, object: nil)
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

    // MARK: - The Project as a Whole

    /// Reads which other projects there are, for Open Recent. Reading every project takes a
    /// moment, so it is done off the main thread.
    func refreshRecentProjects() {
        let store = store
        let currentID = projectID
        Task { [weak self] in
            let summaries = await Task.detached(priority: .utility) {
                (try? store.listSummaries()) ?? []
            }.value
            self?.recentProjects = StudioProjectSummary.recent(
                from: summaries,
                excluding: currentID,
                limit: Self.recentProjectLimit
            )
        }
    }

    /// Saves a copy of the project as a folder the user chooses: the recordings, and a
    /// `.tinyclips` file that opens them in Studio again, here or on another Mac. The project
    /// stays in Studio's own storage and is edited there as before; the folder is a copy as of
    /// now.
    func saveProjectFolder() {
        guard canSaveProjectFolder else { return }
        pause()
        // The folder gets the project as it is saved, so the edits have to be on disk first.
        guard saveNow() else { return }

        let panel = NSSavePanel()
        panel.title = "Save Project"
        panel.message = "Saves the recordings and a .tinyclips file in a folder of this name."
        panel.prompt = "Save"
        panel.nameFieldLabel = "Folder name:"
        panel.nameFieldStringValue = StudioProjectStore.folderName(for: clipName)
        panel.canCreateDirectories = true
        panel.isExtensionHidden = false
        guard panel.runModal() == .OK, let folder = panel.url else { return }

        let store = store
        let id = projectID
        busyMessage = "Saving project…"
        Task { [weak self] in
            let failure: String? = await Task.detached(priority: .userInitiated) {
                do {
                    // The panel has asked before it hands back a name that is taken.
                    try store.exportProjectFolder(id: id, to: folder, replacingSavedProject: true)
                    return nil
                } catch {
                    return error.localizedDescription
                }
            }.value
            guard let self else { return }
            self.busyMessage = nil
            if let failure {
                SaveService.shared.showError("Studio could not save this project: \(failure)")
            } else {
                SaveService.shared.showNotice("Project saved to the folder \(folder.lastPathComponent).")
                let projectFile = folder
                    .appendingPathComponent(folder.lastPathComponent)
                    .appendingPathExtension(StudioProjectStore.projectFileExtension)
                NSWorkspace.shared.activateFileViewerSelecting([projectFile])
            }
        }
    }

    /// Deletes the whole project, after asking: its recordings and every edit. Videos exported
    /// from it and folders it was saved to are files of the user's own and stay. The window
    /// closes.
    func deleteProject() {
        guard canDeleteProject else { return }
        pause()

        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Delete “\(clipName)”?"
        alert.informativeText = "The recording and every edit are removed from Tiny Clips Studio. Videos you exported and folders you saved this project to are not deleted. This cannot be undone."
        let deleteButton = alert.addButton(withTitle: "Delete Project")
        deleteButton.hasDestructiveAction = true
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }

        closesAfterExport = false
        exportTask?.cancel()
        deletesOnClose = true
        requestClose?()
    }

    /// Asked when the user closes the window. Returns false to keep it open.
    func shouldClose() -> Bool {
        if isBusy {
            // The copy runs to its end either way; the window stays to say how it went.
            NSSound.beep()
            return false
        }

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
        markLastUsed()
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

    /// Where the selection goes after an edit.
    private enum SelectionAfterEdit {
        /// The edit did not say. The selection follows the zoom, the cut, or the speed change it
        /// was on.
        case follow

        /// The edit knows where its zoom went, or that it is gone.
        case zoom(Int?)

        /// The edit knows where its cut went, or that it is gone.
        case cut(Int?)

        /// The edit knows where its speed change went, or that it is gone.
        case speed(Int?)
    }

    private func edit(_ change: (inout StudioEditorModel) -> Void) {
        editAndSelect { model in
            change(&model)
            return .follow
        }
    }

    /// An edit to the scene the playhead is in: its layout, its bubble, its split, or how it is
    /// entered.
    private func editCurrentScene(_ change: (inout StudioEditorModel) -> Void) {
        holdSceneForDrag()
        edit(change)
    }

    /// Stops playback before a drag changes the scene the playhead is in. A drag is many changes,
    /// and while the recording plays the playhead may come into the next scene before the drag is
    /// over: the rest of the drag would then change that scene as well. A change that stands
    /// alone, such as a key or a choice from a menu, is over at once and leaves playback alone.
    /// So does a drag in a recording with one scene, which has no other scene to come into.
    private func holdSceneForDrag() {
        guard isInGesture, isPlaying, scenes.count > 1 else { return }
        pause()
    }

    /// An edit to one zoom. The selection goes with the zoom when it is the selected one. An edit
    /// that is refused, because the project cannot be edited just now, leaves the zoom where it was.
    @discardableResult
    private func editZoom(
        at index: Int,
        _ change: (inout StudioEditorModel) -> StudioZoomEditResult
    ) -> StudioZoomEditResult {
        var result = StudioZoomEditResult(changed: false, index: index)
        let isSelected = selectedZoomIndex == index
        editAndSelect { model in
            result = change(&model)
            return isSelected ? .zoom(result.index) : .follow
        }
        return result
    }

    /// An edit to one cut. The selection goes with the cut when it is the selected one. An edit
    /// that is refused, because the project cannot be edited just now, leaves the cut where it was.
    @discardableResult
    private func editCut(
        at index: Int,
        _ change: (inout StudioEditorModel) -> StudioCutEditResult
    ) -> StudioCutEditResult {
        var result = StudioCutEditResult(changed: false, index: index)
        let isSelected = selectedCutIndex == index
        editAndSelect { model in
            result = change(&model)
            return isSelected ? .cut(result.index) : .follow
        }
        return result
    }

    /// An edit to one speed change. The selection goes with it when it is the selected one. An
    /// edit that is refused, because the project cannot be edited just now, leaves it where it was.
    @discardableResult
    private func editSpeed(
        at index: Int,
        _ change: (inout StudioEditorModel) -> StudioSpeedEditResult
    ) -> StudioSpeedEditResult {
        var result = StudioSpeedEditResult(changed: false, index: index)
        let isSelected = selectedSpeedIndex == index
        editAndSelect { model in
            result = change(&model)
            return isSelected ? .speed(result.index) : .follow
        }
        return result
    }

    private func editAndSelect(_ change: (inout StudioEditorModel) -> SelectionAfterEdit) {
        guard isEditable, var model = editor else { return }
        // An edit to the layout changes the scene the playhead is in.
        model.sceneTime = playhead
        let before = model.editableState
        let zoomsBefore = model.project.zooms
        let cutsBefore = model.project.edits.cuts
        let speedBefore = model.project.edits.speed
        let selection = change(&model)
        editor = model
        let isChanged = model.editableState != before

        // Through an edit that did not say, the selection follows the zoom, the cut, or the speed
        // change it was on.
        var zoomIndex = selectedZoomIndex
        var cutIndex = selectedCutIndex
        var speedIndex = selectedSpeedIndex
        if isChanged, let selected = zoomIndex {
            zoomIndex = StudioEditorModel.zoomIndex(following: selected, from: zoomsBefore, to: model.project.zooms)
        }
        if isChanged, let selected = cutIndex {
            cutIndex = StudioEditorModel.cutIndex(following: selected, from: cutsBefore, to: model.project.edits.cuts)
        }
        if isChanged, let selected = speedIndex {
            speedIndex = StudioEditorModel.speedIndex(following: selected, from: speedBefore, to: model.project.edits.speed)
        }

        // One selection: an edit that says where a zoom is lets go of a cut and a speed change,
        // and so on. An edit to the selected zoom finds nothing else selected, so only adding one
        // does.
        switch selection {
        case .follow:
            break
        case .zoom(let index):
            zoomIndex = index
            cutIndex = nil
            speedIndex = nil
        case .cut(let index):
            cutIndex = index
            zoomIndex = nil
            speedIndex = nil
        case .speed(let index):
            speedIndex = index
            zoomIndex = nil
            cutIndex = nil
        }
        setSelectedZoomIndex(zoomIndex)
        setSelectedCutIndex(cutIndex)
        setSelectedSpeedIndex(speedIndex)

        // An edit that says which zoom, cut, or speed change it left selected shows its panel.
        // Adding one does, from the timeline, the menu, or the keyboard.
        switch selection {
        case .follow:
            break
        case .zoom:
            if selectedZoomIndex != nil { showInspectorPanel(.zoom) }
        case .cut:
            if selectedCutIndex != nil { showInspectorPanel(.cut) }
        case .speed:
            if selectedSpeedIndex != nil { showInspectorPanel(.speed) }
        }

        guard isChanged else { return }
        hasUnsavedEdits = true
        scheduleSave()
        if isPlaying, model.isAtPlaybackEnd(playhead) {
            pause()
        }
        // The edit may have changed whether the project is muted, or how fast it plays here.
        applySpeedAndSound(at: playhead)
        refreshPreview()
    }

    /// Sets the selection to a zoom that exists, or to none. It is published only when it changes.
    private func setSelectedZoomIndex(_ index: Int?) {
        var valid: Int?
        if let index, zooms.indices.contains(index) {
            valid = index
        }
        if selectedZoomIndex != valid {
            selectedZoomIndex = valid
        }
    }

    /// Sets the selection to a cut that exists, or to none. It is published only when it changes.
    private func setSelectedCutIndex(_ index: Int?) {
        var valid: Int?
        if let index, cuts.indices.contains(index) {
            valid = index
        }
        if selectedCutIndex != valid {
            selectedCutIndex = valid
        }
    }

    /// Sets the selection to a speed change that exists, or to none. It is published only when it
    /// changes.
    private func setSelectedSpeedIndex(_ index: Int?) {
        var valid: Int?
        if let index, speedChanges.indices.contains(index) {
            valid = index
        }
        if selectedSpeedIndex != valid {
            selectedSpeedIndex = valid
        }
    }

    /// Tells the model where the playhead is once it has moved into another scene. The model is
    /// published, so it is told then and not for every frame that plays. Every edit tells it again.
    private func followPlayheadIntoScene() {
        guard let editor, editor.sceneIndex(at: playhead) != editor.currentSceneIndex else { return }
        self.editor?.sceneTime = playhead
    }

    /// Says through VoiceOver what the screen alone shows.
    private func announce(_ message: String) {
        AccessibilityAnnouncementService.shared.announce(message, priority: .medium)
    }

    /// Redraws the preview for the current project. Only a new canvas size rebuilds the player
    /// item, and one rebuild runs at a time: an edit that arrives during one is applied when it
    /// has ended, so the preview always ends on the latest edit.
    private func refreshPreview() {
        guard editor != nil, let playback else { return }
        needsPreviewRefresh = true
        guard !isRefreshingPreview else { return }
        isRefreshingPreview = true

        previewTask = Task { [weak self] in
            while true {
                guard let self, !self.isTornDown, self.needsPreviewRefresh else { break }
                self.needsPreviewRefresh = false
                await self.applyPreview(to: playback)
            }
            self?.isRefreshingPreview = false
        }
    }

    private func applyPreview(to playback: StudioPlayback) async {
        guard let model = editor else { return }
        let size = Self.renderSize(for: model.project, longSide: Self.previewLongSide)
        // During a drag the canvas keeps its size. A crop changes the size with every step, and
        // every new size rebuilds the player item. The size is applied when the drag ends.
        let newSize: CGSize? = (size == previewSize || isInGesture) ? nil : size
        if let newSize {
            previewSize = newSize
        }

        do {
            let previousItem = playback.player.currentItem
            let item = try await playback.apply(project: model.previewProject, renderSize: newSize)
            guard !isTornDown else { return }
            if item !== previousItem {
                // A new player item starts at zero, so put the playhead back. Whether to go on
                // playing is asked now and not before the wait above: a pause, a scrub or a
                // frame step that came in while the item was being built has to hold.
                seek(to: playhead)
                if isPlaying {
                    play(playback.player, from: playhead)
                }
            }
        } catch {
            guard !isTornDown else { return }
            SaveService.shared.showError("Studio preview failed: \(error.localizedDescription)")
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

    /// Writes an edit that is still waiting for its save, when the app is about to quit. Quitting
    /// closes no window, so without this the last change made before a quit would be lost.
    func saveBeforeQuitting() {
        guard !isTornDown else { return }
        saveNow()
        markLastUsed()
    }

    /// Writes down that the project was in use until now, when its window closes or the app
    /// quits. Cleanup removes the recordings of an exported project some days after it was last
    /// open, and counts from this time. Counted from when the editor opened, a project whose
    /// editor stayed open for longer than that would be removed the moment the editor closed,
    /// by the cleanup that follows every close. A project that was never read has nothing to
    /// write down, and a failure here is not reported: the edits are saved separately.
    private func markLastUsed() {
        guard editor != nil, let saved = try? store.markOpened(id: projectID) else { return }
        editor?.refreshBookkeeping(from: saved)
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
        if !editor.isAtPlaybackEnd(seconds), let target = editor.cutSkipTarget(at: seconds) {
            // Playback has reached a cut and goes on from its end. The player reports times inside
            // the cut until its seek has landed. Those do not send it there again, and they leave
            // the playhead where it was sent.
            if target != cutSkipTarget {
                cutSkipTarget = target
                seek(to: target)
                applySpeedAndSound(at: target)
            }
            return
        }

        // The player reports once for every thirtieth of a second of the recording, which at
        // eight times the speed is eight times as often by the clock. The playhead, and all that
        // is drawn from it, moves no more often than it does at the recording's own speed.
        let isAtEnd = editor.isAtPlaybackEnd(seconds)
        let now = ProcessInfo.processInfo.systemUptime
        if isAtEnd || now - lastPlayheadMove >= 1.0 / 60 {
            lastPlayheadMove = now
            playhead = editor.clampedSourceTime(seconds)
        }
        if isAtEnd {
            pause()
            seek(to: editor.playbackEnd)
        } else {
            applySpeedAndSound(at: seconds)
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
        applySpeedAndSound(at: playhead)
    }

    /// Starts the player at the speed the video has at a source time.
    private func play(_ player: AVPlayer, from sourceTime: Double) {
        requestedRate = Float(editor?.playbackRate(at: sourceTime) ?? 1)
        player.rate = requestedRate
        applySpeedAndSound(at: sourceTime)
    }

    /// Sets how fast the player plays and whether it is heard, for where playback is. A stretch
    /// at another speed plays at its rate and without sound, as in the export (section 7 of the
    /// project format). A player that is paused, or that has stopped by itself, is not started
    /// by this.
    private func applySpeedAndSound(at sourceTime: Double) {
        guard let player, let editor else { return }
        let rate = isPlaying ? Float(editor.playbackRate(at: sourceTime)) : 1
        if isPlaying, player.rate != 0, requestedRate != rate {
            requestedRate = rate
            player.rate = rate
        }
        let isMuted = editor.project.audio.muted || rate != 1
        if player.isMuted != isMuted {
            player.isMuted = isMuted
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