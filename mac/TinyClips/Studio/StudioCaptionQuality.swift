import Foundation

/// What a check of the captions can find: section 15.6 of the project format, in its order.
enum StudioCaptionFindingKind: String, CaseIterable, Sendable {
    case invalidTiming
    case overlap
    case empty
    case whitespace
    case longLine
    case tooManyLines
    case shortDuration
    case readingSpeed
}

struct StudioCaptionFinding: Equatable, Sendable {
    /// The cue's place in time order, from 0.
    var cue: Int
    var kind: StudioCaptionFindingKind

    /// Whether `StudioCaptionQuality.fix` can do something about it.
    var canFix: Bool
}

/// Limits for reading captions, and the fixes that are safe to make: section 15.6 of the project
/// format. They come from OutroKit's CaptionQualityService.
enum StudioCaptionQuality {
    static let shortestDuration = 0.7
    static let longestLine = 42
    static let mostLines = 2
    static let fastestRate = 20.0

    private static let mostParts = 64

    /// The cues in the order the checks and `StudioCaptionResolver` use. What is equal in both
    /// keeps its order: `sorted` does not promise to.
    static func inOrder(_ cues: [StudioCaptionCue]) -> [StudioCaptionCue] {
        cues.enumerated().sorted { first, second in
            if first.element.start != second.element.start { return first.element.start < second.element.start }
            if first.element.end != second.element.end { return first.element.end < second.element.end }
            return first.offset < second.offset
        }.map(\.element)
    }

    /// What is found in the cues of a recording `sourceDuration` seconds long.
    static func check(_ cues: [StudioCaptionCue], sourceDuration: Double) -> [StudioCaptionFinding] {
        let ordered = inOrder(cues)
        var findings: [StudioCaptionFinding] = []
        var previous: StudioCaptionCue?
        for (index, cue) in ordered.enumerated() {
            guard cue.start.isFinite, cue.end.isFinite, cue.end > cue.start else {
                findings.append(StudioCaptionFinding(cue: index, kind: .invalidTiming, canFix: false))
                continue
            }
            if let previous, cue.start < previous.end {
                findings.append(StudioCaptionFinding(cue: index, kind: .overlap, canFix: cue.start - previous.start >= shortestDuration))
            }
            let tidied = tidy(cue.text)
            if tidied.isEmpty {
                findings.append(StudioCaptionFinding(cue: index, kind: .empty, canFix: true))
            } else {
                checkText(&findings, index: index, cue: cue, tidied: tidied, limit: limit(ordered, index, sourceDuration))
            }
            previous = cue
        }
        return findings
    }

    /// Makes the safe fixes, in the order of the format.
    static func fix(_ cues: [StudioCaptionCue], sourceDuration: Double) -> (cues: [StudioCaptionCue], applied: Int) {
        let ordered = inOrder(cues)
        var kept = ordered.filter { !tidy($0.text).isEmpty }
        var applied = ordered.count - kept.count

        for index in kept.indices {
            let tidied = tidy(kept[index].text)
            if tidied != kept[index].text {
                kept[index].text = tidied
                applied += 1
            }
        }

        for index in kept.indices.dropFirst() where kept[index].start < kept[index - 1].end
            && kept[index].start - kept[index - 1].start >= shortestDuration {
            kept[index - 1].end = kept[index].start
            applied += 1
        }

        var laidOut: [StudioCaptionCue] = []
        laidOut.reserveCapacity(kept.count)
        for cue in kept {
            applied += layOut(cue, into: &laidOut)
        }

        for index in laidOut.indices {
            let cue = laidOut[index]
            let length = cue.end - cue.start
            guard length > 0 else { continue }
            let target = wanted(length: length, text: cue.text)
            if target > length, cue.start + target <= limit(laidOut, index, sourceDuration) {
                laidOut[index].end = cue.start + target
                applied += 1
            }
        }
        return (laidOut, applied)
    }

