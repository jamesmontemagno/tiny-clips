import Foundation

// MARK: - Drawing

/// A colour of a drawn caption. An opacity of 0 means the part it paints is not drawn.
struct StudioCaptionPaint: Equatable, Sendable {
    var red: UInt8
    var green: UInt8
    var blue: UInt8
    var opacity: Double

    static let none = StudioCaptionPaint(red: 0, green: 0, blue: 0, opacity: 0)
}

/// How a caption looks on a canvas of a given size, in that canvas's pixels: section 15.4 of the
/// project format. How the text is laid out is the renderer's business.
struct StudioCaptionStyle: Equatable, Sendable {
    /// The canvas's short side is this many units.
    static let unitsInShortSide = 288.0

    var textHeight: Double
    var margin: Double
    var longestLine: Double
    var isBold = false
    var text: StudioCaptionPaint
    var outline = StudioCaptionPaint.none
    /// How far the outline reaches outside the letters. 0 for none.
    var outlineWidth = 0.0
    var shadow = StudioCaptionPaint.none
    /// How far the shadow lies down and to the right. 0 for none.
    var shadowOffset = 0.0
    var panel = StudioCaptionPaint.none
    /// How far the panel reaches past the lines. 0 for none.
    var panelReach = 0.0

    /// How far anything is drawn outside the text's own box.
    var reach: Double {
        max(panelReach, outlineWidth + shadowOffset)
    }

    init(overlay: StudioCaptionOverlay, canvasWidth: Double, canvasHeight: Double) {
        let unit = min(canvasWidth, canvasHeight) / Self.unitsInShortSide
        switch overlay.size {
        case .small: textHeight = 14 * unit
        case .medium: textHeight = 18 * unit
        case .large: textHeight = 24 * unit
        }
        margin = 12 * unit
        longestLine = canvasWidth * 0.9

        let white = StudioCaptionPaint(red: 0xFF, green: 0xFF, blue: 0xFF, opacity: 1)
        let black = StudioCaptionPaint(red: 0, green: 0, blue: 0, opacity: 1)
        let halfBlack = StudioCaptionPaint(red: 0, green: 0, blue: 0, opacity: 0.5)
        switch overlay.appearance {
        case .classicOutline:
            text = white
            outline = black
            outlineWidth = 1.2 * unit
        case .contrastPanel:
            text = white
            panel = StudioCaptionPaint(red: 0, green: 0, blue: 0, opacity: 0.6)
            panelReach = 4 * unit
        case .boldImpact:
            isBold = true
            text = StudioCaptionPaint(red: 0xFF, green: 0xD7, blue: 0x00, opacity: 1)
            outline = black
            outlineWidth = 2.4 * unit
            shadow = black
            shadowOffset = 1.2 * unit
        case .cleanShadow:
            text = StudioCaptionPaint(red: 0xF5, green: 0xF5, blue: 0xF5, opacity: 1)
            outline = halfBlack
            outlineWidth = 0.6 * unit
            shadow = halfBlack
            shadowOffset = 1.5 * unit
        }
    }

    /// Where the top of the first line goes, counted down from the top of the canvas, for text
    /// `textHeight` tall. Text too tall for the canvas keeps its top on the canvas, because a
    /// caption starts at its first line.
    func topOfText(position: StudioCaptionPosition, textHeight: Double, canvasHeight: Double) -> Double {
        position == .top ? margin : max(0, canvasHeight - margin - textHeight)
    }
}

// MARK: - Time Order

/// Something that starts and ends, in seconds.
protocol StudioCaptionTimed {
    var start: Double { get }
    var end: Double { get }
}

extension StudioCaptionCue: StudioCaptionTimed {}
extension StudioCaptionWord: StudioCaptionTimed {}
extension StudioResolvedCaption: StudioCaptionTimed {}

