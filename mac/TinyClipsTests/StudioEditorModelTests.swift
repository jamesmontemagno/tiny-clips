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

    // MARK: - Helpers

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