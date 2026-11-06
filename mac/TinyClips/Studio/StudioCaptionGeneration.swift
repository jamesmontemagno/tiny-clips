import Foundation

// MARK: - Languages

/// A language Apple Speech can listen for on this Mac.
struct StudioCaptionLanguage: Identifiable, Equatable, Sendable {
    /// A BCP 47 tag.
    let id: String
    let name: String
    /// Whether macOS has it already. Otherwise it downloads it the first time.
    let isInstalled: Bool
}

enum StudioCaptionLanguages {
    /// The language to listen for: the stored choice, then the system's, then the first there is.
    static func choose(stored: String?, systemLanguage: String?, from languages: [StudioCaptionLanguage]) -> StudioCaptionLanguage? {
        languages.first { $0.id == stored } ?? languages.first { $0.id == systemLanguage } ?? languages.first
    }

    /// The reservation to give up so that `wanted` can have one. macOS limits how many languages
    /// an app may hold, and asking for one more is an error rather than a wait.
    static func reservationToRelease(reserved: [String], wanted: String, maximum: Int) -> String? {
        guard !reserved.contains(wanted), reserved.count >= max(1, maximum) else { return nil }
        return reserved.first
    }
}

// MARK: - Generating

/// How far generating captions is.
struct StudioCaptionProgress: Equatable, Sendable {
    enum Stage: Int, Sendable {
        case readingSound
        case downloadingLanguage
        case preparingSpeech
        case recognizingSpeech
        case creatingCaptions
    }

    let stage: Stage
    /// Nil while a stage has no measured progress.
    let fraction: Double?

    init(stage: Stage, fraction: Double? = nil) {
        self.stage = stage
        self.fraction = fraction.flatMap { $0.isFinite ? min(1, max(0, $0)) : nil }
    }

    /// Callbacks cross to the main actor independently, so an older update can arrive last.
    func advanced(to next: Self) -> Self {
        guard next.stage.rawValue >= stage.rawValue else { return self }
        guard next.stage == stage else { return next }
        guard let fraction = next.fraction, fraction >= (self.fraction ?? 0) else { return self }
        return next
    }
}

/// What speech recognition heard, ready to go in a project.
struct StudioGeneratedCaptions: Equatable, Sendable {
    /// The language it heard as a BCP 47 tag, or nil when it does not say.
    var language: String?
    var cues: [StudioCaptionCue]
}

/// Why captions could not be made. The message is written for whoever asked for them.
struct StudioCaptionGenerationError: LocalizedError, Equatable {
    let message: String

    var errorDescription: String? { message }

    static let noSound = StudioCaptionGenerationError(message: "This recording has no sound to make captions from.")
    static let noSpeech = StudioCaptionGenerationError(message: "No speech was found in this recording.")
    static let missingRecording = StudioCaptionGenerationError(message: "The screen recording is missing, so it cannot be listened to.")
    static let unreadableSound = StudioCaptionGenerationError(message: "The sound of the screen recording could not be read.")
}

/// Makes captions from a recording's sound, on this computer.
protocol StudioCaptionGenerating: Sendable {
    /// Stops with `CancellationError` when its task is cancelled.
    /// - Parameter language: A BCP 47 tag to listen for, or nil for the system's.
    func generate(
        project: StudioProject,
        paths: StudioProjectPaths,
        language: String?,
        progress: @escaping @Sendable (StudioCaptionProgress) -> Void
    ) async throws -> StudioGeneratedCaptions
}

// MARK: - Transcript

/// A stretch of speech as speech recognition reports it, in seconds of the recording.
struct StudioTranscriptSegment: Equatable, Sendable {
    var start: Double
    var end: Double
    var text: String
    var words: [StudioCaptionWord] = []
}

/// Turns what speech recognition says into cues. Pure, so that what it looks like, as read from
/// a real run, can be tested without a model.
enum StudioCaptionTranscript {
    /// The longest a cue is made: two full lines (section 15.6 of the project format).
    static var longestCue: Int { StudioCaptionQuality.longestLine * StudioCaptionQuality.mostLines }

