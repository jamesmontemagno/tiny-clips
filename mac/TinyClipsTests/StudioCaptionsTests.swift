import XCTest
@testable import TinyClips

final class StudioCaptionsTests: XCTestCase {
    private static let tolerance = 1e-6

    func testSharedCaptionFixtures() throws {
        let files = try fixtureFiles(in: "captions")
        XCTAssertFalse(files.isEmpty, "Expected shared Studio caption fixtures")

        for file in files {
            let fixture = try StudioJSON.makeDecoder().decode(CaptionFixture.self, from: Data(contentsOf: file))
            let name = file.lastPathComponent
            let map = StudioTimeMap(sourceDuration: fixture.sourceDuration, edits: fixture.edits)

            let actual = StudioCaptionResolver.resolve(captions: fixture.captions, timeMap: map)

            XCTAssertEqual(actual.count, fixture.expected.count, name)
            for (caption, expected) in zip(actual, fixture.expected) {
                XCTAssertEqual(caption.start, expected.start, accuracy: Self.tolerance, name)
                XCTAssertEqual(caption.end, expected.end, accuracy: Self.tolerance, name)
                XCTAssertEqual(caption.text, expected.text, name)
            }
            for query in fixture.at {
                XCTAssertEqual(StudioCaptionResolver.index(in: actual, at: query.output), query.index, "\(name) at \(query.output)")
            }
            XCTAssertEqual(StudioCaptionFiles.writeSRT(actual), fixture.srt, name)
            XCTAssertEqual(StudioCaptionFiles.writeVTT(actual), fixture.vtt, name)
        }
    }

    func testSharedCaptionFileFixtures() throws {
        let files = try fixtureFiles(in: "captionfiles")
        XCTAssertFalse(files.isEmpty, "Expected shared Studio caption file fixtures")

        for file in files {
            let fixture = try StudioJSON.makeDecoder().decode(CaptionFileFixture.self, from: Data(contentsOf: file))
            let name = file.lastPathComponent

            let actual = StudioCaptionFiles.read(fixture.text)

            XCTAssertEqual(actual.skippedBlocks, fixture.skipped, name)
            XCTAssertEqual(actual.cues.count, fixture.expected.count, name)
            for (cue, expected) in zip(actual.cues, fixture.expected) {
                XCTAssertEqual(cue.start, expected.start, accuracy: Self.tolerance, name)
                XCTAssertEqual(cue.end, expected.end, accuracy: Self.tolerance, name)
                XCTAssertEqual(cue.text, expected.text, name)
                XCTAssertNotNil(UUID(uuidString: cue.id), name)
                XCTAssertTrue(cue.words.isEmpty, name)
            }
        }
    }

    func testWhatIsWrittenReadsBackTheSame() throws {
        for file in try fixtureFiles(in: "captions") {
            let fixture = try StudioJSON.makeDecoder().decode(CaptionFixture.self, from: Data(contentsOf: file))
            for text in [fixture.srt, fixture.vtt] {
                let read = StudioCaptionFiles.read(text)
                XCTAssertEqual(read.skippedBlocks, 0, file.lastPathComponent)
                XCTAssertEqual(read.cues.map(\.text), fixture.expected.map(\.text), file.lastPathComponent)
                for (cue, expected) in zip(read.cues, fixture.expected) {
                    XCTAssertEqual(cue.start, expected.start, accuracy: 0.0006, file.lastPathComponent)
                    XCTAssertEqual(cue.end, expected.end, accuracy: 0.0006, file.lastPathComponent)
                }
            }
        }
    }

    func testAProjectWithoutCaptionsReadsTheDefaults() throws {
        let project = try readProject("")

        XCTAssertNil(project.captions.language)
        XCTAssertTrue(project.captions.cues.isEmpty)
        XCTAssertEqual(project.overlays.captions, StudioCaptionOverlay())
        XCTAssertFalse(project.overlays.captions.enabled)
        XCTAssertEqual(project.overlays.captions.size, .medium)
        XCTAssertEqual(project.overlays.captions.position, .bottom)
        XCTAssertEqual(project.overlays.captions.appearance, .classicOutline)
    }

