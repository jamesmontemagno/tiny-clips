import AppKit
import SwiftUI

/// The annotation types a theme styles, each with its own section. Redactions and emoji have none,
/// so a theme can never weaken a redaction.
enum EditorThemeToolKind: Hashable, CaseIterable {
    case rectangle
    case ellipse
    case arrow
    case line
    case pen
    case text
    case number

    init?(tool: EditTool) {
        switch tool {
        case .rectangle: self = .rectangle
        case .circle: self = .ellipse
        case .arrow: self = .arrow
        case .line: self = .line
        case .pencil: self = .pen
        case .text: self = .text
        case .number: self = .number
        case .move, .crop, .emoji, .blur: return nil
        }
    }

    var isShape: Bool {
        self == .rectangle || self == .ellipse
    }
}

/// The inspector's defaults for new annotations. Tools share one color and width in the inspector,
/// and shapes share one fill and one set of effects, so while a theme is active each type keeps
/// its own in `toolStyles` and `shapeStyles`.
struct EditorThemeAnnotationDefaults {
    struct ToolStyle {
        var color: Color
        var lineWidth: CGFloat
    }

    struct ShapeStyle {
        var fillColor: Color
        var effects: ShapeEffects
    }

    var toolStyles: [EditorThemeToolKind: ToolStyle]
    /// Only rectangle and ellipse have an entry.
    var shapeStyles: [EditorThemeToolKind: ShapeStyle]
    var arrowStyle: ArrowStyle
    var fontFamily: String
    var fontSize: CGFloat
    var isBold: Bool
    var isItalic: Bool
    var isUnderlined: Bool
    var textBoxStyle: TextBoxStyle
    var numberTextColor: Color
    var numberSizeMultiplier: CGFloat
}

/// One annotation's look, in the values of its theme section.
enum EditorThemeAnnotationLook: Equatable {
    case rectangle(EditorTheme.ShapeSection)
    case ellipse(EditorTheme.ShapeSection)
    case arrow(EditorTheme.ArrowSection)
    case line(EditorTheme.StrokeSection)
    case pen(EditorTheme.StrokeSection)
    case text(EditorTheme.TextSection)
    case number(EditorTheme.NumberSection)
}

/// How a theme maps onto annotations and back. Everything here is free of view model state.
enum EditorThemeStyling {
    // MARK: - Applying

    /// Restyles one annotation without moving it. Redactions and emoji are left alone.
    static func apply(_ theme: EditorTheme, to annotation: inout ScreenshotAnnotation, imagePixelSize: CGSize) {
        switch annotation.tool {
        case .rectangle:
            applyShape(theme.rectangle, to: &annotation)
        case .circle:
            applyShape(theme.ellipse, to: &annotation)
        case .arrow:
            annotation.color = EditorThemeColor.color(theme.arrow.color)
            annotation.lineWidth = CGFloat(theme.arrow.width)
            annotation.arrowStyle = EditorThemeNames.arrowStyle(theme.arrow.style)
        case .line:
            applyStroke(theme.line, to: &annotation)
        case .pencil:
            applyStroke(theme.pen, to: &annotation)
        case .text:
            applyText(theme.text, to: &annotation)
        case .number:
            annotation.color = EditorThemeColor.color(theme.number.badgeColor)
            annotation.textColor = EditorThemeColor.color(theme.number.textColor)
            annotation.rect = numberRect(
                centeredOn: annotation.rect,
                multiplier: CGFloat(theme.number.size),
                imagePixelSize: imagePixelSize
            )
        case .blur, .emoji, .move, .crop:
            break
        }
    }

    private static func applyShape(_ section: EditorTheme.ShapeSection, to annotation: inout ScreenshotAnnotation) {
        annotation.color = EditorThemeColor.color(section.strokeColor)
        annotation.lineWidth = CGFloat(section.strokeWidth)
        annotation.fillColor = EditorThemeColor.color(section.fillColor)
        annotation.shapeEffects = shapeEffects(section, startingFrom: annotation.shapeEffects)
    }

    private static func applyStroke(_ section: EditorTheme.StrokeSection, to annotation: inout ScreenshotAnnotation) {
        annotation.color = EditorThemeColor.color(section.color)
        annotation.lineWidth = CGFloat(section.width)
    }