    /// The findings by count, such as "2 long lines, 1 too short". Empty with none.
    static func summary(_ findings: [StudioCaptionFinding]) -> String {
        var parts: [String] = []
        for kind in StudioCaptionFindingKind.allCases {
            let count = findings.filter { $0.kind == kind }.count
            if count > 0 {
                parts.append("\(count) \(kindText(kind, count: count))")
            }
        }
        return parts.joined(separator: ", ")
    }

    /// Each line with single spaces and nothing around it, and no blank lines.
    static func tidy(_ text: String) -> String {
        StudioCaptionResolver.lines(of: text)
            .map { words(of: $0).joined(separator: " ") }
            .filter { !$0.isEmpty }
            .joined(separator: "\n")
    }

    // MARK: - Private

    private static func checkText(
        _ findings: inout [StudioCaptionFinding],
        index: Int,
        cue: StudioCaptionCue,
        tidied: String,
        limit: Double
    ) {
        if tidied != cue.text {
            findings.append(StudioCaptionFinding(cue: index, kind: .whitespace, canFix: true))
        }

        let lines = tidied.components(separatedBy: "\n")
        let isLong = lines.contains { length(of: $0) > longestLine }
        let isTall = lines.count > mostLines
        if isLong || isTall {
            let canFix = wrap(tidied) != nil || canSplit(cue, split(tidied))
            if isLong {
                findings.append(StudioCaptionFinding(cue: index, kind: .longLine, canFix: canFix))
            }
            if isTall {
                findings.append(StudioCaptionFinding(cue: index, kind: .tooManyLines, canFix: canFix))
            }
        }

        let cueLength = cue.end - cue.start
        let canExtend = cue.start + wanted(length: cueLength, text: tidied) <= limit
        if cueLength < shortestDuration {
            findings.append(StudioCaptionFinding(cue: index, kind: .shortDuration, canFix: canExtend))
        }
        if Double(count(of: tidied)) / cueLength > fastestRate {
            findings.append(StudioCaptionFinding(cue: index, kind: .readingSpeed, canFix: canExtend))
        }
    }

    /// Adds the cue to the list, wrapped or split where it has to be, and says how many fixes that was.
    private static func layOut(_ cue: StudioCaptionCue, into laidOut: inout [StudioCaptionCue]) -> Int {
        let lines = cue.text.components(separatedBy: "\n")
        if lines.count <= mostLines, lines.allSatisfy({ length(of: $0) <= longestLine }) {
            laidOut.append(cue)
            return 0
        }
        if let wrapped = wrap(cue.text) {
            var copy = cue
            copy.text = wrapped
            laidOut.append(copy)
            return 1
        }
        guard let parts = split(cue.text), canSplit(cue, parts) else {
            laidOut.append(cue)
            return 0
        }
        for (place, timing) in splitTiming(start: cue.start, end: cue.end, parts: parts).enumerated() {
            var copy = cue
            if place > 0 { copy.id = UUID().uuidString.lowercased() }
            copy.start = timing.start
            copy.end = timing.end
            copy.text = parts[place]
            copy.words = []
            laidOut.append(copy)
        }
        return 1
    }

    private static func limit(_ ordered: [StudioCaptionCue], _ index: Int, _ sourceDuration: Double) -> Double {
        index + 1 == ordered.count ? sourceDuration : min(ordered[index + 1].start, sourceDuration)
    }

    private static func wanted(length: Double, text: String) -> Double {
        let target = length < shortestDuration ? 1 : length
        let reading = Double(count(of: text)) / fastestRate
        return reading > target ? reading : target
    }

    private static func wrap(_ text: String) -> String? {
        let all = words(of: text)
        guard !all.isEmpty, all.allSatisfy({ length(of: $0) <= longestLine }) else { return nil }
        for groupCount in 1...mostLines {
            let lines = balance(all, into: groupCount).map { $0.joined(separator: " ") }
            if lines.allSatisfy({ length(of: $0) <= longestLine }) {
                return lines.joined(separator: "\n")
            }
        }
        return nil
    }

