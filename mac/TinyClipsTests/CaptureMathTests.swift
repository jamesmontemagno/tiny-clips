import CoreMedia
import XCTest
@testable import TinyClips

final class CaptureMathTests: XCTestCase {
    func testHostedAppDetectsUnitTestRuntime() {
        XCTAssertTrue(TinyClipsRuntime.isRunningUnitTests)
    }

    func testCaptureRegionConvertsPointsToRetinaPixels() {
        let region = CaptureRegion(
            sourceRect: CGRect(x: 10, y: 20, width: 100.25, height: 50.75),
            displayID: 7,
            scaleFactor: 2
        )

        XCTAssertEqual(region.pixelWidth, 201)
        XCTAssertEqual(region.pixelHeight, 102)

        let config = region.makeStreamConfig()
        XCTAssertEqual(config.sourceRect, region.sourceRect)
        XCTAssertEqual(config.width, 201)
        XCTAssertEqual(config.height, 102)
        XCTAssertTrue(config.scalesToFit)
        XCTAssertTrue(config.showsCursor)
    }

    func testCaptureRegionClampsPixelDimensionsToOne() {
        let region = CaptureRegion(
            sourceRect: CGRect(x: 0, y: 0, width: 0, height: 0),
            displayID: 1,
            scaleFactor: 2
        )

        XCTAssertEqual(region.pixelWidth, 1)
        XCTAssertEqual(region.pixelHeight, 1)
    }

    func testWindowFrameConversionChoosesMostOverlappingDisplayScale() {
        let displays = [
            CaptureDisplayGeometry(
                frame: CGRect(x: 0, y: 0, width: 1440, height: 900),
                scaleFactor: 2
            ),
            CaptureDisplayGeometry(
                frame: CGRect(x: 1440, y: 0, width: 1920, height: 1080),
                scaleFactor: 1
            ),
        ]

        let scale = CaptureCoordinateMath.scaleFactor(
            forWindowFrame: CGRect(x: 1300, y: 100, width: 500, height: 400),
            primaryDisplayHeight: 900,
            displays: displays
        )

        XCTAssertEqual(scale, 1)
    }

    func testWindowScaleUsesOverlapAreaForVerticallyStackedDisplays() {
        let displays = [
            CaptureDisplayGeometry(
                frame: CGRect(x: 0, y: 0, width: 1000, height: 800),
                scaleFactor: 2
            ),
            CaptureDisplayGeometry(
                frame: CGRect(x: 0, y: 800, width: 1000, height: 800),
                scaleFactor: 1
            ),
        ]

        let scale = CaptureCoordinateMath.scaleFactor(
            forWindowFrame: CGRect(x: 100, y: -500, width: 800, height: 600),
            primaryDisplayHeight: 800,
            displays: displays
        )

        XCTAssertEqual(scale, 1)
    }

    func testWindowScaleFallsBackWhenNoDisplayOverlaps() {
        let scale = CaptureCoordinateMath.scaleFactor(
            forWindowFrame: CGRect(x: 2000, y: 2000, width: 100, height: 100),
            primaryDisplayHeight: 800,
            displays: [
                CaptureDisplayGeometry(
                    frame: CGRect(x: 0, y: 0, width: 1000, height: 800),
                    scaleFactor: 2
                ),
            ]
        )

        XCTAssertEqual(scale, 1)
    }

    func testMousePointMapsIntoCapturePixelsAndRejectsOutsidePoint() {
        let screenFrame = CGRect(x: 100, y: 50, width: 800, height: 600)
        let sourceRect = CGRect(x: 100, y: 50, width: 300, height: 200)

        let mapped = CaptureCoordinateMath.capturePoint(
            for: CGPoint(x: 250, y: 500),
            screenFrame: screenFrame,
            sourceRect: sourceRect,
            scaleFactor: 2
        )

        XCTAssertEqual(mapped, CGPoint(x: 100, y: 200))
        XCTAssertNil(
            CaptureCoordinateMath.capturePoint(
                for: CGPoint(x: 899, y: 649),
                screenFrame: screenFrame,
                sourceRect: sourceRect,
                scaleFactor: 2
            )
        )
    }

    func testTimelineExcludesCompletedAndActivePauses() {
        let timeline = RecordingTimelineMath.timelineTime(
            now: CMTime(seconds: 20, preferredTimescale: 600),
            firstSampleTime: CMTime(seconds: 5, preferredTimescale: 600),
            totalPausedDuration: CMTime(seconds: 3, preferredTimescale: 600),
            pauseStartedAt: CMTime(seconds: 18, preferredTimescale: 600)
        )

        XCTAssertEqual(timeline.seconds, 10, accuracy: 0.0001)
    }

    func testTimelinePauseAccumulationAndTimestampAdjustment() {
        let accumulated = RecordingTimelineMath.accumulatedPauseDuration(
            CMTime(seconds: 2, preferredTimescale: 600),
            pauseStartedAt: CMTime(seconds: 10, preferredTimescale: 600),
            resumedAt: CMTime(seconds: 14.5, preferredTimescale: 600)
        )
        let adjusted = RecordingTimelineMath.adjustedTimestamp(
            CMTime(seconds: 20, preferredTimescale: 600),
            totalPausedDuration: accumulated
        )

        XCTAssertEqual(accumulated.seconds, 6.5, accuracy: 0.0001)
        XCTAssertEqual(adjusted.seconds, 13.5, accuracy: 0.0001)
    }
}
