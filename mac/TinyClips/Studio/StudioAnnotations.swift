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
