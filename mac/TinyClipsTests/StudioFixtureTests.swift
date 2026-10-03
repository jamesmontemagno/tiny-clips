import XCTest
@testable import TinyClips

final class StudioFixtureTests: XCTestCase {
    func testSharedCanvasFixtures() throws {
        let files = try fixtureFiles(in: "canvas")
        XCTAssertFalse(files.isEmpty, "Expected shared Studio canvas fixtures")

        for file in files {
            let fixture = try StudioJSON.makeDecoder().decode(CanvasFixture.self, from: Data(contentsOf: file))
            for index in fixture.cases.indices {
                let testCase = fixture.cases[index]
                let actual = StudioCanvasMath.exportSize(
                    naturalWidth: testCase.natural.width,
                    naturalHeight: testCase.natural.height,
                    longSideLimit: testCase.limit
                )
                assertRect(actual, testCase.expected, file: file, caseIndex: index, prefix: "expected")
            }
        }
    }

    func testSharedLayoutFixtures() throws {
        let files = try fixtureFiles(in: "layout")
        XCTAssertFalse(files.isEmpty, "Expected shared Studio layout fixtures")

        for file in files {
            let fixture = try StudioJSON.makeDecoder().decode(LayoutFixture.self, from: Data(contentsOf: file))
            let natural = StudioCanvasMath.naturalCanvas(project: fixture.project)
            assertRect(natural, fixture.naturalCanvas, file: file, caseIndex: nil, prefix: "naturalCanvas")

            for index in fixture.cases.indices {
                let testCase = fixture.cases[index]
                let actual = StudioLayoutResolver.resolve(
                    project: fixture.project,
                    time: testCase.time,
                    canvasWidth: testCase.canvas.width,
                    canvasHeight: testCase.canvas.height
                )
                assertFrame(actual, testCase.expected, file: file, caseIndex: index)
            }
        }
    }

    func testSharedTimeMapFixtures() throws {
        let files = try fixtureFiles(in: "timemap")
        XCTAssertFalse(files.isEmpty, "Expected shared Studio time-map fixtures")

        for file in files {
            let fixture = try StudioJSON.makeDecoder().decode(TimeMapFixture.self, from: Data(contentsOf: file))
            let map = StudioTimeMap(sourceDuration: fixture.sourceDuration, edits: fixture.edits)

            XCTAssertEqual(map.outputDuration, fixture.expected.outputDuration, accuracy: 1e-6, "\(file.lastPathComponent) outputDuration")
            XCTAssertEqual(map.segments.count, fixture.expected.segments.count, "\(file.lastPathComponent) segment count")
            for index in 0..<min(map.segments.count, fixture.expected.segments.count) {
                XCTAssertEqual(map.segments[index].start, fixture.expected.segments[index].start, accuracy: 1e-6, "\(file.lastPathComponent) segment \(index) start")
                XCTAssertEqual(map.segments[index].end, fixture.expected.segments[index].end, accuracy: 1e-6, "\(file.lastPathComponent) segment \(index) end")
            }
            for index in fixture.expected.sourceToOutput.indices {
                let sample = fixture.expected.sourceToOutput[index]
                XCTAssertEqual(map.sourceToOutput(sample.source), sample.output, accuracy: 1e-6, "\(file.lastPathComponent) sourceToOutput \(index)")
            }
            for index in fixture.expected.outputToSource.indices {
                let sample = fixture.expected.outputToSource[index]
                XCTAssertEqual(map.outputToSource(sample.output), sample.source, accuracy: 1e-6, "\(file.lastPathComponent) outputToSource \(index)")
            }
        }
    }

    // MARK: - Assertions

