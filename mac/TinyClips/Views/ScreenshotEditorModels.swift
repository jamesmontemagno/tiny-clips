import AppKit
import SwiftUI

let textSystemFontFamily = "System"

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
        case .blur: return "Redact"
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
    var redactionBlurPreset: RedactionBlurPreset = .medium
    var arrowStyle: ArrowStyle = .straight
}

typealias LinePoints = (start: CGPoint, end: CGPoint)

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

enum ArrowStyle: String, CaseIterable, Identifiable {
    case straight
    case curvedLeft
    case curvedRight

    var id: Self { self }

    var label: String {
        switch self {
        case .straight: return "Straight"
        case .curvedLeft: return "Curved Left"
        case .curvedRight: return "Curved Right"
        }
    }

    var curvatureSign: CGFloat {
        switch self {
        case .straight: return 0
        case .curvedLeft: return -1
        case .curvedRight: return 1
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

enum ScreenshotEditorZoomMath {
    static let minimumScale: CGFloat = 0.25
    static let maximumScale: CGFloat = 4
    static let presets: [CGFloat] = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4]

    static func clamp(_ scale: CGFloat) -> CGFloat {
        min(maximumScale, max(minimumScale, scale))
    }

    static func steppedScale(from scale: CGFloat, direction: Int) -> CGFloat {
        let current = clamp(scale)
        if direction > 0 {
            return presets.first(where: { $0 > current + 0.001 }) ?? maximumScale
        }
        return presets.reversed().first(where: { $0 < current - 0.001 }) ?? minimumScale
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

enum ScreenshotEditorCropMath {
    static func pixelRect(for normalizedRect: CGRect, imageSize: CGSize) -> CGRect? {
        guard imageSize.width > 0, imageSize.height > 0 else { return nil }

        let left = max(0, min(imageSize.width, normalizedRect.minX * imageSize.width))
        let top = max(0, min(imageSize.height, normalizedRect.minY * imageSize.height))
        let right = max(0, min(imageSize.width, normalizedRect.maxX * imageSize.width))
        let bottom = max(0, min(imageSize.height, normalizedRect.maxY * imageSize.height))
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

func arrowControlPoint(start: CGPoint, end: CGPoint, style: ArrowStyle) -> CGPoint {
    let mid = CGPoint(x: (start.x + end.x) * 0.5, y: (start.y + end.y) * 0.5)
    guard style != .straight else { return mid }
    let dx = end.x - start.x
    let dy = end.y - start.y
    let length = max(1, hypot(dx, dy))
    let normal = CGPoint(x: -dy / length, y: dx / length)
    let magnitude = max(20, length * 0.25) * style.curvatureSign
    return CGPoint(x: mid.x + normal.x * magnitude, y: mid.y + normal.y * magnitude)
}
