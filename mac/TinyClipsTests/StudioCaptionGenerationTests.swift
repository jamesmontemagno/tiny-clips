import AVFoundation
import Speech
import XCTest
@testable import TinyClips

/// Generating captions, up to the speech engine: what the text of a real run turns into, and the
/// model catalog and folder. A real model is too large to download on every build.
final class StudioCaptionGenerationTests: XCTestCase {
    private func words(_ text: String, startingAt start: Double = 0, each: Double = 0.5) -> [StudioCaptionWord] {
        text.split(separator: " ").enumerated().map { index, word in
            // SpeechAnalyzer's words end with their space, as they did in a real run.
            StudioCaptionWord(start: start + Double(index) * each, end: start + Double(index + 1) * each, text: "\(word) ")
        }
    }

    func testACueKeepsItsWords_WithoutTheirSpaces() {
        let cues = StudioCaptionTranscript.cues(from: [
            StudioTranscriptSegment(start: 0, end: 6.64, text: "I believe that", words: words("I believe that")),
        ], sourceDuration: 20)

        XCTAssertEqual(cues.map(\.text), ["I believe that"])
        XCTAssertEqual(cues[0].words.map(\.text), ["I", "believe", "that"])
        // Section 15 of the project format: a lowercase UUID.
        XCTAssertNotNil(UUID(uuidString: cues[0].id))
        XCTAssertEqual(cues[0].id, cues[0].id.lowercased())
    }