extension Array where Element: StudioCaptionTimed {
    /// The places of the elements in time order: by start, then end. What is equal in both keeps
    /// its order, which `sorted` does not promise to.
    func indexesInTimeOrder() -> [Int] {
        indices.sorted { first, second in
            if self[first].start != self[second].start { return self[first].start < self[second].start }
            if self[first].end != self[second].end { return self[first].end < self[second].end }
            return first < second
        }
    }

    /// The elements in the order of `indexesInTimeOrder`. Captions, checks, and the panel's list
    /// all use this one order.
    func inTimeOrder() -> [Element] {
        indexesInTimeOrder().map { self[$0] }
    }
}

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

        let ordered = parts.inTimeOrder()
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

    private struct UsableCue: StudioCaptionTimed {
        var start: Double
        var end: Double
        var text: String
        var words: [StudioCaptionWord]
    }

    private static func usableCues(_ cues: [StudioCaptionCue]) -> [UsableCue] {
        let usable = cues.compactMap { cue -> UsableCue? in
            let text = cleanText(cue.text)
            guard !text.isEmpty, cue.start.isFinite, cue.end.isFinite, cue.end > cue.start else { return nil }
            let words = cue.words.filter {
                $0.start.isFinite && $0.end.isFinite && $0.end >= $0.start
                    && !$0.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            }
            return UsableCue(start: cue.start, end: cue.end, text: text, words: words.inTimeOrder())
        }
        return usable.inTimeOrder()
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

// MARK: - Editing

/// One end of a caption.
enum StudioCaptionEdge: Sendable {
    case start
    case end
}

/// The Captions panel's edits to the cues, all in source time. Each gives back every cue in time
/// order and where the cue it changed is now, or nil when the edit cannot be made or would change
/// nothing. No part is made shorter than `StudioCaptionResolver.shortestPart`. A cue keeps its
/// words only while each word's middle is inside it, as the resolver reads them.
enum StudioCaptionEditing {
    struct Result: Equatable {
        var cues: [StudioCaptionCue]
        /// Where the changed cue is in `cues`. After a split, the second part, which starts at
        /// the split.
        var index: Int
    }

    /// Moves one end of the cue at `index` to `time`, or as near to it as it can go: the start
    /// stays at or after 0 and a shortest part before the end, and the end a shortest part after
    /// the start and at or before the end of the recording.
    static func retime(
        _ cues: [StudioCaptionCue],
        at index: Int,
        edge: StudioCaptionEdge,
        to time: Double,
        sourceDuration: Double
    ) -> Result? {
        guard cues.indices.contains(index), time.isFinite else { return nil }
        let shortest = StudioCaptionResolver.shortestPart
        var cue = cues[index]
        switch edge {
        case .start:
            guard cue.end.isFinite, cue.end >= shortest else { return nil }
            cue.start = min(max(0, time), cue.end - shortest)
        case .end:
            guard cue.start.isFinite else { return nil }
            let earliest = cue.start + shortest
            let latest = sourceDuration.isFinite && sourceDuration > 0 ? max(earliest, sourceDuration) : .infinity
            cue.end = min(max(earliest, time), latest)
        }
        guard cue.start != cues[index].start || cue.end != cues[index].end else { return nil }
        if !wordsFit(cue.words, start: cue.start, end: cue.end) {
            cue.words = []
        }
        var changed = cues
        changed[index] = cue
        return inTimeOrder(changed, following: index)
    }

    /// Whether `cue` can be split at `time`: each part a shortest part long or more, with at least
    /// one word of the text in each.
    static func canSplit(_ cue: StudioCaptionCue, at time: Double) -> Bool {
        isInside(cue, time) && splitText(of: cue, at: time) != nil
    }

    /// Where Split cuts `cue`: at `playhead` when it can be split there, and otherwise in its
    /// middle, between the words nearest it. Nil when it cannot be split at all.
    static func splitTime(of cue: StudioCaptionCue, playhead: Double) -> Double? {
        if canSplit(cue, at: playhead) {
            return playhead
        }
        guard cue.start.isFinite, cue.end.isFinite else { return nil }
        let middle = (cue.start + cue.end) / 2
        if let gap = middleWordGap(of: cue, near: middle), canSplit(cue, at: gap) {
            return gap
        }
        return canSplit(cue, at: middle) ? middle : nil
    }

    /// Splits the cue at `index` in two at `time`. The words said before it go in the first part
    /// and the rest in the second, by the cue's word times when it has them, and otherwise by how
    /// far through the cue `time` is. The first part keeps the cue's id, and the second has a new one.
    static func split(_ cues: [StudioCaptionCue], at index: Int, time: Double) -> Result? {
        guard cues.indices.contains(index), isInside(cues[index], time),
              let parts = splitText(of: cues[index], at: time) else { return nil }
        let cue = cues[index]
        var first = cue
        first.end = time
        first.text = parts.firstText
        first.words = wordsFit(parts.firstWords, start: cue.start, end: time) ? parts.firstWords : []
        var second = cue
        second.id = UUID().uuidString.lowercased()
        second.start = time
        second.text = parts.secondText
        second.words = wordsFit(parts.secondWords, start: time, end: cue.end) ? parts.secondWords : []

        var changed = cues
        changed[index] = first
        changed.insert(second, at: index + 1)
        return inTimeOrder(changed, following: index + 1)
    }

    /// Whether the cue at `index` has one after it in time order to be put together with.
    static func canMergeWithNext(_ cues: [StudioCaptionCue], at index: Int) -> Bool {
        let order = cues.indexesInTimeOrder()
        guard let position = order.firstIndex(of: index), position + 1 < order.count else { return false }
        return [cues[index], cues[order[position + 1]]].allSatisfy { $0.start.isFinite && $0.end.isFinite }
    }

    /// Puts the cue at `index` together with the one after it in time order: from the earlier
    /// start to the later end, saying the one and then the other on a line. It keeps the first's
    /// id. Its words are both cues' words when each had words that spell out its text.
    static func mergeWithNext(_ cues: [StudioCaptionCue], at index: Int) -> Result? {
        guard canMergeWithNext(cues, at: index) else { return nil }
        let order = cues.indexesInTimeOrder()
        guard let position = order.firstIndex(of: index) else { return nil }
        let nextIndex = order[position + 1]
        let first = cues[index]
        let next = cues[nextIndex]

        var merged = first
        merged.start = min(first.start, next.start)
        merged.end = max(first.end, next.end)
        merged.text = [first.text, next.text]
            .map { StudioCaptionQuality.tidy($0).replacingOccurrences(of: "\n", with: " ") }
            .filter { !$0.isEmpty }
            .joined(separator: " ")
        let spellsBoth = [first, next].allSatisfy {
            !$0.words.isEmpty && StudioCaptionTranscript.spells($0.words, $0.text)
        }
        merged.words = spellsBoth ? first.words + next.words : []

        var changed = cues
        changed[index] = merged
        changed.remove(at: nextIndex)
        return inTimeOrder(changed, following: nextIndex < index ? index - 1 : index)
    }

    /// Where in `ordered`, which is in time order, the caption showing at `time` is, or nil when
    /// none is. A caption ends where the next one starts, as in section 15.2 of the project format.
    static func position<Element: StudioCaptionTimed>(at time: Double, in ordered: [Element]) -> Int? {
        guard time.isFinite else { return nil }
        var low = 0
        var high = ordered.count
        while low < high {
            let middle = low + (high - low) / 2
            if ordered[middle].start <= time {
                low = middle + 1
            } else {
                high = middle
            }
        }
        guard low > 0, ordered[low - 1].end > time else { return nil }
        return low - 1
    }

    /// The place, among `count` rows, of the next one that `hasProblem`: after `current`, or the
    /// first when there is no current row, going round to the first after the last. Nil when
    /// none has a problem.
    static func nextProblem(count: Int, after current: Int?, hasProblem: (Int) -> Bool) -> Int? {
        guard count > 0 else { return nil }
        let start = current.map { min(max($0 + 1, 0), count) } ?? 0
        return (start..<count).first(where: hasProblem) ?? (0..<start).first(where: hasProblem)
    }

    // MARK: - Private

    private struct SplitText {
        var firstText: String
        var secondText: String
        var firstWords: [StudioCaptionWord] = []
        var secondWords: [StudioCaptionWord] = []
    }

    private static func isInside(_ cue: StudioCaptionCue, _ time: Double) -> Bool {
        let shortest = StudioCaptionResolver.shortestPart
        return time.isFinite && cue.start.isFinite && cue.end.isFinite
            && time - cue.start >= shortest && cue.end - time >= shortest
    }

    // The time between two words that is nearest the middle, when the words spell out the text.
    private static func middleWordGap(of cue: StudioCaptionCue, near middle: Double) -> Double? {
        let words = cue.words
        guard words.count >= 2,
              StudioCaptionTranscript.spells(words, StudioCaptionResolver.cleanText(cue.text)) else { return nil }
        var best: Double?
        for count in 1..<words.count {
            let gap: Double = (words[count - 1].end + words[count].start) / 2
            if let nearest = best, abs(nearest - middle) <= abs(gap - middle) {
                continue
            }
            best = gap
        }
        return best
    }

    private static func splitText(of cue: StudioCaptionCue, at time: Double) -> SplitText? {
        let text = StudioCaptionResolver.cleanText(cue.text)
        let words = cue.words
        // Split where the words say, out of the text itself, so its spacing and its lines stay.
        if words.count >= 2, StudioCaptionTranscript.spells(words, text),
           let places = StudioCaptionTranscript.places(of: words, in: text) {
            let said = words.firstIndex { ($0.start + $0.end) / 2 >= time } ?? words.count
            let count = min(max(said, 1), words.count - 1)
            let firstText = StudioCaptionResolver.cleanText(String(text[..<places[count - 1].upperBound]))
            let secondText = StudioCaptionResolver.cleanText(String(text[places[count].lowerBound...]))
            guard !firstText.isEmpty, !secondText.isEmpty else { return nil }
            return SplitText(
                firstText: firstText,
                secondText: secondText,
                firstWords: Array(words[..<count]),
                secondWords: Array(words[count...])
            )
        }

        // Without word times, the text is split where its letters are shared most like the time.
        let tokens = text.unicodeScalars
            .split { $0.properties.isWhitespace }
            .map { String(String.UnicodeScalarView($0)) }
        guard tokens.count >= 2 else { return nil }
        let wanted = (time - cue.start) / (cue.end - cue.start)
        let lengths = tokens.map { $0.unicodeScalars.count }
        let total = Double(lengths.reduce(0, +))
        var best = 1
        var bestDistance = Double.infinity
        var before = 0
        for count in 1..<tokens.count {
            before += lengths[count - 1]
            let distance = abs(Double(before) / total - wanted)
            if distance < bestDistance {
                best = count
                bestDistance = distance
            }
        }
        return SplitText(
            firstText: tokens[..<best].joined(separator: " "),
            secondText: tokens[best...].joined(separator: " ")
        )
    }

    private static func wordsFit(_ words: [StudioCaptionWord], start: Double, end: Double) -> Bool {
        words.allSatisfy {
            let middle = ($0.start + $0.end) / 2
            return middle >= start && middle <= end
        }
    }

    private static func inTimeOrder(_ cues: [StudioCaptionCue], following index: Int) -> Result {
        let order = cues.indexesInTimeOrder()
        return Result(cues: order.map { cues[$0] }, index: order.firstIndex(of: index) ?? 0)
    }
}

// MARK: - Files

/// A kind of caption file.
enum StudioCaptionFormat: String, Sendable {
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
