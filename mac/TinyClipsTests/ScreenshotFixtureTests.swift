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
