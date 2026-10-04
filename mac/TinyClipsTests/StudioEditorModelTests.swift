import XCTest
@testable import TinyClips

final class StudioEditorModelTests: XCTestCase {
    // MARK: - Undo

    func testGestureBecomesOneUndoStep() {
        var model = StudioEditorModel(project: makeProject())

        model.beginEditingGroup()
        model.setCanvasPadding(0.12)
        model.setCanvasPadding(0.20)
        model.setCanvasPadding(0.50)
        XCTAssertTrue(model.isGroupingEdits)
        XCTAssertFalse(model.canUndo)
        XCTAssertEqual(model.project.canvas.padding, 0.4, accuracy: 1e-9)

        model.commitEditingGroup()
        XCTAssertFalse(model.isGroupingEdits)
        XCTAssertTrue(model.canUndo)

        model.undo()
        XCTAssertEqual(model.project.canvas.padding, 0.06, accuracy: 1e-9)
        XCTAssertFalse(model.canUndo)
        XCTAssertTrue(model.canRedo)

        model.redo()
        XCTAssertEqual(model.project.canvas.padding, 0.4, accuracy: 1e-9)
        XCTAssertTrue(model.canUndo)
        XCTAssertFalse(model.canRedo)
    }

    func testGestureThatChangesNothingLeavesNoUndoStep() {
        var model = StudioEditorModel(project: makeProject())

        model.beginEditingGroup()
        model.setCanvasPadding(0.3)
        model.setCanvasPadding(0.06)
        model.commitEditingGroup()

        XCTAssertFalse(model.canUndo)
    }

    func testCancelledGestureRestoresTheProject() {
        var model = StudioEditorModel(project: makeProject())
        let before = model.project

        model.beginEditingGroup()
        model.setScreenShadow(1)
        model.setLayout(.camera)
        model.cancelEditingGroup()

        XCTAssertEqual(model.project, before)
        XCTAssertFalse(model.canUndo)
    }

    func testNewEditClearsRedoAndUndoDepthIsCapped() {
        var model = StudioEditorModel(project: makeProject())
        model.setScreenShadow(0.1)
        model.setScreenShadow(0.2)
        model.undo()
        XCTAssertTrue(model.canRedo)
        model.setScreenShadow(0.9)
        XCTAssertFalse(model.canRedo)

        for index in 0..<(StudioEditorModel.maximumUndoDepth + 20) {
            model.setCanvasPadding(index.isMultiple(of: 2) ? 0.1 : 0.2)
        }
        var undone = 0
        while model.canUndo {
            model.undo()
            undone += 1
        }
        XCTAssertEqual(undone, StudioEditorModel.maximumUndoDepth)
    }

    func testUndoNeverTouchesBookkeeping() {
        var model = StudioEditorModel(project: makeProject())
        model.setLayout(.sideBySide)

        var saved = model.project
        saved.exports = [StudioExport(path: "/tmp/out.mp4")]
        saved.name = "Renamed"
        saved.keepSources = true
        model.refreshBookkeeping(from: saved)

        model.undo()

        XCTAssertEqual(model.project.scenes[0].layout, .bubble)
        XCTAssertEqual(model.project.exports.map(\.path), ["/tmp/out.mp4"])
        XCTAssertEqual(model.project.name, "Renamed")
        XCTAssertTrue(model.project.keepSources)
    }

    // MARK: - Edits

    func testStyleEditsClampToTheFormatRanges() {
        var model = StudioEditorModel(project: makeProject())

        model.setCanvasPadding(-1)
        model.setScreenCornerRadius(3)
        model.setScreenShadow(-1)
        model.setCameraBubbleSize(2)
        model.setCameraCornerRadius(9)
        model.setCameraBorderWidth(1)
        model.setCameraShadow(2)
        model.setSideBySide(cameraSide: .leading, fraction: 0.9)
        model.setCameraBubbleOffsets(x: 5, y: -.infinity)

        XCTAssertEqual(model.project.canvas.padding, 0, accuracy: 1e-9)
        XCTAssertEqual(model.project.screen.cornerRadius, 0.2, accuracy: 1e-9)
        XCTAssertEqual(model.project.screen.shadow, 0, accuracy: 1e-9)
        XCTAssertEqual(model.project.scenes[0].bubble.size, 0.6, accuracy: 1e-9)
        XCTAssertEqual(model.project.camera.cornerRadius, 0.5, accuracy: 1e-9)
        XCTAssertEqual(model.project.camera.borderWidth, 0.02, accuracy: 1e-9)
        XCTAssertEqual(model.project.camera.shadow, 1, accuracy: 1e-9)
        XCTAssertEqual(model.project.scenes[0].split.cameraFraction, 0.6, accuracy: 1e-9)
        XCTAssertEqual(model.project.scenes[0].split.cameraSide, .leading)
        XCTAssertEqual(model.project.scenes[0].bubble.offsetX, 1, accuracy: 1e-9)
        XCTAssertEqual(model.project.scenes[0].bubble.offsetY, 0, accuracy: 1e-9)
    }

    func testColorsAreNormalizedAndBadColorsAreIgnored() {
        var model = StudioEditorModel(project: makeProject())

        model.setCameraBorderColor(" #aabbccdd ")
        XCTAssertEqual(model.project.camera.borderColor, "#AABBCC")
        model.setCameraBorderColor("teal")
        XCTAssertEqual(model.project.camera.borderColor, "#AABBCC")

        model.setBackground(style: .gradient, preset: "candy", primary: "ff6bad", secondary: "#8cc7ff")
        XCTAssertEqual(model.project.canvas.background.style, .gradient)
        XCTAssertEqual(model.project.canvas.background.preset, "candy")
        XCTAssertEqual(model.project.canvas.background.primary, "#FF6BAD")
        XCTAssertEqual(model.project.canvas.background.secondary, "#8CC7FF")

        model.setBackground(style: .solid, preset: nil, primary: "nope", secondary: nil, image: "../secret.png")
        XCTAssertEqual(model.project.canvas.background.style, .solid)
        XCTAssertNil(model.project.canvas.background.preset)
        XCTAssertEqual(model.project.canvas.background.primary, "#FF6BAD")
        XCTAssertNil(model.project.canvas.background.secondary)
        XCTAssertNil(model.project.canvas.background.image)
    }

    func testLayoutChoicesAreIgnoredWithoutACamera() {
        var model = StudioEditorModel(project: makeProject(camera: false))
        XCTAssertFalse(model.hasCamera)
        XCTAssertEqual(model.effectiveLayout, .screen)

        model.setLayout(.sideBySide)
        XCTAssertFalse(model.canUndo)
        XCTAssertEqual(model.effectiveLayout, .screen)

        // The stored layout can be one that needs a camera. Choosing Screen then changes nothing
        // that is drawn, so it is not an edit and not an undo step.
        var project = makeProject(camera: false)
        project.scenes = [StudioScene(start: 0, layout: .bubble)]
        var storedBubble = StudioEditorModel(project: project)
        storedBubble.setLayout(.screen)
        XCTAssertFalse(storedBubble.canUndo)
        XCTAssertEqual(storedBubble.project.scenes[0].layout, .bubble)

        var withCamera = StudioEditorModel(project: makeProject())
        withCamera.setLayout(.camera)
        XCTAssertEqual(withCamera.effectiveLayout, .camera)
    }

    func testOpeningAProjectKeepsLaterScenesAndStoredLayout() {
        var project = makeProject(camera: false)
        project.scenes = [
            StudioScene(start: 0, layout: .bubble),
            StudioScene(start: 4, layout: .camera)
        ]
        var model = StudioEditorModel(project: project)
        model.setCanvasPadding(0.2)

        XCTAssertEqual(model.project.scenes.count, 2)
        XCTAssertEqual(model.project.scenes[0].layout, .bubble)
        XCTAssertEqual(model.project.scenes[1].layout, .camera)

        var empty = makeProject()
        empty.scenes = []
        XCTAssertEqual(StudioEditorModel(project: empty).project.scenes.count, 1)
    }

    func testSettingAnAnchorSnapsTheBubbleBackToTheCorner() {
        var model = StudioEditorModel(project: makeProject())
        model.moveBubble(topLeft: CGPoint(x: 300, y: 200), canvasSize: CGSize(width: 1000, height: 800))
        XCTAssertNotEqual(model.project.scenes[0].bubble.offsetX, 0)

        model.setCameraAnchor(.topRight)

        XCTAssertEqual(model.project.scenes[0].bubble.anchor, .topRight)
        XCTAssertEqual(model.project.scenes[0].bubble.offsetX, 0)
        XCTAssertEqual(model.project.scenes[0].bubble.offsetY, 0)
    }

    // MARK: - Looks

    func testLooksNeverCarryCrops() {
        var project = makeProject()
        project.screen.crop = StudioRect(x: 0.1, y: 0.2, width: 0.5, height: 0.5)
        project.camera.crop = StudioRect(x: 0.2, y: 0.1, width: 0.4, height: 0.4)
        var model = StudioEditorModel(project: project)

        var look = model.currentLook
        XCTAssertNil(look.screen.crop)
        XCTAssertNil(look.camera.crop)
        look.screen.crop = StudioRect(x: 0, y: 0, width: 0.1, height: 0.1)
        look.camera.crop = StudioRect(x: 0, y: 0, width: 0.1, height: 0.1)
        look.canvas.padding = 0.25
        look.screen.cornerRadius = 0.1
        look.camera.shape = .squircle
        model.applyLook(look)

        XCTAssertEqual(model.project.canvas.padding, 0.25, accuracy: 1e-9)
        XCTAssertEqual(model.project.screen.cornerRadius, 0.1, accuracy: 1e-9)
        XCTAssertEqual(model.project.camera.shape, .squircle)
        XCTAssertEqual(model.project.screen.crop, project.screen.crop)
        XCTAssertEqual(model.project.camera.crop, project.camera.crop)

        model.undo()
        XCTAssertEqual(model.project.canvas.padding, 0.06, accuracy: 1e-9)
    }

    // MARK: - Trim and Time

    func testTrimStaysInsideTheRecordingAndKeepsAMinimumLength() {
        var model = StudioEditorModel(project: makeProject(duration: 10))

        model.setTrim(start: 9.95, end: 20)
        XCTAssertEqual(model.project.edits.trimStart, 9.9, accuracy: 1e-9)
        XCTAssertNil(model.project.edits.trimEnd)
        XCTAssertEqual(model.outputDuration, 0.1, accuracy: 1e-9)

        model.setTrim(start: -3, end: 4)
        XCTAssertEqual(model.trimStart, 0, accuracy: 1e-9)
        XCTAssertEqual(model.trimEnd, 4, accuracy: 1e-9)

        model.setTrimStart(8)
        XCTAssertEqual(model.trimStart, 3.9, accuracy: 1e-9)
        XCTAssertEqual(model.trimEnd, 4, accuracy: 1e-9)

        model.setTrimStart(1)
        model.setTrimEnd(0.2)
        XCTAssertEqual(model.trimStart, 1, accuracy: 1e-9)
        XCTAssertEqual(model.trimEnd, 1.1, accuracy: 1e-9)

        model.setTrimEnd(.nan)
        XCTAssertEqual(model.trimStart, 1, accuracy: 1e-9)
        XCTAssertGreaterThanOrEqual(model.trimEnd, 1.1 - 1e-9)

        model.setTrimEnd(10)
        XCTAssertNil(model.project.edits.trimEnd)
        XCTAssertEqual(model.outputDuration, 9, accuracy: 1e-9)
    }

    func testVeryShortAndEmptyRecordings() {
        var short = StudioEditorModel(project: makeProject(duration: 0.04))
        short.setTrim(start: 0.03, end: 0.035)
        XCTAssertEqual(short.trimStart, 0, accuracy: 1e-9)
        XCTAssertEqual(short.trimEnd, 0.04, accuracy: 1e-9)

        let empty = StudioEditorModel(project: makeProject(duration: 0))
        XCTAssertEqual(empty.outputDuration, 0)
        XCTAssertEqual(empty.playbackStart(from: 3), 0)
    }

    func testOpeningClampsADamagedTrim() {
        var project = makeProject(duration: 10)
        project.edits = StudioEdits(trimStart: 50, trimEnd: -2)
        let model = StudioEditorModel(project: project)

        XCTAssertEqual(model.trimStart, 9.9, accuracy: 1e-9)
        XCTAssertEqual(model.trimEnd, 10, accuracy: 1e-9)
        XCTAssertFalse(model.canUndo)
    }

    func testTimeConversionsFollowTheTrim() {
        var model = StudioEditorModel(project: makeProject(duration: 10))
        model.setTrim(start: 2, end: 8)

        XCTAssertEqual(model.outputDuration, 6, accuracy: 1e-9)
        XCTAssertEqual(model.outputTime(forSourceTime: 5), 3, accuracy: 1e-9)
        XCTAssertEqual(model.outputTime(forSourceTime: 1), 0, accuracy: 1e-9)
        XCTAssertEqual(model.outputTime(forSourceTime: 9), 6, accuracy: 1e-9)
        XCTAssertEqual(model.sourceTime(forOutputTime: 1.5), 3.5, accuracy: 1e-9)
        XCTAssertEqual(model.playheadText(sourceTime: 4.5), "0:02.5 of 0:06.0")
        XCTAssertEqual(model.frameDuration, 1.0 / 30.0, accuracy: 1e-12)
    }

    func testPreviewPlaysTheWholeSourceWithSound() {
        var model = StudioEditorModel(project: makeProject(duration: 10))
        model.setTrim(start: 2, end: 8)
        model.setMuted(true)
        model.setCanvasPadding(0.2)

        let preview = model.previewProject

        XCTAssertEqual(preview.edits, StudioEdits())
        XCTAssertFalse(preview.audio.muted)
        XCTAssertEqual(preview.canvas.padding, 0.2, accuracy: 1e-9)
        XCTAssertEqual(StudioTimeMap(sourceDuration: 10, edits: preview.edits).outputDuration, 10, accuracy: 1e-9)
        XCTAssertTrue(model.project.audio.muted)
    }

    func testTransportStartsAndStopsAtTheTrim() {
        var model = StudioEditorModel(project: makeProject(duration: 10))
        model.setTrim(start: 2, end: 8)

        XCTAssertEqual(model.playbackStart(from: 5), 5, accuracy: 1e-9)
        XCTAssertEqual(model.playbackStart(from: 0.5), 2, accuracy: 1e-9)
        XCTAssertEqual(model.playbackStart(from: 8), 2, accuracy: 1e-9)
        XCTAssertEqual(model.playbackStart(from: 7.99), 2, accuracy: 1e-9)
        XCTAssertEqual(model.playbackStart(from: 9.5), 2, accuracy: 1e-9)
        XCTAssertEqual(model.playbackStart(from: .nan), 2, accuracy: 1e-9)

        XCTAssertFalse(model.isAtPlaybackEnd(7.9))
        XCTAssertTrue(model.isAtPlaybackEnd(8))
        XCTAssertTrue(model.isAtPlaybackEnd(8.2))

        XCTAssertEqual(model.clampedSourceTime(-1), 0)
        XCTAssertEqual(model.clampedSourceTime(99), 10)
    }