    /// Text draws centered in its rect, and the rect is sized from the font, so the rect follows
    /// the new font size around its center. That keeps the words where they were.
    private static func applyText(_ section: EditorTheme.TextSection, to annotation: inout ScreenshotAnnotation) {
        let newSize = CGFloat(section.fontSize)
        if annotation.fontSize > 0, newSize != annotation.fontSize {
            let scale = newSize / annotation.fontSize
            let center = CGPoint(x: annotation.rect.midX, y: annotation.rect.midY)
            let width = annotation.rect.width * scale
            let height = annotation.rect.height * scale
            annotation.rect = CGRect(x: center.x - width / 2, y: center.y - height / 2, width: width, height: height)
        }
        annotation.fontSize = newSize
        annotation.color = EditorThemeColor.color(section.color)
        annotation.fontFamily = section.fontFamily ?? textSystemFontFamily
        annotation.isBold = section.bold
        annotation.isItalic = section.italic
        annotation.isUnderlined = section.underline
        annotation.textBoxStyle = textBoxStyle(section.box)
    }

    /// Sets defaults from a theme. `current` supplies what a theme leaves unset, such as the width
    /// of text tools, and an effect's hidden values for when it is later turned on.
    static func defaults(from theme: EditorTheme, current: EditorThemeAnnotationDefaults) -> EditorThemeAnnotationDefaults {
        func style(_ color: String, _ width: Double) -> EditorThemeAnnotationDefaults.ToolStyle {
            .init(color: EditorThemeColor.color(color), lineWidth: CGFloat(width))
        }
        func shapeStyle(_ section: EditorTheme.ShapeSection, _ kind: EditorThemeToolKind) -> EditorThemeAnnotationDefaults.ShapeStyle {
            .init(
                fillColor: EditorThemeColor.color(section.fillColor),
                effects: shapeEffects(section, startingFrom: current.shapeStyles[kind]?.effects ?? ShapeEffects())
            )
        }

        var result = current
        result.toolStyles = [
            .rectangle: style(theme.rectangle.strokeColor, theme.rectangle.strokeWidth),
            .ellipse: style(theme.ellipse.strokeColor, theme.ellipse.strokeWidth),
            .arrow: style(theme.arrow.color, theme.arrow.width),
            .line: style(theme.line.color, theme.line.width),
            .pen: style(theme.pen.color, theme.pen.width),
            .text: .init(
                color: EditorThemeColor.color(theme.text.color),
                lineWidth: current.toolStyles[.text]?.lineWidth ?? 4
            ),
            .number: .init(
                color: EditorThemeColor.color(theme.number.badgeColor),
                lineWidth: current.toolStyles[.number]?.lineWidth ?? 4
            ),
        ]
        result.shapeStyles = [
            .rectangle: shapeStyle(theme.rectangle, .rectangle),
            .ellipse: shapeStyle(theme.ellipse, .ellipse),
        ]
        result.arrowStyle = EditorThemeNames.arrowStyle(theme.arrow.style)
        result.fontFamily = theme.text.fontFamily ?? textSystemFontFamily
        result.fontSize = CGFloat(theme.text.fontSize)
        result.isBold = theme.text.bold
        result.isItalic = theme.text.italic
        result.isUnderlined = theme.text.underline
        result.textBoxStyle = textBoxStyle(theme.text.box)
        result.numberTextColor = EditorThemeColor.color(theme.number.textColor)
        result.numberSizeMultiplier = CGFloat(theme.number.size)
        return result
    }

    private static func shapeEffects(_ section: EditorTheme.ShapeSection, startingFrom current: ShapeEffects) -> ShapeEffects {
        var effects = current
        effects.isGradientFill = section.gradientEndColor != nil
        if let endColor = section.gradientEndColor {
            effects.gradientEndColor = EditorThemeColor.color(endColor)
        }
        effects.gradientDirectionDegrees = section.gradientDirection
        effects.hasShadow = section.shadowBlur > 0
        if section.shadowBlur > 0 {
            effects.shadowBlur = CGFloat(section.shadowBlur)
        }
        effects.shadowColor = EditorThemeColor.color(section.shadowColor)
        return effects
    }

