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
            if item.kind == StudioAnnotationKind.emoji {
                near(item.font, expected.font ?? .nan, "\(name) font")
                let textRect = StudioAnnotationGeometry.layoutEmoji(
                    item,
                    textWidth: testCase.textSize.width,
                    textHeight: testCase.textSize.height,
                    canvasWidth: width,
                    canvasHeight: height
                )
                near(textRect, expected.textRect, "\(name) textRect")
                XCTAssertEqual(item.shadow, expected.shadow, "\(name) shadow")
                continue
            }
            if item.isArea {
                near(item.rect, expected.rect, "\(name) rect")
                if item.kind == StudioAnnotationKind.redact {
                    XCTAssertEqual(item.redactStyle.rawValue, expected.redactStyle, "\(name) redactStyle")
                    near(item.redactAmount, expected.redactAmount ?? .nan, "\(name) redactAmount")
                } else {
                    near(item.stroke, expected.stroke ?? .nan, "\(name) stroke")
                    near(item.radius, expected.radius ?? .nan, "\(name) radius")
                    XCTAssertEqual(item.shapeFill, expected.shapeFill, "\(name) shapeFill")
                    near(item.shapeFillOpacity, expected.shapeFillOpacity ?? .nan, "\(name) shapeFillOpacity")
                }
                continue
            }
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

    // MARK: Areas

    func testAreaPropertiesRoundTripAndTakeTheirDefaults() throws {
        let project = try project(overlays: #"""
        { "annotations": [
          { "kind": "rectangle", "width": 0.4, "height": 0.3, "cornerRadius": 0.05, "shapeFill": "#112233", "shapeFillOpacity": 0.6 },
          { "kind": "redact", "redactStyle": "pixelate", "redactLevel": "heavy" },
          { "kind": "redact", "redactStyle": "smudge", "redactLevel": "loud" },
          { "kind": "ellipse" }
        ] }
        """#)
        let list = project.overlays.annotations
        XCTAssertEqual([list[0].width, list[0].height, list[0].cornerRadius, list[0].shapeFillOpacity], [0.4, 0.3, 0.05, 0.6])
        XCTAssertEqual(list[0].shapeFill, "#112233")
        XCTAssertEqual(list[1].redactStyle, .pixelate)
        XCTAssertEqual(list[1].redactLevel, .heavy)
        XCTAssertEqual(list[2].redactStyle, .blur, "An unknown style reads as the default")
        XCTAssertEqual(list[2].redactLevel, .medium, "An unknown level reads as the default")
        XCTAssertEqual([list[3].width, list[3].height, list[3].cornerRadius, list[3].shapeFillOpacity], [0.2, 0.15, 0, 0.35])
        XCTAssertEqual(list[3].shapeFill, "")
        XCTAssertTrue(list.allSatisfy { $0.extra.isEmpty }, "The new properties are not kept as unknown ones")

        let again = try StudioJSON.makeDecoder().decode(StudioProject.self, from: StudioJSON.makeEncoder().encode(project))
        XCTAssertEqual(again.overlays.annotations, list)
    }

    func testAddingAnAreaFillsAShareOfWhatIsInViewAndUndoes() {
        let zoom = StudioZoom(start: 5, end: 15, scale: 2, focus: StudioZoomFocus(x: 0.25, y: 0.25), easeIn: 0, easeOut: 0)
        var editor = model(zooms: [zoom])
        for kind in [StudioAnnotationKind.rectangle, StudioAnnotationKind.ellipse, StudioAnnotationKind.redact] {
            XCTAssertTrue(editor.addAnnotation(kind: kind, at: 1, events: nil).changed, kind)
        }
        XCTAssertTrue(editor.addAnnotation(kind: StudioAnnotationKind.rectangle, at: 10, events: nil).changed)

        let list = editor.project.overlays.annotations
        XCTAssertEqual(list.map(\.kind), [StudioAnnotationKind.rectangle, StudioAnnotationKind.ellipse, StudioAnnotationKind.redact, StudioAnnotationKind.rectangle])
        for annotation in list {
            XCTAssertEqual(annotation.attach, .screen)
            XCTAssertEqual(annotation.text, "")
            XCTAssertEqual(annotation.shapeFill, "")
            XCTAssertEqual(annotation.cornerRadius, 0)
        }
        XCTAssertEqual(list[0].style, .red)
        XCTAssertEqual(list[1].style, .red)
        XCTAssertEqual(list[2].style, .dark)
        XCTAssertEqual(list[2].redactStyle, .blur)
        XCTAssertEqual(list[2].redactLevel, .medium)
        XCTAssertEqual(list[0].start, 1)
        XCTAssertEqual(list[0].end, 4)
        XCTAssertEqual(list[0].width, 0.2, accuracy: 1e-9)
        XCTAssertEqual(list[0].height, 0.15, accuracy: 1e-9)
        XCTAssertEqual(list[3].x, 0.25, accuracy: 1e-9, "In the middle of the zoom")
        XCTAssertEqual(list[3].width, 0.1, accuracy: 1e-9, "A fifth of the half that the zoom shows")
        XCTAssertEqual(list[3].height, 0.075, accuracy: 1e-9)

        for count in [3, 2, 1, 0] {
            editor.undo()
            XCTAssertEqual(editor.project.overlays.annotations.count, count)
        }
    }

    func testTheAreaEditsStayInRangeAndAreUndone() {
        var editor = model([StudioAnnotation(id: "a", kind: StudioAnnotationKind.rectangle, start: 0, end: 5)])
        XCTAssertTrue(editor.setAnnotationSize(at: 0, width: 3, height: 0).changed)
        XCTAssertEqual(editor.project.overlays.annotations[0].width, 1)
        XCTAssertEqual(editor.project.overlays.annotations[0].height, StudioEditorModel.minimumAreaSize)
        XCTAssertTrue(editor.setAnnotationCornerRadius(at: 0, to: 2).changed)
        XCTAssertEqual(editor.project.overlays.annotations[0].cornerRadius, 0.5)
        XCTAssertTrue(editor.setAnnotationShapeFillOpacity(at: 0, to: -1).changed)
        XCTAssertEqual(editor.project.overlays.annotations[0].shapeFillOpacity, 0)

        // The fill's colors are one undo step, as the other colors are, and taking it away another.
        XCTAssertTrue(editor.setAnnotationShapeFill(at: 0, to: "#111111").changed)
        XCTAssertTrue(editor.setAnnotationShapeFill(at: 0, to: "#222222").changed)
        XCTAssertFalse(editor.setAnnotationShapeFill(at: 0, to: "#222222").changed)
        XCTAssertTrue(editor.setAnnotationShapeFill(at: 0, to: "").changed)
        XCTAssertEqual(editor.project.overlays.annotations[0].shapeFill, "")
        editor.undo()
        XCTAssertEqual(editor.project.overlays.annotations[0].shapeFill, "#222222")
        editor.undo()
        XCTAssertEqual(editor.project.overlays.annotations[0].shapeFill, "")

        var redaction = model([StudioAnnotation(id: "b", kind: StudioAnnotationKind.redact, start: 0, end: 5)])
        XCTAssertTrue(redaction.setAnnotationRedactStyle(at: 0, to: .solid).changed)
        XCTAssertTrue(redaction.setAnnotationRedactLevel(at: 0, to: .light).changed)
        redaction.undo()
        XCTAssertEqual(redaction.project.overlays.annotations[0].redactLevel, .medium)
        XCTAssertEqual(redaction.project.overlays.annotations[0].redactStyle, .solid)
    }

    func testDraggingAnAreaMovesItAndACornerResizesIt() throws {
        let area = StudioAnnotation(kind: StudioAnnotationKind.rectangle, attach: .frame, x: 0.3, y: 0.3, width: 0.2, height: 0.2)
        let whole = try XCTUnwrap(moved(area, .area, 100, 50))
        XCTAssertEqual(whole.x, 0.4, accuracy: 1e-9)
        XCTAssertEqual(whole.y, 0.4, accuracy: 1e-9)
        XCTAssertEqual(whole.width ?? .nan, 0.2, accuracy: 1e-9)

        // The bottom right corner from (400, 200) to (500, 300): the top left stays at (200, 100).
        let corner = try XCTUnwrap(moved(area, .bottomRight, 100, 100))
        XCTAssertEqual(corner.width ?? .nan, 0.3, accuracy: 1e-9)
        XCTAssertEqual(corner.height ?? .nan, 0.4, accuracy: 1e-9)
        XCTAssertEqual(corner.x, 0.35, accuracy: 1e-9)
        XCTAssertEqual(corner.y, 0.4, accuracy: 1e-9)

        // Dragged past the opposite corner, it keeps the smallest size.
        let squashed = try XCTUnwrap(moved(area, .topLeft, 1000, 1000))
        XCTAssertEqual(squashed.width ?? .nan, 0.01, accuracy: 1e-9, "Two hundredths of the short side of 500")
        XCTAssertEqual(squashed.x + (squashed.width ?? 0) / 2, 0.4, accuracy: 1e-9, "The right edge stays")

        var editor = model([StudioAnnotation(id: "a", kind: StudioAnnotationKind.rectangle, start: 0, end: 5)])
        XCTAssertTrue(editor.moveAnnotation(at: 0, to: corner).changed)
        XCTAssertEqual(editor.project.overlays.annotations[0].width, corner.width)
    }

    func testAScreenAreaIsDrawnWhileAnyOfItIsInView() throws {
        let frame = wholeCanvas(source: StudioRect(x: 0.25, y: 0.25, width: 0.5, height: 0.5))
        func resolved(_ x: Double) -> StudioResolvedAnnotation? {
            StudioAnnotationGeometry.resolve(
                StudioAnnotation(kind: StudioAnnotationKind.ellipse, start: 0, end: 5, x: x, y: 0.5, width: 0.1, height: 0.1),
                index: 0,
                frame: frame,
                time: 1,
                canvasWidth: 1000,
                canvasHeight: 500
            )
        }
        let inside = try XCTUnwrap(resolved(0.5))
        near(inside.rect, StudioRect(x: 400, y: 200, width: 200, height: 100), "rect")
        XCTAssertNotNil(resolved(0.21), "Its right edge is in view")
        XCTAssertNil(resolved(0.2), "Its right edge only touches the view")
    }

    func testChangingWhatAnAreaIsFixedToKeepsItWhereItIsInThePicture() {
        let zoom = StudioZoom(start: 5, end: 15, scale: 2, focus: StudioZoomFocus(x: 0.5, y: 0.5), easeIn: 0, easeOut: 0)
        var editor = model([StudioAnnotation(kind: StudioAnnotationKind.rectangle, end: 20, x: 0.5, y: 0.5, width: 0.2, height: 0.1)], zooms: [zoom])

        _ = editor.setAnnotationAttach(at: 0, to: .frame, time: 10, events: nil)
        var annotation = editor.project.overlays.annotations[0]
        XCTAssertEqual(annotation.attach, .frame)
        XCTAssertEqual(annotation.width, 0.4, accuracy: 1e-9, "Twice as wide in a zoom of two")
        XCTAssertEqual(annotation.height, 0.2, accuracy: 1e-9)
        _ = editor.setAnnotationAttach(at: 0, to: .screen, time: 10, events: nil)
        annotation = editor.project.overlays.annotations[0]
        XCTAssertEqual(annotation.width, 0.2, accuracy: 1e-9)
        XCTAssertEqual(annotation.x, 0.5, accuracy: 1e-9)
    }

    func testTheWordsNameTheAreas() {
        XCTAssertEqual(StudioEditorModel.annotationLabel(StudioAnnotation(kind: StudioAnnotationKind.rectangle, text: "ignored")), "Rectangle")
        XCTAssertEqual(StudioEditorModel.annotationLabel(StudioAnnotation(kind: StudioAnnotationKind.ellipse)), "Ellipse")
        XCTAssertEqual(StudioEditorModel.annotationLabel(StudioAnnotation(kind: StudioAnnotationKind.redact)), "Redaction")
    }

    // MARK: Emoji and duplicates

    func testAnEmojisShadowIsOnlyTrueAndRoundTripsWithWhatItDoesNotKnow() throws {
        let project = try project(overlays: #"""
        { "annotations": [
          { "id": "a", "kind": "emoji", "text": "🎉", "shadow": true, "size": "large", "later": [1, 2] },
          { "kind": "emoji", "text": "🔥", "shadow": "yes" },
          { "kind": "emoji", "text": "👀", "shadow": 1 },
          { "kind": "emoji", "text": "🚀" }
        ] }
        """#)
        let list = project.overlays.annotations
        XCTAssertEqual(list.map(\.shadow), [true, false, false, false])
        XCTAssertEqual(list[0].text, "🎉")
        XCTAssertEqual(list[0].size, .large)
        XCTAssertEqual(list[0].extra["later"], .array([.number(1), .number(2)]))
        XCTAssertNil(list[1].extra["shadow"], "A shadow that is not true is not kept as an unknown property")

        let again = try StudioJSON.makeDecoder().decode(StudioProject.self, from: StudioJSON.makeEncoder().encode(project))
        XCTAssertEqual(again.overlays.annotations, list)
    }

    func testAddingAnEmojiPutsAThumbsUpOnTheScreenForThreeSeconds() {
        var editor = model()
        XCTAssertTrue(editor.addAnnotation(kind: StudioAnnotationKind.emoji, at: 2, events: nil).changed)

        let emoji = editor.project.overlays.annotations[0]
        XCTAssertEqual(emoji.kind, StudioAnnotationKind.emoji)
        XCTAssertEqual(emoji.text, "👍")
        XCTAssertEqual(emoji.size, .medium)
        XCTAssertFalse(emoji.shadow)
        XCTAssertEqual(emoji.attach, .screen)
        XCTAssertEqual(emoji.start, 2)
        XCTAssertEqual(emoji.end, 5)
        XCTAssertEqual(emoji.x, 0.5, accuracy: 1e-9)
        XCTAssertEqual(emoji.y, 0.5, accuracy: 1e-9)
        editor.undo()
        XCTAssertTrue(editor.project.overlays.annotations.isEmpty)
    }

    func testPickingAnEmojiAndItsShadowAreEachOneUndoStep() {
        var editor = model([StudioAnnotation(id: "a", kind: StudioAnnotationKind.emoji, start: 0, end: 5, text: "👍")])
        XCTAssertTrue(editor.setAnnotationEmoji(at: 0, to: "🔥").changed)
        XCTAssertTrue(editor.setAnnotationEmoji(at: 0, to: "🚀").changed)
        XCTAssertTrue(editor.setAnnotationShadow(at: 0, to: true).changed)
        XCTAssertTrue(editor.project.overlays.annotations[0].shadow)
        editor.undo()
        XCTAssertFalse(editor.project.overlays.annotations[0].shadow)
        XCTAssertEqual(editor.project.overlays.annotations[0].text, "🚀")
        editor.undo()
        XCTAssertEqual(editor.project.overlays.annotations[0].text, "🔥")
    }

    func testTheEmojiChoicesAreTheTwentyFourWithTheirNames() {
        let choices = StudioEditorModel.emojiChoices
        XCTAssertEqual(choices.count, 24)
        XCTAssertEqual(Set(choices.map(\.emoji)).count, 24, "Each is offered once")
        XCTAssertEqual(choices.first?.emoji, "👍")
        XCTAssertEqual(choices.first?.name, "thumbs up")
        XCTAssertTrue(choices.allSatisfy { !$0.name.isEmpty })
    }

    func testADuplicateIsAboveTheOriginalWithItsOwnIdAndNudged() {
        let original = StudioAnnotation(id: "a", kind: StudioAnnotationKind.callout, attach: .frame, start: 1, end: 4, x: 0.3, y: 0.99, text: "Hi", style: .blue)
        let other = StudioAnnotation(id: "b", kind: StudioAnnotationKind.arrow, start: 0, end: 2)
        var editor = model([original, other])

        let result = editor.duplicateAnnotation(at: 0)
        XCTAssertTrue(result.changed)
        XCTAssertEqual(result.index, 1)
        let list = editor.project.overlays.annotations
        XCTAssertEqual(list.count, 3)
        XCTAssertEqual(list[0], original)
        XCTAssertEqual(list[2].id, "b")
        let copy = list[1]
        XCTAssertNotEqual(copy.id, "a")
        XCTAssertFalse(copy.id.isEmpty)
        XCTAssertEqual(copy.start, 1)
        XCTAssertEqual(copy.end, 4)
        XCTAssertEqual(copy.x, 0.32, accuracy: 1e-9)
        XCTAssertEqual(copy.y, 1, accuracy: 1e-9, "Kept inside what it is fixed to")
        XCTAssertEqual(copy.text, "Hi")
        XCTAssertEqual(copy.style, .blue)
        XCTAssertEqual(copy.attach, .frame)

        let second = editor.duplicateAnnotation(at: 0)
        XCTAssertEqual(second.index, 1)
        XCTAssertNotEqual(editor.project.overlays.annotations[1].id, copy.id, "Every copy has an id of its own")
        editor.undo()
        editor.undo()
        XCTAssertEqual(editor.project.overlays.annotations, [original, other])
        XCTAssertFalse(editor.duplicateAnnotation(at: 5).changed)
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
            var rect: StudioRect?
            var shapeFill: String?
            var shapeFillOpacity: Double?
            var redactStyle: String?
            var redactAmount: Double?
            var shadow: Bool?
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

    @MainActor
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

    private func emoji(_ text: String, shadow: Bool = false) -> StudioResolvedAnnotation {
        var emoji = StudioResolvedAnnotation(
            index: 0,
            kind: StudioAnnotationKind.emoji,
            opacity: 1,
            fill: "#FFFFFF",
            textColor: "#FFFFFF",
            anchor: StudioAnnotationPoint(x: 400, y: 300)
        )
        emoji.boxCenter = emoji.anchor
        emoji.font = 120
        emoji.text = text
        emoji.shadow = shadow
        return emoji
    }

    func testAnEmojiIsDrawnInItsOwnColors() {
        let drawn = compositor.drawAnnotations([emoji("🔥")], over: background, renderSize: size)
        var colored = 0
        for y in stride(from: 240, through: 360, by: 6) {
            colored += pixels(drawn, x: 300, y: y, width: 200).filter { ($0.max() ?? 0) - ($0.min() ?? 0) > 80 }.count
        }

        XCTAssertGreaterThan(colored, 20, "A fire in color, not in one color of gray")
        assertColor(drawn, 400, 619, black, "where an emoji drawn upside down would be")
        assertColor(drawn, 700, 300, black, "beside it")
    }

    func testAnEmojisShadowFallsUnderIt() {
        let paper = CIImage(color: CIColor(red: 1, green: 1, blue: 1)).cropped(to: CGRect(origin: .zero, size: size))
        let plain = compositor.drawAnnotations([emoji("🟥")], over: paper, renderSize: size)
        let shaded = compositor.drawAnnotations([emoji("🟥", shadow: true)], over: paper, renderSize: size)
        var darkerBelow = 0
        var darkerAbove = 0
        for y in 230...400 {
            let before = pixels(plain, x: 400, y: y)[0].reduce(0, +)
            let after = pixels(shaded, x: 400, y: y)[0].reduce(0, +)
            if after < before - 30 {
                if y > 300 { darkerBelow += 1 } else { darkerAbove += 1 }
            }
        }

        XCTAssertGreaterThan(darkerBelow, 0, "The shadow shows under the emoji")
        XCTAssertGreaterThan(darkerBelow, darkerAbove, "More of the shadow is below than above")
    }

    private func area(_ kind: String, fill: String, rect: StudioRect) -> StudioResolvedAnnotation {
        var area = StudioResolvedAnnotation(
            index: 0,
            kind: kind,
            opacity: 1,
            fill: fill,
            textColor: "#FFFFFF",
            anchor: StudioAnnotationPoint(x: rect.x + rect.width / 2, y: rect.y + rect.height / 2)
        )
        area.rect = rect
        return area
    }

    /// White on the left half and black on the right.
    private var halves: CIImage {
        CIImage(color: CIColor(red: 1, green: 1, blue: 1))
            .cropped(to: CGRect(x: 0, y: 0, width: size.width / 2, height: size.height))
            .composited(over: background)
    }

    func testARectangleHasItsOutlineOverItsInsideFill() {
        var rectangle = area(StudioAnnotationKind.rectangle, fill: "#FF0000", rect: StudioRect(x: 100, y: 100, width: 200, height: 100))
        rectangle.stroke = 10
        rectangle.shapeFill = "#0000FF"
        rectangle.shapeFillOpacity = 1
        let drawn = compositor.drawAnnotations([rectangle], over: background, renderSize: size)

        assertColor(drawn, 100, 150, red, "the outline, on the edge")
        assertColor(drawn, 200, 150, blue, "the inside fill")
        assertColor(drawn, 90, 150, black, "past the outline")
        assertColor(drawn, 200, 619, black, "where a rectangle drawn upside down would be")
    }

    func testABlurRedactionChangesOnlyWhatIsInsideIt() {
        var redaction = area(StudioAnnotationKind.redact, fill: "#1C1C1E", rect: StudioRect(x: 540, y: 200, width: 200, height: 200))
        redaction.redactStyle = .blur
        redaction.redactAmount = 20
        let drawn = compositor.drawAnnotations([redaction], over: halves, renderSize: size)

        let middle = pixels(drawn, x: 638, y: 300)[0]
        XCTAssertTrue(middle.allSatisfy { $0 > 20 && $0 < 235 }, "white and black are mixed where they meet inside it: \(middle)")
        assertColor(drawn, 539, 300, white, "just left of it")
        assertColor(drawn, 741, 300, black, "just right of it")
        assertColor(drawn, 638, 190, white, "just over it")
        assertColor(drawn, 642, 410, black, "just under it")
    }

    func testASolidRedactionCoversItsAreaInItsColor() {
        var redaction = area(StudioAnnotationKind.redact, fill: "#0000FF", rect: StudioRect(x: 540, y: 200, width: 200, height: 200))
        redaction.redactStyle = .solid
        let drawn = compositor.drawAnnotations([redaction], over: halves, renderSize: size)

        assertColor(drawn, 560, 300, blue, "inside, over white")
        assertColor(drawn, 720, 300, blue, "inside, over black")
        assertColor(drawn, 530, 300, white, "beside it")
    }

    func testARedactionHidesTheAnnotationsUnderItAndNotThoseOverIt() {
        // The shaft runs from 94 to 106, away from the middle of the cell it is in.
        var redaction = area(StudioAnnotationKind.redact, fill: "#1C1C1E", rect: StudioRect(x: 500, y: 60, width: 200, height: 100))
        redaction.redactStyle = .pixelate
        redaction.redactAmount = 100
        let under = compositor.drawAnnotations([flatArrow, redaction], over: background, renderSize: size)
        let over = compositor.drawAnnotations([redaction, flatArrow], over: background, renderSize: size)

        let hidden = pixels(under, x: 600, y: 100)[0]
        XCTAssertFalse(zip(hidden, red).allSatisfy { abs($0 - $1) <= 3 }, "the shaft under it is not left as it was: \(hidden)")
        assertColor(over, 600, 100, red, "the shaft over it")
        assertColor(under, 400, 100, red, "the shaft beside it")
    }
}
