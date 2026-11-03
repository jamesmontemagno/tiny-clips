import AppKit
import SwiftUI

let textSystemFontFamily = "System"

enum ScreenshotGradientMath {
    /// Returns frame-relative endpoints whose axis matches the requested angle in the frame's
    /// pixel space and whose projection spans every frame corner.
    static func endpoints(degrees: Double, width: Double, height: Double) -> (start: UnitPoint, end: UnitPoint) {
        let safeWidth = width.isFinite && width > 0 ? width : 1
        let safeHeight = height.isFinite && height > 0 ? height : 1
        let normalizedDegrees = degrees.truncatingRemainder(dividingBy: 360)
        let radians = normalizedDegrees * .pi / 180
        let directionX = cos(radians)
        let directionY = sin(radians)
        let halfLength = (abs(directionX) * safeWidth + abs(directionY) * safeHeight) / 2
        let offsetX = directionX * halfLength / safeWidth
        let offsetY = directionY * halfLength / safeHeight

        return (
            UnitPoint(x: CGFloat(0.5 - offsetX), y: CGFloat(0.5 - offsetY)),
            UnitPoint(x: CGFloat(0.5 + offsetX), y: CGFloat(0.5 + offsetY))
        )
    }
}

enum TextBoxPreset: String, CaseIterable, Identifiable {
    case plain
    case light
    case dark
    case accent
    case custom

    var id: String { rawValue }

    var label: String {
        rawValue.capitalized
    }
}

struct TextBoxStyle {
    static let borderWidthRange: ClosedRange<CGFloat> = 0...12
    static let paddingRange: ClosedRange<CGFloat> = 0...48
    static let cornerRadiusRange: ClosedRange<CGFloat> = 0...32

    var preset: TextBoxPreset = .plain
    var backgroundColor: Color = .clear
    var borderColor: Color = .clear
    var borderWidth: CGFloat = 0
    var padding: CGFloat = 0
    var cornerRadius: CGFloat = 0

    static var plain: TextBoxStyle {
        TextBoxStyle()
    }

    static func resolved(_ preset: TextBoxPreset, currentTextColor: Color) -> (style: TextBoxStyle, textColor: Color) {
        switch preset {
        case .plain:
            return (.plain, currentTextColor)
        case .light:
            return (
                TextBoxStyle(
                    preset: .light,
                    backgroundColor: Color(nsColor: NSColor(srgbRed: 0.95, green: 0.95, blue: 0.96, alpha: 0.96)),
                    borderColor: Color(nsColor: NSColor(srgbRed: 0.12, green: 0.12, blue: 0.14, alpha: 0.18)),
                    borderWidth: 1,
                    padding: 8,
                    cornerRadius: 6
                ),
                Color(nsColor: NSColor(srgbRed: 0.08, green: 0.08, blue: 0.09, alpha: 1))
            )
        case .dark:
            return (
                TextBoxStyle(
                    preset: .dark,
                    backgroundColor: Color(nsColor: NSColor(srgbRed: 0.10, green: 0.10, blue: 0.12, alpha: 0.94)),
                    borderColor: Color(nsColor: NSColor(srgbRed: 1, green: 1, blue: 1, alpha: 0.22)),
                    borderWidth: 1,
                    padding: 8,
                    cornerRadius: 6
                ),
                .white
            )
        case .accent:
            let accent = NSColor.controlAccentColor.usingColorSpace(.sRGB) ?? .controlAccentColor
            return (
                TextBoxStyle(
                    preset: .accent,
                    backgroundColor: Color(nsColor: accent),
                    borderColor: .clear,
                    borderWidth: 0,
                    padding: 8,
                    cornerRadius: 6
                ),
                contrastTextColor(for: accent)
            )
        case .custom:
            return (TextBoxStyle(preset: .custom), currentTextColor)
        }
    }

    static func decoratedRect(for contentRect: CGRect, scale: CGFloat, style: TextBoxStyle) -> CGRect {
        let inset = (style.padding + style.borderWidth / 2) * scale
        return contentRect.insetBy(dx: -inset, dy: -inset)
    }

    static func normalizedDecoratedRect(
        for contentRect: CGRect,
        imageSize: CGSize,
        style: TextBoxStyle
    ) -> CGRect {
        guard imageSize.width > 0, imageSize.height > 0 else { return contentRect }
        let pixelInset = (style.padding + style.borderWidth / 2) * (imageSize.width / 800)
        return contentRect.insetBy(
            dx: -(pixelInset / imageSize.width),
            dy: -(pixelInset / imageSize.height)
        )
    }

    static func normalizedContentRect(
        for decoratedRect: CGRect,
        imageSize: CGSize,
        style: TextBoxStyle
    ) -> CGRect {
        guard imageSize.width > 0, imageSize.height > 0 else { return decoratedRect }
        let pixelInset = (style.padding + style.borderWidth / 2) * (imageSize.width / 800)
        return decoratedRect.insetBy(
            dx: pixelInset / imageSize.width,
            dy: pixelInset / imageSize.height
        )
    }

    mutating func markCustom() {
        preset = .custom
        borderWidth = borderWidth.clamped(to: Self.borderWidthRange)
        padding = padding.clamped(to: Self.paddingRange)
        cornerRadius = cornerRadius.clamped(to: Self.cornerRadiusRange)
    }

    func scaled(by factor: CGFloat) -> TextBoxStyle {
        var result = self
        result.borderWidth = (borderWidth * factor).clamped(to: Self.borderWidthRange)
        result.padding = (padding * factor).clamped(to: Self.paddingRange)
        result.cornerRadius = (cornerRadius * factor).clamped(to: Self.cornerRadiusRange)
        if result.preset != .plain {
            result.preset = .custom
        }
        return result
    }

    private static func contrastTextColor(for color: NSColor) -> Color {
        let converted = color.usingColorSpace(.sRGB) ?? color
        let luminance = 0.2126 * linearized(converted.redComponent)
            + 0.7152 * linearized(converted.greenComponent)
            + 0.0722 * linearized(converted.blueComponent)
        return luminance > 0.179 ? .black : .white
    }

    private static func linearized(_ value: CGFloat) -> CGFloat {
        value <= 0.04045 ? value / 12.92 : pow((value + 0.055) / 1.055, 2.4)
    }
}

// Number tool rendering constants
let numberCircleMinPixels: CGFloat = 20
let numberCircleMaxPixels: CGFloat = 80
let numberCircleSizeRatio: CGFloat = 0.05
let numberCircleFontRatio: CGFloat = 0.55
let numberCircleMinimumDisplayPixels: CGFloat = 16

enum EditTool: String, CaseIterable {
    case move = "arrow.up.and.down.and.arrow.left.and.right"
    case crop = "crop"
    case rectangle = "rectangle"
    case circle = "circle"
    case arrow = "arrowshape.right"
    case line = "line.diagonal"
    case pencil = "pencil.tip"
    case text = "textformat"
    case number = "number.circle.fill"
    case emoji = "face.smiling"
    case blur = "eye.slash"

    var label: String {
        switch self {
        case .move: return "Move"
        case .crop: return "Crop"
        case .rectangle: return "Rectangle"
        case .circle: return "Circle"
        case .arrow: return "Arrow"
        case .line: return "Line"
        case .pencil: return "Draw"
        case .text: return "Text"
        case .number: return "Number"
        case .emoji: return "Emoji"
        case .blur: return "Redact"
        }
    }

    /// Tools whose annotations can be turned with the rotation grip. Pencil strokes rotate their
    /// points in place, so they don't store an angle; see `storesRotation`.
    var supportsRotation: Bool {
        switch self {
        case .emoji, .text, .rectangle, .circle, .pencil: return true
        case .move, .crop, .arrow, .line, .number, .blur: return false
        }
    }

    /// Tools that keep a persistent `rotation` angle on the annotation (drawn via a transform).
    var storesRotation: Bool {
        supportsRotation && self != .pencil
    }

    /// Lines and arrow tails take a start cap; an arrow's other end is its head.
    var usesStartCap: Bool { self == .line || self == .arrow }

    var usesEndCap: Bool { self == .line }

    /// Rectangles and arrowheads have corners; circles have none and pencil strokes always round.
    var usesJoin: Bool { self == .rectangle || self == .arrow }

    /// Closed shapes align their stroke; arrows and lines align their outline.
    var usesAlignment: Bool { self == .rectangle || self == .circle || self == .arrow || self == .line }

    /// Arrows and lines are filled bodies (in the annotation's color, at its line width) that can
    /// carry a separate outline.
    var usesOutline: Bool { self == .arrow || self == .line }
}

/// Where a closed shape's stroke sits relative to its outline. Raw values match the Windows names.
enum StrokeAlignment: String, CaseIterable, Identifiable {
    case center
    case inside
    case outside

    var id: Self { self }

    var label: String {
        switch self {
        case .center: return "Center"
        case .inside: return "Inside"
        case .outside: return "Outside"
        }
    }
}

/// A rectangle's stroke as a filled ring: `outer` with its corners shaped by `outerJoin` (rounded
/// or cut by `cornerSize`), minus the always-sharp `inner` rectangle. Shared with Windows through
/// `shared/screenshot/fixtures/alignment`.
struct RectStrokeRing: Equatable {
    var outer: CGRect
    var outerJoin: StrokeJoin
    var cornerSize: CGFloat
    var inner: CGRect

