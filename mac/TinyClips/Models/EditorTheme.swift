import AppKit
import SwiftUI

/// A theme that breaks a format rule. `field` is the JSON path the rule applies to.
struct EditorThemeValidationError: LocalizedError, Equatable {
    let field: String
    let reason: String

    var errorDescription: String? {
        "\(field) \(reason)"
    }
}

/// A named screenshot editor look. The JSON format is shared with Windows and agents; see
/// shared/editor-themes/README.md. Colors stay hex strings so comparing two looks is exact.
struct EditorTheme: Equatable, Identifiable {
    static let currentVersion = 1
    static let maximumNameLength = 60
    static let maximumIDLength = 64

    struct ShapeGroup: Equatable {
        var strokeColor = "#FFFF3B30"
        var strokeWidth: Double = 4
        var fillColor = "#00000000"
        var gradientEndColor: String?
        var gradientDirection: Double = 45
        var shadowBlur: Double = 0
        var shadowColor = "#59000000"
    }

    struct LineGroup: Equatable {
        var color = "#FFFF3B30"
        var width: Double = 4
        var arrowStyle = "straight"
    }

    struct TextBoxGroup: Equatable {
        var backgroundColor = "#00000000"
        var borderColor = "#00000000"
        var borderWidth: Double = 0
        var padding: Double = 0
        var cornerRadius: Double = 0
    }

    struct TextGroup: Equatable {
        var color = "#FFFF3B30"
        var fontFamily: String?
        var fontSize: Double = 16
        var bold = false
        var italic = false
        var underline = false
        var box = TextBoxGroup()
    }

    struct NumberGroup: Equatable {
        var badgeColor = "#FFFF3B30"
        var textColor = "#FFFFFFFF"
        var size: Double = 1
    }

    struct CanvasGroup: Equatable {
        var applyOnOpen = false
        var background = "transparent"
        var color = "#FFF5F5FA"
        var secondaryColor = "#FFD6E6FC"
        var gradientDirection: Double = 45
        var padding: Double = 0
        var cornerRadius: Double = 0
        var shadow: Double = 0
        var frame = "original"
        var horizontalAlignment = "center"
        var verticalAlignment = "center"
    }

    var version = EditorTheme.currentVersion
    var id: String
    var name: String
    var shape = ShapeGroup()
    var line = LineGroup()
    var text = TextGroup()
    var number = NumberGroup()
    var canvas = CanvasGroup()

    /// A nil `id` makes a new one.
    init(id: String? = nil, name: String) {
        self.id = id ?? UUID().uuidString
        self.name = name
    }
}

/// The ranges in shared/editor-themes/README.md, in theme units.
enum EditorThemeRange {
    static let strokeWidth: ClosedRange<Double> = 0...100
    static let shadowBlur: ClosedRange<Double> = 0...100
    static let direction: ClosedRange<Double> = 0...360
    static let fontSize: ClosedRange<Double> = 4...200
    static let boxBorderWidth: ClosedRange<Double> = 0...12
    static let boxPadding: ClosedRange<Double> = 0...48
    static let boxCornerRadius: ClosedRange<Double> = 0...32
    static let numberSize: ClosedRange<Double> = 0.25...4
    static let canvasPadding: ClosedRange<Double> = 0...500
    static let canvasCornerRadius: ClosedRange<Double> = 0...200
    static let canvasShadow: ClosedRange<Double> = 0...200
}

/// sRGB conversions between stored hex strings and editor colors.
enum EditorThemeColor {
    static let transparentHex = "#00000000"

    /// Uppercase `#AARRGGBB`, or nil when `string` isn't a valid color.
    static func normalizedHex(_ string: String) -> String? {
        guard let components = AutomationAnnotations.hexComponents(string) else { return nil }
        return hex(red: components.red, green: components.green, blue: components.blue, alpha: components.alpha)
    }

    static func hex(_ color: Color) -> String {
        guard let resolved = NSColor(color).usingColorSpace(.sRGB) else { return transparentHex }
        let hexValue = hex(
            red: resolved.redComponent,
            green: resolved.greenComponent,
            blue: resolved.blueComponent,
            alpha: resolved.alphaComponent
        )
        return comparableHex(hexValue)
    }

    /// Alpha 0 becomes `.clear`, because the renderers treat exactly that value as "no fill".
    static func color(_ hexValue: String) -> Color {
        guard let components = AutomationAnnotations.hexComponents(hexValue), byte(components.alpha) > 0 else {
            return .clear
        }
        return Color(nsColor: NSColor(
            srgbRed: components.red,
            green: components.green,
            blue: components.blue,
            alpha: components.alpha
        ))
    }

