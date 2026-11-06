import XCTest
@testable import TinyClips

final class StudioCaptionQualityTests: XCTestCase {
    private static let tolerance = 1e-6

    func testChecksAndFixesMatchSharedFixtures() throws {
        let files = try fixtureFiles(in: "captionquality")
        XCTAssertFalse(files.isEmpty, "Expected shared Studio caption quality fixtures")

        for file in files {
            let name = file.lastPathComponent
            let fixture = try StudioJSON.makeDecoder().decode(QualityFixture.self, from: Data(contentsOf: file))

            let findings = StudioCaptionQuality.check(fixture.cues, sourceDuration: fixture.sourceDuration)

            XCTAssertEqual(findings.map(\.cue), fixture.findings.map(\.cue), name)
            XCTAssertEqual(findings.map(\.kind.rawValue), fixture.findings.map(\.kind), name)
            XCTAssertEqual(findings.map(\.canFix), fixture.findings.map(\.canFix), name)
            XCTAssertEqual(StudioCaptionQuality.summary(findings), fixture.summary, name)

            let result = StudioCaptionQuality.fix(fixture.cues, sourceDuration: fixture.sourceDuration)

            XCTAssertEqual(result.applied, fixture.applied, name)
            XCTAssertEqual(result.cues.count, fixture.fixed.count, name)
            for (cue, expected) in zip(result.cues, fixture.fixed) {
                XCTAssertEqual(cue.start, expected.start, accuracy: Self.tolerance, name)
                XCTAssertEqual(cue.end, expected.end, accuracy: Self.tolerance, name)
                XCTAssertEqual(cue.text, expected.text, name)
                XCTAssertEqual(cue.words.count, expected.wordCount, name)
            }
            XCTAssertEqual(
                StudioCaptionQuality.check(result.cues, sourceDuration: fixture.sourceDuration).map(\.kind.rawValue),
                fixture.after,
                name
            )
        }
    }

    func testAFileTimedToTheEditedVideoMatchesSharedFixtures() throws {
        let files = try fixtureFiles(in: "captionimport")
        XCTAssertFalse(files.isEmpty, "Expected shared Studio caption import fixtures")

        for file in files {
            let name = file.lastPathComponent
            let fixture = try StudioJSON.makeDecoder().decode(ImportFixture.self, from: Data(contentsOf: file))
            let map = StudioTimeMap(sourceDuration: fixture.sourceDuration, edits: fixture.edits)
            let text = StudioCaptionFiles.writeSRT(fixture.cues.map {
                StudioResolvedCaption(start: $0.start, end: $0.end, text: $0.text)
            })

            let contents = StudioCaptionFiles.read(text, timing: .editedVideo, timeMap: map)

            XCTAssertEqual(contents.skippedBlocks, fixture.dropped, name)
            XCTAssertEqual(contents.cues.count, fixture.expected.count, name)
            for (cue, expected) in zip(contents.cues, fixture.expected) {
                XCTAssertEqual(cue.start, expected.start, accuracy: Self.tolerance, name)
                XCTAssertEqual(cue.end, expected.end, accuracy: Self.tolerance, name)
                XCTAssertEqual(cue.text, expected.text, name)
            }
        }
    }

    func testAnAgentsImportIsTimedToTheRecording_UnlessItSaysEditedVideo_AndOtherTimingIsRefused() throws {
        XCTAssertEqual(try AutomationCommandRouter.captionTiming([:]), .recording)
        XCTAssertEqual(try AutomationCommandRouter.captionTiming(["timing": "recording"]), .recording)
        XCTAssertEqual(try AutomationCommandRouter.captionTiming(["timing": "editedVideo"]), .editedVideo)
        for timing: Any in ["edited", "", 1] {
            XCTAssertThrowsError(try AutomationCommandRouter.captionTiming(["timing": timing])) { error in
                XCTAssertEqual((error as? AutomationError)?.code, .invalidParams)
            }
        }
    }

