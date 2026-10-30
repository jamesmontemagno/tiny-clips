import Foundation

enum SensitiveTextKind: String, Sendable {
    case email = "Email"
    case phone = "Phone"
    case ipAddress = "IP address"
}

struct SensitiveTextMatch: Equatable {
    let kind: SensitiveTextKind
    let range: NSRange
}

struct SensitiveTextSuggestion: Identifiable, Sendable {
    let id = UUID()
    let kind: SensitiveTextKind
    let text: String
    /// Normalized, top-left image coordinates, independent of display density and zoom.
    let rect: CGRect
    var isApproved = false
}

enum SensitiveTextDetection {
    static func matches(in text: String) -> [SensitiveTextMatch] {
        var matches: [SensitiveTextMatch] = []
        func collect(_ pattern: String, kind: SensitiveTextKind, accepts: (String) -> Bool = { _ in true }) {
            guard let regex = try? NSRegularExpression(pattern: pattern) else { return }
            for result in regex.matches(in: text, range: NSRange(text.startIndex..., in: text)) {
                guard let range = Range(result.range, in: text),
                      accepts(String(text[range])),
                      !matches.contains(where: { NSIntersectionRange($0.range, result.range).length > 0 }) else { continue }
                matches.append(SensitiveTextMatch(kind: kind, range: result.range))
            }
        }
        collect(#"(?i)(?<![\w.!#$%&'*+/=?^`{|}~-])[A-Z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[A-Z0-9!#$%&'*+/=?^_`{|}~-]+)*@[A-Z0-9](?:[A-Z0-9-]*[A-Z0-9])?(?:\.[A-Z0-9](?:[A-Z0-9-]*[A-Z0-9])?)*\.[A-Z]{2,63}(?![\w-])"#, kind: .email)
        collect(#"(?<![\w:.])(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?!\w|\.[0-9])"#, kind: .ipAddress, accepts: isIPv4)
        collect(#"(?i)(?<![\w:.])[A-F0-9:.]*:[A-F0-9:]+(?:\.[0-9]+)*(?![\w:])"#, kind: .ipAddress, accepts: isIPv6)
        collect(#"(?<![\w.+-])\+?(?:\([0-9]{1,4}\)|[0-9])[0-9() .-]{5,}[0-9](?![\w.-])"#, kind: .phone) { value in
            let digits = value.filter(\.isNumber).count
            guard (7...15).contains(digits),
                  value.contains(where: { "+() .-".contains($0) }),
                  value.filter({ $0 == "(" }).count == value.filter({ $0 == ")" }).count else { return false }
            // Dates, decimals, and version numbers are not phone suggestions.
            if value.range(of: #"^(?:\d{4}-\d{2}-\d{2}|\d{1,2}-\d{1,2}-\d{4})(?:$|[ .-])"#, options: .regularExpression) != nil { return false }
            let groups = value.split(whereSeparator: { !$0.isNumber })
            if value.contains(".") && (groups.count != 3 || groups.map(\.count) != [3, 3, 4]) { return false }
            return true
        }
        return matches.sorted { $0.range.location < $1.range.location }
    }

    static func wordRanges(overlapping range: NSRange, in text: String) -> [NSRange] {
        guard let regex = try? NSRegularExpression(pattern: #"\S+"#) else { return [] }
        return regex.matches(in: text, range: NSRange(text.startIndex..., in: text))
            .map(\.range)
            .filter { NSIntersectionRange($0, range).length > 0 }
    }

    private static func isIPv4(_ value: String) -> Bool {
        let parts = value.split(separator: ".", omittingEmptySubsequences: false)
        return parts.count == 4 && parts.allSatisfy {
            !$0.isEmpty && $0.count <= 3 && ($0.count == 1 || !$0.hasPrefix("0"))
                && $0.allSatisfy(\.isNumber) && (Int($0) ?? 256) <= 255
        }
    }

    private static func isIPv6(_ value: String) -> Bool {
        guard value.contains(":"), !value.contains(":::") else { return false }
        let compressed = value.components(separatedBy: "::")
        guard compressed.count <= 2 else { return false }
        if compressed.count == 1 && (value.hasPrefix(":") || value.hasSuffix(":")) { return false }
        let parts = value.split(separator: ":")
        var count = 0
        for (index, part) in parts.enumerated() {
            if part.contains(".") {
                guard index == parts.count - 1, isIPv4(String(part)) else { return false }
                count += 2
            } else {
                guard part.count <= 4, part.allSatisfy(\.isHexDigit) else { return false }
                count += 1
            }
        }
        return compressed.count == 2 ? count < 8 : count == 8
    }

    static func imageRect(fromVision rect: CGRect, pixelSize: CGSize) -> CGRect? {
        guard pixelSize.width.isFinite, pixelSize.height.isFinite, pixelSize.width > 0, pixelSize.height > 0,
              rect.minX.isFinite, rect.minY.isFinite, rect.width.isFinite, rect.height.isFinite,
              rect.width > 0, rect.height > 0 else { return nil }
        // Include a small safety margin around OCR glyph bounds, rounded out to full pixels.
        let pixels = CGRect(
            x: rect.minX * pixelSize.width,
            y: (1 - rect.maxY) * pixelSize.height,
            width: rect.width * pixelSize.width,
            height: rect.height * pixelSize.height
        ).insetBy(dx: -2, dy: -2).integral
            .intersection(CGRect(origin: .zero, size: pixelSize))
        guard !pixels.isNull, !pixels.isEmpty else { return nil }
        return CGRect(x: pixels.minX / pixelSize.width, y: pixels.minY / pixelSize.height,
                      width: pixels.width / pixelSize.width, height: pixels.height / pixelSize.height)
    }
}
