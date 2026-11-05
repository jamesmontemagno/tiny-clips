import Foundation

// MARK: - Model

enum StudioAnnotationAttach: String, StudioStringEnum {
    case screen
    case frame

    static let defaultValue: StudioAnnotationAttach = .screen
}

enum StudioAnnotationStyle: String, StudioStringEnum {
    case red
    case blue
    case yellow
    case white
    case dark

    static let defaultValue: StudioAnnotationStyle = .red
}

enum StudioAnnotationSize: String, StudioStringEnum {
    case small
    case medium
    case large

    static let defaultValue: StudioAnnotationSize = .medium
}

/// The kinds of annotation this version draws. Any other is kept in the project and not drawn.
enum StudioAnnotationKind {
    static let arrow = "arrow"
    static let callout = "callout"
    static let text = "text"
}

/// An arrow, a callout, or a text box over the picture for a stretch of the recording.
struct StudioAnnotation: Codable, Equatable, Sendable {
    var id: String
    /// Text and not an enumeration, so that a kind from a later version stays what it is through
    /// a save and is not drawn as another.
    var kind: String
    var attach: StudioAnnotationAttach
    var start: Double
    var end: Double
    var x: Double
    var y: Double
    var offsetX: Double
    var offsetY: Double
    var text: String
    var style: StudioAnnotationStyle
    var size: StudioAnnotationSize
    var extra: [String: StudioJSONValue]

    init(
        id: String = "",
        kind: String = StudioAnnotationKind.arrow,
        attach: StudioAnnotationAttach = .screen,
        start: Double = 0,
        end: Double = 0,
        x: Double = 0.5,
        y: Double = 0.5,
        offsetX: Double = -0.12,
        offsetY: Double = 0.12,
        text: String = "",
        style: StudioAnnotationStyle = .red,
        size: StudioAnnotationSize = .medium,
        extra: [String: StudioJSONValue] = [:]
    ) {
        self.id = id
        self.kind = kind
        self.attach = attach
        self.start = start
        self.end = end
        self.x = x
        self.y = y
        self.offsetX = offsetX
        self.offsetY = offsetY
        self.text = text
        self.style = style
        self.size = size
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        id = try container.decodeString("id", default: "")
        kind = try container.decodeString("kind", default: StudioAnnotationKind.arrow)
        attach = try container.decodeEnum("attach", default: .screen)
        start = try container.decodeDouble("start", default: 0)
        end = try container.decodeDouble("end", default: 0)
        x = try container.decodeDouble("x", default: 0.5)
        y = try container.decodeDouble("y", default: 0.5)
        offsetX = try container.decodeDouble("offsetX", default: -0.12)
        offsetY = try container.decodeDouble("offsetY", default: 0.12)
        text = try container.decodeString("text", default: "")
        style = try container.decodeEnum("style", default: .red)
        size = try container.decodeEnum("size", default: .medium)
        extra = try StudioJSON.decodeExtra(from: container, excluding: [
            "id", "kind", "attach", "start", "end", "x", "y", "offsetX", "offsetY", "text", "style", "size",
        ])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(id, forKey: StudioJSONKey("id"))
        try container.encode(kind, forKey: StudioJSONKey("kind"))
        try container.encode(attach, forKey: StudioJSONKey("attach"))
        try container.encode(start, forKey: StudioJSONKey("start"))
        try container.encode(end, forKey: StudioJSONKey("end"))
        try container.encode(x, forKey: StudioJSONKey("x"))
        try container.encode(y, forKey: StudioJSONKey("y"))
        try container.encode(offsetX, forKey: StudioJSONKey("offsetX"))
        try container.encode(offsetY, forKey: StudioJSONKey("offsetY"))
        try container.encode(text, forKey: StudioJSONKey("text"))
        try container.encode(style, forKey: StudioJSONKey("style"))
        try container.encode(size, forKey: StudioJSONKey("size"))
    }
}

// MARK: - Geometry

struct StudioAnnotationPoint: Codable, Equatable, Sendable {
    var x: Double
    var y: Double
}