    static func isVisible(_ hexValue: String) -> Bool {
        guard let components = AutomationAnnotations.hexComponents(hexValue) else { return false }
        return byte(components.alpha) > 0
    }

    /// Fully transparent colors look the same whatever their other channels hold.
    static func comparableHex(_ hexValue: String) -> String {
        isVisible(hexValue) ? hexValue : transparentHex
    }

    private static func hex(red: CGFloat, green: CGFloat, blue: CGFloat, alpha: CGFloat) -> String {
        String(format: "#%02X%02X%02X%02X", byte(alpha), byte(red), byte(green), byte(blue))
    }

    private static func byte(_ component: CGFloat) -> Int {
        min(255, max(0, Int((component * 255).rounded())))
    }
}

// MARK: - JSON

// Missing fields keep their defaults, as the format requires. A null or wrongly typed field is an
// error naming that field, except the two fields whose null has a meaning.

private extension KeyedDecodingContainer {
    func themeValue<Value: Decodable>(_ key: Key, _ field: String, default defaultValue: Value) throws -> Value {
        guard contains(key) else { return defaultValue }
        do {
            if try decodeNil(forKey: key) {
                throw EditorThemeValidationError(field: field, reason: "can't be null.")
            }
            return try decode(Value.self, forKey: key)
        } catch let error as EditorThemeValidationError {
            throw error
        } catch {
            throw EditorThemeValidationError(field: field, reason: "has the wrong type.")
        }
    }

    func themeNullableString(_ key: Key, _ field: String) throws -> String? {
        guard contains(key) else { return nil }
        do {
            return try decodeIfPresent(String.self, forKey: key)
        } catch {
            throw EditorThemeValidationError(field: field, reason: "must be a string or null.")
        }
    }
}

extension EditorTheme: Codable {
    private enum CodingKeys: String, CodingKey {
        case version, id, name, shape, line, text, number, canvas
    }

    /// An omitted `id` gets a new one, so saving such a theme creates it.
    init(from decoder: Decoder) throws {
        self.init(name: "Default")
        let container = try decoder.container(keyedBy: CodingKeys.self)
        // Checked first: a newer format's groups may not parse, and the version is the real problem.
        version = try container.themeValue(.version, "version", default: version)
        guard version == Self.currentVersion else {
            throw EditorThemeValidationError(field: "version", reason: "must be \(Self.currentVersion).")
        }
        id = try container.themeValue(.id, "id", default: id)
        name = try container.themeValue(.name, "name", default: name)
        shape = try container.themeValue(.shape, "shape", default: shape)
        line = try container.themeValue(.line, "line", default: line)
        text = try container.themeValue(.text, "text", default: text)
        number = try container.themeValue(.number, "number", default: number)
        canvas = try container.themeValue(.canvas, "canvas", default: canvas)
    }
}

extension EditorTheme.ShapeGroup: Codable {
    private enum CodingKeys: String, CodingKey {
        case strokeColor, strokeWidth, fillColor, gradientEndColor, gradientDirection, shadowBlur, shadowColor
    }

    init(from decoder: Decoder) throws {
        self.init()
        let container = try decoder.container(keyedBy: CodingKeys.self)
        strokeColor = try container.themeValue(.strokeColor, "shape.strokeColor", default: strokeColor)
        strokeWidth = try container.themeValue(.strokeWidth, "shape.strokeWidth", default: strokeWidth)
        fillColor = try container.themeValue(.fillColor, "shape.fillColor", default: fillColor)
        gradientEndColor = try container.themeNullableString(.gradientEndColor, "shape.gradientEndColor")
        gradientDirection = try container.themeValue(.gradientDirection, "shape.gradientDirection", default: gradientDirection)
        shadowBlur = try container.themeValue(.shadowBlur, "shape.shadowBlur", default: shadowBlur)
        shadowColor = try container.themeValue(.shadowColor, "shape.shadowColor", default: shadowColor)
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(strokeColor, forKey: .strokeColor)
        try container.encode(strokeWidth, forKey: .strokeWidth)
        try container.encode(fillColor, forKey: .fillColor)
        // Written as null, not left out, to match the shared format.
        if let gradientEndColor {
            try container.encode(gradientEndColor, forKey: .gradientEndColor)
        } else {
            try container.encodeNil(forKey: .gradientEndColor)
        }
        try container.encode(gradientDirection, forKey: .gradientDirection)
        try container.encode(shadowBlur, forKey: .shadowBlur)
        try container.encode(shadowColor, forKey: .shadowColor)
    }
}

