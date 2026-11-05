import Foundation

// MARK: - Models

/// A speech model captions can be generated with. The ids are the same on both platforms, and
/// agents use them, so that a choice means the same model wherever it is made.
struct StudioCaptionModel: Identifiable, Equatable, Sendable {
    let id: String
    let name: String
    /// About how large its download is, for whoever is asked to agree to it.
    let sizeText: String
    let approximateBytes: Int64
    /// What it is good for.
    let guidance: String
    /// Its folder in the repository WhisperKit downloads from.
    let variant: String

    /// Whether it only knows English, and so has no language to detect.
    var isEnglishOnly: Bool { variant.hasSuffix(".en") }

    /// Whether it is the speech recognition built into macOS, which is told a language rather than
    /// detecting one, and whose languages macOS downloads and manages.
    var isAppleSpeech: Bool { id == StudioCaptionModels.appleSpeech.id }
}

/// A language Apple Speech can listen for on this Mac.
struct StudioCaptionLanguage: Identifiable, Equatable, Sendable {
    /// A BCP 47 tag.
    let id: String
    let name: String
    /// Whether macOS has it already. Otherwise it downloads it the first time.
    let isInstalled: Bool
}

enum StudioCaptionModels {
    private static let megabyte: Int64 = 1_000_000

    static let all: [StudioCaptionModel] = [
        StudioCaptionModel(id: "Tiny", name: "Tiny", sizeText: "77 MB", approximateBytes: 77 * megabyte, guidance: "Fastest, lowest accuracy", variant: "openai_whisper-tiny"),
        StudioCaptionModel(id: "TinyEn", name: "Tiny English", sizeText: "150 MB", approximateBytes: 150 * megabyte, guidance: "Fastest for English", variant: "openai_whisper-tiny.en"),
        StudioCaptionModel(id: "Base", name: "Base", sizeText: "147 MB", approximateBytes: 147 * megabyte, guidance: "Balanced, for most computers", variant: "openai_whisper-base"),
        StudioCaptionModel(id: "BaseEn", name: "Base English", sizeText: "147 MB", approximateBytes: 147 * megabyte, guidance: "Balanced, for English", variant: "openai_whisper-base.en"),
        StudioCaptionModel(id: "Small", name: "Small", sizeText: "486 MB", approximateBytes: 486 * megabyte, guidance: "More accurate, slower", variant: "openai_whisper-small"),
        StudioCaptionModel(id: "SmallEn", name: "Small English", sizeText: "487 MB", approximateBytes: 487 * megabyte, guidance: "More accurate for English, slower", variant: "openai_whisper-small.en"),
        StudioCaptionModel(id: "Medium", name: "Medium", sizeText: "1.5 GB", approximateBytes: 1530 * megabyte, guidance: "High accuracy, uses a lot of memory", variant: "openai_whisper-medium"),
        StudioCaptionModel(id: "MediumEn", name: "Medium English", sizeText: "1.5 GB", approximateBytes: 1530 * megabyte, guidance: "High accuracy for English, uses a lot of memory", variant: "openai_whisper-medium.en"),
        StudioCaptionModel(id: "LargeV3Turbo", name: "Large v3 Turbo", sizeText: "1.6 GB", approximateBytes: 1638 * megabyte, guidance: "Fast for its high accuracy", variant: "openai_whisper-large-v3-v20240930_turbo"),
        StudioCaptionModel(id: "LargeV3", name: "Large v3", sizeText: "3.1 GB", approximateBytes: 3090 * megabyte, guidance: "Highest accuracy, slowest", variant: "openai_whisper-large-v3"),
    ]

    static let defaultModel = all[2]

    /// The speech recognition built into macOS 26 and later. Not one of `all`, because not every
    /// Mac has it and nothing of it is kept in the app's own folder.
    static let appleSpeech = StudioCaptionModel(
        id: "AppleSpeech",
        name: "Apple Speech",
        sizeText: "built into macOS",
        approximateBytes: 0,
        guidance: "Fast, for the language you choose",
        variant: ""
    )

    static func find(_ id: String?) -> StudioCaptionModel? {
        id == appleSpeech.id ? appleSpeech : all.first { $0.id == id }
    }

    /// What can be chosen on this Mac, best first.
    static func options(appleSpeechIsAvailable: Bool) -> [StudioCaptionModel] {
        appleSpeechIsAvailable ? [appleSpeech] + all : all
    }

    /// The model to use for a stored choice. Without one, Apple Speech where it can hear the
    /// system's language, and otherwise a Whisper model, which detects the language by itself.
    static func resolve(id: String?, appleSpeechIsAvailable: Bool, canHearSystemLanguage: Bool = true) -> StudioCaptionModel {
        if id == appleSpeech.id {
            return appleSpeechIsAvailable ? appleSpeech : defaultModel
        }
        if let found = find(id) {
            return found
        }
        return appleSpeechIsAvailable && canHearSystemLanguage ? appleSpeech : defaultModel
    }
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

/// Where the speech models are kept. A model counts as there only when its download finished, so
/// one that was stopped is not taken for a model and is picked up again next time.
struct StudioCaptionModelStore: Sendable {
    private static let finishedMarker = ".tinyclips-finished"
    /// Where WhisperKit puts a model under the folder it is told to use.
    private static let repositoryPath = "models/argmaxinc/whisperkit-coreml"

    let directory: URL

    init(directory: URL = Self.defaultDirectory) {
        self.directory = directory
    }