/// One annotation at one moment, in canvas pixels, before any text is measured (section 6.10 of
/// the project format). The arrow's values are 0 for a box, and the box's are 0 for an arrow.
struct StudioResolvedAnnotation: Equatable, Sendable {
    /// Its place in the project's list, which is also its place from the bottom.
    var index: Int
    var kind: String
    var opacity: Double
    var fill: String
    var textColor: String
    /// What an arrow or a callout points at, or the center of a text box.
    var anchor: StudioAnnotationPoint
    var stroke: Double = 0
    var tail = StudioAnnotationPoint(x: 0, y: 0)
    var headBase = StudioAnnotationPoint(x: 0, y: 0)
    var halfWidth: Double = 0
    /// False when the head is as long as the arrow, which is then its head alone.
    var hasShaft = false
    /// Where the box of a callout or a text is centered before it is pushed inside the canvas.
    var boxCenter = StudioAnnotationPoint(x: 0, y: 0)
    var font: Double = 0
    var maxTextWidth: Double = 0
    var radius: Double = 0
    var text: String = ""

    var isArrow: Bool { kind == StudioAnnotationKind.arrow }
}

/// Where a box goes once its text has been measured.
struct StudioAnnotationBox: Equatable, Sendable {
    var box: StudioRect
    var textRect: StudioRect
    /// The two points of the pointer's base, when it has one.
    var pointer: (a: StudioAnnotationPoint, b: StudioAnnotationPoint)?

    static func == (lhs: StudioAnnotationBox, rhs: StudioAnnotationBox) -> Bool {
        lhs.box == rhs.box && lhs.textRect == rhs.textRect
            && lhs.pointer?.a == rhs.pointer?.a && lhs.pointer?.b == rhs.pointer?.b
    }
}

/// The rules of section 6.10 of the project format, which the fixtures in
/// `shared/studio/fixtures/annotations` hold both platforms to. The same on Windows
/// (`StudioAnnotations.cs`).
enum StudioAnnotationGeometry {
    private static let headLengthInStrokes = 3.2
    private static let headHalfWidthInLengths = 0.62
    private static let padXInFonts = 0.7
    private static let padYInFonts = 0.45
    private static let radiusInFonts = 0.4
    private static let pointerHalfBaseInFonts = 0.45
    private static let maxBoxWidthInCanvases = 0.7
    private static let bitmapMargin = 2.0

    /// The whole pixels of the canvas a bitmap of an annotation with these bounds has to cover,
    /// with room for its antialiased edges, or nil when none of it falls on the canvas. Only what
    /// is on the canvas: a box of long text can be far larger than the canvas, and it has to show
    /// the part that fits whatever the size of the canvas it is drawn on.
    static func bitmapBounds(
        minX: Double,
        minY: Double,
        maxX: Double,
        maxY: Double,
        canvasWidth: Double,
        canvasHeight: Double
    ) -> StudioRect? {
        let left = max(0, (minX - bitmapMargin).rounded(.down))
        let top = max(0, (minY - bitmapMargin).rounded(.down))
        let right = min(canvasWidth, (maxX + bitmapMargin).rounded(.up))
        let bottom = min(canvasHeight, (maxY + bitmapMargin).rounded(.up))
        guard right - left >= 1, bottom - top >= 1 else { return nil }
        return StudioRect(x: left, y: top, width: (right - left).rounded(.up), height: (bottom - top).rounded(.up))
    }

    /// The numbers of the middle of what is in view: of the part of the recording a frame shows, or
    /// of the canvas. A frame with no screen has the middle of the recording.
    static func centerInView(attach: StudioAnnotationAttach, frame: StudioResolvedFrame) -> StudioAnnotationPoint {
        guard attach == .screen, let source = frame.screen?.source else { return StudioAnnotationPoint(x: 0.5, y: 0.5) }
        return StudioAnnotationPoint(x: source.x + source.width / 2, y: source.y + source.height / 2)
    }

    /// The numbers that keep an annotation where it is in the picture once it is fixed to `attach`.
    /// Its own numbers where it is not in the picture to begin with, or the frame has no screen to
    /// fix it to. A point beside the screen goes to the screen's edge.
    static func pointWhenAttached(
        _ annotation: StudioAnnotation,
        to attach: StudioAnnotationAttach,
        frame: StudioResolvedFrame,
        canvasWidth: Double,
        canvasHeight: Double
    ) -> StudioAnnotationPoint {
        let own = StudioAnnotationPoint(x: annotation.x, y: annotation.y)
        guard attach != annotation.attach,
              let (anchor, _) = anchorAndOpacity(annotation, frame: frame, canvasWidth: canvasWidth, canvasHeight: canvasHeight) else {
            return own
        }
        if attach == .frame {
            return StudioAnnotationPoint(x: anchor.x / canvasWidth, y: anchor.y / canvasHeight)
        }
        guard let screen = frame.screen, screen.rect.width > 0, screen.rect.height > 0 else { return own }
        let acrossX = StudioCanvasMath.clamped((anchor.x - screen.rect.x) / screen.rect.width, 0, 1)
        let acrossY = StudioCanvasMath.clamped((anchor.y - screen.rect.y) / screen.rect.height, 0, 1)
        return StudioAnnotationPoint(x: screen.source.x + acrossX * screen.source.width, y: screen.source.y + acrossY * screen.source.height)
    }

