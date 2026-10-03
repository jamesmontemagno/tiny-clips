import XCTest
@testable import TinyClips

final class StudioLayoutTests: XCTestCase {
    func testCanvasMathAutoCropAndExportLimit() {
        var project = makeProject()
        project.screen.crop = StudioRect(x: 0.1, y: 0.1, width: 0.5, height: 0.5)

        let natural = StudioCanvasMath.naturalCanvas(project: project)
        let export = StudioCanvasMath.exportSize(project: project, longSideLimit: 500)

        XCTAssertEqual(natural.width, 960)
        XCTAssertEqual(natural.height, 540)
        XCTAssertEqual(export.width, 500)
        XCTAssertEqual(export.height, 282)
    }

    func testCanvasMathAspectPresetsUseEvenSizes() {
        var project = makeProject(width: 1001, height: 500)
        project.canvas.aspect = .square

        let natural = StudioCanvasMath.naturalCanvas(project: project)

        XCTAssertEqual(natural.width, 1002)
        XCTAssertEqual(natural.height, 1002)
    }

    func testScreenLayoutFitsScreenInsideContentArea() {
        var project = makeProject(camera: nil)
        project.canvas.padding = 0.1
        project.scenes = [StudioScene(layout: .bubble)]

        let frame = StudioLayoutResolver.resolve(project: project, time: 0, canvasWidth: 1000, canvasHeight: 800)

        XCTAssertEqual(frame.layout, .screen)
        XCTAssertNil(frame.camera)
        // Padding is a fraction of the short side (800), so the content area is inset by 80.
        XCTAssertEqual(frame.screen?.rect.x ?? -1, 80, accuracy: 1e-6)
        XCTAssertEqual(frame.screen?.rect.y ?? -1, 163.75, accuracy: 1e-6)
        XCTAssertEqual(frame.screen?.rect.width ?? -1, 840, accuracy: 1e-6)
        XCTAssertEqual(frame.screen?.rect.height ?? -1, 472.5, accuracy: 1e-6)
    }

    func testBubbleLayoutPlacesCameraAtAnchoredCornerWithOffsetsAndTiming() {
        var project = makeProject()
        project.scenes = [
            StudioScene(
                layout: .bubble,
                bubble: StudioBubble(anchor: .topLeft, size: 0.25, offsetX: 0.1, offsetY: 0.1)
            )
        ]

        let frame = StudioLayoutResolver.resolve(project: project, time: 0.5, canvasWidth: 1000, canvasHeight: 800)

        XCTAssertEqual(frame.layout, .bubble)
        XCTAssertEqual(frame.camera?.rect.x ?? -1, 124, accuracy: 1e-6)
        XCTAssertEqual(frame.camera?.rect.y ?? -1, 104, accuracy: 1e-6)
        XCTAssertEqual(frame.camera?.rect.width ?? -1, 200, accuracy: 1e-6)
        XCTAssertEqual(frame.camera?.rect.height ?? -1, 200, accuracy: 1e-6)
        XCTAssertEqual(frame.camera?.sourceTime ?? -1, 0.25, accuracy: 1e-6)
        XCTAssertTrue(frame.camera?.visible ?? false)
        XCTAssertEqual(frame.camera?.shape, .circle)
    }

    func testSideBySideHorizontalAndVerticalLayouts() {
        var project = makeProject()
        project.scenes = [StudioScene(layout: .sideBySide, split: StudioSplit(cameraSide: .leading, cameraFraction: 0.25))]

        let horizontal = StudioLayoutResolver.resolve(project: project, time: 1, canvasWidth: 1000, canvasHeight: 800)
        XCTAssertEqual(horizontal.layout, .sideBySide)
        XCTAssertLessThan(horizontal.camera?.rect.x ?? 1_000, horizontal.screen?.rect.x ?? 0)
        XCTAssertEqual(horizontal.camera?.rect.height ?? -1, horizontal.screen?.rect.height ?? -2, accuracy: 1e-6)

        let vertical = StudioLayoutResolver.resolve(project: project, time: 1, canvasWidth: 800, canvasHeight: 1000)
        XCTAssertLessThan(vertical.camera?.rect.y ?? 1_000, vertical.screen?.rect.y ?? 0)
        XCTAssertEqual(vertical.camera?.rect.width ?? -1, vertical.screen?.rect.width ?? -2, accuracy: 1e-6)
    }