    static func textBoxStyle(_ box: EditorTheme.TextBoxSection) -> TextBoxStyle {
        let drawsNothing = !EditorThemeColor.isVisible(box.backgroundColor)
            && (!EditorThemeColor.isVisible(box.borderColor) || box.borderWidth == 0)
            && box.padding == 0
        return TextBoxStyle(
            preset: drawsNothing ? .plain : .custom,
            backgroundColor: EditorThemeColor.color(box.backgroundColor),
            borderColor: EditorThemeColor.color(box.borderColor),
            borderWidth: CGFloat(box.borderWidth),
            padding: CGFloat(box.padding),
            cornerRadius: CGFloat(box.cornerRadius)
        )
    }

    // MARK: - Number size

    static func numberBaseSidePixels(imageWidth: CGFloat) -> CGFloat {
        max(numberCircleMinPixels, min(numberCircleMaxPixels, imageWidth * numberCircleSizeRatio))
    }

    static func numberSidePixels(multiplier: CGFloat, imageWidth: CGFloat) -> CGFloat {
        max(numberCircleMinimumDisplayPixels, numberBaseSidePixels(imageWidth: imageWidth) * multiplier)
    }

    private static func numberRect(centeredOn rect: CGRect, multiplier: CGFloat, imagePixelSize size: CGSize) -> CGRect {
        guard size.width > 0, size.height > 0 else { return rect }
        let side = numberSidePixels(multiplier: multiplier, imageWidth: size.width)
        let width = side / size.width
        let height = side / size.height
        return CGRect(x: rect.midX - width / 2, y: rect.midY - height / 2, width: width, height: height)
    }

    /// The theme's size when the badge is drawn at it, so a size the minimum clamped to a larger
    /// badge doesn't read as an edit. Otherwise the badge's own size, within the format's range.
    private static func numberSize(of annotation: ScreenshotAnnotation, base: Double, imagePixelSize size: CGSize) -> Double {
        guard size.width > 0 else { return base }
        let side = annotation.rect.width * size.width
        if abs(side - numberSidePixels(multiplier: CGFloat(base), imageWidth: size.width)) < 0.5 {
            return base
        }
        return clamped(Double(side / numberBaseSidePixels(imageWidth: size.width)), EditorThemeRange.numberSize)
    }

    // MARK: - Reading looks back

    static func look(
        of annotation: ScreenshotAnnotation,
        theme: EditorTheme,
        imagePixelSize: CGSize
    ) -> EditorThemeAnnotationLook? {
        switch annotation.tool {
        case .rectangle:
            return .rectangle(shapeSection(
                color: annotation.color,
                lineWidth: annotation.lineWidth,
                fillColor: annotation.fillColor,
                effects: annotation.shapeEffects,
                base: theme.rectangle
            ))
        case .circle:
            return .ellipse(shapeSection(
                color: annotation.color,
                lineWidth: annotation.lineWidth,
                fillColor: annotation.fillColor,
                effects: annotation.shapeEffects,
                base: theme.ellipse
            ))
        case .arrow:
            return .arrow(arrowSection(color: annotation.color, lineWidth: annotation.lineWidth, style: annotation.arrowStyle))
        case .line:
            return .line(strokeSection(color: annotation.color, lineWidth: annotation.lineWidth))
        case .pencil:
            return .pen(strokeSection(color: annotation.color, lineWidth: annotation.lineWidth))
        case .text:
            return .text(textSection(
                color: annotation.color,
                fontFamily: annotation.fontFamily,
                fontSize: annotation.fontSize,
                isBold: annotation.isBold,
                isItalic: annotation.isItalic,
                isUnderlined: annotation.isUnderlined,
                box: annotation.textBoxStyle
            ))
        case .number:
            return .number(EditorTheme.NumberSection(
                badgeColor: EditorThemeColor.hex(annotation.color),
                textColor: EditorThemeColor.hex(annotation.textColor),
                size: numberSize(of: annotation, base: theme.number.size, imagePixelSize: imagePixelSize)
            ))
        case .blur, .emoji, .move, .crop:
            return nil
        }
    }

