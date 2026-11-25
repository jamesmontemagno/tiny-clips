import SwiftUI
import XCTest
@testable import TinyClips

final class ScreenshotFixtureTests: XCTestCase {
    private let tolerance: CGFloat = 1e-6

    func testSharedArrowFixtures() throws {
        let files = try fixtureFiles(in: "arrow").filter { $0.lastPathComponent != "handle.json" }
        XCTAssertFalse(files.isEmpty, "Expected shared screenshot arrow fixtures")

        for file in files {
            let fixture = try JSONDecoder().decode(ArrowFixture.self, from: Data(contentsOf: file))
            for (index, testCase) in fixture.cases.enumerated() {
                let label = "\(file.lastPathComponent) case \(index)"
                let actual = ArrowGeometry.build(
                    start: testCase.start.point,
                    end: testCase.end.point,
                    width: testCase.width,
                    options: ArrowShapeOptions(
                        bend: testCase.bend,
                        startWidthScale: testCase.startWidthScale,
                        endWidthScale: testCase.endWidthScale,
                        headScale: testCase.headScale
                    ),
                    startCap: try XCTUnwrap(StrokeCap(rawValue: testCase.startCap), label),
                    join: try XCTUnwrap(StrokeJoin(rawValue: testCase.join), label),
                    scale: testCase.scale
                )
                assertPoint(actual.control, testCase.expected.control, "\(label) control")
                assertPoint(actual.handle, testCase.expected.handle, "\(label) handle")
                XCTAssertEqual(actual.points.count, testCase.expected.points.count, "\(label) point count")
                for (pointIndex, pair) in zip(actual.points, testCase.expected.points).enumerated() {
                    assertPoint(pair.0, pair.1, "\(label) point \(pointIndex)")
                }
            }
            if let preset = fixture.preset {
                let name = file.deletingPathExtension().lastPathComponent
                let options = ArrowShapeOptions(
                    bend: preset.bend,
                    startWidthScale: preset.startWidthScale,
                    endWidthScale: preset.endWidthScale,
                    headScale: preset.headScale
                )
                XCTAssertEqual(options.preset.rawValue, name)
            }
        }
    }

    func testSharedArrowHandleFixtures() throws {
        let file = fixtureURL("arrow", "handle.json")
        let fixture = try JSONDecoder().decode(HandleFixture.self, from: Data(contentsOf: file))
        for (index, testCase) in fixture.cases.enumerated() {
            let actual = ArrowGeometry.bend(start: testCase.start.point, end: testCase.end.point, handle: testCase.handle.point)
            XCTAssertEqual(actual, testCase.expected, accuracy: tolerance, "handle.json case \(index)")
        }
    }

    func testSharedRectangleAlignmentFixtures() throws {
        let file = fixtureURL("alignment", "rectangle.json")
        let fixture = try JSONDecoder().decode(RectangleAlignmentFixture.self, from: Data(contentsOf: file))
        XCTAssertFalse(fixture.cases.isEmpty)
        for (index, testCase) in fixture.cases.enumerated() {
            let label = "rectangle.json case \(index)"
            let actual = RectStrokeRing(
                bounds: testCase.bounds.rect,
                width: testCase.width,
                alignment: try XCTUnwrap(StrokeAlignment(rawValue: testCase.alignment), label),
                join: try XCTUnwrap(StrokeJoin(rawValue: testCase.join), label)
            )
            assertRect(actual.outer, testCase.expected.outer, "\(label) outer")
            XCTAssertEqual(actual.outerJoin.rawValue, testCase.expected.outerJoin, "\(label) outerJoin")
            XCTAssertEqual(actual.cornerSize, testCase.expected.cornerSize, accuracy: tolerance, "\(label) cornerSize")
            assertRect(actual.inner, testCase.expected.inner, "\(label) inner")
        }
    }