    func testCameraLayoutUsesWholeContentAreaAndCardShape() {
        var project = makeProject()
        project.canvas.padding = 0.05
        project.scenes = [StudioScene(layout: .camera)]

        let frame = StudioLayoutResolver.resolve(project: project, time: -1, canvasWidth: 1000, canvasHeight: 800)

        XCTAssertNil(frame.screen)
        XCTAssertEqual(frame.camera?.rect, StudioRect(x: 40, y: 40, width: 920, height: 720))
        XCTAssertEqual(frame.camera?.shape, .roundedRectangle)
        XCTAssertFalse(frame.camera?.visible ?? true)
        XCTAssertEqual(frame.camera?.sourceTime ?? -1, 0, accuracy: 1e-6)
    }

    func testSceneNormalizationSelectsLastSceneAtSameStart() {
        var project = makeProject()
        project.scenes = [
            StudioScene(start: 5, layout: .screen),
            StudioScene(start: -1, layout: .bubble),
            StudioScene(start: 5, layout: .camera)
        ]

        let early = StudioLayoutResolver.resolve(project: project, time: -1, canvasWidth: 1000, canvasHeight: 800)
        let later = StudioLayoutResolver.resolve(project: project, time: 5, canvasWidth: 1000, canvasHeight: 800)

        XCTAssertEqual(early.layout, .bubble)
        XCTAssertEqual(early.sceneIndex, 0)
        XCTAssertEqual(later.layout, .camera)
        // sceneIndex counts positions in the normalized list: [bubble at 0, camera at 5].
        XCTAssertEqual(later.sceneIndex, 1)
    }

    func testTimeMapTrimCutsAndInverseMapping() {
        let edits = StudioEdits(
            trimStart: 2,
            trimEnd: 18,
            cuts: [
                StudioTimeRange(start: 5, end: 7),
                StudioTimeRange(start: 7, end: 9),
                StudioTimeRange(start: -1, end: 1)
            ]
        )
        let map = StudioTimeMap(sourceDuration: 20, edits: edits)

        XCTAssertEqual(map.segments, [
            StudioTimeSegment(start: 2, end: 5),
            StudioTimeSegment(start: 9, end: 18)
        ])
        XCTAssertEqual(map.outputDuration, 12, accuracy: 1e-6)
        XCTAssertEqual(map.sourceToOutput(1), 0, accuracy: 1e-6)
        XCTAssertEqual(map.sourceToOutput(6), 3, accuracy: 1e-6)
        XCTAssertEqual(map.sourceToOutput(10), 4, accuracy: 1e-6)
        XCTAssertEqual(map.outputToSource(3), 9, accuracy: 1e-6)
        XCTAssertEqual(map.outputToSource(12), 18, accuracy: 1e-6)
    }

    func testTimeMapAllCutMapsToTrimStartAndZeroDuration() {
        let map = StudioTimeMap(
            sourceDuration: 10,
            edits: StudioEdits(trimStart: 2, trimEnd: 8, cuts: [StudioTimeRange(start: 0, end: 10)])
        )

        XCTAssertTrue(map.segments.isEmpty)
        XCTAssertEqual(map.outputDuration, 0)
        XCTAssertEqual(map.sourceToOutput(5), 0)
        XCTAssertEqual(map.outputToSource(1), 2)
    }

    // MARK: - Helpers

    private func makeProject(width: Int = 1920, height: Int = 1080, camera: StudioCameraSource? = StudioCameraSource(width: 640, height: 480, duration: 8, startOffset: 0.25)) -> StudioProject {
        StudioProject(
            id: "layout",
            sources: StudioSources(
                screen: StudioScreenSource(width: width, height: height, duration: 10),
                camera: camera
            )
        )
    }
}
