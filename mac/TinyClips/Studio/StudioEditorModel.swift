import Foundation

// MARK: - Editor Geometry

/// Where the canvas sits inside the preview view, and how to convert between the two.
/// Both spaces have their origin at the top left.
struct StudioEditorCanvasGeometry: Equatable, Sendable {
    var viewSize: CGSize
    var canvasSize: CGSize
    var canvasRectInView: StudioRect

    /// The canvas point under a view point, or nil when the view point is outside the canvas.
    func canvasPoint(fromViewPoint point: CGPoint) -> CGPoint? {
        guard canvasRectInView.width > 0, canvasRectInView.height > 0 else { return nil }
        let x = (point.x - canvasRectInView.x) / canvasRectInView.width * canvasSize.width
        let y = (point.y - canvasRectInView.y) / canvasRectInView.height * canvasSize.height
        guard x >= 0, x <= canvasSize.width, y >= 0, y <= canvasSize.height else { return nil }
        return CGPoint(x: x, y: y)
    }

    func viewPoint(fromCanvasPoint point: CGPoint) -> CGPoint {
        CGPoint(
            x: canvasRectInView.x + (point.x / max(1, canvasSize.width)) * canvasRectInView.width,
            y: canvasRectInView.y + (point.y / max(1, canvasSize.height)) * canvasRectInView.height
        )
    }

    /// Canvas pixels per view point.
    var canvasPixelsPerViewPoint: Double {
        canvasRectInView.width > 0 ? Double(canvasSize.width) / canvasRectInView.width : 1
    }
}

// MARK: - Editable State

/// The parts of a project the editor changes. Undo, redo, and "changed since export" work on this
/// value, so they never touch the project's bookkeeping (exports, dates, name, sources).
struct StudioEditableState: Equatable, Sendable {
    var canvas: StudioCanvas
    var screen: StudioScreenStyle
    var camera: StudioCameraStyle
    var scenes: [StudioScene]
    var zooms: [StudioZoom]
    var edits: StudioEdits
    var audio: StudioAudio
    var overlays: StudioOverlays

    init(project: StudioProject) {
        canvas = project.canvas
        screen = project.screen
        camera = project.camera
        scenes = project.scenes
        zooms = project.zooms
        edits = project.edits
        audio = project.audio
        overlays = project.overlays
    }

    func applied(to project: StudioProject) -> StudioProject {
        var copy = project
        copy.canvas = canvas
        copy.screen = screen
        copy.camera = camera
        copy.scenes = scenes
        copy.zooms = zooms
        copy.edits = edits
        copy.audio = audio
        copy.overlays = overlays
        return copy
    }
}

/// What an edit to a cut did.
struct StudioCutEditResult: Equatable, Sendable {
    /// Whether the project changed.
    var changed: Bool

    /// Where the cut is in `project.edits.cuts` now, or nil when it is gone or there is none.
    var index: Int?
}

/// What an edit to a zoom did.
struct StudioZoomEditResult: Equatable, Sendable {
    /// Whether the project changed.
    var changed: Bool

    /// Where the zoom is in `project.zooms` now, or nil when it is gone or there is none. The list
    /// is kept in time order, so an edit can move a zoom.
    var index: Int?
}

/// What an edit to the scenes did.
struct StudioSceneEditResult: Equatable, Sendable {
    /// Whether the project changed. False when there was nothing to do or the edit is not possible.
    var changed: Bool

    /// The scene the edit is about, as the scenes are now: the new scene after a split, and after
    /// a delete the scene that has taken over the deleted scene's time.
    var index: Int
}

/// What the focus pad shows for a zoom. The pad stands for the part of the screen that can be
/// zoomed into, which is the screen crop, or the whole screen without one.
struct StudioZoomPad: Equatable, Sendable {
    /// The pad's width divided by its height.
    var aspectRatio: Double

    /// The part the zoom holds, from 0 to 1 across and down the pad.
    var window: StudioRect

    /// Where the zoom looks, from 0 at the pad's left edge to 1 at its right.
    var focusX: Double

    /// Where the zoom looks, from 0 at the pad's top edge to 1 at its bottom.
    var focusY: Double
}

/// An edge of a crop.
enum StudioCropEdge: Sendable {
    case left
    case top
    case right
    case bottom
}

/// A crop as how much is cut off each edge, each a fraction of the frame. This is what the
/// inspector's four crop sliders show and change.
struct StudioCropInsets: Equatable, Sendable {
    /// The most two opposite edges can cut off together. A crop keeps 0.05 of the frame each way.
    static let maximumTotal = 0.95

    var left: Double
    var top: Double
    var right: Double
    var bottom: Double

    init(left: Double = 0, top: Double = 0, right: Double = 0, bottom: Double = 0) {
        self.left = left
        self.top = top
        self.right = right
        self.bottom = bottom
    }

    /// The insets of a crop. A missing crop, or one that is not valid, cuts nothing off.
    init(crop: StudioRect?) {
        guard let valid = StudioCanvasMath.validCrop(crop) else {
            self.init()
            return
        }
        self.init(
            left: Self.tidy(valid.x),
            top: Self.tidy(valid.y),
            right: Self.tidy(max(0, 1 - valid.x - valid.width)),
            bottom: Self.tidy(max(0, 1 - valid.y - valid.height))
        )
    }

    var isEmpty: Bool { left == 0 && top == 0 && right == 0 && bottom == 0 }

    subscript(edge: StudioCropEdge) -> Double {
        switch edge {
        case .left: return left
        case .top: return top
        case .right: return right
        case .bottom: return bottom
        }
    }

    /// These insets with one edge moved. The edge stops where it would leave less than 0.05 of the
    /// frame between it and the opposite edge. A value that is not a number changes nothing.
    func with(_ edge: StudioCropEdge, _ value: Double) -> StudioCropInsets {
        guard value.isFinite else { return self }
        var copy = self
        switch edge {
        case .left: copy.left = Self.limit(value, opposite: right)
        case .top: copy.top = Self.limit(value, opposite: bottom)
        case .right: copy.right = Self.limit(value, opposite: left)
        case .bottom: copy.bottom = Self.limit(value, opposite: top)
        }
        return copy
    }

    /// The crop for these insets, or nil when nothing is cut off. It is made from `current`, the
    /// crop that is there, so members of it that this version does not know are kept.
    func crop(from current: StudioRect?) -> StudioRect? {
        guard !isEmpty else { return nil }
        var crop = current ?? StudioRect()
        crop.x = left
        crop.y = top
        crop.width = Self.tidy(1 - left - right)
        crop.height = Self.tidy(1 - top - bottom)
        return crop
    }

    private static func limit(_ value: Double, opposite: Double) -> Double {
        tidy(max(0, min(value, maximumTotal - opposite)))
    }

    // Sums of fractions such as 1 - 0.07 - 0.2 carry binary noise. Six decimals is finer than a
    // pixel of any recording and keeps the stored numbers, and what is read back from them, plain.
    private static func tidy(_ value: Double) -> Double {
        (value * 1_000_000).rounded() / 1_000_000
    }
}

// MARK: - Editor Model

/// The Studio editor's state and every edit it can make, with undo. It is a plain value with no
/// AppKit or AVFoundation in it so it can be tested anywhere.
///
/// The layout controls change the current scene, which is the one the playhead is in. The owner
/// says where the playhead is with `sceneTime`. The stored scenes are kept as section 6.1 of the
/// project format reads them: in time order, each with a start of its own, the first at 0.
struct StudioEditorModel: Equatable, Sendable {
    static let minimumDuration = 0.1

    /// The shortest zoom the editor makes, in seconds. It is also the shortest suggestion (section 8).
    static let minimumZoomDuration = 0.3

    /// How long a zoom is when it is added, in seconds, where there is room for it.
    static let newZoomDuration = 3.0

    /// Said when a zoom has been added at the playhead.
    static let zoomAddedMessage = "Zoom added."

    /// Said when a zoom was asked for where one already is. That zoom is selected instead.
    static let zoomAlreadyThereMessage = "There is already a zoom here."

    /// Said when a zoom was asked for where less than the shortest zoom fits.
    static let noRoomForZoomMessage = "There is no room for a zoom here."

    /// Said when the selected zoom has been deleted.
    static let zoomDeletedMessage = "Zoom deleted."

    /// Said when the suggested zooms have been removed.
    static let zoomSuggestionsRemovedMessage = "Suggested zooms removed."

    /// Said when zooms were asked for and the clicks give none.
    static let noZoomSuggestionsMessage = "No zooms to suggest for this recording."

    /// Why zooms cannot be suggested for a recording without clicks, such as one of a window.
    static let noClicksExplanation = "This recording has no clicks to suggest zooms from."

    /// Why a zoom cannot follow the pointer in a recording without pointer positions.
    static let noPointerExplanation = "This recording has no pointer positions to follow."

    /// The shortest scene the editor makes, in seconds.
    static let minimumSceneDuration = 0.3