    func testFormattedTime() {
        XCTAssertEqual(StudioEditorModel.formattedTime(0), "0:00.0")
        XCTAssertEqual(StudioEditorModel.formattedTime(62.39), "1:02.3")
        XCTAssertEqual(StudioEditorModel.formattedTime(599.99), "9:59.9")
        XCTAssertEqual(StudioEditorModel.formattedTime(-4), "0:00.0")
        XCTAssertEqual(StudioEditorModel.formattedTime(.nan), "0:00.0")
        XCTAssertEqual(StudioEditorModel.formattedTime(.infinity), "0:00.0")
    }

    // MARK: - Bubble Dragging

    func testDraggedBubbleLandsWhereItWasDroppedInEveryQuadrantAndShape() {
        let canvas = CGSize(width: 1000, height: 800)
        let drops: [(CGPoint, StudioAnchor)] = [
            (CGPoint(x: 80, y: 90), .topLeft),
            (CGPoint(x: 700, y: 60), .topRight),
            (CGPoint(x: 120, y: 520), .bottomLeft),
            (CGPoint(x: 640, y: 470), .bottomRight)
        ]

        for shape in [StudioCameraShape.circle, .roundedRectangle, .squircle, .rectangle] {
            for (topLeft, anchor) in drops {
                var model = StudioEditorModel(project: makeProject())
                model.setCameraShape(shape)
                model.moveBubble(topLeft: topLeft, canvasSize: canvas)

                let rect = resolvedCamera(model.project, canvas: canvas)
                XCTAssertEqual(model.project.scenes[0].bubble.anchor, anchor, "\(shape) \(topLeft)")
                XCTAssertEqual(rect?.x ?? -1, Double(topLeft.x), accuracy: 1e-6, "\(shape) \(topLeft)")
                XCTAssertEqual(rect?.y ?? -1, Double(topLeft.y), accuracy: 1e-6, "\(shape) \(topLeft)")
            }
        }
    }

    func testDraggedBubbleStaysOnTheCanvas() {
        let canvas = CGSize(width: 1000, height: 800)
        var model = StudioEditorModel(project: makeProject())

        model.moveBubble(center: CGPoint(x: 5000, y: 5000), canvasSize: canvas)
        var rect = resolvedCamera(model.project, canvas: canvas)
        XCTAssertEqual(rect?.maxX ?? 0, 1000, accuracy: 1e-6)
        XCTAssertEqual(rect?.maxY ?? 0, 800, accuracy: 1e-6)
        XCTAssertEqual(model.project.scenes[0].bubble.anchor, .bottomRight)

        model.moveBubble(topLeft: CGPoint(x: -400, y: -400), canvasSize: canvas)
        rect = resolvedCamera(model.project, canvas: canvas)
        XCTAssertEqual(rect?.x ?? -1, 0, accuracy: 1e-6)
        XCTAssertEqual(rect?.y ?? -1, 0, accuracy: 1e-6)
        XCTAssertEqual(model.project.scenes[0].bubble.anchor, .topLeft)
    }

    func testBubbleDragIsOneUndoStepAndWorksFromAnotherLayout() {
        let canvas = CGSize(width: 1000, height: 800)
        var model = StudioEditorModel(project: makeProject())
        model.setLayout(.sideBySide)
        XCTAssertNotNil(model.bubbleRect(canvasSize: canvas))

        model.moveBubble(topLeft: CGPoint(x: 80, y: 90), canvasSize: canvas)
        XCTAssertEqual(model.project.scenes[0].layout, .sideBySide)

        model.undo()
        XCTAssertEqual(model.project.scenes[0].bubble.offsetX, 0)
        XCTAssertEqual(model.project.scenes[0].bubble.anchor, .bottomRight)
        model.undo()
        XCTAssertEqual(model.project.scenes[0].layout, .bubble)
        XCTAssertFalse(model.canUndo)
    }

    func testBubbleDragNeedsACameraAndARealCanvas() {
        var noCamera = StudioEditorModel(project: makeProject(camera: false))
        noCamera.moveBubble(topLeft: CGPoint(x: 10, y: 10), canvasSize: CGSize(width: 1000, height: 800))
        XCTAssertFalse(noCamera.canUndo)
        XCTAssertNil(noCamera.bubbleRect(canvasSize: CGSize(width: 1000, height: 800)))

        var model = StudioEditorModel(project: makeProject())
        model.moveBubble(topLeft: CGPoint(x: 10, y: 10), canvasSize: .zero)
        model.moveBubble(topLeft: CGPoint(x: Double.nan, y: 10), canvasSize: CGSize(width: 1000, height: 800))
        XCTAssertFalse(model.canUndo)
    }

    // MARK: - Geometry

    func testCanvasGeometryFitsAndConvertsPoints() {
        let geometry = StudioEditorModel.canvasGeometry(
            viewSize: CGSize(width: 1000, height: 800),
            canvasSize: CGSize(width: 1920, height: 1080)
        )

        XCTAssertEqual(geometry.canvasRectInView.x, 0, accuracy: 1e-9)
        XCTAssertEqual(geometry.canvasRectInView.y, 118.75, accuracy: 1e-9)
        XCTAssertEqual(geometry.canvasRectInView.width, 1000, accuracy: 1e-9)
        XCTAssertEqual(geometry.canvasRectInView.height, 562.5, accuracy: 1e-9)
        XCTAssertEqual(geometry.canvasPixelsPerViewPoint, 1.92, accuracy: 1e-9)

        let center = geometry.canvasPoint(fromViewPoint: CGPoint(x: 500, y: 400))
        XCTAssertEqual(Double(center?.x ?? -1), 960, accuracy: 1e-9)
        XCTAssertEqual(Double(center?.y ?? -1), 540, accuracy: 1e-9)
        let back = geometry.viewPoint(fromCanvasPoint: CGPoint(x: 960, y: 540))
        XCTAssertEqual(Double(back.x), 500, accuracy: 1e-9)
        XCTAssertEqual(Double(back.y), 400, accuracy: 1e-9)
        XCTAssertNil(geometry.canvasPoint(fromViewPoint: CGPoint(x: 10, y: 10)))

        let tall = StudioEditorModel.canvasGeometry(
            viewSize: CGSize(width: 1000, height: 800),
            canvasSize: CGSize(width: 1080, height: 1920)
        )
        XCTAssertEqual(tall.canvasRectInView.height, 800, accuracy: 1e-9)
        XCTAssertEqual(tall.canvasRectInView.width, 450, accuracy: 1e-9)
        XCTAssertEqual(tall.canvasRectInView.x, 275, accuracy: 1e-9)

        let empty = StudioEditorModel.canvasGeometry(viewSize: .zero, canvasSize: CGSize(width: 100, height: 100))
        XCTAssertNil(empty.canvasPoint(fromViewPoint: .zero))
        XCTAssertEqual(empty.canvasPixelsPerViewPoint, 1)
    }

    // MARK: - Export Tracking

    func testADraftHasUnexportedChangesUntilItIsExported() {
        var model = StudioEditorModel(project: makeProject())
        XCTAssertTrue(model.hasNeverExported)
        XCTAssertTrue(model.hasUnexportedChanges)

        let rendered = model.editableState
        model.setScreenShadow(0.9)
        var saved = model.project
        saved.exports = [StudioExport(path: "/tmp/out.mp4")]
        model.refreshBookkeeping(from: saved)
        model.markExported(rendered)

        XCTAssertFalse(model.hasNeverExported)
        XCTAssertTrue(model.hasUnexportedChanges, "the shadow changed while the export ran")
        XCTAssertEqual(model.project.screen.shadow, 0.9, accuracy: 1e-9)

        model.markExported(model.editableState)
        XCTAssertFalse(model.hasUnexportedChanges)
    }

    func testAReopenedExportedProjectIsCleanUntilEdited() {
        var exported = makeProject()
        exported.exports = [StudioExport(path: "/tmp/out.mp4")]
        var model = StudioEditorModel(project: exported)

        XCTAssertFalse(model.hasNeverExported)
        XCTAssertFalse(model.hasUnexportedChanges)

        model.setLayout(.camera)
        XCTAssertTrue(model.hasUnexportedChanges)
        model.undo()
        XCTAssertFalse(model.hasUnexportedChanges)
    }

    func testEditableStateRoundTrips() {
        var model = StudioEditorModel(project: makeProject())
        let original = model.project
        model.setLayout(.sideBySide)
        model.setTrim(start: 1, end: 5)
        model.setMuted(true)
        model.setBrandingEnabled(true)
        model.setClickRingsEnabled(false)

        let state = model.editableState
        let rebuilt = state.applied(to: original)

        XCTAssertEqual(rebuilt, model.project)
        XCTAssertEqual(StudioEditableState(project: rebuilt), state)
        XCTAssertEqual(rebuilt.id, original.id)
    }

    // MARK: - Names

    func testNamesForAccessibility() {
        XCTAssertEqual(StudioEditorModel.layoutName(.bubble), "Screen with camera bubble")
        XCTAssertEqual(StudioEditorModel.anchorName(.bottomRight), "Bottom right")
        XCTAssertEqual(StudioEditorModel.shapeName(.roundedRectangle), "Rounded rectangle")
        XCTAssertEqual(StudioEditorModel.aspectName(.portrait9x16), "9:16")
        XCTAssertEqual(StudioEditorModel.secondsText(2.44), "2.4 seconds")
    }

    // MARK: - Zooms: Adding and Removing

    func testAddedZoomHasTheDefaultsAndLooksWhereThePointerIs() {
        var model = StudioEditorModel(project: makeProject(duration: 8, camera: false))
        let events = StudioEvents(cursor: [
            StudioCursorSample(t: 1.5, x: 0.2, y: 0.4),
            StudioCursorSample(t: 2.5, x: 0.8, y: 0.6)
        ])

        XCTAssertEqual(model.addZoom(at: 2, events: events), StudioZoomEditResult(changed: true, index: 0))

        XCTAssertEqual(model.project.zooms.count, 1)
        guard let zoom = model.project.zooms.first else { return }
        XCTAssertEqual(zoom.start, 2, accuracy: 1e-9)
        XCTAssertEqual(zoom.end, 5, accuracy: 1e-9)
        XCTAssertEqual(zoom.scale, 2, accuracy: 1e-9)
        XCTAssertEqual(zoom.easeIn, 0.5, accuracy: 1e-9)
        XCTAssertEqual(zoom.easeOut, 0.5, accuracy: 1e-9)
        XCTAssertEqual(zoom.focus.mode, .point)
        XCTAssertEqual(zoom.focus.x, 0.2, accuracy: 1e-9)
        XCTAssertEqual(zoom.focus.y, 0.4, accuracy: 1e-9)
        XCTAssertEqual(zoom.origin, .manual)

        model.undo()
        XCTAssertTrue(model.project.zooms.isEmpty)
        model.redo()
        XCTAssertEqual(model.project.zooms.count, 1)
    }

    func testAddedZoomLooksAtTheCenterWithoutCursorSamples() {
        var model = StudioEditorModel(project: makeProject(camera: false))

        model.addZoom(at: 1)
        model.addZoom(at: 5, events: StudioEvents())

        XCTAssertEqual(model.project.zooms.count, 2)
        for zoom in model.project.zooms {
            XCTAssertEqual(zoom.focus.x, 0.5, accuracy: 1e-9)
            XCTAssertEqual(zoom.focus.y, 0.5, accuracy: 1e-9)
        }
    }

    func testAddingWhereAZoomIsChangesNothingAndSaysWhichZoomThatIs() {
        var project = makeProject(camera: false)
        project.zooms = [zoom(1, 3), zoom(5, 7)]
        var model = StudioEditorModel(project: project)

        XCTAssertEqual(model.addZoom(at: 5), StudioZoomEditResult(changed: false, index: 1))
        XCTAssertEqual(model.addZoom(at: 6.9), StudioZoomEditResult(changed: false, index: 1))
        XCTAssertEqual(model.addZoom(at: 1), StudioZoomEditResult(changed: false, index: 0))

        XCTAssertEqual(model.project.zooms.count, 2)
        XCTAssertFalse(model.canUndo)
    }

    func testAddedZoomStopsAtTheNextZoomAndAtTheEndOfTheRecording() {
        var project = makeProject(duration: 10, camera: false)
        project.zooms = [zoom(4, 6)]
        var model = StudioEditorModel(project: project)

        XCTAssertEqual(model.addZoom(at: 2), StudioZoomEditResult(changed: true, index: 0))
        XCTAssertEqual(model.addZoom(at: 8.5), StudioZoomEditResult(changed: true, index: 2))

        XCTAssertEqual(model.project.zooms.map(\.start), [2, 4, 8.5])
        guard model.project.zooms.count == 3 else { return }
        XCTAssertTrue(model.project.zooms[0].end == model.project.zooms[1].start)
        XCTAssertEqual(model.project.zooms[2].end, 10, accuracy: 1e-9)
    }

    func testAZoomAddedAtTheEndOfAnotherIsChainedToIt() {
        var project = makeProject(camera: false)
        project.zooms = [zoom(1, 3)]
        var model = StudioEditorModel(project: project)

        XCTAssertEqual(model.addZoom(at: 3), StudioZoomEditResult(changed: true, index: 1))
        guard model.project.zooms.count == 2 else { return XCTFail("Expected two zooms") }
        XCTAssertTrue(model.project.zooms[0].end == model.project.zooms[1].start)
    }

    func testAddingWithoutRoomForTheShortestZoomChangesNothing() {
        for time in [3.8, 9.8, 10, 25] {
            var project = makeProject(duration: 10, camera: false)
            project.zooms = [zoom(4, 6)]
            var model = StudioEditorModel(project: project)

            XCTAssertEqual(model.addZoom(at: time), StudioZoomEditResult(changed: false, index: nil), "at \(time)")
            XCTAssertEqual(model.project.zooms.count, 1, "at \(time)")
            XCTAssertFalse(model.canUndo, "at \(time)")
        }
    }

    func testAddingKeepsTheTimeInsideTheRecordingAndIgnoresWhatIsNotANumber() {
        var model = StudioEditorModel(project: makeProject(duration: 10, camera: false))

        XCTAssertEqual(model.addZoom(at: .nan), StudioZoomEditResult(changed: false, index: nil))
        XCTAssertEqual(model.addZoom(at: .infinity), StudioZoomEditResult(changed: false, index: nil))
        XCTAssertEqual(model.addZoom(at: -4), StudioZoomEditResult(changed: true, index: 0))

        XCTAssertEqual(model.project.zooms.map(\.start), [0])
    }

    func testRemovingTakesOneZoomOut() {
        var project = makeProject(camera: false)
        project.zooms = [zoom(1, 2), zoom(3, 4), zoom(5, 6)]
        var model = StudioEditorModel(project: project)

        XCTAssertEqual(model.removeZoom(at: 1), StudioZoomEditResult(changed: true, index: nil))
        XCTAssertEqual(model.project.zooms.map(\.start), [1, 5])
        XCTAssertEqual(model.removeZoom(at: 2), StudioZoomEditResult(changed: false, index: nil))
        XCTAssertEqual(model.removeZoom(at: -1), StudioZoomEditResult(changed: false, index: nil))

        model.undo()
        XCTAssertEqual(model.project.zooms.map(\.start), [1, 3, 5])
    }

