import Foundation

// MARK: - Resolver

/// A caption as a video shows it, in output time.
struct StudioResolvedCaption: Equatable, Sendable {
    var start: Double
    var end: Double
    var text: String
}

/// Turns a project's cues into the captions its video has: section 15.2 of the project format.
/// Both the files and the drawn captions come from here, so they cannot disagree.
enum StudioCaptionResolver {
    /// The shortest caption there is, in seconds. Less than this cannot be read.
    static let shortestPart = 0.1

    /// The captions of `project`, in time order and clear of each other.
    static func resolve(project: StudioProject) -> [StudioResolvedCaption] {
        resolve(
            captions: project.captions,
            timeMap: StudioTimeMap(sourceDuration: project.sources.screen.duration, edits: project.edits)
        )
    }

    /// The captions `captions` gives a video with this time map.
    static func resolve(captions: StudioCaptions, timeMap: StudioTimeMap) -> [StudioResolvedCaption] {
        var parts: [StudioResolvedCaption] = []
        for cue in usableCues(captions.cues) {
            var elapsed = 0.0
            for piece in timeMap.pieces {
                // A piece at another rate has no sound (section 7), so there is nothing to caption.
                if piece.rate == 1, let part = part(of: cue, in: piece, elapsed: elapsed) {
                    parts.append(part)
                }
                elapsed += piece.outputDuration
            }
        }

        let ordered = inOrder(parts, start: \.start, end: \.end)
        var result: [StudioResolvedCaption] = []
        result.reserveCapacity(ordered.count)
        for part in ordered {
            if let last = result.last, last.end > part.start {
                result[result.count - 1].end = part.start
                if part.start - last.start < shortestPart {
                    result.removeLast()
                }
            }
            result.append(part)
        }
        return result
    }

    /// Where in `captions` the caption showing at `outputTime` is, or nil when none is. `captions`
    /// comes from `resolve`.
    static func index(in captions: [StudioResolvedCaption], at outputTime: Double) -> Int? {
        var low = 0
        var high = captions.count - 1
        while low <= high {
            let middle = low + (high - low) / 2
            if outputTime < captions[middle].start {
                high = middle - 1
            } else if outputTime >= captions[middle].end {
                low = middle + 1
            } else {
                return middle
            }
        }
        return nil
    }

    /// Every line without the space around it, and no blank lines.
    static func cleanText(_ text: String) -> String {
        lines(of: text)
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
            .joined(separator: "\n")
    }

    /// The lines of a text, whatever ends them. It goes by Unicode scalars because a Swift
    /// `Character` holds "\r\n" as one.
    static func lines(of text: String) -> [String] {
        var lines: [String] = []
        var current = String.UnicodeScalarView()
        var afterReturn = false
        for scalar in text.unicodeScalars {
            if scalar == "\n" {
                if !afterReturn {
                    lines.append(String(current))
                    current = String.UnicodeScalarView()
                }
                afterReturn = false
            } else if scalar == "\r" {
                lines.append(String(current))
                current = String.UnicodeScalarView()
                afterReturn = true
            } else {
                current.append(scalar)
                afterReturn = false
            }
        }
        lines.append(String(current))
        return lines
    }

    // MARK: - Private

    private struct UsableCue {
        var start: Double
        var end: Double
        var text: String
        var words: [StudioCaptionWord]
    }

    /// Sorted by start, then end. What is equal in both keeps its order: `sorted` does not promise to.
    private static func inOrder<T>(_ items: [T], start: (T) -> Double, end: (T) -> Double) -> [T] {
        items.enumerated().sorted { first, second in
            if start(first.element) != start(second.element) { return start(first.element) < start(second.element) }
            if end(first.element) != end(second.element) { return end(first.element) < end(second.element) }
            return first.offset < second.offset
        }.map(\.element)
    }

    private static func usableCues(_ cues: [StudioCaptionCue]) -> [UsableCue] {
        let usable = cues.compactMap { cue -> UsableCue? in
            let text = cleanText(cue.text)
            guard !text.isEmpty, cue.start.isFinite, cue.end.isFinite, cue.end > cue.start else { return nil }
            let words = cue.words.filter {
                $0.start.isFinite && $0.end.isFinite && $0.end >= $0.start
                    && !$0.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            }
            return UsableCue(start: cue.start, end: cue.end, text: text, words: inOrder(words, start: \.start, end: \.end))
        }
        return inOrder(usable, start: \.start, end: \.end)
    }

    private static func part(of cue: UsableCue, in piece: StudioTimePiece, elapsed: Double) -> StudioResolvedCaption? {
        var start = max(cue.start, piece.start)
        var end = min(cue.end, piece.end)
        guard end > start else { return nil }

        var text = cue.text
        if !cue.words.isEmpty {
            let kept = cue.words.filter {
                let middle = ($0.start + $0.end) / 2
                return middle >= piece.start && middle < piece.end
            }
            guard let first = kept.first, let last = kept.last else { return nil }
            if kept.count < cue.words.count {
                text = kept.map { $0.text.trimmingCharacters(in: .whitespacesAndNewlines) }.joined(separator: " ")
                start = max(piece.start, first.start)
                end = min(piece.end, last.end)
            }
        }

        guard end - start >= shortestPart else { return nil }
        return StudioResolvedCaption(start: elapsed + (start - piece.start), end: elapsed + (end - piece.start), text: text)
    }
}

