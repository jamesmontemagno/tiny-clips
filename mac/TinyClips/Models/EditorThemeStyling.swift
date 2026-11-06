import AppKit
import SwiftUI

/// Annotation families that share one set of theme values. Redactions and emoji have none, so a
/// theme can never weaken a redaction.
enum EditorThemeGroupKind: Hashable, CaseIterable {
    case shape
    case line
    case text
    case number

    init?(tool: EditTool) {
        switch tool {
        case .rectangle, .circle: self = .shape
        case .arrow, .line, .pencil: self = .line
        case .text: self = .text
        case .number: self = .number
        case .move, .crop, .emoji, .blur: return nil
        }
    }
}

/// The inspector's defaults for new annotations. Tools share one color and width in the inspector,
/// so while a theme is active each group keeps its own pair in `toolStyles`.
struct EditorThemeAnnotationDefaults {
    struct ToolStyle {
        var color: Color
        var lineWidth: CGFloat
    }

    var toolStyles: [EditorThemeGroupKind: ToolStyle]
    var fillColor: Color
    var shapeEffects: ShapeEffects
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

/// One annotation's look, in theme values.
enum EditorThemeAnnotationLook: Equatable {
    case shape(EditorTheme.ShapeGroup)
    case line(EditorTheme.LineGroup)
    case text(EditorTheme.TextGroup)
    case number(EditorTheme.NumberGroup)
}

/// How a theme maps onto annotations and back. Everything here is free of view model state.
enum EditorThemeStyling {
    // MARK: - Applying