    static func cues(from segments: [StudioTranscriptSegment], sourceDuration: Double) -> [StudioCaptionCue] {
        segments.flatMap { cues(from: $0, sourceDuration: sourceDuration) }
    }

    private static func cues(from segment: StudioTranscriptSegment, sourceDuration: Double) -> [StudioCaptionCue] {
        let text = StudioCaptionResolver.cleanText(segment.text)
        let end = min(segment.end, sourceDuration)
        guard !text.isEmpty, end > segment.start else { return [] }

        let words = spelledWords(of: segment, text: text, sourceDuration: sourceDuration)
        // Counted in Unicode scalars, as the checks count them, so a part made here is not split
        // again by the safe fixes, which would lose its words.
        guard text.unicodeScalars.count > longestCue, !words.isEmpty else {
            return [cue(start: segment.start, end: end, text: text, words: words)]
        }

        // Too long for one cue. It is broken between words, so each part keeps the words it says.
        // Each part's text comes out of the text itself: not every language puts spaces between
        // its words, and a word can have one inside it, as Apple Speech's `out of` does.
        guard let places = self.places(of: words, in: text) else {
            return [cue(start: segment.start, end: end, text: text, words: [])]
        }
        var parts: [Range<Int>] = []
        var first = 0
        for index in words.indices where index > first {
            if text[places[first].lowerBound..<places[index].upperBound].unicodeScalars.count > longestCue {
                parts.append(first..<index)
                first = index
            }
        }
        parts.append(first..<words.count)

        return parts.enumerated().map { partIndex, part in
            let partStart = partIndex == 0 ? segment.start : words[part.lowerBound].start
            let next = partIndex + 1 < parts.count ? words[parts[partIndex + 1].lowerBound].start : end
            return cue(
                start: partStart,
                end: max(next, partStart + StudioCaptionResolver.shortestPart),
                text: String(text[places[part.lowerBound].lowerBound..<places[part.upperBound - 1].upperBound]),
                words: Array(words[part])
            )
        }
    }

    /// Where each word is in `text`, or nil when the words do not spell it out from its start.
    static func places(of words: [StudioCaptionWord], in text: String) -> [Range<String.Index>]? {
        var index = text.startIndex
        var places: [Range<String.Index>] = []
        places.reserveCapacity(words.count)
        for word in words {
            var start: String.Index?
            for character in word.text where !character.isWhitespace {
                while index < text.endIndex, text[index].isWhitespace {
                    index = text.index(after: index)
                }
                guard index < text.endIndex, text[index] == character else { return nil }
                start = start ?? index
                index = text.index(after: index)
            }
            places.append((start ?? index)..<index)
        }
        return places
    }

    /// Whether `words` spell out `text`, leaving out the space between and around them.
    static func spells(_ words: [StudioCaptionWord], _ text: String) -> Bool {
        words.map(\.text).joined().filter { !$0.isWhitespace } == text.filter { !$0.isWhitespace }
    }

    private static func cue(start: Double, end: Double, text: String, words: [StudioCaptionWord]) -> StudioCaptionCue {
        StudioCaptionCue(id: UUID().uuidString.lowercased(), start: start, end: end, text: text, words: words)
    }

    /// The segment's words, when they spell out its text. A cue without words is only clipped by
    /// a cut, which is right, and a cue with wrong words would say something else.
    private static func spelledWords(of segment: StudioTranscriptSegment, text: String, sourceDuration: Double) -> [StudioCaptionWord] {
        let words = segment.words.compactMap { word -> StudioCaptionWord? in
            let cleaned = word.text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !cleaned.isEmpty else { return nil }
            return StudioCaptionWord(start: min(word.start, sourceDuration), end: min(word.end, sourceDuration), text: cleaned)
        }
        return spells(words, text) ? words : []
    }
}
