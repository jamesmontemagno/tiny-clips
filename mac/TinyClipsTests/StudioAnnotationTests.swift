import XCTest
@testable import TinyClips

/// Section 6.10 of the project format, against the fixtures Windows is held to as well.
final class StudioAnnotationTests: XCTestCase {
    private func project(overlays: String) throws -> StudioProject {
        let json = """
        { "id": "00000000-0000-0000-0000-000000000001", "sources": { "screen": { "width": 1920, "height": 1080, "duration": 10 } }, "overlays": \(overlays) }
        """
        return try StudioJSON.makeDecoder().decode(StudioProject.self, from: Data(json.utf8))
    }

    func testSharedFixtures() throws {
        let folder = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("shared/studio/fixtures/annotations", isDirectory: true)
        let files = try FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)
            .filter { $0.pathExtension == "json" }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
        XCTAssertFalse(files.isEmpty, "Expected shared Studio annotation fixtures")

        for file in files {
            let fixture = try StudioJSON.makeDecoder().decode(Fixture.self, from: Data(contentsOf: file))
            for (caseIndex, testCase) in fixture.cases.enumerated() {
                verify(testCase, in: fixture.project, label: "\(file.lastPathComponent) case \(caseIndex)")
            }
        }
    }

    private func verify(_ testCase: Fixture.Case, in project: StudioProject, label: String) {
        let width = testCase.canvas.width
        let height = testCase.canvas.height
        let frame = StudioLayoutResolver.resolve(project: project, time: testCase.time, canvasWidth: width, canvasHeight: height, events: nil)
        let actual = StudioAnnotationGeometry.resolveAll(project: project, frame: frame, time: testCase.time, canvasWidth: width, canvasHeight: height)

        XCTAssertEqual(actual.count, testCase.expected.count, "\(label) count")
        for (position, (item, expected)) in zip(actual, testCase.expected).enumerated() {
            let name = "\(label) item \(position)"
            XCTAssertEqual(item.index, expected.index, "\(name) index")
            XCTAssertEqual(item.kind, expected.kind, "\(name) kind")
            XCTAssertEqual(item.fill, expected.fill, "\(name) fill")
            XCTAssertEqual(item.textColor, expected.textColor, "\(name) textColor")
            near(item.opacity, expected.opacity, "\(name) opacity")
            near(item.anchor, expected.anchor, "\(name) anchor")
            if item.isArrow {
                near(item.stroke, expected.stroke ?? .nan, "\(name) stroke")
                near(item.halfWidth, expected.halfWidth ?? .nan, "\(name) halfWidth")
                XCTAssertEqual(item.hasShaft, expected.hasShaft, "\(name) hasShaft")
                near(item.tail, expected.tail, "\(name) tail")
                near(item.headBase, expected.headBase, "\(name) headBase")
                continue
            }

            near(item.font, expected.font ?? .nan, "\(name) font")
            near(item.maxTextWidth, expected.maxTextWidth ?? .nan, "\(name) maxTextWidth")
            near(item.radius, expected.radius ?? .nan, "\(name) radius")
            let placed = StudioAnnotationGeometry.layoutBox(
                item,
                textWidth: testCase.textSize.width,
                textHeight: testCase.textSize.height,
                canvasWidth: width,
                canvasHeight: height
            )
            near(placed.box, expected.box, "\(name) box")
            near(placed.textRect, expected.textRect, "\(name) textRect")
            XCTAssertEqual(placed.pointer == nil, expected.pointer == nil, "\(name) pointer")
            if let pointer = placed.pointer, let points = expected.pointer, points.count == 2 {
                near(pointer.a, points[0], "\(name) pointer a")
                near(pointer.b, points[1], "\(name) pointer b")
            }
        }
    }

    func testRoundTripKeepsEveryPropertyAndAKindFromALaterVersion() throws {
        let project = try project(overlays: #"""
        { "annotations": [
          { "id": "a", "kind": "callout", "attach": "frame", "start": 1.5, "end": 3.25, "x": 0.1, "y": 0.2, "offsetX": 0.3, "offsetY": -0.4, "text": "Line one\nLine two", "style": "dark", "size": "large", "later": { "keep": true } },
          { "id": "b", "kind": "highlight", "start": 1, "end": 2, "strength": 3 }
        ] }
        """#)

        let encoded = try StudioJSON.makeEncoder().encode(project)
        let again = try StudioJSON.makeDecoder().decode(StudioProject.self, from: encoded)

        XCTAssertEqual(again.overlays.annotations, project.overlays.annotations)
        let first = again.overlays.annotations[0]
        XCTAssertEqual(first.id, "a")
        XCTAssertEqual(first.kind, StudioAnnotationKind.callout)
        XCTAssertEqual(first.attach, .frame)
        XCTAssertEqual(first.start, 1.5)
        XCTAssertEqual(first.end, 3.25)
        XCTAssertEqual(first.x, 0.1)
        XCTAssertEqual(first.y, 0.2)
        XCTAssertEqual(first.offsetX, 0.3)
        XCTAssertEqual(first.offsetY, -0.4)
        XCTAssertEqual(first.text, "Line one\nLine two")
        XCTAssertEqual(first.style, .dark)
        XCTAssertEqual(first.size, .large)
        XCTAssertEqual(first.extra["later"], .object(["keep": .bool(true)]))
        XCTAssertEqual(again.overlays.annotations[1].kind, "highlight")
        XCTAssertEqual(again.overlays.annotations[1].extra["strength"], .number(3))
    }

    func testAProjectWithoutAnnotationsReadsAnEmptyList() throws {
        for overlays in ["{ }", #"{ "annotations": null }"#, #"{ "annotations": [] }"#, #"{ "annotations": [ null ] }"#] {
            XCTAssertEqual(try project(overlays: overlays).overlays.annotations, [], overlays)
        }
    }

    func testMissingAndNullPropertiesTakeTheDefaults() throws {
        let project = try project(overlays: #"""
        { "annotations": [ { }, { "id": null, "kind": null, "attach": null, "x": null, "offsetX": null, "offsetY": null, "text": null, "style": null, "size": null } ] }
        """#)

        XCTAssertEqual(project.overlays.annotations.count, 2)
        for annotation in project.overlays.annotations {
            XCTAssertEqual(annotation, StudioAnnotation())
        }
    }

    func testEditableStateSeesAChangedAnnotation() {
        var project = StudioProject(id: "p", sources: StudioSources(screen: StudioScreenSource(width: 1920, height: 1080, duration: 10)))
        project.overlays.annotations = [StudioAnnotation(kind: StudioAnnotationKind.text, end: 1, text: "One")]
        let state = StudioEditableState(project: project)
        project.overlays.annotations[0].text = "Two"

        XCTAssertNotEqual(state, StudioEditableState(project: project))
    }

    // MARK: - Helpers

    private func near(_ actual: Double, _ expected: Double, _ message: String) {
        XCTAssertEqual(actual, expected, accuracy: 1e-6, message)
    }

    private func near(_ actual: StudioAnnotationPoint, _ expected: StudioAnnotationPoint?, _ message: String) {
        near(actual.x, expected?.x ?? .nan, message + ".x")
        near(actual.y, expected?.y ?? .nan, message + ".y")
    }

    private func near(_ actual: StudioRect, _ expected: StudioRect?, _ message: String) {
        near(actual.x, expected?.x ?? .nan, message + ".x")
        near(actual.y, expected?.y ?? .nan, message + ".y")
        near(actual.width, expected?.width ?? .nan, message + ".width")
        near(actual.height, expected?.height ?? .nan, message + ".height")
    }

    private struct Fixture: Decodable {
        var project: StudioProject
        var cases: [Case]

        struct Size: Decodable {
            var width: Double
            var height: Double
        }

        struct Case: Decodable {
            var time: Double
            var canvas: Size
            var textSize: Size
            var expected: [Expected]
        }

        struct Expected: Decodable {
            var index: Int
            var kind: String
            var opacity: Double
            var fill: String
            var textColor: String
            var anchor: StudioAnnotationPoint
            var stroke: Double?
            var tail: StudioAnnotationPoint?
            var headBase: StudioAnnotationPoint?
            var halfWidth: Double?
            var hasShaft: Bool?
            var font: Double?
            var maxTextWidth: Double?
            var radius: Double?
            var box: StudioRect?
            var textRect: StudioRect?
            var pointer: [StudioAnnotationPoint]?
        }
    }
}