// MARK: - Files

/// A kind of caption file.
enum StudioCaptionFormat: String, CaseIterable, Sendable {
    case srt
    case vtt

    /// The extension of the kind of file, without its dot.
    var fileExtension: String { rawValue }

    /// The kind of file a path's extension names, or nil.
    init?(path: String) {
        self.init(rawValue: (path as NSString).pathExtension.lowercased())
    }
}

/// What the times of a caption file count from: section 15.5 of the project format.
enum StudioCaptionTiming: String, Sendable {
    /// The recording, as the project keeps them.
    case recording

    /// The edited video, such as a file made from an exported video.
    case editedVideo
}

/// Writes and reads SRT and WebVTT text: section 15.3 of the project format.
enum StudioCaptionFiles {
    /// What `read` found in a file.
    struct Contents: Equatable, Sendable {
        /// The cues, in the order of the file, each with an id of its own and no words.
        var cues: [StudioCaptionCue]

        /// How many blocks of the file could not be read as a cue.
        var skippedBlocks: Int
    }

    enum FileError: LocalizedError {
        case tooLarge
        case notText

        var errorDescription: String? {
            switch self {
            case .tooLarge: return "This file is too large to be a caption file."
            case .notText: return "This file could not be read as text."
            }
        }
    }

    /// A caption file is text. Anything larger than this is not one.
    static let largestFile = 64 * 1024 * 1024

    /// The text of a file of that kind.
    static func write(_ format: StudioCaptionFormat, _ captions: [StudioResolvedCaption]) -> String {
        format == .vtt ? writeVTT(captions) : writeSRT(captions)
    }

    /// The cues of a file with the given timing, in source time. A file timed to the edited video
    /// goes through `timeMap`, and a cue with no time left is counted as skipped.
    static func read(_ text: String, timing: StudioCaptionTiming, timeMap: StudioTimeMap) -> Contents {
        var contents = read(text)
        guard timing == .editedVideo else { return contents }
        let count = contents.cues.count
        contents.cues = contents.cues.compactMap { cue in
            var mapped = cue
            mapped.start = timeMap.outputToSource(cue.start)
            mapped.end = timeMap.outputToSource(cue.end)
            return mapped.end > mapped.start ? mapped : nil
        }
        contents.skippedBlocks += count - contents.cues.count
        return contents
    }

    /// The text of a caption file: UTF-8 unless a byte order mark says otherwise.
    static func readFile(_ url: URL) throws -> String {
        let data = try Data(contentsOf: url, options: .mappedIfSafe)
        guard data.count <= largestFile else { throw FileError.tooLarge }
        for (mark, encoding) in byteOrderMarks where data.starts(with: mark) {
            guard let text = String(data: data, encoding: encoding) else { throw FileError.notText }
            return text
        }
        guard let text = String(data: data, encoding: .utf8) ?? String(data: data, encoding: .windowsCP1252) else {
            throw FileError.notText
        }
        return text
    }

    // The longer marks first: UTF-16's little-endian mark is how UTF-32's starts.
    private static let byteOrderMarks: [([UInt8], String.Encoding)] = [
        ([0xFF, 0xFE, 0x00, 0x00], .utf32LittleEndian),
        ([0x00, 0x00, 0xFE, 0xFF], .utf32BigEndian),
        ([0xEF, 0xBB, 0xBF], .utf8),
        ([0xFF, 0xFE], .utf16LittleEndian),
        ([0xFE, 0xFF], .utf16BigEndian),
    ]

    /// The text of an SRT file. Empty with no captions.
    static func writeSRT(_ captions: [StudioResolvedCaption]) -> String {
        var text = ""
        for (index, caption) in captions.enumerated() {
            if index > 0 { text += "\n" }
            text += "\(index + 1)\n\(timing(of: caption, separator: ","))\n\(caption.text)\n"
        }
        return text
    }

    /// The text of a WebVTT file. Only its header with no captions.
    static func writeVTT(_ captions: [StudioResolvedCaption]) -> String {
        var text = "WEBVTT\n"
        for caption in captions {
            let escaped = caption.text
                .replacingOccurrences(of: "&", with: "&amp;")
                .replacingOccurrences(of: "<", with: "&lt;")
                .replacingOccurrences(of: ">", with: "&gt;")
            text += "\n\(timing(of: caption, separator: "."))\n\(escaped)\n"
        }
        return text
    }

