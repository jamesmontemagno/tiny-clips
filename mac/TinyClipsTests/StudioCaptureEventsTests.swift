import XCTest
@testable import TinyClips

final class StudioCaptureEventsTests: XCTestCase {
    func testProjectFrameRateIsTheConfiguredRateNotTheFilesAverage() {
        // Little moved on screen, so the file averages 7 frames a second. It was recorded at 60.
        XCTAssertEqual(StudioCaptureEvents.projectFrameRate(configured: 60, measured: 7.2), 60)
        XCTAssertEqual(StudioCaptureEvents.projectFrameRate(configured: 24, measured: 24), 24)
        XCTAssertEqual(StudioCaptureEvents.projectFrameRate(configured: 1000, measured: 30), 240)
    }

    func testProjectFrameRateFallsBackToTheFileButNeverBelowThirty() {
        XCTAssertEqual(StudioCaptureEvents.projectFrameRate(configured: 0, measured: 7.2), 30)
        XCTAssertEqual(StudioCaptureEvents.projectFrameRate(configured: .nan, measured: 59.94), 60)
        XCTAssertEqual(StudioCaptureEvents.projectFrameRate(configured: 0, measured: 0), 30)
        XCTAssertEqual(StudioCaptureEvents.projectFrameRate(configured: 0, measured: .infinity), 30)
    }

    func testNormalizedPointDropsClicksOutsideCaptureRect() {
        let rect = CGRect(x: 10, y: 20, width: 200, height: 100)

        XCTAssertEqual(
            StudioCaptureEvents.normalizedPoint(CGPoint(x: 110, y: 70), in: rect, dropsOutside: true),
            CGPoint(x: 0.5, y: 0.5)
        )
        XCTAssertNil(StudioCaptureEvents.normalizedPoint(CGPoint(x: 9.9, y: 70), in: rect, dropsOutside: true))
    }

    func testNormalizedClicksSortAndKeepButtons() {
        let samples = [
            StudioCaptureClickSample(t: 2, point: CGPoint(x: 110, y: 70), button: .right),
            StudioCaptureClickSample(t: 1, point: CGPoint(x: 10, y: 20), button: .left),
            StudioCaptureClickSample(t: 3, point: CGPoint(x: 211, y: 20), button: .middle)
        ]

        let clicks = StudioCaptureEvents.normalizedClicks(
            samples,
            captureRect: CGRect(x: 10, y: 20, width: 200, height: 100)
        )

        XCTAssertEqual(clicks.count, 2)
        XCTAssertEqual(clicks[0], StudioClickEvent(t: 1, x: 0, y: 0, button: .left))
        XCTAssertEqual(clicks[1], StudioClickEvent(t: 2, x: 0.5, y: 0.5, button: .right))
    }

    func testCursorSamplesAreThinnedSortedAndDeduplicated() {
        let samples = [
            StudioCaptureCursorSampleInput(t: 0.010, point: CGPoint(x: 20, y: 20)),
            StudioCaptureCursorSampleInput(t: 0.000, point: CGPoint(x: 20, y: 20)),
            StudioCaptureCursorSampleInput(t: 0.020, point: CGPoint(x: 20, y: 20)),
            StudioCaptureCursorSampleInput(t: 0.040, point: CGPoint(x: 30, y: 40)),
            StudioCaptureCursorSampleInput(t: 0.050, point: CGPoint(x: 50, y: 50)),
            StudioCaptureCursorSampleInput(t: 0.070, point: CGPoint(x: 30, y: 40))
        ]

        let cursor = StudioCaptureEvents.normalizedCursorSamples(
            samples,
            captureRect: CGRect(x: 10, y: 20, width: 100, height: 100),
            maxSamplesPerSecond: 60
        )

        XCTAssertEqual(cursor, [
            StudioCursorSample(t: 0, x: 0.1, y: 0),
            StudioCursorSample(t: 0.04, x: 0.2, y: 0.2)
        ])
    }

    func testCursorSamplesCanFallOutsideCaptureRect() {
        let cursor = StudioCaptureEvents.normalizedCursorSamples(
            [StudioCaptureCursorSampleInput(t: 1, point: CGPoint(x: 0, y: 220))],
            captureRect: CGRect(x: 10, y: 20, width: 100, height: 100)
        )

        XCTAssertEqual(cursor, [StudioCursorSample(t: 1, x: -0.1, y: 2)])
    }

    func testCameraCornersStartAtInitialCornerAndDropDuplicates() {
        let corners = StudioCaptureEvents.cameraCornerEvents(
            initialCorner: .bottomRight,
            changes: [
                StudioCameraCornerEvent(t: 2, corner: .topLeft),
                StudioCameraCornerEvent(t: 1, corner: .bottomRight),
                StudioCameraCornerEvent(t: 3, corner: .topLeft),
                StudioCameraCornerEvent(t: 4, corner: .bottomLeft)
            ]
        )

        XCTAssertEqual(corners, [
            StudioCameraCornerEvent(t: 0, corner: .bottomRight),
            StudioCameraCornerEvent(t: 2, corner: .topLeft),
            StudioCameraCornerEvent(t: 4, corner: .bottomLeft)
        ])
    }