    /// What the project's annotations draw at `time`, bottom first.
    static func resolveAll(
        project: StudioProject,
        frame: StudioResolvedFrame,
        time: Double,
        canvasWidth: Double,
        canvasHeight: Double
    ) -> [StudioResolvedAnnotation] {
        project.overlays.annotations.enumerated().compactMap { index, annotation in
            resolve(annotation, index: index, frame: frame, time: time, canvasWidth: canvasWidth, canvasHeight: canvasHeight)
        }
    }

    /// What `annotation` draws at `time`, or nil when it draws nothing then.
    static func resolve(
        _ annotation: StudioAnnotation,
        index: Int,
        frame: StudioResolvedFrame,
        time: Double,
        canvasWidth: Double,
        canvasHeight: Double
    ) -> StudioResolvedAnnotation? {
        let kind = annotation.kind
        let isArrow = kind == StudioAnnotationKind.arrow
        guard isArrow || kind == StudioAnnotationKind.callout || kind == StudioAnnotationKind.text else { return nil }
        guard annotation.start <= time, time < annotation.end else { return nil }
        if !isArrow, isBlank(annotation.text) { return nil }
        guard let (anchor, opacity) = anchorAndOpacity(annotation, frame: frame, canvasWidth: canvasWidth, canvasHeight: canvasHeight),
              opacity > 0 else {
            return nil
        }

        let shortSide = min(canvasWidth, canvasHeight)
        let (fill, textColor) = colors(annotation.style)
        let (fontShare, strokeShare) = sizes(annotation.size)
        let font = shortSide * fontShare
        let offsetX = StudioCanvasMath.clamped(annotation.offsetX, -1, 1) * shortSide
        let offsetY = StudioCanvasMath.clamped(annotation.offsetY, -1, 1) * shortSide
        var resolved = StudioResolvedAnnotation(index: index, kind: kind, opacity: opacity, fill: fill, textColor: textColor, anchor: anchor)

        if isArrow {
            let length = (offsetX * offsetX + offsetY * offsetY).squareRoot()
            guard length > 0 else { return nil }
            let stroke = shortSide * strokeShare
            let head = min(stroke * headLengthInStrokes, length)
            resolved.stroke = stroke
            resolved.tail = StudioAnnotationPoint(x: anchor.x + offsetX, y: anchor.y + offsetY)
            resolved.headBase = StudioAnnotationPoint(x: anchor.x + offsetX / length * head, y: anchor.y + offsetY / length * head)
            resolved.halfWidth = head * headHalfWidthInLengths
            resolved.hasShaft = head < length
            return resolved
        }

        resolved.boxCenter = kind == StudioAnnotationKind.callout
            ? StudioAnnotationPoint(x: anchor.x + offsetX, y: anchor.y + offsetY)
            : anchor
        resolved.font = font
        resolved.maxTextWidth = max(font, canvasWidth * maxBoxWidthInCanvases - 2 * font * padXInFonts)
        resolved.radius = font * radiusInFonts
        resolved.text = annotation.text
        return resolved
    }

