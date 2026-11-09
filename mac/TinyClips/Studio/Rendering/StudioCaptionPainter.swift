import CoreGraphics
import CoreText
import Foundation

/// A caption drawn on a transparent bitmap, and where its bottom-left corner goes on the canvas,
/// counted up from the bottom as Core Image counts.
struct StudioCaptionPicture {
    let image: CGImage
    let originY: Double
}

/// Draws a caption with Core Text: section 15.4 of the project format. Windows draws the same
/// picture with its own text system, so lines may wrap at different words.
enum StudioCaptionPainter {
    /// `language` is a BCP 47 tag, which helps choose a font for what the system's has no letters for.
    static func picture(
        text: String,
        overlay: StudioCaptionOverlay,
        language: String?,
        canvasWidth: Int,
        canvasHeight: Int
    ) -> StudioCaptionPicture? {
        guard canvasWidth > 0, canvasHeight > 0, !text.isEmpty else { return nil }
        let width = Double(canvasWidth)
        let height = Double(canvasHeight)
        let style = StudioCaptionStyle(overlay: overlay, canvasWidth: width, canvasHeight: height)

        let font = CTFontCreateUIFontForLanguage(
            style.isBold ? .emphasizedSystem : .system,
            CGFloat(style.textHeight),
            language as CFString?
        ) ?? CTFontCreateWithName("Helvetica" as CFString, CGFloat(style.textHeight), nil)
        var alignment = CTTextAlignment.center
        let paragraph = withUnsafePointer(to: &alignment) { pointer in
            var setting = CTParagraphStyleSetting(
                spec: .alignment,
                valueSize: MemoryLayout<CTTextAlignment>.size,
                value: pointer
            )
            return CTParagraphStyleCreate(&setting, 1)
        }
        var attributes: [NSAttributedString.Key: Any] = [
            NSAttributedString.Key(kCTFontAttributeName as String): font,
            NSAttributedString.Key(kCTParagraphStyleAttributeName as String): paragraph,
            NSAttributedString.Key(kCTForegroundColorFromContextAttributeName as String): true,
        ]
        if let language {
            attributes[NSAttributedString.Key(kCTLanguageAttributeName as String)] = language
        }

        let string = NSAttributedString(string: text, attributes: attributes)
        let framesetter = CTFramesetterCreateWithAttributedString(string)
        let fitted = CTFramesetterSuggestFrameSizeWithConstraints(
            framesetter,
            CFRange(location: 0, length: 0),
            nil,
            CGSize(width: style.longestLine, height: .greatestFiniteMagnitude),
            nil
        )
        let textHeight = Double(fitted.height.rounded(.up)) + 1

        // Counted down from the top of the canvas, as section 15.4 is, and whole pixels around
        // all that is drawn. What falls off the canvas is left out.
        let reach = style.reach.rounded(.up) + 2
        let textTop = style.topOfText(position: overlay.position, textHeight: textHeight, canvasHeight: height)
        let top = max(0, (textTop - reach).rounded(.down))
        let bottom = min(height, (textTop + textHeight + reach).rounded(.up))
        guard bottom > top, let context = CGContext(
            data: nil,
            width: canvasWidth,
            height: Int(bottom - top),
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue
        ) else {
            return nil
        }

        // In the bitmap, y counts up from its own bottom edge.
        let box = CGRect(
            x: (width - style.longestLine) / 2,
            y: bottom - textTop - textHeight,
            width: style.longestLine,
            height: textHeight
        )
        let frame = CTFramesetterCreateFrame(
            framesetter,
            CFRange(location: 0, length: 0),
            CGPath(rect: box, transform: nil),
            nil
        )
        draw(frame: frame, in: context, box: box, style: style)
        guard let image = context.makeImage() else { return nil }
        return StudioCaptionPicture(image: image, originY: height - bottom)
    }

    /// From the back: the panel, the shadow, the outline, and the letters.
    private static func draw(frame: CTFrame, in context: CGContext, box: CGRect, style: StudioCaptionStyle) {
        context.setShouldAntialias(true)
        context.setLineJoin(.round)
        context.setLineCap(.round)

        if style.panelReach > 0 {
            let lines = linesBox(of: frame, in: box)
            context.setFillColor(color(style.panel))
            context.fill(lines.insetBy(dx: -CGFloat(style.panelReach), dy: -CGFloat(style.panelReach)))
        }

        let strokeWidth = CGFloat(style.outlineWidth * 2)

        // A paint that is not solid is laid on as one sheet, or it would darken wherever two
        // letters' outlines, or a shadow's own outline and letters, overlap.
        if style.shadowOffset > 0 {
            let offset = CGFloat(style.shadowOffset)
            let paint = color(StudioCaptionPaint(red: style.shadow.red, green: style.shadow.green, blue: style.shadow.blue, opacity: 1))
            onOneSheet(context, opacity: style.shadow.opacity) {
                context.translateBy(x: offset, y: -offset)
                context.setFillColor(paint)
                context.setStrokeColor(paint)
                context.setLineWidth(strokeWidth)
                context.setTextDrawingMode(strokeWidth > 0 ? .fillStroke : .fill)
                CTFrameDraw(frame, context)
            }
        }

        if style.outlineWidth > 0 {
            let paint = color(StudioCaptionPaint(red: style.outline.red, green: style.outline.green, blue: style.outline.blue, opacity: 1))
            onOneSheet(context, opacity: style.outline.opacity) {
                context.setStrokeColor(paint)
                context.setLineWidth(strokeWidth)
                context.setTextDrawingMode(.stroke)
                CTFrameDraw(frame, context)
            }
        }

        context.setFillColor(color(style.text))
        context.setTextDrawingMode(.fill)
        CTFrameDraw(frame, context)
    }

    private static func onOneSheet(_ context: CGContext, opacity: Double, draw: () -> Void) {
        context.saveGState()
        context.setAlpha(CGFloat(opacity))
        context.beginTransparencyLayer(auxiliaryInfo: nil)
        draw()
        context.endTransparencyLayer()
        context.restoreGState()
    }

    /// The box the lines themselves take, without the width of the room they were given.
    private static func linesBox(of frame: CTFrame, in box: CGRect) -> CGRect {
        guard let lines = CTFrameGetLines(frame) as? [CTLine], !lines.isEmpty else { return box }
        var origins = [CGPoint](repeating: .zero, count: lines.count)
        CTFrameGetLineOrigins(frame, CFRange(location: 0, length: 0), &origins)
        var result = CGRect.null
        for (line, origin) in zip(lines, origins) {
            var ascent: CGFloat = 0
            var descent: CGFloat = 0
            var leading: CGFloat = 0
            let width = CTLineGetTypographicBounds(line, &ascent, &descent, &leading)
            let lineBox = CGRect(
                x: box.minX + origin.x,
                y: box.minY + origin.y - descent,
                width: CGFloat(width) - CGFloat(CTLineGetTrailingWhitespaceWidth(line)),
                height: ascent + descent
            )
            result = result.union(lineBox)
        }
        return result
    }

    /// In the bitmap's own colour space, so that a colour of section 15.4 is not converted on the way in.
    private static func color(_ paint: StudioCaptionPaint) -> CGColor {
        let components = [
            CGFloat(paint.red) / 255,
            CGFloat(paint.green) / 255,
            CGFloat(paint.blue) / 255,
            CGFloat(paint.opacity),
        ]
        return components.withUnsafeBufferPointer { values in
            CGColor(colorSpace: CGColorSpaceCreateDeviceRGB(), components: values.baseAddress!)
        } ?? .black
    }
}