    // MARK: - Zooms: Moving the Ends

    func testZoomEndsStopAtTheNeighboursOnExactlyTheirNumbers() {
        var project = makeProject(duration: 12, camera: false)
        project.zooms = [zoom(1, 3.1), zoom(5, 7), zoom(9.3, 11)]
        var model = StudioEditorModel(project: project)

        XCTAssertEqual(model.setZoomStart(at: 1, to: 2), StudioZoomEditResult(changed: true, index: 1))
        XCTAssertEqual(model.setZoomEnd(at: 1, to: 10), StudioZoomEditResult(changed: true, index: 1))

        let zooms = model.project.zooms
        XCTAssertEqual(zooms.map(\.start), [1, 3.1, 9.3])
        guard zooms.count == 3 else { return }
        XCTAssertTrue(zooms[1].start == zooms[0].end)
        XCTAssertTrue(zooms[1].end == zooms[2].start)
    }

    func testZoomEndsStopAtTheEdgesOfTheRecording() {
        var project = makeProject(duration: 10, camera: false)
        project.zooms = [zoom(4, 6)]
        var model = StudioEditorModel(project: project)

        model.setZoomStart(at: 0, to: -3)
        model.setZoomEnd(at: 0, to: 40)

        XCTAssertEqual(model.project.zooms[0].start, 0, accuracy: 1e-9)
        XCTAssertEqual(model.project.zooms[0].end, 10, accuracy: 1e-9)
    }

    func testAZoomIsNeverShorterThanTheShortestZoom() {
        var project = makeProject(duration: 10, camera: false)
        project.zooms = [zoom(4, 6)]
        var model = StudioEditorModel(project: project)

        model.setZoomStart(at: 0, to: 5.9)
        XCTAssertEqual(model.project.zooms[0].start, 6 - StudioEditorModel.minimumZoomDuration, accuracy: 1e-9)

        model.setZoomEnd(at: 0, to: 1)
        XCTAssertEqual(
            model.project.zooms[0].end,
            model.project.zooms[0].start + StudioEditorModel.minimumZoomDuration,
            accuracy: 1e-9
        )
    }

    func testAZoomSqueezedBetweenItsNeighboursDoesNotOverlapThem() {
        // Not something the editor makes: a project written by hand, with 0.2 seconds between two zooms.
        var project = makeProject(duration: 10, camera: false)
        project.zooms = [zoom(1, 3), zoom(3, 3.2), zoom(3.2, 6)]
        var model = StudioEditorModel(project: project)

        model.setZoomStart(at: 1, to: 0)
        model.setZoomEnd(at: 1, to: 9)

        XCTAssertEqual(model.project.zooms[1].start, 3, accuracy: 1e-9)
        XCTAssertEqual(model.project.zooms[1].end, 3.2, accuracy: 1e-9)
    }

    func testZoomEditsThatAreNotANumberOrNameNoZoomChangeNothing() {
        var project = makeProject(camera: false)
        project.zooms = [zoom(1, 3)]
        var model = StudioEditorModel(project: project)
        let before = model.project
        let unchanged = StudioZoomEditResult(changed: false, index: 0)
        let noZoom = StudioZoomEditResult(changed: false, index: nil)

        XCTAssertEqual(model.setZoomStart(at: 0, to: .nan), unchanged)
        XCTAssertEqual(model.setZoomEnd(at: 0, to: -.infinity), unchanged)
        XCTAssertEqual(model.setZoomScale(at: 0, to: .nan), unchanged)
        XCTAssertEqual(model.setZoomFocusPoint(at: 0, x: .nan, y: 0.2), unchanged)
        XCTAssertEqual(model.setZoomFocusPoint(at: 0, x: 0.2, y: .infinity), unchanged)
        XCTAssertEqual(model.setZoomEaseIn(at: 0, to: .nan), unchanged)
        XCTAssertEqual(model.setZoomEaseOut(at: 0, to: .nan), unchanged)

        XCTAssertEqual(model.setZoomStart(at: 1, to: 2), noZoom)
        XCTAssertEqual(model.setZoomEnd(at: -1, to: 2), noZoom)
        XCTAssertEqual(model.setZoomScale(at: 7, to: 2), noZoom)
        XCTAssertEqual(model.setZoomFocusMode(at: 1, to: .cursor), noZoom)
        XCTAssertEqual(model.setZoomFocusPoint(at: 1, x: 0.5, y: 0.5), noZoom)
        XCTAssertEqual(model.setZoomEaseIn(at: 1, to: 1), noZoom)
        XCTAssertEqual(model.setZoomEaseOut(at: 1, to: 1), noZoom)

        XCTAssertEqual(model.project, before)
        XCTAssertFalse(model.canUndo)
    }

    // MARK: - Zooms: The Other Values

    func testZoomValuesAreKeptInTheirRanges() {
        var project = makeProject(camera: false)
        project.zooms = [zoom(1, 5)]
        var model = StudioEditorModel(project: project)

        model.setZoomScale(at: 0, to: 9)
        model.setZoomFocusPoint(at: 0, x: -1, y: 2)
        model.setZoomEaseIn(at: 0, to: 7)
        model.setZoomEaseOut(at: 0, to: -1)
        model.setZoomFocusMode(at: 0, to: .cursor)

        let edited = model.project.zooms[0]
        XCTAssertEqual(edited.scale, 5, accuracy: 1e-9)
        XCTAssertEqual(edited.focus.x, 0, accuracy: 1e-9)
        XCTAssertEqual(edited.focus.y, 1, accuracy: 1e-9)
        XCTAssertEqual(edited.easeIn, 3, accuracy: 1e-9)
        XCTAssertEqual(edited.easeOut, 0, accuracy: 1e-9)
        XCTAssertEqual(edited.focus.mode, .cursor)

        model.setZoomScale(at: 0, to: 0.2)
        XCTAssertEqual(model.project.zooms[0].scale, 1, accuracy: 1e-9)
    }

    func testChangingASuggestedZoomMakesItTheUsersOwnAndAnEditThatChangesNothingDoesNot() {
        var project = makeProject(camera: false)
        project.zooms = [zoom(1, 4, origin: .auto), zoom(5, 8, origin: .auto)]
        var model = StudioEditorModel(project: project)
        let unchanged = StudioZoomEditResult(changed: false, index: 0)

        // The values the zoom already has, as the start of a drag sends them.
        XCTAssertEqual(model.setZoomScale(at: 0, to: 2), unchanged)
        XCTAssertEqual(model.setZoomStart(at: 0, to: 1), unchanged)
        XCTAssertEqual(model.setZoomEnd(at: 0, to: 4), unchanged)
        XCTAssertEqual(model.setZoomFocusMode(at: 0, to: .point), unchanged)
        XCTAssertEqual(model.setZoomFocusPoint(at: 0, x: 0.5, y: 0.5), unchanged)
        XCTAssertEqual(model.project.zooms[0].origin, .auto)
        XCTAssertFalse(model.canUndo)

        XCTAssertEqual(model.setZoomScale(at: 0, to: 3), StudioZoomEditResult(changed: true, index: 0))

        XCTAssertEqual(model.project.zooms[0].origin, .manual)
        XCTAssertEqual(model.project.zooms[1].origin, .auto)

        model.undo()
        XCTAssertEqual(model.project.zooms[0].origin, .auto)
        XCTAssertEqual(model.project.zooms[0].scale, 2, accuracy: 1e-9)
    }

    func testZoomEditsKeepWhatTheZoomHasThatThisVersionDoesNotKnow() throws {
        let json = """
        {
          "id": "3f0013cf-ba10-4453-af91-792b7882dae6",
          "sources": { "screen": { "width": 1920, "height": 1080, "duration": 10 } },
          "zooms": [ { "start": 1, "end": 4, "tilt": 3, "focus": { "x": 0.2, "y": 0.3, "depth": 2 } } ]
        }
        """
        let project = try StudioJSON.makeDecoder().decode(StudioProject.self, from: Data(json.utf8))
        var model = StudioEditorModel(project: project)
        XCTAssertFalse(project.zooms[0].extra.isEmpty)
        XCTAssertFalse(project.zooms[0].focus.extra.isEmpty)

        model.setZoomScale(at: 0, to: 3)
        model.setZoomFocusPoint(at: 0, x: 0.6, y: 0.7)
        model.setZoomEnd(at: 0, to: 5)

        XCTAssertEqual(model.project.zooms[0].extra, project.zooms[0].extra)
        XCTAssertEqual(model.project.zooms[0].focus.extra, project.zooms[0].focus.extra)
    }

    func testADragOfAZoomIsOneUndoStepAndCanBeCancelled() {
        var project = makeProject(camera: false)
        project.zooms = [zoom(1, 4)]
        var model = StudioEditorModel(project: project)

        model.beginEditingGroup()
        model.setZoomEnd(at: 0, to: 4.5)
        model.setZoomEnd(at: 0, to: 5)
        model.setZoomEnd(at: 0, to: 6)
        XCTAssertFalse(model.canUndo)
        model.commitEditingGroup()

        XCTAssertEqual(model.project.zooms[0].end, 6, accuracy: 1e-9)
        model.undo()
        XCTAssertEqual(model.project.zooms[0].end, 4, accuracy: 1e-9)
        XCTAssertFalse(model.canUndo)

        model.beginEditingGroup()
        model.setZoomFocusPoint(at: 0, x: 0.1, y: 0.9)
        model.cancelEditingGroup()
        XCTAssertEqual(model.project.zooms[0].focus.x, 0.5, accuracy: 1e-9)
        XCTAssertFalse(model.canUndo)
    }

    func testSettingAZoomBackToWhatWasExportedIsNoLongerAChange() {
        var exported = makeProject(camera: false)
        exported.zooms = [zoom(1, 4)]
        exported.exports = [StudioExport(path: "/tmp/out.mp4")]
        var model = StudioEditorModel(project: exported)
        XCTAssertFalse(model.hasUnexportedChanges)

        model.setZoomScale(at: 0, to: 3)
        XCTAssertTrue(model.hasUnexportedChanges)
        model.setZoomScale(at: 0, to: 2)
        XCTAssertFalse(model.hasUnexportedChanges)

        model.addZoom(at: 6)
        XCTAssertTrue(model.hasUnexportedChanges)
        model.undo()
        XCTAssertFalse(model.hasUnexportedChanges)
    }

    // MARK: - Zooms: The List

    func testTheEditorKeepsTheZoomsInTimeOrderFromTheMomentItOpens() {
        var project = makeProject(camera: false)
        project.zooms = [zoom(5, 7), zoom(1, 3), zoom(8, 9)]
        var model = StudioEditorModel(project: project)

        XCTAssertEqual(model.project.zooms.map(\.start), [1, 5, 8])
        XCTAssertFalse(model.canUndo)

        XCTAssertEqual(model.addZoom(at: 3.5), StudioZoomEditResult(changed: true, index: 1))
        XCTAssertEqual(model.project.zooms.map(\.start), [1, 3.5, 5, 8])
    }

    func testTheZoomAtATimeHasItsStartAndNotItsEnd() {
        var project = makeProject(camera: false)
        project.zooms = [zoom(1, 3), zoom(3, 5), zoom(6, 7)]
        let model = StudioEditorModel(project: project)

        XCTAssertNil(model.zoomIndex(at: 0.99))
        XCTAssertEqual(model.zoomIndex(at: 1), 0)
        XCTAssertEqual(model.zoomIndex(at: 2.99), 0)
        XCTAssertEqual(model.zoomIndex(at: 3), 1)
        XCTAssertEqual(model.zoomIndex(at: 4.5), 1)
        XCTAssertNil(model.zoomIndex(at: 5))
        XCTAssertEqual(model.zoomIndex(at: 6), 2)
        XCTAssertNil(model.zoomIndex(at: .nan))
    }

    // MARK: - Zooms: Suggestions

    func testApplyingSuggestionsReplacesOnlyTheSuggestedZoomsInOneUndoStep() {
        var project = makeProject(camera: false)
        project.zooms = [zoom(1, 2), zoom(4, 5, origin: .auto), zoom(9, 9.5)]
        var model = StudioEditorModel(project: project)

        // Suggestions arrive marked as such, but the editor does not rely on it.
        XCTAssertTrue(model.applyZoomSuggestions([zoom(6, 8), zoom(3, 3.5, origin: .auto)]))

        XCTAssertEqual(model.project.zooms.map(\.start), [1, 3, 6, 9])
        XCTAssertEqual(model.project.zooms.map(\.origin), [.manual, .auto, .auto, .manual])

        model.undo()
        XCTAssertEqual(model.project.zooms.map(\.start), [1, 4, 9])
        XCTAssertFalse(model.canUndo)
    }

    func testApplyingTheSuggestionsThatAreThereChangesNothing() {
        var model = StudioEditorModel(project: makeProject(camera: false))
        let events = StudioEvents(clicks: [StudioClickEvent(t: 3, x: 0.2, y: 0.3, button: .left)])

        XCTAssertTrue(model.applyZoomSuggestions(StudioLayoutResolver.suggestZooms(project: model.project, events: events)))
        XCTAssertFalse(model.applyZoomSuggestions(StudioLayoutResolver.suggestZooms(project: model.project, events: events)))

        XCTAssertEqual(model.project.zooms.count, 1)
        model.undo()
        XCTAssertTrue(model.project.zooms.isEmpty)
        XCTAssertFalse(model.canUndo)
    }

    func testApplyingNoSuggestionsTakesTheSuggestedZoomsAway() {
        var project = makeProject(camera: false)
        project.zooms = [zoom(1, 2, origin: .auto), zoom(4, 5)]
        var model = StudioEditorModel(project: project)

        XCTAssertTrue(model.applyZoomSuggestions([]))

        XCTAssertEqual(model.project.zooms.map(\.start), [4])
    }

    func testSuggestionsForClicksThatMoveAreChainedByOneNumber() {
        let project = makeProject(camera: false)
        let events = StudioEvents(clicks: [
            StudioClickEvent(t: 3, x: 0.2, y: 0.3, button: .left),
            StudioClickEvent(t: 5.1, x: 0.8, y: 0.7, button: .left)
        ])

        let zooms = StudioLayoutResolver.suggestZooms(project: project, events: events)

        XCTAssertEqual(zooms.count, 2)
        guard zooms.count == 2 else { return }

        // Not merely close: the layout chains two zooms only when the numbers are the same.
        XCTAssertTrue(zooms[0].end == zooms[1].start)
        XCTAssertEqual(zooms.map(\.origin), [.auto, .auto])

        var chained = project
        chained.zooms = zooms
        let justBefore = StudioLayoutResolver.resolve(
            project: chained,
            time: zooms[1].start - 0.001,
            canvasWidth: 1920,
            canvasHeight: 1080
        ).screen?.source
        assertRect(justBefore, x: 0, y: 0.05, width: 0.5, height: 0.5)
    }

    func testSuggestionsNeedClicks() {
        let project = makeProject(camera: false)

        XCTAssertTrue(StudioLayoutResolver.suggestZooms(project: project, events: nil).isEmpty)
        XCTAssertTrue(StudioLayoutResolver.suggestZooms(project: project, events: StudioEvents()).isEmpty)
    }

    // MARK: - Crops