    /// The look of the defaults for new annotations of one type, or nil if there aren't any.
    private static func defaultLook(
        of kind: EditorThemeToolKind,
        defaults: EditorThemeAnnotationDefaults,
        theme: EditorTheme
    ) -> EditorThemeAnnotationLook? {
        guard let style = defaults.toolStyles[kind] else { return nil }
        switch kind {
        case .rectangle:
            guard let shape = defaults.shapeStyles[.rectangle] else { return nil }
            return .rectangle(shapeSection(
                color: style.color,
                lineWidth: style.lineWidth,
                fillColor: shape.fillColor,
                effects: shape.effects,
                base: theme.rectangle
            ))
        case .ellipse:
            guard let shape = defaults.shapeStyles[.ellipse] else { return nil }
            return .ellipse(shapeSection(
                color: style.color,
                lineWidth: style.lineWidth,
                fillColor: shape.fillColor,
                effects: shape.effects,
                base: theme.ellipse
            ))
        case .arrow:
            return .arrow(arrowSection(color: style.color, lineWidth: style.lineWidth, style: defaults.arrowStyle))
        case .line:
            return .line(strokeSection(color: style.color, lineWidth: style.lineWidth))
        case .pen:
            return .pen(strokeSection(color: style.color, lineWidth: style.lineWidth))
        case .text:
            return .text(textSection(
                color: style.color,
                fontFamily: defaults.fontFamily,
                fontSize: defaults.fontSize,
                isBold: defaults.isBold,
                isItalic: defaults.isItalic,
                isUnderlined: defaults.isUnderlined,
                box: defaults.textBoxStyle
            ))
        case .number:
            return .number(EditorTheme.NumberSection(
                badgeColor: EditorThemeColor.hex(style.color),
                textColor: EditorThemeColor.hex(defaults.numberTextColor),
                size: clamped(Double(defaults.numberSizeMultiplier), EditorThemeRange.numberSize)
            ))
        }
    }

    private static func shapeSection(
        color: Color,
        lineWidth: CGFloat,
        fillColor: Color,
        effects: ShapeEffects,
        base: EditorTheme.ShapeSection
    ) -> EditorTheme.ShapeSection {
        let fillHex = EditorThemeColor.hex(fillColor)
        // Values with no visible effect stay as the theme has them, so they can't read as edits.
        let drawsGradient = effects.isGradientFill && EditorThemeColor.isVisible(fillHex)
        return EditorTheme.ShapeSection(
            strokeColor: EditorThemeColor.hex(color),
            strokeWidth: clamped(Double(lineWidth), EditorThemeRange.strokeWidth),
            fillColor: fillHex,
            gradientEndColor: drawsGradient ? EditorThemeColor.hex(effects.gradientEndColor) : nil,
            gradientDirection: drawsGradient ? effects.gradientDirectionDegrees : base.gradientDirection,
            shadowBlur: effects.drawsShadow ? clamped(Double(effects.shadowBlur), EditorThemeRange.shadowBlur) : 0,
            shadowColor: effects.drawsShadow ? EditorThemeColor.hex(effects.shadowColor) : base.shadowColor
        )
    }

    private static func arrowSection(color: Color, lineWidth: CGFloat, style: ArrowStyle) -> EditorTheme.ArrowSection {
        EditorTheme.ArrowSection(
            color: EditorThemeColor.hex(color),
            width: clamped(Double(lineWidth), EditorThemeRange.strokeWidth),
            style: EditorThemeNames.name(for: style)
        )
    }

    private static func strokeSection(color: Color, lineWidth: CGFloat) -> EditorTheme.StrokeSection {
        EditorTheme.StrokeSection(
            color: EditorThemeColor.hex(color),
            width: clamped(Double(lineWidth), EditorThemeRange.strokeWidth)
        )
    }

    private static func textSection(
        color: Color,
        fontFamily: String,
        fontSize: CGFloat,
        isBold: Bool,
        isItalic: Bool,
        isUnderlined: Bool,
        box: TextBoxStyle
    ) -> EditorTheme.TextSection {
        EditorTheme.TextSection(
            color: EditorThemeColor.hex(color),
            fontFamily: fontFamily == textSystemFontFamily ? nil : fontFamily,
            fontSize: clamped(Double(fontSize), EditorThemeRange.fontSize),
            bold: isBold,
            italic: isItalic,
            underline: isUnderlined,
            box: EditorTheme.TextBoxSection(
                backgroundColor: EditorThemeColor.hex(box.backgroundColor),
                borderColor: EditorThemeColor.hex(box.borderColor),
                borderWidth: clamped(Double(box.borderWidth), EditorThemeRange.boxBorderWidth),
                padding: clamped(Double(box.padding), EditorThemeRange.boxPadding),
                cornerRadius: clamped(Double(box.cornerRadius), EditorThemeRange.boxCornerRadius)
            )
        )
    }

