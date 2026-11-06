import AVFoundation
import Speech
import XCTest
@testable import TinyClips

/// Generating captions, up to the speech engine: what the text of a real run turns into, and the
/// model catalog and folder. A real model is too large to download on every build.
final class StudioCaptionGenerationTests: XCTestCase {
    private func words(_ text: String, startingAt start: Double = 0, each: Double = 0.5) -> [StudioCaptionWord] {
        text.split(separator: " ").enumerated().map { index, word in
            // WhisperKit's words start with a space, as they did in a real run.
            StudioCaptionWord(start: start + Double(index) * each, end: start + Double(index + 1) * each, text: " \(word)")
        }
    }

    func testTheModelsOwnMarks_AreNotSpeech() {
        let cues = StudioCaptionTranscript.cues(from: [
            StudioTranscriptSegment(start: 0, end: 6.64, text: "<|startoftranscript|><|en|><|transcribe|><|0.00|> I believe that<|7.04|>", words: words("I believe that")),
        ], sourceDuration: 20)

        XCTAssertEqual(cues.map(\.text), ["I believe that"])
        XCTAssertEqual(cues[0].words.map(\.text), ["I", "believe", "that"])
        XCTAssertFalse(cues[0].id.isEmpty)
    }

    func testAStretchWithoutSpeech_IsNoCue() {
        let cues = StudioCaptionTranscript.cues(from: [
            StudioTranscriptSegment(start: 0, end: 1, text: "[BLANK_AUDIO]"),
            StudioTranscriptSegment(start: 1, end: 2, text: "  <|0.00|>  "),
            StudioTranscriptSegment(start: 3, end: 3, text: "No time"),
        ], sourceDuration: 20)

        XCTAssertTrue(cues.isEmpty)
    }

    func testAnEndPastTheRecording_StopsAtItsEnd() {
        let cues = StudioCaptionTranscript.cues(from: [
            StudioTranscriptSegment(start: 19, end: 21.04, text: "the end", words: [
                StudioCaptionWord(start: 19, end: 20, text: " the"),
                StudioCaptionWord(start: 20, end: 21.04, text: " end"),
            ]),
        ], sourceDuration: 20.5)

        XCTAssertEqual(cues[0].end, 20.5)
        XCTAssertEqual(cues[0].words[1].end, 20.5)
    }

    func testWordsThatDoNotSpellOutTheText_AreLeftOut() {
        let cues = StudioCaptionTranscript.cues(from: [
            StudioTranscriptSegment(start: 0, end: 1, text: "Something else", words: words("Not this")),
        ], sourceDuration: 20)

        XCTAssertEqual(cues.map(\.text), ["Something else"])
        XCTAssertTrue(cues[0].words.isEmpty)
    }

    func testASegmentTooLongForACue_IsBrokenBetweenWords_AndEachPartKeepsItsWords() {
        let text = "No single space project in this period will be more impressive to mankind or more important for the long range exploration of space."
        let cues = StudioCaptionTranscript.cues(from: [
            StudioTranscriptSegment(start: 12, end: 26, text: text, words: words(text, startingAt: 12)),
        ], sourceDuration: 30)

        XCTAssertGreaterThan(cues.count, 1)
        XCTAssertEqual(cues.map(\.text).joined(separator: " "), text)
        for cue in cues {
            XCTAssertLessThanOrEqual(cue.text.count, StudioCaptionTranscript.longestCue)
            XCTAssertEqual(cue.words.map(\.text).joined(separator: " "), cue.text)
        }
        XCTAssertEqual(cues[0].start, 12)
        XCTAssertEqual(cues[cues.count - 1].end, 26)
        for (cue, next) in zip(cues, cues.dropFirst()) {
            XCTAssertEqual(cue.end, next.start)
        }
    }