    /// How long a move into a scene can be set to take, in seconds. Section 6.9 allows none at
    /// all, which is what a cut is for.
    static let sceneTransitionDurationRange = 0.1...2.0

    /// Said when a scene has been split at the playhead.
    static let sceneSplitMessage = "Scene split."

    /// Said when the current scene has been deleted.
    static let sceneDeletedMessage = "Scene deleted."

    /// Why a recording without a camera has one scene.
    static let noCameraForScenesExplanation = "This recording has no camera, so there is nothing to arrange differently."

    /// Why a scene cannot be split near where it starts or ends.
    static let sceneTooShortToSplitExplanation = "Both scenes would have to last at least 0.3 seconds."

    /// Why a scene cannot be split while its layers are still moving into place.
    static let sceneStillMovingExplanation = "This scene is still moving into place here."

    /// Why the only scene cannot be deleted.
    static let onlySceneExplanation = "The only scene cannot be deleted."

    /// Why the first scene has no start to set and no way of being entered.
    static let firstSceneExplanation = "The first scene starts with the recording and has nothing to move from."

    /// The shortest cut the editor makes, in seconds.
    static let minimumCutDuration = 0.1

    /// How long a cut is when it is added, in seconds, where there is room for it.
    static let newCutDuration = 1.0

    /// Said when a cut has been added at the playhead.
    static let cutAddedMessage = "Cut added."

    /// Said when a cut was asked for where one already is. That cut is selected instead.
    static let cutAlreadyThereMessage = "There is already a cut here."

    /// Said when a cut was asked for where the shortest cut does not fit, or where it would leave no video.
    static let noRoomForCutMessage = "There is no room for a cut here."

    /// Said when the selected cut has been deleted.
    static let cutDeletedMessage = "Cut deleted."

    static let maximumUndoDepth = 100

    private(set) var project: StudioProject

    /// Where the playhead is, in source time, as the owner last said. It picks the current scene.
    /// It is not part of what undo restores.
    var sceneTime: Double = 0

    private var undoStack: [StudioEditableState] = []
    private var redoStack: [StudioEditableState] = []
    private var groupedSnapshot: StudioEditableState?
    private var exportedState: StudioEditableState?

    init(project: StudioProject) {
        self.project = project
        ensureScene()
        normalizeStoredScenes()
        normalizeStoredCuts()
        normalizeTrim()
        sortStoredZooms()
        exportedState = project.exports.isEmpty ? nil : StudioEditableState(project: self.project)
    }

    // MARK: - State

    var editableState: StudioEditableState { StudioEditableState(project: project) }
    var canUndo: Bool { !undoStack.isEmpty }
    var canRedo: Bool { !redoStack.isEmpty }
    var isGroupingEdits: Bool { groupedSnapshot != nil }

    var hasCamera: Bool { project.sources.camera != nil }
    var hasNeverExported: Bool { project.exports.isEmpty }

    /// True when the composition differs from what was last exported, or nothing was exported yet.
    /// A project reopened after an export counts as unchanged until it is edited.
    var hasUnexportedChanges: Bool {
        guard let exportedState else { return true }
        return exportedState != editableState
    }

    /// The scene the playhead is in.
    var currentSceneIndex: Int { sceneIndex(at: sceneTime) }

    var currentScene: StudioScene {
        let index = currentSceneIndex
        return project.scenes.indices.contains(index) ? project.scenes[index] : StudioScene()
    }

    /// The layout the current scene is drawn with once it has been entered: a project without a
    /// camera always shows the screen alone.
    var effectiveLayout: StudioLayout { hasCamera ? currentScene.layout : .screen }

    var currentLook: StudioLook {
        StudioLook(canvas: project.canvas, screen: project.screen, camera: project.camera).withoutCrops
    }

    // MARK: - Time

    var sourceDuration: Double { max(0, project.sources.screen.duration) }

    var frameDuration: Double {
        let frameRate = project.sources.screen.frameRate
        return frameRate.isFinite && frameRate > 0 ? 1 / frameRate : 1 / 30
    }

    var timeMap: StudioTimeMap { StudioTimeMap(sourceDuration: sourceDuration, edits: project.edits) }
    var outputDuration: Double { timeMap.outputDuration }
    var trimStart: Double { timeMap.trimStart }
    var trimEnd: Double { timeMap.trimEnd }

    func sourceTime(forOutputTime outputTime: Double) -> Double { timeMap.outputToSource(outputTime) }
    func outputTime(forSourceTime sourceTime: Double) -> Double { timeMap.sourceToOutput(sourceTime) }

    /// The project the live preview plays: the whole source timeline with sound. Trimming and
    /// muting then never rebuild the player; the transport applies them instead (see
    /// `playbackStart(from:)` and `isAtPlaybackEnd(_:)`), and export uses the real project.
    var previewProject: StudioProject {
        var copy = project
        copy.edits = StudioEdits()
        copy.audio.muted = false
        return copy
    }

    func clampedSourceTime(_ sourceTime: Double) -> Double {
        guard sourceTime.isFinite else { return 0 }
        return StudioCanvasMath.clamped(sourceTime, 0, sourceDuration)
    }

    /// The first instant the video shows: the trim start, or the end of a cut that begins there.
    /// The trim start when cuts leave nothing.
    var playbackStart: Double { timeMap.segments.first?.start ?? trimStart }

    /// Where the video ends: the trim end, or the start of a cut that runs up to it. The trim
    /// start when cuts leave nothing.
    var playbackEnd: Double { timeMap.segments.last?.end ?? trimStart }

    /// Where playback starts when Play is pressed at `sourceTime`: the same spot, the end of the
    /// cut it is in, or the start of the video when the playhead is outside what the video shows
    /// or already at its end.
    func playbackStart(from sourceTime: Double) -> Double {
        let time = clampedSourceTime(sourceTime)
        if time < playbackStart || time >= playbackEnd - frameDuration / 2 {
            return playbackStart
        }
        return cutSkipTarget(at: time) ?? time
    }

    /// Whether a playing preview has reached the end of the video and should stop.
    func isAtPlaybackEnd(_ sourceTime: Double) -> Bool {
        sourceTime >= playbackEnd - 1e-6
    }

    static func formattedTime(_ seconds: Double) -> String {
        guard seconds.isFinite else { return "0:00.0" }
        let clamped = min(max(0, seconds), 359_999)
        let tenths = Int((clamped * 10).rounded(.down))
        return String(format: "%d:%02d.%d", tenths / 600, (tenths / 10) % 60, tenths % 10)
    }

    // MARK: - Undo

    /// Starts a gesture. Every edit until `commitEditingGroup()` becomes one undo step.
    mutating func beginEditingGroup() {
        guard groupedSnapshot == nil else { return }
        groupedSnapshot = editableState
    }

    mutating func commitEditingGroup() {
        guard let snapshot = groupedSnapshot else { return }
        groupedSnapshot = nil
        pushUndo(snapshot)
    }

    mutating func cancelEditingGroup() {
        guard let snapshot = groupedSnapshot else { return }
        groupedSnapshot = nil
        project = snapshot.applied(to: project)
    }

    mutating func undo() {
        commitEditingGroup()
        guard let previous = undoStack.popLast() else { return }
        redoStack.append(editableState)
        project = previous.applied(to: project)
    }

    mutating func redo() {
        commitEditingGroup()
        guard let next = redoStack.popLast() else { return }
        undoStack.append(editableState)
        project = next.applied(to: project)
    }

    // MARK: - Layout and Canvas

    /// Sets the layout of the current scene. Layouts that need a camera are ignored without one.
    mutating func setLayout(_ layout: StudioLayout) {
        // Without a camera the screen is always shown alone, so no choice changes what is drawn.
        guard hasCamera else { return }
        let index = currentSceneIndex
        mutate { $0.scenes[index].layout = layout }
    }

    mutating func setCanvasAspect(_ aspect: StudioCanvasAspect) {
        mutate { $0.canvas.aspect = aspect }
    }

    mutating func setCanvasPadding(_ padding: Double) {
        mutate { $0.canvas.padding = StudioCanvasMath.clamped(padding, 0, 0.4) }
    }

    /// Colors are `#RRGGBB`; anything else keeps the color that was there.
    mutating func setBackground(
        style: StudioBackgroundStyle,
        preset: String?,
        primary: String,
        secondary: String? = nil,
        image: String? = nil
    ) {
        let current = project.canvas.background
        let newPrimary = Self.normalizedHex(primary) ?? current.primary
        let newSecondary = secondary.map { Self.normalizedHex($0) ?? current.secondary ?? newPrimary }
        let newImage = image.flatMap { StudioJSON.isPlainFileName($0) ? $0 : nil }
        mutate {
            $0.canvas.background.style = style
            $0.canvas.background.preset = preset
            $0.canvas.background.primary = newPrimary
            $0.canvas.background.secondary = newSecondary
            $0.canvas.background.image = newImage
        }
    }

    // MARK: - Screen and Camera