    /// The box of a callout or a text whose text measures `textWidth` by `textHeight`.
    static func layoutBox(
        _ resolved: StudioResolvedAnnotation,
        textWidth: Double,
        textHeight: Double,
        canvasWidth: Double,
        canvasHeight: Double
    ) -> StudioAnnotationBox {
        let padX = resolved.font * padXInFonts
        let padY = resolved.font * padYInFonts
        let width = textWidth + 2 * padX
        let height = textHeight + 2 * padY

        // The lower bound last: a box larger than the canvas keeps its top and left in view.
        let x = max(0, min(resolved.boxCenter.x - width / 2, canvasWidth - width))
        let y = max(0, min(resolved.boxCenter.y - height / 2, canvasHeight - height))
        let box = StudioRect(x: x, y: y, width: width, height: height)
        let textRect = StudioRect(x: x + padX, y: y + padY, width: textWidth, height: textHeight)
        let anchor = resolved.anchor
        let isInside = anchor.x >= x && anchor.x <= x + width && anchor.y >= y && anchor.y <= y + height
        guard resolved.kind == StudioAnnotationKind.callout, !isInside else {
            return StudioAnnotationBox(box: box, textRect: textRect, pointer: nil)
        }

        let dx = max(x - anchor.x, anchor.x - (x + width), 0)
        let dy = max(y - anchor.y, anchor.y - (y + height), 0)
        let radius = resolved.radius
        let mostHalf = resolved.font * pointerHalfBaseInFonts
        if dy >= dx {
            let edge = anchor.y < y ? y : y + height
            let half = min(mostHalf, max(0, width / 2 - radius))
            let along = max(x + radius + half, min(anchor.x, x + width - radius - half))
            return StudioAnnotationBox(box: box, textRect: textRect, pointer: (
                StudioAnnotationPoint(x: along - half, y: edge),
                StudioAnnotationPoint(x: along + half, y: edge)
            ))
        }
        let edge = anchor.x < x ? x : x + width
        let half = min(mostHalf, max(0, height / 2 - radius))
        let along = max(y + radius + half, min(anchor.y, y + height - radius - half))
        return StudioAnnotationBox(box: box, textRect: textRect, pointer: (
            StudioAnnotationPoint(x: edge, y: along - half),
            StudioAnnotationPoint(x: edge, y: along + half)
        ))
    }

    /// True when `text` has nothing to show, by the list in section 6.10. Not Foundation's
    /// white space, which is not the same as .NET's, and the same project has to draw the same
    /// boxes on both.
    static func isBlank(_ text: String) -> Bool {
        text.unicodeScalars.allSatisfy { scalar in
            switch scalar.value {
            case 0x09...0x0D, 0x20, 0x85, 0xA0, 0x1680, 0x2000...0x200B, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000:
                return true
            default:
                return false
            }
        }
    }

    private static func anchorAndOpacity(
        _ annotation: StudioAnnotation,
        frame: StudioResolvedFrame,
        canvasWidth: Double,
        canvasHeight: Double
    ) -> (StudioAnnotationPoint, Double)? {
        let x = StudioCanvasMath.clamped(annotation.x, 0, 1)
        let y = StudioCanvasMath.clamped(annotation.y, 0, 1)
        if annotation.attach == .frame {
            return (StudioAnnotationPoint(x: x * canvasWidth, y: y * canvasHeight), 1)
        }
        guard let screen = frame.screen else { return nil }
        let source = screen.source
        guard source.width > 0, source.height > 0,
              x >= source.x, x <= source.x + source.width,
              y >= source.y, y <= source.y + source.height else {
            return nil
        }
        return (
            StudioAnnotationPoint(
                x: screen.rect.x + (x - source.x) / source.width * screen.rect.width,
                y: screen.rect.y + (y - source.y) / source.height * screen.rect.height
            ),
            screen.opacity
        )
    }

    private static func colors(_ style: StudioAnnotationStyle) -> (fill: String, text: String) {
        switch style {
        case .red: return ("#FF453A", "#FFFFFF")
        case .blue: return ("#0A84FF", "#FFFFFF")
        case .yellow: return ("#FFD60A", "#1C1C1E")
        case .white: return ("#FFFFFF", "#1C1C1E")
        case .dark: return ("#1C1C1E", "#FFFFFF")
        }
    }

    private static func sizes(_ size: StudioAnnotationSize) -> (font: Double, stroke: Double) {
        switch size {
        case .small: return (0.028, 0.006)
        case .medium: return (0.036, 0.009)
        case .large: return (0.05, 0.013)
        }
    }
}

// MARK: - Editing

/// What an edit to an annotation did: whether the project changed, and where the annotation is in
/// the list afterwards. The index is nil when there is no such annotation, which is also the case
/// once it has been deleted.
struct StudioAnnotationEditResult: Equatable, Sendable {
    var changed: Bool
    var index: Int?
}