    private static func clamped(_ value: Double, _ range: ClosedRange<Double>) -> Double {
        guard value.isFinite else { return range.lowerBound }
        return min(range.upperBound, max(range.lowerBound, value))
    }
}

// MARK: - Modified and capture

extension EditorThemeStyling {
    /// The look the document holds, computed per plans/editor-themes.md: each annotation type takes
    /// its newest annotation that differs from `theme`, then its defaults if they differ, then the
    /// theme's values. `acceptedLooks` holds looks Update or Save as New already considered, which
    /// no longer count.
    static func captured(
        from theme: EditorTheme,
        annotations: [ScreenshotAnnotation],
        defaults: EditorThemeAnnotationDefaults,
        canvas: EditorTheme.CanvasSection,
        canvasBaseline: EditorTheme.CanvasSection,
        acceptedLooks: [UUID: EditorThemeAnnotationLook] = [:],
        imagePixelSize: CGSize
    ) -> EditorTheme {
        var result = theme
        var found = Set<EditorThemeToolKind>()
        for annotation in annotations.reversed() {
            guard let kind = EditorThemeToolKind(tool: annotation.tool), !found.contains(kind),
                  let look = look(of: annotation, theme: theme, imagePixelSize: imagePixelSize),
                  acceptedLooks[annotation.id] != look else {
                continue
            }
            if adopt(look, into: &result, theme: theme) {
                found.insert(kind)
            }
            if found.count == EditorThemeToolKind.allCases.count { break }
        }

        for kind in EditorThemeToolKind.allCases where !found.contains(kind) {
            if let look = defaultLook(of: kind, defaults: defaults, theme: theme) {
                adopt(look, into: &result, theme: theme)
            }
        }

        if comparable(canvas) != comparable(canvasBaseline) {
            result.canvas = capturedCanvas(canvas, theme: theme.canvas)
        }
        return result
    }

    /// Puts `look` in its own section of `result` if it differs from `theme`, and says whether it did.
    @discardableResult
    private static func adopt(_ look: EditorThemeAnnotationLook, into result: inout EditorTheme, theme: EditorTheme) -> Bool {
        switch look {
        case .rectangle(let section):
            guard comparable(section) != comparable(theme.rectangle) else { return false }
            result.rectangle = comparable(section)
        case .ellipse(let section):
            guard comparable(section) != comparable(theme.ellipse) else { return false }
            result.ellipse = comparable(section)
        case .arrow(let section):
            guard comparable(section) != comparable(theme.arrow) else { return false }
            result.arrow = comparable(section)
        case .line(let section):
            guard comparable(section) != comparable(theme.line) else { return false }
            result.line = comparable(section)
        case .pen(let section):
            guard comparable(section) != comparable(theme.pen) else { return false }
            result.pen = comparable(section)
        case .text(let section):
            guard comparable(section) != comparable(theme.text) else { return false }
            result.text = comparable(section)
        case .number(let section):
            guard comparable(section) != comparable(theme.number) else { return false }
            result.number = comparable(section)
        }
        return true
    }

    /// Whether two themes look the same, whatever their id and name.
    static func hasSameLook(_ lhs: EditorTheme, _ rhs: EditorTheme) -> Bool {
        comparable(lhs.rectangle) == comparable(rhs.rectangle)
            && comparable(lhs.ellipse) == comparable(rhs.ellipse)
            && comparable(lhs.arrow) == comparable(rhs.arrow)
            && comparable(lhs.line) == comparable(rhs.line)
            && comparable(lhs.pen) == comparable(rhs.pen)
            && comparable(lhs.text) == comparable(rhs.text)
            && comparable(lhs.number) == comparable(rhs.number)
            && comparable(lhs.canvas) == comparable(rhs.canvas)
    }

