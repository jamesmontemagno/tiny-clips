import XCTest
@testable import TinyClips

final class StudioCaptureEventsTests: XCTestCase {
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
