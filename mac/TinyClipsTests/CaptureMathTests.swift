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

    func testPanoramaStitchesKnownVerticalShift() throws {
        let first = panoramaFrame(globalStartRow: 0)
        let second = panoramaFrame(globalStartRow: 20)

        let result = try PanoramaStitcher(limits: panoramaLimits()).stitch([first, second])

        XCTAssertEqual(result.frameCount, 2)
        XCTAssertEqual(result.outputHeight, 120)
    }

    func testPanoramaRejectsFramesWithoutCredibleAlignment() {
        let first = panoramaFrame(globalStartRow: 0)
        let unrelated = PanoramaFrame(
            width: 40,
            height: 100,
            pixels: [UInt8](repeating: 255, count: 40 * 100 * 4)
        )

        XCTAssertThrowsError(
            try PanoramaStitcher(limits: panoramaLimits()).stitch([first, unrelated])
        ) { error in
            XCTAssertEqual(error as? PanoramaCaptureError, .alignmentFailed)
        }
    }

    func testPanoramaSuppressesStationaryFooterCopies() throws {
        let first = panoramaFrame(globalStartRow: 0, fixedFooterHeight: 5)
        let second = panoramaFrame(globalStartRow: 20, fixedFooterHeight: 5)

        let result = try PanoramaStitcher(limits: panoramaLimits()).stitch([first, second])

        XCTAssertEqual(result.outputHeight, 120)
        XCTAssertEqual(redValue(in: result.image, x: 0, y: 95), UInt8((95 * 7) % 251))
        XCTAssertEqual(redValue(in: result.image, x: 0, y: 119), 32)
    }

    func testPanoramaEnforcesPeakMemoryBudget() {
        let first = panoramaFrame(globalStartRow: 0)
        let second = panoramaFrame(globalStartRow: 20)
        let limits = PanoramaCaptureLimits(
            maxFrames: 10,
            maxOutputHeight: 1_000,
            maxMemoryBytes: 70_000,
            noMovementTimeout: 8
        )

        XCTAssertThrowsError(try PanoramaStitcher(limits: limits).stitch([first, second])) { error in
            XCTAssertEqual(error as? PanoramaCaptureError, .memoryLimit)
        }
    }

    func testPanoramaKeepsPartialResultWhenMemoryLimitIsReached() throws {
        let frames = [
            panoramaFrame(globalStartRow: 0),
            panoramaFrame(globalStartRow: 20),
            panoramaFrame(globalStartRow: 40)
        ]
        let limits = PanoramaCaptureLimits(
            maxFrames: 10,
            maxOutputHeight: 1_000,
            maxMemoryBytes: 75_000,
            noMovementTimeout: 8
        )

        let result = try PanoramaStitcher(limits: limits).stitch(frames)

        XCTAssertTrue(result.reachedLimit)
        XCTAssertEqual(result.frameCount, 2)
        XCTAssertEqual(result.outputHeight, 120)
    }

    func testPanoramaKeepsPartialResultWhenOutputHeightIsReached() throws {
        let frames = [
            panoramaFrame(globalStartRow: 0),
            panoramaFrame(globalStartRow: 20),
            panoramaFrame(globalStartRow: 40)
        ]
        let limits = PanoramaCaptureLimits(
            maxFrames: 10,
            maxOutputHeight: 130,
            maxMemoryBytes: 2_000_000,
            noMovementTimeout: 8
        )

        let result = try PanoramaStitcher(limits: limits).stitch(frames)

        XCTAssertTrue(result.reachedLimit)
        XCTAssertEqual(result.outputHeight, 120)
    }

    func testPanoramaMemoryUseDoesNotGrowWithFrameCount() {
        // Peak memory tracks the stitched output plus the retained and incoming frames, so a
        // long capture of a modest region must stay well inside the default budget.
        let width = 2_400
        let height = 1_800
        let frameBytes = Int64(width) * Int64(height) * 4
        let shiftPerFrame = 200
        let outputHeight = height + shiftPerFrame * 200
        let outputBytes = Int64(width) * Int64(outputHeight) * 4

        XCTAssertLessThanOrEqual(
            outputBytes * 2 + frameBytes * 2,
            PanoramaCaptureLimits.default.maxMemoryBytes
        )
        XCTAssertLessThanOrEqual(outputHeight, PanoramaCaptureLimits.default.maxOutputHeight)
    }

    func testPanoramaPrefersSmallestShiftOnRepeatingContent() throws {
        // A page of repeating rows aliases at shift + N * period; picking a later
        // alias duplicates rows that are already committed.
        let first = repeatingPanoramaFrame(globalStartRow: 0, period: 30)
        let second = repeatingPanoramaFrame(globalStartRow: 20, period: 30)

        let result = try PanoramaStitcher(limits: panoramaLimits()).stitch([first, second])

        XCTAssertEqual(result.frameCount, 2)
        XCTAssertEqual(result.outputHeight, 120)
    }

    func testPanoramaAlignsSmallScrollSteps() throws {
        // Slow scrolling advances only a few pixels per frame, which must not be
        // rounded up to a larger shift.
        let first = panoramaFrame(globalStartRow: 0)
        let second = panoramaFrame(globalStartRow: 4)

        let result = try PanoramaStitcher(limits: panoramaLimits()).stitch([first, second])

        XCTAssertEqual(result.outputHeight, 104)
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

    private func panoramaFrame(globalStartRow: Int, fixedFooterHeight: Int = 0) -> PanoramaFrame {
        let width = 40
        let height = 100
        var pixels = [UInt8](repeating: 255, count: width * height * 4)
        for y in 0..<height {
            for x in 0..<width {
                let index = (y * width + x) * 4
                let isFooter = fixedFooterHeight > 0 && y >= height - fixedFooterHeight
                let value = isFooter
                    ? UInt8(32)
                    : UInt8(((globalStartRow + y) * 7 + x * 13) % 251)
                pixels[index] = value
                pixels[index + 1] = value
                pixels[index + 2] = value
                pixels[index + 3] = 255
            }
        }
        return PanoramaFrame(width: width, height: height, pixels: pixels)
    }

    func testPanoramaAlignsPeriodicContentAcrossShiftSizes() {
        let width = 320
        let height = 900
        let period = 100
        func makeFrame(globalStartRow: Int) -> PanoramaFrame {
            var pixels = [UInt8](repeating: 255, count: width * height * 4)
            for y in 0..<height {
                let row = globalStartRow + y
                let band = (row % period) < period / 2 ? 40 : 210
                for x in 0..<width {
                    let index = (y * width + x) * 4
                    let value = UInt8(min(255, max(0, band + ((row % 7) * 3) + ((x % 11) * 2))))
                    pixels[index] = value
                    pixels[index + 1] = value
                    pixels[index + 2] = value
                    pixels[index + 3] = 255
                }
            }
            return PanoramaFrame(width: width, height: height, pixels: pixels)
        }

        let base = makeFrame(globalStartRow: 0)
        for trueShift in [6, 37, 120, 480] {
            let alignment = PanoramaAccumulator.estimateVerticalShift(
                previous: base,
                current: makeFrame(globalStartRow: trueShift)
            )
            XCTAssertEqual(alignment?.shift, trueShift, "shift \(trueShift) misaligned")
        }
    }

    private func repeatingPanoramaFrame(globalStartRow: Int, period: Int) -> PanoramaFrame {
        let width = 40
        let height = 100
        var pixels = [UInt8](repeating: 255, count: width * height * 4)
        for y in 0..<height {
            let row = (globalStartRow + y) % period
            for x in 0..<width {
                let index = (y * width + x) * 4
                let value = UInt8((row * 8 + x * 3) % 251)
                pixels[index] = value
                pixels[index + 1] = value
                pixels[index + 2] = value
                pixels[index + 3] = 255
            }
        }
        return PanoramaFrame(width: width, height: height, pixels: pixels)
    }

    private func panoramaLimits() -> PanoramaCaptureLimits {
        PanoramaCaptureLimits(
            maxFrames: 10,
            maxOutputHeight: 1_000,
            maxMemoryBytes: 2_000_000,
            noMovementTimeout: 8
        )
    }

    private func redValue(in image: CGImage, x: Int, y: Int) -> UInt8? {
        guard let data = image.dataProvider?.data,
              let bytes = CFDataGetBytePtr(data) else {
            return nil
        }
        return bytes[(y * image.width + x) * 4]
    }
}
