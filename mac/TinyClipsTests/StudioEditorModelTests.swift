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

    func testLayoutsThatNeedACameraAreIgnoredWithoutOne() {
        var model = StudioEditorModel(project: makeProject(camera: false))
        XCTAssertFalse(model.hasCamera)
        XCTAssertEqual(model.effectiveLayout, .screen)

        model.setLayout(.sideBySide)
        XCTAssertFalse(model.canUndo)
        XCTAssertEqual(model.effectiveLayout, .screen)

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

    private func resolvedCamera(_ project: StudioProject, canvas: CGSize) -> StudioRect? {
        StudioLayoutResolver.resolve(
            project: project,
            time: 0,
            canvasWidth: Double(canvas.width),
            canvasHeight: Double(canvas.height)
        ).camera?.rect
    }
}