    func testProjectCreationRequestCarriesCaptureDefaults() {
        let overlay = StudioClickOverlay(
            enabled: false,
            color: "#123456",
            size: 44,
            strokeWidth: 4,
            opacity: 0.5,
            duration: 0.6
        )
        let look = StudioLook(canvas: StudioCanvas(aspect: .square, padding: 0.2))
        let request = StudioCaptureEvents.makeProjectCreationRequest(
            name: "TinyClips 2026-10-03 at 07.26.39",
            screen: StudioCaptureMediaInfo(width: 1920, height: 1080, duration: 5, frameRate: 60),
            camera: StudioCaptureMediaInfo(width: 640, height: 480, duration: 4, frameRate: 30),
            cameraStartOffset: 0.25,
            bubbleAnchor: .topRight,
            clickOverlay: overlay,
            branding: true,
            appVersion: "1.2.3",
            look: look
        )

        XCTAssertEqual(request.name, "TinyClips 2026-10-03 at 07.26.39")
        XCTAssertEqual(request.screenWidth, 1920)
        XCTAssertEqual(request.screenFrameRate, 60)
        XCTAssertEqual(request.camera, StudioCameraCreationInfo(width: 640, height: 480, duration: 4, startOffset: 0.25))
        XCTAssertEqual(request.bubbleAnchor, .topRight)
        XCTAssertEqual(request.clickOverlay, overlay)
        XCTAssertTrue(request.branding)
        XCTAssertEqual(request.appVersion, "1.2.3")
        XCTAssertEqual(request.look, look)

        // A recorder that does not say what the sound tracks hold leaves the project without a list.
        XCTAssertNil(request.screenAudioTracks)
        let withSound = StudioCaptureEvents.makeProjectCreationRequest(
            name: "Recording",
            screen: StudioCaptureMediaInfo(width: 1920, height: 1080, duration: 5, frameRate: 60, audioTrackCount: 2),
            screenAudioTracks: ["system", "microphone"],
            camera: nil,
            cameraStartOffset: 0,
            bubbleAnchor: .topRight,
            clickOverlay: overlay,
            branding: false,
            appVersion: "1.2.3"
        )
        XCTAssertEqual(withSound.screenAudioTracks, ["system", "microphone"])
    }

    func testTheSoundTracksAreListedOnlyWhenTheFileHasOneForEachInput() {
        XCTAssertEqual(
            StudioCaptureEvents.screenAudioTracks(recorded: [.system, .microphone], trackCountInFile: 2),
            ["system", "microphone"]
        )
        XCTAssertEqual(StudioCaptureEvents.screenAudioTracks(recorded: [.microphone], trackCountInFile: 1), ["microphone"])
        XCTAssertEqual(StudioCaptureEvents.screenAudioTracks(recorded: [.system], trackCountInFile: 1), ["system"])

        // A recording without sound says that it has none.
        XCTAssertEqual(StudioCaptureEvents.screenAudioTracks(recorded: [], trackCountInFile: 0), [])

        // An input that never got a sample may be missing from the file, and a file can have a
        // track nobody set up. Either way the list would not fit the file.
        XCTAssertNil(StudioCaptureEvents.screenAudioTracks(recorded: [.system, .microphone], trackCountInFile: 1))
        XCTAssertNil(StudioCaptureEvents.screenAudioTracks(recorded: [.system], trackCountInFile: 2))
        XCTAssertNil(StudioCaptureEvents.screenAudioTracks(recorded: [], trackCountInFile: 1))
        XCTAssertNil(StudioCaptureEvents.screenAudioTracks(recorded: [.system], trackCountInFile: 0))
    }

    // MARK: - Scenes from the recording
    //
    // Section 9.1 of the format. The shared fixtures hold the cases; these are the ones a fixture
    // cannot hold.

    func testAChangeExactlyAShortestSceneAfterTheLastGetsItsOwnScene() {
        let first = StudioScene(bubble: StudioBubble(anchor: .bottomRight))
        let shortest = StudioCaptureEvents.shortestRecordedScene
        XCTAssertEqual(shortest, StudioEditorModel.minimumSceneDuration)
        XCTAssertEqual(shortest, 0.3)

        var scenes = StudioCaptureEvents.scenes(
            first: first,
            corners: [corner(0, .bottomRight), corner(5, .topLeft), corner(5 + shortest, .topRight)],
            markers: [],
            duration: 20
        )
        XCTAssertEqual(scenes.map(\.start), [0, 5, 5 + shortest])
        XCTAssertEqual(scenes.map(\.bubble.anchor), [.bottomRight, .topLeft, .topRight])

        // And a change exactly that long after the start of the recording.
        scenes = StudioCaptureEvents.scenes(first: first, corners: [corner(shortest, .topLeft)], markers: [], duration: 20)
        XCTAssertEqual(scenes.map(\.start), [0, shortest])
        XCTAssertEqual(scenes[0].bubble.anchor, .bottomRight)
    }