    func testWhatIsWrittenReadsBackTimedToTheEditedVideoAsTheSameCaptions() throws {
        for file in try fixtureFiles(in: "captions") {
            let name = file.lastPathComponent
            let fixture = try StudioJSON.makeDecoder().decode(CaptionFixture.self, from: Data(contentsOf: file))
            let map = StudioTimeMap(sourceDuration: fixture.sourceDuration, edits: fixture.edits)

            for text in [fixture.srt, fixture.vtt] {
                let contents = StudioCaptionFiles.read(text, timing: .editedVideo, timeMap: map)
                let again = StudioCaptionResolver.resolve(captions: StudioCaptions(cues: contents.cues), timeMap: map)

                XCTAssertEqual(again.map(\.text), fixture.expected.map(\.text), name)
                for (caption, expected) in zip(again, fixture.expected) {
                    XCTAssertEqual(caption.start, expected.start, accuracy: 0.0011, name)
                    XCTAssertEqual(caption.end, expected.end, accuracy: 0.0011, name)
                }
            }
        }
    }

    func testAFixKeepsTheIdOfTheFirstPartAndGivesTheOthersNewOnes() {
        let text = Array(repeating: "word", count: 40).joined(separator: " ")
        let cue = StudioCaptionCue(id: "first", start: 0, end: 20, text: text)

        let result = StudioCaptionQuality.fix([cue], sourceDuration: 60)

        XCTAssertGreaterThan(result.cues.count, 1)
        XCTAssertEqual(result.cues.first?.id, "first")
        XCTAssertEqual(Set(result.cues.map(\.id)).count, result.cues.count)
    }

    func testACharacterIsACodePointSoAnEmojiCountsOnce() {
        let line = String(repeating: "\u{1F600}", count: 42)

        let findings = StudioCaptionQuality.check([StudioCaptionCue(start: 0, end: 10, text: line)], sourceDuration: 60)

        XCTAssertTrue(findings.isEmpty)
    }

    func testWhatTheEditorSaysAboutCaptions() {
        XCTAssertEqual(StudioCaptionText.countText(0), "No captions yet")
        XCTAssertEqual(StudioCaptionText.countText(1), "1 caption")
        XCTAssertEqual(StudioCaptionText.countText(12), "12 captions")
        XCTAssertEqual(StudioCaptionText.rangeText(StudioCaptionCue(start: 1, end: 2.5)), "1.0 to 2.5 seconds")
        XCTAssertEqual(StudioCaptionText.rowText(StudioCaptionCue(text: " One\ntwo ")), "One two")
        XCTAssertEqual(StudioCaptionText.rowText(StudioCaptionCue(text: " ")), "(Empty)")
        XCTAssertEqual(StudioCaptionText.importedMessage(count: 1, skipped: 0), "Imported 1 caption.")
        XCTAssertEqual(StudioCaptionText.importedMessage(count: 2, skipped: 1), "Imported 2 captions. 1 block could not be read.")
        XCTAssertEqual(StudioCaptionText.importedMessage(count: 2, skipped: 3), "Imported 2 captions. 3 blocks could not be read.")
        XCTAssertEqual(StudioCaptionText.fixedMessage(0), "Nothing to fix.")
        XCTAssertEqual(StudioCaptionText.fixedMessage(1), "Made 1 fix.")
        XCTAssertEqual(StudioCaptionText.fixedMessage(3), "Made 3 fixes.")
        XCTAssertEqual(StudioCaptionText.fixedMessage(2, left: 1), "Made 2 fixes. 1 problem needs a manual edit.")
        XCTAssertEqual(StudioCaptionText.fixedMessage(0, left: 3), "Nothing could be fixed automatically. 3 problems need a manual edit.")
        XCTAssertNil(StudioCaptionText.manualFixNote(fixable: 2, total: 2))
        XCTAssertEqual(StudioCaptionText.manualFixNote(fixable: 0, total: 1), "Auto Fix can't fix this one. Edit the caption's text or timing.")
        XCTAssertEqual(StudioCaptionText.manualFixNote(fixable: 0, total: 2), "Auto Fix can't fix these. Edit the captions' text or timing.")
        XCTAssertEqual(StudioCaptionText.manualFixNote(fixable: 2, total: 3), "Auto Fix can fix 2 of 3. The other needs a manual edit.")
        XCTAssertEqual(StudioCaptionText.manualFixNote(fixable: 1, total: 3), "Auto Fix can fix 1 of 3. The others need a manual edit.")
    }