// Annotations are stored in source time, like zooms, and may overlap. The list is the order they
// are drawn in, bottom first, so an edit leaves an annotation at its place in the list and only
// changing its layer moves it there. The same on Windows (`StudioEditorModel.Annotations.cs`).
extension StudioEditorModel {
    /// The shortest annotation the editor makes, in seconds of the recording.
    static let minimumAnnotationDuration = 0.1

    /// How long an annotation lasts when it is added, in seconds, where there is room for it.
    static let newAnnotationDuration = 3.0

    /// What a callout or a text box says when it is added.
    static let newAnnotationText = "Your text"

    var canAddAnnotation: Bool { sourceDuration >= Self.minimumAnnotationDuration }

    private func hasAnnotation(_ index: Int) -> Bool {
        project.overlays.annotations.indices.contains(index)
    }

    /// Adds an annotation at `sourceTime`, on top of the others. Text is fixed to the frame, and an
    /// arrow and a callout to the screen, unless the frame has no screen then. It goes in the
    /// middle of what is in view.
    mutating func addAnnotation(kind: String, at sourceTime: Double, events: StudioEvents?) -> StudioAnnotationEditResult {
        guard canAddAnnotation, sourceTime.isFinite else { return StudioAnnotationEditResult(changed: false, index: nil) }
        let start = min(max(0, sourceTime), sourceDuration - Self.minimumAnnotationDuration)
        let canvas = StudioCanvasMath.naturalCanvas(project: project)
        let frame = StudioLayoutResolver.resolve(project: project, time: start, canvasWidth: canvas.width, canvasHeight: canvas.height, events: events)
        let attach: StudioAnnotationAttach = kind != StudioAnnotationKind.text && frame.screen != nil ? .screen : .frame
        let point = StudioAnnotationGeometry.centerInView(attach: attach, frame: frame)
        let style: StudioAnnotationStyle
        switch kind {
        case StudioAnnotationKind.callout: style = .dark
        case StudioAnnotationKind.text: style = .white
        default: style = .red
        }
        let annotation = StudioAnnotation(
            id: UUID().uuidString.lowercased(),
            kind: kind,
            attach: attach,
            start: start,
            end: min(start + Self.newAnnotationDuration, sourceDuration),
            x: point.x,
            y: point.y,
            text: kind == StudioAnnotationKind.arrow ? "" : Self.newAnnotationText,
            style: style
        )
        mutate { $0.overlays.annotations.append(annotation) }
        return StudioAnnotationEditResult(changed: true, index: project.overlays.annotations.count - 1)
    }

    mutating func removeAnnotation(at index: Int) -> StudioAnnotationEditResult {
        guard hasAnnotation(index) else { return StudioAnnotationEditResult(changed: false, index: nil) }
        mutate { $0.overlays.annotations.remove(at: index) }
        return StudioAnnotationEditResult(changed: true, index: nil)
    }

    /// One edit to one annotation.
    private mutating func editAnnotation(at index: Int, _ change: (inout StudioAnnotation) -> Void) -> StudioAnnotationEditResult {
        guard hasAnnotation(index) else { return StudioAnnotationEditResult(changed: false, index: nil) }
        let before = project.overlays.annotations[index]
        mutate { change(&$0.overlays.annotations[index]) }
        return StudioAnnotationEditResult(changed: project.overlays.annotations[index] != before, index: index)
    }

    private func annotationUnchanged(_ index: Int) -> StudioAnnotationEditResult {
        StudioAnnotationEditResult(changed: false, index: hasAnnotation(index) ? index : nil)
    }

    /// Moves when an annotation starts, up to the shortest it can be.
    mutating func setAnnotationStart(at index: Int, to sourceTime: Double) -> StudioAnnotationEditResult {
        guard sourceTime.isFinite else { return annotationUnchanged(index) }
        return editAnnotation(at: index) { $0.start = max(0, min(sourceTime, $0.end - Self.minimumAnnotationDuration)) }
    }

    /// Moves when an annotation ends, down to the shortest it can be.
    mutating func setAnnotationEnd(at index: Int, to sourceTime: Double) -> StudioAnnotationEditResult {
        guard sourceTime.isFinite else { return annotationUnchanged(index) }
        let duration = sourceDuration
        return editAnnotation(at: index) { $0.end = min(duration, max(sourceTime, $0.start + Self.minimumAnnotationDuration)) }
    }