    func testACropIsMadeValidSizeFirstAndThenPosition() {
        var model = StudioEditorModel(project: makeProject())

        model.setScreenCrop(StudioRect(x: -1, y: 0.9, width: 2, height: 0.01))
        model.setCameraCrop(StudioRect(x: 0.9, y: 0.9, width: 0.2, height: 0.2))

        assertRect(model.project.screen.crop, x: 0, y: 0.9, width: 1, height: 0.05)
        assertRect(model.project.camera.crop, x: 0.8, y: 0.8, width: 0.2, height: 0.2)
        XCTAssertNotNil(StudioCanvasMath.validCrop(model.project.screen.crop))
        XCTAssertNotNil(StudioCanvasMath.validCrop(model.project.camera.crop))
    }

    func testAValidCropIsStoredAsItIsAndChangesTheCanvas() {
        var model = StudioEditorModel(project: makeProject(camera: false))

        model.setScreenCrop(StudioRect(x: 0.25, y: 0.25, width: 0.5, height: 0.25))

        XCTAssertEqual(model.project.screen.crop, StudioRect(x: 0.25, y: 0.25, width: 0.5, height: 0.25))
        let natural = StudioCanvasMath.naturalCanvas(project: model.project)
        XCTAssertEqual(natural.width, 960, accuracy: 1e-9)
        XCTAssertEqual(natural.height, 270, accuracy: 1e-9)
    }

    func testCropsCanBeClearedAndUndoneAndStayOutOfASavedLook() {
        var model = StudioEditorModel(project: makeProject())
        model.setScreenCrop(StudioRect(x: 0.1, y: 0.1, width: 0.5, height: 0.5))
        model.setCameraCrop(StudioRect(x: 0.2, y: 0.2, width: 0.5, height: 0.5))

        XCTAssertNil(model.currentLook.screen.crop)
        XCTAssertNil(model.currentLook.camera.crop)

        model.clearScreenCrop()
        model.clearCameraCrop()
        XCTAssertNil(model.project.screen.crop)
        XCTAssertNil(model.project.camera.crop)

        // Clearing what is not there is not an edit.
        model.clearScreenCrop()
        model.undo()
        XCTAssertNotNil(model.project.camera.crop)
        model.undo()
        XCTAssertNotNil(model.project.screen.crop)
    }

    func testACropThatIsNotANumberIsIgnored() {
        var model = StudioEditorModel(project: makeProject())
        model.setScreenCrop(StudioRect(x: 0.1, y: 0.1, width: 0.5, height: 0.5))

        model.setScreenCrop(StudioRect(x: .nan, y: 0, width: 0.5, height: 0.5))
        model.setScreenCrop(StudioRect(x: 0, y: 0, width: .infinity, height: 0.5))
        model.setCameraCrop(StudioRect(x: 0, y: .nan, width: 0.5, height: 0.5))

        XCTAssertEqual(model.project.screen.crop, StudioRect(x: 0.1, y: 0.1, width: 0.5, height: 0.5))
        XCTAssertNil(model.project.camera.crop)
    }

    func testAZoomWorksInsideTheCrop() {
        var model = StudioEditorModel(project: makeProject(camera: false))
        model.setScreenCrop(StudioRect(x: 0.5, y: 0, width: 0.5, height: 1))
        model.addZoom(at: 1)
        model.setZoomEaseIn(at: 0, to: 0)

        // The focus is the middle of the whole screen, which is the crop's left edge.
        let source = StudioLayoutResolver.resolve(
            project: model.project,
            time: 2,
            canvasWidth: 960,
            canvasHeight: 1080
        ).screen?.source

        assertRect(source, x: 0.5, y: 0.25, width: 0.25, height: 0.5)
    }

    // MARK: - Zooms: Moving a Whole Zoom

    func testAMovedZoomKeepsItsLengthAndGoesWhereItIsPut() {
        var project = makeProject(duration: 20, camera: false)
        project.zooms = [zoom(1, 3), zoom(8, 10.5), zoom(15, 17)]
        var model = StudioEditorModel(project: project)

        XCTAssertEqual(model.moveZoom(at: 1, to: 5.25), StudioZoomEditResult(changed: true, index: 1))

        XCTAssertEqual(model.project.zooms[1].start, 5.25, accuracy: 1e-9)
        XCTAssertEqual(model.project.zooms[1].end, 7.75, accuracy: 1e-9)
        XCTAssertTrue(model.canUndo)

        model.undo()
        XCTAssertEqual(model.project.zooms[1].start, 8, accuracy: 1e-9)
        XCTAssertEqual(model.project.zooms[1].end, 10.5, accuracy: 1e-9)
    }

    func testAMovedZoomStopsAtTheNeighboursOnExactlyTheirNumbers() {
        // Numbers that are not exact in binary: 11.1 - 2.3 + 2.3 is not 11.1.
        var project = makeProject(duration: 20, camera: false)
        project.zooms = [zoom(1, 3.1), zoom(4, 6.3), zoom(11.1, 13)]
        var model = StudioEditorModel(project: project)

        XCTAssertEqual(model.moveZoom(at: 1, to: 19), StudioZoomEditResult(changed: true, index: 1))

        var zooms = model.project.zooms
        XCTAssertTrue(zooms[1].end == zooms[2].start)
        XCTAssertEqual(zooms[1].end - zooms[1].start, 2.3, accuracy: 1e-9)

        XCTAssertEqual(model.moveZoom(at: 1, to: 0), StudioZoomEditResult(changed: true, index: 1))

        zooms = model.project.zooms
        XCTAssertTrue(zooms[1].start == zooms[0].end)
        XCTAssertEqual(zooms[1].end - zooms[1].start, 2.3, accuracy: 1e-9)
        XCTAssertEqual(zooms[0].start, 1, accuracy: 1e-9)
        XCTAssertEqual(zooms[2].start, 11.1, accuracy: 1e-9)
    }

    func testAMovedZoomStopsAtTheEndsOfTheRecording() {
        var project = makeProject(duration: 11.1, camera: false)
        project.zooms = [zoom(4, 6.3)]
        var model = StudioEditorModel(project: project)

        model.moveZoom(at: 0, to: -5)
        XCTAssertEqual(model.project.zooms[0].start, 0, accuracy: 1e-9)
        XCTAssertEqual(model.project.zooms[0].end, 2.3, accuracy: 1e-9)

        // Exactly the end of the recording, not a rounding error past it.
        model.moveZoom(at: 0, to: 50)
        XCTAssertEqual(model.project.zooms[0].start, 8.8, accuracy: 1e-9)
        XCTAssertTrue(model.project.zooms[0].end == 11.1)
    }

    func testMovingASuggestionMakesItTheUsersOwnAndMovingItNowhereDoesNot() {
        var project = makeProject(camera: false)
        project.zooms = [zoom(4, 6, origin: .auto)]
        var model = StudioEditorModel(project: project)

        XCTAssertEqual(model.moveZoom(at: 0, to: 4), StudioZoomEditResult(changed: false, index: 0))
        XCTAssertEqual(model.project.zooms[0].origin, .auto)
        XCTAssertFalse(model.canUndo)

        XCTAssertEqual(model.moveZoom(at: 0, to: 5), StudioZoomEditResult(changed: true, index: 0))
        XCTAssertEqual(model.project.zooms[0].origin, .manual)
    }

    func testMovingAZoomToWhatIsNotANumberOrNamingNoZoomChangesNothing() {
        var project = makeProject(camera: false)
        project.zooms = [zoom(4, 6)]
        var model = StudioEditorModel(project: project)
        let before = model.project

        XCTAssertEqual(model.moveZoom(at: 0, to: .nan), StudioZoomEditResult(changed: false, index: 0))
        XCTAssertEqual(model.moveZoom(at: 0, to: .infinity), StudioZoomEditResult(changed: false, index: 0))
        XCTAssertEqual(model.moveZoom(at: 1, to: 2), StudioZoomEditResult(changed: false, index: nil))
        XCTAssertEqual(model.moveZoom(at: -1, to: 2), StudioZoomEditResult(changed: false, index: nil))

        XCTAssertEqual(model.project, before)
        XCTAssertFalse(model.canUndo)
    }

    func testAZoomLongerThanTheRoomBetweenItsNeighboursIsMovedIntoTheRoom() {
        // Not something the editor makes: a project written by hand, where a zoom of 3.5 seconds
        // overlaps the next one and has 3 seconds of room.
        var project = makeProject(duration: 10, camera: false)
        project.zooms = [zoom(1, 3), zoom(4, 7.5), zoom(6, 9)]
        var model = StudioEditorModel(project: project)

        XCTAssertEqual(model.moveZoom(at: 1, to: 0), StudioZoomEditResult(changed: true, index: 1))
        XCTAssertEqual(model.project.zooms[1].start, 3, accuracy: 1e-9)
        XCTAssertEqual(model.project.zooms[1].end, 6, accuracy: 1e-9)

        // And with no room at all between them it stays where it is.
        project.zooms = [zoom(1, 5), zoom(4, 4.5), zoom(4.2, 9)]
        var crowded = StudioEditorModel(project: project)

        XCTAssertEqual(crowded.moveZoom(at: 1, to: 0), StudioZoomEditResult(changed: false, index: 1))
        XCTAssertEqual(crowded.project.zooms[1].start, 4, accuracy: 1e-9)
    }

    // MARK: - Zooms: Whether One Can Be Added

    func testAZoomCanBeAddedExactlyWhereAddingOneAnswersWithAZoom() {
        var project = makeProject(duration: 10, camera: false)
        project.zooms = [zoom(1, 3), zoom(3.2, 7), zoom(9.8, 10)]

        for step in -4...208 {
            let time = Double(step) * 0.05
            var model = StudioEditorModel(project: project)
            let canAdd = model.canAddZoom(at: time)
            let result = model.addZoom(at: time)

            XCTAssertEqual(canAdd, result.index != nil, "at \(time): adding gave \(result)")
        }

        let untouched = StudioEditorModel(project: project)

        // A second before the first zoom, and inside a zoom, which adding selects.
        XCTAssertTrue(untouched.canAddZoom(at: 0))
        XCTAssertTrue(untouched.canAddZoom(at: 2))

        // 0.15 seconds before the next zoom, and 0.2 before the last.
        XCTAssertFalse(untouched.canAddZoom(at: 3.05))
        XCTAssertFalse(untouched.canAddZoom(at: 9.6))

        // At the end of a zoom, chained to it.
        XCTAssertTrue(untouched.canAddZoom(at: 7))

        // Times outside the recording count as its ends.
        XCTAssertTrue(untouched.canAddZoom(at: -3))
        XCTAssertFalse(untouched.canAddZoom(at: 40))
        XCTAssertFalse(untouched.canAddZoom(at: .nan))
        XCTAssertFalse(untouched.canAddZoom(at: .infinity))
    }

    // MARK: - Zooms: Stepping Through Them

    func testTheNextZoomFollowsTheSelectedOneOrIsTheOneAtOrAfterThePlayhead() {
        var project = makeProject(duration: 12, camera: false)
        project.zooms = [zoom(1, 3), zoom(5, 7), zoom(9, 11)]
        let model = StudioEditorModel(project: project)
        let cases: [(selected: Int?, playhead: Double, expected: Int?)] = [
            (nil, 0, 0),
            (nil, 2, 0),
            (nil, 3, 1),
            (nil, 6, 1),
            (nil, 12, nil),
            (0, 12, 1),
            (1, 0, 2),
            (2, 0, nil),
            (9, 6, 1),
        ]

        for item in cases {
            XCTAssertEqual(
                model.zoomIndex(after: item.selected, playhead: item.playhead),
                item.expected,
                "selected \(String(describing: item.selected)), playhead \(item.playhead)"
            )
        }
    }

    func testThePreviousZoomComesBeforeTheSelectedOneOrIsTheOneAtOrBeforeThePlayhead() {
        var project = makeProject(duration: 12, camera: false)
        project.zooms = [zoom(1, 3), zoom(5, 7), zoom(9, 11)]
        let model = StudioEditorModel(project: project)
        let cases: [(selected: Int?, playhead: Double, expected: Int?)] = [
            (nil, 0, nil),
            (nil, 1, 0),
            (nil, 4, 0),
            (nil, 6, 1),
            (nil, 12, 2),
            (2, 0, 1),
            (0, 12, nil),
            (-1, 6, 1),
        ]

        for item in cases {
            XCTAssertEqual(
                model.zoomIndex(before: item.selected, playhead: item.playhead),
                item.expected,
                "selected \(String(describing: item.selected)), playhead \(item.playhead)"
            )
        }
    }

    func testWithoutZoomsThereIsNoNextOrPreviousOne() {
        let model = StudioEditorModel(project: makeProject(camera: false))

        XCTAssertNil(model.zoomIndex(after: nil, playhead: 0))
        XCTAssertNil(model.zoomIndex(before: nil, playhead: 5))
        XCTAssertNil(model.zoomIndex(after: 0, playhead: 0))
    }

    // MARK: - Zooms: Keeping a Selection on Its Zoom

    func testWhenOnlyOneZoomHasChangedItIsFollowedToTheSamePlaceHoweverFarItMoved() {
        let before = [zoom(1, 2), zoom(4, 5), zoom(8, 9)]

        // The middle zoom, moved to where it shares no time with what it was.
        XCTAssertEqual(
            StudioEditorModel.zoomIndex(following: 1, from: before, to: [zoom(1, 2), zoom(6, 7, scale: 3), zoom(8, 9)]),
            1
        )

        // Nothing changed at all, as when an edit was to something other than the zooms.
        XCTAssertEqual(StudioEditorModel.zoomIndex(following: 2, from: before, to: before), 2)
        XCTAssertEqual(StudioEditorModel.zoomIndex(following: 0, from: before, to: before), 0)
    }

    func testWhenMoreHasChangedAZoomIsFollowedToTheOneThatSharesTheMostTimeWithIt() {
        let after = [zoom(0, 2), zoom(2, 5), zoom(6, 9)]

        // Itself, one place on, behind a zoom that was added.
        XCTAssertEqual(StudioEditorModel.zoomIndex(following: 0, from: [zoom(2, 5), zoom(6, 9)], to: after), 1)

        // Half a second with the first, two seconds with the second.
        XCTAssertEqual(StudioEditorModel.zoomIndex(following: 0, from: [zoom(1.5, 4)], to: after), 1)

        // Half a second with the second, two seconds with the third.
        XCTAssertEqual(StudioEditorModel.zoomIndex(following: 0, from: [zoom(4.5, 8)], to: after), 2)

        // Touching two zooms is not sharing time with either.
        XCTAssertNil(StudioEditorModel.zoomIndex(following: 0, from: [zoom(5, 6)], to: after))
        XCTAssertNil(StudioEditorModel.zoomIndex(following: 0, from: [zoom(10, 12)], to: after))
        XCTAssertNil(StudioEditorModel.zoomIndex(following: 0, from: [zoom(1, 2)], to: []))
    }