    mutating func setScreenCornerRadius(_ value: Double) {
        mutate { $0.screen.cornerRadius = StudioCanvasMath.clamped(value, 0, 0.2) }
    }

    mutating func setScreenShadow(_ value: Double) {
        mutate { $0.screen.shadow = StudioCanvasMath.clamped(value, 0, 1) }
    }

    mutating func setCameraShape(_ shape: StudioCameraShape) {
        mutate { $0.camera.shape = shape }
    }

    mutating func setCameraCornerRadius(_ value: Double) {
        mutate { $0.camera.cornerRadius = StudioCanvasMath.clamped(value, 0, 0.5) }
    }

    mutating func setCameraMirror(_ isMirrored: Bool) {
        mutate { $0.camera.mirror = isMirrored }
    }

    mutating func setCameraBorderWidth(_ value: Double) {
        mutate { $0.camera.borderWidth = StudioCanvasMath.clamped(value, 0, 0.02) }
    }

    mutating func setCameraBorderColor(_ value: String) {
        guard let color = Self.normalizedHex(value) else { return }
        mutate { $0.camera.borderColor = color }
    }

    mutating func setCameraShadow(_ value: Double) {
        mutate { $0.camera.shadow = StudioCanvasMath.clamped(value, 0, 1) }
    }

    mutating func setCameraBubbleSize(_ value: Double) {
        let index = currentSceneIndex
        mutate { $0.scenes[index].bubble.size = StudioCanvasMath.clamped(value, 0.08, 0.6) }
    }

    /// Snaps the bubble to a corner, clearing any offset from dragging.
    mutating func setCameraAnchor(_ anchor: StudioAnchor) {
        let index = currentSceneIndex
        mutate {
            $0.scenes[index].bubble.anchor = anchor
            $0.scenes[index].bubble.offsetX = 0
            $0.scenes[index].bubble.offsetY = 0
        }
    }

    mutating func setCameraBubbleOffsets(x: Double, y: Double) {
        let index = currentSceneIndex
        mutate {
            $0.scenes[index].bubble.offsetX = x.isFinite ? StudioCanvasMath.clamped(x, -1, 1) : 0
            $0.scenes[index].bubble.offsetY = y.isFinite ? StudioCanvasMath.clamped(y, -1, 1) : 0
        }
    }

    mutating func setSideBySide(cameraSide: StudioCameraSide, fraction: Double) {
        let index = currentSceneIndex
        mutate {
            $0.scenes[index].split.cameraSide = cameraSide
            $0.scenes[index].split.cameraFraction = StudioCanvasMath.clamped(fraction, 0.15, 0.6)
        }
    }

    // MARK: - Scenes

    /// The scene a time is in (section 6.1): the last one that has started.
    func sceneIndex(at sourceTime: Double) -> Int {
        var index = 0
        for (position, scene) in project.scenes.enumerated() where scene.start <= sourceTime {
            index = position
        }
        return index
    }

    /// When a scene starts and ends. It ends where the next one starts, or with the recording.
    func sceneRange(at index: Int) -> (start: Double, end: Double)? {
        let scenes = project.scenes
        guard scenes.indices.contains(index) else { return nil }
        let end = index + 1 < scenes.count ? scenes[index + 1].start : sourceDuration
        return (scenes[index].start, max(scenes[index].start, end))
    }

    /// How long the layers take to move into a scene, as the layout applies it (section 6.9): 0
    /// for a cut and for the first scene, and never longer than the scene.
    func sceneTransitionLength(at index: Int) -> Double {
        StudioLayoutResolver.transitionLength(project.scenes, index: index)
    }

    /// A time at which a scene has been entered, to show it at: the end of its move, kept a frame
    /// inside the scene, which contains its start and not its end.
    func sceneLookTime(at index: Int) -> Double? {
        guard let range = sceneRange(at: index) else { return nil }
        let time = max(range.start, min(range.start + sceneTransitionLength(at: index), range.end - frameDuration))
        return clampedSourceTime(time)
    }

    /// Why a scene cannot be split at a time, or nil when it can be.
    func splitSceneExplanation(at sourceTime: Double) -> String? {
        guard hasCamera else { return Self.noCameraForScenesExplanation }
        guard sourceTime.isFinite else { return Self.sceneTooShortToSplitExplanation }
        let time = clampedSourceTime(sourceTime)
        let index = sceneIndex(at: time)
        guard let range = sceneRange(at: index),
              time - range.start >= Self.minimumSceneDuration,
              range.end - time >= Self.minimumSceneDuration else {
            return Self.sceneTooShortToSplitExplanation
        }
        guard time >= range.start + sceneTransitionLength(at: index) else {
            return Self.sceneStillMovingExplanation
        }
        return nil
    }

    func canSplitScene(at sourceTime: Double) -> Bool {
        splitSceneExplanation(at: sourceTime) == nil
    }

    /// Starts a new scene at a time: a copy of the scene that time is in, entered by moving. Both
    /// halves have to last `minimumSceneDuration`, and the scene has to be at rest there.
    @discardableResult
    mutating func splitScene(at sourceTime: Double) -> StudioSceneEditResult {
        let time = clampedSourceTime(sourceTime)
        let index = sceneIndex(at: time)
        guard canSplitScene(at: sourceTime) else {
            return StudioSceneEditResult(changed: false, index: index)
        }
        var added = project.scenes[index]
        added.start = time
        added.transition = StudioTransition(kind: .morph)
        let scene = added
        mutate { $0.scenes.insert(scene, at: index + 1) }
        return StudioSceneEditResult(changed: true, index: index + 1)
    }

    /// Whether a scene can be deleted: any scene but the only one.
    func canRemoveScene(at index: Int) -> Bool {
        project.scenes.count > 1 && project.scenes.indices.contains(index)
    }

    /// Deletes a scene. The scene before it then lasts until the next one; deleting the first
    /// hands its time to the second, which then starts at 0.
    @discardableResult
    mutating func removeScene(at index: Int) -> StudioSceneEditResult {
        guard canRemoveScene(at: index) else {
            return StudioSceneEditResult(changed: false, index: min(max(0, index), project.scenes.count - 1))
        }
        mutate {
            $0.scenes.remove(at: index)
            $0.scenes[0].start = 0
        }
        return StudioSceneEditResult(changed: true, index: max(0, index - 1))
    }

    /// Moves where a scene starts. It stays `minimumSceneDuration` after the start of the scene
    /// before it and as long before its own end. The first scene always starts at 0.
    @discardableResult
    mutating func setSceneStart(at index: Int, to sourceTime: Double) -> StudioSceneEditResult {
        let unchanged = StudioSceneEditResult(changed: false, index: min(max(0, index), project.scenes.count - 1))
        guard index >= 1, sourceTime.isFinite, let range = sceneRange(at: index) else { return unchanged }
        let earliest = project.scenes[index - 1].start + Self.minimumSceneDuration
        let latest = range.end - Self.minimumSceneDuration
        guard earliest <= latest else { return unchanged }
        let start = min(max(sourceTime, earliest), latest)
        guard start != project.scenes[index].start else { return unchanged }
        mutate { $0.scenes[index].start = start }
        return StudioSceneEditResult(changed: true, index: index)
    }

    /// Sets whether a scene is cut to or entered by moving. The first scene is entered at once
    /// whatever it says, so it is left alone.
    @discardableResult
    mutating func setSceneTransitionKind(at index: Int, to kind: StudioTransitionKind) -> StudioSceneEditResult {
        editSceneTransition(at: index) { $0.kind = kind }
    }

    /// Sets how long the move into a scene takes, within `sceneTransitionDurationRange`.
    @discardableResult
    mutating func setSceneTransitionDuration(at index: Int, to seconds: Double) -> StudioSceneEditResult {
        guard seconds.isFinite else {
            return StudioSceneEditResult(changed: false, index: min(max(0, index), project.scenes.count - 1))
        }
        let range = Self.sceneTransitionDurationRange
        return editSceneTransition(at: index) {
            $0.duration = StudioCanvasMath.clamped(seconds, range.lowerBound, range.upperBound)
        }
    }

    // MARK: - Crops

    /// Shows only part of the screen. A rectangle that is not a valid crop (section 3 of the project
    /// format) is made one: its size is brought to between 0.05 and 1 first, and then it is moved
    /// back inside the frame. `nil` removes the crop. A rectangle with a member that is not a
    /// number is ignored.
    mutating func setScreenCrop(_ crop: StudioRect?) {
        guard crop.map(Self.isFinite) ?? true else { return }
        mutate { $0.screen.crop = crop.map(Self.clampedCrop) }
    }

    mutating func clearScreenCrop() {
        setScreenCrop(nil)
    }

    /// Shows only part of the camera picture. The same rules as `setScreenCrop(_:)`.
    mutating func setCameraCrop(_ crop: StudioRect?) {
        guard crop.map(Self.isFinite) ?? true else { return }
        mutate { $0.camera.crop = crop.map(Self.clampedCrop) }
    }