    func testAChangeExactlyAShortestSceneBeforeTheEndIsKept() {
        let first = StudioScene(bubble: StudioBubble(anchor: .bottomRight))
        let shortest = StudioCaptureEvents.shortestRecordedScene

        var scenes = StudioCaptureEvents.scenes(first: first, corners: [corner(20 - shortest, .topLeft)], markers: [], duration: 20)
        XCTAssertEqual(scenes.map(\.start), [0, 20 - shortest])

        // The next number after it is too late.
        scenes = StudioCaptureEvents.scenes(first: first, corners: [corner((20 - shortest).nextUp, .topLeft)], markers: [], duration: 20)
        XCTAssertEqual(scenes.count, 1)
        XCTAssertEqual(scenes[0].bubble.anchor, .bottomRight)
    }

    func testTimesThatAreNoNumbersAreLeftOut() {
        let first = StudioScene(
            start: 4,
            layout: .bubble,
            bubble: StudioBubble(anchor: .bottomRight),
            transition: StudioTransition(kind: .morph, duration: 1)
        )

        let scenes = StudioCaptureEvents.scenes(
            first: first,
            corners: [corner(.nan, .topLeft), corner(.infinity, .topRight), corner(-.infinity, .bottomLeft), corner(6, .topRight)],
            markers: [StudioLayoutMarker(t: .nan, layout: .camera), StudioLayoutMarker(t: 10, layout: .sideBySide)],
            duration: 20
        )

        XCTAssertEqual(scenes.map(\.start), [0, 6, 10])
        XCTAssertEqual(scenes.map(\.bubble.anchor), [.bottomRight, .topRight, .topRight])
        XCTAssertEqual(scenes.map(\.layout), [.bubble, .bubble, .sideBySide])

        // The first scene starts at 0 whatever it was given, and is entered as it was given.
        XCTAssertEqual(scenes[0].transition, StudioTransition(kind: .morph, duration: 1))
        XCTAssertEqual(scenes[1].transition, StudioTransition(kind: .morph, duration: 0.35))

        // No changes at all.
        let alone = StudioCaptureEvents.scenes(first: first, corners: [], markers: [], duration: 20)
        XCTAssertEqual(alone.map(\.start), [0])
    }

    func testTheRequestCarriesWhatWasChangedWhileRecording() {
        let corners = [corner(0, .topRight), corner(3, .bottomLeft)]
        let markers = [StudioLayoutMarker(t: 5, layout: .camera)]
        let request = StudioCaptureEvents.makeProjectCreationRequest(
            name: "Recording",
            screen: StudioCaptureMediaInfo(width: 1920, height: 1080, duration: 12, frameRate: 30),
            camera: StudioCaptureMediaInfo(width: 640, height: 480, duration: 12, frameRate: 30),
            cameraStartOffset: 0,
            bubbleAnchor: .topRight,
            clickOverlay: StudioClickOverlay(),
            branding: false,
            appVersion: "1.2.3",
            cameraCorners: corners,
            layoutMarkers: markers
        )
        XCTAssertEqual(request.cameraCorners, corners)
        XCTAssertEqual(request.layoutMarkers, markers)
    }

    private func corner(_ t: Double, _ anchor: StudioAnchor) -> StudioCameraCornerEvent {
        StudioCameraCornerEvent(t: t, corner: anchor)
    }

    func testEventsDocumentCarriesCaptureMetadata() {
        let events = StudioCaptureEvents.makeEvents(
            captureWidth: 100,
            captureHeight: 50,
            captureScale: 2,
            captureKind: .region,
            clicks: [StudioClickEvent(t: 1, x: 0.2, y: 0.3, button: .other)],
            cursor: [StudioCursorSample(t: 2, x: 0.4, y: 0.5)],
            cameraCorners: [StudioCameraCornerEvent(t: 0, corner: .bottomRight)]
        )

        XCTAssertEqual(events.capture, StudioCaptureInfo(width: 100, height: 50, scale: 2, kind: .region))
        XCTAssertEqual(events.clicks, [StudioClickEvent(t: 1, x: 0.2, y: 0.3, button: .other)])
        XCTAssertEqual(events.cursor, [StudioCursorSample(t: 2, x: 0.4, y: 0.5)])
        XCTAssertEqual(events.cameraCorners, [StudioCameraCornerEvent(t: 0, corner: .bottomRight)])
    }
}