    /// Restyles one annotation without moving it. Redactions and emoji are left alone.
    static func apply(_ theme: EditorTheme, to annotation: inout ScreenshotAnnotation, imagePixelSize: CGSize) {
        switch annotation.tool {
        case .rectangle, .circle:
            annotation.color = EditorThemeColor.color(theme.shape.strokeColor)
            annotation.lineWidth = CGFloat(theme.shape.strokeWidth)
            annotation.fillColor = EditorThemeColor.color(theme.shape.fillColor)
            annotation.shapeEffects = shapeEffects(theme.shape, startingFrom: annotation.shapeEffects)
        case .arrow:
            annotation.color = EditorThemeColor.color(theme.line.color)
            annotation.lineWidth = CGFloat(theme.line.width)
            annotation.arrowStyle = EditorThemeNames.arrowStyle(theme.line.arrowStyle)
        case .line, .pencil:
            annotation.color = EditorThemeColor.color(theme.line.color)
            annotation.lineWidth = CGFloat(theme.line.width)
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

    /// Text draws centered in its rect, and the rect is sized from the font, so the rect follows
    /// the new font size around its center. That keeps the words where they were.
    private static func applyText(_ group: EditorTheme.TextGroup, to annotation: inout ScreenshotAnnotation) {
        let newSize = CGFloat(group.fontSize)
        if annotation.fontSize > 0, newSize != annotation.fontSize {
            let scale = newSize / annotation.fontSize
            let center = CGPoint(x: annotation.rect.midX, y: annotation.rect.midY)
            let width = annotation.rect.width * scale
            let height = annotation.rect.height * scale
            annotation.rect = CGRect(x: center.x - width / 2, y: center.y - height / 2, width: width, height: height)
        }
        annotation.fontSize = newSize
        annotation.color = EditorThemeColor.color(group.color)
        annotation.fontFamily = group.fontFamily ?? textSystemFontFamily
        annotation.isBold = group.bold
        annotation.isItalic = group.italic
        annotation.isUnderlined = group.underline
        annotation.textBoxStyle = textBoxStyle(group.box)
    }

    /// Sets defaults from a theme. `current` supplies what a theme leaves unset, such as the width
    /// of text tools, and an effect's hidden values for when it is later turned on.
    static func defaults(from theme: EditorTheme, current: EditorThemeAnnotationDefaults) -> EditorThemeAnnotationDefaults {
        var result = current
        result.toolStyles = [
            .shape: .init(color: EditorThemeColor.color(theme.shape.strokeColor), lineWidth: CGFloat(theme.shape.strokeWidth)),
            .line: .init(color: EditorThemeColor.color(theme.line.color), lineWidth: CGFloat(theme.line.width)),
            .text: .init(
                color: EditorThemeColor.color(theme.text.color),
                lineWidth: current.toolStyles[.text]?.lineWidth ?? 4
            ),
            .number: .init(
                color: EditorThemeColor.color(theme.number.badgeColor),
                lineWidth: current.toolStyles[.number]?.lineWidth ?? 4
            ),
        ]
        result.fillColor = EditorThemeColor.color(theme.shape.fillColor)
        result.shapeEffects = shapeEffects(theme.shape, startingFrom: current.shapeEffects)
        result.arrowStyle = EditorThemeNames.arrowStyle(theme.line.arrowStyle)
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

    private static func shapeEffects(_ group: EditorTheme.ShapeGroup, startingFrom current: ShapeEffects) -> ShapeEffects {
        var effects = current
        effects.isGradientFill = group.gradientEndColor != nil
        if let endColor = group.gradientEndColor {
            effects.gradientEndColor = EditorThemeColor.color(endColor)
        }
        effects.gradientDirectionDegrees = group.gradientDirection
        effects.hasShadow = group.shadowBlur > 0
        if group.shadowBlur > 0 {
            effects.shadowBlur = CGFloat(group.shadowBlur)
        }
        effects.shadowColor = EditorThemeColor.color(group.shadowColor)
        return effects
    }

    static func textBoxStyle(_ box: EditorTheme.TextBoxGroup) -> TextBoxStyle {
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
        switch EditorThemeGroupKind(tool: annotation.tool) {
        case .shape:
            return .shape(shapeGroup(
                color: annotation.color,
                lineWidth: annotation.lineWidth,
                fillColor: annotation.fillColor,
                effects: annotation.shapeEffects,
                base: theme.shape
            ))
        case .line:
            var group = theme.line
            group.color = EditorThemeColor.hex(annotation.color)
            group.width = clamped(Double(annotation.lineWidth), EditorThemeRange.strokeWidth)
            if annotation.tool == .arrow {
                group.arrowStyle = EditorThemeNames.name(for: annotation.arrowStyle)
            }
            return .line(group)
        case .text:
            return .text(textGroup(
                color: annotation.color,
                fontFamily: annotation.fontFamily,
                fontSize: annotation.fontSize,
                isBold: annotation.isBold,
                isItalic: annotation.isItalic,
                isUnderlined: annotation.isUnderlined,
                box: annotation.textBoxStyle
            ))
        case .number:
            return .number(EditorTheme.NumberGroup(
                badgeColor: EditorThemeColor.hex(annotation.color),
                textColor: EditorThemeColor.hex(annotation.textColor),
                size: numberSize(of: annotation, base: theme.number.size, imagePixelSize: imagePixelSize)
            ))
        case nil:
            return nil
        }
    }

    private static func shapeGroup(
        color: Color,
        lineWidth: CGFloat,
        fillColor: Color,
        effects: ShapeEffects,
        base: EditorTheme.ShapeGroup
    ) -> EditorTheme.ShapeGroup {
        let fillHex = EditorThemeColor.hex(fillColor)
        // Values with no visible effect stay as the theme has them, so they can't read as edits.
        let drawsGradient = effects.isGradientFill && EditorThemeColor.isVisible(fillHex)
        return EditorTheme.ShapeGroup(
            strokeColor: EditorThemeColor.hex(color),
            strokeWidth: clamped(Double(lineWidth), EditorThemeRange.strokeWidth),
            fillColor: fillHex,
            gradientEndColor: drawsGradient ? EditorThemeColor.hex(effects.gradientEndColor) : nil,
            gradientDirection: drawsGradient ? effects.gradientDirectionDegrees : base.gradientDirection,
            shadowBlur: effects.drawsShadow ? clamped(Double(effects.shadowBlur), EditorThemeRange.shadowBlur) : 0,
            shadowColor: effects.drawsShadow ? EditorThemeColor.hex(effects.shadowColor) : base.shadowColor
        )
    }

    private static func textGroup(
        color: Color,
        fontFamily: String,
        fontSize: CGFloat,
        isBold: Bool,
        isItalic: Bool,
        isUnderlined: Bool,
        box: TextBoxStyle
    ) -> EditorTheme.TextGroup {
        EditorTheme.TextGroup(
            color: EditorThemeColor.hex(color),
            fontFamily: fontFamily == textSystemFontFamily ? nil : fontFamily,
            fontSize: clamped(Double(fontSize), EditorThemeRange.fontSize),
            bold: isBold,
            italic: isItalic,
            underline: isUnderlined,
            box: EditorTheme.TextBoxGroup(
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
    /// The look the document holds, computed per plans/editor-themes.md: each group takes its newest
    /// annotation that differs from `theme`, then the defaults if they differ, then the theme's values.
    /// `acceptedLooks` holds looks Update or Save as New already considered, which no longer count.
    static func captured(
        from theme: EditorTheme,
        annotations: [ScreenshotAnnotation],
        defaults: EditorThemeAnnotationDefaults,
        canvas: EditorTheme.CanvasGroup,
        canvasBaseline: EditorTheme.CanvasGroup,
        acceptedLooks: [UUID: EditorThemeAnnotationLook] = [:],
        imagePixelSize: CGSize
    ) -> EditorTheme {
        var result = theme
        var found = Set<EditorThemeGroupKind>()
        for annotation in annotations.reversed() {
            guard let look = look(of: annotation, theme: theme, imagePixelSize: imagePixelSize),
                  acceptedLooks[annotation.id] != look else {
                continue
            }
            switch look {
            case .shape(let group) where !found.contains(.shape) && comparable(group) != comparable(theme.shape):
                result.shape = comparable(group)
                found.insert(.shape)
            case .line(let group) where !found.contains(.line) && comparable(group) != comparable(theme.line):
                result.line = comparable(group)
                found.insert(.line)
            case .text(let group) where !found.contains(.text) && comparable(group) != comparable(theme.text):
                result.text = comparable(group)
                found.insert(.text)
            case .number(let group) where !found.contains(.number) && comparable(group) != comparable(theme.number):
                result.number = comparable(group)
                found.insert(.number)
            default:
                break
            }
            if found.count == EditorThemeGroupKind.allCases.count { break }
        }

        if !found.contains(.shape), let style = defaults.toolStyles[.shape] {
            let group = shapeGroup(
                color: style.color,
                lineWidth: style.lineWidth,
                fillColor: defaults.fillColor,
                effects: defaults.shapeEffects,
                base: theme.shape
            )
            if comparable(group) != comparable(theme.shape) { result.shape = comparable(group) }
        }
        if !found.contains(.line), let style = defaults.toolStyles[.line] {
            let group = EditorTheme.LineGroup(
                color: EditorThemeColor.hex(style.color),
                width: clamped(Double(style.lineWidth), EditorThemeRange.strokeWidth),
                arrowStyle: EditorThemeNames.name(for: defaults.arrowStyle)
            )
            if comparable(group) != comparable(theme.line) { result.line = comparable(group) }
        }
        if !found.contains(.text), let style = defaults.toolStyles[.text] {
            let group = textGroup(
                color: style.color,
                fontFamily: defaults.fontFamily,
                fontSize: defaults.fontSize,
                isBold: defaults.isBold,
                isItalic: defaults.isItalic,
                isUnderlined: defaults.isUnderlined,
                box: defaults.textBoxStyle
            )
            if comparable(group) != comparable(theme.text) { result.text = comparable(group) }
        }
        if !found.contains(.number), let style = defaults.toolStyles[.number] {
            let group = EditorTheme.NumberGroup(
                badgeColor: EditorThemeColor.hex(style.color),
                textColor: EditorThemeColor.hex(defaults.numberTextColor),
                size: clamped(Double(defaults.numberSizeMultiplier), EditorThemeRange.numberSize)
            )
            if comparable(group) != comparable(theme.number) { result.number = comparable(group) }
        }

        if comparable(canvas) != comparable(canvasBaseline) {
            result.canvas = capturedCanvas(canvas, theme: theme.canvas)
        }
        return result
    }

    /// Whether two themes look the same, whatever their id and name.
    static func hasSameLook(_ lhs: EditorTheme, _ rhs: EditorTheme) -> Bool {
        comparable(lhs.shape) == comparable(rhs.shape)
            && comparable(lhs.line) == comparable(rhs.line)
            && comparable(lhs.text) == comparable(rhs.text)
            && comparable(lhs.number) == comparable(rhs.number)
            && comparable(lhs.canvas) == comparable(rhs.canvas)
    }

    /// A wallpaper can't be saved, so the theme's background stays. `applyOnOpen` isn't part of the
    /// document at all.
    private static func capturedCanvas(_ canvas: EditorTheme.CanvasGroup, theme: EditorTheme.CanvasGroup) -> EditorTheme.CanvasGroup {
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

    private static func comparable(_ group: EditorTheme.ShapeGroup) -> EditorTheme.ShapeGroup {
        var result = group
        result.strokeColor = EditorThemeColor.comparableHex(group.strokeColor)
        result.strokeWidth = rounded(group.strokeWidth)
        result.fillColor = EditorThemeColor.comparableHex(group.fillColor)
        result.gradientDirection = rounded(group.gradientDirection)
        result.shadowBlur = rounded(group.shadowBlur)
        result.shadowColor = EditorThemeColor.comparableHex(group.shadowColor)
        return result
    }

    private static func comparable(_ group: EditorTheme.LineGroup) -> EditorTheme.LineGroup {
        var result = group
        result.color = EditorThemeColor.comparableHex(group.color)
        result.width = rounded(group.width)
        return result
    }

    private static func comparable(_ group: EditorTheme.TextGroup) -> EditorTheme.TextGroup {
        var result = group
        result.color = EditorThemeColor.comparableHex(group.color)
        if group.fontFamily == textSystemFontFamily { result.fontFamily = nil }
        result.fontSize = rounded(group.fontSize)
        result.box.backgroundColor = EditorThemeColor.comparableHex(group.box.backgroundColor)
        result.box.borderColor = EditorThemeColor.comparableHex(group.box.borderColor)
        result.box.borderWidth = rounded(group.box.borderWidth)
        result.box.padding = rounded(group.box.padding)
        result.box.cornerRadius = rounded(group.box.cornerRadius)
        return result
    }

    private static func comparable(_ group: EditorTheme.NumberGroup) -> EditorTheme.NumberGroup {
        var result = group
        result.badgeColor = EditorThemeColor.comparableHex(group.badgeColor)
        result.textColor = EditorThemeColor.comparableHex(group.textColor)
        result.size = rounded(group.size)
        return result
    }

    private static func comparable(_ group: EditorTheme.CanvasGroup) -> EditorTheme.CanvasGroup {
        var result = group
        // Whether a theme applies on open isn't something a document can change.
        result.applyOnOpen = false
        // Colors a background doesn't draw can't make two looks differ.
        result.color = group.background == "transparent"
            ? EditorThemeColor.transparentHex
            : EditorThemeColor.comparableHex(group.color)
        result.secondaryColor = group.background == "gradient"
            ? EditorThemeColor.comparableHex(group.secondaryColor)
            : EditorThemeColor.transparentHex
        result.gradientDirection = group.background == "gradient" ? rounded(group.gradientDirection) : 0
        result.padding = rounded(group.padding)
        result.cornerRadius = rounded(group.cornerRadius)
        result.shadow = rounded(group.shadow)
        return result
    }
}