    /// The cues of an SRT or WebVTT file. Text that is neither has no cues.
    static func read(_ text: String) -> Contents {
        var blocks = readBlocks(text)
        let isVTT = blocks.first.map { startsWithKeyword($0[0], "WEBVTT") } ?? false
        if isVTT { blocks.removeFirst() }

        var cues: [StudioCaptionCue] = []
        var skipped = 0
        for block in blocks {
            if isVTT, ["NOTE", "STYLE", "REGION"].contains(where: { startsWithKeyword(block[0], $0) }) {
                continue
            }
            if let cue = readCue(block, isVTT: isVTT) {
                cues.append(cue)
            } else {
                skipped += 1
            }
        }
        return Contents(cues: cues, skippedBlocks: skipped)
    }

    // MARK: - Private

    private static let timingExpression = expression(
        "^\\s*(?:([0-9]+):)?([0-9]{1,2}):([0-9]{2})[.,]([0-9]{1,3})\\s*-->\\s*(?:([0-9]+):)?([0-9]{1,2}):([0-9]{2})[.,]([0-9]{1,3})"
    )

    // An SRT has no way to write a literal tag, so only the tags it has go.
    private static let srtMarkup = expression("</?(?:[bBiIuUsS]|[fF][oO][nN][tT])(?:\\s[^<>]*)?>|\\{\\\\[^{}]*\\}")

    // A VTT escapes every literal "<", so anything shaped like a tag is one.
    private static let vttMarkup = expression("</?[A-Za-z][^<>]*>|<[0-9][0-9:.]*>")

    private static func expression(_ pattern: String) -> NSRegularExpression {
        // The patterns are constants, so one that does not compile is a mistake in this file.
        try! NSRegularExpression(pattern: pattern)
    }

    private static func timing(of caption: StudioResolvedCaption, separator: String) -> String {
        "\(timestamp(caption.start, separator: separator)) --> \(timestamp(caption.end, separator: separator))"
    }

    private static func timestamp(_ seconds: Double, separator: String) -> String {
        let ms = Int64((seconds * 1000 + 0.5).rounded(.down))
        return String(format: "%02lld:%02lld:%02lld", ms / 3_600_000, ms / 60000 % 60, ms / 1000 % 60)
            + separator
            + String(format: "%03lld", ms % 1000)
    }

    private static func readBlocks(_ text: String) -> [[String]] {
        var scalars = String.UnicodeScalarView(text.unicodeScalars)
        if scalars.first == "\u{FEFF}" { scalars.removeFirst() }

        var blocks: [[String]] = []
        var current: [String] = []
        for line in StudioCaptionResolver.lines(of: String(scalars)) {
            if line.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                if !current.isEmpty { blocks.append(current) }
                current = []
            } else {
                current.append(line)
            }
        }
        if !current.isEmpty { blocks.append(current) }
        return blocks
    }

    private static func startsWithKeyword(_ line: String, _ keyword: String) -> Bool {
        line == keyword || line.hasPrefix(keyword + " ") || line.hasPrefix(keyword + "\t")
    }

    private static func readCue(_ block: [String], isVTT: Bool) -> StudioCaptionCue? {
        // The timing is the first line, or the second after a number or a name.
        for index in 0..<min(2, block.count) {
            let line = block[index] as NSString
            guard let match = timingExpression.firstMatch(in: block[index], range: NSRange(location: 0, length: line.length)) else {
                continue
            }
            let start = seconds(from: match, firstGroup: 1, in: line)
            let end = seconds(from: match, firstGroup: 5, in: line)

            let joined = block[(index + 1)...].joined(separator: "\n")
            let markup = isVTT ? vttMarkup : srtMarkup
            var body = markup.stringByReplacingMatches(
                in: joined,
                range: NSRange(location: 0, length: (joined as NSString).length),
                withTemplate: ""
            )
            if isVTT {
                body = body
                    .replacingOccurrences(of: "&lt;", with: "<")
                    .replacingOccurrences(of: "&gt;", with: ">")
                    .replacingOccurrences(of: "&nbsp;", with: "\u{00A0}")
                    .replacingOccurrences(of: "&lrm;", with: "\u{200E}")
                    .replacingOccurrences(of: "&rlm;", with: "\u{200F}")
                    .replacingOccurrences(of: "&amp;", with: "&")
            }
            body = StudioCaptionResolver.cleanText(body)
            guard end > start, !body.isEmpty else { return nil }
            return StudioCaptionCue(id: UUID().uuidString.lowercased(), start: start, end: end, text: body)
        }
        return nil
    }

    private static func seconds(from match: NSTextCheckingResult, firstGroup: Int, in line: NSString) -> Double {
        func number(_ group: Int) -> Int64 {
            let range = match.range(at: group)
            return range.location == NSNotFound ? 0 : Int64(line.substring(with: range)) ?? 0
        }
        let fractionRange = match.range(at: firstGroup + 3)
        let fraction = Int64(line.substring(with: fractionRange).padding(toLength: 3, withPad: "0", startingAt: 0)) ?? 0
        let ms = number(firstGroup) * 3_600_000 + number(firstGroup + 1) * 60000 + number(firstGroup + 2) * 1000 + fraction
        return Double(ms) / 1000
    }
}