    func testATooLongSegmentWithoutWords_StaysOneCue_ForTheSafeFixesToWrap() {
        let text = String(repeating: "word ", count: 40).trimmingCharacters(in: .whitespaces)
        let cues = StudioCaptionTranscript.cues(from: [StudioTranscriptSegment(start: 0, end: 10, text: text)], sourceDuration: 30)

        XCTAssertEqual(cues.map(\.text), [text])
    }

    func testTheModelIDs_AreTheOnesAgentsAndWindowsUse() {
        XCTAssertEqual(StudioCaptionModels.all.map(\.id), [
            "Tiny", "TinyEn", "Base", "BaseEn", "Small", "SmallEn", "Medium", "MediumEn", "LargeV3Turbo", "LargeV3",
        ])
        XCTAssertEqual(StudioCaptionModels.defaultModel.id, "Base")
        XCTAssertEqual(StudioCaptionModels.find("SmallEn")?.variant, "openai_whisper-small.en")
        XCTAssertNil(StudioCaptionModels.find("NotAModel"))
        XCTAssertNil(StudioCaptionModels.find(nil))
        XCTAssertTrue(StudioCaptionModels.find("BaseEn")!.isEnglishOnly)
        XCTAssertFalse(StudioCaptionModels.find("Base")!.isEnglishOnly)
    }

    func testAModelIsInstalled_OnlyOnceItsDownloadFinished_AndDeletingTakesAllOfIt() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = StudioCaptionModelStore(directory: directory)
        let model = StudioCaptionModels.defaultModel

        XCTAssertFalse(store.isInstalled(model))
        XCTAssertEqual(store.usedBytes(), 0)

        // A download that was stopped: files, and no word that it finished.
        let folder = store.folder(for: model)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data(repeating: 1, count: 1000).write(to: folder.appendingPathComponent("AudioEncoder.bin"))
        XCTAssertFalse(store.isInstalled(model))
        XCTAssertTrue(store.installedModels().isEmpty)
        XCTAssertEqual(store.usedBytes(), 1000)

        try store.markInstalled(model)
        XCTAssertTrue(store.isInstalled(model))
        XCTAssertEqual(store.installedModels(), [model])