    static var defaultDirectory: URL {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        return support.appendingPathComponent("TinyClips/Models", isDirectory: true)
    }

    func folder(for model: StudioCaptionModel) -> URL {
        directory
            .appendingPathComponent(Self.repositoryPath, isDirectory: true)
            .appendingPathComponent(model.variant, isDirectory: true)
    }

    func isInstalled(_ model: StudioCaptionModel) -> Bool {
        FileManager.default.fileExists(atPath: folder(for: model).appendingPathComponent(Self.finishedMarker).path)
    }

    func markInstalled(_ model: StudioCaptionModel) throws {
        try Data().write(to: folder(for: model).appendingPathComponent(Self.finishedMarker))
    }

    /// The models on this computer, in the order of the catalog.
    func installedModels() -> [StudioCaptionModel] {
        StudioCaptionModels.all.filter(isInstalled)
    }

    /// How much room everything in the folder takes, a download that was stopped included.
    func usedBytes() -> Int64 {
        guard let files = FileManager.default.enumerator(at: directory, includingPropertiesForKeys: [.fileSizeKey]) else {
            return 0
        }
        var total: Int64 = 0
        for case let file as URL in files {
            total += Int64((try? file.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0)
        }
        return total
    }

    /// Deletes every model, and whatever a download that was stopped left behind.
    func deleteAll() throws {
        if FileManager.default.fileExists(atPath: directory.path) {
            try FileManager.default.removeItem(at: directory)
        }
    }
}

// MARK: - Generating

/// How far generating captions is.
struct StudioCaptionProgress: Equatable, Sendable {
    enum Stage: Sendable {
        case readingSound
        case downloadingModel
        /// The system compiles a model for this Mac the first time it is used, without a measure of how far it is.
        case preparingModel
        case recognizingSpeech
    }

    var stage: Stage
    var fraction: Double
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
    func isModelInstalled(_ model: StudioCaptionModel) -> Bool

    /// Stops with `CancellationError` when its task is cancelled, which can take a few seconds.
    /// - Parameter language: A BCP 47 tag for Apple Speech to listen for, or nil for the system's.
    ///   A Whisper model detects the language and does not use it.
    func generate(
        project: StudioProject,
        paths: StudioProjectPaths,
        model: StudioCaptionModel,
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

    // What whisper says instead of text for a stretch without speech.
    private static let blankAudioMarker = "[BLANK_AUDIO]"

    static func cues(from segments: [StudioTranscriptSegment], sourceDuration: Double) -> [StudioCaptionCue] {
        segments.flatMap { cues(from: $0, sourceDuration: sourceDuration) }
    }

    /// `<|startoftranscript|>`, `<|0.00|>` and the like are the model's own marks, not speech.
    static func withoutMarks(_ text: String) -> String {
        guard text.contains("<|") else { return text }
        return text.replacingOccurrences(of: "<\\|[^|]*\\|>", with: "", options: .regularExpression)
    }

    private static func cues(from segment: StudioTranscriptSegment, sourceDuration: Double) -> [StudioCaptionCue] {
        let text = StudioCaptionResolver.cleanText(withoutMarks(segment.text))
        let end = min(segment.end, sourceDuration)
        guard !text.isEmpty, text != blankAudioMarker, end > segment.start else { return [] }

        let words = spelledWords(of: segment, text: text, sourceDuration: sourceDuration)
        guard text.count > longestCue, !words.isEmpty else {
            return [cue(start: segment.start, end: end, text: text, words: words)]
        }

        // Too long for one cue. It is broken between words, so each part keeps the words it says.
        // Each part's text comes out of the text itself: not every language puts spaces between
        // its words, and a word can have one inside it, as Apple Speech's `out of` does.
        let places = self.places(of: words, in: text)
        var parts: [Range<Int>] = []
        var first = 0
        for index in words.indices where index > first {
            if text[places[first].lowerBound..<places[index].upperBound].count > longestCue {
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

    /// Where each word is in `text`, which `spelledWords` has found them to spell out.
    private static func places(of words: [StudioCaptionWord], in text: String) -> [Range<String.Index>] {
        var index = text.startIndex
        return words.map { word in
            var start: String.Index?
            for character in word.text where !character.isWhitespace {
                while text[index].isWhitespace {
                    index = text.index(after: index)
                }
                start = start ?? index
                index = text.index(after: index)
            }
            return (start ?? index)..<index
        }
    }

    private static func cue(start: Double, end: Double, text: String, words: [StudioCaptionWord]) -> StudioCaptionCue {
        StudioCaptionCue(id: UUID().uuidString, start: start, end: end, text: text, words: words)
    }

    /// The segment's words, when they spell out its text. A cue without words is only clipped by
    /// a cut, which is right, and a cue with wrong words would say something else.
    private static func spelledWords(of segment: StudioTranscriptSegment, text: String, sourceDuration: Double) -> [StudioCaptionWord] {
        let words = segment.words.compactMap { word -> StudioCaptionWord? in
            let cleaned = withoutMarks(word.text).trimmingCharacters(in: .whitespacesAndNewlines)
            guard !cleaned.isEmpty else { return nil }
            return StudioCaptionWord(start: min(word.start, sourceDuration), end: min(word.end, sourceDuration), text: cleaned)
        }
        let spelled = words.map(\.text).joined().filter { !$0.isWhitespace }
        let expected = text.filter { !$0.isWhitespace }
        return spelled == expected ? words : []
    }
}