    func testAmongZoomsThatShareTheSameTimeTheOneNearestToWhereItWasIsFollowed() {
        // Written by hand: the editor does not make zooms that overlap.
        let after = [zoom(0, 10), zoom(2, 5), zoom(2, 5, scale: 3)]
        let before = [zoom(2, 5), zoom(2, 5), zoom(2, 5), zoom(2, 5)]

        XCTAssertEqual(StudioEditorModel.zoomIndex(following: 0, from: before, to: after), 0)
        XCTAssertEqual(StudioEditorModel.zoomIndex(following: 1, from: before, to: after), 1)
        XCTAssertEqual(StudioEditorModel.zoomIndex(following: 2, from: before, to: after), 2)
        XCTAssertEqual(StudioEditorModel.zoomIndex(following: 3, from: before, to: after), 2)
    }

    func testAZoomThatWasNotThereIsNotFollowedAnywhere() {
        let zooms = [zoom(1, 2)]

        XCTAssertNil(StudioEditorModel.zoomIndex(following: 1, from: zooms, to: zooms))
        XCTAssertNil(StudioEditorModel.zoomIndex(following: -1, from: zooms, to: zooms))
        XCTAssertNil(StudioEditorModel.zoomIndex(following: 0, from: [], to: zooms))
    }

    // MARK: - Zooms: Showing One

    func testAZoomIsShownAtTheMomentItHasMovedIn() {
        var project = makeProject(duration: 20, camera: false)
        project.zooms = [
            zoom(1, 4),
            zoom(5, 8, easeIn: 1.25),
            zoom(10, 12, easeIn: 0),
            // Eases longer than the zoom are shortened in proportion: 3 and 1 in one second are
            // 0.75 and 0.25.
            zoom(14, 15, easeIn: 3, easeOut: 1),
        ]
        let model = StudioEditorModel(project: project)

        XCTAssertEqual(model.zoomLookTime(at: 0) ?? .nan, 1.5, accuracy: 1e-9)
        XCTAssertEqual(model.zoomLookTime(at: 1) ?? .nan, 6.25, accuracy: 1e-9)
        XCTAssertEqual(model.zoomLookTime(at: 2) ?? .nan, 10, accuracy: 1e-9)
        XCTAssertEqual(model.zoomLookTime(at: 3) ?? .nan, 14.75, accuracy: 1e-9)
        XCTAssertNil(model.zoomLookTime(at: 4))
        XCTAssertNil(model.zoomLookTime(at: -1))

        // At each of those times the layout shows the whole zoom: twice the size, in the middle.
        for index in 0..<4 {
            let source = StudioLayoutResolver.resolve(
                project: model.project,
                time: model.zoomLookTime(at: index) ?? .nan,
                canvasWidth: 1920,
                canvasHeight: 1080
            ).screen?.source
            assertRect(source, x: 0.25, y: 0.25, width: 0.5, height: 0.5)
        }
    }

    func testAZoomThatNeverMovesAllTheWayInIsShownOnItsLastFrame() {
        // The next zoom is chained, so this one has no ease out and its ease in takes the whole
        // second. A zoom does not contain its end, so the time stays one frame inside it.
        var project = makeProject(duration: 20, camera: false)
        project.zooms = [zoom(14, 15, easeIn: 3, easeOut: 1), zoom(15, 18)]
        let model = StudioEditorModel(project: project)

        let time = model.zoomLookTime(at: 0) ?? .nan

        XCTAssertEqual(time, 15 - 1.0 / 30, accuracy: 1e-9)
        XCTAssertEqual(model.zoomIndex(at: time), 0)
    }

    func testAZoomShorterThanAFrameIsShownAtItsStart() {
        var project = makeProject(duration: 20, camera: false)
        project.zooms = [zoom(3, 3.01, easeIn: 0)]
        let model = StudioEditorModel(project: project)

        XCTAssertEqual(model.zoomLookTime(at: 0) ?? .nan, 3, accuracy: 1e-9)
    }

    // MARK: - Zooms: The Focus Pad

    func testThePadShowsTheWindowAndThePointAcrossTheWholeScreen() {
        var project = makeProject(camera: false)
        project.zooms = [zoom(1, 3, scale: 4, x: 0.5, y: 0.25), zoom(5, 7, scale: 2, x: 0.9, y: 0.05)]
        let model = StudioEditorModel(project: project)

        let centered = model.zoomPad(at: 0)
        XCTAssertEqual(centered?.aspectRatio ?? .nan, 16.0 / 9, accuracy: 1e-9)
        assertRect(centered?.window, x: 0.375, y: 0.125, width: 0.25, height: 0.25)
        XCTAssertEqual(centered?.focusX ?? .nan, 0.5, accuracy: 1e-9)
        XCTAssertEqual(centered?.focusY ?? .nan, 0.25, accuracy: 1e-9)

        // Near a corner the window stops at the edges, and the point stays where it was put.
        let corner = model.zoomPad(at: 1)
        assertRect(corner?.window, x: 0.5, y: 0, width: 0.5, height: 0.5)
        XCTAssertEqual(corner?.focusX ?? .nan, 0.9, accuracy: 1e-9)
        XCTAssertEqual(corner?.focusY ?? .nan, 0.05, accuracy: 1e-9)

        XCTAssertNil(model.zoomPad(at: 2))
        XCTAssertNil(model.zoomPad(at: -1))
    }

    func testThePadStandsForTheCropWhenTheScreenIsCropped() {
        var project = makeProject(camera: false)
        project.zooms = [zoom(1, 3, scale: 2, x: 0.75, y: 0.5)]
        var model = StudioEditorModel(project: project)
        model.setScreenCrop(StudioRect(x: 0.5, y: 0, width: 0.5, height: 1))

        // The right half of a 16:9 screen, and the zoom looks at the middle of it.
        let pad = model.zoomPad(at: 0)
        XCTAssertEqual(pad?.aspectRatio ?? .nan, 8.0 / 9, accuracy: 1e-9)
        XCTAssertEqual(pad?.focusX ?? .nan, 0.5, accuracy: 1e-9)
        XCTAssertEqual(pad?.focusY ?? .nan, 0.5, accuracy: 1e-9)
        assertRect(pad?.window, x: 0.25, y: 0.25, width: 0.5, height: 0.5)

        // A point the crop has cut off is shown on the pad's edge, where the window stops too.
        model.setZoomFocusPoint(at: 0, x: 0.1, y: 0.5)
        let outside = model.zoomPad(at: 0)
        XCTAssertEqual(outside?.focusX ?? .nan, 0, accuracy: 1e-9)
        assertRect(outside?.window, x: 0, y: 0.25, width: 0.5, height: 0.5)
    }

    func testThePadsWindowIsTheOneTheLayoutDraws() {
        var project = makeProject(camera: false)
        project.zooms = [zoom(1, 5, scale: 3, x: 0.2, y: 0.9, easeIn: 0)]
        var model = StudioEditorModel(project: project)
        model.setScreenCrop(StudioRect(x: 0.1, y: 0.2, width: 0.6, height: 0.5))

        guard let pad = model.zoomPad(at: 0) else {
            XCTFail("Expected a pad")
            return
        }
        let source = StudioLayoutResolver.resolve(
            project: model.project,
            time: 2,
            canvasWidth: 1920,
            canvasHeight: 1080
        ).screen?.source

        assertRect(
            source,
            x: 0.1 + pad.window.x * 0.6,
            y: 0.2 + pad.window.y * 0.5,
            width: pad.window.width * 0.6,
            height: pad.window.height * 0.5
        )
        XCTAssertEqual(pad.window.width, 1.0 / 3, accuracy: 1e-9)
        XCTAssertEqual(pad.window.height, 1.0 / 3, accuracy: 1e-9)
    }

    func testPointingOnThePadSetsTheFocusInTheScreenFrame() {
        var project = makeProject(camera: false)
        project.zooms = [zoom(1, 3)]
        var model = StudioEditorModel(project: project)
        model.setScreenCrop(StudioRect(x: 0.5, y: 0.2, width: 0.4, height: 0.6))

        // A quarter of the way across the crop, and below the pad, which counts as its bottom edge.
        XCTAssertEqual(model.setZoomFocusOnPad(at: 0, x: 0.25, y: 1.5), StudioZoomEditResult(changed: true, index: 0))

        XCTAssertEqual(model.project.zooms[0].focus.x, 0.6, accuracy: 1e-9)
        XCTAssertEqual(model.project.zooms[0].focus.y, 0.8, accuracy: 1e-9)
        let pad = model.zoomPad(at: 0)
        XCTAssertEqual(pad?.focusX ?? .nan, 0.25, accuracy: 1e-9)
        XCTAssertEqual(pad?.focusY ?? .nan, 1, accuracy: 1e-9)
    }

    func testPointingOnThePadAtWhatIsNotANumberOrForNoZoomChangesNothing() {
        var project = makeProject(camera: false)
        project.zooms = [zoom(1, 3)]
        var model = StudioEditorModel(project: project)
        let before = model.project
        let unchanged = StudioZoomEditResult(changed: false, index: 0)
        let noZoom = StudioZoomEditResult(changed: false, index: nil)

        XCTAssertEqual(model.setZoomFocusOnPad(at: 0, x: .nan, y: 0.5), unchanged)
        XCTAssertEqual(model.setZoomFocusOnPad(at: 0, x: 0.5, y: -.infinity), unchanged)
        XCTAssertEqual(model.setZoomFocusOnPad(at: 3, x: 0.5, y: 0.5), noZoom)
        XCTAssertEqual(model.setZoomFocusOnPad(at: 3, x: .nan, y: 0.5), noZoom)

        // Where the zoom already looks.
        XCTAssertEqual(model.setZoomFocusOnPad(at: 0, x: 0.5, y: 0.5), unchanged)

        XCTAssertEqual(model.project, before)
        XCTAssertFalse(model.canUndo)
    }

    func testSuggestedZoomsAreCountedUntilTheyAreChanged() {
        var project = makeProject(camera: false)
        project.zooms = [zoom(1, 2, origin: .auto), zoom(3, 4), zoom(5, 6, origin: .auto)]
        var model = StudioEditorModel(project: project)

        XCTAssertEqual(model.suggestedZoomCount, 2)

        model.setZoomScale(at: 0, to: 3)

        XCTAssertEqual(model.suggestedZoomCount, 1)
    }

    // MARK: - Crops: As Insets

    func testCropInsetsAreWhatACropCutsOffEachEdge() {
        XCTAssertEqual(StudioCropInsets(crop: nil), StudioCropInsets())
        XCTAssertTrue(StudioCropInsets(crop: nil).isEmpty)

        let insets = StudioCropInsets(crop: StudioRect(x: 0.1, y: 0.2, width: 0.6, height: 0.4))

        // Plain numbers, without what binary leaves behind in 1 - 0.1 - 0.6.
        XCTAssertEqual(insets, StudioCropInsets(left: 0.1, top: 0.2, right: 0.3, bottom: 0.4))
        XCTAssertFalse(insets.isEmpty)
        XCTAssertEqual(insets[.left], 0.1)
        XCTAssertEqual(insets[.top], 0.2)
        XCTAssertEqual(insets[.right], 0.3)
        XCTAssertEqual(insets[.bottom], 0.4)

        // A crop that is not valid is not applied, so it cuts nothing off.
        XCTAssertEqual(StudioCropInsets(crop: StudioRect(x: 0.5, y: 0.5, width: 0.01, height: 0.5)), StudioCropInsets())
    }

    func testACropEdgeGoesWhereItIsPutAndTheCropIsStoredInPlainNumbers() {
        var model = StudioEditorModel(project: makeProject())

        model.setScreenCropInset(.left, to: 0.07)
        assertRect(model.project.screen.crop, x: 0.07, y: 0, width: 0.93, height: 1)

        model.setScreenCropInset(.right, to: 0.2)
        model.setScreenCropInset(.top, to: 0.1)
        model.setScreenCropInset(.bottom, to: 0.25)

        XCTAssertEqual(model.project.screen.crop, StudioRect(x: 0.07, y: 0.1, width: 0.73, height: 0.65))
        XCTAssertEqual(model.screenCropInsets, StudioCropInsets(left: 0.07, top: 0.1, right: 0.2, bottom: 0.25))
        XCTAssertNotNil(StudioCanvasMath.validCrop(model.project.screen.crop))

        XCTAssertNil(model.project.camera.crop)
        model.setCameraCropInset(.top, to: 0.3)
        assertRect(model.project.camera.crop, x: 0, y: 0.3, width: 1, height: 0.7)
        XCTAssertEqual(model.cameraCropInsets, StudioCropInsets(top: 0.3))
        XCTAssertEqual(model.screenCropInsets, StudioCropInsets(left: 0.07, top: 0.1, right: 0.2, bottom: 0.25))
    }

    func testWithNothingCutOffAnyEdgeTheCropIsRemoved() {
        var model = StudioEditorModel(project: makeProject(camera: false))
        model.setScreenCrop(StudioRect(x: 0.1, y: 0.2, width: 0.6, height: 0.4))

        model.setScreenCropInset(.left, to: 0)
        model.setScreenCropInset(.top, to: 0)
        model.setScreenCropInset(.right, to: 0)
        assertRect(model.project.screen.crop, x: 0, y: 0, width: 1, height: 0.6)

        model.setScreenCropInset(.bottom, to: 0)
        XCTAssertNil(model.project.screen.crop)
        XCTAssertTrue(model.screenCropInsets.isEmpty)
    }

    func testACropEdgeStopsWhereAPieceOfTheFrameIsStillLeft() {
        var model = StudioEditorModel(project: makeProject(camera: false))

        model.setScreenCropInset(.left, to: 0.5)
        model.setScreenCropInset(.right, to: 0.9)
        assertRect(model.project.screen.crop, x: 0.5, y: 0, width: 0.05, height: 1)

        // Already as far as it goes.
        let before = model
        model.setScreenCropInset(.left, to: 2)
        XCTAssertEqual(model, before)

        model.setScreenCropInset(.bottom, to: 1)
        model.setScreenCropInset(.top, to: -3)
        assertRect(model.project.screen.crop, x: 0.5, y: 0, width: 0.05, height: 0.05)
        XCTAssertNotNil(StudioCanvasMath.validCrop(model.project.screen.crop))
    }

    func testACropEdgeThatIsNotANumberOrIsPutWhereItIsChangesNothing() {
        var model = StudioEditorModel(project: makeProject(camera: false))
        model.setScreenCropInset(.left, to: 0.1)
        let before = model

        model.setScreenCropInset(.left, to: .nan)
        model.setScreenCropInset(.right, to: .infinity)
        model.setScreenCropInset(.left, to: 0.1)
        model.setScreenCropInset(.top, to: 0)

        XCTAssertEqual(model, before)
        model.undo()
        XCTAssertNil(model.project.screen.crop)
        XCTAssertFalse(model.canUndo)
    }

    func testMovingACropEdgeKeepsWhatTheCropHasThatThisVersionDoesNotKnow() throws {
        let json = """
        {
          "id": "3f0013cf-ba10-4453-af91-792b7882dae6",
          "sources": { "screen": { "width": 1920, "height": 1080, "duration": 10 } },
          "screen": { "crop": { "x": 0.1, "y": 0.1, "width": 0.8, "height": 0.8, "feather": 4 } }
        }
        """
        let project = try StudioJSON.makeDecoder().decode(StudioProject.self, from: Data(json.utf8))
        var model = StudioEditorModel(project: project)
        XCTAssertFalse(project.screen.crop?.extra.isEmpty ?? true)

        model.setScreenCropInset(.left, to: 0.3)

        assertRect(model.project.screen.crop, x: 0.3, y: 0.1, width: 0.6, height: 0.8)
        XCTAssertEqual(model.project.screen.crop?.extra, project.screen.crop?.extra)
    }