    func testCurvedArrowsAreHitAlongTheCurveNotInsideTheArc() {
        let start = CGPoint(x: 100, y: 100)
        let end = CGPoint(x: 400, y: 100)
        let handle = ArrowGeometry.handle(start: start, end: end, bend: 0.2)

        XCTAssertLessThan(ArrowGeometry.distance(from: handle, start: start, end: end, bend: 0.2), 0.5)
        // The chord's midpoint is inside the arc, 60 px from the curve.
        XCTAssertGreaterThan(ArrowGeometry.distance(from: CGPoint(x: 250, y: 100), start: start, end: end, bend: 0.2), 55)
    }

    func testDefaultStrokeWidthKeepsTheOldLookOnLargeCaptures() {
        XCTAssertEqual(AnnotationStrokeMath.defaultWidth(forImagePixelWidth: 640), 4)
        XCTAssertEqual(AnnotationStrokeMath.defaultWidth(forImagePixelWidth: 1800), 8)
        XCTAssertEqual(AnnotationStrokeMath.defaultWidth(forImagePixelWidth: 2880), 13)
        XCTAssertEqual(AnnotationStrokeMath.defaultWidth(forImagePixelWidth: 20_000), 40)
    }

    func testSharedLineFixtures() throws {
        let file = fixtureURL("line", "line.json")
        let fixture = try JSONDecoder().decode(LineFixture.self, from: Data(contentsOf: file))
        XCTAssertFalse(fixture.cases.isEmpty)
        for (index, testCase) in fixture.cases.enumerated() {
            let label = "line.json case \(index)"
            let actual = LineGeometry.outline(
                start: testCase.start.point,
                end: testCase.end.point,
                width: testCase.width,
                startCap: try XCTUnwrap(StrokeCap(rawValue: testCase.startCap), label),
                endCap: try XCTUnwrap(StrokeCap(rawValue: testCase.endCap), label),
                scale: testCase.scale
            )
            XCTAssertEqual(actual.count, testCase.expected.count, "\(label) point count")
            for (pointIndex, pair) in zip(actual, testCase.expected).enumerated() {
                assertPoint(pair.0, pair.1, "\(label) point \(pointIndex)")
            }
        }
    }

    func testSharedPenFixtures() throws {
        let file = fixtureURL("pen", "pen.json")
        let fixture = try JSONDecoder().decode(PenFixture.self, from: Data(contentsOf: file))
        XCTAssertFalse(fixture.cases.isEmpty)
        for testCase in fixture.cases {
            let label = "pen.json \(testCase.description)"
            let actual = PenGeometry.outline(testCase.points.map(\.point), width: testCase.width)
            XCTAssertEqual(actual.count, testCase.expected.count, "\(label) point count")
            for (pointIndex, pair) in zip(actual, testCase.expected).enumerated() {
                assertPoint(pair.0, pair.1, "\(label) point \(pointIndex)")
            }
        }
    }

    func testOnlyArrowsAndLinesTakeAnOutline() {
        XCTAssertTrue(EditTool.arrow.usesOutline)
        XCTAssertTrue(EditTool.line.usesOutline)
        XCTAssertFalse(EditTool.rectangle.usesOutline)
        XCTAssertTrue(EditTool.line.usesAlignment)
    }

    func testOnlyOpenStrokesTakeCapsAndOnlyRectanglesTakeCorners() {
        XCTAssertTrue(EditTool.line.usesStartCap && EditTool.line.usesEndCap)
        XCTAssertTrue(EditTool.arrow.usesStartCap)
        XCTAssertFalse(EditTool.arrow.usesEndCap)
        XCTAssertFalse(EditTool.pencil.usesStartCap)
        XCTAssertTrue(EditTool.rectangle.usesJoin)
        XCTAssertTrue(EditTool.arrow.usesJoin)
        XCTAssertFalse(EditTool.circle.usesJoin)
    }

    private func assertRect(_ actual: CGRect, _ expected: FixtureRect, _ label: String) {
        XCTAssertEqual(actual.minX, expected.x, accuracy: tolerance, "\(label).x")
        XCTAssertEqual(actual.minY, expected.y, accuracy: tolerance, "\(label).y")
        XCTAssertEqual(actual.width, expected.width, accuracy: tolerance, "\(label).width")
        XCTAssertEqual(actual.height, expected.height, accuracy: tolerance, "\(label).height")
    }