extension EditorTheme.LineGroup: Codable {
    private enum CodingKeys: String, CodingKey {
        case color, width, arrowStyle
    }

    init(from decoder: Decoder) throws {
        self.init()
        let container = try decoder.container(keyedBy: CodingKeys.self)
        color = try container.themeValue(.color, "line.color", default: color)
        width = try container.themeValue(.width, "line.width", default: width)
        arrowStyle = try container.themeValue(.arrowStyle, "line.arrowStyle", default: arrowStyle)
    }
}

extension EditorTheme.TextBoxGroup: Codable {
    private enum CodingKeys: String, CodingKey {
        case backgroundColor, borderColor, borderWidth, padding, cornerRadius
    }

    init(from decoder: Decoder) throws {
        self.init()
        let container = try decoder.container(keyedBy: CodingKeys.self)
        backgroundColor = try container.themeValue(.backgroundColor, "text.box.backgroundColor", default: backgroundColor)
        borderColor = try container.themeValue(.borderColor, "text.box.borderColor", default: borderColor)
        borderWidth = try container.themeValue(.borderWidth, "text.box.borderWidth", default: borderWidth)
        padding = try container.themeValue(.padding, "text.box.padding", default: padding)
        cornerRadius = try container.themeValue(.cornerRadius, "text.box.cornerRadius", default: cornerRadius)
    }
}

extension EditorTheme.TextGroup: Codable {
    private enum CodingKeys: String, CodingKey {
        case color, fontFamily, fontSize, bold, italic, underline, box
    }

    init(from decoder: Decoder) throws {
        self.init()
        let container = try decoder.container(keyedBy: CodingKeys.self)
        color = try container.themeValue(.color, "text.color", default: color)
        fontFamily = try container.themeNullableString(.fontFamily, "text.fontFamily")
        fontSize = try container.themeValue(.fontSize, "text.fontSize", default: fontSize)
        bold = try container.themeValue(.bold, "text.bold", default: bold)
        italic = try container.themeValue(.italic, "text.italic", default: italic)
        underline = try container.themeValue(.underline, "text.underline", default: underline)
        box = try container.themeValue(.box, "text.box", default: box)
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(color, forKey: .color)
        // Written as null, not left out, to match the shared format.
        if let fontFamily {
            try container.encode(fontFamily, forKey: .fontFamily)
        } else {
            try container.encodeNil(forKey: .fontFamily)
        }
        try container.encode(fontSize, forKey: .fontSize)
        try container.encode(bold, forKey: .bold)
        try container.encode(italic, forKey: .italic)
        try container.encode(underline, forKey: .underline)
        try container.encode(box, forKey: .box)
    }
}

extension EditorTheme.NumberGroup: Codable {
    private enum CodingKeys: String, CodingKey {
        case badgeColor, textColor, size
    }

    init(from decoder: Decoder) throws {
        self.init()
        let container = try decoder.container(keyedBy: CodingKeys.self)
        badgeColor = try container.themeValue(.badgeColor, "number.badgeColor", default: badgeColor)
        textColor = try container.themeValue(.textColor, "number.textColor", default: textColor)
        size = try container.themeValue(.size, "number.size", default: size)
    }
}

extension EditorTheme.CanvasGroup: Codable {
    private enum CodingKeys: String, CodingKey {
        case applyOnOpen, background, color, secondaryColor, gradientDirection, padding, cornerRadius, shadow, frame
        case horizontalAlignment, verticalAlignment
    }

    init(from decoder: Decoder) throws {
        self.init()
        let container = try decoder.container(keyedBy: CodingKeys.self)
        applyOnOpen = try container.themeValue(.applyOnOpen, "canvas.applyOnOpen", default: applyOnOpen)
        background = try container.themeValue(.background, "canvas.background", default: background)
        color = try container.themeValue(.color, "canvas.color", default: color)
        secondaryColor = try container.themeValue(.secondaryColor, "canvas.secondaryColor", default: secondaryColor)
        gradientDirection = try container.themeValue(.gradientDirection, "canvas.gradientDirection", default: gradientDirection)
        padding = try container.themeValue(.padding, "canvas.padding", default: padding)
        cornerRadius = try container.themeValue(.cornerRadius, "canvas.cornerRadius", default: cornerRadius)
        shadow = try container.themeValue(.shadow, "canvas.shadow", default: shadow)
        frame = try container.themeValue(.frame, "canvas.frame", default: frame)
        horizontalAlignment = try container.themeValue(.horizontalAlignment, "canvas.horizontalAlignment", default: horizontalAlignment)
        verticalAlignment = try container.themeValue(.verticalAlignment, "canvas.verticalAlignment", default: verticalAlignment)
    }
}

