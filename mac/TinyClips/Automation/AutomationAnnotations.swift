import AppKit
import SwiftUI

/// Parses `editor.annotate` annotations into the screenshot editor's own annotation model, so
/// the result renders exactly like an edit made in the editor.
enum AutomationAnnotations {
    static let maximumCount = 500
    /// Editor text sizes are relative to an 800-pixel-wide image. Stroke widths are image pixels.
    static let editorTextReferenceWidth: CGFloat = 800

    private static let allowedKeys: Set<String> = [
        "type", "bounds", "points", "text", "color", "textColor", "fillColor",
        "lineWidth", "fontSize", "redactionLevel", "redactionStyle", "emoji",
    ]

    /// Validates every annotation against `imagePixelSize` and returns editor annotations in
    /// normalized coordinates. Agent sizes (`lineWidth`, `fontSize`) are image pixels.
    static func parse(_ value: Any?, imagePixelSize: CGSize) throws -> [ScreenshotAnnotation] {
        guard imagePixelSize.width >= 1, imagePixelSize.height >= 1 else {
            throw AutomationError(.invalidParams, "The image has no pixels.")
        }
        guard let items = value as? [Any], !items.isEmpty else {
            throw AutomationError(.invalidParams, "annotations must be a non-empty array.")
        }
        guard items.count <= maximumCount else {
            throw AutomationError(.invalidParams, "annotations can have at most \(maximumCount) items.")
        }
        var nextNumber = 1
        return try items.enumerated().map { index, item in
            guard let object = item as? [String: Any] else {
                throw AutomationError(.invalidParams, "annotations[\(index)] must be an object.")
            }
            return try parseAnnotation(object, index: index, imagePixelSize: imagePixelSize, nextNumber: &nextNumber)
        }
    }

