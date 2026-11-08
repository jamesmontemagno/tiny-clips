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

/// How a redaction hides what is under it.
enum StudioRedactStyle: String, StudioStringEnum {
    case blur
    case pixelate
    case solid

    static let defaultValue: StudioRedactStyle = .blur
}

/// How strongly a blur or a pixelate redaction hides.
enum StudioRedactLevel: String, StudioStringEnum {
    case light
    case medium
    case heavy

    static let defaultValue: StudioRedactLevel = .medium
}

/// The kinds of annotation this version draws. Any other is kept in the project and not drawn.
enum StudioAnnotationKind {
    static let arrow = "arrow"
    static let callout = "callout"
    static let text = "text"
    static let rectangle = "rectangle"
    static let ellipse = "ellipse"
    static let redact = "redact"
    static let emoji = "emoji"

    static let known: [String] = [arrow, callout, text, rectangle, ellipse, redact, emoji]

    static func isKnown(_ kind: String) -> Bool { known.contains(kind) }

    /// A rectangle, an ellipse, or a redaction: a kind that covers an area instead of pointing at a place.
    static func isArea(_ kind: String) -> Bool { kind == rectangle || kind == ellipse || kind == redact }

    /// A rectangle or an ellipse: an area drawn as an outline.
    static func isShape(_ kind: String) -> Bool { kind == rectangle || kind == ellipse }
}