    private static func split(_ text: String) -> [String]? {
        let all = words(of: text)
        guard all.count >= 2, all.allSatisfy({ length(of: $0) <= longestLine }) else { return nil }
        let joinedLength = Double(length(of: all.joined(separator: " ")))
        let first = max(2, Int((joinedLength / Double(longestLine * mostLines)).rounded(.up)))
        let last = min(mostParts, all.count)
        guard first <= last else { return nil }
        for groupCount in first...last {
            let parts = balance(all, into: groupCount).map { wrap($0.joined(separator: " ")) }
            if parts.allSatisfy({ $0 != nil }) {
                return parts.compactMap { $0 }
            }
        }
        return nil
    }

    private static func canSplit(_ cue: StudioCaptionCue, _ parts: [String]?) -> Bool {
        guard let parts else { return false }
        return splitTiming(start: cue.start, end: cue.end, parts: parts).allSatisfy { $0.end - $0.start >= shortestDuration }
    }

    private static func balance(_ all: [String], into groupCount: Int) -> [[String]] {
        if groupCount <= 1 { return [all] }
        if all.count <= groupCount { return all.map { [$0] } }

        var groups: [[String]] = []
        var cursor = 0
        for group in 1..<groupCount {
            let target = Double(length(of: all[cursor...].joined(separator: " "))) / Double(groupCount - group + 1)
            var best = cursor + 1
            var bestScore = Double.infinity
            var groupLength = -1
            for index in cursor..<(all.count - (groupCount - group)) {
                groupLength += length(of: all[index]) + 1
                let score = abs(Double(groupLength) - target) - bonus(all[index])
                if score < bestScore {
                    bestScore = score
                    best = index + 1
                }
            }
            groups.append(Array(all[cursor..<best]))
            cursor = best
        }
        groups.append(Array(all[cursor...]))
        return groups
    }

    private static func bonus(_ word: String) -> Double {
        switch word.unicodeScalars.last {
        case ".", "!", "?": return 6
        case ",", ";", ":": return 3
        default: return 0
        }
    }

    private static func splitTiming(start: Double, end: Double, parts: [String]) -> [(start: Double, end: Double)] {
        let weights = parts.map { max(1, count(of: $0)) }
        let total = weights.reduce(0, +)
        let cueLength = max(0, end - start)
        var timings: [(start: Double, end: Double)] = []
        var cursor = start
        for (index, weight) in weights.enumerated() {
            let following = index == parts.count - 1 ? end : cursor + cueLength * (Double(weight) / Double(total))
            timings.append((cursor, following))
            cursor = following
        }
        return timings
    }

    private static func words(of text: String) -> [String] {
        text.unicodeScalars
            .split { $0.properties.isWhitespace }
            .map { String(String.UnicodeScalarView($0)) }
    }

    // A character is a Unicode code point, on every platform.
    private static func length(of text: String) -> Int {
        text.unicodeScalars.count
    }

    private static func count(of text: String) -> Int {
        text.unicodeScalars.filter { !$0.properties.isWhitespace }.count
    }

    private static func kindText(_ kind: StudioCaptionFindingKind, count: Int) -> String {
        switch kind {
        case .invalidTiming: return count == 1 ? "invalid time" : "invalid times"
        case .overlap: return count == 1 ? "overlap" : "overlaps"
        case .empty: return count == 1 ? "empty caption" : "empty captions"
        case .whitespace: return "with extra space"
        case .longLine: return count == 1 ? "long line" : "long lines"
        case .tooManyLines: return "with too many lines"
        case .shortDuration: return "too short"
        case .readingSpeed: return "too fast to read"
        }
    }
}

/// What the editor says about captions. The same, word for word, on Windows (`StudioCaptionText`).
enum StudioCaptionText {
    /// Said about where the sound goes.
    static let privacyNote = "Your sound stays on this Mac. Only the model is downloaded."

    /// Said before generating over captions that are there.
    static let replaceNote = "This replaces the current captions. Undo brings them back."

    /// Said when generating starts.
    static let generatingMessage = "Generating captions."