    private static func parseAnnotation(
        _ object: [String: Any],
        index: Int,
        imagePixelSize size: CGSize,
        nextNumber: inout Int
    ) throws -> ScreenshotAnnotation {
        let label = "annotations[\(index)]"
        func fail(_ message: String) -> AutomationError {
            AutomationError(.invalidParams, "\(label).\(message)")
        }
        if let unknown = object.keys.sorted().first(where: { !allowedKeys.contains($0) }) {
            throw fail("\(unknown) is not a known field.")
        }

        guard let type = object["type"] as? String else { throw fail("type is required.") }
        let tool: EditTool
        switch type {
        case "rectangle": tool = .rectangle
        case "ellipse": tool = .circle
        case "arrow": tool = .arrow
        case "line": tool = .line
        case "pen": tool = .pencil
        case "text": tool = .text
        case "number": tool = .number
        case "emoji": tool = .emoji
        case "redact": tool = .blur
        default:
            throw fail("type must be rectangle, ellipse, arrow, line, pen, text, number, emoji, or redact.")
        }

        let bounds = try parseBounds(object["bounds"], imagePixelSize: size, label: label)
        let isLinear = tool == .arrow || tool == .line || tool == .pencil
        if !isLinear, bounds.width <= 0 || bounds.height <= 0 {
            throw fail("bounds width and height must be greater than 0 for \(type).")
        }

        var points = try parsePoints(object["points"], imagePixelSize: size, label: label)
        switch tool {
        case .pencil:
            guard points.count >= 2 else { throw fail("points needs at least 2 points for pen.") }
        case .arrow, .line:
            if points.isEmpty {
                guard bounds.width > 0 || bounds.height > 0 else {
                    throw fail("bounds must have a width or height, or give 2 points, for \(type).")
                }
                points = [bounds.origin, CGPoint(x: bounds.maxX, y: bounds.maxY)]
            } else if points.count != 2 {
                throw fail("points must have exactly 2 points (start, end) for \(type).")
            } else if points[0] == points[1] {
                throw fail("points start and end must differ for \(type).")
            }
        default:
            if object["points"] != nil {
                throw fail("points is only used by pen, arrow, and line.")
            }
        }

        let color = try parseColor(object["color"], key: "color", label: label) ?? .red
        let textColor = try parseColor(object["textColor"], key: "textColor", label: label) ?? .white
        let fillColor = try parseColor(object["fillColor"], key: "fillColor", label: label) ?? .clear

        var lineWidth = AnnotationStrokeMath.defaultWidth(forImagePixelWidth: size.width)
        if object["lineWidth"] != nil {
            guard let pixels = AutomationValue.double(object["lineWidth"]), pixels.isFinite, pixels >= 0, pixels <= 500 else {
                throw fail("lineWidth must be a number from 0 to 500.")
            }
            lineWidth = CGFloat(pixels)
        }

        var annotation = ScreenshotAnnotation(
            tool: tool,
            rect: CGRect(
                x: bounds.minX / size.width,
                y: bounds.minY / size.height,
                width: bounds.width / size.width,
                height: bounds.height / size.height
            ),
            color: color,
            textColor: textColor,
            fillColor: fillColor,
            lineWidth: lineWidth,
            text: "",
            points: points.map { CGPoint(x: $0.x / size.width, y: $0.y / size.height) }
        )

        let text = try optionalString(object["text"], key: "text", label: label)
        switch tool {
        case .text:
            guard let text, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                throw fail("text is required for text annotations.")
            }
            annotation.text = text
            if object["fontSize"] != nil {
                guard let pixels = AutomationValue.double(object["fontSize"]), pixels.isFinite, pixels >= 1, pixels <= 2000 else {
                    throw fail("fontSize must be a number from 1 to 2000.")
                }
                annotation.fontSize = CGFloat(pixels) * editorTextReferenceWidth / size.width
            } else {
                // Fill most of the box height when no size is given.
                annotation.fontSize = max(1, bounds.height * 0.7) * editorTextReferenceWidth / size.width
            }
        case .number:
            if let text {
                guard !text.isEmpty, text.count <= 4 else { throw fail("text must be 1 to 4 characters for number.") }
                annotation.text = text
            } else {
                annotation.text = "\(nextNumber)"
                nextNumber += 1
            }
        case .emoji:
            let value = try optionalString(object["emoji"], key: "emoji", label: label) ?? text
            guard let value, let emoji = EmojiAnnotationMath.emoji(from: value), emoji == value else {
                throw fail("emoji must be a single emoji character.")
            }
            annotation.text = emoji
        default:
            if text != nil { throw fail("text is only used by text and number.") }
        }
        if tool != .emoji, object["emoji"] != nil { throw fail("emoji is only used by emoji.") }
        if tool != .text, object["fontSize"] != nil { throw fail("fontSize is only used by text.") }