    // MARK: - Zooms: Text

    func testZoomAccessibilityText() {
        XCTAssertEqual(
            StudioEditorModel.zoomAccessibilityText(StudioZoom(start: 12, end: 16.5)),
            "Zoom 2×, 12.0 to 16.5 seconds"
        )
        XCTAssertEqual(
            StudioEditorModel.zoomAccessibilityText(zoom(1, 3, scale: 2.5, mode: .cursor, origin: .auto)),
            "Zoom 2.5×, 1.0 to 3.0 seconds, follows the pointer, suggested"
        )
        XCTAssertEqual(
            StudioEditorModel.zoomAccessibilityText(zoom(0, 0.3, scale: 1.25, origin: .auto)),
            "Zoom 1.25×, 0.0 to 0.3 seconds, suggested"
        )

        // The scale that is drawn, which is the stored one kept within 1 to 5.
        XCTAssertEqual(StudioEditorModel.zoomAccessibilityText(zoom(1, 2, scale: 9)), "Zoom 5×, 1.0 to 2.0 seconds")
        XCTAssertEqual(StudioEditorModel.zoomAccessibilityText(zoom(1, 2, scale: 0.2)), "Zoom 1×, 1.0 to 2.0 seconds")
        XCTAssertEqual(StudioEditorModel.zoomAccessibilityText(zoom(1, 2, scale: .nan)), "Zoom 1×, 1.0 to 2.0 seconds")
    }

    func testZoomScaleTextIsTheScaleThatIsDrawnWithUpToTwoDecimals() {
        XCTAssertEqual(StudioEditorModel.zoomScaleText(2), "2×")
        XCTAssertEqual(StudioEditorModel.zoomScaleText(2.5), "2.5×")
        XCTAssertEqual(StudioEditorModel.zoomScaleText(1.25), "1.25×")
        XCTAssertEqual(StudioEditorModel.zoomScaleText(3.14159), "3.14×")
        XCTAssertEqual(StudioEditorModel.zoomScaleText(9), "5×")
        XCTAssertEqual(StudioEditorModel.zoomScaleText(0.2), "1×")
        XCTAssertEqual(StudioEditorModel.zoomScaleText(.nan), "1×")
    }

    func testZoomRangeTextSaysTheTimesInSourceTime() {
        XCTAssertEqual(StudioEditorModel.zoomRangeText(zoom(12, 16.5)), "12.0 to 16.5 seconds")
        XCTAssertEqual(StudioEditorModel.zoomRangeText(zoom(0, 0.3)), "0.0 to 0.3 seconds")
        XCTAssertEqual(StudioEditorModel.zoomRangeText(zoom(.nan, .infinity)), "0.0 to 0.0 seconds")
    }

    func testZoomPositionTextCountsFromOne() {
        XCTAssertEqual(StudioEditorModel.zoomPositionText(index: 0, count: 1), "Zoom 1 of 1")
        XCTAssertEqual(StudioEditorModel.zoomPositionText(index: 1, count: 5), "Zoom 2 of 5")
        XCTAssertEqual(StudioEditorModel.zoomPositionText(index: 11, count: 1000), "Zoom 12 of 1000")
    }

    func testZoomSuggestionsTextSaysHowManyThereAre() {
        XCTAssertEqual(StudioEditorModel.zoomSuggestionsText(count: 0), "No zooms to suggest for this recording.")
        XCTAssertEqual(StudioEditorModel.zoomSuggestionsText(count: -1), "No zooms to suggest for this recording.")
        XCTAssertEqual(StudioEditorModel.zoomSuggestionsText(count: 1), "1 zoom suggested.")
        XCTAssertEqual(StudioEditorModel.zoomSuggestionsText(count: 2), "2 zooms suggested.")
        XCTAssertEqual(StudioEditorModel.zoomSuggestionsText(count: 1200), "1200 zooms suggested.")
    }

    // MARK: - Helpers

    // MARK: - Scenes

    func testTheCurrentSceneIsTheOneThePlayheadIsIn() {
        var model = StudioEditorModel(project: threeScenes())
        for (time, index) in [(-5.0, 0), (0.0, 0), (3.999, 0), (4.0, 1), (6.9, 1), (7.0, 2), (100.0, 2)] {
            model.sceneTime = time
            XCTAssertEqual(model.currentSceneIndex, index, "at \(time)")
            XCTAssertEqual(model.sceneIndex(at: time), index, "at \(time)")
        }
        model.sceneTime = 5
        XCTAssertEqual(model.currentScene.layout, .sideBySide)
        XCTAssertEqual(model.effectiveLayout, .sideBySide)

        // Moving the playhead is not an edit.
        XCTAssertFalse(model.canUndo)
        XCTAssertEqual(model.project, StudioEditorModel(project: threeScenes()).project)
    }

    func testTheLayoutControlsChangeTheCurrentSceneOnly() {
        var model = StudioEditorModel(project: threeScenes())
        let before = model.project.scenes

        model.sceneTime = 5
        model.setLayout(.screen)
        model.setSideBySide(cameraSide: .leading, fraction: 0.5)
        XCTAssertEqual(model.project.scenes[1].layout, .screen)
        XCTAssertEqual(model.project.scenes[1].split.cameraSide, .leading)
        XCTAssertEqual(model.project.scenes[1].split.cameraFraction, 0.5, accuracy: 1e-9)
        XCTAssertEqual(model.project.scenes[0], before[0])
        XCTAssertEqual(model.project.scenes[2], before[2])

        model.sceneTime = 8
        model.setCameraBubbleSize(0.4)
        model.setCameraAnchor(.topLeft)
        model.setCameraBubbleOffsets(x: 0.1, y: 0.2)
        XCTAssertEqual(model.project.scenes[2].bubble.size, 0.4, accuracy: 1e-9)
        XCTAssertEqual(model.project.scenes[2].bubble.anchor, .topLeft)
        XCTAssertEqual(model.project.scenes[2].bubble.offsetX, 0.1, accuracy: 1e-9)
        XCTAssertEqual(model.project.scenes[2].bubble.offsetY, 0.2, accuracy: 1e-9)
        XCTAssertEqual(model.project.scenes[0], before[0])

        // Undo puts the scene back, and the current scene is still the one the playhead is in.
        model.sceneTime = 5
        model.undo()
        model.undo()
        model.undo()
        model.undo()
        model.undo()
        XCTAssertEqual(model.project.scenes, before)
        XCTAssertEqual(model.currentSceneIndex, 1)
    }

    func testOpeningPutsScenesInOrderAndDropsThoseThatNeverShow() {
        // As section 6.1 reads them: a negative start is 0, the last scene stored for a start wins,
        // and the first scene starts at 0. Scenes that start at or after the end of the recording
        // are never shown and are dropped.
        var project = makeProject()
        project.scenes = [
            StudioScene(start: 6, layout: .camera),
            StudioScene(start: -2, layout: .screen),
            StudioScene(start: 3, layout: .sideBySide),
            StudioScene(start: 3, layout: .bubble),
            StudioScene(start: 10, layout: .camera),
            StudioScene(start: 12, layout: .screen),
        ]
        let model = StudioEditorModel(project: project)
        XCTAssertEqual(model.project.scenes.map(\.start), [0, 3, 6])
        XCTAssertEqual(model.project.scenes.map(\.layout), [.screen, .bubble, .camera])
        XCTAssertFalse(model.canUndo)

        // A first scene that starts late starts at 0, and stays even when it is the only one.
        var late = makeProject()
        late.scenes = [StudioScene(start: 25, layout: .camera)]
        let lateModel = StudioEditorModel(project: late)
        XCTAssertEqual(lateModel.project.scenes.map(\.start), [0])
        XCTAssertEqual(lateModel.project.scenes[0].layout, .camera)

        // A recording of no length still has its first scene.
        let empty = StudioEditorModel(project: makeProject(duration: 0))
        XCTAssertEqual(empty.project.scenes.count, 1)
        XCTAssertEqual(empty.currentSceneIndex, 0)
        XCTAssertEqual(empty.currentScene.layout, .bubble)
    }

    func testSplittingStartsACopyOfTheSceneEnteredByMoving() {
        var model = StudioEditorModel(project: makeProject())
        model.setCameraBubbleSize(0.4)
        model.setSideBySide(cameraSide: .leading, fraction: 0.45)

        let result = model.splitScene(at: 4)
        XCTAssertEqual(result, StudioSceneEditResult(changed: true, index: 1))
        XCTAssertEqual(model.project.scenes.count, 2)
        XCTAssertEqual(model.project.scenes[0].start, 0)
        XCTAssertEqual(model.project.scenes[1].start, 4)
        XCTAssertEqual(model.project.scenes[1].layout, model.project.scenes[0].layout)
        XCTAssertEqual(model.project.scenes[1].bubble, model.project.scenes[0].bubble)
        XCTAssertEqual(model.project.scenes[1].split, model.project.scenes[0].split)
        XCTAssertEqual(model.project.scenes[1].transition, StudioTransition(kind: .morph, duration: 0.35))

        // The scene before keeps how it was entered.
        XCTAssertEqual(model.project.scenes[0].transition, StudioTransition())

        // One undo step, and the playhead's scene follows.
        model.sceneTime = 4
        XCTAssertEqual(model.currentSceneIndex, 1)
        model.undo()
        XCTAssertEqual(model.project.scenes.count, 1)
        XCTAssertEqual(model.currentSceneIndex, 0)
        model.redo()
        XCTAssertEqual(model.project.scenes.count, 2)
    }

    func testASceneIsNotSplitWhereAHalfWouldBeTooShortOrItIsStillMoving() {
        var model = StudioEditorModel(project: makeProject())

        // Less than 0.3 s from the start of the scene, or from the end of the recording.
        for time in [0.0, 0.25, 9.75, 10.0, 50.0, -3.0] {
            XCTAssertFalse(model.canSplitScene(at: time), "at \(time)")
            XCTAssertEqual(model.splitSceneExplanation(at: time), StudioEditorModel.sceneTooShortToSplitExplanation, "at \(time)")
            XCTAssertEqual(model.splitScene(at: time), StudioSceneEditResult(changed: false, index: 0), "at \(time)")
        }
        XCTAssertFalse(model.canSplitScene(at: .nan))
        XCTAssertFalse(model.canUndo)

        // Half a second from either is enough.
        XCTAssertTrue(model.canSplitScene(at: 0.5))
        XCTAssertTrue(model.canSplitScene(at: 9.5))
        XCTAssertNil(model.splitSceneExplanation(at: 4))
        XCTAssertTrue(model.splitScene(at: 4).changed)

        // The new scene is entered over 0.35 s. At 4.32 s both halves would be long enough, and
        // its layers are still moving.
        XCTAssertEqual(model.splitSceneExplanation(at: 4.32), StudioEditorModel.sceneStillMovingExplanation)
        XCTAssertEqual(model.splitScene(at: 4.32), StudioSceneEditResult(changed: false, index: 1))
        XCTAssertTrue(model.canSplitScene(at: 4.5))

        // Too close to the scene after it, and too close to its own start.
        XCTAssertEqual(model.splitSceneExplanation(at: 3.75), StudioEditorModel.sceneTooShortToSplitExplanation)
        XCTAssertEqual(model.splitSceneExplanation(at: 4.25), StudioEditorModel.sceneTooShortToSplitExplanation)

        // A scene that is cut to is at rest from its first instant.
        model.setSceneTransitionKind(at: 1, to: .cut)
        XCTAssertTrue(model.canSplitScene(at: 4.32))

        // Without a camera there is nothing to arrange differently.
        var screenOnly = StudioEditorModel(project: makeProject(camera: false))
        XCTAssertFalse(screenOnly.canSplitScene(at: 4))
        XCTAssertEqual(screenOnly.splitSceneExplanation(at: 4), StudioEditorModel.noCameraForScenesExplanation)
        XCTAssertFalse(screenOnly.splitScene(at: 4).changed)
        XCTAssertEqual(screenOnly.project.scenes.count, 1)
    }

    func testDeletingASceneHandsItsTimeToTheSceneBefore() {
        var model = StudioEditorModel(project: threeScenes())
        XCTAssertEqual(model.removeScene(at: 1), StudioSceneEditResult(changed: true, index: 0))
        XCTAssertEqual(model.project.scenes.map(\.start), [0, 7])
        XCTAssertEqual(model.project.scenes.map(\.layout), [.bubble, .camera])
        model.undo()
        XCTAssertEqual(model.project.scenes.count, 3)

        // The first scene's time goes to the second, which then starts at 0.
        XCTAssertEqual(model.removeScene(at: 0), StudioSceneEditResult(changed: true, index: 0))
        XCTAssertEqual(model.project.scenes.map(\.start), [0, 7])
        XCTAssertEqual(model.project.scenes.map(\.layout), [.sideBySide, .camera])

        XCTAssertEqual(model.removeScene(at: 1), StudioSceneEditResult(changed: true, index: 0))
        XCTAssertEqual(model.project.scenes.map(\.layout), [.sideBySide])

        // The only scene stays, and so does everything when there is no such scene.
        XCTAssertFalse(model.canRemoveScene(at: 0))
        XCTAssertEqual(model.removeScene(at: 0), StudioSceneEditResult(changed: false, index: 0))
        var three = StudioEditorModel(project: threeScenes())
        XCTAssertFalse(three.canRemoveScene(at: 3))
        XCTAssertFalse(three.canRemoveScene(at: -1))
        XCTAssertEqual(three.removeScene(at: 3), StudioSceneEditResult(changed: false, index: 2))
        XCTAssertEqual(three.removeScene(at: -1), StudioSceneEditResult(changed: false, index: 0))
        XCTAssertFalse(three.canUndo)
    }

    func testASceneStartStaysClearOfItsNeighbors() {
        var model = StudioEditorModel(project: threeScenes())
        XCTAssertEqual(model.setSceneStart(at: 1, to: 5), StudioSceneEditResult(changed: true, index: 1))
        XCTAssertEqual(model.project.scenes.map(\.start), [0, 5, 7])

        // No closer than 0.3 s to the start of the scene before, or to its own end.
        model.setSceneStart(at: 1, to: 0.1)
        XCTAssertEqual(model.project.scenes[1].start, 0.3, accuracy: 1e-12)
        model.setSceneStart(at: 1, to: 6.9)
        XCTAssertEqual(model.project.scenes[1].start, 6.7, accuracy: 1e-12)

        // The last scene ends with the recording, 10 s long.
        model.setSceneStart(at: 2, to: 50)
        XCTAssertEqual(model.project.scenes[2].start, 9.7, accuracy: 1e-12)
        model.setSceneStart(at: 2, to: 0)
        XCTAssertEqual(model.project.scenes[2].start, 7.0, accuracy: 1e-12)

        // The first scene starts with the recording; a start that is not a number, the start a
        // scene already has, and a scene that is not there change nothing.
        let before = model.project.scenes
        let steps = model.canUndo
        XCTAssertFalse(model.setSceneStart(at: 0, to: 2).changed)
        XCTAssertFalse(model.setSceneStart(at: 1, to: .nan).changed)
        XCTAssertFalse(model.setSceneStart(at: 2, to: 7.0).changed)
        XCTAssertFalse(model.setSceneStart(at: 3, to: 5).changed)
        XCTAssertEqual(model.project.scenes, before)
        XCTAssertEqual(model.canUndo, steps)

        // A drag is one undo step.
        var dragged = StudioEditorModel(project: threeScenes())
        dragged.beginEditingGroup()
        dragged.setSceneStart(at: 1, to: 4.5)
        dragged.setSceneStart(at: 1, to: 5.5)
        dragged.commitEditingGroup()
        dragged.undo()
        XCTAssertEqual(dragged.project.scenes.map(\.start), [0, 4, 7])
        XCTAssertFalse(dragged.canUndo)

        // Scenes from a file that are closer together than the editor makes them stay where they are.
        var close = makeProject()
        close.scenes = [StudioScene(start: 0), StudioScene(start: 0.2, layout: .camera), StudioScene(start: 0.4, layout: .screen)]
        var closeModel = StudioEditorModel(project: close)
        XCTAssertFalse(closeModel.setSceneStart(at: 1, to: 0.3).changed)
        XCTAssertEqual(closeModel.project.scenes.map(\.start), [0, 0.2, 0.4])
    }