    init(bounds: CGRect, width: CGFloat, alignment: StrokeAlignment, join: StrokeJoin) {
        let outerOffset: CGFloat
        let innerInset: CGFloat
        switch alignment {
        case .center:
            outerOffset = width / 2
            innerInset = width / 2
        case .inside:
            outerOffset = 0
            innerInset = width
        case .outside:
            outerOffset = width
            innerInset = 0
        }
        outer = Self.inset(bounds, by: -outerOffset)
        outerJoin = outerOffset > 0 ? join : .miter
        cornerSize = outerOffset > 0 && join != .miter ? outerOffset : 0
        inner = Self.inset(bounds, by: innerInset)
    }

    /// The ring to fill with the even-odd rule.
    var path: CGPath {
        let path = CGMutablePath()
        let o = outer
        let c = cornerSize
        switch outerJoin {
        case .round where c > 0:
            path.addRoundedRect(in: o, cornerWidth: c, cornerHeight: c)
        case .bevel where c > 0:
            path.addLines(between: [
                CGPoint(x: o.minX + c, y: o.minY), CGPoint(x: o.maxX - c, y: o.minY),
                CGPoint(x: o.maxX, y: o.minY + c), CGPoint(x: o.maxX, y: o.maxY - c),
                CGPoint(x: o.maxX - c, y: o.maxY), CGPoint(x: o.minX + c, y: o.maxY),
                CGPoint(x: o.minX, y: o.maxY - c), CGPoint(x: o.minX, y: o.minY + c),
            ])
            path.closeSubpath()
        default:
            path.addRect(o)
        }
        path.addRect(inner)
        return path
    }

    /// Insets by `amount` (outsets when negative), never past an empty rectangle.
    static func inset(_ rect: CGRect, by amount: CGFloat) -> CGRect {
        let dx = min(amount, rect.width / 2)
        let dy = min(amount, rect.height / 2)
        return CGRect(x: rect.minX + dx, y: rect.minY + dy, width: rect.width - 2 * dx, height: rect.height - 2 * dy)
    }
}

/// How an open stroke ends. Raw values match the Windows `StrokeCap` names and the shared contract.
enum StrokeCap: String, CaseIterable, Identifiable {
    case butt
    case round
    case square

    var id: Self { self }

    var label: String {
        switch self {
        case .butt: return "Flat"
        case .round: return "Round"
        case .square: return "Square"
        }
    }

    var cgLineCap: CGLineCap {
        switch self {
        case .butt: return .butt
        case .round: return .round
        case .square: return .square
        }
    }
}

/// How a stroke turns a corner. Raw values match the Windows `StrokeJoin` names.
enum StrokeJoin: String, CaseIterable, Identifiable {
    case miter
    case round
    case bevel

    var id: Self { self }

    var label: String {
        switch self {
        case .miter: return "Sharp"
        case .round: return "Round"
        case .bevel: return "Beveled"
        }
    }

    var cgLineJoin: CGLineJoin {
        switch self {
        case .miter: return .miter
        case .round: return .round
        case .bevel: return .bevel
        }
    }
}

struct ScreenshotAnnotation: Identifiable {
    let id = UUID()
    let tool: EditTool
    var rect: CGRect
    var color: Color
    var textColor: Color = .white
    var fillColor: Color = .clear
    var lineWidth: CGFloat
    var text: String
    var points: [CGPoint]
    var fontSize: CGFloat = 16
    var fontFamily: String = textSystemFontFamily
    var isBold: Bool = false
    var isItalic: Bool = false
    var isUnderlined: Bool = false
    var textBoxStyle: TextBoxStyle = .plain
    var redactionBlurPreset: RedactionBlurPreset = .medium
    var isOpaqueRedaction = false
    var arrow: ArrowShapeOptions = .classic
    var startCap: StrokeCap = .round
    var endCap: StrokeCap = .round
    var lineJoin: StrokeJoin = .round
    var strokeAlignment: StrokeAlignment = .center
    /// Arrows and lines only: an outline around the body; a width of 0 draws none.
    var outlineColor: Color = .black
    var outlineWidth: CGFloat = 0
    /// Clockwise rotation in radians around the center of `rect` for tools where `tool.storesRotation`.
    var rotation: CGFloat = 0

    /// Whether this annotation is drawn through a rotation transform.
    var isRotated: Bool {
        tool.storesRotation && abs(rotation) > 0.0001
    }
}

extension Array where Element == ScreenshotAnnotation {
    /// Indices in back-to-front drawing order: opaque redactions always render above every
    /// other annotation. Drawing, export, and hit-testing (reversed) must all use this order.
    var drawOrderIndices: [Int] {
        indices.filter { !self[$0].isOpaqueRedaction } + indices.filter { self[$0].isOpaqueRedaction }
    }
}

typealias LinePoints = (start: CGPoint, end: CGPoint)

// MARK: - Emoji annotations

/// Sizing constants for emoji stickers, expressed relative to the image width.
let emojiDefaultSideRatio: CGFloat = 0.10
let emojiMinimumSidePixels: CGFloat = 16
let emojiMaximumSideRatio: CGFloat = 1.0
/// Fraction of the sticker's square that the emoji glyph's point size occupies.
let emojiGlyphFontRatio: CGFloat = 0.8

/// Geometry helpers for annotations that can rotate around their center.
///
/// All functions take a `size` describing the rendered image (pixels for export and
/// hit-testing, points for the SwiftUI preview). Because both spaces share the image's
/// aspect ratio, the same math produces correct results in either one; normalized rects
/// are converted into that space before any rotation is applied.
enum RotatableAnnotationGeometry {
    static func scaledRect(_ rect: CGRect, in size: CGSize) -> CGRect {
        CGRect(
            x: rect.origin.x * size.width,
            y: rect.origin.y * size.height,
            width: rect.width * size.width,
            height: rect.height * size.height
        )
    }

    static func center(of rect: CGRect, in size: CGSize) -> CGPoint {
        let scaled = scaledRect(rect, in: size)
        return CGPoint(x: scaled.midX, y: scaled.midY)
    }

    /// Rotates `point` clockwise (in a y-down coordinate space) around `center`.
    static func rotate(_ point: CGPoint, around center: CGPoint, by angle: CGFloat) -> CGPoint {
        let dx = point.x - center.x
        let dy = point.y - center.y
        let cosA = cos(angle)
        let sinA = sin(angle)
        return CGPoint(
            x: center.x + dx * cosA - dy * sinA,
            y: center.y + dx * sinA + dy * cosA
        )
    }

    /// Corner positions (top-left, top-right, bottom-left, bottom-right) after rotation.
    static func corners(of rect: CGRect, rotation: CGFloat, in size: CGSize) -> [CGPoint] {
        let scaled = scaledRect(rect, in: size)
        let center = CGPoint(x: scaled.midX, y: scaled.midY)
        return [
            CGPoint(x: scaled.minX, y: scaled.minY),
            CGPoint(x: scaled.maxX, y: scaled.minY),
            CGPoint(x: scaled.minX, y: scaled.maxY),
            CGPoint(x: scaled.maxX, y: scaled.maxY),
        ].map { rotate($0, around: center, by: rotation) }
    }

    /// Distance between the rotated top edge and the rotation handle.
    static func rotationHandleOffset(forScaledHeight height: CGFloat) -> CGFloat {
        max(18, height * 0.3)
    }

    /// Position of the rotation grip, which sits above the sticker's top edge and follows its rotation.
    static func rotationHandle(for rect: CGRect, rotation: CGFloat, in size: CGSize) -> CGPoint {
        let scaled = scaledRect(rect, in: size)
        let center = CGPoint(x: scaled.midX, y: scaled.midY)
        let unrotated = CGPoint(
            x: center.x,
            y: scaled.minY - rotationHandleOffset(forScaledHeight: scaled.height)
        )
        return rotate(unrotated, around: center, by: rotation)
    }

    /// Whether `point` (already in the `size` space) falls inside the rotated rect, grown by `padding`.
    static func contains(_ point: CGPoint, rect: CGRect, rotation: CGFloat, in size: CGSize, padding: CGFloat = 0) -> Bool {
        let scaled = scaledRect(rect, in: size)
        let center = CGPoint(x: scaled.midX, y: scaled.midY)
        let local = rotate(point, around: center, by: -rotation)
        return scaled.insetBy(dx: -padding, dy: -padding).contains(local)
    }

    /// Rotation (clockwise radians, 0 = straight up) of the direction from `center` to `point`.
    static func angle(from center: CGPoint, to point: CGPoint) -> CGFloat {
        atan2(point.x - center.x, -(point.y - center.y))
    }

    /// Normalizes an angle into the `-π...π` range.
    static func normalizedAngle(_ angle: CGFloat) -> CGFloat {
        var result = angle.truncatingRemainder(dividingBy: 2 * .pi)
        if result > .pi { result -= 2 * .pi }
        if result < -.pi { result += 2 * .pi }
        return result
    }

    /// Snaps to the nearest `increment` (e.g. 15°) when `shouldSnap` is true.
    static func snapped(_ angle: CGFloat, to increment: CGFloat, shouldSnap: Bool) -> CGFloat {
        guard shouldSnap, increment > 0 else { return angle }
        return (angle / increment).rounded() * increment
    }
}

enum EmojiAnnotationMath {
    /// Default sticker side length in pixels for an image of `imageSize`.
    static func defaultSidePixels(forImageSize imageSize: CGSize) -> CGFloat {
        clampSidePixels(imageSize.width * emojiDefaultSideRatio, imageSize: imageSize)
    }