    mutating func clearCameraCrop() {
        setCameraCrop(nil)
    }

    /// How much the screen crop cuts off each edge.
    var screenCropInsets: StudioCropInsets { StudioCropInsets(crop: project.screen.crop) }

    /// How much the camera crop cuts off each edge.
    var cameraCropInsets: StudioCropInsets { StudioCropInsets(crop: project.camera.crop) }

    /// Moves one edge of the screen crop to cut off `value` of the frame. With nothing cut off any
    /// edge, the crop is removed.
    mutating func setScreenCropInset(_ edge: StudioCropEdge, to value: Double) {
        setScreenCrop(screenCropInsets.with(edge, value).crop(from: project.screen.crop))
    }

    /// Moves one edge of the camera crop. The same rules as `setScreenCropInset(_:to:)`.
    mutating func setCameraCropInset(_ edge: StudioCropEdge, to value: Double) {
        setCameraCrop(cameraCropInsets.with(edge, value).crop(from: project.camera.crop))
    }

    // MARK: - Zooms
    //
    // `project.zooms` is kept in time order, so a zoom's index identifies it between two edits.
    // Every edit that can move or remove a zoom says where it is afterwards.

    /// The zoom that contains `sourceTime`, or nil. A zoom contains its start and not its end, as
    /// in the layout.
    func zoomIndex(at sourceTime: Double) -> Int? {
        project.zooms.firstIndex { $0.start <= sourceTime && sourceTime < $0.end }
    }

    /// Adds a zoom that starts at `sourceTime` and lasts `newZoomDuration`, or until the next zoom
    /// or the end of the recording when that comes sooner. It looks at where the pointer is at that
    /// time, or at the center when the recording has no cursor samples.
    ///
    /// Returns the new zoom's index. Unchanged with the index of the zoom that is already there,
    /// and unchanged with no index when there is no room for `minimumZoomDuration`.
    @discardableResult
    mutating func addZoom(at sourceTime: Double, events: StudioEvents? = nil) -> StudioZoomEditResult {
        guard sourceTime.isFinite else { return StudioZoomEditResult(changed: false, index: nil) }
        let start = clampedSourceTime(sourceTime)
        if let existing = zoomIndex(at: start) {
            return StudioZoomEditResult(changed: false, index: existing)
        }
        guard let place = newZoomPlace(startingAt: start) else {
            return StudioZoomEditResult(changed: false, index: nil)
        }

        // The same smoothing as a zoom that follows the pointer, taken once and kept as a point.
        let pointer = events?.preparedCursorSamples.focus(at: start)
        let zoom = StudioZoom(
            start: start,
            end: place.end,
            focus: StudioZoomFocus(
                mode: .point,
                x: StudioCanvasMath.clamped(pointer?.x ?? 0.5, 0, 1),
                y: StudioCanvasMath.clamped(pointer?.y ?? 0.5, 0, 1)
            )
        )
        mutate { $0.zooms.insert(zoom, at: place.index) }
        return StudioZoomEditResult(changed: true, index: place.index)
    }

    /// Whether `addZoom(at:events:)` at this time has a zoom to answer with: a new one, or the one
    /// that is already there. False where less than `minimumZoomDuration` fits.
    func canAddZoom(at sourceTime: Double) -> Bool {
        guard sourceTime.isFinite else { return false }
        let start = clampedSourceTime(sourceTime)
        return zoomIndex(at: start) != nil || newZoomPlace(startingAt: start) != nil
    }

    @discardableResult
    mutating func removeZoom(at index: Int) -> StudioZoomEditResult {
        guard project.zooms.indices.contains(index) else {
            return StudioZoomEditResult(changed: false, index: nil)
        }
        mutate { $0.zooms.remove(at: index) }
        return StudioZoomEditResult(changed: true, index: nil)
    }

    /// Moves a zoom's start. It stays at or after the end of the zoom before it, and at least
    /// `minimumZoomDuration` before its own end. Moved up against the zoom before it, it takes
    /// exactly that zoom's end, which is what chains the two (section 6.8).
    @discardableResult
    mutating func setZoomStart(at index: Int, to sourceTime: Double) -> StudioZoomEditResult {
        guard project.zooms.indices.contains(index) else {
            return StudioZoomEditResult(changed: false, index: nil)
        }
        guard sourceTime.isFinite else { return StudioZoomEditResult(changed: false, index: index) }

        // Zooms never overlap, so where both limits cannot be kept the zoom before decides.
        let earliest = index > 0 ? project.zooms[index - 1].end : 0
        let latest = project.zooms[index].end - Self.minimumZoomDuration
        let start = max(earliest, min(sourceTime, latest))
        return editZoom(at: index) { $0.start = start }
    }

    /// Moves a zoom's end. It stays at least `minimumZoomDuration` after its own start, and at or
    /// before the start of the next zoom and the end of the recording. Moved up against the next
    /// zoom, it takes exactly that zoom's start.
    @discardableResult
    mutating func setZoomEnd(at index: Int, to sourceTime: Double) -> StudioZoomEditResult {
        guard project.zooms.indices.contains(index) else {
            return StudioZoomEditResult(changed: false, index: nil)
        }
        guard sourceTime.isFinite else { return StudioZoomEditResult(changed: false, index: index) }

        let earliest = project.zooms[index].start + Self.minimumZoomDuration
        let latest = index + 1 < project.zooms.count ? project.zooms[index + 1].start : sourceDuration
        let end = min(max(sourceTime, earliest), latest)
        return editZoom(at: index) { $0.end = end }
    }

    @discardableResult
    mutating func setZoomScale(at index: Int, to value: Double) -> StudioZoomEditResult {
        editZoom(at: index, isValid: value.isFinite) { $0.scale = StudioCanvasMath.clamped(value, 1, 5) }
    }

    @discardableResult
    mutating func setZoomFocusMode(at index: Int, to mode: StudioZoomFocusMode) -> StudioZoomEditResult {
        editZoom(at: index) { $0.focus.mode = mode }
    }

    /// Where a zoom looks, as a point in the screen frame from 0 to 1.
    @discardableResult
    mutating func setZoomFocusPoint(at index: Int, x: Double, y: Double) -> StudioZoomEditResult {
        editZoom(at: index, isValid: x.isFinite && y.isFinite) {
            $0.focus.x = StudioCanvasMath.clamped(x, 0, 1)
            $0.focus.y = StudioCanvasMath.clamped(y, 0, 1)
        }
    }

    @discardableResult
    mutating func setZoomEaseIn(at index: Int, to seconds: Double) -> StudioZoomEditResult {
        editZoom(at: index, isValid: seconds.isFinite) { $0.easeIn = StudioCanvasMath.clamped(seconds, 0, 3) }
    }

    @discardableResult
    mutating func setZoomEaseOut(at index: Int, to seconds: Double) -> StudioZoomEditResult {
        editZoom(at: index, isValid: seconds.isFinite) { $0.easeOut = StudioCanvasMath.clamped(seconds, 0, 3) }
    }

    /// Moves a whole zoom so it starts at `sourceTime`, keeping its length. It stays between the
    /// zoom before it and the zoom after it, or the ends of the recording. Moved up against a
    /// neighbour it takes exactly the neighbour's number, which chains the two.
    @discardableResult
    mutating func moveZoom(at index: Int, to sourceTime: Double) -> StudioZoomEditResult {
        guard project.zooms.indices.contains(index) else {
            return StudioZoomEditResult(changed: false, index: nil)
        }
        guard sourceTime.isFinite else { return StudioZoomEditResult(changed: false, index: index) }

        let zoom = project.zooms[index]
        let length = zoom.end - zoom.start
        let earliest = index > 0 ? project.zooms[index - 1].end : 0
        let latestEnd = index + 1 < project.zooms.count ? project.zooms[index + 1].start : sourceDuration

        let start: Double
        let end: Double
        if sourceTime <= earliest {
            start = earliest
            end = min(earliest + length, latestEnd)
        } else if sourceTime + length >= latestEnd {
            end = latestEnd
            start = max(earliest, latestEnd - length)
        } else {
            start = sourceTime
            end = sourceTime + length
        }

        // Only a file written elsewhere can leave no room between two neighbours.
        guard end > start else { return StudioZoomEditResult(changed: false, index: index) }
        return editZoom(at: index) {
            $0.start = start
            $0.end = end
        }
    }

    /// The zoom after the selected one, for stepping through the zooms. With nothing selected, the
    /// zoom at `playhead` or the first one after it. Nil when there is none.
    func zoomIndex(after selected: Int?, playhead: Double) -> Int? {
        let zooms = project.zooms
        if let selected, zooms.indices.contains(selected) {
            return selected + 1 < zooms.count ? selected + 1 : nil
        }
        return zooms.firstIndex { $0.end > playhead }
    }