    private func assertFrame(_ actual: StudioResolvedFrame, _ expected: StudioResolvedFrame, file: URL, caseIndex: Int) {
        XCTAssertEqual(actual.sceneIndex, expected.sceneIndex, message(file, caseIndex, "sceneIndex"))
        XCTAssertEqual(actual.layout, expected.layout, message(file, caseIndex, "layout"))
        XCTAssertEqual(actual.screen == nil, expected.screen == nil, message(file, caseIndex, "screen nil"))
        XCTAssertEqual(actual.camera == nil, expected.camera == nil, message(file, caseIndex, "camera nil"))

        if let actualScreen = actual.screen, let expectedScreen = expected.screen {
            assertRect(actualScreen.rect, expectedScreen.rect, file: file, caseIndex: caseIndex, prefix: "screen.rect")
            assertRect(actualScreen.source, expectedScreen.source, file: file, caseIndex: caseIndex, prefix: "screen.source")
            XCTAssertEqual(actualScreen.cornerRadius, expectedScreen.cornerRadius, accuracy: 1e-6, message(file, caseIndex, "screen.cornerRadius"))
            assertShadow(actualScreen.shadow, expectedScreen.shadow, file: file, caseIndex: caseIndex, prefix: "screen.shadow")
        }

        if let actualCamera = actual.camera, let expectedCamera = expected.camera {
            assertRect(actualCamera.rect, expectedCamera.rect, file: file, caseIndex: caseIndex, prefix: "camera.rect")
            assertRect(actualCamera.source, expectedCamera.source, file: file, caseIndex: caseIndex, prefix: "camera.source")
            XCTAssertEqual(actualCamera.shape, expectedCamera.shape, message(file, caseIndex, "camera.shape"))
            XCTAssertEqual(actualCamera.cornerRadius, expectedCamera.cornerRadius, accuracy: 1e-6, message(file, caseIndex, "camera.cornerRadius"))
            XCTAssertEqual(actualCamera.mirror, expectedCamera.mirror, message(file, caseIndex, "camera.mirror"))
            XCTAssertEqual(actualCamera.borderWidth, expectedCamera.borderWidth, accuracy: 1e-6, message(file, caseIndex, "camera.borderWidth"))
            assertShadow(actualCamera.shadow, expectedCamera.shadow, file: file, caseIndex: caseIndex, prefix: "camera.shadow")
            XCTAssertEqual(actualCamera.sourceTime, expectedCamera.sourceTime, accuracy: 1e-6, message(file, caseIndex, "camera.sourceTime"))
            XCTAssertEqual(actualCamera.visible, expectedCamera.visible, message(file, caseIndex, "camera.visible"))
        }
    }

    private func assertRect(_ actual: StudioRect, _ expected: StudioRect, file: URL, caseIndex: Int?, prefix: String) {
        XCTAssertEqual(actual.x, expected.x, accuracy: 1e-6, message(file, caseIndex, "\(prefix).x"))
        XCTAssertEqual(actual.y, expected.y, accuracy: 1e-6, message(file, caseIndex, "\(prefix).y"))
        XCTAssertEqual(actual.width, expected.width, accuracy: 1e-6, message(file, caseIndex, "\(prefix).width"))
        XCTAssertEqual(actual.height, expected.height, accuracy: 1e-6, message(file, caseIndex, "\(prefix).height"))
    }

    private func assertShadow(_ actual: StudioResolvedShadow, _ expected: StudioResolvedShadow, file: URL, caseIndex: Int, prefix: String) {
        XCTAssertEqual(actual.blur, expected.blur, accuracy: 1e-6, message(file, caseIndex, "\(prefix).blur"))
        XCTAssertEqual(actual.offsetY, expected.offsetY, accuracy: 1e-6, message(file, caseIndex, "\(prefix).offsetY"))
        XCTAssertEqual(actual.opacity, expected.opacity, accuracy: 1e-6, message(file, caseIndex, "\(prefix).opacity"))
    }

    private func message(_ file: URL, _ caseIndex: Int?, _ field: String) -> String {
        if let caseIndex = caseIndex {
            return "\(file.lastPathComponent) case \(caseIndex) \(field)"
        }
        return "\(file.lastPathComponent) \(field)"
    }

    // MARK: - Fixtures

    private func fixtureFiles(in folder: String) throws -> [URL] {
        let url = repoRoot()
            .appendingPathComponent("shared", isDirectory: true)
            .appendingPathComponent("studio", isDirectory: true)
            .appendingPathComponent("fixtures", isDirectory: true)
            .appendingPathComponent(folder, isDirectory: true)
        guard FileManager.default.fileExists(atPath: url.path) else { return [] }
        return try FileManager.default.contentsOfDirectory(at: url, includingPropertiesForKeys: nil)
            .filter { $0.pathExtension.lowercased() == "json" }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
    }

    // This file lives at <repo>/mac/TinyClipsTests/, so the repository root is three levels up.
    private func repoRoot() -> URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
    }

    private struct LayoutFixture: Decodable {
        var project: StudioProject
        var naturalCanvas: StudioRect
        var cases: [LayoutFixtureCase]
    }

    private struct CanvasFixture: Decodable {
        var cases: [CanvasFixtureCase]
    }

    private struct CanvasFixtureCase: Decodable {
        var natural: StudioRect
        var limit: Double
        var expected: StudioRect
    }

    private struct LayoutFixtureCase: Decodable {
        var time: Double
        var canvas: StudioRect
        var expected: StudioResolvedFrame
    }

    private struct TimeMapFixture: Decodable {
        var sourceDuration: Double
        var edits: StudioEdits
        var expected: TimeMapExpected
    }

    private struct TimeMapExpected: Decodable {
        var outputDuration: Double
        var segments: [StudioTimeSegment]
        var sourceToOutput: [TimeMapSourceSample]
        var outputToSource: [TimeMapOutputSample]
    }

    private struct TimeMapSourceSample: Decodable {
        var source: Double
        var output: Double
    }

    private struct TimeMapOutputSample: Decodable {
        var output: Double
        var source: Double
    }
}
