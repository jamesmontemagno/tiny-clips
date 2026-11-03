import SwiftUI
import XCTest
@testable import TinyClips

final class ScreenshotFixtureTests: XCTestCase {
    private let tolerance: CGFloat = 1e-6

    func testSharedArrowFixtures() throws {
        let files = try fixtureFiles(in: "arrow")
        XCTAssertFalse(files.isEmpty, "Expected shared screenshot arrow fixtures")

        for file in files {
            let fixture = try JSONDecoder().decode(ArrowFixture.self, from: Data(contentsOf: file))
            for (index, testCase) in fixture.cases.enumerated() {
                let label = "\(file.lastPathComponent) case \(index)"
                let style = try XCTUnwrap(ArrowStyle(rawValue: testCase.style), "\(label) style")
                let actual = ArrowGeometry.build(
                    start: testCase.start.point,
                    end: testCase.end.point,
                    width: testCase.width,
                    style: style,
                    scale: testCase.scale
                )
                let expected = testCase.expected
                XCTAssertEqual(actual.curved, expected.curved, "\(label) curved")
                assertPoint(actual.shaftStart, expected.shaftStart, "\(label) shaftStart")
                assertPoint(actual.shaftControl, expected.shaftControl, "\(label) shaftControl")
                assertPoint(actual.shaftEnd, expected.shaftEnd, "\(label) shaftEnd")
                assertPoint(actual.tip, expected.tip, "\(label) tip")
                assertPoint(actual.head1, expected.head1, "\(label) head1")
                assertPoint(actual.head2, expected.head2, "\(label) head2")
            }
        }
    }

    func testDefaultStrokeWidthKeepsTheOldLookOnLargeCaptures() {
        XCTAssertEqual(AnnotationStrokeMath.defaultWidth(forImagePixelWidth: 640), 4)
        XCTAssertEqual(AnnotationStrokeMath.defaultWidth(forImagePixelWidth: 1800), 8)
        XCTAssertEqual(AnnotationStrokeMath.defaultWidth(forImagePixelWidth: 2880), 13)
        XCTAssertEqual(AnnotationStrokeMath.defaultWidth(forImagePixelWidth: 20_000), 40)
    }

    func testMatchingCapsStrokeNativelyWithNoExtraShapes() {
        let stroke = OpenStrokeShape(
            start: .zero, startDirection: CGPoint(x: 10, y: 0), startCap: .square,
            end: CGPoint(x: 10, y: 0), endDirection: .zero, endCap: .square,
            width: 4
        )
        XCTAssertEqual(stroke.lineCap, .square)
        XCTAssertTrue(stroke.capPaths.isEmpty)
    }

    func testMixedCapsStrokeFlatAndAddEachCapBeyondItsEnd() {
        let stroke = OpenStrokeShape(
            start: .zero, startDirection: CGPoint(x: 10, y: 0), startCap: .butt,
            end: CGPoint(x: 10, y: 0), endDirection: .zero, endCap: .square,
            width: 4
        )
        XCTAssertEqual(stroke.lineCap, .butt)
        XCTAssertEqual(stroke.capPaths.count, 1)
        let bounds = stroke.capPaths[0].boundingBoxOfPath
        XCTAssertEqual(bounds.minX, 10, accuracy: tolerance)
        XCTAssertEqual(bounds.maxX, 12, accuracy: tolerance)
        XCTAssertEqual(bounds.minY, -2, accuracy: tolerance)
        XCTAssertEqual(bounds.maxY, 2, accuracy: tolerance)
    }

    func testRoundCapIsACircleAroundTheEnd() throws {
        let cap = try XCTUnwrap(OpenStrokeShape.capPath(.round, at: CGPoint(x: 5, y: 5), awayFrom: .zero, width: 6))
        XCTAssertEqual(cap.boundingBoxOfPath, CGRect(x: 2, y: 2, width: 6, height: 6))
    }

    func testOpaqueComponentsSplitAlpha() {
        let (opaque, alpha) = Color.red.opacity(0.4).opaqueComponents
        XCTAssertEqual(alpha, 0.4, accuracy: 0.001)
        XCTAssertEqual(opaque.opaqueComponents.alpha, 1, accuracy: 0.001)
    }

    func testOnlyOpenStrokesTakeCapsAndOnlyRectanglesTakeCorners() {
        XCTAssertTrue(EditTool.line.usesStartCap && EditTool.line.usesEndCap)
        XCTAssertTrue(EditTool.arrow.usesStartCap)
        XCTAssertFalse(EditTool.arrow.usesEndCap)
        XCTAssertFalse(EditTool.pencil.usesStartCap)
        XCTAssertTrue(EditTool.rectangle.usesJoin)
        XCTAssertFalse(EditTool.circle.usesJoin)
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
        var curved: Bool
        var shaftStart: FixturePoint
        var shaftControl: FixturePoint
        var shaftEnd: FixturePoint
        var tip: FixturePoint
        var head1: FixturePoint
        var head2: FixturePoint
    }

    private struct ArrowCase: Decodable {
        var start: FixturePoint
        var end: FixturePoint
        var width: CGFloat
        var style: String
        var scale: CGFloat
        var expected: ExpectedArrow
    }

    private struct ArrowFixture: Decodable {
        var cases: [ArrowCase]
    }
}