    /// A wallpaper can't be saved, so the theme's background stays. `applyOnOpen` isn't part of the
    /// document at all.
    private static func capturedCanvas(_ canvas: EditorTheme.CanvasSection, theme: EditorTheme.CanvasSection) -> EditorTheme.CanvasSection {
        var result = canvas
        result.applyOnOpen = theme.applyOnOpen
        if !EditorThemeNames.backgrounds.contains(result.background) {
            result.background = theme.background
        }
        // Colors this background doesn't draw stay as the theme has them.
        if result.background == "transparent" || canvas.background == "wallpaper" {
            result.color = theme.color
        }
        if result.background != "gradient" || canvas.background == "wallpaper" {
            result.secondaryColor = theme.secondaryColor
            result.gradientDirection = theme.gradientDirection
        }
        result.gradientDirection = rounded(clamped(result.gradientDirection, EditorThemeRange.direction))
        result.padding = rounded(clamped(result.padding, EditorThemeRange.canvasPadding))
        result.cornerRadius = rounded(clamped(result.cornerRadius, EditorThemeRange.canvasCornerRadius))
        result.shadow = rounded(clamped(result.shadow, EditorThemeRange.canvasShadow))
        return result
    }

    // MARK: Comparable forms

    private static func rounded(_ value: Double) -> Double {
        (value * 100).rounded() / 100
    }

    private static func comparable(_ section: EditorTheme.ShapeSection) -> EditorTheme.ShapeSection {
        var result = section
        result.strokeColor = EditorThemeColor.comparableHex(section.strokeColor)
        result.strokeWidth = rounded(section.strokeWidth)
        result.fillColor = EditorThemeColor.comparableHex(section.fillColor)
        result.gradientEndColor = section.gradientEndColor.map(EditorThemeColor.comparableHex)
        result.gradientDirection = rounded(section.gradientDirection)
        result.shadowBlur = rounded(section.shadowBlur)
        result.shadowColor = EditorThemeColor.comparableHex(section.shadowColor)
        return result
    }

    private static func comparable(_ section: EditorTheme.ArrowSection) -> EditorTheme.ArrowSection {
        var result = section
        result.color = EditorThemeColor.comparableHex(section.color)
        result.width = rounded(section.width)
        return result
    }

    private static func comparable(_ section: EditorTheme.StrokeSection) -> EditorTheme.StrokeSection {
        var result = section
        result.color = EditorThemeColor.comparableHex(section.color)
        result.width = rounded(section.width)
        return result
    }

    private static func comparable(_ section: EditorTheme.TextSection) -> EditorTheme.TextSection {
        var result = section
        result.color = EditorThemeColor.comparableHex(section.color)
        if section.fontFamily == textSystemFontFamily { result.fontFamily = nil }
        result.fontSize = rounded(section.fontSize)
        result.box.backgroundColor = EditorThemeColor.comparableHex(section.box.backgroundColor)
        result.box.borderColor = EditorThemeColor.comparableHex(section.box.borderColor)
        result.box.borderWidth = rounded(section.box.borderWidth)
        result.box.padding = rounded(section.box.padding)
        result.box.cornerRadius = rounded(section.box.cornerRadius)
        return result
    }

    private static func comparable(_ section: EditorTheme.NumberSection) -> EditorTheme.NumberSection {
        var result = section
        result.badgeColor = EditorThemeColor.comparableHex(section.badgeColor)
        result.textColor = EditorThemeColor.comparableHex(section.textColor)
        result.size = rounded(section.size)
        return result
    }

    private static func comparable(_ section: EditorTheme.CanvasSection) -> EditorTheme.CanvasSection {
        var result = section
        // Whether a theme applies on open isn't something a document can change.
        result.applyOnOpen = false
        // Colors a background doesn't draw can't make two looks differ.
        result.color = section.background == "transparent"
            ? EditorThemeColor.transparentHex
            : EditorThemeColor.comparableHex(section.color)
        result.secondaryColor = section.background == "gradient"
            ? EditorThemeColor.comparableHex(section.secondaryColor)
            : EditorThemeColor.transparentHex
        result.gradientDirection = section.background == "gradient" ? rounded(section.gradientDirection) : 0
        result.padding = rounded(section.padding)
        result.cornerRadius = rounded(section.cornerRadius)
        result.shadow = rounded(section.shadow)
        return result
    }
}