    func testNullCaptionsAndNullStyleReadTheDefaults() throws {
        var project = try readProject(#", "captions": null, "overlays": { "captions": null }"#)
        XCTAssertTrue(project.captions.cues.isEmpty)
        XCTAssertEqual(project.overlays.captions, StudioCaptionOverlay())

        project = try readProject(#", "overlays": { "captions": { "enabled": null, "size": null, "position": "side", "appearance": "neon" } }"#)
        XCTAssertEqual(project.overlays.captions, StudioCaptionOverlay())
    }

    func testAStyleThatNamesOnlySomeOfItsPropertiesReadsTheDefaultsForTheRest() throws {
        let project = try readProject(#", "overlays": { "captions": { "enabled": true } }"#)

        XCTAssertEqual(project.overlays.captions, StudioCaptionOverlay(enabled: true))
    }

    func testCaptionsAndUnknownPropertiesSurviveASave() throws {
        let project = try readProject(#"""
            , "overlays": { "captions": { "enabled": true, "size": "large", "position": "top", "appearance": "boldImpact", "font": "x" } },
            "captions": {
              "language": "en-US",
              "future": 1,
              "cues": [ { "id": "a", "start": 1, "end": 2, "text": "Hi", "speaker": "Sam",
                "words": [ { "start": 1, "end": 2, "text": "Hi", "confidence": 0.9 } ] } ]
            }
            """#)

        let data = try StudioJSON.makeEncoder().encode(project)
        let root = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])

        let style = try XCTUnwrap((root["overlays"] as? [String: Any])?["captions"] as? [String: Any])
        XCTAssertEqual(style["enabled"] as? Bool, true)
        XCTAssertEqual(style["size"] as? String, "large")
        XCTAssertEqual(style["position"] as? String, "top")
        XCTAssertEqual(style["appearance"] as? String, "boldImpact")
        XCTAssertEqual(style["font"] as? String, "x")

        let captions = try XCTUnwrap(root["captions"] as? [String: Any])
        XCTAssertEqual(captions["language"] as? String, "en-US")
        XCTAssertEqual(captions["future"] as? Int, 1)
        let cue = try XCTUnwrap((captions["cues"] as? [[String: Any]])?.first)
        XCTAssertEqual(cue["id"] as? String, "a")
        XCTAssertEqual(cue["speaker"] as? String, "Sam")
        let word = try XCTUnwrap((cue["words"] as? [[String: Any]])?.first)
        XCTAssertEqual(word["confidence"] as? Double, 0.9)
    }

    func testALanguageThatIsNotKnownIsLeftOut() throws {
        let data = try StudioJSON.makeEncoder().encode(try readProject(""))
        let root = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        let captions = try XCTUnwrap(root["captions"] as? [String: Any])
        XCTAssertNil(captions["language"])
    }

    func testAChangeToTheCaptionsIsAChangeToTheEditableState() throws {
        let project = try readProject("")
        var changed = project
        changed.captions.cues = [StudioCaptionCue(start: 1, end: 2, text: "Hi")]

        XCTAssertNotEqual(StudioEditableState(project: project), StudioEditableState(project: changed))
        XCTAssertEqual(StudioEditableState(project: changed).applied(to: project).captions.cues.first?.text, "Hi")
    }

    func testIndexFindsTheCaptionShowingInAListOfAnyLength() {
        let cues = (0..<200).reversed().map {
            StudioCaptionCue(start: Double($0) * 0.5, end: Double($0) * 0.5 + 0.25, text: "Cue \($0)")
        }

        let resolved = StudioCaptionResolver.resolve(
            captions: StudioCaptions(cues: cues),
            timeMap: StudioTimeMap(sourceDuration: 100, edits: StudioEdits())
        )

        XCTAssertEqual(resolved.count, 200)
        XCTAssertEqual(resolved.first?.text, "Cue 0")
        XCTAssertEqual(StudioCaptionResolver.index(in: resolved, at: 60.1), 120)
        XCTAssertNil(StudioCaptionResolver.index(in: resolved, at: 60.3))
        XCTAssertNil(StudioCaptionResolver.index(in: [], at: 1))
    }

    // MARK: - Fixtures

    private func readProject(_ members: String) throws -> StudioProject {
        let json = """
            {
              "schemaVersion": 1,
              "id": "3f0013cf-ba10-4453-af91-792b7882dae6",
              "sources": { "screen": { "width": 1920, "height": 1080, "duration": 60 } }
              \(members)
            }
            """
        return try StudioJSON.makeDecoder().decode(StudioProject.self, from: Data(json.utf8))
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

    private struct ExpectedCaption: Decodable {
        var start: Double
        var end: Double
        var text: String
    }

    private struct CaptionQuery: Decodable {
        var output: Double
        var index: Int?
    }

    private struct CaptionFixture: Decodable {
        var sourceDuration: Double
        var edits: StudioEdits
        var captions: StudioCaptions
        var expected: [ExpectedCaption]
        var at: [CaptionQuery]
        var srt: String
        var vtt: String
    }

    private struct CaptionFileFixture: Decodable {
        var text: String
        var expected: [ExpectedCaption]
        var skipped: Int
    }
}