    func testHowASceneIsEntered() {
        var model = StudioEditorModel(project: threeScenes())
        XCTAssertEqual(model.project.scenes[1].transition.kind, .cut)
        XCTAssertEqual(model.sceneTransitionLength(at: 1), 0)

        XCTAssertEqual(model.setSceneTransitionKind(at: 1, to: .morph), StudioSceneEditResult(changed: true, index: 1))
        XCTAssertEqual(model.sceneTransitionLength(at: 1), 0.35, accuracy: 1e-12)
        XCTAssertFalse(model.setSceneTransitionKind(at: 1, to: .morph).changed)

        // Between 0.1 and 2 seconds.
        model.setSceneTransitionDuration(at: 1, to: 1.5)
        XCTAssertEqual(model.project.scenes[1].transition.duration, 1.5, accuracy: 1e-12)
        model.setSceneTransitionDuration(at: 1, to: 9)
        XCTAssertEqual(model.project.scenes[1].transition.duration, 2, accuracy: 1e-12)
        model.setSceneTransitionDuration(at: 1, to: 0)
        XCTAssertEqual(model.project.scenes[1].transition.duration, 0.1, accuracy: 1e-12)
        XCTAssertFalse(model.setSceneTransitionDuration(at: 1, to: .nan).changed)
        XCTAssertEqual(model.project.scenes[1].transition.duration, 0.1, accuracy: 1e-12)

        // The first scene has nothing to move from, and a scene that is not there cannot be changed.
        let before = model.project.scenes
        XCTAssertEqual(model.setSceneTransitionKind(at: 0, to: .morph), StudioSceneEditResult(changed: false, index: 0))
        XCTAssertFalse(model.setSceneTransitionDuration(at: 0, to: 1).changed)
        XCTAssertEqual(model.setSceneTransitionKind(at: 5, to: .morph), StudioSceneEditResult(changed: false, index: 2))
        XCTAssertEqual(model.project.scenes, before)
        XCTAssertEqual(model.sceneTransitionLength(at: 0), 0)

        // A move is never longer than its scene. The second scene lasts from 4 s to 7 s; with the
        // third brought to 5 s it lasts one second, and a move of 2 s takes that one second.
        model.setSceneTransitionDuration(at: 1, to: 2)
        XCTAssertNil(model.sceneMoveLimitedText(at: 1))
        model.setSceneStart(at: 2, to: 5)
        XCTAssertEqual(model.sceneTransitionLength(at: 1), 1, accuracy: 1e-12)
        XCTAssertEqual(model.sceneMoveLimitedText(at: 1), "The scene is shorter than that, so the move takes 1.00 seconds.")
        XCTAssertNil(model.sceneMoveLimitedText(at: 2))
        XCTAssertNil(model.sceneMoveLimitedText(at: 0))
    }

    func testASceneIsLookedAtWhereItHasBeenEntered() {
        var model = StudioEditorModel(project: threeScenes())
        model.setSceneTransitionKind(at: 1, to: .morph)

        // The first scene and a scene that is cut to are whole from their first instant. A scene
        // that is moved into is whole when the move ends, 0.35 s after it starts.
        XCTAssertEqual(model.sceneLookTime(at: 0), 0)
        XCTAssertEqual(model.sceneLookTime(at: 1) ?? -1, 4.35, accuracy: 1e-12)
        XCTAssertEqual(model.sceneLookTime(at: 2), 7)
        XCTAssertNil(model.sceneLookTime(at: 3))

        // A move that takes the whole scene: the last frame of the scene, at 30 frames a second.
        model.setSceneTransitionDuration(at: 1, to: 2)
        model.setSceneStart(at: 2, to: 4.5)
        XCTAssertEqual(model.sceneLookTime(at: 1) ?? -1, 4.5 - 1.0 / 30, accuracy: 1e-12)

        XCTAssertEqual(model.sceneRange(at: 0)?.start, 0)
        XCTAssertEqual(model.sceneRange(at: 0)?.end, 4)
        XCTAssertEqual(model.sceneRange(at: 2)?.end, 10)
        XCTAssertNil(model.sceneRange(at: 3))
    }

    func testSceneTextNamesTheLayoutAndTheTimes() {
        let model = StudioEditorModel(project: threeScenes())
        XCTAssertEqual(model.sceneAccessibilityText(at: 0), "Scene 1 of 3, Screen with camera bubble, 0.0 to 4.0 seconds")
        XCTAssertEqual(model.sceneAccessibilityText(at: 1), "Scene 2 of 3, Side by side, 4.0 to 7.0 seconds")
        XCTAssertEqual(model.sceneAccessibilityText(at: 2), "Scene 3 of 3, Camera only, 7.0 to 10.0 seconds")
        XCTAssertEqual(model.sceneAccessibilityText(at: 3), "")
        XCTAssertEqual(StudioEditorModel.scenePositionText(index: 1, count: 3), "Scene 2 of 3")
        XCTAssertEqual(model.sceneRangeText(at: 1), "4.0 to 7.0 seconds")

        // Without a camera every scene shows the screen alone, whatever layout it stores.
        var project = threeScenes()
        project.sources.camera = nil
        XCTAssertEqual(StudioEditorModel(project: project).sceneAccessibilityText(at: 1), "Scene 2 of 3, Screen only, 4.0 to 7.0 seconds")
    }

    func testTheBubbleHandleIsWhereTheCurrentSceneHasTheBubbleAtRest() {
        // On a 1000×800 canvas the gap is 24 px. The first scene's bubble is 0.24 of 800, 192 px,
        // in the top-left corner. The second's is 0.4 of 800, 320 px, in the bottom-right corner,
        // and is moved into over 2 s.
        var project = makeProject()
        project.scenes = [
            StudioScene(start: 0, layout: .bubble, bubble: StudioBubble(anchor: .topLeft, size: 0.24)),
            StudioScene(start: 4, layout: .bubble, bubble: StudioBubble(anchor: .bottomRight, size: 0.4), transition: StudioTransition(kind: .morph, duration: 2)),
        ]
        var model = StudioEditorModel(project: project)
        let canvas = CGSize(width: 1000, height: 800)

        model.sceneTime = 1
        assertRect(model.bubbleRect(canvasSize: canvas), x: 24, y: 24, width: 192, height: 192)

        // A second into the move the picture has the bubble on its way; the handle is where the
        // scene has it once it is there, because that is what dragging changes.
        model.sceneTime = 5
        assertRect(model.bubbleRect(canvasSize: canvas), x: 656, y: 456, width: 320, height: 320)

        model.moveBubble(topLeft: CGPoint(x: 100, y: 456), canvasSize: canvas)
        assertRect(model.bubbleRect(canvasSize: canvas), x: 100, y: 456, width: 320, height: 320)
        XCTAssertEqual(model.project.scenes[1].bubble.anchor, .bottomLeft)
        XCTAssertEqual(model.project.scenes[0], project.scenes[0])
    }

    // MARK: - Cuts
    //
    // The same cases, with the same numbers, are in the Windows StudioEditorModelCutTests.

    func testACutStartsAtATimeAndLastsOneSecondOrUntilTheNextOne() {
        var model = StudioEditorModel(project: makeProject())
        XCTAssertEqual(model.addCut(at: 4), StudioCutEditResult(changed: true, index: 0))
        assertCuts(model, [(4, 5)])
        XCTAssertEqual(model.outputDuration, 9, accuracy: 1e-9)
        XCTAssertTrue(model.canUndo)

        // Where a cut already is, that one is the answer and nothing changes.
        XCTAssertEqual(model.addCut(at: 4.5), StudioCutEditResult(changed: false, index: 0))
        assertCuts(model, [(4, 5)])

        // The list stays in time order, and a new cut ends where the next one starts.
        XCTAssertEqual(model.addCut(at: 2), StudioCutEditResult(changed: true, index: 0))
        XCTAssertEqual(model.addCut(at: 3.5), StudioCutEditResult(changed: true, index: 1))
        assertCuts(model, [(2, 3), (3.5, 4), (4, 5)])
        XCTAssertEqual(model.addCut(at: 3.95), StudioCutEditResult(changed: false, index: 1))

        // A cut contains its start and not its end, so one can start where another ends.
        XCTAssertEqual(model.addCut(at: 3), StudioCutEditResult(changed: true, index: 1))
        assertCuts(model, [(2, 3), (3, 3.5), (3.5, 4), (4, 5)])

        // Less than 0.1 s before the end of the recording there is no room.
        XCTAssertEqual(model.addCut(at: 9.95), StudioCutEditResult(changed: false, index: nil))
        XCTAssertFalse(model.canAddCut(at: 9.95))
        XCTAssertTrue(model.canAddCut(at: 4.2))
        XCTAssertTrue(model.canAddCut(at: 6))
        XCTAssertEqual(model.addCut(at: .nan), StudioCutEditResult(changed: false, index: nil))
        XCTAssertFalse(model.canAddCut(at: .nan))

        // A time before the recording is its start.
        XCTAssertEqual(model.addCut(at: -3), StudioCutEditResult(changed: true, index: 0))
        XCTAssertEqual(model.project.edits.cuts[0].start, 0, accuracy: 1e-9)
        XCTAssertEqual(model.project.edits.cuts[0].end, 1, accuracy: 1e-9)
    }

    func testACutOrATrimThatWouldLeaveNoVideoIsNotMade() {
        // A recording barely longer than a new cut.
        var brief = StudioEditorModel(project: makeProject(duration: 1.05))
        XCTAssertFalse(brief.canAddCut(at: 0))
        XCTAssertEqual(brief.addCut(at: 0), StudioCutEditResult(changed: false, index: nil))
        XCTAssertTrue(brief.project.edits.cuts.isEmpty)
        XCTAssertFalse(brief.canUndo)

        // What counts is what the trim keeps: here the second from 4 to 5.
        var model = StudioEditorModel(project: makeProject())
        model.setTrim(start: 4, end: 5)
        XCTAssertFalse(model.canAddCut(at: 4))
        XCTAssertEqual(model.addCut(at: 4), StudioCutEditResult(changed: false, index: nil))
        XCTAssertEqual(model.addCut(at: 4.5), StudioCutEditResult(changed: true, index: 0))
        XCTAssertEqual(model.outputDuration, 0.5, accuracy: 1e-9)

        // The start of the cut can come down to 4.1, which leaves 0.1 s, and no further.
        XCTAssertEqual(model.setCutStart(at: 0, to: 4.05), StudioCutEditResult(changed: false, index: 0))
        XCTAssertEqual(model.project.edits.cuts[0].start, 4.5, accuracy: 1e-9)
        XCTAssertEqual(model.setCutStart(at: 0, to: 4.1), StudioCutEditResult(changed: true, index: 0))
        XCTAssertEqual(model.outputDuration, 0.1, accuracy: 1e-9)

        // Nor can the trim take the rest away.
        model.setTrimStart(4.05)
        XCTAssertEqual(model.trimStart, 4, accuracy: 1e-9)
        model.setTrimStart(3)
        XCTAssertEqual(model.trimStart, 3, accuracy: 1e-9)
        XCTAssertEqual(model.outputDuration, 1.1, accuracy: 1e-9)

        // Moving a cut onto all that is left is not made either.
        var moved = StudioEditorModel(project: withCuts([(0, 2)], duration: 3))
        XCTAssertEqual(moved.setCutEnd(at: 0, to: 2.95), StudioCutEditResult(changed: false, index: 0))
        XCTAssertEqual(moved.setCutEnd(at: 0, to: 2.9), StudioCutEditResult(changed: true, index: 0))
    }

    func testTheEndsOfACutStayClearOfItsNeighbors() {
        var model = StudioEditorModel(project: threeCuts())

        XCTAssertEqual(model.setCutStart(at: 1, to: 4), StudioCutEditResult(changed: true, index: 1))
        assertCuts(model, [(2, 3), (4, 6), (8, 9)])

        // The start stops at the end of the cut before, and 0.1 s before its own end.
        model.setCutStart(at: 1, to: 1)
        assertCuts(model, [(2, 3), (3, 6), (8, 9)])
        model.setCutStart(at: 1, to: 5.95)
        assertCuts(model, [(2, 3), (5.9, 6), (8, 9)])

        // The end stops at the start of the next cut, and 0.1 s after its own start.
        XCTAssertEqual(model.setCutEnd(at: 1, to: 7), StudioCutEditResult(changed: true, index: 1))
        model.setCutEnd(at: 1, to: 9.5)
        assertCuts(model, [(2, 3), (5.9, 8), (8, 9)])
        model.setCutEnd(at: 1, to: 5)
        assertCuts(model, [(2, 3), (5.9, 6), (8, 9)])

        // The first and the last stop at the ends of the recording.
        model.setCutStart(at: 0, to: -1)
        model.setCutEnd(at: 2, to: 12)
        assertCuts(model, [(0, 3), (5.9, 6), (8, 10)])

        // Nothing to change, nothing that is a number, no such cut.
        let depth = undoDepth(&model)
        XCTAssertEqual(model.setCutStart(at: 1, to: 5.9), StudioCutEditResult(changed: false, index: 1))
        XCTAssertEqual(model.setCutEnd(at: 1, to: .nan), StudioCutEditResult(changed: false, index: 1))
        XCTAssertEqual(model.setCutStart(at: 1, to: .infinity), StudioCutEditResult(changed: false, index: 1))
        XCTAssertEqual(model.setCutStart(at: 3, to: 1), StudioCutEditResult(changed: false, index: nil))
        XCTAssertEqual(model.setCutEnd(at: -1, to: 1), StudioCutEditResult(changed: false, index: nil))
        XCTAssertEqual(undoDepth(&model), depth)

        // A cut from a file that is shorter than 0.1 s and sits against the one before: the cut
        // before decides, so its start does not move into it.
        var tight = StudioEditorModel(project: withCuts([(2, 3), (3, 3.05)]))
        XCTAssertEqual(tight.setCutStart(at: 1, to: 2.5), StudioCutEditResult(changed: false, index: 1))
        assertCuts(tight, [(2, 3), (3, 3.05)])
    }