    static func clampSidePixels(_ side: CGFloat, imageSize: CGSize) -> CGFloat {
        let maximum = max(emojiMinimumSidePixels, min(imageSize.width, imageSize.height) * emojiMaximumSideRatio)
        return min(max(side, emojiMinimumSidePixels), maximum)
    }

    /// Normalized rect for a square sticker of `sidePixels` centered on a normalized point.
    static func normalizedRect(centeredAt center: CGPoint, sidePixels: CGFloat, imageSize: CGSize) -> CGRect {
        guard imageSize.width > 0, imageSize.height > 0 else { return .zero }
        let normW = sidePixels / imageSize.width
        let normH = sidePixels / imageSize.height
        return CGRect(x: center.x - normW / 2, y: center.y - normH / 2, width: normW, height: normH)
    }

    /// Point size of the emoji glyph for a sticker rendered `sideLength` tall.
    static func glyphFontSize(forSide sideLength: CGFloat) -> CGFloat {
        max(1, sideLength * emojiGlyphFontRatio)
    }

    /// Returns the last user-perceived character of `text` if it is an emoji, otherwise nil.
    static func emoji(from text: String) -> String? {
        guard let last = text.last else { return nil }
        let scalars = Array(last.unicodeScalars)
        // Plain digits/symbols report `isEmoji` too, so require emoji presentation unless
        // the cluster carries a variation selector, modifier, or ZWJ sequence.
        let isEmoji = scalars.contains { $0.properties.isEmojiPresentation }
            || (scalars.count > 1 && scalars.contains { $0.properties.isEmoji })
        return isEmoji ? String(last) : nil
    }
}

enum EmojiPalette {
    /// Curated quick picks shown in the inspector; the system picker covers everything else.
    static let common: [String] = [
        "👍", "👎", "👏", "🙌", "👉", "👀",
        "😀", "😂", "😍", "😎", "🤔", "😱",
        "❤️", "🔥", "⭐", "✨", "💯", "🎉",
        "✅", "❌", "⚠️", "❓", "💡", "🚀",
    ]

    static let defaultEmoji = "😀"
    static let maximumRecent = 12
}

enum RedactionBlurPreset: String, CaseIterable, Identifiable {
    case light
    case medium
    case heavy

    var id: Self { self }

    var label: String {
        switch self {
        case .light: return "Light"
        case .medium: return "Medium"
        case .heavy: return "Heavy"
        }
    }

    var previewBlockSize: CGFloat {
        switch self {
        case .light: return 8
        case .medium: return 10
        case .heavy: return 14
        }
    }

    var exportBlockSize: CGFloat {
        switch self {
        case .light: return 10
        case .medium: return 12
        case .heavy: return 16
        }
    }

    var baseBrightness: Double {
        switch self {
        case .light: return 0.34
        case .medium: return 0.29
        case .heavy: return 0.24
        }
    }

    var contrastStep: Double {
        switch self {
        case .light: return 0.08
        case .medium: return 0.12
        case .heavy: return 0.16
        }
    }

    var cycleLength: Int {
        switch self {
        case .light: return 2
        case .medium: return 3
        case .heavy: return 4
        }
    }
}

enum ExportBackgroundStyle: String, CaseIterable, Identifiable {
    case transparent
    case solid
    case gradient
    case wallpaper

    var id: Self { self }

    var label: String {
        switch self {
        case .transparent: return "Transparent"
        case .solid: return "Solid"
        case .gradient: return "Gradient"
        case .wallpaper: return "Wallpaper"
        }
    }
}

enum ExportFramePreset: String, CaseIterable, Identifiable {
    case original
    case square
    case landscapeFourByThree
    case landscapeSixteenByNine
    case portraitThreeByFour
    case portraitNineBySixteen

    var id: Self { self }

    var label: String {
        switch self {
        case .original: return "Original"
        case .square: return "1:1"
        case .landscapeFourByThree: return "4:3"
        case .landscapeSixteenByNine: return "16:9"
        case .portraitThreeByFour: return "3:4"
        case .portraitNineBySixteen: return "9:16"
        }
    }

    var aspectRatio: CGFloat? {
        switch self {
        case .original: return nil
        case .square: return 1
        case .landscapeFourByThree: return 4.0 / 3.0
        case .landscapeSixteenByNine: return 16.0 / 9.0
        case .portraitThreeByFour: return 3.0 / 4.0
        case .portraitNineBySixteen: return 9.0 / 16.0
        }
    }
}

enum ExportHorizontalAlignment: String, CaseIterable, Identifiable {
    case leading
    case center
    case trailing

    var id: Self { self }

    var label: String {
        switch self {
        case .leading: return "Left"
        case .center: return "Center"
        case .trailing: return "Right"
        }
    }

    var placementFactor: CGFloat {
        switch self {
        case .leading: return 0
        case .center: return 0.5
        case .trailing: return 1
        }
    }
}

enum ExportVerticalAlignment: String, CaseIterable, Identifiable {
    case top
    case center
    case bottom

    var id: Self { self }

    var label: String {
        switch self {
        case .top: return "Top"
        case .center: return "Center"
        case .bottom: return "Bottom"
        }
    }

    var placementFactor: CGFloat {
        switch self {
        case .top: return 0
        case .center: return 0.5
        case .bottom: return 1
        }
    }
}

struct ExportFrameLayout {
    let frameSize: CGSize
    let imageRect: CGRect

    static func make(
        imageSize: CGSize,
        padding: CGFloat,
        preset: ExportFramePreset,
        horizontalAlignment: ExportHorizontalAlignment,
        verticalAlignment: ExportVerticalAlignment,
        snapsToPixels: Bool = false
    ) -> Self {
        let safePadding = max(0, padding)
        let baseSize = CGSize(
            width: imageSize.width + (safePadding * 2),
            height: imageSize.height + (safePadding * 2)
        )

        var frameSize = baseSize
        if let targetRatio = preset.aspectRatio, baseSize.width > 0, baseSize.height > 0 {
            if baseSize.width / baseSize.height < targetRatio {
                frameSize.width = ceil(baseSize.height * targetRatio)
            } else if baseSize.width / baseSize.height > targetRatio {
                frameSize.height = ceil(baseSize.width / targetRatio)
            }
        }

        var originX = safePadding + (max(0, frameSize.width - baseSize.width) * horizontalAlignment.placementFactor)
        var originY = safePadding + (max(0, frameSize.height - baseSize.height) * verticalAlignment.placementFactor)

        if snapsToPixels {
            // Snap to whole pixels for bitmap export. Centering the card inside an odd
            // amount of leftover preset space lands its origin on a half pixel, and
            // Core Graphics then anti-aliases the card edge across two columns/rows
            // at 50% alpha. On a transparent background exported as JPEG that edge
            // flattens to a light hairline around the screenshot. The display-space
            // preview path keeps fractional geometry so it scales proportionally.
            frameSize.width = frameSize.width.rounded(.up)
            frameSize.height = frameSize.height.rounded(.up)
            originX = originX.rounded()
                .clamped(to: 0...Swift.max(0, frameSize.width - imageSize.width))
            originY = originY.rounded()
                .clamped(to: 0...Swift.max(0, frameSize.height - imageSize.height))
        }

        return Self(
            frameSize: frameSize,
            imageRect: CGRect(
                x: originX,
                y: originY,
                width: imageSize.width,
                height: imageSize.height
            )
        )
    }
}

private extension CGFloat {
    func clamped(to range: ClosedRange<CGFloat>) -> CGFloat {
        Swift.min(Swift.max(self, range.lowerBound), range.upperBound)
    }
}

/// What Esc does in the editor. Each press takes only the first step that applies.
enum ScreenshotEditorEscapeAction: Equatable {
    case cancelTextAnnotation
    /// Esc in a sidebar text field leaves the field; it never closes the editor.
    case leaveTextField
    case clearCropSelection
    case close

    static func resolve(
        isEditingTextAnnotation: Bool,
        textFieldHasFocus: Bool,
        hasCropSelection: Bool
    ) -> ScreenshotEditorEscapeAction {
        if isEditingTextAnnotation { return .cancelTextAnnotation }
        if textFieldHasFocus { return .leaveTextField }
        if hasCropSelection { return .clearCropSelection }
        return .close
    }
}

/// What the editor has to confirm before it closes.
enum ScreenshotEditorClosePrompt: Equatable {
    case discardChanges
    /// The capture only exists as a temporary file, so closing throws it away.
    case discardUnsavedCapture
    case closeEditor

    enum Trigger {
        case closeCommand
        case escapeKey
    }

    static func resolve(
        trigger: Trigger,
        confirmOnEscape: Bool,
        hasUnsavedChanges: Bool,
        discardsUnsavedCapture: Bool
    ) -> ScreenshotEditorClosePrompt? {
        switch trigger {
        case .closeCommand:
            return hasUnsavedChanges ? .discardChanges : nil
        case .escapeKey:
            guard confirmOnEscape else { return nil }
            if discardsUnsavedCapture { return .discardUnsavedCapture }
            return hasUnsavedChanges ? .discardChanges : .closeEditor
        }
    }

    var title: String {
        switch self {
        case .discardChanges: "Discard changes?"
        case .discardUnsavedCapture: "Discard screenshot?"
        case .closeEditor: "Close the editor?"
        }
    }

    var message: String {
        switch self {
        case .discardChanges:
            "You have unsaved changes. Are you sure you want to exit?"
        case .discardUnsavedCapture:
            "This screenshot has not been saved. Closing the editor discards it."
        case .closeEditor:
            "This screenshot is saved and has no unsaved changes. You can turn off this confirmation in General settings."
        }
    }

