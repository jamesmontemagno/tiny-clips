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
}
