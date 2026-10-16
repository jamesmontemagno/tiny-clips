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
        XCTAssertTrue(map.pieces.isEmpty)
        XCTAssertEqual(map.outputDuration, 0)
        XCTAssertEqual(map.sourceToOutput(5), 0)
        XCTAssertEqual(map.outputToSource(1), 2)
        XCTAssertEqual(map.rate(at: 5), 1)
    }

    func testTimeMapWithoutSpeedThePiecesAreTheKeptSegmentsAtRateOne() {
        let map = StudioTimeMap(
            sourceDuration: 20,
            edits: StudioEdits(trimStart: 2, trimEnd: 18, cuts: [StudioTimeRange(start: 5, end: 8)])
        )

        XCTAssertEqual(map.pieces, [
            StudioTimePiece(start: 2, end: 5, rate: 1),
            StudioTimePiece(start: 8, end: 18, rate: 1)
        ])
        XCTAssertEqual(map.pieces[0].outputDuration, 3)
        XCTAssertEqual(map.rate(at: 4), 1)
        XCTAssertEqual(map.rate(at: 6), 1)
    }

    func testTimeMapSpeedDividesWhatIsKeptAndChangesHowLongItTakes() {
        // Four times as fast from 4 to 12, with a cut inside it, and half as fast from 14 to 16.
        let map = StudioTimeMap(
            sourceDuration: 20,
            edits: StudioEdits(
                cuts: [StudioTimeRange(start: 6, end: 8)],
                speed: [
                    StudioSpeedRange(start: 14, end: 16, rate: 0.5),
                    StudioSpeedRange(start: 4, end: 12, rate: 4)
                ]
            )
        )

        XCTAssertEqual(map.segments, [
            StudioTimeSegment(start: 0, end: 6),
            StudioTimeSegment(start: 8, end: 20)
        ])
        XCTAssertEqual(map.pieces, [
            StudioTimePiece(start: 0, end: 4, rate: 1),
            StudioTimePiece(start: 4, end: 6, rate: 4),
            StudioTimePiece(start: 8, end: 12, rate: 4),
            StudioTimePiece(start: 12, end: 14, rate: 1),
            StudioTimePiece(start: 14, end: 16, rate: 0.5),
            StudioTimePiece(start: 16, end: 20, rate: 1)
        ])

        // 4 + 0.5 + 1 + 2 + 4 + 4.
        XCTAssertEqual(map.outputDuration, 15.5, accuracy: 1e-6)
        XCTAssertEqual(map.sourceToOutput(5), 4.25, accuracy: 1e-6)
        XCTAssertEqual(map.sourceToOutput(7), 4.5, accuracy: 1e-6)
        XCTAssertEqual(map.sourceToOutput(10), 5, accuracy: 1e-6)
        XCTAssertEqual(map.sourceToOutput(15), 9.5, accuracy: 1e-6)
        XCTAssertEqual(map.outputToSource(4.25), 5, accuracy: 1e-6)
        XCTAssertEqual(map.outputToSource(4.5), 8, accuracy: 1e-6)
        XCTAssertEqual(map.outputToSource(9.5), 15, accuracy: 1e-6)
        XCTAssertEqual(map.outputToSource(15.5), 20, accuracy: 1e-6)

        // The rate at a time: of the piece it is in, and 1 inside a cut and outside everything.
        XCTAssertEqual(map.rate(at: 3.999), 1)
        XCTAssertEqual(map.rate(at: 4), 4)
        XCTAssertEqual(map.rate(at: 7), 1)
        XCTAssertEqual(map.rate(at: 11.999), 4)
        XCTAssertEqual(map.rate(at: 12), 1)
        XCTAssertEqual(map.rate(at: 15), 0.5)
        XCTAssertEqual(map.rate(at: -1), 1)
        XCTAssertEqual(map.rate(at: 25), 1)
    }

    func testTimeMapSpeedKeepsRatesWithinItsLimitsAndLeavesOutWhatIsNoRate() {
        let map = StudioTimeMap(
            sourceDuration: 40,
            edits: StudioEdits(
                speed: [
                    StudioSpeedRange(start: 2, end: 4, rate: 100),
                    StudioSpeedRange(start: 6, end: 8, rate: 0.01),
                    StudioSpeedRange(start: 10, end: 12, rate: 1),
                    StudioSpeedRange(start: 14, end: 16, rate: 0),
                    StudioSpeedRange(start: 18, end: 20, rate: -2),
                    StudioSpeedRange(start: 22, end: 24, rate: .nan),
                    StudioSpeedRange(start: 26, end: 28, rate: .infinity),
                    StudioSpeedRange(start: 32, end: 30, rate: 2)
                ]
            )
        )

        XCTAssertEqual(map.pieces, [
            StudioTimePiece(start: 0, end: 2, rate: 1),
            StudioTimePiece(start: 2, end: 4, rate: StudioTimeMap.fastestRate),
            StudioTimePiece(start: 4, end: 6, rate: 1),
            StudioTimePiece(start: 6, end: 8, rate: StudioTimeMap.slowestRate),
            StudioTimePiece(start: 8, end: 40, rate: 1)
        ])
        XCTAssertEqual(StudioTimeMap.fastestRate, 8)
        XCTAssertEqual(StudioTimeMap.slowestRate, 0.25)
    }

    // MARK: - Zooms and Cursor Samples

    func testPreparedCursorSamplesGiveThePlainSumOfTheSpecForAHundredThousandSamples() {
        // Stored newest first, so they also have to be put in order.
        var samples: [StudioCursorSample] = []
        samples.reserveCapacity(100_000)
        for index in (0..<100_000).reversed() {
            samples.append(StudioCursorSample(
                t: Double(index) / 60,
                x: Double(index % 97) / 96,
                y: 1 - Double(index % 89) / 88
            ))
        }
        let prepared = StudioPreparedCursorSamples(samples)

        XCTAssertEqual(prepared.samples.count, 100_000)
        for time in [-10, 0, 0.125, 0.5, 3.25, 18.75, 123.456, 999.9, 1600.25, 1666.15, 1666.65, 2000] {
            let actual = prepared.focus(at: time)
            let expected = plainCursorFocus(samples: samples, time: time)
            XCTAssertEqual(actual?.x ?? -1, expected.x, accuracy: 1e-12, "x at \(time)")
            XCTAssertEqual(actual?.y ?? -1, expected.y, accuracy: 1e-12, "y at \(time)")
        }
    }

    func testPreparedCursorSamplesFollowTheCursorOfTheirEvents() {
        var events = StudioEvents(cursor: [StudioCursorSample(t: 1, x: 0.2, y: 0.3)])
        XCTAssertEqual(events.preparedCursorSamples.focus(at: 5)?.x ?? -1, 0.2, accuracy: 1e-9)

        events.cursor = [StudioCursorSample(t: 1, x: 0.9, y: 0.3)]
        XCTAssertEqual(events.preparedCursorSamples.focus(at: 5)?.x ?? -1, 0.9, accuracy: 1e-9)

        events.cursor = []
        XCTAssertNil(events.preparedCursorSamples.focus(at: 5))
        XCTAssertTrue(events.preparedCursorSamples.isEmpty)
        XCTAssertEqual(events, StudioEvents())
    }

    func testPreparedCursorSamplesAreNotWrittenToTheFile() throws {
        let events = StudioEvents(cursor: [StudioCursorSample(t: 1, x: 0.2, y: 0.3)])

        let data = try StudioJSON.makeEncoder().encode(events)
        let text = String(decoding: data, as: UTF8.self)
        let decoded = try StudioJSON.makeDecoder().decode(StudioEvents.self, from: data)

        XCTAssertFalse(text.contains("prepared"))
        XCTAssertEqual(decoded, events)
        XCTAssertEqual(decoded.preparedCursorSamples.focus(at: 5)?.x ?? -1, 0.2, accuracy: 1e-9)
    }

    func testCursorSamplesWithTheSameTimeKeepTheOrderTheyAreStoredIn() {
        // Two samples at one instant: the pointer ends up where the later one says.
        let inOrder = [
            StudioCursorSample(t: 1, x: 0.2, y: 0.2),
            StudioCursorSample(t: 1, x: 0.8, y: 0.6)
        ]
        let outOfOrder = [
            StudioCursorSample(t: 9, x: 0.5, y: 0.5),
            StudioCursorSample(t: 1, x: 0.2, y: 0.2),
            StudioCursorSample(t: 1, x: 0.8, y: 0.6)
        ]

        for samples in [inOrder, outOfOrder] {
            let focus = StudioPreparedCursorSamples(samples).focus(at: 4)
            XCTAssertEqual(focus?.x ?? -1, 0.8, accuracy: 1e-9)
            XCTAssertEqual(focus?.y ?? -1, 0.6, accuracy: 1e-9)
        }
    }

    func testTheResolverUsesTheEventsOnlyForAZoomThatFollowsThePointer() {
        let events = StudioEvents(cursor: [
            StudioCursorSample(t: 1, x: 0.8, y: 0.8),
            StudioCursorSample(t: 2, x: 0.8, y: 0.8)
        ])
        var follows = makeProject(camera: nil)
        follows.zooms = [StudioZoom(start: 1, end: 5, focus: StudioZoomFocus(mode: .cursor, x: 0.1, y: 0.1), easeIn: 0, easeOut: 0)]
        var point = makeProject(camera: nil)
        point.zooms = [StudioZoom(start: 1, end: 5, focus: StudioZoomFocus(mode: .point, x: 0.1, y: 0.1), easeIn: 0, easeOut: 0)]

        let withoutEvents = StudioLayoutResolver.resolve(project: follows, time: 2, canvasWidth: 1920, canvasHeight: 1080)
        let withEvents = StudioLayoutResolver.resolve(project: follows, time: 2, canvasWidth: 1920, canvasHeight: 1080, events: events)

        XCTAssertEqual(withoutEvents.screen?.source.x ?? -1, 0, accuracy: 1e-9)
        XCTAssertEqual(withoutEvents.screen?.source.y ?? -1, 0, accuracy: 1e-9)
        XCTAssertEqual(withEvents.screen?.source.x ?? -1, 0.5, accuracy: 1e-9)
        XCTAssertEqual(withEvents.screen?.source.y ?? -1, 0.5, accuracy: 1e-9)
        XCTAssertEqual(withEvents.screen?.source.width ?? -1, 0.5, accuracy: 1e-9)
        XCTAssertEqual(
            StudioLayoutResolver.resolve(project: point, time: 2, canvasWidth: 1920, canvasHeight: 1080),
            StudioLayoutResolver.resolve(project: point, time: 2, canvasWidth: 1920, canvasHeight: 1080, events: events)
        )
        XCTAssertEqual(
            withoutEvents,
            StudioLayoutResolver.resolve(project: follows, time: 2, canvasWidth: 1920, canvasHeight: 1080, events: StudioEvents())
        )
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

    private func plainCursorFocus(samples: [StudioCursorSample], time: Double) -> (x: Double, y: Double) {
        let sorted = samples.enumerated().sorted {
            if $0.element.t == $1.element.t { return $0.offset < $1.offset }
            return $0.element.t < $1.element.t
        }.map(\.element)
        let a = time - 0.5
        let b = time + 0.5
        var x = 0.0
        var y = 0.0
        for index in sorted.indices {
            let start = index == sorted.startIndex ? -Double.infinity : sorted[index].t
            let end = index == sorted.index(before: sorted.endIndex) ? Double.infinity : sorted[sorted.index(after: index)].t
            let length = max(0, min(b, end) - max(a, start))
            x += StudioCanvasMath.clamped(sorted[index].x, 0, 1) * length
            y += StudioCanvasMath.clamped(sorted[index].y, 0, 1) * length
        }
        return (x, y)
    }
}