    private func fixtureURL(_ folder: String, _ name: String) -> URL {
        repoRoot()
            .appendingPathComponent("shared/screenshot/fixtures", isDirectory: true)
            .appendingPathComponent(folder, isDirectory: true)
            .appendingPathComponent(name)
    }

    private func assertPoint(_ actual: CGPoint, _ expected: FixturePoint, _ label: String) {
        XCTAssertEqual(actual.x, expected.x, accuracy: tolerance, "\(label).x")
        XCTAssertEqual(actual.y, expected.y, accuracy: tolerance, "\(label).y")
    }

    private func fixtureFiles(in folder: String) throws -> [URL] {
        let url = repoRoot()
            .appendingPathComponent("shared", isDirectory: true)
            .appendingPathComponent("screenshot", isDirectory: true)
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

    private struct FixturePoint: Decodable {
        var x: CGFloat
        var y: CGFloat
        var point: CGPoint { CGPoint(x: x, y: y) }
    }

    private struct ExpectedArrow: Decodable {
        var control: FixturePoint
        var handle: FixturePoint
        var points: [FixturePoint]
    }

    private struct ArrowCase: Decodable {
        var start: FixturePoint
        var end: FixturePoint
        var width: CGFloat
        var bend: CGFloat
        var startWidthScale: CGFloat
        var endWidthScale: CGFloat
        var headScale: CGFloat
        var startCap: String
        var join: String
        var scale: CGFloat
        var expected: ExpectedArrow
    }

    private struct ArrowPresetValues: Decodable {
        var bend: CGFloat
        var startWidthScale: CGFloat
        var endWidthScale: CGFloat
        var headScale: CGFloat
    }

    private struct HandleCase: Decodable {
        var start: FixturePoint
        var end: FixturePoint
        var handle: FixturePoint
        var expected: CGFloat
    }

    private struct LineCase: Decodable {
        var start: FixturePoint
        var end: FixturePoint
        var width: CGFloat
        var startCap: String
        var endCap: String
        var scale: CGFloat
        var expected: [FixturePoint]
    }

    private struct PenCase: Decodable {
        var description: String
        var points: [FixturePoint]
        var width: CGFloat
        var expected: [FixturePoint]
    }

    private struct PenFixture: Decodable {
        var cases: [PenCase]
    }

    private struct LineFixture: Decodable {
        var cases: [LineCase]
    }

    private struct HandleFixture: Decodable {
        var cases: [HandleCase]
    }

    private struct FixtureRect: Decodable {
        var x: CGFloat
        var y: CGFloat
        var width: CGFloat
        var height: CGFloat
        var rect: CGRect { CGRect(x: x, y: y, width: width, height: height) }
    }

    private struct ExpectedRing: Decodable {
        var outer: FixtureRect
        var outerJoin: String
        var cornerSize: CGFloat
        var inner: FixtureRect
    }

    private struct RectangleAlignmentCase: Decodable {
        var bounds: FixtureRect
        var width: CGFloat
        var alignment: String
        var join: String
        var expected: ExpectedRing
    }

    private struct RectangleAlignmentFixture: Decodable {
        var cases: [RectangleAlignmentCase]
    }

    private struct ArrowFixture: Decodable {
        var preset: ArrowPresetValues?
        var cases: [ArrowCase]
    }

    func testThickArrowsAreHitAcrossTheirHeadAndShaft() {
        let outline = ArrowGeometry.build(
            start: CGPoint(x: 100, y: 100), end: CGPoint(x: 400, y: 100), width: 20,
            options: .classic, startCap: .round, join: .miter
        ).points
        // In the head's wing, 24 px from the centerline: farther than half the shaft's width.
        XCTAssertTrue(ArrowGeometry.outlineContains(outline, point: CGPoint(x: 345, y: 124), tolerance: 0))
        // Beside the 20 px shaft.
        XCTAssertFalse(ArrowGeometry.outlineContains(outline, point: CGPoint(x: 250, y: 130), tolerance: 0))
        XCTAssertTrue(ArrowGeometry.outlineContains(outline, point: CGPoint(x: 250, y: 113), tolerance: 4))
    }
}