    /// The zoom before the selected one. With nothing selected, the zoom at `playhead` or the last
    /// one before it. Nil when there is none.
    func zoomIndex(before selected: Int?, playhead: Double) -> Int? {
        let zooms = project.zooms
        if let selected, zooms.indices.contains(selected) {
            return selected > 0 ? selected - 1 : nil
        }
        return zooms.lastIndex { $0.start <= playhead }
    }

    /// Where the zoom at `index` of `before` is in `after`, for an edit that did not say, such as
    /// undo. When the two lists differ in that one zoom at most, it is that zoom, changed: the same
    /// place. Otherwise it is the zoom that shares the most time with it, and among equals the one
    /// nearest to where it was. Nil when there was no such zoom, or none shares any time with it.
    static func zoomIndex(following index: Int, from before: [StudioZoom], to after: [StudioZoom]) -> Int? {
        guard before.indices.contains(index) else { return nil }

        if before.count == after.count,
           before.indices.allSatisfy({ $0 == index || before[$0] == after[$0] }) {
            return index
        }

        let zoom = before[index]
        var best: Int?
        var bestShared = 0.0
        for (position, candidate) in after.enumerated() {
            let shared = min(zoom.end, candidate.end) - max(zoom.start, candidate.start)
            guard shared > 0 else { continue }
            if let current = best {
                let isNearer = shared == bestShared && abs(position - index) < abs(current - index)
                guard shared > bestShared || isNearer else { continue }
            }
            best = position
            bestShared = shared
        }
        return best
    }

    /// A time at which a zoom has moved all the way in, to show it at: the end of its ease in. Kept
    /// a frame inside the zoom, which contains its start and not its end.
    func zoomLookTime(at index: Int) -> Double? {
        let zooms = project.zooms
        guard zooms.indices.contains(index) else { return nil }
        let zoom = zooms[index]

        // The eases as the layout applies them (section 6.8 of the project format).
        let nextIsChained = index + 1 < zooms.count && zooms[index + 1].start == zoom.end
        var easeIn = StudioCanvasMath.clamped(zoom.easeIn, 0, 3)
        let easeOut = nextIsChained ? 0 : StudioCanvasMath.clamped(zoom.easeOut, 0, 3)
        let length = zoom.end - zoom.start
        if easeIn + easeOut > length, easeIn + easeOut > 0 {
            easeIn *= length / (easeIn + easeOut)
        }

        let time = max(zoom.start, min(zoom.start + easeIn, zoom.end - frameDuration))
        return clampedSourceTime(time)
    }

    /// What the focus pad shows for a zoom, with the point the zoom stores. Nil when there is no
    /// such zoom.
    func zoomPad(at index: Int) -> StudioZoomPad? {
        guard project.zooms.indices.contains(index) else { return nil }
        let zoom = project.zooms[index]
        let area = StudioLayoutResolver.screenBaseRect(project: project)
        let focusX = StudioCanvasMath.clamped(zoom.focus.x, 0, 1)
        let focusY = StudioCanvasMath.clamped(zoom.focus.y, 0, 1)
        let held = StudioLayoutResolver.heldWindow(base: area, scale: zoom.scale, focusX: focusX, focusY: focusY)
        let screen = project.sources.screen
        let aspect = screen.width > 0 && screen.height > 0
            ? (Double(screen.width) * area.width) / (Double(screen.height) * area.height)
            : 16.0 / 9
        return StudioZoomPad(
            aspectRatio: aspect,
            window: StudioRect(
                x: (held.x - area.x) / area.width,
                y: (held.y - area.y) / area.height,
                width: held.width / area.width,
                height: held.height / area.height
            ),
            focusX: StudioCanvasMath.clamped((focusX - area.x) / area.width, 0, 1),
            focusY: StudioCanvasMath.clamped((focusY - area.y) / area.height, 0, 1)
        )
    }

    /// Points a zoom at a place on its focus pad, from 0 to 1 across and down the pad.
    @discardableResult
    mutating func setZoomFocusOnPad(at index: Int, x: Double, y: Double) -> StudioZoomEditResult {
        guard x.isFinite, y.isFinite else {
            return StudioZoomEditResult(changed: false, index: project.zooms.indices.contains(index) ? index : nil)
        }
        let area = StudioLayoutResolver.screenBaseRect(project: project)
        return setZoomFocusPoint(
            at: index,
            x: area.x + StudioCanvasMath.clamped(x, 0, 1) * area.width,
            y: area.y + StudioCanvasMath.clamped(y, 0, 1) * area.height
        )
    }

    /// How many of the zooms are suggestions that have not been changed since they were made.
    var suggestedZoomCount: Int {
        project.zooms.filter { $0.origin == .auto }.count
    }

    /// Replaces the suggested zooms (origin `auto`) with `suggestions` and leaves every other zoom
    /// as it is. One undo step. Returns whether the project changed.
    @discardableResult
    mutating func applyZoomSuggestions(_ suggestions: [StudioZoom]) -> Bool {
        let suggested = suggestions.map { suggestion -> StudioZoom in
            var copy = suggestion
            copy.origin = .auto
            return copy
        }
        let before = project.zooms
        mutate { $0.zooms = Self.sortedByStart($0.zooms.filter { $0.origin != .auto } + suggested) }
        return project.zooms != before
    }

    // MARK: - Cuts
    //
    // Stretches of the recording the video leaves out. They are stored in source time, so a cut
    // moves nothing else. `project.edits.cuts` is kept in time order without overlaps, so a cut's
    // index identifies it between two edits. Two cuts may touch; the time map plays them as one.

    /// The cut that contains `sourceTime`, or nil. A cut contains its start and not its end, as in
    /// the time map: the frame at its end is the first one kept again.
    func cutIndex(at sourceTime: Double) -> Int? {
        project.edits.cuts.firstIndex { $0.start <= sourceTime && sourceTime < $0.end }
    }

    /// Where a playing preview goes on from when it has reached a stretch the video leaves out:
    /// the next instant that is kept. Nil where `sourceTime` is kept itself, and outside what the
    /// video shows, where playback starts or stops and does not jump.
    func cutSkipTarget(at sourceTime: Double) -> Double? {
        let segments = timeMap.segments
        for index in segments.indices.dropLast()
        where sourceTime >= segments[index].end && sourceTime < segments[index + 1].start {
            return segments[index + 1].start
        }
        return nil
    }

    /// Adds a cut that starts at `sourceTime` and lasts `newCutDuration`, or until the next cut or
    /// the end of the recording when that comes sooner.
    ///
    /// Returns the new cut's index. Unchanged with the index of the cut that is already there, and
    /// unchanged with no index when there is no room for `minimumCutDuration` or the cut would
    /// leave no video.
    @discardableResult
    mutating func addCut(at sourceTime: Double) -> StudioCutEditResult {
        guard sourceTime.isFinite else { return StudioCutEditResult(changed: false, index: nil) }
        let start = clampedSourceTime(sourceTime)
        if let existing = cutIndex(at: start) {
            return StudioCutEditResult(changed: false, index: existing)
        }
        guard let place = newCutPlace(startingAt: start) else {
            return StudioCutEditResult(changed: false, index: nil)
        }

        var cuts = project.edits.cuts
        cuts.insert(StudioTimeRange(start: start, end: place.end), at: place.index)
        return setCuts(cuts)
            ? StudioCutEditResult(changed: true, index: place.index)
            : StudioCutEditResult(changed: false, index: nil)
    }

    /// Whether `addCut(at:)` at this time has a cut to answer with: a new one, or the one that is
    /// already there.
    func canAddCut(at sourceTime: Double) -> Bool {
        guard sourceTime.isFinite else { return false }
        let start = clampedSourceTime(sourceTime)
        if cutIndex(at: start) != nil {
            return true
        }
        guard let place = newCutPlace(startingAt: start) else { return false }
        var edits = project.edits
        edits.cuts.insert(StudioTimeRange(start: start, end: place.end), at: place.index)
        return leavesEnoughVideo(edits)
    }

    /// Deletes a cut, which puts its stretch back into the video.
    @discardableResult
    mutating func removeCut(at index: Int) -> StudioCutEditResult {
        guard project.edits.cuts.indices.contains(index) else {
            return StudioCutEditResult(changed: false, index: nil)
        }
        mutate { $0.edits.cuts.remove(at: index) }
        return StudioCutEditResult(changed: true, index: nil)
    }

    /// Moves a cut's start. It stays at or after the end of the cut before it, and at least
    /// `minimumCutDuration` before its own end.
    @discardableResult
    mutating func setCutStart(at index: Int, to sourceTime: Double) -> StudioCutEditResult {
        let cuts = project.edits.cuts
        guard cuts.indices.contains(index) else {
            return StudioCutEditResult(changed: false, index: nil)
        }
        guard sourceTime.isFinite else { return StudioCutEditResult(changed: false, index: index) }

        // Cuts never overlap, so where both limits cannot be kept the cut before decides.
        let earliest = index > 0 ? cuts[index - 1].end : 0
        let latest = cuts[index].end - Self.minimumCutDuration
        return replaceCut(at: index, start: max(earliest, min(sourceTime, latest)), end: cuts[index].end)
    }