    /// Moves a whole annotation so it starts at `sourceTime`, keeping its length.
    mutating func moveAnnotation(at index: Int, to sourceTime: Double) -> StudioAnnotationEditResult {
        guard sourceTime.isFinite else { return annotationUnchanged(index) }
        let duration = sourceDuration
        return editAnnotation(at: index) {
            let length = $0.end - $0.start
            $0.start = max(0, min(sourceTime, duration - length))
            $0.end = $0.start + length
        }
    }

    /// Sets what a callout or a text box says. Text typed with no other edit in between is one
    /// undo step, until `endAnnotationTyping()`.
    mutating func setAnnotationText(at index: Int, to text: String) -> StudioAnnotationEditResult {
        guard hasAnnotation(index) else { return StudioAnnotationEditResult(changed: false, index: nil) }
        guard project.overlays.annotations[index].text != text else { return StudioAnnotationEditResult(changed: false, index: index) }
        if typingInAnnotation == index {
            applyWithoutUndoStep { $0.overlays.annotations[index].text = text }
        } else {
            mutate { $0.overlays.annotations[index].text = text }
        }
        typingInAnnotation = index
        return StudioAnnotationEditResult(changed: true, index: index)
    }

    /// Typing has stopped: what is typed from now on is another undo step.
    mutating func endAnnotationTyping() {
        typingInAnnotation = nil
    }

    mutating func setAnnotationStyle(at index: Int, to style: StudioAnnotationStyle) -> StudioAnnotationEditResult {
        editAnnotation(at: index) { $0.style = style }
    }

    mutating func setAnnotationSize(at index: Int, to size: StudioAnnotationSize) -> StudioAnnotationEditResult {
        editAnnotation(at: index) { $0.size = size }
    }

    /// What an annotation is fixed to. It stays where it is in the picture at `sourceTime` where it
    /// can: a point out of view, or a screen the frame does not have, leaves its numbers.
    mutating func setAnnotationAttach(at index: Int, to attach: StudioAnnotationAttach, time sourceTime: Double, events: StudioEvents?) -> StudioAnnotationEditResult {
        let canvas = StudioCanvasMath.naturalCanvas(project: project)
        let frame = StudioLayoutResolver.resolve(project: project, time: sourceTime, canvasWidth: canvas.width, canvasHeight: canvas.height, events: events)
        return editAnnotation(at: index) {
            let point = StudioAnnotationGeometry.pointWhenAttached($0, to: attach, frame: frame, canvasWidth: canvas.width, canvasHeight: canvas.height)
            $0.attach = attach
            $0.x = point.x
            $0.y = point.y
        }
    }

    /// Moves what an annotation points at, or the center of a text box, in normalized numbers.
    mutating func setAnnotationPoint(at index: Int, x: Double, y: Double) -> StudioAnnotationEditResult {
        guard x.isFinite, y.isFinite else { return annotationUnchanged(index) }
        return editAnnotation(at: index) {
            $0.x = StudioCanvasMath.clamped(x, 0, 1)
            $0.y = StudioCanvasMath.clamped(y, 0, 1)
        }
    }

    /// Moves an arrow's tail or a callout's box, in short sides of the canvas from what it points at.
    mutating func setAnnotationOffset(at index: Int, x: Double, y: Double) -> StudioAnnotationEditResult {
        guard x.isFinite, y.isFinite else { return annotationUnchanged(index) }
        return editAnnotation(at: index) {
            $0.offsetX = StudioCanvasMath.clamped(x, -1, 1)
            $0.offsetY = StudioCanvasMath.clamped(y, -1, 1)
        }
    }

    /// Moves an annotation one place up (1) or down (-1) in the drawing order. Nothing past either end.
    mutating func moveAnnotationLayer(at index: Int, by places: Int) -> StudioAnnotationEditResult {
        guard hasAnnotation(index) else { return StudioAnnotationEditResult(changed: false, index: nil) }
        let target = index + places.signum()
        guard target != index, hasAnnotation(target) else { return StudioAnnotationEditResult(changed: false, index: index) }
        mutate { $0.overlays.annotations.swapAt(index, target) }
        return StudioAnnotationEditResult(changed: true, index: target)
    }

    private var annotationsInTimeOrder: [Int] {
        let annotations = project.overlays.annotations
        return annotations.indices.sorted { (annotations[$0].start, $0) < (annotations[$1].start, $1) }
    }

