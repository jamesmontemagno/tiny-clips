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

    func testABitmapCoversOnlyWhatIsOnTheCanvas() throws {
        func bounds(_ minX: Double, _ minY: Double, _ maxX: Double, _ maxY: Double) -> StudioRect? {
            StudioAnnotationGeometry.bitmapBounds(minX: minX, minY: minY, maxX: maxX, maxY: maxY, canvasWidth: 1920, canvasHeight: 1080)
        }

        // Inside the canvas: whole pixels, with two to spare for the edges.
        XCTAssertEqual(bounds(100.5, 200.25, 300.5, 400.75), StudioRect(x: 98, y: 198, width: 205, height: 205))

        // A box far larger than any bitmap can be still shows the part on the canvas.
        XCTAssertEqual(bounds(10, 20, 50_000, 90_000), StudioRect(x: 8, y: 18, width: 1912, height: 1062))
        XCTAssertEqual(bounds(-500, -500, 30, 40), StudioRect(x: 0, y: 0, width: 32, height: 42))

        // Nothing on the canvas, past each of its sides.
        XCTAssertNil(bounds(2000, 10, 3000, 20))
        XCTAssertNil(bounds(10, 1100, 20, 1200))
        XCTAssertNil(bounds(-300, 10, -200, 20))
        XCTAssertNil(bounds(10, -300, 20, -200))
    }

    func testEditableStateSeesAChangedAnnotation() {
        var project = StudioProject(id: "p", sources: StudioSources(screen: StudioScreenSource(width: 1920, height: 1080, duration: 10)))
        project.overlays.annotations = [StudioAnnotation(kind: StudioAnnotationKind.text, end: 1, text: "One")]
        let state = StudioEditableState(project: project)
        project.overlays.annotations[0].text = "Two"

        XCTAssertNotEqual(state, StudioEditableState(project: project))
    }

    // MARK: - Editing

    private func model(_ annotations: [StudioAnnotation] = [], zooms: [StudioZoom] = []) -> StudioEditorModel {
        var project = StudioProject(
            id: "00000000-0000-0000-0000-000000000001",
            sources: StudioSources(screen: StudioScreenSource(width: 1920, height: 1080, duration: 60)),
            scenes: [StudioScene(layout: .screen)]
        )
        project.canvas.padding = 0
        project.zooms = zooms
        project.overlays.annotations = annotations
        return StudioEditorModel(project: project)
    }

    func testAddingPutsItOnTopForThreeSecondsWithTheDefaultsOfItsKind() {
        var editor = model()
        XCTAssertEqual(editor.addAnnotation(kind: StudioAnnotationKind.arrow, at: 10, events: nil), StudioAnnotationEditResult(changed: true, index: 0))
        XCTAssertEqual(editor.addAnnotation(kind: StudioAnnotationKind.callout, at: 10, events: nil), StudioAnnotationEditResult(changed: true, index: 1))
        XCTAssertEqual(editor.addAnnotation(kind: StudioAnnotationKind.text, at: 59.95, events: nil), StudioAnnotationEditResult(changed: true, index: 2))

        let list = editor.project.overlays.annotations
        XCTAssertEqual(list[0].start, 10)
        XCTAssertEqual(list[0].end, 13)
        XCTAssertEqual(list[0].attach, .screen)
        XCTAssertEqual(list[0].style, .red)
        XCTAssertEqual(list[0].text, "")
        XCTAssertEqual(list[1].style, .dark)
        XCTAssertEqual(list[1].text, StudioEditorModel.newAnnotationText)
        XCTAssertEqual(list[2].attach, .frame)
        XCTAssertEqual(list[2].style, .white)
        XCTAssertEqual(list[2].start, 60 - StudioEditorModel.minimumAnnotationDuration, accuracy: 1e-9)
        XCTAssertEqual(list[2].end, 60)
        XCTAssertEqual(Set(list.map(\.id)).count, 3)
    }

    func testAddingGoesInTheMiddleOfWhatIsInView() {
        let zoom = StudioZoom(start: 5, end: 15, scale: 2, focus: StudioZoomFocus(x: 0.25, y: 0.25), easeIn: 0, easeOut: 0)
        var editor = model(zooms: [zoom])
        _ = editor.addAnnotation(kind: StudioAnnotationKind.arrow, at: 1, events: nil)
        _ = editor.addAnnotation(kind: StudioAnnotationKind.arrow, at: 10, events: nil)
        _ = editor.addAnnotation(kind: StudioAnnotationKind.text, at: 10, events: nil)

        let list = editor.project.overlays.annotations
        XCTAssertEqual(list[0].x, 0.5, accuracy: 1e-9)
        XCTAssertEqual(list[1].x, 0.25, accuracy: 1e-9)
        XCTAssertEqual(list[1].y, 0.25, accuracy: 1e-9)
        XCTAssertEqual(list[2].x, 0.5, accuracy: 1e-9)
    }

    func testTimingKeepsAnAnnotationInsideTheRecordingAndAtLeastTheShortest() {
        var editor = model([StudioAnnotation(start: 10, end: 20)])
        _ = editor.setAnnotationStart(at: 0, to: 30)
        XCTAssertEqual(editor.project.overlays.annotations[0].start, 20 - StudioEditorModel.minimumAnnotationDuration, accuracy: 1e-9)
        _ = editor.setAnnotationStart(at: 0, to: -5)
        XCTAssertEqual(editor.project.overlays.annotations[0].start, 0)
        _ = editor.setAnnotationEnd(at: 0, to: 500)
        XCTAssertEqual(editor.project.overlays.annotations[0].end, 60)
        _ = editor.setAnnotationEnd(at: 0, to: -5)
        XCTAssertEqual(editor.project.overlays.annotations[0].end, StudioEditorModel.minimumAnnotationDuration, accuracy: 1e-9)
        XCTAssertFalse(editor.setAnnotationStart(at: 0, to: .nan).changed)
        XCTAssertEqual(editor.setAnnotationStart(at: 5, to: 1), StudioAnnotationEditResult(changed: false, index: nil))
    }

    func testMovingKeepsItsLengthAndStopsAtBothEnds() {
        var editor = model([StudioAnnotation(start: 10, end: 14)])
        _ = editor.moveAnnotation(at: 0, to: 100)
        XCTAssertEqual(editor.project.overlays.annotations[0].start, 56)
        XCTAssertEqual(editor.project.overlays.annotations[0].end, 60)
        _ = editor.moveAnnotation(at: 0, to: -3)
        XCTAssertEqual(editor.project.overlays.annotations[0].start, 0)
        XCTAssertEqual(editor.project.overlays.annotations[0].end, 4)
    }

    func testTheLayerMovesOnePlaceAndNotPastEitherEnd() {
        var editor = model([StudioAnnotation(id: "a", end: 1), StudioAnnotation(id: "b", end: 1)])
        XCTAssertEqual(editor.moveAnnotationLayer(at: 0, by: 1), StudioAnnotationEditResult(changed: true, index: 1))
        XCTAssertEqual(editor.project.overlays.annotations.map(\.id), ["b", "a"])
        XCTAssertEqual(editor.moveAnnotationLayer(at: 1, by: 1), StudioAnnotationEditResult(changed: false, index: 1))
        XCTAssertEqual(editor.moveAnnotationLayer(at: 1, by: -1), StudioAnnotationEditResult(changed: true, index: 0))
        XCTAssertEqual(editor.moveAnnotationLayer(at: 0, by: -1), StudioAnnotationEditResult(changed: false, index: 0))
    }

    func testTypingIsOneUndoStepUntilItStops() {
        var editor = model([StudioAnnotation(kind: StudioAnnotationKind.text, end: 1, text: "x")])
        _ = editor.setAnnotationText(at: 0, to: "a")
        _ = editor.setAnnotationText(at: 0, to: "ab")
        editor.endAnnotationTyping()
        _ = editor.setAnnotationText(at: 0, to: "abc")

        editor.undo()
        XCTAssertEqual(editor.project.overlays.annotations[0].text, "ab")
        editor.undo()
        XCTAssertEqual(editor.project.overlays.annotations[0].text, "x")
        XCTAssertFalse(editor.canUndo)
    }

    func testAnotherEditEndsTheTypingToo() {
        var editor = model([StudioAnnotation(kind: StudioAnnotationKind.text, end: 1, text: "x")])
        _ = editor.setAnnotationText(at: 0, to: "a")
        _ = editor.setAnnotationStyle(at: 0, to: .blue)
        _ = editor.setAnnotationText(at: 0, to: "ab")

        editor.undo()
        XCTAssertEqual(editor.project.overlays.annotations[0].text, "a")
        XCTAssertEqual(editor.project.overlays.annotations[0].style, .blue)
    }

    func testChangingWhatItIsFixedToKeepsItWhereItIsInThePicture() {
        let zoom = StudioZoom(start: 5, end: 15, scale: 2, focus: StudioZoomFocus(x: 0.5, y: 0.5), easeIn: 0, easeOut: 0)
        var editor = model([StudioAnnotation(end: 20, x: 0.5, y: 0.25), StudioAnnotation(end: 20, x: 0.1, y: 0.1)], zooms: [zoom])

        _ = editor.setAnnotationAttach(at: 0, to: .frame, time: 10, events: nil)
        XCTAssertEqual(editor.project.overlays.annotations[0].y, 0, accuracy: 1e-9)
        _ = editor.setAnnotationAttach(at: 0, to: .screen, time: 10, events: nil)
        XCTAssertEqual(editor.project.overlays.annotations[0].y, 0.25, accuracy: 1e-9)

        // Out of view it keeps its numbers.
        _ = editor.setAnnotationAttach(at: 1, to: .frame, time: 10, events: nil)
        XCTAssertEqual(editor.project.overlays.annotations[1].attach, .frame)
        XCTAssertEqual(editor.project.overlays.annotations[1].x, 0.1, accuracy: 1e-9)
    }

    func testSteppingGoesByTimeAndThenByPlaceInTheList() {
        let editor = model([StudioAnnotation(start: 20, end: 25), StudioAnnotation(start: 5, end: 8), StudioAnnotation(start: 5, end: 6)])
        XCTAssertEqual(editor.annotationIndex(after: 1, sourceTime: 0), 2)
        XCTAssertEqual(editor.annotationIndex(after: 2, sourceTime: 0), 0)
        XCTAssertNil(editor.annotationIndex(after: 0, sourceTime: 0))
        XCTAssertEqual(editor.annotationIndex(before: 2, sourceTime: 0), 1)
        XCTAssertNil(editor.annotationIndex(before: 1, sourceTime: 0))
        XCTAssertEqual(editor.annotationIndex(after: nil, sourceTime: 7), 1)
        XCTAssertEqual(editor.annotationIndex(after: nil, sourceTime: 10), 0)
        XCTAssertEqual(editor.annotationIndex(before: nil, sourceTime: 10), 2)
        XCTAssertNil(editor.annotationIndex(before: nil, sourceTime: 1))
    }

    func testFollowingAnAnnotationGoesByItsIdOrItsPlaceWithoutOne() {
        let before = [StudioAnnotation(id: "a"), StudioAnnotation(id: "b"), StudioAnnotation()]
        XCTAssertEqual(StudioEditorModel.annotationIndex(following: 1, from: before, to: [StudioAnnotation(id: "b")]), 0)
        XCTAssertNil(StudioEditorModel.annotationIndex(following: 1, from: before, to: [StudioAnnotation(id: "a")]))
        XCTAssertEqual(StudioEditorModel.annotationIndex(following: 2, from: before, to: before), 2)
        XCTAssertNil(StudioEditorModel.annotationIndex(following: 2, from: before, to: [StudioAnnotation()]))
    }

    func testTheWordsNameAnAnnotationInOneLine() {
        let callout = StudioAnnotation(kind: StudioAnnotationKind.callout, start: 12, end: 16.5, text: "Billing lives\nhere")
        XCTAssertEqual(StudioEditorModel.annotationLabel(StudioAnnotation()), "Arrow")
        XCTAssertEqual(StudioEditorModel.annotationLabel(callout), "Callout: Billing lives here")
        XCTAssertEqual(StudioEditorModel.annotationLabel(StudioAnnotation(kind: StudioAnnotationKind.text, text: "a very long title that goes on and on")), "Text: a very long title that g\u{2026}")
        XCTAssertEqual(StudioEditorModel.annotationDescription(callout), "Callout: Billing lives here, 12.0 to 16.5 seconds")
        XCTAssertEqual(StudioEditorModel.annotationStepText(index: 1, count: 3, annotation: callout), "Annotation 2 of 3, Callout: Billing lives here, 12.0 to 16.5 seconds")
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