/// An arrow, a callout, a text box, an emoji, or an area (a rectangle, an ellipse, or a redaction)
/// over the picture for a stretch of the recording.
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
    /// `#RRGGBB` in place of the style's colors. Anything else leaves the style's.
    var fill: String
    var textColor: String
    /// What `size` is multiplied by.
    var scale: Double
    var rotation: Double
    /// The size of an area, normalized like `x` and `y`.
    var width: Double
    var height: Double
    /// A rectangle's corner radius, in canvas short sides.
    var cornerRadius: Double
    /// `#RRGGBB` fills the inside of a rectangle or an ellipse. Anything else leaves it empty.
    var shapeFill: String
    var shapeFillOpacity: Double
    var redactStyle: StudioRedactStyle
    var redactLevel: StudioRedactLevel
    /// A soft drop shadow under an emoji.
    var shadow: Bool
    var extra: [String: StudioJSONValue]

    static let defaultWidth = 0.2
    static let defaultHeight = 0.15
    static let defaultShapeFillOpacity = 0.35

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
        fill: String = "",
        textColor: String = "",
        scale: Double = 1,
        rotation: Double = 0,
        width: Double = StudioAnnotation.defaultWidth,
        height: Double = StudioAnnotation.defaultHeight,
        cornerRadius: Double = 0,
        shapeFill: String = "",
        shapeFillOpacity: Double = StudioAnnotation.defaultShapeFillOpacity,
        redactStyle: StudioRedactStyle = .blur,
        redactLevel: StudioRedactLevel = .medium,
        shadow: Bool = false,
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
        self.fill = fill
        self.textColor = textColor
        self.scale = scale
        self.rotation = rotation
        self.width = width
        self.height = height
        self.cornerRadius = cornerRadius
        self.shapeFill = shapeFill
        self.shapeFillOpacity = shapeFillOpacity
        self.redactStyle = redactStyle
        self.redactLevel = redactLevel
        self.shadow = shadow
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
        fill = try container.decodeString("fill", default: "")
        textColor = try container.decodeString("textColor", default: "")
        scale = try container.decodeDouble("scale", default: 1)
        rotation = try container.decodeDouble("rotation", default: 0)
        width = try container.decodeDouble("width", default: Self.defaultWidth)
        height = try container.decodeDouble("height", default: Self.defaultHeight)
        cornerRadius = try container.decodeDouble("cornerRadius", default: 0)
        shapeFill = try container.decodeString("shapeFill", default: "")
        shapeFillOpacity = try container.decodeDouble("shapeFillOpacity", default: Self.defaultShapeFillOpacity)
        redactStyle = try container.decodeEnum("redactStyle", default: .blur)
        redactLevel = try container.decodeEnum("redactLevel", default: .medium)
        // Anything but JSON true reads as false, a string such as "yes" included.
        let shadowValue = try? container.decodeIfPresent(Bool.self, forKey: StudioJSONKey("shadow"))
        shadow = shadowValue == true
        extra = try StudioJSON.decodeExtra(from: container, excluding: [
            "id", "kind", "attach", "start", "end", "x", "y", "offsetX", "offsetY", "text", "style", "size", "fill", "textColor", "scale",
            "width", "height", "cornerRadius", "shapeFill", "shapeFillOpacity", "redactStyle", "redactLevel", "shadow", "rotation",
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
        try container.encode(fill, forKey: StudioJSONKey("fill"))
        try container.encode(textColor, forKey: StudioJSONKey("textColor"))
        try container.encode(scale, forKey: StudioJSONKey("scale"))
        try container.encode(rotation, forKey: StudioJSONKey("rotation"))
        try container.encode(width, forKey: StudioJSONKey("width"))
        try container.encode(height, forKey: StudioJSONKey("height"))
        try container.encode(cornerRadius, forKey: StudioJSONKey("cornerRadius"))
        try container.encode(shapeFill, forKey: StudioJSONKey("shapeFill"))
        try container.encode(shapeFillOpacity, forKey: StudioJSONKey("shapeFillOpacity"))
        try container.encode(redactStyle, forKey: StudioJSONKey("redactStyle"))
        try container.encode(redactLevel, forKey: StudioJSONKey("redactLevel"))
        try container.encode(shadow, forKey: StudioJSONKey("shadow"))
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
    /// Where a rectangle, an ellipse, or a redaction is on the canvas.
    var rect = StudioRect()
    /// The inside fill of a rectangle or an ellipse, as `#RRGGBB`, or "" for none.
    var shapeFill: String = ""
    var shapeFillOpacity: Double = 0
    var redactStyle: StudioRedactStyle = .blur
    /// The blur's standard deviation or the pixel cell's side, in canvas pixels. 0 for a solid one.
    var redactAmount: Double = 0
    /// Whether an emoji has a drop shadow.
    var shadow = false
    var rotation: Double = 0

    var isArrow: Bool { kind == StudioAnnotationKind.arrow }
    var isArea: Bool { StudioAnnotationKind.isArea(kind) }
}

/// What a pointer can take hold of on an annotation in the preview.
enum StudioAnnotationPart: Equatable, Sendable {
    /// What an arrow or a callout points at.
    case tip
    /// The far end of an arrow.
    case tail
    /// A whole arrow, by its shaft.
    case body
    /// The box of a callout or a text.
    case box
    /// A whole rectangle, ellipse, or redaction.
    case area
    /// A corner of an area, which resizes it with the opposite corner held still.
    case topLeft
    case topRight
    case bottomLeft
    case bottomRight
    case rotate

    var isCorner: Bool { self == .topLeft || self == .topRight || self == .bottomLeft || self == .bottomRight }
}

/// Where a drag leaves an annotation, in the numbers of the project, and whether it has settled on
/// a center line of the canvas.
struct StudioAnnotationMove: Equatable, Sendable {
    var x: Double
    var y: Double
    var offsetX: Double
    var offsetY: Double
    var isOnVerticalCenter: Bool
    var isOnHorizontalCenter: Bool
    /// The size of an area, which a corner changes. Nil for the other kinds.
    var width: Double? = nil
    var height: Double? = nil
    var scale: Double? = nil
    var rotation: Double? = nil
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
    static func normalizeRotation(_ degrees: Double) -> Double {
        guard degrees.isFinite else { return 0 }
        var value = degrees.truncatingRemainder(dividingBy: 360)
        if value <= -180 { value += 360 }
        if value > 180 { value -= 360 }
        return value
    }

    static func rotate(_ point: StudioAnnotationPoint, around center: StudioAnnotationPoint, degrees: Double) -> StudioAnnotationPoint {
        let radians = normalizeRotation(degrees) * .pi / 180
        let x = point.x - center.x
        let y = point.y - center.y
        return StudioAnnotationPoint(x: center.x + x * cos(radians) - y * sin(radians),
                                     y: center.y + x * sin(radians) + y * cos(radians))
    }

    static func transform(_ start: StudioAnnotation, part: StudioAnnotationPart, anchor: StudioAnnotationPoint,
                          pressed: StudioAnnotationPoint, current: StudioAnnotationPoint) -> StudioAnnotationMove? {
        let x = pressed.x - anchor.x
        let y = pressed.y - anchor.y
        let dx = current.x - anchor.x
        let dy = current.y - anchor.y
        let distanceSquared = x * x + y * y
        guard distanceSquared.isFinite, distanceSquared >= 1, dx.isFinite, dy.isFinite else { return nil }
        var move = StudioAnnotationMove(x: start.x, y: start.y, offsetX: start.offsetX, offsetY: start.offsetY,
                                        isOnVerticalCenter: false, isOnHorizontalCenter: false)
        if part == .rotate {
            move.rotation = normalizeRotation(start.rotation + (atan2(dy, dx) - atan2(y, x)) * 180 / .pi)
        } else {
            let scale = StudioCanvasMath.clamped(start.scale * max(0.01, (x * dx + y * dy) / distanceSquared), minimumScale, maximumScale)
            let factor = scale / StudioCanvasMath.clamped(start.scale, minimumScale, maximumScale)
            move.scale = scale
            if start.kind == StudioAnnotationKind.arrow || start.kind == StudioAnnotationKind.callout {
                move.offsetX = StudioCanvasMath.clamped(start.offsetX * factor, -1, 1)
                move.offsetY = StudioCanvasMath.clamped(start.offsetY * factor, -1, 1)
            }
        }
        return move
    }

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

    /// The numbers of `point` on the canvas: in the canvas, or in the recording where the frame
    /// shows it. A point beside the screen goes to the screen's edge. Nil with no screen to fix it to.
    private static func point(
        at point: StudioAnnotationPoint,
        attach: StudioAnnotationAttach,
        frame: StudioResolvedFrame,
        canvasWidth: Double,
        canvasHeight: Double
    ) -> StudioAnnotationPoint? {
        if attach == .frame {
            return StudioAnnotationPoint(
                x: StudioCanvasMath.clamped(point.x / canvasWidth, 0, 1),
                y: StudioCanvasMath.clamped(point.y / canvasHeight, 0, 1)
            )
        }
        guard let screen = frame.screen, screen.rect.width > 0, screen.rect.height > 0 else { return nil }
        let acrossX = StudioCanvasMath.clamped((point.x - screen.rect.x) / screen.rect.width, 0, 1)
        let acrossY = StudioCanvasMath.clamped((point.y - screen.rect.y) / screen.rect.height, 0, 1)
        return StudioAnnotationPoint(x: screen.source.x + acrossX * screen.source.width, y: screen.source.y + acrossY * screen.source.height)
    }

    /// The numbers `annotation` has once `part` of it is dragged by `byX`, `byY` canvas pixels from
    /// where it was at the start of the drag, with where its box was drawn then, if it has one. What is not dragged stays where it is in the picture:
    /// the tail of an arrow whose tip moves, the box of a callout whose point moves. Whatever is
    /// dragged settles on the middle of the canvas when it comes close. Nil when it isn't in the
    /// picture, or when it would make an arrow too short to see or to take hold of again.
    static func moved(
        _ annotation: StudioAnnotation,
        part: StudioAnnotationPart,
        byX: Double,
        byY: Double,
        drawnBoxCenter: StudioAnnotationPoint? = nil,
        frame: StudioResolvedFrame,
        canvasWidth: Double,
        canvasHeight: Double
    ) -> StudioAnnotationMove? {
        if StudioAnnotationKind.isArea(annotation.kind) {
            return movedArea(annotation, part: part, byX: byX, byY: byY, frame: frame, canvasWidth: canvasWidth, canvasHeight: canvasHeight)
        }
        guard byX.isFinite, byY.isFinite, canvasWidth > 0, canvasHeight > 0,
              let (anchor, _) = anchorAndOpacity(annotation, frame: frame, canvasWidth: canvasWidth, canvasHeight: canvasHeight) else {
            return nil
        }
        let shortSide = min(canvasWidth, canvasHeight)
        let offsetX = StudioCanvasMath.clamped(annotation.offsetX, -1, 1) * shortSide
        let offsetY = StudioCanvasMath.clamped(annotation.offsetY, -1, 1) * shortSide
        let far = StudioAnnotationPoint(x: anchor.x + offsetX, y: anchor.y + offsetY)
        let isFarEnd = part == .tail || (part == .box && annotation.kind == StudioAnnotationKind.callout)
        // A box pushed inside the canvas is not where its numbers say, and it is what was taken hold of.
        let from = part == .box ? (drawnBoxCenter ?? (isFarEnd ? far : anchor)) : (isFarEnd ? far : anchor)

        var x = from.x + byX
        var y = from.y + byY
        let reach = shortSide * snapShareOfShortSide
        let isOnVerticalCenter = abs(x - canvasWidth / 2) <= reach
        let isOnHorizontalCenter = abs(y - canvasHeight / 2) <= reach
        if isOnVerticalCenter { x = canvasWidth / 2 }
        if isOnHorizontalCenter { y = canvasHeight / 2 }
        let target = StudioAnnotationPoint(x: x, y: y)

        var move = StudioAnnotationMove(
            x: StudioCanvasMath.clamped(annotation.x, 0, 1),
            y: StudioCanvasMath.clamped(annotation.y, 0, 1),
            offsetX: StudioCanvasMath.clamped(annotation.offsetX, -1, 1),
            offsetY: StudioCanvasMath.clamped(annotation.offsetY, -1, 1),
            isOnVerticalCenter: isOnVerticalCenter,
            isOnHorizontalCenter: isOnHorizontalCenter
        )
        if isFarEnd {
            move.offsetX = StudioCanvasMath.clamped((target.x - anchor.x) / shortSide, -1, 1)
            move.offsetY = StudioCanvasMath.clamped((target.y - anchor.y) / shortSide, -1, 1)
        } else {
            guard let moved = point(at: target, attach: annotation.attach, frame: frame, canvasWidth: canvasWidth, canvasHeight: canvasHeight) else { return nil }
            move.x = moved.x
            move.y = moved.y
            if part == .tip {
                var placed = annotation
                placed.x = moved.x
                placed.y = moved.y
                guard let (newAnchor, _) = anchorAndOpacity(placed, frame: frame, canvasWidth: canvasWidth, canvasHeight: canvasHeight) else { return nil }
                move.offsetX = StudioCanvasMath.clamped((far.x - newAnchor.x) / shortSide, -1, 1)
                move.offsetY = StudioCanvasMath.clamped((far.y - newAnchor.y) / shortSide, -1, 1)
            }
        }
        if annotation.kind == StudioAnnotationKind.arrow, part != .body,
           (move.offsetX * move.offsetX + move.offsetY * move.offsetY).squareRoot() < shortestArrowInShortSides {
            return nil
        }
        return move
    }

    /// How near the middle of the canvas something dragged has to come to settle on it.
    private static let snapShareOfShortSide = 0.008

    /// The shortest arrow a drag leaves behind, in short sides of the canvas.
    private static let shortestArrowInShortSides = 0.02

    /// The smallest side a corner drag leaves an area with, in short sides of the canvas, so that
    /// it can be taken hold of again.
    private static let smallestAreaInShortSides = 0.02

    /// The numbers of a canvas point, in the canvas or in the recording where the frame shows it,
    /// without moving a point beside the screen to its edge. Nil with no screen to fix it to.
    private static func unclampedPoint(
        at point: StudioAnnotationPoint,
        attach: StudioAnnotationAttach,
        frame: StudioResolvedFrame,
        canvasWidth: Double,
        canvasHeight: Double
    ) -> StudioAnnotationPoint? {
        if attach == .frame {
            return StudioAnnotationPoint(x: point.x / canvasWidth, y: point.y / canvasHeight)
        }
        guard let screen = frame.screen, screen.rect.width > 0, screen.rect.height > 0 else { return nil }
        return StudioAnnotationPoint(
            x: screen.source.x + (point.x - screen.rect.x) / screen.rect.width * screen.source.width,
            y: screen.source.y + (point.y - screen.rect.y) / screen.rect.height * screen.source.height
        )
    }

    /// A drag of a whole area, which keeps its size, or of one of its corners, which keeps the
    /// opposite corner where it is. What is dragged settles on the middle of the canvas when it
    /// comes close. Nil when the area is not in the picture.
    private static func movedArea(
        _ annotation: StudioAnnotation,
        part: StudioAnnotationPart,
        byX: Double,
        byY: Double,
        frame: StudioResolvedFrame,
        canvasWidth: Double,
        canvasHeight: Double
    ) -> StudioAnnotationMove? {
        guard byX.isFinite, byY.isFinite, canvasWidth > 0, canvasHeight > 0,
              let (rect, _) = areaAndOpacity(annotation, frame: frame, canvasWidth: canvasWidth, canvasHeight: canvasHeight) else {
            return nil
        }
        let shortSide = min(canvasWidth, canvasHeight)
        let reach = shortSide * snapShareOfShortSide
        let centerX = StudioCanvasMath.clamped(annotation.x, 0, 1)
        let centerY = StudioCanvasMath.clamped(annotation.y, 0, 1)
        let sizeX = StudioCanvasMath.clamped(annotation.width, 0, 1)
        let sizeY = StudioCanvasMath.clamped(annotation.height, 0, 1)
        var move = StudioAnnotationMove(
            x: centerX,
            y: centerY,
            offsetX: StudioCanvasMath.clamped(annotation.offsetX, -1, 1),
            offsetY: StudioCanvasMath.clamped(annotation.offsetY, -1, 1),
            isOnVerticalCenter: false,
            isOnHorizontalCenter: false,
            width: sizeX,
            height: sizeY
        )

        guard part.isCorner else {
            var x = rect.x + rect.width / 2 + byX
            var y = rect.y + rect.height / 2 + byY
            move.isOnVerticalCenter = abs(x - canvasWidth / 2) <= reach
            move.isOnHorizontalCenter = abs(y - canvasHeight / 2) <= reach
            if move.isOnVerticalCenter { x = canvasWidth / 2 }
            if move.isOnHorizontalCenter { y = canvasHeight / 2 }
            guard let center = unclampedPoint(
                at: StudioAnnotationPoint(x: x, y: y),
                attach: annotation.attach,
                frame: frame,
                canvasWidth: canvasWidth,
                canvasHeight: canvasHeight
            ) else {
                return nil
            }
            // A screen area keeps its middle on what the frame shows, so it stays in view.
            var lowX = 0.0, highX = 1.0, lowY = 0.0, highY = 1.0
            if annotation.attach == .screen, let source = frame.screen?.source {
                lowX = max(0, source.x)
                highX = min(1, source.x + source.width)
                lowY = max(0, source.y)
                highY = min(1, source.y + source.height)
            }
            move.x = StudioCanvasMath.clamped(center.x, lowX, max(lowX, highX))
            move.y = StudioCanvasMath.clamped(center.y, lowY, max(lowY, highY))
            return move
        }

        let delta = rotate(StudioAnnotationPoint(x: byX, y: byY), around: StudioAnnotationPoint(x: 0, y: 0), degrees: -annotation.rotation)
        let movesRight = part == .topRight || part == .bottomRight
        let movesDown = part == .bottomLeft || part == .bottomRight
        let fixedX = movesRight ? rect.x : rect.x + rect.width
        let fixedY = movesDown ? rect.y : rect.y + rect.height
        var x = (movesRight ? rect.x + rect.width : rect.x) + delta.x
        var y = (movesDown ? rect.y + rect.height : rect.y) + delta.y
        move.isOnVerticalCenter = normalizeRotation(annotation.rotation) == 0 && abs(x - canvasWidth / 2) <= reach
        move.isOnHorizontalCenter = normalizeRotation(annotation.rotation) == 0 && abs(y - canvasHeight / 2) <= reach
        if move.isOnVerticalCenter { x = canvasWidth / 2 }
        if move.isOnHorizontalCenter { y = canvasHeight / 2 }
        let least = shortSide * smallestAreaInShortSides
        let limitedX = movesRight ? max(x, fixedX + least) : min(x, fixedX - least)
        let limitedY = movesDown ? max(y, fixedY + least) : min(y, fixedY - least)
        if limitedX != x { move.isOnVerticalCenter = false }
        if limitedY != y { move.isOnHorizontalCenter = false }
        guard let corner = unclampedPoint(
            at: StudioAnnotationPoint(x: limitedX, y: limitedY),
            attach: annotation.attach,
            frame: frame,
            canvasWidth: canvasWidth,
            canvasHeight: canvasHeight
        ) else {
            return nil
        }

        // The corner that stays, in the project's numbers, so that it does not drift.
        let stayX = movesRight ? centerX - sizeX / 2 : centerX + sizeX / 2
        let stayY = movesDown ? centerY - sizeY / 2 : centerY + sizeY / 2
        let newWidth = min(1, abs(corner.x - stayX))
        let newHeight = min(1, abs(corner.y - stayY))
        guard newWidth > 0, newHeight > 0 else { return nil }
        let newCenterX = movesRight ? stayX + newWidth / 2 : stayX - newWidth / 2
        let newCenterY = movesDown ? stayY + newHeight / 2 : stayY - newHeight / 2
        move.x = StudioCanvasMath.clamped(newCenterX, 0, 1)
        move.y = StudioCanvasMath.clamped(newCenterY, 0, 1)
        move.width = newWidth
        move.height = newHeight
        if normalizeRotation(annotation.rotation) != 0 {
            let newAnchor = StudioAnnotationPoint(
                x: rect.x + rect.width / 2 + (move.x - centerX) / sizeX * rect.width,
                y: rect.y + rect.height / 2 + (move.y - centerY) / sizeY * rect.height)
            guard let center = unclampedPoint(
                    at: rotate(newAnchor, around: StudioAnnotationPoint(x: rect.x + rect.width / 2, y: rect.y + rect.height / 2), degrees: annotation.rotation),
                    attach: annotation.attach, frame: frame, canvasWidth: canvasWidth, canvasHeight: canvasHeight) else { return nil }
            move.x = StudioCanvasMath.clamped(center.x, 0, 1)
            move.y = StudioCanvasMath.clamped(center.y, 0, 1)
        }
        return move
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
        guard var resolved = resolveUnrotated(annotation, index: index, frame: frame, time: time, canvasWidth: canvasWidth, canvasHeight: canvasHeight) else { return nil }
        resolved.rotation = normalizeRotation(annotation.rotation)
        return resolved
    }

    private static func resolveUnrotated(
        _ annotation: StudioAnnotation, index: Int, frame: StudioResolvedFrame, time: Double,
        canvasWidth: Double, canvasHeight: Double
    ) -> StudioResolvedAnnotation? {
        let kind = annotation.kind
        let isArrow = kind == StudioAnnotationKind.arrow
        guard StudioAnnotationKind.isKnown(kind) else { return nil }
        guard annotation.start <= time, time < annotation.end else { return nil }
        if StudioAnnotationKind.isArea(kind) {
            return resolveArea(annotation, index: index, frame: frame, canvasWidth: canvasWidth, canvasHeight: canvasHeight)
        }
        if !isArrow, isBlank(annotation.text) { return nil }
        guard let (anchor, opacity) = anchorAndOpacity(annotation, frame: frame, canvasWidth: canvasWidth, canvasHeight: canvasHeight),
              opacity > 0 else {
            return nil
        }

        let shortSide = min(canvasWidth, canvasHeight)
        let styleColors = colors(annotation.style)
        let fill = customColor(annotation.fill) ?? styleColors.fill
        let textColor = customColor(annotation.textColor) ?? styleColors.text
        let (fontShare, strokeShare) = sizes(annotation.size)
        let scale = StudioCanvasMath.clamped(annotation.scale, minimumScale, maximumScale)
        let font = shortSide * fontShare * scale
        let offsetX = StudioCanvasMath.clamped(annotation.offsetX, -1, 1) * shortSide
        let offsetY = StudioCanvasMath.clamped(annotation.offsetY, -1, 1) * shortSide
        var resolved = StudioResolvedAnnotation(index: index, kind: kind, opacity: opacity, fill: fill, textColor: textColor, anchor: anchor)

        if kind == StudioAnnotationKind.emoji {
            resolved.boxCenter = anchor
            resolved.font = shortSide * emojiFontShare(annotation.size) * scale
            resolved.text = annotation.text
            resolved.shadow = annotation.shadow
            return resolved
        }

        if isArrow {
            let length = (offsetX * offsetX + offsetY * offsetY).squareRoot()
            guard length > 0 else { return nil }
            let stroke = shortSide * strokeShare * scale
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

    /// A rectangle, an ellipse, or a redaction that is showing, worked out on the canvas.
    private static func resolveArea(
        _ annotation: StudioAnnotation,
        index: Int,
        frame: StudioResolvedFrame,
        canvasWidth: Double,
        canvasHeight: Double
    ) -> StudioResolvedAnnotation? {
        guard let (rect, opacity) = areaAndOpacity(annotation, frame: frame, canvasWidth: canvasWidth, canvasHeight: canvasHeight),
              opacity > 0 else {
            return nil
        }
        let shortSide = min(canvasWidth, canvasHeight)
        let colors = effectiveColors(annotation)
        var resolved = StudioResolvedAnnotation(
            index: index,
            kind: annotation.kind,
            opacity: opacity,
            fill: colors.fill,
            textColor: colors.text,
            anchor: StudioAnnotationPoint(x: rect.x + rect.width / 2, y: rect.y + rect.height / 2)
        )
        resolved.rect = rect
        if annotation.kind == StudioAnnotationKind.redact {
            resolved.redactStyle = annotation.redactStyle
            resolved.redactAmount = redactAmount(style: annotation.redactStyle, level: annotation.redactLevel, shortSide: shortSide)
            return resolved
        }
        let scale = StudioCanvasMath.clamped(annotation.scale, minimumScale, maximumScale)
        resolved.stroke = shortSide * sizes(annotation.size).stroke * scale
        if annotation.kind == StudioAnnotationKind.rectangle {
            resolved.radius = min(StudioCanvasMath.clamped(annotation.cornerRadius, 0, maximumCornerRadius) * shortSide, rect.width / 2, rect.height / 2)
        }
        resolved.shapeFill = customColor(annotation.shapeFill) ?? ""
        resolved.shapeFillOpacity = StudioCanvasMath.clamped(annotation.shapeFillOpacity, 0, 1)
        return resolved
    }

    static let maximumCornerRadius = 0.5

    /// The blur's standard deviation or the pixel cell's side, in canvas pixels. 0 for a solid one.
    static func redactAmount(style: StudioRedactStyle, level: StudioRedactLevel, shortSide: Double) -> Double {
        switch (style, level) {
        case (.blur, .light): return shortSide * 0.006
        case (.blur, .medium): return shortSide * 0.012
        case (.blur, .heavy): return shortSide * 0.022
        case (.pixelate, .light): return shortSide * 0.008
        case (.pixelate, .medium): return shortSide * 0.016
        case (.pixelate, .heavy): return shortSide * 0.028
        case (.solid, _): return 0
        }
    }

    /// Where an area is on the canvas and how opaque, or nil when it draws nothing: an empty one,
    /// or a screen one with none of it in view.
    private static func areaAndOpacity(
        _ annotation: StudioAnnotation,
        frame: StudioResolvedFrame,
        canvasWidth: Double,
        canvasHeight: Double
    ) -> (StudioRect, Double)? {
        let centerX = StudioCanvasMath.clamped(annotation.x, 0, 1)
        let centerY = StudioCanvasMath.clamped(annotation.y, 0, 1)
        let sizeX = StudioCanvasMath.clamped(annotation.width, 0, 1)
        let sizeY = StudioCanvasMath.clamped(annotation.height, 0, 1)
        guard sizeX > 0, sizeY > 0 else { return nil }
        let lowX = centerX - sizeX / 2
        let lowY = centerY - sizeY / 2
        let highX = centerX + sizeX / 2
        let highY = centerY + sizeY / 2
        if annotation.attach == .frame {
            return (StudioRect(x: lowX * canvasWidth, y: lowY * canvasHeight, width: sizeX * canvasWidth, height: sizeY * canvasHeight), 1)
        }
        guard let screen = frame.screen else { return nil }
        let source = screen.source
        let rotation = normalizeRotation(annotation.rotation)
        guard source.width > 0, source.height > 0 else {
            return nil
        }
        if rotation == 0 && (!(lowX < source.x + source.width && highX > source.x)
            || !(lowY < source.y + source.height && highY > source.y)) { return nil }
        let rect = StudioRect(
            x: screen.rect.x + (lowX - source.x) / source.width * screen.rect.width,
            y: screen.rect.y + (lowY - source.y) / source.height * screen.rect.height,
            width: sizeX / source.width * screen.rect.width,
            height: sizeY / source.height * screen.rect.height
        )
        if rotation != 0 {
            let center = StudioAnnotationPoint(x: rect.x + rect.width / 2, y: rect.y + rect.height / 2)
            let corners = [
                StudioAnnotationPoint(x: rect.x, y: rect.y), StudioAnnotationPoint(x: rect.maxX, y: rect.y),
                StudioAnnotationPoint(x: rect.maxX, y: rect.maxY), StudioAnnotationPoint(x: rect.x, y: rect.maxY),
            ].map { rotate($0, around: center, degrees: rotation) }
            if corners.map(\.x).max()! <= screen.rect.x || corners.map(\.x).min()! >= screen.rect.maxX
                || corners.map(\.y).max()! <= screen.rect.y || corners.map(\.y).min()! >= screen.rect.maxY { return nil }
        }
        return (rect, screen.opacity)
    }

    /// The center and size that keep an area where it is in the picture once it is fixed to
    /// `attach`. Nil where it is not in the picture to begin with, or the frame has no screen.
    static func areaWhenAttached(
        _ annotation: StudioAnnotation,
        to attach: StudioAnnotationAttach,
        frame: StudioResolvedFrame,
        canvasWidth: Double,
        canvasHeight: Double
    ) -> (x: Double, y: Double, width: Double, height: Double)? {
        guard attach != annotation.attach,
              let (rect, _) = areaAndOpacity(annotation, frame: frame, canvasWidth: canvasWidth, canvasHeight: canvasHeight),
              let low = unclampedPoint(at: StudioAnnotationPoint(x: rect.x, y: rect.y), attach: attach, frame: frame, canvasWidth: canvasWidth, canvasHeight: canvasHeight),
              let high = unclampedPoint(at: StudioAnnotationPoint(x: rect.maxX, y: rect.maxY), attach: attach, frame: frame, canvasWidth: canvasWidth, canvasHeight: canvasHeight) else {
            return nil
        }
        return (
            StudioCanvasMath.clamped((low.x + high.x) / 2, 0, 1),
            StudioCanvasMath.clamped((low.y + high.y) / 2, 0, 1),
            StudioCanvasMath.clamped(high.x - low.x, 0, 1),
            StudioCanvasMath.clamped(high.y - low.y, 0, 1)
        )
    }

    /// Where an emoji whose text measures `textWidth` by `textHeight` is drawn: centered on its
    /// anchor and pushed back inside the canvas, its top and left last.
    static func layoutEmoji(
        _ resolved: StudioResolvedAnnotation,
        textWidth: Double,
        textHeight: Double,
        canvasWidth: Double,
        canvasHeight: Double
    ) -> StudioRect {
        let x = max(0, min(resolved.anchor.x - textWidth / 2, canvasWidth - textWidth))
        let y = max(0, min(resolved.anchor.y - textHeight / 2, canvasHeight - textHeight))
        return StudioRect(x: x, y: y, width: textWidth, height: textHeight)
    }

    /// How far down an emoji's shadow is moved, in canvas pixels.
    static func emojiShadowOffset(font: Double) -> Double { font * 0.04 }

    /// The standard deviation of the blur of an emoji's shadow, in canvas pixels.
    static func emojiShadowSigma(font: Double) -> Double { font * 0.06 }

    static let emojiShadowOpacity = 0.45

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

    static let minimumScale = 0.5
    static let maximumScale = 3.0

    /// The colors `annotation` is drawn in: its own where it has them, and otherwise its style's.
    static func effectiveColors(_ annotation: StudioAnnotation) -> (fill: String, text: String) {
        let style = colors(annotation.style)
        return (customColor(annotation.fill) ?? style.fill, customColor(annotation.textColor) ?? style.text)
    }

    /// `text` in capitals when it is `#RRGGBB`. Nil for anything else, which leaves the style's color.
    static func customColor(_ text: String) -> String? {
        let characters = Array(text.utf8)
        guard characters.count == 7, characters[0] == UInt8(ascii: "#"),
              characters.dropFirst().allSatisfy({ $0 < 128 && Character(UnicodeScalar($0)).isHexDigit }) else {
            return nil
        }
        return text.uppercased()
    }

    /// The colors a style gives, which the pickers show until they are changed.
    static func colors(_ style: StudioAnnotationStyle) -> (fill: String, text: String) {
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

    /// An emoji's font, in canvas short sides, before its scale.
    private static func emojiFontShare(_ size: StudioAnnotationSize) -> Double {
        switch size {
        case .small: return 0.06
        case .medium: return 0.09
        case .large: return 0.13
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

    /// What an emoji shows when it is added.
    static let newEmoji = "\u{1F44D}"

    /// How far a copy of an annotation is moved from the original, right and down, normalized.
    static let duplicateNudge = 0.02

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
        let isArea = StudioAnnotationKind.isArea(kind)
        let style: StudioAnnotationStyle
        switch kind {
        case StudioAnnotationKind.callout, StudioAnnotationKind.redact: style = .dark
        case StudioAnnotationKind.text: style = .white
        default: style = .red
        }
        var annotation = StudioAnnotation(
            id: UUID().uuidString.lowercased(),
            kind: kind,
            attach: attach,
            start: start,
            end: min(start + Self.newAnnotationDuration, sourceDuration),
            x: point.x,
            y: point.y,
            text: kind == StudioAnnotationKind.arrow || isArea ? "" : kind == StudioAnnotationKind.emoji ? Self.newEmoji : Self.newAnnotationText,
            style: style
        )
        if isArea {
            // A share of what is in view, so it looks the same size however far in a zoom is.
            var viewWidth = 1.0, viewHeight = 1.0
            if attach == .screen, let source = frame.screen?.source {
                viewWidth = source.width
                viewHeight = source.height
            }
            annotation.width = StudioCanvasMath.clamped(StudioAnnotation.defaultWidth * viewWidth, Self.minimumAreaSize, 1)
            annotation.height = StudioCanvasMath.clamped(StudioAnnotation.defaultHeight * viewHeight, Self.minimumAreaSize, 1)
        }
        mutate { $0.overlays.annotations.append(annotation) }
        return StudioAnnotationEditResult(changed: true, index: project.overlays.annotations.count - 1)
    }

    mutating func removeAnnotation(at index: Int) -> StudioAnnotationEditResult {
        guard hasAnnotation(index) else { return StudioAnnotationEditResult(changed: false, index: nil) }
        mutate { $0.overlays.annotations.remove(at: index) }
        return StudioAnnotationEditResult(changed: true, index: nil)
    }

    /// Puts a copy of an annotation, with an id of its own and the same timing, directly above it,
    /// moved a little right and down so both can be seen. In the numbers of what it is fixed to: the
    /// recording for the screen, the canvas for the frame.
    mutating func duplicateAnnotation(at index: Int) -> StudioAnnotationEditResult {
        guard hasAnnotation(index) else { return StudioAnnotationEditResult(changed: false, index: nil) }
        var copy = project.overlays.annotations[index]
        copy.id = UUID().uuidString.lowercased()
        copy.x = StudioCanvasMath.clamped((copy.x.isFinite ? copy.x : 0) + Self.duplicateNudge, 0, 1)
        copy.y = StudioCanvasMath.clamped((copy.y.isFinite ? copy.y : 0) + Self.duplicateNudge, 0, 1)
        endAnnotationTyping()
        endAnnotationColorChoice()
        mutate { $0.overlays.annotations.insert(copy, at: index + 1) }
        return StudioAnnotationEditResult(changed: true, index: index + 1)
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

    /// A style is a set of colors, so choosing one takes back the colors chosen by hand.
    mutating func setAnnotationStyle(at index: Int, to style: StudioAnnotationStyle) -> StudioAnnotationEditResult {
        editAnnotation(at: index) {
            $0.style = style
            $0.fill = ""
            $0.textColor = ""
        }
    }

    /// The color of an arrow or a box, as `#RRGGBB`. A color picker hands over many on its way to
    /// the one that is meant, which are one undo step, as text being typed is.
    mutating func setAnnotationFill(at index: Int, to color: String) -> StudioAnnotationEditResult {
        setAnnotationColor(at: index, part: 0, to: color) { $0.fill = $1 }
    }

    /// The color of the text of a box, as `#RRGGBB`, in the same way.
    mutating func setAnnotationTextColor(at index: Int, to color: String) -> StudioAnnotationEditResult {
        setAnnotationColor(at: index, part: 1, to: color) { $0.textColor = $1 }
    }

    private mutating func setAnnotationColor(
        at index: Int,
        part: Int,
        to color: String,
        apply: (inout StudioAnnotation, String) -> Void
    ) -> StudioAnnotationEditResult {
        guard hasAnnotation(index), let color = StudioAnnotationGeometry.customColor(color) else { return annotationUnchanged(index) }
        let before = project.overlays.annotations[index]
        // The color it already has: a picker that says what it shows must not turn a style into a custom color.
        let effective = StudioAnnotationGeometry.effectiveColors(before)
        let current = part == 0 ? effective.fill : part == 1 ? effective.text : before.shapeFill.uppercased()
        guard color != current else { return annotationUnchanged(index) }
        var after = before
        apply(&after, color)
        let marker = index * 3 + part
        if choosingColorInAnnotation == marker {
            applyWithoutUndoStep { $0.overlays.annotations[index] = after }
        } else {
            mutate { $0.overlays.annotations[index] = after }
        }
        choosingColorInAnnotation = marker
        return StudioAnnotationEditResult(changed: true, index: index)
    }

    /// A color has been chosen: the next one is another undo step.
    mutating func endAnnotationColorChoice() {
        choosingColorInAnnotation = nil
    }

    /// What the size of an annotation is multiplied by.
    mutating func setAnnotationScale(at index: Int, to scale: Double) -> StudioAnnotationEditResult {
        guard scale.isFinite else { return annotationUnchanged(index) }
        return editAnnotation(at: index) {
            $0.scale = StudioCanvasMath.clamped(scale, StudioAnnotationGeometry.minimumScale, StudioAnnotationGeometry.maximumScale)
        }

        mutating func setAnnotationRotation(at index: Int, to rotation: Double) -> StudioAnnotationEditResult {
            guard rotation.isFinite else { return annotationUnchanged(index) }
            return editAnnotation(at: index) { $0.rotation = StudioAnnotationGeometry.normalizeRotation(rotation) }
        }
    }

    /// The smallest side the editor gives an area, normalized.
    static let minimumAreaSize = 0.01

    /// The width and height of a rectangle, an ellipse, or a redaction, normalized, about its center.
    mutating func setAnnotationSize(at index: Int, width: Double, height: Double) -> StudioAnnotationEditResult {
        guard width.isFinite, height.isFinite else { return annotationUnchanged(index) }
        return editAnnotation(at: index) {
            $0.width = StudioCanvasMath.clamped(width, Self.minimumAreaSize, 1)
            $0.height = StudioCanvasMath.clamped(height, Self.minimumAreaSize, 1)
        }
    }

    /// How round a rectangle's corners are, in short sides of the canvas.
    mutating func setAnnotationCornerRadius(at index: Int, to radius: Double) -> StudioAnnotationEditResult {
        guard radius.isFinite else { return annotationUnchanged(index) }
        return editAnnotation(at: index) {
            $0.cornerRadius = StudioCanvasMath.clamped(radius, 0, StudioAnnotationGeometry.maximumCornerRadius)
        }
    }

    /// The inside fill of a rectangle or an ellipse as `#RRGGBB`, a color picker's colors being one
    /// undo step as with the other colors, or "" for no fill, which is a step of its own.
    mutating func setAnnotationShapeFill(at index: Int, to color: String) -> StudioAnnotationEditResult {
        if color.isEmpty {
            endAnnotationColorChoice()
            return editAnnotation(at: index) { $0.shapeFill = "" }
        }
        return setAnnotationColor(at: index, part: 2, to: color) { $0.shapeFill = $1 }
    }

    mutating func setAnnotationShapeFillOpacity(at index: Int, to opacity: Double) -> StudioAnnotationEditResult {
        guard opacity.isFinite else { return annotationUnchanged(index) }
        return editAnnotation(at: index) { $0.shapeFillOpacity = StudioCanvasMath.clamped(opacity, 0, 1) }
    }

    mutating func setAnnotationRedactStyle(at index: Int, to style: StudioRedactStyle) -> StudioAnnotationEditResult {
        editAnnotation(at: index) { $0.redactStyle = style }
    }

    mutating func setAnnotationRedactLevel(at index: Int, to level: StudioRedactLevel) -> StudioAnnotationEditResult {
        editAnnotation(at: index) { $0.redactLevel = level }
    }

    /// Small, medium, or large: the size of an annotation before its scale.
    mutating func setAnnotationSize(at index: Int, to size: StudioAnnotationSize) -> StudioAnnotationEditResult {
        editAnnotation(at: index) { $0.size = size }
    }

    /// Picks an emoji from a list, which is one undo step, and ends any typing in the text.
    mutating func setAnnotationEmoji(at index: Int, to emoji: String) -> StudioAnnotationEditResult {
        endAnnotationTyping()
        return editAnnotation(at: index) { $0.text = emoji }
    }

    mutating func setAnnotationShadow(at index: Int, to shadow: Bool) -> StudioAnnotationEditResult {
        editAnnotation(at: index) { $0.shadow = shadow }
    }

    /// Where a drag on the preview leaves an annotation.
    mutating func moveAnnotation(at index: Int, to move: StudioAnnotationMove) -> StudioAnnotationEditResult {
        editAnnotation(at: index) {
            $0.x = move.x
            $0.y = move.y
            $0.offsetX = move.offsetX
            $0.offsetY = move.offsetY
            if let scale = move.scale { $0.scale = scale }
            if let rotation = move.rotation { $0.rotation = rotation }
            if let width = move.width { $0.width = width }
            if let height = move.height { $0.height = height }
        }
    }

    /// What an annotation is fixed to. It stays where it is in the picture at `sourceTime` where it
    /// can: a point out of view, or a screen the frame does not have, leaves its numbers.
    mutating func setAnnotationAttach(at index: Int, to attach: StudioAnnotationAttach, time sourceTime: Double, events: StudioEvents?) -> StudioAnnotationEditResult {
        let canvas = StudioCanvasMath.naturalCanvas(project: project)
        let frame = StudioLayoutResolver.resolve(project: project, time: sourceTime, canvasWidth: canvas.width, canvasHeight: canvas.height, events: events)
        return editAnnotation(at: index) {
            if StudioAnnotationKind.isArea($0.kind) {
                let area = StudioAnnotationGeometry.areaWhenAttached($0, to: attach, frame: frame, canvasWidth: canvas.width, canvasHeight: canvas.height)
                $0.attach = attach
                if let area, area.width > 0, area.height > 0 {
                    $0.x = area.x
                    $0.y = area.y
                    $0.width = area.width
                    $0.height = area.height
                }
                return
            }
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
    static let annotationEmptyHint = "Press A, C, T, H, E, B, or M, or use the tools above, to add an arrow, a callout, text, a rectangle, an ellipse, a redaction, or an emoji at the playhead."
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
        case StudioAnnotationKind.rectangle: return "Rectangle"
        case StudioAnnotationKind.ellipse: return "Ellipse"
        case StudioAnnotationKind.redact: return "Redaction"
        case StudioAnnotationKind.emoji: return "Emoji"
        default: return "Annotation"
        }
    }

    /// The emoji offered in a grid, with their short names for VoiceOver.
    static let emojiChoices: [(emoji: String, name: String)] = [
        ("\u{1F44D}", "thumbs up"),
        ("\u{1F44E}", "thumbs down"),
        ("\u{1F44F}", "clapping hands"),
        ("\u{1F64C}", "raising hands"),
        ("\u{1F389}", "party popper"),
        ("\u{1F525}", "fire"),
        ("\u{2705}", "check mark button"),
        ("\u{274C}", "cross mark"),
        ("\u{26A0}\u{FE0F}", "warning"),
        ("\u{2757}", "red exclamation mark"),
        ("\u{2753}", "red question mark"),
        ("\u{1F4A1}", "light bulb"),
        ("\u{2B50}", "star"),
        ("\u{2764}\u{FE0F}", "red heart"),
        ("\u{1F602}", "face with tears of joy"),
        ("\u{1F62E}", "face with open mouth"),
        ("\u{1F914}", "thinking face"),
        ("\u{1F60E}", "smiling face with sunglasses"),
        ("\u{1F440}", "eyes"),
        ("\u{1F449}", "backhand index pointing right"),
        ("\u{1F448}", "backhand index pointing left"),
        ("\u{1F446}", "backhand index pointing up"),
        ("\u{1F447}", "backhand index pointing down"),
        ("\u{1F680}", "rocket"),
    ]

    static func annotationAddedMessage(_ kind: String) -> String {
        "\(annotationKindName(kind)) added."
    }

    /// An annotation in a list: "Arrow", or "Callout: Billing lives here", with long text shortened
    /// and on one line.
    static func annotationLabel(_ annotation: StudioAnnotation) -> String {
        let name = annotationKindName(annotation.kind)
        let words = annotation.text.split(whereSeparator: \.isWhitespace).joined(separator: " ")
        guard annotation.kind != StudioAnnotationKind.arrow, !StudioAnnotationKind.isArea(annotation.kind), !words.isEmpty else { return name }
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