extension EditorTheme {
    /// Decodes, validates, and normalizes one theme. Every failure is an `EditorThemeValidationError`.
    static func parse(_ data: Data) throws -> EditorTheme {
        do {
            return try JSONDecoder().decode(EditorTheme.self, from: data).validated()
        } catch let error as EditorThemeValidationError {
            throw error
        } catch {
            throw EditorThemeValidationError(field: "theme", reason: "isn't a theme object.")
        }
    }

    static func parse(jsonObject: Any) throws -> EditorTheme {
        guard jsonObject is [String: Any],
              let data = try? JSONSerialization.data(withJSONObject: jsonObject) else {
            throw EditorThemeValidationError(field: "theme", reason: "isn't a theme object.")
        }
        return try parse(data)
    }

    func encodedData() throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return try encoder.encode(self)
    }

    /// The theme as plain JSON values, which is what agents exchange.
    func jsonObject() throws -> [String: Any] {
        guard let object = try JSONSerialization.jsonObject(with: try encodedData()) as? [String: Any] else {
            throw EditorThemeValidationError(field: "theme", reason: "isn't a theme object.")
        }
        return object
    }
}

// MARK: - Validation

extension EditorTheme {
    /// Applies the format's rules and returns the theme with uppercase `#AARRGGBB` colors and a
    /// trimmed name. Values are rejected rather than clamped.
    func validated() throws -> EditorTheme {
        var theme = self
        guard version == Self.currentVersion else {
            throw EditorThemeValidationError(field: "version", reason: "must be \(Self.currentVersion).")
        }
        guard (1...Self.maximumIDLength).contains(id.utf8.count),
              id.utf8.allSatisfy({ byte in
                  (48...57).contains(byte) || (65...90).contains(byte) || (97...122).contains(byte) || byte == 45
              }) else {
            throw EditorThemeValidationError(
                field: "id",
                reason: "must be 1 to \(Self.maximumIDLength) letters, digits, or hyphens."
            )
        }
        theme.name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard (1...Self.maximumNameLength).contains(theme.name.count) else {
            throw EditorThemeValidationError(field: "name", reason: "must be 1 to \(Self.maximumNameLength) characters.")
        }

        theme.shape.strokeColor = try Self.checkedColor(shape.strokeColor, "shape.strokeColor")
        try Self.check(shape.strokeWidth, EditorThemeRange.strokeWidth, "shape.strokeWidth")
        theme.shape.fillColor = try Self.checkedColor(shape.fillColor, "shape.fillColor")
        if let endColor = shape.gradientEndColor {
            theme.shape.gradientEndColor = try Self.checkedColor(endColor, "shape.gradientEndColor")
            guard EditorThemeColor.isVisible(theme.shape.fillColor) else {
                throw EditorThemeValidationError(field: "shape.gradientEndColor", reason: "needs a visible shape.fillColor.")
            }
        }
        try Self.check(shape.gradientDirection, EditorThemeRange.direction, "shape.gradientDirection")
        try Self.check(shape.shadowBlur, EditorThemeRange.shadowBlur, "shape.shadowBlur")
        theme.shape.shadowColor = try Self.checkedColor(shape.shadowColor, "shape.shadowColor")

        theme.line.color = try Self.checkedColor(line.color, "line.color")
        try Self.check(line.width, EditorThemeRange.strokeWidth, "line.width")
        try Self.checkName(line.arrowStyle, EditorThemeNames.arrowStyles, "line.arrowStyle")

        theme.text.color = try Self.checkedColor(text.color, "text.color")
        try Self.check(text.fontSize, EditorThemeRange.fontSize, "text.fontSize")
        theme.text.box.backgroundColor = try Self.checkedColor(text.box.backgroundColor, "text.box.backgroundColor")
        theme.text.box.borderColor = try Self.checkedColor(text.box.borderColor, "text.box.borderColor")
        try Self.check(text.box.borderWidth, EditorThemeRange.boxBorderWidth, "text.box.borderWidth")
        try Self.check(text.box.padding, EditorThemeRange.boxPadding, "text.box.padding")
        try Self.check(text.box.cornerRadius, EditorThemeRange.boxCornerRadius, "text.box.cornerRadius")

        theme.number.badgeColor = try Self.checkedColor(number.badgeColor, "number.badgeColor")
        theme.number.textColor = try Self.checkedColor(number.textColor, "number.textColor")
        try Self.check(number.size, EditorThemeRange.numberSize, "number.size")

        try Self.checkName(canvas.background, EditorThemeNames.backgrounds, "canvas.background")
        theme.canvas.color = try Self.checkedColor(canvas.color, "canvas.color")
        theme.canvas.secondaryColor = try Self.checkedColor(canvas.secondaryColor, "canvas.secondaryColor")
        try Self.check(canvas.gradientDirection, EditorThemeRange.direction, "canvas.gradientDirection")
        try Self.check(canvas.padding, EditorThemeRange.canvasPadding, "canvas.padding")
        try Self.check(canvas.cornerRadius, EditorThemeRange.canvasCornerRadius, "canvas.cornerRadius")
        try Self.check(canvas.shadow, EditorThemeRange.canvasShadow, "canvas.shadow")
        try Self.checkName(canvas.frame, EditorThemeNames.frames, "canvas.frame")
        try Self.checkName(canvas.horizontalAlignment, EditorThemeNames.horizontalAlignments, "canvas.horizontalAlignment")
        try Self.checkName(canvas.verticalAlignment, EditorThemeNames.verticalAlignments, "canvas.verticalAlignment")
        return theme
    }