    func testFormatsComeFromTheExtension() {
        XCTAssertEqual(StudioCaptionFormat(path: "a.srt"), .srt)
        XCTAssertEqual(StudioCaptionFormat(path: "a.SRT"), .srt)
        XCTAssertEqual(StudioCaptionFormat(path: "a.vtt"), .vtt)
        XCTAssertNil(StudioCaptionFormat(path: "a.mp4"))
        XCTAssertNil(StudioCaptionFormat(path: "a"))
    }

    func testReadFileUnderstandsAMark() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".srt")
        defer { try? FileManager.default.removeItem(at: url) }
        let text = "1\n00:00:01,000 --> 00:00:02,000\nCafé\n"
        var data = Data([0xFF, 0xFE])
        data.append(try XCTUnwrap(text.data(using: .utf16LittleEndian)))
        try data.write(to: url)

        XCTAssertEqual(StudioCaptionFiles.read(try StudioCaptionFiles.readFile(url)).cues.first?.text, "Café")
    }

    func testEditingCaptionsInTheModel() throws {
        var model = try makeModel()
        model.replaceCaptions([
            StudioCaptionCue(id: "b", start: 3, end: 4, text: "Second"),
            StudioCaptionCue(id: "a", start: 1, end: 2, text: "First", words: [StudioCaptionWord(start: 1, end: 2, text: "First")]),
        ])
        XCTAssertEqual(model.project.captions.cues.map(\.id), ["a", "b"])
        XCTAssertNil(model.project.captions.language)

        XCTAssertFalse(model.setCaptionText(at: 0, to: "  First \n"))
        XCTAssertEqual(model.project.captions.cues[0].words.count, 1)

        XCTAssertTrue(model.setCaptionText(at: 0, to: "Changed"))
        XCTAssertEqual(model.project.captions.cues[0].text, "Changed")
        XCTAssertTrue(model.project.captions.cues[0].words.isEmpty)

        model.undo()
        XCTAssertEqual(model.project.captions.cues[0].text, "First")
        XCTAssertEqual(model.project.captions.cues[0].words.count, 1)

        XCTAssertTrue(model.removeCaption(at: 0))
        XCTAssertEqual(model.project.captions.cues.map(\.id), ["b"])
        XCTAssertFalse(model.removeCaption(at: 5))
        model.undo()
        XCTAssertEqual(model.project.captions.cues.count, 2)
    }

    func testTheOrderOfAListOfCaptionsIsByTime() throws {
        var model = try makeModel()
        model.applyAutomationProject({
            var project = model.project
            project.captions.cues = [
                StudioCaptionCue(start: 5, end: 6, text: "Later"),
                StudioCaptionCue(start: 1, end: 2, text: "Earlier"),
            ]
            return project
        }())

        XCTAssertEqual(model.project.captions.cues.indexesInTimeOrder(), [1, 0])
    }

    func testFixingCaptionsIsOneUndoStep() throws {
        var model = try makeModel()
        model.replaceCaptions([StudioCaptionCue(start: 1, end: 1.2, text: "Hi")])

        XCTAssertEqual(model.fixCaptions(), 1)
        XCTAssertEqual(model.project.captions.cues[0].end, 2, accuracy: Self.tolerance)
        XCTAssertEqual(model.fixCaptions(), 0)

        model.undo()
        XCTAssertEqual(model.project.captions.cues[0].end, 1.2, accuracy: Self.tolerance)
    }

    // MARK: - Editing

    func testMovingAnEndKeepsACaptionAShortestPartLongAndInTheRecording() throws {
        let words = [StudioCaptionWord(start: 1, end: 1.4, text: "Hi"), StudioCaptionWord(start: 1.5, end: 2, text: "there")]
        let cues = [
            StudioCaptionCue(id: "a", start: 1, end: 2, text: "Hi there", words: words),
            StudioCaptionCue(id: "b", start: 3, end: 4, text: "Next"),
        ]

        let earlier = try XCTUnwrap(StudioCaptionEditing.retime(cues, at: 0, edge: .start, to: -5, sourceDuration: 60))
        XCTAssertEqual(earlier.cues[0].start, 0)
        XCTAssertEqual(earlier.cues[0].words, words)

        let late = try XCTUnwrap(StudioCaptionEditing.retime(cues, at: 0, edge: .start, to: 10, sourceDuration: 60))
        XCTAssertEqual(late.cues[0].start, 1.9, accuracy: Self.tolerance)
        XCTAssertTrue(late.cues[0].words.isEmpty, "A word whose middle is outside the caption is no longer its word")

        let long = try XCTUnwrap(StudioCaptionEditing.retime(cues, at: 1, edge: .end, to: 100, sourceDuration: 60))
        XCTAssertEqual(long.cues[1].end, 60)
        let short = try XCTUnwrap(StudioCaptionEditing.retime(cues, at: 1, edge: .end, to: 0, sourceDuration: 60))
        XCTAssertEqual(short.cues[1].end, 3.1, accuracy: Self.tolerance)

        let moved = try XCTUnwrap(StudioCaptionEditing.retime(cues, at: 1, edge: .start, to: 0.5, sourceDuration: 60))
        XCTAssertEqual(moved.cues.map(\.id), ["b", "a"])
        XCTAssertEqual(moved.index, 0)

        XCTAssertNil(StudioCaptionEditing.retime(cues, at: 0, edge: .start, to: 1, sourceDuration: 60))
        XCTAssertNil(StudioCaptionEditing.retime(cues, at: 0, edge: .end, to: .nan, sourceDuration: 60))
        XCTAssertNil(StudioCaptionEditing.retime(cues, at: 5, edge: .end, to: 3, sourceDuration: 60))
    }

    func testSplittingACaptionGoesByItsWordsAndKeepsItsText() throws {
        let cue = StudioCaptionCue(id: "a", start: 1, end: 3, text: "Hello there,\nworld", words: [
            StudioCaptionWord(start: 1, end: 1.4, text: "Hello"),
            StudioCaptionWord(start: 1.5, end: 1.9, text: "there,"),
            StudioCaptionWord(start: 2.5, end: 3, text: "world"),
        ])

        let result = try XCTUnwrap(StudioCaptionEditing.split([cue], at: 0, time: 2.2))
        XCTAssertEqual(result.cues.map(\.text), ["Hello there,", "world"])
        XCTAssertEqual(result.cues.map(\.start), [1, 2.2])
        XCTAssertEqual(result.cues.map(\.end), [2.2, 3])
        XCTAssertEqual(result.cues.map(\.words.count), [2, 1])
        XCTAssertEqual(result.cues[0].id, "a")
        XCTAssertNotEqual(result.cues[1].id, "a")
        XCTAssertEqual(result.cues[1].id, result.cues[1].id.lowercased())
        XCTAssertEqual(result.index, 1, "The second part starts at the split, where the playhead is")

        let early = try XCTUnwrap(StudioCaptionEditing.split([cue], at: 0, time: 1.1))
        XCTAssertEqual(early.cues.map(\.text), ["Hello", "there,\nworld"])
        XCTAssertTrue(early.cues[0].words.isEmpty, "A word said after the split is not the first part's")

        let unspaced = StudioCaptionCue(start: 0, end: 2, text: "こんにちは世界", words: [
            StudioCaptionWord(start: 0, end: 1, text: "こんにちは"),
            StudioCaptionWord(start: 1, end: 2, text: "世界"),
        ])
        XCTAssertEqual(StudioCaptionEditing.split([unspaced], at: 0, time: 1)?.cues.map(\.text), ["こんにちは", "世界"])
    }

    func testTheSplitTimeIsThePlayheadInsideTheCaptionOrElseTheWordGapNearestItsMiddle() throws {
        let cue = StudioCaptionCue(id: "a", start: 1, end: 3, text: "Hello there,\nworld", words: [
            StudioCaptionWord(start: 1, end: 1.4, text: "Hello"),
            StudioCaptionWord(start: 1.5, end: 1.9, text: "there,"),
            StudioCaptionWord(start: 2.5, end: 3, text: "world"),
        ])

        XCTAssertEqual(StudioCaptionEditing.splitTime(of: cue, playhead: 1.7), 1.7)
        XCTAssertEqual(try XCTUnwrap(StudioCaptionEditing.splitTime(of: cue, playhead: 1)), 2.2, accuracy: 1e-6)
        XCTAssertEqual(try XCTUnwrap(StudioCaptionEditing.splitTime(of: cue, playhead: 10)), 2.2, accuracy: 1e-6)
        XCTAssertEqual(StudioCaptionEditing.splitTime(of: StudioCaptionCue(start: 0, end: 4, text: "aaaa bbbb"), playhead: 0), 2)
        XCTAssertNil(StudioCaptionEditing.splitTime(of: StudioCaptionCue(start: 0, end: 4, text: "Hello"), playhead: 0))
    }

    func testSplittingACaptionWithoutWordsGoesByHowFarThroughItIs() throws {
        let cue = StudioCaptionCue(id: "a", start: 0, end: 4, text: "aaaa bbbb\ncccc  dddd")

        let result = try XCTUnwrap(StudioCaptionEditing.split([cue], at: 0, time: 1))
        XCTAssertEqual(result.cues.map(\.text), ["aaaa", "bbbb cccc dddd"])
        XCTAssertTrue(result.cues.allSatisfy(\.words.isEmpty))

        XCTAssertTrue(StudioCaptionEditing.canSplit(cue, at: 2))
        XCTAssertFalse(StudioCaptionEditing.canSplit(cue, at: 0.05), "Each part is a shortest part long")
        XCTAssertFalse(StudioCaptionEditing.canSplit(cue, at: 3.95))
        XCTAssertFalse(StudioCaptionEditing.canSplit(StudioCaptionCue(start: 0, end: 4, text: "Hello"), at: 2))
        XCTAssertNil(StudioCaptionEditing.split([cue], at: 0, time: 5))
    }

    func testMergingACaptionWithTheNextOneInTime() throws {
        let first = StudioCaptionCue(id: "a", start: 1, end: 2, text: "Hello\nthere", words: [
            StudioCaptionWord(start: 1, end: 1.4, text: "Hello"),
            StudioCaptionWord(start: 1.5, end: 2, text: "there"),
        ])
        let next = StudioCaptionCue(id: "b", start: 3, end: 4, text: " world ", words: [StudioCaptionWord(start: 3, end: 4, text: "world")])

        let result = try XCTUnwrap(StudioCaptionEditing.mergeWithNext([next, first], at: 1))
        XCTAssertEqual(result.cues.count, 1)
        XCTAssertEqual(result.index, 0)
        XCTAssertEqual(result.cues[0].id, "a")
        XCTAssertEqual(result.cues[0].text, "Hello there world")
        XCTAssertEqual(result.cues[0].start, 1)
        XCTAssertEqual(result.cues[0].end, 4)
        XCTAssertEqual(result.cues[0].words.map(\.text), ["Hello", "there", "world"])

        var unspelled = next
        unspelled.words = []
        XCTAssertEqual(StudioCaptionEditing.mergeWithNext([first, unspelled], at: 0)?.cues[0].words, [])

        XCTAssertFalse(StudioCaptionEditing.canMergeWithNext([first, next], at: 1))
        XCTAssertNil(StudioCaptionEditing.mergeWithNext([first, next], at: 1))
        XCTAssertNil(StudioCaptionEditing.mergeWithNext([first], at: 0))
    }

    func testTheCaptionAtATimeEndsWhereTheNextStarts() {
        let cues = [
            StudioCaptionCue(start: 0, end: 1),
            StudioCaptionCue(start: 1, end: 2),
            StudioCaptionCue(start: 3, end: 4),
        ]
        XCTAssertEqual(StudioCaptionEditing.position(at: 0.5, in: cues), 0)
        XCTAssertEqual(StudioCaptionEditing.position(at: 1, in: cues), 1)
        XCTAssertNil(StudioCaptionEditing.position(at: 2.5, in: cues))
        XCTAssertEqual(StudioCaptionEditing.position(at: 3.99, in: cues), 2)
        XCTAssertNil(StudioCaptionEditing.position(at: 4, in: cues))
        XCTAssertNil(StudioCaptionEditing.position(at: -1, in: cues))
        XCTAssertNil(StudioCaptionEditing.position(at: .nan, in: cues))

        let covered = [StudioCaptionCue(start: 0, end: 10), StudioCaptionCue(start: 2, end: 3)]
        XCTAssertNil(StudioCaptionEditing.position(at: 5, in: covered), "The first was cut short where the second starts")
    }

    func testNextProblemGoesRoundAfterTheLast() {
        let problems: Set<Int> = [1, 3]
        let next = { StudioCaptionEditing.nextProblem(count: 5, after: $0, hasProblem: problems.contains) }
        XCTAssertEqual(next(nil), 1, "With nothing open, the first")
        XCTAssertEqual(next(1), 3)
        XCTAssertEqual(next(2), 3)
        XCTAssertEqual(next(3), 1, "Round to the first after the last")
        XCTAssertEqual(next(4), 1)
        XCTAssertEqual(StudioCaptionEditing.nextProblem(count: 5, after: 3) { $0 == 3 }, 3, "The only one stays open")
        XCTAssertNil(StudioCaptionEditing.nextProblem(count: 5, after: nil) { _ in false })
        XCTAssertNil(StudioCaptionEditing.nextProblem(count: 0, after: nil) { _ in true })
    }

    func testSplittingMergingAndMovingCaptionsAreEachOneUndoStep() throws {
        var model = try makeModel()
        model.replaceCaptions([
            StudioCaptionCue(id: "a", start: 1, end: 3, text: "One two"),
            StudioCaptionCue(id: "b", start: 4, end: 5, text: "Three"),
        ])
        let original = model.project.captions.cues

        XCTAssertEqual(model.splitCaption(at: 0, sourceTime: 2), 1)
        XCTAssertEqual(model.project.captions.cues.map(\.text), ["One", "two", "Three"])
        model.undo()
        XCTAssertEqual(model.project.captions.cues, original)

        XCTAssertEqual(model.mergeCaptionWithNext(at: 0), 0)
        XCTAssertEqual(model.project.captions.cues.map(\.text), ["One two Three"])
        model.undo()
        XCTAssertEqual(model.project.captions.cues, original)

        XCTAssertEqual(model.retimeCaption(at: 1, edge: .start, to: 0), 0)
        XCTAssertEqual(model.project.captions.cues.map(\.id), ["b", "a"])
        XCTAssertNil(model.retimeCaption(at: 0, edge: .start, to: 0))
        model.undo()
        XCTAssertEqual(model.project.captions.cues, original)

        XCTAssertNil(model.splitCaption(at: 1, sourceTime: 4.5))
        XCTAssertNil(model.mergeCaptionWithNext(at: 1))
    }

    // MARK: - Panel

    func testSearchingCaptionsIgnoresCaseAccentsAndWidth() {
        XCTAssertTrue(StudioCaptionSearch.matches("Le café est ouvert", query: "CAFE"))
        XCTAssertTrue(StudioCaptionSearch.matches("ＴｉｎｙＣｌｉｐｓ rocks", query: "tinyclips"))
        XCTAssertFalse(StudioCaptionSearch.matches("Hello", query: ""))
        XCTAssertEqual(StudioCaptionSearch.normalized("  hi \n"), "hi")

        let text = "Go, go, GO!"
        XCTAssertEqual(StudioCaptionSearch.ranges(of: "go", in: text).map { String(text[$0]) }, ["Go", "go", "GO"])
        XCTAssertEqual(StudioCaptionSearch.ranges(of: "", in: text), [])
    }

    func testTheNoSpeechNoticeNamesTheLanguageAndSaysTheCaptionsAreKept() {
        XCTAssertEqual(
            StudioCaptionPanelText.noSpeechMessage(languageName: "French", hasCaptions: false),
            "No speech was found in this recording when listening for French. If people speak another language in it, try that one."
        )
        XCTAssertEqual(
            StudioCaptionPanelText.noSpeechMessage(languageName: nil, hasCaptions: true),
            "No speech was found in this recording. If people speak another language in it, try that one. The captions are as they were."
        )
    }

    func testCaptionTimesReadAsAClockToATenth() {
        XCTAssertEqual(StudioCaptionPanelText.clockText(5), "0:05.0")
        XCTAssertEqual(StudioCaptionPanelText.clockText(65.34), "1:05.3")
        XCTAssertEqual(StudioCaptionPanelText.clockText(59.96), "1:00.0")
        XCTAssertEqual(StudioCaptionPanelText.clockText(-1), "0:00.0")
        XCTAssertEqual(StudioCaptionPanelText.clockText(.nan), "0:00.0")
        XCTAssertEqual(StudioCaptionPanelText.clockText(1e19), "5999:59.0")
    }

    // MARK: - Fixtures

    private func makeModel() throws -> StudioEditorModel {
        let json = """
            {
              "schemaVersion": 1,
              "id": "3f0013cf-ba10-4453-af91-792b7882dae6",
              "sources": { "screen": { "width": 1920, "height": 1080, "duration": 60 } }
            }
            """
        return StudioEditorModel(project: try StudioJSON.makeDecoder().decode(StudioProject.self, from: Data(json.utf8)))
    }

    private func fixtureFiles(in folder: String) throws -> [URL] {
        // This file lives at <repo>/mac/TinyClipsTests/, so the repository root is three levels up.
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("shared/studio/fixtures", isDirectory: true)
            .appendingPathComponent(folder, isDirectory: true)
        return try FileManager.default.contentsOfDirectory(at: url, includingPropertiesForKeys: nil)
            .filter { $0.pathExtension.lowercased() == "json" }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
    }

    private struct ExpectedFinding: Decodable {
        var cue: Int
        var kind: String
        var canFix: Bool
    }

    private struct FixedCue: Decodable {
        var start: Double
        var end: Double
        var text: String
        var wordCount: Int
    }

    private struct QualityFixture: Decodable {
        var sourceDuration: Double
        var cues: [StudioCaptionCue]
        var findings: [ExpectedFinding]
        var summary: String
        var fixed: [FixedCue]
        var applied: Int
        var after: [String]
    }

    private struct ExpectedCue: Decodable {
        var start: Double
        var end: Double
        var text: String
    }

    private struct ImportFixture: Decodable {
        var sourceDuration: Double
        var edits: StudioEdits
        var cues: [ExpectedCue]
        var expected: [ExpectedCue]
        var dropped: Int
    }

    private struct CaptionFixture: Decodable {
        var sourceDuration: Double
        var edits: StudioEdits
        var expected: [ExpectedCue]
        var srt: String
        var vtt: String
    }
}
