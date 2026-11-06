import AppKit
import SwiftUI

/// Gradient fill and shadow for rectangles and ellipses, copied by value into each annotation.
/// Windows models the same values; see plans/editor-themes.md.
struct ShapeEffects {
    static let shadowBlurRange: ClosedRange<CGFloat> = 1...40
    /// How far a shadow falls, as a fraction of its blur.
    static let shadowOffsetRatio: CGFloat = 0.35
    static let defaultShadowColor = Color(.sRGB, red: 0, green: 0, blue: 0, opacity: 89.0 / 255)

    var isGradientFill = false
    var gradientEndColor = Color(red: 0.20, green: 0.52, blue: 0.96)
    var gradientDirectionDegrees: Double = 45
    var hasShadow = false
    var shadowColor = ShapeEffects.defaultShadowColor
    var shadowBlur: CGFloat = 12

    var drawsShadow: Bool {
        hasShadow && shadowBlur > 0
    }
}

extension EditTool {
    var supportsShapeEffects: Bool {
        self == .rectangle || self == .circle
    }
}

/// Draws a rectangle or ellipse with its effects. The export is authoritative: Core Graphics takes
/// a blur whose Gaussian standard deviation is half its value, and the preview is matched to that.
enum ShapeEffectsRenderer {
    /// SwiftUI's shadow radius is the standard deviation itself.
    static let previewRadiusPerBlur: CGFloat = 0.5

    // MARK: - Preview

    /// `effectScale` converts stored shadow units to the context's points.
    static func drawPreview(
        _ annotation: ScreenshotAnnotation,
        in context: GraphicsContext,
        rect: CGRect,
        lineWidth: CGFloat,
        effectScale: CGFloat
    ) {
        let effects = annotation.shapeEffects
        guard effects.drawsShadow else {
            drawPreviewShape(annotation, in: context, rect: rect, lineWidth: lineWidth)
            return
        }

        let blur = effects.shadowBlur * effectScale
        var shadowed = context
        // Added before any rotation, so the shadow keeps falling straight down.
        shadowed.addFilter(.shadow(
            color: effects.shadowColor,
            radius: blur * previewRadiusPerBlur,
            x: 0,
            y: blur * ShapeEffects.shadowOffsetRatio
        ))
        // A layer casts one shadow from the fill and stroke together.
        shadowed.drawLayer { layer in
            drawPreviewShape(annotation, in: layer, rect: rect, lineWidth: lineWidth)
        }
    }

    private static func drawPreviewShape(
        _ annotation: ScreenshotAnnotation,
        in context: GraphicsContext,
        rect: CGRect,
        lineWidth: CGFloat
    ) {
        var context = context
        var rect = rect
        if annotation.isRotated {
            context.translateBy(x: rect.midX, y: rect.midY)
            context.rotate(by: .radians(annotation.rotation))
            rect = CGRect(x: -rect.width / 2, y: -rect.height / 2, width: rect.width, height: rect.height)
        }

        let path = annotation.tool == .circle ? Path(ellipseIn: rect) : Path(rect)
        if annotation.fillColor != .clear {
            if annotation.shapeEffects.isGradientFill {
                let line = gradientLine(degrees: annotation.shapeEffects.gradientDirectionDegrees, in: rect)
                context.fill(path, with: .linearGradient(
                    Gradient(colors: [annotation.fillColor, annotation.shapeEffects.gradientEndColor]),
                    startPoint: line.start,
                    endPoint: line.end
                ))
            } else {
                context.fill(path, with: .color(annotation.fillColor))
            }
        }
        context.stroke(path, with: .color(annotation.color), lineWidth: lineWidth)
    }

    // MARK: - Export

    /// Runs `draw` so that everything it paints casts one shadow. `scale` converts stored shadow
    /// units to the bitmap's pixels.
    static func withExportShadow(_ effects: ShapeEffects, scale: CGFloat, in context: CGContext, _ draw: () -> Void) {
        guard effects.drawsShadow else {
            draw()
            return
        }

        let blur = effects.shadowBlur * scale
        context.saveGState()
        // Export contexts are y-up, and shadow offsets ignore rotation.
        context.setShadow(
            offset: CGSize(width: 0, height: -blur * ShapeEffects.shadowOffsetRatio),
            blur: blur,
            color: NSColor(effects.shadowColor).cgColor
        )
        context.beginTransparencyLayer(auxiliaryInfo: nil)
        draw()
        context.endTransparencyLayer()
        context.restoreGState()
    }

    /// `rect` is in the y-up space of `context`, which already has the shape's rotation applied.
    /// The stroke color and width are the context's current ones.
    static func drawExportShape(_ annotation: ScreenshotAnnotation, in context: CGContext, rect: CGRect) {
        let path = annotation.tool == .circle
            ? CGPath(ellipseIn: rect, transform: nil)
            : CGPath(rect: rect, transform: nil)

        if annotation.fillColor != .clear {
            if annotation.shapeEffects.isGradientFill {
                fillExportGradient(annotation, path: path, rect: rect, in: context)
            } else {
                context.setFillColor(NSColor(annotation.fillColor).cgColor)
                context.addPath(path)
                context.fillPath()
            }
        }

        context.addPath(path)
        context.strokePath()
    }

    private static func fillExportGradient(_ annotation: ScreenshotAnnotation, path: CGPath, rect: CGRect, in context: CGContext) {
        let colors = [NSColor(annotation.fillColor).cgColor, NSColor(annotation.shapeEffects.gradientEndColor).cgColor] as CFArray
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let gradient = CGGradient(colorsSpace: space, colors: colors, locations: [0, 1]) else {
            return
        }
        let line = gradientLine(degrees: annotation.shapeEffects.gradientDirectionDegrees, in: rect)
        context.saveGState()
        context.addPath(path)
        context.clip()
        // gradientLine measures from the top, and this context's y runs up.
        context.drawLinearGradient(
            gradient,
            start: CGPoint(x: line.start.x, y: rect.minY + rect.maxY - line.start.y),
            end: CGPoint(x: line.end.x, y: rect.minY + rect.maxY - line.end.y),
            options: [.drawsBeforeStartLocation, .drawsAfterEndLocation]
        )
        context.restoreGState()
    }

    // MARK: - Geometry

    /// Where a gradient starts and ends in `rect`'s own coordinates, with y running down.
    private static func gradientLine(degrees: Double, in rect: CGRect) -> (start: CGPoint, end: CGPoint) {
        let endpoints = ScreenshotGradientMath.endpoints(
            degrees: degrees,
            width: Double(rect.width),
            height: Double(rect.height)
        )
        return (
            CGPoint(x: rect.minX + endpoints.start.x * rect.width, y: rect.minY + endpoints.start.y * rect.height),
            CGPoint(x: rect.minX + endpoints.end.x * rect.width, y: rect.minY + endpoints.end.y * rect.height)
        )
    }
}