    var confirmTitle: String {
        switch self {
        case .discardChanges: "Discard Changes"
        case .discardUnsavedCapture: "Discard Screenshot"
        case .closeEditor: "Close Editor"
        }
    }

    var isDestructive: Bool {
        self != .closeEditor
    }
}

enum ScreenshotEditorZoomMath {
    static let minimumScale: CGFloat = 0.25
    static let maximumScale: CGFloat = 4
    static let presets: [CGFloat] = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4]

    static func clamp(_ scale: CGFloat, maximumScale: CGFloat = ScreenshotEditorZoomMath.maximumScale) -> CGFloat {
        min(maximumScale, max(minimumScale, scale))
    }

    static func steppedScale(
        from scale: CGFloat,
        direction: Int,
        maximumScale: CGFloat = ScreenshotEditorZoomMath.maximumScale
    ) -> CGFloat {
        let current = clamp(scale, maximumScale: maximumScale)
        if direction > 0 {
            return presets.first(where: { $0 > current + 0.001 }) ?? maximumScale
        }
        return presets.reversed().first(where: { $0 < current - 0.001 }) ?? minimumScale
    }

    static func nativeSizeScale(fitScale: CGFloat, backingScale: CGFloat) -> CGFloat? {
        guard fitScale.isFinite, fitScale > 0, backingScale.isFinite, backingScale > 0 else {
            return nil
        }
        let scale = 1 / (fitScale * backingScale)
        return scale.isFinite && scale > 0 ? scale : nil
    }

    static func focalAdjustedPan(
        _ pan: CGSize,
        oldScale: CGFloat,
        newScale: CGFloat,
        focalPoint: CGPoint,
        viewportSize: CGSize
    ) -> CGSize {
        guard oldScale > 0 else { return pan }
        let ratio = newScale / oldScale
        let focalOffset = CGPoint(
            x: focalPoint.x - viewportSize.width / 2,
            y: focalPoint.y - viewportSize.height / 2
        )
        return CGSize(
            width: focalOffset.x - ((focalOffset.x - pan.width) * ratio),
            height: focalOffset.y - ((focalOffset.y - pan.height) * ratio)
        )
    }

    static func clampedPan(_ pan: CGSize, contentSize: CGSize, viewportSize: CGSize) -> CGSize {
        let maxX = max(0, (contentSize.width - viewportSize.width) / 2)
        let maxY = max(0, (contentSize.height - viewportSize.height) / 2)
        return CGSize(
            width: min(maxX, max(-maxX, pan.width)),
            height: min(maxY, max(-maxY, pan.height))
        )
    }
}

/// Grab points on the crop selection: four corners plus the four edges.
enum CropHandle: CaseIterable {
    case topLeft
    case top
    case topRight
    case left
    case right
    case bottomLeft
    case bottom
    case bottomRight

    var movesLeftEdge: Bool { self == .topLeft || self == .left || self == .bottomLeft }
    var movesRightEdge: Bool { self == .topRight || self == .right || self == .bottomRight }
    var movesTopEdge: Bool { self == .topLeft || self == .top || self == .topRight }
    var movesBottomEdge: Bool { self == .bottomLeft || self == .bottom || self == .bottomRight }

    var isCorner: Bool {
        (movesLeftEdge || movesRightEdge) && (movesTopEdge || movesBottomEdge)
    }
}

/// What a drag that starts at a given point does to the crop selection.
enum CropDragMode: Equatable {
    case create
    case move
    case resize(CropHandle)
}

/// Crop selection geometry. Rects and points are normalized to the image (0...1 on both axes,
/// y-down) unless a parameter says otherwise.
enum ScreenshotEditorCropMath {
    /// Selections smaller than this on screen are treated as accidental drags.
    static let minimumSelectionDisplayPoints: CGFloat = 4
    /// Half-size of the square hit area around each handle and edge, in screen points.
    static let handleHitRadiusPoints: CGFloat = 8

    static func handlePoint(_ handle: CropHandle, in rect: CGRect) -> CGPoint {
        let x: CGFloat
        if handle.movesLeftEdge {
            x = rect.minX
        } else if handle.movesRightEdge {
            x = rect.maxX
        } else {
            x = rect.midX
        }

        let y: CGFloat
        if handle.movesTopEdge {
            y = rect.minY
        } else if handle.movesBottomEdge {
            y = rect.maxY
        } else {
            y = rect.midY
        }

        return CGPoint(x: x, y: y)
    }

    /// Converts the on-screen handle hit radius into normalized units for an image drawn at `displaySize`.
    static func hitTolerance(forDisplaySize displaySize: CGSize) -> CGSize {
        guard displaySize.width > 0, displaySize.height > 0 else {
            return CGSize(width: 0.02, height: 0.02)
        }
        return CGSize(
            width: handleHitRadiusPoints / displaySize.width,
            height: handleHitRadiusPoints / displaySize.height
        )
    }

    /// Corners win over edges, edges over the interior; anywhere else starts a new selection.
    static func dragMode(at point: CGPoint, selection: CGRect?, tolerance: CGSize) -> CropDragMode {
        guard let selection, selection.width > 0, selection.height > 0 else { return .create }

        let leftDistance = abs(point.x - selection.minX)
        let rightDistance = abs(point.x - selection.maxX)
        let topDistance = abs(point.y - selection.minY)
        let bottomDistance = abs(point.y - selection.maxY)

        // A selection narrower than the tolerance matches both sides; keep the closer one.
        let nearLeft = leftDistance <= tolerance.width && leftDistance <= rightDistance
        let nearRight = rightDistance <= tolerance.width && !nearLeft
        let nearTop = topDistance <= tolerance.height && topDistance <= bottomDistance
        let nearBottom = bottomDistance <= tolerance.height && !nearTop

        if nearTop && nearLeft { return .resize(.topLeft) }
        if nearTop && nearRight { return .resize(.topRight) }
        if nearBottom && nearLeft { return .resize(.bottomLeft) }
        if nearBottom && nearRight { return .resize(.bottomRight) }

        let spansX = point.x >= selection.minX && point.x <= selection.maxX
        let spansY = point.y >= selection.minY && point.y <= selection.maxY
        if nearLeft && spansY { return .resize(.left) }
        if nearRight && spansY { return .resize(.right) }
        if nearTop && spansX { return .resize(.top) }
        if nearBottom && spansX { return .resize(.bottom) }

        return spansX && spansY ? .move : .create
    }

    /// A new selection dragged from `start` to `end`. `square` keeps it square in image pixels.
    static func created(from start: CGPoint, to end: CGPoint, square: Bool, imageSize: CGSize) -> CGRect {
        let start = clampedToImage(start)
        var end = clampedToImage(end)

        if square, imageSize.width > 0, imageSize.height > 0 {
            let dx = end.x - start.x
            let dy = end.y - start.y
            let availableX = (dx >= 0 ? 1 - start.x : start.x) * imageSize.width
            let availableY = (dy >= 0 ? 1 - start.y : start.y) * imageSize.height
            let side = min(
                max(abs(dx) * imageSize.width, abs(dy) * imageSize.height),
                availableX,
                availableY
            )
            end = CGPoint(
                x: start.x + (dx >= 0 ? side : -side) / imageSize.width,
                y: start.y + (dy >= 0 ? side : -side) / imageSize.height
            )
        }

        return CGRect(
            x: min(start.x, end.x),
            y: min(start.y, end.y),
            width: abs(end.x - start.x),
            height: abs(end.y - start.y)
        )
    }

    /// Moves the selection without resizing it, stopping at the image edges.
    static func moved(_ rect: CGRect, by delta: CGSize) -> CGRect {
        let width = min(1, rect.width)
        let height = min(1, rect.height)
        return CGRect(
            x: min(max(0, rect.minX + delta.width), 1 - width),
            y: min(max(0, rect.minY + delta.height), 1 - height),
            width: width,
            height: height
        )
    }

    /// Moves the edges owned by `handle` to `point`. Dragging past the opposite edge flips the
    /// selection instead of collapsing it. `lockedAspect` (normalized width / height) keeps the
    /// shape for corner handles.
    static func resized(_ rect: CGRect, handle: CropHandle, to point: CGPoint, lockedAspect: CGFloat? = nil) -> CGRect {
        let point = clampedToImage(point)

        if let lockedAspect, lockedAspect > 0, handle.isCorner {
            let anchor = CGPoint(
                x: handle.movesLeftEdge ? rect.maxX : rect.minX,
                y: handle.movesTopEdge ? rect.maxY : rect.minY
            )
            let dx = point.x - anchor.x
            let dy = point.y - anchor.y
            let growsRight = dx == 0 ? handle.movesRightEdge : dx > 0
            let growsDown = dy == 0 ? handle.movesBottomEdge : dy > 0
            let availableWidth = growsRight ? 1 - anchor.x : anchor.x
            let availableHeight = growsDown ? 1 - anchor.y : anchor.y

            var width = max(abs(dx), abs(dy) * lockedAspect)
            width = min(width, availableWidth, availableHeight * lockedAspect)
            let height = width / lockedAspect

            return CGRect(
                x: growsRight ? anchor.x : anchor.x - width,
                y: growsDown ? anchor.y : anchor.y - height,
                width: width,
                height: height
            )
        }

        var left = rect.minX
        var right = rect.maxX
        var top = rect.minY
        var bottom = rect.maxY
        if handle.movesLeftEdge { left = point.x }
        if handle.movesRightEdge { right = point.x }
        if handle.movesTopEdge { top = point.y }
        if handle.movesBottomEdge { bottom = point.y }

        return CGRect(
            x: min(left, right),
            y: min(top, bottom),
            width: abs(right - left),
            height: abs(bottom - top)
        )
    }