        try store.deleteAll()
        XCTAssertFalse(store.isInstalled(model))
        XCTAssertEqual(store.usedBytes(), 0)
        XCTAssertNoThrow(try store.deleteAll())
    }

    func testSpeechIsListenedForInTheMicrophone_WhenThereIsOne() {
        var project = StudioProject(
            id: "3f0013cf-ba10-4453-af91-792b7882dae6",
            sources: StudioSources(screen: StudioScreenSource(width: 1920, height: 1080, frameRate: 30, duration: 10))
        )

        // Nothing known about the tracks: all of them.
        XCTAssertEqual(StudioSound.speechTrackIndexes(project: project, trackCount: 2), [0, 1])

        project.sources.screen.audioTracks = ["system", "microphone"]
        XCTAssertEqual(StudioSound.speechTrackIndexes(project: project, trackCount: 2), [1])

        project.sources.screen.audioTracks = ["system"]
        XCTAssertEqual(StudioSound.speechTrackIndexes(project: project, trackCount: 1), [0])
        XCTAssertEqual(StudioSound.speechTrackIndexes(project: project, trackCount: 0), [])
    }

    func testGeneratedCaptions_GoInAsOneEdit_WithTheirLanguage() {
        var project = StudioProject(
            id: "3f0013cf-ba10-4453-af91-792b7882dae6",
            sources: StudioSources(screen: StudioScreenSource(width: 1920, height: 1080, frameRate: 30, duration: 10))
        )
        project.captions.cues = [StudioCaptionCue(start: 5, end: 6, text: "Imported")]
        var editor = StudioEditorModel(project: project)

        editor.setGeneratedCaptions(language: "en", cues: [
            StudioCaptionCue(start: 3, end: 4, text: "Later"),
            StudioCaptionCue(start: 1, end: 2, text: "Earlier"),
        ])

        XCTAssertEqual(editor.project.captions.language, "en")
        XCTAssertEqual(editor.project.captions.cues.map(\.text), ["Earlier", "Later"])

        editor.undo()
        XCTAssertNil(editor.project.captions.language)
        XCTAssertEqual(editor.project.captions.cues.map(\.text), ["Imported"])
    }

    func testWhatThePanelSays() {
        XCTAssertEqual(StudioCaptionText.generatedMessage(1), "Generated 1 caption.")
        XCTAssertEqual(StudioCaptionText.generatedMessage(12), "Generated 12 captions.")
        XCTAssertEqual(StudioCaptionText.progressText(StudioCaptionProgress(stage: .recognizingSpeech, fraction: 0.4)), "Recognizing speech: 40%")
        XCTAssertEqual(StudioCaptionText.progressText(StudioCaptionProgress(stage: .downloadingModel, fraction: 1.5)), "Downloading the model: 100%")
        XCTAssertEqual(StudioCaptionText.progressText(StudioCaptionProgress(stage: .preparingModel, fraction: 0)), "Getting the model ready\u{2026}")
        let base = StudioCaptionModels.defaultModel
        XCTAssertEqual(StudioCaptionText.generateActionText(base, isInstalled: true), "Generate")
        XCTAssertEqual(StudioCaptionText.generateActionText(base, isInstalled: false), "Download 147 MB and Generate")
        XCTAssertEqual(StudioCaptionText.installedModelsText(count: 0, bytes: 0), "No models downloaded.")
        XCTAssertTrue(StudioCaptionText.installedModelsText(count: 2, bytes: 5_000_000).hasPrefix("2 models downloaded, using "))
    }

    // MARK: - Apple Speech

    func testAppleSpeech_IsOfferedFirst_OnlyWhereItIsAvailable() {
        let all = StudioCaptionModels.all
        XCTAssertEqual(StudioCaptionModels.options(appleSpeechIsAvailable: false, whisper: all), all)
        XCTAssertEqual(StudioCaptionModels.options(appleSpeechIsAvailable: true, whisper: all).first, StudioCaptionModels.appleSpeech)
        XCTAssertEqual(StudioCaptionModels.options(appleSpeechIsAvailable: true, whisper: all).count, all.count + 1)
        XCTAssertEqual(StudioCaptionModels.find("AppleSpeech"), StudioCaptionModels.appleSpeech)
        XCTAssertTrue(StudioCaptionModels.appleSpeech.isAppleSpeech)
        XCTAssertFalse(StudioCaptionModels.defaultModel.isAppleSpeech)
        XCTAssertFalse(StudioCaptionModels.all.contains { $0.isAppleSpeech })
    }

    func testWithoutAChoice_AppleSpeechIsUsedWhereItCanHearTheSystemsLanguage() {
        let apple = StudioCaptionModels.appleSpeech
        let base = StudioCaptionModels.defaultModel
        let all = StudioCaptionModels.all
        XCTAssertEqual(StudioCaptionModels.resolve(id: "", appleSpeechIsAvailable: true, whisper: all), apple)
        XCTAssertEqual(StudioCaptionModels.resolve(id: nil, appleSpeechIsAvailable: true, whisper: all), apple)
        XCTAssertEqual(StudioCaptionModels.resolve(id: "", appleSpeechIsAvailable: false, whisper: all), base)
        XCTAssertEqual(StudioCaptionModels.resolve(id: "", appleSpeechIsAvailable: true, canHearSystemLanguage: false, whisper: all), base)
    }

    func testAChoiceIsKept_UnlessThisMacCannotDoIt() {
        let small = StudioCaptionModels.find("Small")
        let all = StudioCaptionModels.all
        XCTAssertEqual(StudioCaptionModels.resolve(id: "Small", appleSpeechIsAvailable: true, whisper: all), small)
        XCTAssertEqual(StudioCaptionModels.resolve(id: "AppleSpeech", appleSpeechIsAvailable: true, canHearSystemLanguage: false, whisper: all), StudioCaptionModels.appleSpeech)
        XCTAssertEqual(StudioCaptionModels.resolve(id: "AppleSpeech", appleSpeechIsAvailable: false, whisper: all), StudioCaptionModels.defaultModel)
    }

    func testOnMacOS15_TheModelsSeenToGiveNonsenseAreLeftOut() {
        let models = StudioCaptionModels.whisperModels(macOSMajor: 15, isIntel: false)
        XCTAssertEqual(models.map(\.id), ["Tiny", "TinyEn", "SmallEn", "Medium", "MediumEn", "LargeV3Turbo", "LargeV3"])
        XCTAssertEqual(StudioCaptionModels.whisperModels(macOSMajor: 26, isIntel: false), StudioCaptionModels.all)

        // A choice from before, or from a newer macOS, becomes the best one there is.
        XCTAssertEqual(StudioCaptionModels.resolve(id: "", appleSpeechIsAvailable: false, whisper: models).id, "Tiny")
        XCTAssertEqual(StudioCaptionModels.resolve(id: "Base", appleSpeechIsAvailable: false, whisper: models).id, "Tiny")
        XCTAssertEqual(StudioCaptionModels.resolve(id: "SmallEn", appleSpeechIsAvailable: false, whisper: models).id, "SmallEn")
    }

    func testOnIntel_ThereAreNoWhisperModels_ForWhisperCrashesThere() {
        XCTAssertEqual(StudioCaptionModels.whisperModels(macOSMajor: 26, isIntel: true), [])
        XCTAssertEqual(StudioCaptionModels.options(appleSpeechIsAvailable: false, whisper: []), [])
        XCTAssertEqual(StudioCaptionModels.options(appleSpeechIsAvailable: true, whisper: []), [StudioCaptionModels.appleSpeech])
        XCTAssertEqual(StudioCaptionModels.resolve(id: "Base", appleSpeechIsAvailable: true, whisper: []), StudioCaptionModels.appleSpeech)
        XCTAssertEqual(StudioCaptionModels.resolve(id: "", appleSpeechIsAvailable: true, canHearSystemLanguage: false, whisper: []), StudioCaptionModels.appleSpeech)
    }

    func testAskingForAModelThisMacCannotUse_SaysSo_InsteadOfRunningIt() async throws {
        let unusable = StudioCaptionModels.all.first { !StudioCaptionModels.whisperModelsHere.contains($0) }
        guard let unusable else { throw XCTSkip("every model can be used here") }
        let project = StudioProject(
            id: "3f0013cf-ba10-4453-af91-792b7882dae6",
            sources: StudioSources(screen: StudioScreenSource(width: 320, height: 180, frameRate: 15, duration: 1))
        )
        let folder = FileManager.default.temporaryDirectory
        let paths = StudioProjectPaths(
            id: project.id,
            projectDirectory: folder,
            projectJSONURL: folder,
            screenURL: folder.appendingPathComponent("nothing.mp4"),
            cameraURL: nil,
            eventsURL: folder,
            posterURL: folder
        )
        do {
            _ = try await StudioWhisperCaptionGenerator().generate(project: project, paths: paths, model: unusable, language: nil) { _ in }
            XCTFail("it ran")
        } catch let error as StudioCaptionGenerationError {
            XCTAssertNotEqual(error, .missingRecording)
        }
    }

    func testTheLanguage_IsTheStoredOne_ThenTheSystems_ThenTheFirst() {
        let languages = ["de-DE", "en-US", "fr-FR"].map { StudioCaptionLanguage(id: $0, name: $0, isInstalled: false) }
        XCTAssertEqual(StudioCaptionLanguages.choose(stored: "fr-FR", systemLanguage: "en-US", from: languages)?.id, "fr-FR")
        XCTAssertEqual(StudioCaptionLanguages.choose(stored: "", systemLanguage: "en-US", from: languages)?.id, "en-US")
        XCTAssertEqual(StudioCaptionLanguages.choose(stored: "ja-JP", systemLanguage: "en-US", from: languages)?.id, "en-US")
        XCTAssertEqual(StudioCaptionLanguages.choose(stored: nil, systemLanguage: "ja-JP", from: languages)?.id, "de-DE")
        XCTAssertNil(StudioCaptionLanguages.choose(stored: "en-US", systemLanguage: "en-US", from: []))
    }

    func testAReservationIsGivenUp_OnlyWhenThereIsNoRoomForTheLanguage() {
        let reserved = ["en-US", "fr-FR"]
        XCTAssertEqual(StudioCaptionLanguages.reservationToRelease(reserved: reserved, wanted: "de-DE", maximum: 2), "en-US")
        XCTAssertNil(StudioCaptionLanguages.reservationToRelease(reserved: reserved, wanted: "de-DE", maximum: 5))
        XCTAssertNil(StudioCaptionLanguages.reservationToRelease(reserved: reserved, wanted: "fr-FR", maximum: 2))
        XCTAssertNil(StudioCaptionLanguages.reservationToRelease(reserved: [], wanted: "de-DE", maximum: 0))
    }

    func testThePanel_DoesNotPromiseASizeForAppleSpeech() {
        let apple = StudioCaptionModels.appleSpeech
        XCTAssertEqual(StudioCaptionText.generateActionText(apple, isInstalled: true), "Generate")
        XCTAssertEqual(StudioCaptionText.generateActionText(apple, isInstalled: false), "Download and Generate")
        XCTAssertTrue(StudioCaptionText.modelNote(apple, isInstalled: false).contains("macOS downloads this language"))
        XCTAssertTrue(StudioCaptionText.modelNote(apple, isInstalled: true).contains("already on this Mac"))
    }

    /// What a real result from SpeechAnalyzer looks like: runs that end with their space, one with
    /// two words in it, and a single result that spans far more than one cue can hold.
    func testARealResult_IsBrokenIntoCues_ThatKeepTheirWords() throws {
        guard #available(macOS 26.0, *) else { throw XCTSkip("SpeechAnalyzer needs macOS 26") }
        let spoken = "I believe that this nation should commit itself to achieving the goal before this decade is out of landing a man on the moon and returning him safely"
        var pieces = spoken.split(separator: " ").map { String($0) + " " }
        if let at = pieces.firstIndex(of: "out "), pieces[at + 1] == "of " {
            pieces[at] = "out of "
            pieces.remove(at: at + 1)
        }
        let segment = try XCTUnwrap(StudioAppleSpeechResults.segment(
            text: attributed(pieces.enumerated().map { ($1, Double($0) * 0.4) }),
            range: CMTimeRange(start: CMTime(seconds: 0.2, preferredTimescale: 600), end: CMTime(seconds: 11, preferredTimescale: 600))
        ))
        XCTAssertEqual(segment.start, 0.2, accuracy: 0.01)
        XCTAssertEqual(segment.words.count, pieces.count)
        XCTAssertTrue(segment.words.contains { $0.text == "out of " })

        let cues = StudioCaptionTranscript.cues(from: [segment], sourceDuration: 20)
        XCTAssertGreaterThan(cues.count, 1)
        XCTAssertEqual(cues.map(\.text).joined(separator: " "), spoken)
        for cue in cues {
            XCTAssertLessThanOrEqual(cue.text.count, StudioCaptionTranscript.longestCue)
            XCTAssertFalse(cue.words.isEmpty)
            XCTAssertEqual(cue.words.map(\.text).joined(separator: " "), cue.text)
        }
    }

    func testPunctuationInARunOfItsOwn_JoinsItsWord() throws {
        guard #available(macOS 26.0, *) else { throw XCTSkip("SpeechAnalyzer needs macOS 26") }
        let segment = try XCTUnwrap(StudioAppleSpeechResults.segment(
            text: attributed([("Hello", 0), (", ", 0.5), ("world", 1), (".", 1.5)]),
            range: CMTimeRange(start: .zero, end: CMTime(seconds: 2, preferredTimescale: 600))
        ))
        XCTAssertEqual(segment.text, "Hello, world.")
        XCTAssertEqual(segment.words.map(\.text), ["Hello, ", "world."])
    }

    func testALanguageWithoutSpaces_CanStillBeBrokenBetweenItsWords_AndGainsNoSpaces() throws {
        guard #available(macOS 26.0, *) else { throw XCTSkip("SpeechAnalyzer needs macOS 26") }
        let pieces = Array(repeating: "これは字幕です", count: 20)
        let segment = try XCTUnwrap(StudioAppleSpeechResults.segment(
            text: attributed(pieces.enumerated().map { ($1, Double($0)) }),
            range: CMTimeRange(start: .zero, end: CMTime(seconds: 20, preferredTimescale: 600))
        ))
        XCTAssertEqual(segment.words.count, 20)

        let cues = StudioCaptionTranscript.cues(from: [segment], sourceDuration: 30)
        XCTAssertGreaterThan(cues.count, 1)
        XCTAssertEqual(cues.map(\.text).joined(), pieces.joined())
        XCTAssertFalse(cues.contains { $0.text.contains(" ") })
    }

    func testAResultWithoutTimes_HasNoWords_AndKeepsItsText() throws {
        guard #available(macOS 26.0, *) else { throw XCTSkip("SpeechAnalyzer needs macOS 26") }
        let segment = try XCTUnwrap(StudioAppleSpeechResults.segment(
            text: AttributedString("No times here."),
            range: CMTimeRange(start: .zero, end: CMTime(seconds: 2, preferredTimescale: 600))
        ))
        XCTAssertTrue(segment.words.isEmpty)
        XCTAssertEqual(StudioCaptionTranscript.cues(from: [segment], sourceDuration: 5).map(\.text), ["No times here."])
    }

    func testAResultOfNothing_IsNoSegment() throws {
        guard #available(macOS 26.0, *) else { throw XCTSkip("SpeechAnalyzer needs macOS 26") }
        XCTAssertNil(StudioAppleSpeechResults.segment(text: AttributedString("  \n"), range: .zero))
    }

    func testTheSamples_GoToATemporaryFile_ThatReadsBackTheSame() throws {
        let samples = (0..<200_000).map { Float(sin(Double($0) * 0.05)) * 0.5 }
        let url = try StudioCaptionSound.writeTemporaryFile(samples)
        defer { try? FileManager.default.removeItem(at: url) }

        let file = try AVAudioFile(forReading: url)
        XCTAssertEqual(file.length, AVAudioFramePosition(samples.count))
        XCTAssertEqual(file.processingFormat.sampleRate, 16_000)
        XCTAssertEqual(file.processingFormat.channelCount, 1)
        let buffer = try XCTUnwrap(AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(samples.count)))
        try file.read(into: buffer)
        let channel = try XCTUnwrap(buffer.floatChannelData?[0])
        for index in stride(from: 0, to: samples.count, by: 9973) {
            XCTAssertEqual(channel[index], samples[index], accuracy: 0.0001)
        }
    }

    @available(macOS 26.0, *)
    private func attributed(_ runs: [(String, Double)]) -> AttributedString {
        var whole = AttributedString()
        for (text, start) in runs {
            var piece = AttributedString(text)
            piece.audioTimeRange = CMTimeRange(
                start: CMTime(seconds: start, preferredTimescale: 600),
                end: CMTime(seconds: start + 0.3, preferredTimescale: 600)
            )
            whole.append(piece)
        }
        return whole
    }
}