    func testMovingACutKeepsItsLengthBetweenItsNeighbors() {
        var model = StudioEditorModel(project: threeCuts())

        XCTAssertEqual(model.moveCut(at: 1, to: 6.5), StudioCutEditResult(changed: true, index: 1))
        assertCuts(model, [(2, 3), (6.5, 7.5), (8, 9)])

        // Against the next cut, and against the one before.
        model.moveCut(at: 1, to: 7.8)
        assertCuts(model, [(2, 3), (7, 8), (8, 9)])
        model.moveCut(at: 1, to: 0)
        assertCuts(model, [(2, 3), (3, 4), (8, 9)])

        // Against the ends of the recording.
        model.moveCut(at: 0, to: -5)
        model.moveCut(at: 2, to: 20)
        assertCuts(model, [(0, 1), (3, 4), (9, 10)])

        XCTAssertEqual(model.moveCut(at: 1, to: 3), StudioCutEditResult(changed: false, index: 1))
        XCTAssertEqual(model.moveCut(at: 1, to: .nan), StudioCutEditResult(changed: false, index: 1))
        XCTAssertEqual(model.moveCut(at: 3, to: 1), StudioCutEditResult(changed: false, index: nil))
    }

    func testDeletingACutPutsItsStretchBack() {
        var model = StudioEditorModel(project: threeCuts())
        XCTAssertEqual(model.outputDuration, 7, accuracy: 1e-9)

        XCTAssertEqual(model.removeCut(at: 1), StudioCutEditResult(changed: true, index: nil))
        assertCuts(model, [(2, 3), (8, 9)])
        XCTAssertEqual(model.outputDuration, 8, accuracy: 1e-9)

        model.undo()
        assertCuts(model, [(2, 3), (5, 6), (8, 9)])
        model.redo()
        assertCuts(model, [(2, 3), (8, 9)])

        XCTAssertEqual(model.removeCut(at: 2), StudioCutEditResult(changed: false, index: nil))
        XCTAssertEqual(model.removeCut(at: -1), StudioCutEditResult(changed: false, index: nil))
    }

    func testOpeningPutsCutsInOrderAndJoinsThoseThatOverlap() {
        let model = StudioEditorModel(project: withCuts([
            (5, 6), (2, 3), (2.5, 4), (9, 12), (11, 11.5), (.nan, 3), (7, 6.5), (4, 4.5), (5.2, 5.5),
            (-.infinity, 1),
        ]))

        // Two that overlap are one, and so is one inside another. Two that touch stay two, and
        // play as one. One that reaches past the recording stops at its ends, and one that is not
        // a number is dropped.
        assertCuts(model, [(0, 1), (2, 4), (4, 4.5), (5, 6), (9, 10)])
        XCTAssertFalse(model.canUndo)
        XCTAssertEqual(model.outputDuration, 4.5, accuracy: 1e-9)
    }

    func testPlaybackJumpsOverCutsAndEndsWhereTheVideoDoes() {
        let model = StudioEditorModel(project: withCuts([(2, 3), (9, 10)]))

        // A cut that runs up to the end of the recording ends the video where it starts.
        XCTAssertEqual(model.playbackStart, 0, accuracy: 1e-9)
        XCTAssertEqual(model.playbackEnd, 9, accuracy: 1e-9)
        XCTAssertFalse(model.isAtPlaybackEnd(8.99))
        XCTAssertTrue(model.isAtPlaybackEnd(9))

        // Inside a cut the next thing to show is its end. A cut contains its start and not its end.
        XCTAssertNil(model.cutSkipTarget(at: 1.99))
        XCTAssertEqual(model.cutSkipTarget(at: 2) ?? -1, 3, accuracy: 1e-9)
        XCTAssertEqual(model.cutSkipTarget(at: 2.5) ?? -1, 3, accuracy: 1e-9)
        XCTAssertNil(model.cutSkipTarget(at: 3))
        XCTAssertNil(model.cutSkipTarget(at: 9.5))

        // Play starts where the playhead is, after the cut it is in, or over again at the end.
        XCTAssertEqual(model.playbackStart(from: 1), 1, accuracy: 1e-9)
        XCTAssertEqual(model.playbackStart(from: 2.5), 3, accuracy: 1e-9)
        XCTAssertEqual(model.playbackStart(from: 8.99), 0, accuracy: 1e-9)
        XCTAssertEqual(model.playbackStart(from: 9.4), 0, accuracy: 1e-9)

        // A cut at the start of the recording starts the video at its end.
        let late = StudioEditorModel(project: withCuts([(0, 1.5)]))
        XCTAssertEqual(late.playbackStart, 1.5, accuracy: 1e-9)
        XCTAssertEqual(late.playbackStart(from: 0.5), 1.5, accuracy: 1e-9)
        XCTAssertNil(late.cutSkipTarget(at: 0.5))

        // Cuts count inside the trim only.
        var trimmed = StudioEditorModel(project: withCuts([(0.5, 2), (7, 9)]))
        trimmed.setTrim(start: 1, end: 8)
        XCTAssertEqual(trimmed.playbackStart, 2, accuracy: 1e-9)
        XCTAssertEqual(trimmed.playbackEnd, 7, accuracy: 1e-9)
        XCTAssertEqual(trimmed.outputDuration, 5, accuracy: 1e-9)
        XCTAssertEqual(trimmed.outputTime(forSourceTime: 4), 2, accuracy: 1e-9)
        XCTAssertNil(trimmed.cutSkipTarget(at: 1.5))
        XCTAssertNil(trimmed.cutSkipTarget(at: 7.5))

        // Two cuts that touch are jumped as one.
        let touching = StudioEditorModel(project: withCuts([(2, 3), (3, 4)]))
        XCTAssertEqual(touching.cutSkipTarget(at: 2.2) ?? -1, 4, accuracy: 1e-9)

        // A file in which the cuts leave nothing: the video starts and ends in one place.
        let nothing = StudioEditorModel(project: withCuts([(0, 10)]))
        XCTAssertEqual(nothing.outputDuration, 0, accuracy: 1e-9)
        XCTAssertEqual(nothing.playbackStart, 0, accuracy: 1e-9)
        XCTAssertEqual(nothing.playbackEnd, 0, accuracy: 1e-9)
        XCTAssertTrue(nothing.isAtPlaybackEnd(0))
    }

    func testWithoutCutsPlaybackStartsAndEndsWithTheTrim() {
        var model = StudioEditorModel(project: makeProject())
        model.setTrim(start: 2, end: 8)
        XCTAssertEqual(model.playbackStart, 2, accuracy: 1e-9)
        XCTAssertEqual(model.playbackEnd, 8, accuracy: 1e-9)
        XCTAssertEqual(model.playbackStart(from: 1), 2, accuracy: 1e-9)
        XCTAssertEqual(model.playbackStart(from: 5), 5, accuracy: 1e-9)
        XCTAssertEqual(model.playbackStart(from: 8), 2, accuracy: 1e-9)
        XCTAssertTrue(model.isAtPlaybackEnd(8))
        XCTAssertFalse(model.isAtPlaybackEnd(7.9))
        XCTAssertNil(model.cutSkipTarget(at: 5))
        XCTAssertNil(model.cutIndex(at: 5))
    }

    func testSteppingThroughCutsAndFollowingOneThroughAnEditThatDidNotSay() {
        let model = StudioEditorModel(project: threeCuts())
        XCTAssertEqual(model.cutIndex(at: 5), 1)
        XCTAssertEqual(model.cutIndex(at: 5.99), 1)
        XCTAssertNil(model.cutIndex(at: 6))
        XCTAssertNil(model.cutIndex(at: 4.99))

        // With nothing selected: the cut at the playhead, or the nearest one on that side.
        XCTAssertEqual(model.cutIndex(after: nil, playhead: 0), 0)
        XCTAssertEqual(model.cutIndex(after: nil, playhead: 2.5), 0)
        XCTAssertEqual(model.cutIndex(after: nil, playhead: 3), 1)
        XCTAssertNil(model.cutIndex(after: nil, playhead: 9))
        XCTAssertEqual(model.cutIndex(before: nil, playhead: 10), 2)
        XCTAssertEqual(model.cutIndex(before: nil, playhead: 5.5), 1)
        XCTAssertNil(model.cutIndex(before: nil, playhead: 1))

        // With one selected: its neighbors.
        XCTAssertEqual(model.cutIndex(after: 0, playhead: 9), 1)
        XCTAssertNil(model.cutIndex(after: 2, playhead: 0))
        XCTAssertEqual(model.cutIndex(before: 1, playhead: 9), 0)
        XCTAssertNil(model.cutIndex(before: 0, playhead: 9))

        let before = [cut(2, 3), cut(5, 6)]

        // Only that cut differs: it is the same cut, changed.
        XCTAssertEqual(StudioEditorModel.cutIndex(following: 1, from: before, to: [cut(2, 3), cut(5.5, 7)]), 1)

        // Otherwise the one that shares the most time with it, or none.
        XCTAssertEqual(StudioEditorModel.cutIndex(following: 1, from: before, to: [cut(5, 6)]), 0)
        XCTAssertEqual(
            StudioEditorModel.cutIndex(following: 1, from: before, to: [cut(1, 2), cut(4.5, 5.2), cut(5.2, 6.5)]),
            2
        )
        XCTAssertEqual(StudioEditorModel.cutIndex(following: 1, from: before, to: [cut(5.2, 6.5), cut(8, 9)]), 0)
        XCTAssertNil(StudioEditorModel.cutIndex(following: 0, from: before, to: [cut(5, 6)]))
        XCTAssertNil(StudioEditorModel.cutIndex(following: 2, from: before, to: before))
        XCTAssertNil(StudioEditorModel.cutIndex(following: -1, from: before, to: before))
    }

    func testACutMovesNothingElse() {
        var project = makeProject()
        project.scenes = [StudioScene(start: 0, layout: .bubble), StudioScene(start: 4, layout: .sideBySide)]
        project.zooms = [zoom(3, 6)]
        var model = StudioEditorModel(project: project)
        let scenes = model.project.scenes
        let zooms = model.project.zooms

        XCTAssertTrue(model.addCut(at: 3.5).changed)

        XCTAssertEqual(model.project.scenes, scenes)
        XCTAssertEqual(model.project.zooms, zooms)
        XCTAssertEqual(model.trimStart, 0, accuracy: 1e-9)
        XCTAssertEqual(model.trimEnd, 10, accuracy: 1e-9)

        // The video is a second shorter, and what came after the cut is a second earlier in it.
        XCTAssertEqual(model.outputDuration, 9, accuracy: 1e-9)
        XCTAssertEqual(model.outputTime(forSourceTime: 8), 7, accuracy: 1e-9)
        XCTAssertEqual(model.outputTime(forSourceTime: 4), 3.5, accuracy: 1e-9)
    }

    func testCutTextNamesTheTimes() {
        XCTAssertEqual(StudioEditorModel.cutAccessibilityText(cut(12, 16.5)), "Cut, 12.0 to 16.5 seconds")
        XCTAssertEqual(StudioEditorModel.cutRangeText(cut(12, 16.5)), "12.0 to 16.5 seconds")
        XCTAssertEqual(StudioEditorModel.cutLengthText(cut(12, 16.5)), "4.5 seconds long")
        XCTAssertEqual(StudioEditorModel.cutLengthText(cut(3, .nan)), "0.0 seconds long")
        XCTAssertEqual(StudioEditorModel.cutPositionText(index: 1, count: 3), "Cut 2 of 3")
    }

    private func assertCuts(_ model: StudioEditorModel, _ expected: [(Double, Double)], line: UInt = #line) {
        let cuts = model.project.edits.cuts
        XCTAssertEqual(cuts.count, expected.count, "count", line: line)
        for (index, range) in expected.enumerated() where index < cuts.count {
            XCTAssertEqual(cuts[index].start, range.0, accuracy: 1e-9, "start of cut \(index)", line: line)
            XCTAssertEqual(cuts[index].end, range.1, accuracy: 1e-9, "end of cut \(index)", line: line)
        }
    }

    /// How many times Undo can be pressed. Everything undone is redone before it returns.
    private func undoDepth(_ model: inout StudioEditorModel) -> Int {
        var depth = 0
        while model.canUndo {
            model.undo()
            depth += 1
        }
        for _ in 0..<depth {
            model.redo()
        }
        return depth
    }

    private func cut(_ start: Double, _ end: Double) -> StudioTimeRange {
        StudioTimeRange(start: start, end: end)
    }

    /// Cuts from 2 to 3, 5 to 6 and 8 to 9 seconds, in a recording 10 s long.
    private func threeCuts() -> StudioProject {
        withCuts([(2, 3), (5, 6), (8, 9)])
    }

    private func withCuts(_ cuts: [(Double, Double)], duration: Double = 10) -> StudioProject {
        var project = makeProject(duration: duration)
        project.edits = StudioEdits(cuts: cuts.map { cut($0.0, $0.1) })
        return project
    }

    /// The bubble until 4 s, side by side until 7 s, then the camera alone, in a recording 10 s long.
    private func threeScenes() -> StudioProject {
        var project = makeProject()
        project.scenes = [
            StudioScene(start: 0, layout: .bubble),
            StudioScene(start: 4, layout: .sideBySide),
            StudioScene(start: 7, layout: .camera),
        ]
        return project
    }

    private func makeProject(duration: Double = 10, camera: Bool = true) -> StudioProject {
        StudioProject(
            id: "3f0013cf-ba10-4453-af91-792b7882dae6",
            sources: StudioSources(
                screen: StudioScreenSource(width: 1920, height: 1080, frameRate: 30, duration: duration),
                camera: camera ? StudioCameraSource(width: 640, height: 480, duration: duration) : nil
            )
        )
    }

    private func zoom(
        _ start: Double,
        _ end: Double,
        scale: Double = 2,
        mode: StudioZoomFocusMode = .point,
        x: Double = 0.5,
        y: Double = 0.5,
        easeIn: Double = 0.5,
        easeOut: Double = 0.5,
        origin: StudioZoomOrigin = .manual
    ) -> StudioZoom {
        StudioZoom(
            start: start,
            end: end,
            scale: scale,
            focus: StudioZoomFocus(mode: mode, x: x, y: y),
            easeIn: easeIn,
            easeOut: easeOut,
            origin: origin
        )
    }

    private func assertRect(_ rect: StudioRect?, x: Double, y: Double, width: Double, height: Double, line: UInt = #line) {
        guard let rect = rect else {
            XCTFail("Expected a rectangle", line: line)
            return
        }
        XCTAssertEqual(rect.x, x, accuracy: 1e-9, "x", line: line)
        XCTAssertEqual(rect.y, y, accuracy: 1e-9, "y", line: line)
        XCTAssertEqual(rect.width, width, accuracy: 1e-9, "width", line: line)
        XCTAssertEqual(rect.height, height, accuracy: 1e-9, "height", line: line)
    }

    private func resolvedCamera(_ project: StudioProject, canvas: CGSize) -> StudioRect? {
        StudioLayoutResolver.resolve(
            project: project,
            time: 0,
            canvasWidth: Double(canvas.width),
            canvasHeight: Double(canvas.height)
        ).camera?.rect
    }
}