    /// The annotation after the selected one in time, or with none selected the one at `sourceTime`
    /// or the first one after it. Those that start together go by their place in the list.
    func annotationIndex(after selected: Int?, sourceTime: Double) -> Int? {
        let order = annotationsInTimeOrder
        if let selected, let at = order.firstIndex(of: selected) {
            return at + 1 < order.count ? order[at + 1] : nil
        }
        return order.first { project.overlays.annotations[$0].end > sourceTime }
    }

    /// The annotation before the selected one in time, or with none selected the one at `sourceTime`
    /// or the last one before it.
    func annotationIndex(before selected: Int?, sourceTime: Double) -> Int? {
        let order = annotationsInTimeOrder
        if let selected, let at = order.firstIndex(of: selected) {
            return at > 0 ? order[at - 1] : nil
        }
        return order.last { project.overlays.annotations[$0].start <= sourceTime }
    }

    /// Where the annotation at `index` of `before` is in `after`, for an edit that did not say, such
    /// as undo: the one with the same id, or for one without an id, which only an agent or a project
    /// file makes, the same place in a list of the same length. Nil when it is gone.
    static func annotationIndex(following index: Int, from before: [StudioAnnotation], to after: [StudioAnnotation]) -> Int? {
        guard before.indices.contains(index) else { return nil }
        let id = before[index].id
        if id.isEmpty {
            return before.count == after.count ? index : nil
        }
        return after.firstIndex { $0.id == id }
    }
}

// MARK: - Words

extension StudioEditorModel {
    static let annotationEmptyHint = "Press A, C, or T, or use the tools at the top of the window, to add an arrow, a callout, or text at the playhead."
    static let selectAnnotationHint = "Select an annotation on the timeline or in the list, or step to one with the arrows above."
    static let annotationNotDrawnNote = "This annotation comes from a newer version of Tiny Clips, so it is not drawn here. You can still change when it shows, or delete it."
    static let annotationDeletedMessage = "Annotation deleted."
    static let noRoomForAnnotationMessage = "There is no room for an annotation here."
    private static let annotationLabelLength = 24

    /// What a kind of annotation is called. Another kind is an "Annotation".
    static func annotationKindName(_ kind: String) -> String {
        switch kind {
        case StudioAnnotationKind.arrow: return "Arrow"
        case StudioAnnotationKind.callout: return "Callout"
        case StudioAnnotationKind.text: return "Text"
        default: return "Annotation"
        }
    }

    static func annotationAddedMessage(_ kind: String) -> String {
        "\(annotationKindName(kind)) added."
    }

    /// An annotation in a list: "Arrow", or "Callout: Billing lives here", with long text shortened
    /// and on one line.
    static func annotationLabel(_ annotation: StudioAnnotation) -> String {
        let name = annotationKindName(annotation.kind)
        let words = annotation.text.split(whereSeparator: \.isWhitespace).joined(separator: " ")
        guard annotation.kind != StudioAnnotationKind.arrow, !words.isEmpty else { return name }
        guard words.count > annotationLabelLength else { return "\(name): \(words)" }
        let shortened = String(words.prefix(annotationLabelLength))
        let trimmed = shortened.replacingOccurrences(of: "\\s+$", with: "", options: .regularExpression)
        return "\(name): \(trimmed)\u{2026}"
    }

    /// When an annotation starts and ends: "12.0 to 16.5 seconds".
    static func annotationRangeText(_ annotation: StudioAnnotation) -> String {
        let start = annotation.start.isFinite ? annotation.start : 0
        let end = annotation.end.isFinite ? annotation.end : 0
        return String(format: "%.1f to %.1f seconds", start, end)
    }

    /// An annotation for VoiceOver: "Callout: Billing lives here, 12.0 to 16.5 seconds".
    static func annotationDescription(_ annotation: StudioAnnotation) -> String {
        "\(annotationLabel(annotation)), \(annotationRangeText(annotation))"
    }

    static func annotationPositionText(index: Int, count: Int) -> String {
        "Annotation \(index + 1) of \(count)"
    }

    /// What is read out when an annotation is stepped to with Previous or Next.
    static func annotationStepText(index: Int, count: Int, annotation: StudioAnnotation) -> String {
        "\(annotationPositionText(index: index, count: count)), \(annotationDescription(annotation))"
    }
}