    /// Moves a cut's end. It stays at least `minimumCutDuration` after its own start, and at or
    /// before the start of the next cut and the end of the recording.
    @discardableResult
    mutating func setCutEnd(at index: Int, to sourceTime: Double) -> StudioCutEditResult {
        let cuts = project.edits.cuts
        guard cuts.indices.contains(index) else {
            return StudioCutEditResult(changed: false, index: nil)
        }
        guard sourceTime.isFinite else { return StudioCutEditResult(changed: false, index: index) }

        let earliest = cuts[index].start + Self.minimumCutDuration
        let latest = index + 1 < cuts.count ? cuts[index + 1].start : sourceDuration
        return replaceCut(at: index, start: cuts[index].start, end: min(max(sourceTime, earliest), latest))
    }

    /// Moves a whole cut so it starts at `sourceTime`, keeping its length. It stays between the cut
    /// before it and the cut after it, or the ends of the recording.
    @discardableResult
    mutating func moveCut(at index: Int, to sourceTime: Double) -> StudioCutEditResult {
        let cuts = project.edits.cuts
        guard cuts.indices.contains(index) else {
            return StudioCutEditResult(changed: false, index: nil)
        }
        guard sourceTime.isFinite else { return StudioCutEditResult(changed: false, index: index) }

        let length = cuts[index].end - cuts[index].start
        let earliest = index > 0 ? cuts[index - 1].end : 0
        let latestEnd = index + 1 < cuts.count ? cuts[index + 1].start : sourceDuration

        let start: Double
        let end: Double
        if sourceTime <= earliest {
            start = earliest
            end = min(earliest + length, latestEnd)
        } else if sourceTime + length >= latestEnd {
            end = latestEnd
            start = max(earliest, latestEnd - length)
        } else {
            start = sourceTime
            end = sourceTime + length
        }

        guard end > start else { return StudioCutEditResult(changed: false, index: index) }
        return replaceCut(at: index, start: start, end: end)
    }

    /// The cut after the selected one, for stepping through the cuts. With nothing selected, the
    /// cut at `playhead` or the first one after it. Nil when there is none.
    func cutIndex(after selected: Int?, playhead: Double) -> Int? {
        let cuts = project.edits.cuts
        if let selected, cuts.indices.contains(selected) {
            return selected + 1 < cuts.count ? selected + 1 : nil
        }
        return cuts.firstIndex { $0.end > playhead }
    }

    /// The cut before the selected one. With nothing selected, the cut at `playhead` or the last
    /// one before it. Nil when there is none.
    func cutIndex(before selected: Int?, playhead: Double) -> Int? {
        let cuts = project.edits.cuts
        if let selected, cuts.indices.contains(selected) {
            return selected > 0 ? selected - 1 : nil
        }
        return cuts.lastIndex { $0.start <= playhead }
    }

    /// Where the cut at `index` of `before` is in `after`, for an edit that did not say, such as
    /// undo. When the two lists differ in that one cut at most, it is that cut, changed: the same
    /// place. Otherwise it is the cut that shares the most time with it, and among equals the one
    /// nearest to where it was. Nil when there was no such cut, or none shares any time with it.
    static func cutIndex(following index: Int, from before: [StudioTimeRange], to after: [StudioTimeRange]) -> Int? {
        guard before.indices.contains(index) else { return nil }

        if before.count == after.count,
           before.indices.allSatisfy({
               $0 == index || (before[$0].start == after[$0].start && before[$0].end == after[$0].end)
           }) {
            return index
        }

        let cut = before[index]
        var best: Int?
        var bestShared = 0.0
        for (position, candidate) in after.enumerated() {
            let shared = min(cut.end, candidate.end) - max(cut.start, candidate.start)
            guard shared > 0 else { continue }
            if let current = best {
                let isNearer = shared == bestShared && abs(position - index) < abs(current - index)
                guard shared > bestShared || isNearer else { continue }
            }
            best = position
            bestShared = shared
        }
        return best
    }

    // MARK: - Bubble Dragging

    /// Moves the camera bubble so its top-left corner is at `desiredTopLeft` (canvas pixels for a
    /// canvas of `canvasSize`), kept on the canvas. This inverts section 6.3 of the format spec: the
    /// bubble is anchored to the corner nearest its center and the rest becomes the offset.
    mutating func moveBubble(topLeft desiredTopLeft: CGPoint, canvasSize: CGSize) {
        guard hasCamera, canvasSize.width > 0, canvasSize.height > 0,
              desiredTopLeft.x.isFinite, desiredTopLeft.y.isFinite else { return }
        let width = Double(canvasSize.width)
        let height = Double(canvasSize.height)
        guard let size = bubbleRect(in: project, width: width, height: height) else { return }

        let x = StudioCanvasMath.clamped(Double(desiredTopLeft.x), 0, max(0, width - size.width))
        let y = StudioCanvasMath.clamped(Double(desiredTopLeft.y), 0, max(0, height - size.height))
        let isLeft = x + size.width / 2 < width / 2
        let isTop = y + size.height / 2 < height / 2
        let anchor: StudioAnchor = isTop ? (isLeft ? .topLeft : .topRight) : (isLeft ? .bottomLeft : .bottomRight)

        let index = currentSceneIndex
        var anchored = project
        anchored.scenes[index].layout = .bubble
        anchored.scenes[index].bubble.anchor = anchor
        anchored.scenes[index].bubble.offsetX = 0
        anchored.scenes[index].bubble.offsetY = 0
        guard let base = bubbleRect(in: anchored, width: width, height: height) else { return }

        mutate {
            $0.scenes[index].bubble.anchor = anchor
            $0.scenes[index].bubble.offsetX = (x - base.x) / width
            $0.scenes[index].bubble.offsetY = (y - base.y) / height
        }
    }

    mutating func moveBubble(center desiredCenter: CGPoint, canvasSize: CGSize) {
        guard hasCamera, canvasSize.width > 0, canvasSize.height > 0,
              let size = bubbleRect(in: project, width: Double(canvasSize.width), height: Double(canvasSize.height))
        else { return }
        moveBubble(
            topLeft: CGPoint(x: Double(desiredCenter.x) - size.width / 2, y: Double(desiredCenter.y) - size.height / 2),
            canvasSize: canvasSize
        )
    }

    /// The rectangle the current scene's bubble has at rest on a canvas of the given size, whatever
    /// its layout is.
    func bubbleRect(canvasSize: CGSize) -> StudioRect? {
        guard canvasSize.width > 0, canvasSize.height > 0 else { return nil }
        return bubbleRect(in: project, width: Double(canvasSize.width), height: Double(canvasSize.height))
    }

    // MARK: - Trim, Audio, Overlays

    /// Sets the trim in source time. The kept range stays inside the recording, in order, and at
    /// least `minimumDuration` long (or the whole recording when it is shorter). A trim that would
    /// leave less than that between the cuts is not made.
    mutating func setTrim(start: Double, end: Double?) {
        let edits = clampedEdits(project.edits, trimStart: start, trimEnd: end)
        guard edits.cuts.isEmpty || leavesEnoughVideo(edits) else { return }
        mutate { $0.edits = edits }
    }

    /// Moves the trim start, leaving the end where it is.
    mutating func setTrimStart(_ value: Double) {
        let end = trimEnd
        let latest = max(0, end - min(Self.minimumDuration, sourceDuration))
        setTrim(start: min(value, latest), end: project.edits.trimEnd)
    }

    /// Moves the trim end, leaving the start where it is.
    mutating func setTrimEnd(_ value: Double) {
        let start = trimStart
        let earliest = min(sourceDuration, start + min(Self.minimumDuration, sourceDuration))
        setTrim(start: start, end: max(value, earliest))
    }

    mutating func setMuted(_ isMuted: Bool) {
        mutate { $0.audio.muted = isMuted }
    }

    mutating func setClickRingsEnabled(_ isEnabled: Bool) {
        mutate { $0.overlays.clicks.enabled = isEnabled }
    }

    mutating func setBrandingEnabled(_ isEnabled: Bool) {
        mutate { $0.overlays.branding = isEnabled }
    }

    // MARK: - Looks

    /// Applies a saved look. Crops belong to one recording, so the project's own crops stay.
    mutating func applyLook(_ look: StudioLook) {
        mutate {
            let screenCrop = $0.screen.crop
            let cameraCrop = $0.camera.crop
            $0.canvas = look.canvas
            $0.screen = look.screen
            $0.camera = look.camera
            $0.screen.crop = screenCrop
            $0.camera.crop = cameraCrop
        }
    }

    // MARK: - Bookkeeping

    /// Records that `exported` (the editable state that was rendered) is now on disk as a video.
    mutating func markExported(_ exported: StudioEditableState) {
        exportedState = exported
    }

