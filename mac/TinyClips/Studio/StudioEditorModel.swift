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

/// What an edit to a zoom did.
struct StudioZoomEditResult: Equatable, Sendable {
    /// Whether the project changed.
    var changed: Bool

    /// Where the zoom is in `project.zooms` now, or nil when it is gone or there is none. The list
    /// is kept in time order, so an edit can move a zoom.
    var index: Int?
}

// MARK: - Editor Model

/// The Studio editor's state and every edit it can make, with undo. It is a plain value with no
/// AppKit or AVFoundation in it so it can be tested anywhere.
///
/// The first version edits one scene: `scenes[0]`. Any further scenes are left exactly as they are.
struct StudioEditorModel: Equatable, Sendable {
    static let minimumDuration = 0.1

    /// The shortest zoom the editor makes, in seconds. It is also the shortest suggestion (section 8).
    static let minimumZoomDuration = 0.3

    /// How long a zoom is when it is added, in seconds, where there is room for it.
    static let newZoomDuration = 3.0

    static let maximumUndoDepth = 100

    private(set) var project: StudioProject
    private var undoStack: [StudioEditableState] = []
    private var redoStack: [StudioEditableState] = []
    private var groupedSnapshot: StudioEditableState?
    private var exportedState: StudioEditableState?

    init(project: StudioProject) {
        self.project = project
        ensureScene()
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

    var currentScene: StudioScene { project.scenes.first ?? StudioScene() }

    /// The layout that is drawn: a project without a camera always shows the screen alone.
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

    /// Where playback starts when Play is pressed at `sourceTime`: the same spot, or the trim start
    /// when the playhead is outside the kept range or already at its end.
    func playbackStart(from sourceTime: Double) -> Double {
        let time = clampedSourceTime(sourceTime)
        if time < trimStart || time >= trimEnd - frameDuration / 2 {
            return trimStart
        }
        return time
    }

    /// Whether a playing preview has reached the end of the kept range and should stop.
    func isAtPlaybackEnd(_ sourceTime: Double) -> Bool {
        sourceTime >= trimEnd - 1e-6
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

    /// Sets the layout of the edited scene. Layouts that need a camera are ignored without one.
    mutating func setLayout(_ layout: StudioLayout) {
        // Without a camera the screen is always shown alone, so no choice changes what is drawn.
        guard hasCamera else { return }
        mutate { $0.scenes[0].layout = layout }
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
        mutate { $0.scenes[0].bubble.size = StudioCanvasMath.clamped(value, 0.08, 0.6) }
    }

    /// Snaps the bubble to a corner, clearing any offset from dragging.
    mutating func setCameraAnchor(_ anchor: StudioAnchor) {
        mutate {
            $0.scenes[0].bubble.anchor = anchor
            $0.scenes[0].bubble.offsetX = 0
            $0.scenes[0].bubble.offsetY = 0
        }
    }

    mutating func setCameraBubbleOffsets(x: Double, y: Double) {
        mutate {
            $0.scenes[0].bubble.offsetX = x.isFinite ? StudioCanvasMath.clamped(x, -1, 1) : 0
            $0.scenes[0].bubble.offsetY = y.isFinite ? StudioCanvasMath.clamped(y, -1, 1) : 0
        }
    }

    mutating func setSideBySide(cameraSide: StudioCameraSide, fraction: Double) {
        mutate {
            $0.scenes[0].split.cameraSide = cameraSide
            $0.scenes[0].split.cameraFraction = StudioCanvasMath.clamped(fraction, 0.15, 0.6)
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

        let zooms = project.zooms
        let index = zooms.firstIndex { $0.start > start } ?? zooms.count
        let nextStart = index < zooms.count ? zooms[index].start : sourceDuration
        let end = min(start + Self.newZoomDuration, nextStart, sourceDuration)
        guard end - start >= Self.minimumZoomDuration else {
            return StudioZoomEditResult(changed: false, index: nil)
        }

        // The same smoothing as a zoom that follows the pointer, taken once and kept as a point.
        let pointer = events?.preparedCursorSamples.focus(at: start)
        let zoom = StudioZoom(
            start: start,
            end: end,
            focus: StudioZoomFocus(
                mode: .point,
                x: StudioCanvasMath.clamped(pointer?.x ?? 0.5, 0, 1),
                y: StudioCanvasMath.clamped(pointer?.y ?? 0.5, 0, 1)
            )
        )
        mutate { $0.zooms.insert(zoom, at: index) }
        return StudioZoomEditResult(changed: true, index: index)
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

        var anchored = project
        anchored.scenes[0].layout = .bubble
        anchored.scenes[0].bubble.anchor = anchor
        anchored.scenes[0].bubble.offsetX = 0
        anchored.scenes[0].bubble.offsetY = 0
        guard let base = bubbleRect(in: anchored, width: width, height: height) else { return }

        mutate {
            $0.scenes[0].bubble.anchor = anchor
            $0.scenes[0].bubble.offsetX = (x - base.x) / width
            $0.scenes[0].bubble.offsetY = (y - base.y) / height
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

    /// The bubble's rectangle on a canvas of the given size, whatever layout is current.
    func bubbleRect(canvasSize: CGSize) -> StudioRect? {
        guard canvasSize.width > 0, canvasSize.height > 0 else { return nil }
        return bubbleRect(in: project, width: Double(canvasSize.width), height: Double(canvasSize.height))
    }

    // MARK: - Trim, Audio, Overlays

    /// Sets the trim in source time. The kept range stays inside the recording, in order, and at
    /// least `minimumDuration` long (or the whole recording when it is shorter).
    mutating func setTrim(start: Double, end: Double?) {
        let edits = clampedEdits(project.edits, trimStart: start, trimEnd: end)
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
        // The scale that is drawn, which is the stored one kept within 1 to 5.
        let scale = zoom.scale.isFinite ? StudioCanvasMath.clamped(zoom.scale, 1, 5) : 1
        let start = String(format: "%.1f", zoom.start.isFinite ? zoom.start : 0)
        let end = String(format: "%.1f", zoom.end.isFinite ? zoom.end : 0)
        var text = "Zoom \(scaleText(scale))×, \(start) to \(end) seconds"
        if zoom.focus.mode == .cursor {
            text += ", follows the pointer"
        }
        if zoom.origin == .auto {
            text += ", suggested"
        }
        return text
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

    private mutating func normalizeTrim() {
        project.edits = clampedEdits(project.edits, trimStart: project.edits.trimStart, trimEnd: project.edits.trimEnd)
    }

    private mutating func sortStoredZooms() {
        project.zooms = Self.sortedByStart(project.zooms)
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

    /// The bubble rectangle the layout resolver produces for `project`, forcing the bubble layout so
    /// the answer does not depend on the layout currently shown.
    private func bubbleRect(in project: StudioProject, width: Double, height: Double) -> StudioRect? {
        var copy = project
        guard !copy.scenes.isEmpty else { return nil }
        copy.scenes = [copy.scenes[0]]
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