    /// Said from when stopping is asked for until it has stopped, which can take a while.
    static let stoppingMessage = "Stopping captions\u{2026}"

    /// Said when generating was stopped.
    static let cancelledMessage = "Generating captions stopped. The captions are as they were."

    /// What came of generating.
    static func generatedMessage(_ count: Int) -> String {
        count == 1 ? "Generated 1 caption." : "Generated \(count) captions."
    }

    /// How far generating is: `Recognizing speech: 40%`.
    static func progressText(_ progress: StudioCaptionProgress) -> String {
        let percent = Int((min(1, max(0, progress.fraction)) * 100).rounded())
        switch progress.stage {
        case .readingSound: return "Reading the sound: \(percent)%"
        case .downloadingModel: return "Downloading the model: \(percent)%"
        case .preparingModel: return "Getting the model ready\u{2026}"
        case .recognizingSpeech: return "Recognizing speech: \(percent)%"
        }
    }

    static func modelNote(_ model: StudioCaptionModel, isInstalled: Bool) -> String {
        isInstalled
            ? "\(model.guidance). Already on this Mac."
            : "\(model.guidance). Downloads \(model.sizeText) the first time."
    }

    static func generateActionText(_ model: StudioCaptionModel, isInstalled: Bool) -> String {
        isInstalled ? "Generate" : "Download \(model.sizeText) and Generate"
    }

    /// How much room the models take, in the words of Settings.
    static func installedModelsText(count: Int, bytes: Int64) -> String {
        if count == 0 {
            return "No models downloaded."
        }
        let size = ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
        return count == 1 ? "1 model downloaded, using \(size)." : "\(count) models downloaded, using \(size)."
    }

    /// What the panel says when a project has no captions.
    static let emptyHint = "Generate captions from the recording's sound, or import an SRT or WebVTT file."

    /// What the panel says about what an export leaves out.
    static let exportNote = "Exports are timed to the edited video. Captions in cut or sped-up parts are left out."

    /// What the checks say when they find nothing.
    static let noProblems = "No problems found."

    /// Said about drawing captions into the picture.
    static let drawnNote = "Drawn captions are part of the picture, so viewers cannot turn them off. Export a file as well for players that can."

    /// Said when a file has no cues in it.
    static let nothingToImport = "No captions could be read from that file."

    /// Said when captions have been written to a file.
    static let exportedMessage = "Captions exported."

    /// How many cues there are: `No captions yet`, `1 caption`, `12 captions`.
    static func countText(_ count: Int) -> String {
        switch count {
        case 0: return "No captions yet"
        case 1: return "1 caption"
        default: return "\(count) captions"
        }
    }

    /// When a cue starts and ends, in source time: `12.0 to 16.5 seconds`.
    static func rangeText(_ cue: StudioCaptionCue) -> String {
        let start = String(format: "%.1f", cue.start.isFinite ? cue.start : 0)
        let end = String(format: "%.1f", cue.end.isFinite ? cue.end : 0)
        return "\(start) to \(end) seconds"
    }

    /// What a cue says, on one line. A cue with nothing to say says so.
    static func rowText(_ cue: StudioCaptionCue) -> String {
        let text = StudioCaptionResolver.cleanText(cue.text).replacingOccurrences(of: "\n", with: " ")
        return text.isEmpty ? "(Empty)" : text
    }

    /// What came of an import: `Imported 12 captions. 1 block could not be read.`
    static func importedMessage(count: Int, skipped: Int) -> String {
        let imported = count == 1 ? "Imported 1 caption." : "Imported \(count) captions."
        switch skipped {
        case 0: return imported
        case 1: return "\(imported) 1 block could not be read."
        default: return "\(imported) \(skipped) blocks could not be read."
        }
    }

    /// What came of Auto Fix: `Made 3 fixes.`
    static func fixedMessage(_ applied: Int) -> String {
        switch applied {
        case 0: return "Nothing to fix."
        case 1: return "Made 1 fix."
        default: return "Made \(applied) fixes."
        }
    }
}
