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

        XCTAssertEqual(model.captionIndexesInOrder(), [1, 0])
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