    /// Takes the fields the store or other windows own from the saved project, leaving edits alone.
    mutating func refreshBookkeeping(from saved: StudioProject) {
        project.exports = saved.exports
        project.modifiedAt = saved.modifiedAt
        project.lastOpenedAt = saved.lastOpenedAt
        project.keepSources = saved.keepSources
        project.name = saved.name
    }

    // MARK: - Geometry

    /// The canvas aspect-fitted and centered in a view.
    static func canvasGeometry(viewSize: CGSize, canvasSize: CGSize) -> StudioEditorCanvasGeometry {
        guard viewSize.width > 0, viewSize.height > 0, canvasSize.width > 0, canvasSize.height > 0 else {
            return StudioEditorCanvasGeometry(viewSize: viewSize, canvasSize: canvasSize, canvasRectInView: StudioRect())
        }
        let scale = min(viewSize.width / canvasSize.width, viewSize.height / canvasSize.height)
        let width = Double(canvasSize.width * scale)
        let height = Double(canvasSize.height * scale)
        return StudioEditorCanvasGeometry(
            viewSize: viewSize,
            canvasSize: canvasSize,
            canvasRectInView: StudioRect(
                x: (Double(viewSize.width) - width) / 2,
                y: (Double(viewSize.height) - height) / 2,
                width: width,
                height: height
            )
        )
    }

    // MARK: - Accessibility Text

    static func secondsText(_ seconds: Double) -> String {
        String(format: "%.1f seconds", seconds.isFinite ? seconds : 0)
    }

    /// A zoom for VoiceOver, such as "Zoom 2×, 12.0 to 16.5 seconds". The times are source time, as
    /// the trim handles read. "Follows the pointer" and "suggested" are added where they apply.
    static func zoomAccessibilityText(_ zoom: StudioZoom) -> String {
        "Zoom \(zoomDetailText(zoom))"
    }

    /// What is read out when a zoom is stepped to with Previous or Next, which say nothing of where
    /// they land by themselves: which zoom it is, and then what `zoomAccessibilityText` says of it,
    /// such as "Zoom 2 of 5, 2×, 12.0 to 16.5 seconds".
    static func zoomStepText(index: Int, count: Int, zoom: StudioZoom) -> String {
        "\(zoomPositionText(index: index, count: count)), \(zoomDetailText(zoom))"
    }

    /// "2×, 12.0 to 16.5 seconds", with "follows the pointer" and "suggested" where they apply.
    private static func zoomDetailText(_ zoom: StudioZoom) -> String {
        var text = "\(zoomScaleText(zoom.scale)), \(zoomRangeText(zoom))"
        if zoom.focus.mode == .cursor {
            text += ", follows the pointer"
        }
        if zoom.origin == .auto {
            text += ", suggested"
        }
        return text
    }

    /// How much a zoom magnifies, such as "2×" or "1.25×": the scale that is drawn, which is the
    /// stored one kept within 1 to 5, with up to two decimals.
    static func zoomScaleText(_ scale: Double) -> String {
        let drawn = scale.isFinite ? StudioCanvasMath.clamped(scale, 1, 5) : 1
        return "\(scaleText(drawn))×"
    }

    /// When a zoom starts and ends, in source time: "12.0 to 16.5 seconds".
    static func zoomRangeText(_ zoom: StudioZoom) -> String {
        let start = String(format: "%.1f", zoom.start.isFinite ? zoom.start : 0)
        let end = String(format: "%.1f", zoom.end.isFinite ? zoom.end : 0)
        return "\(start) to \(end) seconds"
    }

    /// Which zoom is selected, counting from one: "Zoom 2 of 5".
    static func zoomPositionText(index: Int, count: Int) -> String {
        "Zoom \(index + 1) of \(count)"
    }

    /// What to say after zooms were suggested, given how many suggestions there are now.
    static func zoomSuggestionsText(count: Int) -> String {
        switch count {
        case ...0: return noZoomSuggestionsMessage
        case 1: return "1 zoom suggested."
        default: return "\(count) zooms suggested."
        }
    }

    static func layoutName(_ layout: StudioLayout) -> String {
        switch layout {
        case .screen: return "Screen only"
        case .bubble: return "Screen with camera bubble"
        case .sideBySide: return "Side by side"
        case .camera: return "Camera only"
        }
    }

    static func anchorName(_ anchor: StudioAnchor) -> String {
        switch anchor {
        case .topLeft: return "Top left"
        case .topRight: return "Top right"
        case .bottomLeft: return "Bottom left"
        case .bottomRight: return "Bottom right"
        }
    }

    static func shapeName(_ shape: StudioCameraShape) -> String {
        switch shape {
        case .circle: return "Circle"
        case .roundedRectangle: return "Rounded rectangle"
        case .squircle: return "Squircle"
        case .rectangle: return "Rectangle"
        }
    }

    static func aspectName(_ aspect: StudioCanvasAspect) -> String {
        switch aspect {
        case .auto: return "Auto"
        case .square: return "1:1"
        case .landscape4x3: return "4:3"
        case .landscape16x9: return "16:9"
        case .portrait3x4: return "3:4"
        case .portrait9x16: return "9:16"
        }
    }

    /// A scene for VoiceOver, such as "Scene 2 of 3, Side by side, 12.0 to 30.5 seconds". The times
    /// are source time, as the trim handles read. Empty when there is no such scene.
    func sceneAccessibilityText(at index: Int) -> String {
        guard project.scenes.indices.contains(index) else { return "" }
        let layout = hasCamera ? project.scenes[index].layout : .screen
        let position = Self.scenePositionText(index: index, count: project.scenes.count)
        return "\(position), \(Self.layoutName(layout)), \(sceneRangeText(at: index))"
    }

    /// Which scene the playhead is in, counting from one: "Scene 2 of 3".
    static func scenePositionText(index: Int, count: Int) -> String {
        "Scene \(index + 1) of \(count)"
    }

    /// When a scene starts and ends, in source time: "12.0 to 30.5 seconds".
    func sceneRangeText(at index: Int) -> String {
        guard let range = sceneRange(at: index) else { return "" }
        return "\(String(format: "%.1f", range.start)) to \(String(format: "%.1f", range.end)) seconds"
    }

    /// Says how long the move into a scene really is when the scene is shorter than the time
    /// asked for. Nil when the move takes as long as asked, or the scene is cut to.
    func sceneMoveLimitedText(at index: Int) -> String? {
        guard index >= 1, project.scenes.indices.contains(index),
              project.scenes[index].transition.kind == .morph else { return nil }
        let asked = StudioCanvasMath.clamped(project.scenes[index].transition.duration, 0, 2)
        let length = sceneTransitionLength(at: index)
        guard length < asked else { return nil }
        return "The scene is shorter than that, so the move takes \(String(format: "%.2f", length)) seconds."
    }

    /// A cut for VoiceOver, such as "Cut, 12.0 to 16.5 seconds". The times are source time, as the
    /// trim handles read.
    static func cutAccessibilityText(_ cut: StudioTimeRange) -> String {
        "Cut, \(cutRangeText(cut))"
    }

    /// When a cut starts and ends, in source time: "12.0 to 16.5 seconds".
    static func cutRangeText(_ cut: StudioTimeRange) -> String {
        let start = String(format: "%.1f", cut.start.isFinite ? cut.start : 0)
        let end = String(format: "%.1f", cut.end.isFinite ? cut.end : 0)
        return "\(start) to \(end) seconds"
    }

    /// How long a cut is: "4.5 seconds long".
    static func cutLengthText(_ cut: StudioTimeRange) -> String {
        let length = cut.end - cut.start
        return String(format: "%.1f seconds long", length.isFinite ? max(0, length) : 0)
    }

    /// Which cut is selected, counting from one: "Cut 2 of 3".
    static func cutPositionText(index: Int, count: Int) -> String {
        "Cut \(index + 1) of \(count)"
    }

    /// For example "0:02.5 of 0:10.0".
    func playheadText(sourceTime: Double) -> String {
        "\(Self.formattedTime(outputTime(forSourceTime: sourceTime))) of \(Self.formattedTime(outputDuration))"
    }

    // MARK: - Private

    private mutating func mutate(_ body: (inout StudioProject) -> Void) {
        let before = editableState
        body(&project)
        guard before != editableState else { return }
        if groupedSnapshot == nil {
            pushUndo(before)
        }
    }

    private mutating func pushUndo(_ snapshot: StudioEditableState) {
        guard snapshot != editableState else { return }
        undoStack.append(snapshot)
        if undoStack.count > Self.maximumUndoDepth {
            undoStack.removeFirst(undoStack.count - Self.maximumUndoDepth)
        }
        redoStack.removeAll()
    }

    private mutating func ensureScene() {
        if project.scenes.isEmpty {
            project.scenes = [StudioScene(start: 0, layout: hasCamera ? .bubble : .screen)]
        }
    }

