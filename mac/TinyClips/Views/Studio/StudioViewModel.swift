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

    /// The cut the inspector shows, as its place in the project's cuts, or nil. A zoom or a cut is
    /// selected, never both: selecting one lets go of the other.
    @Published private(set) var selectedCutIndex: Int?

    /// The size the preview is drawn at. It follows the project, except during a drag, when a new
    /// size waits for the drag to end so the player is not rebuilt for every step of it.
    @Published private(set) var previewSize = CGSize.zero

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
    /// selected cut is let go when a zoom is selected.
    func selectZoom(_ index: Int?) {
        setSelectedZoomIndex(index)
        if selectedZoomIndex != nil {
            setSelectedCutIndex(nil)
        }
    }

    /// Lets go of whatever is selected, a zoom or a cut. The playhead stays. A press on an empty
    /// part of a lane does this: selecting no zoom alone would leave a selected cut as it is.
    func selectNothing() {
        setSelectedZoomIndex(nil)
        setSelectedCutIndex(nil)
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
    /// zoom is let go when a cut is selected.
    func selectCut(_ index: Int?) {
        setSelectedCutIndex(index)
        if selectedCutIndex != nil {
            setSelectedZoomIndex(nil)
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
        let index = currentSceneIndex
        edit { model in
            model.setSceneTransitionKind(at: index, to: kind)
        }
    }

    /// Sets how long the move into the current scene takes. Not for the first scene.
    func setCurrentSceneTransitionDuration(_ seconds: Double) {
        let index = currentSceneIndex
        edit { model in
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
            announceCurrentScene()
        }
    }

    /// The Next scene button and menu item. The scene it lands on is read out.
    func stepToNextScene() {
        if showNextScene() {
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

    /// Where the selection goes after an edit.
    private enum SelectionAfterEdit {
        /// The edit did not say. The selection follows the zoom or the cut it was on.
        case follow

        /// The edit knows where its zoom went, or that it is gone.
        case zoom(Int?)

        /// The edit knows where its cut went, or that it is gone.
        case cut(Int?)
    }

    private func edit(_ change: (inout StudioEditorModel) -> Void) {
        editAndSelect { model in
            change(&model)
            return .follow
        }
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

    private func editAndSelect(_ change: (inout StudioEditorModel) -> SelectionAfterEdit) {
        guard isEditable, var model = editor else { return }
        // An edit to the layout changes the scene the playhead is in.
        model.sceneTime = playhead
        let before = model.editableState
        let zoomsBefore = model.project.zooms
        let cutsBefore = model.project.edits.cuts
        let selection = change(&model)
        editor = model
        let isChanged = model.editableState != before

        // Through an edit that did not say, the selection follows the zoom or the cut it was on.
        var zoomIndex = selectedZoomIndex
        var cutIndex = selectedCutIndex
        if isChanged, let selected = zoomIndex {
            zoomIndex = StudioEditorModel.zoomIndex(following: selected, from: zoomsBefore, to: model.project.zooms)
        }
        if isChanged, let selected = cutIndex {
            cutIndex = StudioEditorModel.cutIndex(following: selected, from: cutsBefore, to: model.project.edits.cuts)
        }

        // One selection: an edit that says where a zoom is lets go of the cut, and the other way
        // round. An edit to the selected zoom finds no cut selected, so only adding one does.
        switch selection {
        case .follow:
            break
        case .zoom(let index):
            zoomIndex = index
            cutIndex = nil
        case .cut(let index):
            cutIndex = index
            zoomIndex = nil
        }
        setSelectedZoomIndex(zoomIndex)
        setSelectedCutIndex(cutIndex)

        guard isChanged else { return }
        hasUnsavedEdits = true
        scheduleSave()
        player?.isMuted = model.project.audio.muted
        if isPlaying, model.isAtPlaybackEnd(playhead) {
            pause()
        }
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
        let wasPlaying = isPlaying

        do {
            let previousItem = playback.player.currentItem
            let item = try await playback.apply(project: model.previewProject, renderSize: newSize)
            guard !isTornDown else { return }
            if item !== previousItem {
                // A new player item starts at zero, so put the playhead back.
                seek(to: playhead)
                if wasPlaying {
                    playback.player.play()
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
            }
            return
        }

        playhead = editor.clampedSourceTime(seconds)
        if editor.isAtPlaybackEnd(seconds) {
            pause()
            seek(to: editor.playbackEnd)
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