    func testAStretchWithoutSpeech_IsNoCue() {
        let cues = StudioCaptionTranscript.cues(from: [
            StudioTranscriptSegment(start: 1, end: 2, text: "   "),
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
            XCTAssertLessThanOrEqual(cue.text.unicodeScalars.count, StudioCaptionTranscript.longestCue)
            XCTAssertEqual(cue.words.map(\.text).joined(separator: " "), cue.text)
        }
        XCTAssertEqual(cues[0].start, 12)
        XCTAssertEqual(cues[cues.count - 1].end, 26)
        for (cue, next) in zip(cues, cues.dropFirst()) {
            XCTAssertEqual(cue.end, next.start)
        }
    }

    /// Letters with their accents as marks of their own are one letter to a reader and two code
    /// points to the checks. A part is as long as the checks count it, so Auto Fix only wraps it
    /// and its words stay.
    func testALongSegment_IsBrokenAsTheChecksCount_SoTheSafeFixesKeepItsWords() {
        let text = Array(repeating: "e\u{301}te\u{301}", count: 16).joined(separator: " ")
        let cues = StudioCaptionTranscript.cues(from: [
            StudioTranscriptSegment(start: 0, end: 16, text: text, words: words(text)),
        ], sourceDuration: 20)

        XCTAssertGreaterThan(cues.count, 1)
        for cue in cues {
            XCTAssertLessThanOrEqual(cue.text.unicodeScalars.count, StudioCaptionTranscript.longestCue)
        }
        let fixed = StudioCaptionQuality.fix(cues, sourceDuration: 20).cues
        XCTAssertEqual(fixed.count, cues.count)
        XCTAssertFalse(fixed.contains { $0.words.isEmpty })
    }

    func testATooLongSegmentWithoutWords_StaysOneCue_ForTheSafeFixesToWrap() {
        let text = String(repeating: "word ", count: 40).trimmingCharacters(in: .whitespaces)
        let cues = StudioCaptionTranscript.cues(from: [StudioTranscriptSegment(start: 0, end: 10, text: text)], sourceDuration: 30)

        XCTAssertEqual(cues.map(\.text), [text])
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

    // MARK: - Apple Speech

    func testWhatThePanelSays() {
        XCTAssertEqual(StudioCaptionText.generatedMessage(1), "Generated 1 caption.")
        XCTAssertEqual(StudioCaptionText.generatedMessage(12), "Generated 12 captions.")
        XCTAssertEqual(StudioCaptionText.progressText(StudioCaptionProgress(stage: .recognizingSpeech, fraction: 0.4)), "Recognizing speech: 40%")
        XCTAssertEqual(StudioCaptionText.progressText(StudioCaptionProgress(stage: .downloadingLanguage, fraction: 1.5)), "Downloading the language: 100%")
        XCTAssertEqual(StudioCaptionText.generateActionText(isInstalled: true), "Generate")
        XCTAssertEqual(StudioCaptionText.generateActionText(isInstalled: false), "Download and Generate")
        XCTAssertTrue(StudioCaptionText.languageNote(isInstalled: false).contains("macOS downloads this language"))
        XCTAssertTrue(StudioCaptionText.languageNote(isInstalled: true).contains("already on this Mac"))
    }

    @MainActor
    func testAMacWithoutAppleSpeech_SaysSo_AndAgentsAreNotOfferedIt() async throws {
        guard !StudioAppleSpeech.isAvailable else { throw XCTSkip("this Mac has Apple Speech") }
        let languages = await StudioAppleSpeech.languages()
        let systemLanguage = await StudioAppleSpeech.language(equivalentTo: nil)
        XCTAssertTrue(languages.isEmpty)
        XCTAssertNil(systemLanguage)
        XCTAssertFalse(AutomationCommandRouter.supportedCommands.contains("studio.captions.models"))
        XCTAssertFalse(AutomationCommandRouter.supportedCommands.contains("studio.captions.generate"))

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
            _ = try await StudioAppleSpeechCaptionGenerator().generate(project: project, paths: paths, language: nil) { _ in }
            XCTFail("it ran")
        } catch let error as StudioCaptionGenerationError {
            XCTAssertEqual(error.message, StudioCaptionText.needsNewerMac)
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
            XCTAssertLessThanOrEqual(cue.text.unicodeScalars.count, StudioCaptionTranscript.longestCue)
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

    func testTheSound_GoesToATemporaryFile_AsSpeechRecognitionWantsIt() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }

        // Two seconds of a tone at 48 kHz, as a recording's sound might be.
        let recording = folder.appendingPathComponent("recording.wav")
        let recordingFormat = try XCTUnwrap(AVAudioFormat(standardFormatWithSampleRate: 48_000, channels: 1))
        let frames = AVAudioFrameCount(96_000)
        let tone = try XCTUnwrap(AVAudioPCMBuffer(pcmFormat: recordingFormat, frameCapacity: frames))
        tone.frameLength = frames
        let toneSamples = try XCTUnwrap(tone.floatChannelData?[0])
        for frame in 0..<Int(frames) {
            toneSamples[frame] = Float(sin(Double(frame) * 2 * .pi * 440 / 48_000)) * 0.5
        }
        let recordingFile = try AVAudioFile(forWriting: recording, settings: recordingFormat.settings)
        try recordingFile.write(from: tone)
        recordingFile.close()

        let project = StudioProject(
            id: "3f0013cf-ba10-4453-af91-792b7882dae6",
            sources: StudioSources(screen: StudioScreenSource(width: 320, height: 180, frameRate: 30, duration: 2))
        )
        let url = try await StudioCaptionSound.writeTemporaryFile(from: recording, project: project) { _ in }
        defer { try? FileManager.default.removeItem(at: url) }

        let file = try AVAudioFile(forReading: url)
        XCTAssertEqual(file.processingFormat.sampleRate, 16_000)
        XCTAssertEqual(file.processingFormat.channelCount, 1)
        XCTAssertEqual(Double(file.length), 32_000, accuracy: 320)
        let buffer = try XCTUnwrap(AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)))
        try file.read(into: buffer)
        let channel = try XCTUnwrap(buffer.floatChannelData?[0])
        let middle = Int(buffer.frameLength) / 4..<Int(buffer.frameLength) * 3 / 4
        let loudness = (middle.reduce(0.0) { $0 + Double(channel[$1] * channel[$1]) } / Double(middle.count)).squareRoot()
        XCTAssertEqual(loudness, 0.5 / 2.0.squareRoot(), accuracy: 0.05)
    }

    func testAMissingRecording_LeavesNoFileBehind() async throws {
        let project = StudioProject(
            id: "3f0013cf-ba10-4453-af91-792b7882dae6",
            sources: StudioSources(screen: StudioScreenSource(width: 320, height: 180, frameRate: 30, duration: 2))
        )
        do {
            _ = try await StudioCaptionSound.writeTemporaryFile(
                from: FileManager.default.temporaryDirectory.appendingPathComponent("\(UUID().uuidString).mp4"),
                project: project
            ) { _ in }
            XCTFail("it wrote a file")
        } catch let error as StudioCaptionGenerationError {
            XCTAssertEqual(error, .missingRecording)
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