    /// Whether the selection is big enough on screen to be a deliberate crop.
    static func isUsableSelection(_ rect: CGRect, displaySize: CGSize) -> Bool {
        rect.width * displaySize.width >= minimumSelectionDisplayPoints
            && rect.height * displaySize.height >= minimumSelectionDisplayPoints
    }

    private static func clampedToImage(_ point: CGPoint) -> CGPoint {
        CGPoint(x: min(1, max(0, point.x)), y: min(1, max(0, point.y)))
    }

    /// Moves and/or grows the selection by whole image pixels, for keyboard and VoiceOver control.
    /// It works on the pixel rect Apply Crop would cut, so each step changes the readout exactly.
    /// Growth keeps the top-left corner in place until the selection reaches an image edge.
    static func adjusted(
        _ rect: CGRect,
        movingBy offset: CGSize = .zero,
        growingBy growth: CGSize = .zero,
        imageSize: CGSize
    ) -> CGRect {
        guard let pixels = pixelRect(for: rect, imageSize: imageSize) else { return rect }

        let width = min(imageSize.width, max(1, pixels.width + growth.width))
        let height = min(imageSize.height, max(1, pixels.height + growth.height))
        let x = min(max(0, pixels.minX + offset.width), imageSize.width - width)
        let y = min(max(0, pixels.minY + offset.height), imageSize.height - height)

        return CGRect(
            x: x / imageSize.width,
            y: y / imageSize.height,
            width: width / imageSize.width,
            height: height / imageSize.height
        )
    }

    /// Normalized edges that sit on a pixel boundary come back a hair off after the round trip
    /// through 0...1; without this, rounding outward would widen such a selection by a pixel.
    private static func snappedToPixelBoundary(_ value: CGFloat) -> CGFloat {
        let nearest = value.rounded()
        return abs(value - nearest) < 0.001 ? nearest : value
    }

    static func pixelRect(for normalizedRect: CGRect, imageSize: CGSize) -> CGRect? {
        guard imageSize.width > 0, imageSize.height > 0 else { return nil }

        let left = snappedToPixelBoundary(max(0, min(imageSize.width, normalizedRect.minX * imageSize.width)))
        let top = snappedToPixelBoundary(max(0, min(imageSize.height, normalizedRect.minY * imageSize.height)))
        let right = snappedToPixelBoundary(max(0, min(imageSize.width, normalizedRect.maxX * imageSize.width)))
        let bottom = snappedToPixelBoundary(max(0, min(imageSize.height, normalizedRect.maxY * imageSize.height)))
        let width = abs(right - left)
        let height = abs(bottom - top)
        guard width > 0, height > 0 else { return nil }

        let rect = CGRect(
            x: min(left, right),
            y: min(top, bottom),
            width: width,
            height: height
        ).integral

        guard rect.width >= 1, rect.height >= 1 else { return nil }
        return rect
    }
}

struct ExportBackgroundPreset: Identifiable {
    let id: String
    let label: String
    let style: ExportBackgroundStyle
    let primary: Color
    let secondary: Color?
}

let solidBackgroundPresets: [ExportBackgroundPreset] = [
    ExportBackgroundPreset(id: "transparent", label: "Transparent", style: .transparent, primary: .clear, secondary: nil),
    ExportBackgroundPreset(id: "white", label: "White", style: .solid, primary: .white, secondary: nil),
    ExportBackgroundPreset(id: "ink", label: "Ink", style: .solid, primary: Color(red: 0.08, green: 0.09, blue: 0.10), secondary: nil),
    ExportBackgroundPreset(id: "coral", label: "Coral", style: .solid, primary: Color(red: 1.00, green: 0.48, blue: 0.42), secondary: nil),
    ExportBackgroundPreset(id: "lemon", label: "Lemon", style: .solid, primary: Color(red: 1.00, green: 0.88, blue: 0.25), secondary: nil),
    ExportBackgroundPreset(id: "mint", label: "Mint", style: .solid, primary: Color(red: 0.41, green: 0.86, blue: 0.62), secondary: nil),
    ExportBackgroundPreset(id: "sky", label: "Sky", style: .solid, primary: Color(red: 0.34, green: 0.67, blue: 0.96), secondary: nil),
    ExportBackgroundPreset(id: "lilac", label: "Lilac", style: .solid, primary: Color(red: 0.70, green: 0.58, blue: 0.94), secondary: nil),
    ExportBackgroundPreset(id: "bubblegum", label: "Bubblegum", style: .solid, primary: Color(red: 1.00, green: 0.42, blue: 0.76), secondary: nil),
    ExportBackgroundPreset(id: "tangerine", label: "Tangerine", style: .solid, primary: Color(red: 1.00, green: 0.56, blue: 0.16), secondary: nil),
    ExportBackgroundPreset(id: "lagoon", label: "Lagoon", style: .solid, primary: Color(red: 0.00, green: 0.72, blue: 0.78), secondary: nil),
    ExportBackgroundPreset(id: "plum", label: "Plum", style: .solid, primary: Color(red: 0.39, green: 0.18, blue: 0.58), secondary: nil),
]

let gradientBackgroundPresets: [ExportBackgroundPreset] = [
    ExportBackgroundPreset(id: "sunset", label: "Sunset", style: .gradient, primary: Color(red: 1.00, green: 0.48, blue: 0.37), secondary: Color(red: 1.00, green: 0.86, blue: 0.31)),
    ExportBackgroundPreset(id: "ocean", label: "Ocean", style: .gradient, primary: Color(red: 0.15, green: 0.53, blue: 0.91), secondary: Color(red: 0.18, green: 0.88, blue: 0.75)),
    ExportBackgroundPreset(id: "candy", label: "Candy", style: .gradient, primary: Color(red: 1.00, green: 0.42, blue: 0.68), secondary: Color(red: 0.55, green: 0.78, blue: 1.00)),
    ExportBackgroundPreset(id: "forest", label: "Forest", style: .gradient, primary: Color(red: 0.16, green: 0.56, blue: 0.35), secondary: Color(red: 0.72, green: 0.88, blue: 0.42)),
    ExportBackgroundPreset(id: "ember", label: "Ember", style: .gradient, primary: Color(red: 0.22, green: 0.08, blue: 0.05), secondary: Color(red: 1.00, green: 0.45, blue: 0.16)),
    ExportBackgroundPreset(id: "aurora", label: "Aurora", style: .gradient, primary: Color(red: 0.28, green: 0.94, blue: 0.72), secondary: Color(red: 0.52, green: 0.42, blue: 1.00)),
    ExportBackgroundPreset(id: "peach", label: "Peach", style: .gradient, primary: Color(red: 1.00, green: 0.72, blue: 0.52), secondary: Color(red: 0.98, green: 0.42, blue: 0.54)),
    ExportBackgroundPreset(id: "glacier", label: "Glacier", style: .gradient, primary: Color(red: 0.73, green: 0.94, blue: 1.00), secondary: Color(red: 0.42, green: 0.58, blue: 0.96)),
    ExportBackgroundPreset(id: "neon", label: "Neon", style: .gradient, primary: Color(red: 0.05, green: 1.00, blue: 0.54), secondary: Color(red: 1.00, green: 0.08, blue: 0.70)),
    ExportBackgroundPreset(id: "mango", label: "Mango", style: .gradient, primary: Color(red: 1.00, green: 0.78, blue: 0.20), secondary: Color(red: 1.00, green: 0.26, blue: 0.18)),
    ExportBackgroundPreset(id: "midnight", label: "Midnight", style: .gradient, primary: Color(red: 0.05, green: 0.07, blue: 0.18), secondary: Color(red: 0.00, green: 0.58, blue: 0.82)),
    ExportBackgroundPreset(id: "prism", label: "Prism", style: .gradient, primary: Color(red: 0.98, green: 0.16, blue: 0.38), secondary: Color(red: 0.18, green: 0.86, blue: 0.93)),
]

enum EditorPopover: String, Identifiable {
    case saveOptions

    var id: Self { self }
}

struct AnnotationColorPreset: Identifiable {
    let id: String
    let name: String
    let color: Color
}

let annotationColorPresets: [AnnotationColorPreset] = [
    AnnotationColorPreset(id: "black", name: "Black", color: .black),
    AnnotationColorPreset(id: "white", name: "White", color: .white),
    AnnotationColorPreset(id: "gray", name: "Gray", color: Color(red: 0.55, green: 0.55, blue: 0.57)),
    AnnotationColorPreset(id: "red", name: "Red", color: Color(red: 0.90, green: 0.20, blue: 0.18)),
    AnnotationColorPreset(id: "orange", name: "Orange", color: Color(red: 1.00, green: 0.58, blue: 0.16)),
    AnnotationColorPreset(id: "yellow", name: "Yellow", color: Color(red: 1.00, green: 0.84, blue: 0.20)),
    AnnotationColorPreset(id: "green", name: "Green", color: Color(red: 0.24, green: 0.70, blue: 0.34)),
    AnnotationColorPreset(id: "teal", name: "Teal", color: Color(red: 0.00, green: 0.72, blue: 0.72)),
    AnnotationColorPreset(id: "blue", name: "Blue", color: Color(red: 0.20, green: 0.52, blue: 0.96)),
    AnnotationColorPreset(id: "purple", name: "Purple", color: Color(red: 0.55, green: 0.35, blue: 0.90)),
    AnnotationColorPreset(id: "pink", name: "Pink", color: Color(red: 1.00, green: 0.36, blue: 0.66)),
    AnnotationColorPreset(id: "brown", name: "Brown", color: Color(red: 0.60, green: 0.40, blue: 0.24)),
]

