import XCTest
import CoreImage
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

    // MARK: Colors and size

    func testAColorIsSixHexDigitsAndAnythingElseLeavesTheStyle() {
        XCTAssertEqual(StudioAnnotationGeometry.customColor("#12ab9F"), "#12AB9F")
        for text in ["", "red", "#12345", "#1234567", "#12345678", "#GGGGGG", "12345678", "#１２３４５６"] {
            XCTAssertNil(StudioAnnotationGeometry.customColor(text), text)
        }
        let colors = StudioAnnotationGeometry.effectiveColors(StudioAnnotation(style: .blue, fill: "#102030"))
        XCTAssertEqual(colors.fill, "#102030")
        XCTAssertEqual(colors.text, "#FFFFFF")
    }

    func testChoosingAColorIsOneUndoStepUntilSomethingElseIsEdited() {
        var editor = model([StudioAnnotation(id: "a", start: 0, end: 5)])
        XCTAssertTrue(editor.setAnnotationFill(at: 0, to: "#111111").changed)
        XCTAssertTrue(editor.setAnnotationFill(at: 0, to: "#222222").changed)
        XCTAssertFalse(editor.setAnnotationFill(at: 0, to: "#222222").changed)
        XCTAssertFalse(editor.setAnnotationFill(at: 0, to: "nope").changed)
        XCTAssertEqual(editor.project.overlays.annotations[0].fill, "#222222")
        editor.undo()
        XCTAssertEqual(editor.project.overlays.annotations[0].fill, "")

        XCTAssertTrue(editor.setAnnotationFill(at: 0, to: "#111111").changed)
        XCTAssertTrue(editor.setAnnotationTextColor(at: 0, to: "#333333").changed)
        editor.undo()
        XCTAssertEqual(editor.project.overlays.annotations[0].textColor, "")
        XCTAssertEqual(editor.project.overlays.annotations[0].fill, "#111111")
    }

    func testAColorItAlreadyHasIsNoEditAndDoesNotLeaveItsStyle() {
        var editor = model([StudioAnnotation(id: "a", start: 0, end: 5, style: .blue)])
        XCTAssertFalse(editor.setAnnotationFill(at: 0, to: "#0a84ff").changed)
        XCTAssertFalse(editor.setAnnotationTextColor(at: 0, to: "#FFFFFF").changed)
        XCTAssertEqual(editor.project.overlays.annotations[0].fill, "")
        XCTAssertEqual(editor.project.overlays.annotations[0].textColor, "")
    }

    func testAChoiceThatHasEndedIsItsOwnUndoStep() {
        var editor = model([StudioAnnotation(id: "a", start: 0, end: 5)])
        _ = editor.setAnnotationFill(at: 0, to: "#111111")
        editor.endAnnotationColorChoice()
        _ = editor.setAnnotationFill(at: 0, to: "#222222")
        editor.undo()
        XCTAssertEqual(editor.project.overlays.annotations[0].fill, "#111111")
    }

    func testChoosingAStyleTakesBackTheColorsChosenByHand() {
        var editor = model([StudioAnnotation(id: "a", start: 0, end: 5, fill: "#111111", textColor: "#222222")])
        XCTAssertTrue(editor.setAnnotationStyle(at: 0, to: .blue).changed)
        let annotation = editor.project.overlays.annotations[0]
        XCTAssertEqual(annotation.style, .blue)
        XCTAssertEqual(annotation.fill, "")
        XCTAssertEqual(annotation.textColor, "")
    }

    func testTheScaleStaysBetweenItsEnds() {
        var editor = model([StudioAnnotation(id: "a", start: 0, end: 5)])
        XCTAssertTrue(editor.setAnnotationScale(at: 0, to: 9).changed)
        XCTAssertEqual(editor.project.overlays.annotations[0].scale, 3)
        XCTAssertTrue(editor.setAnnotationScale(at: 0, to: 0).changed)
        XCTAssertEqual(editor.project.overlays.annotations[0].scale, 0.5)
        XCTAssertFalse(editor.setAnnotationScale(at: 0, to: .nan).changed)
    }

    // MARK: Dragging

    private func wholeCanvas(source: StudioRect = StudioRect(x: 0, y: 0, width: 1, height: 1)) -> StudioResolvedFrame {
        var frame = StudioLayoutResolver.resolve(project: model([]).project, time: 0, canvasWidth: 1000, canvasHeight: 500, events: nil)
        frame.screen = frame.screen.map { screen in
            var screen = screen
            screen.rect = StudioRect(x: 0, y: 0, width: 1000, height: 500)
            screen.source = source
            return screen
        }
        return frame
    }

    private func moved(_ annotation: StudioAnnotation, _ part: StudioAnnotationPart, _ x: Double, _ y: Double, frame: StudioResolvedFrame? = nil) -> StudioAnnotationMove? {
        StudioAnnotationGeometry.moved(annotation, part: part, byX: x, byY: y, frame: frame ?? wholeCanvas(), canvasWidth: 1000, canvasHeight: 500)
    }

    func testDraggingAnArrowsTipLeavesItsTailWhereItIs() throws {
        let arrow = StudioAnnotation(attach: .frame, x: 0.2, y: 0.2, offsetX: 0.2, offsetY: 0.2)
        let move = try XCTUnwrap(moved(arrow, .tip, 100, 50))
        XCTAssertEqual(move.x, 0.3, accuracy: 1e-9)
        XCTAssertEqual(move.y, 0.3, accuracy: 1e-9)
        XCTAssertEqual(move.offsetX, 0, accuracy: 1e-9)
        XCTAssertEqual(move.offsetY, 0.1, accuracy: 1e-9)
    }

    func testDraggingAnArrowsTailOrShaft() throws {
        let arrow = StudioAnnotation(attach: .frame, x: 0.2, y: 0.2, offsetX: 0.2, offsetY: 0.2)
        let tail = try XCTUnwrap(moved(arrow, .tail, 50, -25))
        XCTAssertEqual(tail.x, 0.2, accuracy: 1e-9)
        XCTAssertEqual(tail.offsetX, 0.3, accuracy: 1e-9)
        XCTAssertEqual(tail.offsetY, 0.15, accuracy: 1e-9)
        let body = try XCTUnwrap(moved(arrow, .body, 100, 50))
        XCTAssertEqual(body.x, 0.3, accuracy: 1e-9)
        XCTAssertEqual(body.y, 0.3, accuracy: 1e-9)
        XCTAssertEqual(body.offsetX, 0.2, accuracy: 1e-9)
    }

    func testDraggingABoxMovesACalloutsBoxAndAWholeText() throws {
        let callout = StudioAnnotation(kind: StudioAnnotationKind.callout, attach: .frame, x: 0.2, y: 0.2, offsetX: 0.1, offsetY: 0.1)
        let box = try XCTUnwrap(moved(callout, .box, 50, 25))
        XCTAssertEqual(box.x, 0.2, accuracy: 1e-9)
        XCTAssertEqual(box.offsetX, 0.2, accuracy: 1e-9)
        XCTAssertEqual(box.offsetY, 0.15, accuracy: 1e-9)
        let text = StudioAnnotation(kind: StudioAnnotationKind.text, attach: .frame, x: 0.2, y: 0.2)
        let whole = try XCTUnwrap(moved(text, .box, 100, 50))
        XCTAssertEqual(whole.x, 0.3, accuracy: 1e-9)
        XCTAssertEqual(whole.y, 0.3, accuracy: 1e-9)
        XCTAssertEqual(whole.offsetX, -0.12, accuracy: 1e-9)
    }

    func testABoxPushedInsideTheCanvasMovesFromWhereItIsDrawn() throws {
        let text = StudioAnnotation(kind: StudioAnnotationKind.text, attach: .frame, x: 1, y: 0.25)
        let drawn = StudioAnnotationPoint(x: 800, y: 125)
        let whole = try XCTUnwrap(StudioAnnotationGeometry.moved(text, part: .box, byX: -100, byY: 0, drawnBoxCenter: drawn, frame: wholeCanvas(), canvasWidth: 1000, canvasHeight: 500))
        XCTAssertEqual(whole.x, 0.7, accuracy: 1e-9, "It follows the pointer at once")

        let callout = StudioAnnotation(kind: StudioAnnotationKind.callout, attach: .frame, x: 0.9, y: 0.25, offsetX: 0.5, offsetY: 0)
        let box = try XCTUnwrap(StudioAnnotationGeometry.moved(callout, part: .box, byX: -100, byY: 0, drawnBoxCenter: drawn, frame: wholeCanvas(), canvasWidth: 1000, canvasHeight: 500))
        XCTAssertEqual(box.x, 0.9, accuracy: 1e-9, "What it points at stays")
        XCTAssertEqual(box.offsetX, -0.4, accuracy: 1e-9, "From 900 to 700, in short sides of 500")
    }

    func testADragSettlesOnTheCenterLinesAndSaysSo() throws {
        let text = StudioAnnotation(kind: StudioAnnotationKind.text, attach: .frame, x: 0.2, y: 0.2)
        let near = try XCTUnwrap(moved(text, .box, 298, 148))
        XCTAssertEqual(near.x, 0.5, accuracy: 1e-9)
        XCTAssertEqual(near.y, 0.5, accuracy: 1e-9)
        XCTAssertTrue(near.isOnVerticalCenter)
        XCTAssertTrue(near.isOnHorizontalCenter)
        let far = try XCTUnwrap(moved(text, .box, 290, 100))
        XCTAssertEqual(far.x, 0.49, accuracy: 1e-9)
        XCTAssertFalse(far.isOnVerticalCenter)
        XCTAssertFalse(far.isOnHorizontalCenter)
    }

    func testAScreenPointStaysOnWhatTheFrameShows() throws {
        let frame = wholeCanvas(source: StudioRect(x: 0.25, y: 0.25, width: 0.5, height: 0.5))
        let arrow = StudioAnnotation(x: 0.3, y: 0.3, offsetX: 0.1, offsetY: 0.1)
        let inside = try XCTUnwrap(moved(arrow, .body, 200, 100, frame: frame))
        XCTAssertEqual(inside.x, 0.4, accuracy: 1e-9)
        XCTAssertEqual(inside.y, 0.4, accuracy: 1e-9)
        let beyond = try XCTUnwrap(moved(arrow, .body, 5000, 5000, frame: frame))
        XCTAssertEqual(beyond.x, 0.75, accuracy: 1e-9)
        XCTAssertEqual(beyond.y, 0.75, accuracy: 1e-9)
        XCTAssertNil(moved(StudioAnnotation(x: 0.1, y: 0.1), .body, 10, 10, frame: frame), "It isn't in the picture")
    }

    func testAnArrowDoesNotCollapseUnderADrag() {
        let arrow = StudioAnnotation(attach: .frame, x: 0.2, y: 0.2, offsetX: 0.1, offsetY: 0)
        XCTAssertNil(moved(arrow, .tail, -50, 0), "The tail on the tip")
        XCTAssertNil(moved(arrow, .tip, 50, 0), "The tip on the tail")
    }

    func testMovingWritesEveryNumberAtOnce() {
        var editor = model([StudioAnnotation(id: "a", start: 0, end: 5)])
        let move = StudioAnnotationMove(x: 0.1, y: 0.2, offsetX: 0.3, offsetY: 0.4, isOnVerticalCenter: false, isOnHorizontalCenter: false)
        XCTAssertTrue(editor.moveAnnotation(at: 0, to: move).changed)
        let annotation = editor.project.overlays.annotations[0]
        XCTAssertEqual([annotation.x, annotation.y, annotation.offsetX, annotation.offsetY], [0.1, 0.2, 0.3, 0.4])
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

/// Whether what the geometry works out reaches the picture: the compositor's drawing, read back.
/// Places count from the top, as the format does, so a picture drawn upside down fails here.
final class StudioAnnotationDrawingTests: XCTestCase {
    private let size = CGSize(width: 1280, height: 720)
    private let compositor = StudioCompositor()
    private let context = CIContext(options: [.workingColorSpace: NSNull(), .outputColorSpace: NSNull()])
    private let red = [255, 0, 0]
    private let blue = [0, 0, 255]
    private let white = [255, 255, 255]
    private let black = [0, 0, 0]

    private var background: CIImage {
        CIImage(color: CIColor(red: 0, green: 0, blue: 0)).cropped(to: CGRect(origin: .zero, size: size))
    }

    private func pixels(_ image: CIImage, x: Int, y: Int, width: Int = 1) -> [[Int]] {
        var bytes = [UInt8](repeating: 0, count: width * 4)
        context.render(
            image,
            toBitmap: &bytes,
            rowBytes: width * 4,
            bounds: CGRect(x: x, y: Int(size.height) - 1 - y, width: width, height: 1),
            format: .RGBA8,
            colorSpace: nil
        )
        var colors: [[Int]] = []
        for column in 0..<width {
            let at = column * 4
            colors.append([Int(bytes[at]), Int(bytes[at + 1]), Int(bytes[at + 2])])
        }
        return colors
    }

    private func assertColor(_ image: CIImage, _ x: Int, _ y: Int, _ expected: [Int], _ what: String, line: UInt = #line) {
        let actual = pixels(image, x: x, y: y)[0]
        let isClose = zip(actual, expected).allSatisfy { abs($0 - $1) <= 3 }
        XCTAssertTrue(isClose, "\(what): at \(x), \(y) the color is \(actual), not \(expected)", line: line)
    }

    private func arrow(fill: String, from tail: StudioAnnotationPoint, to tip: StudioAnnotationPoint, headBase: StudioAnnotationPoint, opacity: Double = 1) -> StudioResolvedAnnotation {
        StudioResolvedAnnotation(
            index: 0,
            kind: StudioAnnotationKind.arrow,
            opacity: opacity,
            fill: fill,
            textColor: "#FFFFFF",
            anchor: tip,
            stroke: 12,
            tail: tail,
            headBase: headBase,
            halfWidth: 30,
            hasShaft: true
        )
    }

    private var flatArrow: StudioResolvedAnnotation {
        arrow(
            fill: "#FF0000",
            from: StudioAnnotationPoint(x: 300, y: 100),
            to: StudioAnnotationPoint(x: 900, y: 100),
            headBase: StudioAnnotationPoint(x: 840, y: 100)
        )
    }

    func testEveryPresetIsItselfOnceSavedAndAColorKeepsItsHex() {
        for preset in annotationColorPresets {
            let saved = StudioAnnotationInspectorSection.color(StudioViewModel.hexString(for: preset.color))
            XCTAssertTrue(annotationColorsEqual(preset.color, saved), preset.name)
        }
        for hex in ["#FF453A", "#0A84FF", "#FFD60A", "#FFFFFF", "#1C1C1E", "#12AB9F", "#010203", "#FEFDFC"] {
            XCTAssertEqual(StudioViewModel.hexString(for: StudioAnnotationInspectorSection.color(hex)), hex)
        }
    }

    func testAnArrowIsDrawnWhereItsPointsAre() {
        let drawn = compositor.drawAnnotations([flatArrow], over: background, renderSize: size)

        assertColor(drawn, 600, 100, red, "the shaft")
        assertColor(drawn, 845, 80, red, "the head, wider than the shaft")
        assertColor(drawn, 600, 80, black, "beside the shaft")
        assertColor(drawn, 950, 100, black, "past the tip")
        assertColor(drawn, 600, 619, black, "where an arrow drawn upside down would be")
    }

    func testAFadedAnnotationLetsThroughWhatIsUnderIt() {
        var faded = flatArrow
        faded.opacity = 0.5
        let drawn = compositor.drawAnnotations([faded], over: background, renderSize: size)

        assertColor(drawn, 600, 100, [128, 0, 0], "half of the red over black")
    }

    func testTheLastInTheListIsOnTop() {
        let across = arrow(
            fill: "#0000FF",
            from: StudioAnnotationPoint(x: 600, y: 20),
            to: StudioAnnotationPoint(x: 600, y: 300),
            headBase: StudioAnnotationPoint(x: 600, y: 240)
        )
        let blueOnTop = compositor.drawAnnotations([flatArrow, across], over: background, renderSize: size)
        let redOnTop = compositor.drawAnnotations([across, flatArrow], over: background, renderSize: size)

        assertColor(blueOnTop, 600, 100, blue, "the last of red, blue")
        assertColor(redOnTop, 600, 100, red, "the last of blue, red")
    }

    func testACalloutIsABoxWithItsTextAndAPointerToWhatItPointsAt() {
        let callout = StudioResolvedAnnotation(
            index: 0,
            kind: StudioAnnotationKind.callout,
            opacity: 1,
            fill: "#FFFFFF",
            textColor: "#000000",
            anchor: StudioAnnotationPoint(x: 900, y: 200),
            boxCenter: StudioAnnotationPoint(x: 300, y: 200),
            font: 40,
            maxTextWidth: 400,
            radius: 8,
            text: "MMMM"
        )
        let drawn = compositor.drawAnnotations([callout], over: background, renderSize: size)
        let row = pixels(drawn, x: 100, y: 200, width: 400)

        XCTAssertTrue(row.contains { $0.allSatisfy { $0 > 250 } }, "the box's fill is on the row through its middle")
        XCTAssertTrue(row.contains { $0.allSatisfy { $0 < 60 } }, "the text's ink is on the row through its middle")
        assertColor(drawn, 800, 200, white, "the pointer, on its way to the anchor")
        assertColor(drawn, 920, 200, black, "past the anchor")
        assertColor(drawn, 300, 80, black, "above the box")
        assertColor(drawn, 800, 519, black, "where a pointer drawn upside down would be")
    }

    func testATextHasABoxAndNoPointer() {
        let text = StudioResolvedAnnotation(
            index: 0,
            kind: StudioAnnotationKind.text,
            opacity: 1,
            fill: "#FFFFFF",
            textColor: "#000000",
            anchor: StudioAnnotationPoint(x: 640, y: 500),
            boxCenter: StudioAnnotationPoint(x: 640, y: 500),
            font: 40,
            maxTextWidth: 400,
            radius: 8,
            text: "MMMM"
        )
        let drawn = compositor.drawAnnotations([text], over: background, renderSize: size)
        let row = pixels(drawn, x: 440, y: 500, width: 400)

        XCTAssertTrue(row.contains { $0.allSatisfy { $0 > 250 } }, "the box's fill is on the row through its middle")
        XCTAssertTrue(row.contains { $0.allSatisfy { $0 < 60 } }, "the text's ink is on the row through its middle")
        assertColor(drawn, 1000, 500, black, "beside the box")
    }
}