        let level = try optionalString(object["redactionLevel"], key: "redactionLevel", label: label)
        let style = try optionalString(object["redactionStyle"], key: "redactionStyle", label: label)
        if tool == .blur {
            if let level {
                guard let preset = RedactionBlurPreset(rawValue: level) else {
                    throw fail("redactionLevel must be light, medium, or heavy.")
                }
                annotation.redactionBlurPreset = preset
            }
            switch style {
            case nil, "blur": break
            case "pixelate": annotation.isPixelatedRedaction = true
            case "solid": annotation.isOpaqueRedaction = true
            default: throw fail("redactionStyle must be blur, pixelate, or solid.")
            }
        } else if level != nil || style != nil {
            throw fail("redactionLevel and redactionStyle are only used by redact.")
        }
        return annotation
    }

    static func parseBounds(_ value: Any?, imagePixelSize size: CGSize, label: String) throws -> CGRect {
        guard let object = value as? [String: Any] else {
            throw AutomationError(.invalidParams, "\(label).bounds must be an object with x, y, width, and height.")
        }
        let keys: Set<String> = ["x", "y", "width", "height"]
        if let unknown = object.keys.sorted().first(where: { !keys.contains($0) }) {
            throw AutomationError(.invalidParams, "\(label).bounds has unknown field \(unknown).")
        }
        func number(_ key: String) throws -> CGFloat {
            guard let value = AutomationValue.double(object[key]), value.isFinite else {
                throw AutomationError(.invalidParams, "\(label).bounds.\(key) must be a number.")
            }
            return CGFloat(value)
        }
        let rect = CGRect(x: try number("x"), y: try number("y"), width: try number("width"), height: try number("height"))
        guard rect.minX >= 0, rect.minY >= 0, rect.width >= 0, rect.height >= 0,
              rect.maxX <= size.width, rect.maxY <= size.height else {
            throw AutomationError(
                .invalidParams,
                "\(label).bounds must fit inside the image (\(Int(size.width))×\(Int(size.height)) pixels)."
            )
        }
        return rect
    }

    static func parsePoints(_ value: Any?, imagePixelSize size: CGSize, label: String) throws -> [CGPoint] {
        guard let value, !(value is NSNull) else { return [] }
        guard let items = value as? [Any], items.count <= 10_000 else {
            throw AutomationError(.invalidParams, "\(label).points must be an array of at most 10000 {x, y} objects.")
        }
        return try items.enumerated().map { index, item in
            guard let point = item as? [String: Any], Set(point.keys) == ["x", "y"],
                  let x = AutomationValue.double(point["x"]), let y = AutomationValue.double(point["y"]),
                  x.isFinite, y.isFinite else {
                throw AutomationError(.invalidParams, "\(label).points[\(index)] must be {x, y} numbers.")
            }
            guard x >= 0, y >= 0, x <= Double(size.width), y <= Double(size.height) else {
                throw AutomationError(.invalidParams, "\(label).points[\(index)] is outside the image.")
            }
            return CGPoint(x: x, y: y)
        }
    }

    private static func optionalString(_ value: Any?, key: String, label: String) throws -> String? {
        switch value {
        case nil, is NSNull: return nil
        case let string as String: return string
        default: throw AutomationError(.invalidParams, "\(label).\(key) must be a string.")
        }
    }

    private static func parseColor(_ value: Any?, key: String, label: String) throws -> Color? {
        guard let string = try optionalString(value, key: key, label: label) else { return nil }
        guard let components = hexComponents(string) else {
            throw AutomationError(.invalidParams, "\(label).\(key) must be #RRGGBB or #AARRGGBB.")
        }
        return Color(nsColor: NSColor(
            srgbRed: components.red,
            green: components.green,
            blue: components.blue,
            alpha: components.alpha
        ))
    }

    /// Parses `#RRGGBB` or `#AARRGGBB` into 0–1 components.
    static func hexComponents(_ string: String) -> (red: CGFloat, green: CGFloat, blue: CGFloat, alpha: CGFloat)? {
        guard string.hasPrefix("#") else { return nil }
        let hex = string.dropFirst()
        guard hex.count == 6 || hex.count == 8,
              hex.allSatisfy(\.isHexDigit),
              let value = UInt32(hex, radix: 16) else { return nil }
        let alpha = hex.count == 8 ? CGFloat((value >> 24) & 0xFF) / 255 : 1
        return (
            CGFloat((value >> 16) & 0xFF) / 255,
            CGFloat((value >> 8) & 0xFF) / 255,
            CGFloat(value & 0xFF) / 255,
            alpha
        )
    }

    /// Renders `annotations` onto `sourceURL` with the screenshot editor's export path and writes
    /// the result to `destinationURL`. The source file is never modified.
    @MainActor
    static func render(
        sourceURL: URL,
        annotationsJSON: Any?,
        destination: (_ fileExtension: String) -> URL
    ) throws -> (url: URL, count: Int) {
        let viewModel = ScreenshotEditorViewModel(url: sourceURL)
        guard let image = viewModel.originalImage,
              let rep = image.representations.first, rep.pixelsWide > 0, rep.pixelsHigh > 0 else {
            throw AutomationError(.invalidParams, "\(sourceURL.lastPathComponent) is not a readable image.")
        }
        let annotations = try parse(
            annotationsJSON,
            imagePixelSize: CGSize(width: rep.pixelsWide, height: rep.pixelsHigh)
        )
        viewModel.annotations = annotations
        guard let url = viewModel.save(to: destination(viewModel.saveFormat.rawValue)) else {
            throw AutomationError(.internal, "Could not save the annotated screenshot.")
        }
        return (url, annotations.count)
    }
}