func annotationColorsEqual(_ lhs: Color, _ rhs: Color) -> Bool {
    guard let left = NSColor(lhs).usingColorSpace(.deviceRGB),
          let right = NSColor(rhs).usingColorSpace(.deviceRGB) else {
        return false
    }

    var lr: CGFloat = 0, lg: CGFloat = 0, lb: CGFloat = 0, la: CGFloat = 0
    var rr: CGFloat = 0, rg: CGFloat = 0, rb: CGFloat = 0, ra: CGFloat = 0
    left.getRed(&lr, green: &lg, blue: &lb, alpha: &la)
    right.getRed(&rr, green: &rg, blue: &rb, alpha: &ra)

    return abs(lr - rr) < 0.0001 &&
        abs(lg - rg) < 0.0001 &&
        abs(lb - rb) < 0.0001 &&
        abs(la - ra) < 0.0001
}

private func redactionBrightness(for row: Int, column: Int, preset: RedactionBlurPreset) -> Double {
    let phase = Double((row + column) % preset.cycleLength)
    return min(0.82, preset.baseBrightness + phase * preset.contrastStep)
}

func drawCheckerboardRedaction(in context: GraphicsContext, rect: CGRect, preset: RedactionBlurPreset) {
    let blockSize = preset.previewBlockSize
    let cols = max(1, Int(ceil(rect.width / blockSize)))
    let rows = max(1, Int(ceil(rect.height / blockSize)))

    for row in 0..<rows {
        for col in 0..<cols {
            let blockRect = CGRect(
                x: rect.minX + CGFloat(col) * blockSize,
                y: rect.minY + CGFloat(row) * blockSize,
                width: min(blockSize, rect.maxX - (rect.minX + CGFloat(col) * blockSize)),
                height: min(blockSize, rect.maxY - (rect.minY + CGFloat(row) * blockSize))
            )
            guard blockRect.width > 0, blockRect.height > 0 else { continue }
            let brightness = redactionBrightness(for: row, column: col, preset: preset)
            context.fill(Path(blockRect), with: .color(Color(white: brightness, opacity: 1.0)))
        }
    }
}

func drawCheckerboardRedaction(in context: CGContext, rect: CGRect, preset: RedactionBlurPreset) {
    let blockSize = preset.exportBlockSize
    let cols = max(1, Int(ceil(rect.width / blockSize)))
    let rows = max(1, Int(ceil(rect.height / blockSize)))

    for row in 0..<rows {
        for col in 0..<cols {
            let blockRect = CGRect(
                x: rect.minX + CGFloat(col) * blockSize,
                y: rect.minY + CGFloat(row) * blockSize,
                width: min(blockSize, rect.maxX - (rect.minX + CGFloat(col) * blockSize)),
                height: min(blockSize, rect.maxY - (rect.minY + CGFloat(row) * blockSize))
            )
            guard blockRect.width > 0, blockRect.height > 0 else { continue }
            let brightness = redactionBrightness(for: row, column: col, preset: preset)
            context.setFillColor(CGColor(gray: brightness, alpha: 1.0))
            context.fill(blockRect)
        }
    }
}

/// The arrow's shape, which stays in sync with the bend and size sliders.
enum ArrowPreset: String, CaseIterable, Identifiable {
    case classic
    case tapered
    case curved
    case custom

    var id: Self { self }

    var label: String {
        switch self {
        case .classic: return "Classic"
        case .tapered: return "Tapered"
        case .curved: return "Curved"
        case .custom: return "Custom"
        }
    }
}

/// How an arrow is shaped. `bend` is how far the curve's midpoint sits from the chord's midpoint,
/// as a fraction of the chord length; positive bows to the left of travel. The width scales
/// multiply the stroke width at the tail and where the shaft meets the head.
struct ArrowShapeOptions: Equatable {
    var bend: CGFloat
    var startWidthScale: CGFloat
    var endWidthScale: CGFloat
    var headScale: CGFloat

    static let classic = ArrowShapeOptions(bend: 0, startWidthScale: 1, endWidthScale: 1, headScale: 1)
    static let tapered = ArrowShapeOptions(bend: 0, startWidthScale: 0.25, endWidthScale: 1, headScale: 1.2)
    static let curved = ArrowShapeOptions(bend: 0.2, startWidthScale: 1, endWidthScale: 1, headScale: 1)

    static let bendRange: ClosedRange<CGFloat> = -1...1
    static let widthScaleRange: ClosedRange<CGFloat> = 0...2
    static let headScaleRange: ClosedRange<CGFloat> = 0.5...3

    /// The preset these options match, or `.custom`.
    var preset: ArrowPreset {
        if matches(.classic) { return .classic }
        if matches(.tapered) { return .tapered }
        if matches(.curved) { return .curved }
        return .custom
    }

    private func matches(_ other: ArrowShapeOptions) -> Bool {
        abs(bend - other.bend) < 0.001
            && abs(startWidthScale - other.startWidthScale) < 0.001
            && abs(endWidthScale - other.endWidthScale) < 0.001
            && abs(headScale - other.headScale) < 0.001
    }
}

extension ArrowShapeOptions {
    init(preset: ArrowPreset) {
        switch preset {
        case .tapered: self = .tapered
        case .curved: self = .curved
        case .classic, .custom: self = .classic
        }
    }
}

/// An arrow's single filled outline, plus the curve's control point and bend handle.
struct ArrowOutline {
    var control: CGPoint
    var handle: CGPoint
    var points: [CGPoint]

    /// The outline as a path; fill it with the nonzero rule so a tight bend stays solid.
    var path: CGPath { LineGeometry.path(points) }
}

/// Distance from `point` to the segment from `start` to `end`.
func distanceToSegment(from point: CGPoint, start: CGPoint, end: CGPoint) -> CGFloat {
    let dx = end.x - start.x
    let dy = end.y - start.y
    let lengthSquared = dx * dx + dy * dy
    if lengthSquared <= 0.000001 {
        return hypot(point.x - start.x, point.y - start.y)
    }
    let t = max(0, min(1, ((point.x - start.x) * dx + (point.y - start.y) * dy) / lengthSquared))
    return hypot(point.x - (start.x + t * dx), point.y - (start.y + t * dy))
}

/// Arrow geometry shared by the preview, the export, and the Windows app through
/// `shared/screenshot/fixtures/arrow`. Points are y-down. Widths are in source-image pixels;
/// `scale` is the caller's units per image pixel, so the preview and the export draw the same
/// proportions. The arrow is one outline (tapered shaft, head, and tail cap), so a translucent
/// color fills it evenly.
enum ArrowGeometry {
    /// Shortest head, in image pixels, so thin arrows still read as arrows.
    static let minimumHeadLength: CGFloat = 12
    /// Head length as a multiple of the stroke width.
    static let headLengthPerWidth: CGFloat = 3.5
    /// Half the angle between the head's two wings.
    static let headSpread: CGFloat = .pi / 7
    private static let maximumHeadFraction: CGFloat = 0.45
    private static let shaftEndFraction: CGFloat = 0.8
    private static let shaftSamples = 24
    private static let capSegments = 8
    private static let cornerSegments = 6
    private static let trimIterations = 40

    static func headLength(width: CGFloat) -> CGFloat {
        max(minimumHeadLength, width * headLengthPerWidth)
    }

    /// The point on the curve at its midpoint, where the bend handle sits.
    static func handle(start: CGPoint, end: CGPoint, bend: CGFloat) -> CGPoint {
        let chord = sub(end, start)
        let n = rightNormal(normalize(chord, fallback: CGPoint(x: 1, y: 0)))
        return sub(lerp(start, end, 0.5), mul(n, bend * length(chord)))
    }

    /// The bend that puts the curve's midpoint nearest `handle`.
    static func bend(start: CGPoint, end: CGPoint, handle: CGPoint) -> CGFloat {
        let chord = sub(end, start)
        let chordLength = length(chord)
        guard chordLength >= 0.001 else { return 0 }
        let n = rightNormal(mul(chord, 1 / chordLength))
        let offset = sub(handle, lerp(start, end, 0.5))
        let bend = -(offset.x * n.x + offset.y * n.y) / chordLength
        return min(max(bend, ArrowShapeOptions.bendRange.lowerBound), ArrowShapeOptions.bendRange.upperBound)
    }

    static func control(start: CGPoint, end: CGPoint, bend: CGFloat) -> CGPoint {
        sub(mul(handle(start: start, end: end, bend: bend), 2), lerp(start, end, 0.5))
    }

    static func pointOnCurve(_ start: CGPoint, _ control: CGPoint, _ end: CGPoint, _ t: CGFloat) -> CGPoint {
        let u = 1 - t
        return CGPoint(
            x: u * u * start.x + 2 * u * t * control.x + t * t * end.x,
            y: u * u * start.y + 2 * u * t * control.y + t * t * end.y
        )
    }