    private static func checkedColor(_ value: String, _ field: String) throws -> String {
        guard let normalized = EditorThemeColor.normalizedHex(value) else {
            throw EditorThemeValidationError(field: field, reason: "must be #RRGGBB or #AARRGGBB.")
        }
        return normalized
    }

    private static func check(_ value: Double, _ range: ClosedRange<Double>, _ field: String) throws {
        guard value.isFinite, range.contains(value) else {
            throw EditorThemeValidationError(
                field: field,
                reason: "must be from \(range.lowerBound.formatted()) to \(range.upperBound.formatted())."
            )
        }
    }

    private static func checkName(_ value: String, _ allowed: [String], _ field: String) throws {
        guard allowed.contains(value) else {
            throw EditorThemeValidationError(field: field, reason: "must be one of \(allowed.joined(separator: ", ")).")
        }
    }
}

// MARK: - Editor values

/// The format's named values, and how they map to the editor's own enums.
enum EditorThemeNames {
    static let arrowStyles = ["straight", "curvedLeft", "curvedRight"]
    static let backgrounds = ["transparent", "solid", "gradient"]
    static let frames = ["original", "square", "landscape4x3", "landscape16x9", "portrait3x4", "portrait9x16"]
    static let horizontalAlignments = ["left", "center", "right"]
    static let verticalAlignments = ["top", "center", "bottom"]

    static func arrowStyle(_ name: String) -> ArrowStyle {
        ArrowStyle(rawValue: name) ?? .straight
    }

    static func name(for style: ArrowStyle) -> String {
        style.rawValue
    }

    static func background(_ name: String) -> ExportBackgroundStyle {
        switch name {
        case "solid": return .solid
        case "gradient": return .gradient
        default: return .transparent
        }
    }

    /// Nil for a wallpaper, because themes leave out images.
    static func name(for style: ExportBackgroundStyle) -> String? {
        switch style {
        case .transparent: return "transparent"
        case .solid: return "solid"
        case .gradient: return "gradient"
        case .wallpaper: return nil
        }
    }

    static func frame(_ name: String) -> ExportFramePreset {
        switch name {
        case "square": return .square
        case "landscape4x3": return .landscapeFourByThree
        case "landscape16x9": return .landscapeSixteenByNine
        case "portrait3x4": return .portraitThreeByFour
        case "portrait9x16": return .portraitNineBySixteen
        default: return .original
        }
    }

    static func name(for preset: ExportFramePreset) -> String {
        switch preset {
        case .original: return "original"
        case .square: return "square"
        case .landscapeFourByThree: return "landscape4x3"
        case .landscapeSixteenByNine: return "landscape16x9"
        case .portraitThreeByFour: return "portrait3x4"
        case .portraitNineBySixteen: return "portrait9x16"
        }
    }

    static func horizontalAlignment(_ name: String) -> ExportHorizontalAlignment {
        switch name {
        case "left": return .leading
        case "right": return .trailing
        default: return .center
        }
    }

    static func name(for alignment: ExportHorizontalAlignment) -> String {
        switch alignment {
        case .leading: return "left"
        case .center: return "center"
        case .trailing: return "right"
        }
    }

    static func verticalAlignment(_ name: String) -> ExportVerticalAlignment {
        ExportVerticalAlignment(rawValue: name) ?? .center
    }

    static func name(for alignment: ExportVerticalAlignment) -> String {
        alignment.rawValue
    }
}