    /// Puts the stored scenes in the order section 6.1 of the format reads them in, and drops those
    /// that start at or after the end of the recording, which are never shown.
    private mutating func normalizeStoredScenes() {
        let normalized = StudioLayoutResolver.normalizeScenes(project.scenes)
        let duration = sourceDuration
        project.scenes = normalized.enumerated().filter { $0.offset == 0 || $0.element.start < duration }.map(\.element)
    }

    /// Changes how a scene is entered. Not the first scene, which is entered at once.
    private mutating func editSceneTransition(at index: Int, _ body: (inout StudioTransition) -> Void) -> StudioSceneEditResult {
        guard index >= 1, project.scenes.indices.contains(index) else {
            return StudioSceneEditResult(changed: false, index: min(max(0, index), project.scenes.count - 1))
        }
        var transition = project.scenes[index].transition
        body(&transition)
        guard transition != project.scenes[index].transition else {
            return StudioSceneEditResult(changed: false, index: index)
        }
        let replacement = transition
        mutate { $0.scenes[index].transition = replacement }
        return StudioSceneEditResult(changed: true, index: index)
    }

    /// Keeps the stored cuts as the editor needs them: inside the recording, in time order, and
    /// without overlaps, which are joined. Ranges that remove nothing are dropped, and with them
    /// any whose start or end is not a number, which compares as neither before nor after.
    private mutating func normalizeStoredCuts() {
        let duration = sourceDuration
        var inside: [StudioTimeRange] = []
        for cut in project.edits.cuts {
            var clamped = cut
            clamped.start = StudioCanvasMath.clamped(cut.start, 0, duration)
            clamped.end = StudioCanvasMath.clamped(cut.end, 0, duration)
            if clamped.end > clamped.start {
                inside.append(clamped)
            }
        }

        // In time order, and for equal starts in the order they were in.
        let ordered = inside.enumerated()
            .sorted {
                if $0.element.start == $1.element.start { return $0.offset < $1.offset }
                return $0.element.start < $1.element.start
            }
            .map(\.element)

        var joined: [StudioTimeRange] = []
        for cut in ordered {
            if let last = joined.last, cut.start < last.end {
                joined[joined.count - 1].end = max(last.end, cut.end)
            } else {
                joined.append(cut)
            }
        }
        project.edits.cuts = joined
    }

    /// At least `minimumDuration` of video has to stay, or the whole recording when it is shorter.
    private func leavesEnoughVideo(_ edits: StudioEdits) -> Bool {
        let kept = StudioTimeMap(sourceDuration: sourceDuration, edits: edits).outputDuration
        return kept >= min(Self.minimumDuration, sourceDuration) - 1e-9
    }

    /// Replaces the cuts unless that would leave too little video. True when the project changed.
    private mutating func setCuts(_ cuts: [StudioTimeRange]) -> Bool {
        var edits = project.edits
        edits.cuts = cuts
        guard leavesEnoughVideo(edits) else { return false }
        let before = editableState
        let replacement = edits
        mutate { $0.edits = replacement }
        return before != editableState
    }

    /// Its neighbors keep a cut in its place in the list, so its index stays.
    private mutating func replaceCut(at index: Int, start: Double, end: Double) -> StudioCutEditResult {
        var cuts = project.edits.cuts
        cuts[index].start = start
        cuts[index].end = end
        return StudioCutEditResult(changed: setCuts(cuts), index: index)
    }

    /// Where a cut starting at a time that no cut contains goes in the list, and where it ends:
    /// after `newCutDuration`, or at the next cut or the end of the recording when that comes
    /// sooner. Nil when that leaves less than the shortest cut.
    private func newCutPlace(startingAt start: Double) -> (index: Int, end: Double)? {
        let cuts = project.edits.cuts
        let index = cuts.firstIndex { $0.start > start } ?? cuts.count
        let nextStart = index < cuts.count ? cuts[index].start : sourceDuration
        let end = min(start + Self.newCutDuration, nextStart)
        guard end - start >= Self.minimumCutDuration else { return nil }
        return (index, end)
    }

    private mutating func normalizeTrim() {
        project.edits = clampedEdits(project.edits, trimStart: project.edits.trimStart, trimEnd: project.edits.trimEnd)
    }

    private mutating func sortStoredZooms() {
        project.zooms = Self.sortedByStart(project.zooms)
    }

    /// Where a zoom starting at a time that no zoom contains goes in the list, and where it ends:
    /// after `newZoomDuration`, or at the next zoom or the end of the recording when that comes
    /// sooner. Nil when that leaves less than the shortest zoom.
    private func newZoomPlace(startingAt start: Double) -> (index: Int, end: Double)? {
        let zooms = project.zooms
        let index = zooms.firstIndex { $0.start > start } ?? zooms.count
        let nextStart = index < zooms.count ? zooms[index].start : sourceDuration
        let end = min(start + Self.newZoomDuration, nextStart, sourceDuration)
        guard end - start >= Self.minimumZoomDuration else { return nil }
        return (index, end)
    }

    /// In time order, and for equal starts in the order they were in.
    private static func sortedByStart(_ zooms: [StudioZoom]) -> [StudioZoom] {
        zooms.enumerated()
            .sorted {
                if $0.element.start == $1.element.start { return $0.offset < $1.offset }
                return $0.element.start < $1.element.start
            }
            .map(\.element)
    }

    /// Changes one zoom. An edit that changes a suggested zoom makes it the user's own; one that
    /// changes nothing leaves it a suggestion.
    private mutating func editZoom(at index: Int, isValid: Bool = true, _ body: (inout StudioZoom) -> Void) -> StudioZoomEditResult {
        guard project.zooms.indices.contains(index) else {
            return StudioZoomEditResult(changed: false, index: nil)
        }
        guard isValid else { return StudioZoomEditResult(changed: false, index: index) }

        var edited = project.zooms[index]
        body(&edited)
        guard edited != project.zooms[index] else {
            return StudioZoomEditResult(changed: false, index: index)
        }

        edited.origin = .manual
        let replacement = edited
        mutate {
            $0.zooms[index] = replacement
            $0.zooms = Self.sortedByStart($0.zooms)
        }
        return StudioZoomEditResult(changed: true, index: project.zooms.firstIndex(of: replacement) ?? index)
    }

    private func clampedEdits(_ edits: StudioEdits, trimStart: Double, trimEnd: Double?) -> StudioEdits {
        var copy = edits
        let duration = sourceDuration
        guard duration > 0 else {
            copy.trimStart = 0
            copy.trimEnd = nil
            return copy
        }
        let minimum = min(Self.minimumDuration, duration)
        let startInput = trimStart.isFinite ? trimStart : 0
        let endInput = trimEnd.map { $0.isFinite ? $0 : duration } ?? duration
        var start = StudioCanvasMath.clamped(startInput, 0, duration - minimum)
        var end = StudioCanvasMath.clamped(endInput, minimum, duration)
        if end - start < minimum {
            if start + minimum <= duration {
                end = start + minimum
            } else {
                start = max(0, end - minimum)
            }
        }
        copy.trimStart = start
        copy.trimEnd = abs(end - duration) < 1e-9 ? nil : end
        return copy
    }

    /// The bubble rectangle the layout resolver produces for the current scene of `project` at rest,
    /// forcing the bubble layout so the answer does not depend on the layout currently shown.
    private func bubbleRect(in project: StudioProject, width: Double, height: Double) -> StudioRect? {
        var copy = project
        let index = currentSceneIndex
        guard copy.scenes.indices.contains(index) else { return nil }
        copy.scenes = [copy.scenes[index]]
        copy.scenes[0].start = 0
        copy.scenes[0].layout = .bubble
        return StudioLayoutResolver.resolve(project: copy, time: 0, canvasWidth: width, canvasHeight: height).camera?.rect
    }

    private static func normalizedHex(_ value: String) -> String? {
        var text = value.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
        if text.hasPrefix("#") {
            text.removeFirst()
        }
        guard text.count == 6 || text.count == 8, UInt64(text, radix: 16) != nil else {
            return nil
        }
        return "#\(text.prefix(6))"
    }

    private static func isFinite(_ rect: StudioRect) -> Bool {
        rect.x.isFinite && rect.y.isFinite && rect.width.isFinite && rect.height.isFinite
    }

    // The size first, then the position: a rectangle dragged past an edge stops there with its size.
    private static func clampedCrop(_ crop: StudioRect) -> StudioRect {
        var copy = crop
        copy.width = StudioCanvasMath.clamped(crop.width, 0.05, 1)
        copy.height = StudioCanvasMath.clamped(crop.height, 0.05, 1)
        copy.x = StudioCanvasMath.clamped(crop.x, 0, 1 - copy.width)
        copy.y = StudioCanvasMath.clamped(crop.y, 0, 1 - copy.height)
        return copy
    }

    /// Up to two decimals without trailing zeros: 2, 2.5, 1.25.
    private static func scaleText(_ scale: Double) -> String {
        var text = String(format: "%.2f", scale)
        while text.hasSuffix("0") {
            text.removeLast()
        }
        if text.hasSuffix(".") {
            text.removeLast()
        }
        return text
    }
}