    static func build(
        start: CGPoint,
        end: CGPoint,
        width: CGFloat,
        options: ArrowShapeOptions,
        startCap: StrokeCap,
        join: StrokeJoin,
        scale: CGFloat = 1
    ) -> ArrowOutline {
        let chord = sub(end, start)
        let chordLength = length(chord)
        let handle = handle(start: start, end: end, bend: options.bend)
        let control = sub(mul(handle, 2), lerp(start, end, 0.5))
        guard chordLength >= 0.001 else {
            return ArrowOutline(control: control, handle: handle, points: [])
        }
        let u = mul(chord, 1 / chordLength)

        let head = min(options.headScale * headLength(width: width) * scale, maximumHeadFraction * chordLength)
        let tipDirection = normalize(sub(end, control), fallback: u)
        let angle = atan2(tipDirection.y, tipDirection.x)
        let wingRight = CGPoint(x: end.x - head * cos(angle - headSpread), y: end.y - head * sin(angle - headSpread))
        let wingLeft = CGPoint(x: end.x - head * cos(angle + headSpread), y: end.y - head * sin(angle + headSpread))

        // The shaft stops where the curve is as far from the tip as the head's base.
        let baseDistance = head * cos(headSpread)
        var lo: CGFloat = 0
        var hi: CGFloat = 1
        for _ in 0..<trimIterations {
            let m = (lo + hi) / 2
            if length(sub(pointOnCurve(start, control, end, m), end)) > baseDistance {
                lo = m
            } else {
                hi = m
            }
        }
        let trim = (lo + hi) / 2

        let startHalf = width * options.startWidthScale / 2 * scale
        let endHalf = min(width * options.endWidthScale / 2 * scale, shaftEndFraction * head * sin(headSpread))
        var left: [CGPoint] = []
        var right: [CGPoint] = []
        for i in 0...shaftSamples {
            let f = CGFloat(i) / CGFloat(shaftSamples)
            let t = trim * f
            let p = pointOnCurve(start, control, end, t)
            let n = rightNormal(normalize(tangent(start, control, end, t), fallback: u))
            let half = startHalf + (endHalf - startHalf) * f
            left.append(sub(p, mul(n, half)))
            right.append(add(p, mul(n, half)))
        }

        let size = min(width / 2 * scale, 0.2 * head)
        var points = left
        points += corner(previous: left[left.count - 1], vertex: wingLeft, next: end, size: size, join: join)
        points += corner(previous: wingLeft, vertex: end, next: wingRight, size: size, join: join)
        points += corner(previous: end, vertex: wingRight, next: right[right.count - 1], size: size, join: join)
        points += right.reversed()

        let tailTangent = normalize(tangent(start, control, end, 0), fallback: u)
        let tailNormal = rightNormal(tailTangent)
        switch startCap {
        case .round:
            for j in 1..<capSegments {
                let theta = CGFloat.pi * CGFloat(j) / CGFloat(capSegments)
                points.append(add(start, add(
                    mul(tailNormal, startHalf * cos(theta)),
                    mul(tailTangent, -startHalf * sin(theta))
                )))
            }
        case .square:
            let back = mul(tailTangent, -startHalf)
            points.append(add(right[0], back))
            points.append(add(left[0], back))
        case .butt:
            break
        }
        return ArrowOutline(control: control, handle: handle, points: points)
    }

    /// Distance from `point` to the arrow's curve, for hit testing.
    static func distance(from point: CGPoint, start: CGPoint, end: CGPoint, bend: CGFloat) -> CGFloat {
        let control = control(start: start, end: end, bend: bend)
        var previous = start
        var best = CGFloat.greatestFiniteMagnitude
        for i in 1...32 {
            let next = pointOnCurve(start, control, end, CGFloat(i) / 32)
            best = min(best, distanceToSegment(from: point, start: previous, end: next))
            previous = next
        }
        return best
    }

    private static func corner(previous: CGPoint, vertex: CGPoint, next: CGPoint, size: CGFloat, join: StrokeJoin) -> [CGPoint] {
        guard join != .miter, size > 0 else { return [vertex] }
        let toPrevious = sub(previous, vertex)
        let toNext = sub(next, vertex)
        let k = min(size, min(0.45 * length(toPrevious), 0.45 * length(toNext)))
        let a = add(vertex, mul(normalize(toPrevious, fallback: .zero), k))
        let b = add(vertex, mul(normalize(toNext, fallback: .zero), k))
        if join == .bevel {
            return [a, b]
        }
        return (0...cornerSegments).map { pointOnCurve(a, vertex, b, CGFloat($0) / CGFloat(cornerSegments)) }
    }

    private static func tangent(_ start: CGPoint, _ control: CGPoint, _ end: CGPoint, _ t: CGFloat) -> CGPoint {
        CGPoint(
            x: 2 * (1 - t) * (control.x - start.x) + 2 * t * (end.x - control.x),
            y: 2 * (1 - t) * (control.y - start.y) + 2 * t * (end.y - control.y)
        )
    }

    /// The unit normal to the right of travel in a y-down space.
    private static func rightNormal(_ direction: CGPoint) -> CGPoint {
        CGPoint(x: -direction.y, y: direction.x)
    }

    private static func normalize(_ v: CGPoint, fallback: CGPoint) -> CGPoint {
        let vectorLength = length(v)
        return vectorLength > 1e-9 ? mul(v, 1 / vectorLength) : fallback
    }

    private static func length(_ v: CGPoint) -> CGFloat {
        (v.x * v.x + v.y * v.y).squareRoot()
    }

    private static func add(_ a: CGPoint, _ b: CGPoint) -> CGPoint { CGPoint(x: a.x + b.x, y: a.y + b.y) }

    private static func sub(_ a: CGPoint, _ b: CGPoint) -> CGPoint { CGPoint(x: a.x - b.x, y: a.y - b.y) }

    private static func mul(_ a: CGPoint, _ k: CGFloat) -> CGPoint { CGPoint(x: a.x * k, y: a.y * k) }

    private static func lerp(_ a: CGPoint, _ b: CGPoint, _ t: CGFloat) -> CGPoint {
        CGPoint(x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t)
    }
}

/// A straight line as one filled outline, shared with Windows through
/// `shared/screenshot/fixtures/line`. Points are y-down; `scale` is the caller's units per image
/// pixel. Filling the outline (rather than stroking the line) lets each end take its own cap and
/// lets the line carry an outline of its own, like an arrow.
enum LineGeometry {
    private static let capSegments = 8

    static func outline(start: CGPoint, end: CGPoint, width: CGFloat, startCap: StrokeCap, endCap: StrokeCap, scale: CGFloat = 1) -> [CGPoint] {
        let dx = end.x - start.x
        let dy = end.y - start.y
        let length = (dx * dx + dy * dy).squareRoot()
        guard length >= 0.001 else { return [] }
        let u = CGPoint(x: dx * (1 / length), y: dy * (1 / length))
        let n = CGPoint(x: -u.y, y: u.x)
        let half = width / 2 * scale
        var points = [
            CGPoint(x: start.x - n.x * half, y: start.y - n.y * half),
            CGPoint(x: end.x - n.x * half, y: end.y - n.y * half),
        ]
        points += cap(around: end, outward: u, normal: n, half: half, cap: endCap)
        points.append(CGPoint(x: end.x + n.x * half, y: end.y + n.y * half))
        points.append(CGPoint(x: start.x + n.x * half, y: start.y + n.y * half))
        points += cap(around: start, outward: CGPoint(x: -u.x, y: -u.y), normal: CGPoint(x: -n.x, y: -n.y), half: half, cap: startCap)
        return points
    }

    /// Points around one end between the line's two edges, going away along `outward`. `normal`
    /// points from the edge the outline arrives on to the one it leaves by.
    private static func cap(around center: CGPoint, outward: CGPoint, normal: CGPoint, half: CGFloat, cap: StrokeCap) -> [CGPoint] {
        switch cap {
        case .round:
            return (1..<capSegments).map { j in
                let theta = CGFloat.pi * CGFloat(j) / CGFloat(capSegments)
                let along = -half * cos(theta)
                let away = half * sin(theta)
                return CGPoint(
                    x: center.x + (normal.x * along + outward.x * away),
                    y: center.y + (normal.y * along + outward.y * away)
                )
            }
        case .square:
            return [
                CGPoint(x: center.x - normal.x * half + outward.x * half, y: center.y - normal.y * half + outward.y * half),
                CGPoint(x: center.x + normal.x * half + outward.x * half, y: center.y + normal.y * half + outward.y * half),
            ]
        case .butt:
            return []
        }
    }

    /// A closed path through `points`; fill it with the nonzero rule.
    static func path(_ points: [CGPoint]) -> CGPath {
        let path = CGMutablePath()
        guard points.count >= 3 else { return path }
        path.addLines(between: points)
        path.closeSubpath()
        return path
    }
}

/// Freehand pen strokes as one filled outline, shared with Windows through
/// `shared/screenshot/fixtures/pen`. A port of perfect-freehand's `getStroke` (MIT, © 2021 Stephen
/// Ruiz Ltd, github.com/steveruizok/perfect-freehand at 176e00f) with fixed options: size = the
/// stroke width, thinning 0.3, smoothing 0.5, streamline 0.5, simulated pressure, linear easing,
/// both ends tapered, and the stroke treated as complete. Lengths use `squareRoot()` rather than
/// `hypot`, and the cap loops keep the source's accumulating steps, so every platform produces the
/// same points. Work in image pixels: the thresholds are in the stroke's own units.
enum PenGeometry {
    private static let thinning: CGFloat = 0.3
    private static let smoothing: CGFloat = 0.5
    private static let streamline: CGFloat = 0.5
    private static let taperPerWidth: CGFloat = 3
    private static let rateOfPressureChange: CGFloat = 0.275
    private static let fixedPi: CGFloat = .pi + 0.0001
    private static let startCapSegments: CGFloat = 13
    private static let cornerCapSegments: CGFloat = 13
    private static let endNoiseThreshold: CGFloat = 3
    private static let minStreamlineT: CGFloat = 0.15
    private static let streamlineTRange: CGFloat = 0.85
    private static let minRadius: CGFloat = 0.01
    private static let defaultFirstPressure: CGFloat = 0.25
    private static let defaultPressure: CGFloat = 0.5

    private struct StrokePoint {
        var point: CGPoint
        var pressure: CGFloat
        var vector: CGPoint
        var distance: CGFloat
        var runningLength: CGFloat
    }

    static func outline(_ input: [CGPoint], width: CGFloat) -> [CGPoint] {
        let size = width
        let points = strokePoints(input, size: size)
        guard !points.isEmpty, size > 0 else { return [] }

        let total = points[points.count - 1].runningLength
        let taperStart = size * taperPerWidth
        let taperEnd = size * taperPerWidth
        let minDistance = pow(size * smoothing, 2)
        var left: [CGPoint] = []
        var right: [CGPoint] = []

        var prevPressure = points[0].pressure
        for curr in points.prefix(10) {
            prevPressure = (prevPressure + simulatePressure(prevPressure, distance: curr.distance, size: size)) / 2
        }

        var radius = strokeRadius(size, pressure: points[points.count - 1].pressure)
        var firstRadius: CGFloat?
        var prevVector = points[0].vector
        var prevLeft = points[0].point
        var prevRight = prevLeft
        var tempLeft = prevLeft
        var tempRight = prevRight
        var prevSharp = false

        for i in points.indices {
            let sp = points[i]
            let p = sp.point
            let vector = sp.vector
            let running = sp.runningLength
            let isLast = i == points.count - 1
            if !isLast && total - running < endNoiseThreshold {
                continue
            }

            let pressure = simulatePressure(prevPressure, distance: sp.distance, size: size)
            radius = strokeRadius(size, pressure: pressure)
            if firstRadius == nil {
                firstRadius = radius
            }

            var startStrength: CGFloat = 1
            if running < taperStart {
                let x = running / taperStart
                startStrength = x * (2 - x)
            }
            var endStrength: CGFloat = 1
            if total - running < taperEnd {
                let x = (total - running) / taperEnd
                endStrength = (x - 1) * (x - 1) * (x - 1) + 1
            }
            radius = max(minRadius, radius * min(startStrength, endStrength))

            let nextVector = (!isLast ? points[i + 1] : sp).vector
            let nextDpr = !isLast ? dpr(vector, nextVector) : 1.0
            let prevDpr = dpr(vector, prevVector)
            let isSharp = prevDpr < 0 && !prevSharp
            let isNextSharp = nextDpr < 0

            if isSharp || isNextSharp {
                let offset = mul(per(prevVector), radius)
                let step = 1 / cornerCapSegments
                var t: CGFloat = 0
                while t <= 1 {
                    tempLeft = rotate(sub(p, offset), around: p, by: fixedPi * t)
                    left.append(tempLeft)
                    tempRight = rotate(add(p, offset), around: p, by: fixedPi * -t)
                    right.append(tempRight)
                    t += step
                }
                prevLeft = tempLeft
                prevRight = tempRight
                if isNextSharp {
                    prevSharp = true
                }
                continue
            }

            prevSharp = false
            if isLast {
                let offset = mul(per(vector), radius)
                left.append(sub(p, offset))
                right.append(add(p, offset))
                continue
            }

            let offset = mul(per(lerp(nextVector, vector, nextDpr)), radius)
            tempLeft = sub(p, offset)
            if i <= 1 || dist2(prevLeft, tempLeft) > minDistance {
                left.append(tempLeft)
                prevLeft = tempLeft
            }
            tempRight = add(p, offset)
            if i <= 1 || dist2(prevRight, tempRight) > minDistance {
                right.append(tempRight)
                prevRight = tempRight
            }
            prevPressure = pressure
            prevVector = vector
        }

        let first = points[0].point
        if points.count == 1 {
            // A dot: the stroke is complete, so even a tapered one draws a circle.
            let start = add(first, mul(unit(per(sub(first, add(first, CGPoint(x: 1, y: 1))))), -(firstRadius ?? radius)))
            var dot: [CGPoint] = []
            let step = 1 / startCapSegments
            var t = step
            while t <= 1 {
                dot.append(rotate(start, around: first, by: fixedPi * 2 * t))
                t += step
            }
            return dot
        }

        // Both ends taper, so there is no start cap and the end is the last point.
        return left + [points[points.count - 1].point] + right.reversed()
    }

    /// A smooth closed path through the outline: quadratic curves through each pair of points'
    /// midpoints, as perfect-freehand recommends, so the edge does not look faceted.
    static func smoothPath(_ outline: [CGPoint]) -> CGPath {
        let path = CGMutablePath()
        guard outline.count >= 3 else { return path }
        path.move(to: outline[0])
        for i in outline.indices {
            let point = outline[i]
            let next = outline[(i + 1) % outline.count]
            path.addQuadCurve(to: lerp(point, next, 0.5), control: point)
        }
        path.closeSubpath()
        return path
    }

    private static func strokePoints(_ input: [CGPoint], size: CGFloat) -> [StrokePoint] {
        guard !input.isEmpty else { return [] }
        let t = minStreamlineT + (1 - streamline) * streamlineTRange
        var pts = input
        if pts.count == 2 {
            let last = pts[1]
            pts = [pts[0]] + (1..<5).map { lerp(pts[0], last, CGFloat($0) / 4) }
        }
        if pts.count == 1 {
            pts.append(add(pts[0], CGPoint(x: 1, y: 1)))
        }

        var result = [StrokePoint(point: pts[0], pressure: defaultFirstPressure, vector: CGPoint(x: 1, y: 1), distance: 0, runningLength: 0)]
        var prev = result[0]
        var reachedMinimum = false
        var running: CGFloat = 0
        let lastIndex = pts.count - 1
        for i in 1..<pts.count {
            let p = i == lastIndex ? pts[i] : lerp(prev.point, pts[i], t)
            if p.x == prev.point.x && p.y == prev.point.y {
                continue
            }
            let distance = length(sub(p, prev.point))
            running += distance
            if i < lastIndex && !reachedMinimum {
                if running < size {
                    continue
                }
                reachedMinimum = true
            }
            prev = StrokePoint(point: p, pressure: defaultPressure, vector: unit(sub(prev.point, p)), distance: distance, runningLength: running)
            result.append(prev)
        }
        result[0].vector = result.count > 1 ? result[1].vector : .zero
        return result
    }

    private static func simulatePressure(_ prev: CGFloat, distance: CGFloat, size: CGFloat) -> CGFloat {
        let sp = min(1, distance / size)
        let rp = min(1, 1 - sp)
        return min(1, prev + (rp - prev) * (sp * rateOfPressureChange))
    }

    private static func strokeRadius(_ size: CGFloat, pressure: CGFloat) -> CGFloat {
        size * (0.5 - thinning * (0.5 - pressure))
    }

    private static func rotate(_ a: CGPoint, around c: CGPoint, by r: CGFloat) -> CGPoint {
        let s = sin(r)
        let co = cos(r)
        let px = a.x - c.x
        let py = a.y - c.y
        return CGPoint(x: px * co - py * s + c.x, y: px * s + py * co + c.y)
    }

    private static func length(_ a: CGPoint) -> CGFloat { (a.x * a.x + a.y * a.y).squareRoot() }

    private static func unit(_ a: CGPoint) -> CGPoint {
        let n = length(a)
        return CGPoint(x: a.x / n, y: a.y / n)
    }

    private static func per(_ a: CGPoint) -> CGPoint { CGPoint(x: a.y, y: -a.x) }

    private static func dpr(_ a: CGPoint, _ b: CGPoint) -> CGFloat { a.x * b.x + a.y * b.y }

    private static func dist2(_ a: CGPoint, _ b: CGPoint) -> CGFloat {
        let dx = a.x - b.x
        let dy = a.y - b.y
        return dx * dx + dy * dy
    }

    private static func lerp(_ a: CGPoint, _ b: CGPoint, _ t: CGFloat) -> CGPoint {
        CGPoint(x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t)
    }

    private static func add(_ a: CGPoint, _ b: CGPoint) -> CGPoint { CGPoint(x: a.x + b.x, y: a.y + b.y) }

    private static func sub(_ a: CGPoint, _ b: CGPoint) -> CGPoint { CGPoint(x: a.x - b.x, y: a.y - b.y) }

    private static func mul(_ a: CGPoint, _ k: CGFloat) -> CGPoint { CGPoint(x: a.x * k, y: a.y * k) }
}

/// Stroke widths are stored in source-image pixels, the unit the Windows editor and the shared
/// fixtures use.
enum AnnotationStrokeMath {
    static let widthRange: ClosedRange<CGFloat> = 1...40

    /// The starting width for an image. Before widths were stored in image pixels the default was
    /// 4 at a 900-pixel-wide reference, so this keeps that look on large captures.
    static func defaultWidth(forImagePixelWidth width: CGFloat) -> CGFloat {
        let scaled = (4 * max(1, width / 900)).rounded()
        return min(max(scaled, widthRange.lowerBound), widthRange.upperBound)
    }
}
