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

    func testPixelAlignedRectSnapsFractionalOriginAndSizeToDevicePixels() {
        let aligned = CaptureCoordinateMath.pixelAlignedRect(
            CGRect(x: 412.3, y: 100.6, width: 100.25, height: 50.75),
            scaleFactor: 2
        )

        // 824.6 -> 825, 1025.1 -> 1025, so origin 412.5 pt and width 100.0 pt.
        XCTAssertEqual(aligned.minX, 412.5)
        XCTAssertEqual(aligned.minY, 100.5)
        XCTAssertEqual(aligned.width, 100)
        XCTAssertEqual(aligned.height, 51)
    }

    func testPixelAlignedRectProducesExactIntegralPixelScale() {
        let region = CaptureRegion(
            sourceRect: CGRect(x: 412.3, y: 100.6, width: 100.25, height: 50.75),
            displayID: 7,
            scaleFactor: 2
        ).pixelAligned()

        XCTAssertEqual(region.sourceRect.minX * 2, (region.sourceRect.minX * 2).rounded())
        XCTAssertEqual(region.sourceRect.minY * 2, (region.sourceRect.minY * 2).rounded())
        XCTAssertEqual(CGFloat(region.pixelWidth), region.sourceRect.width * 2)
        XCTAssertEqual(CGFloat(region.pixelHeight), region.sourceRect.height * 2)
    }

    func testPixelAlignedIsIdempotent() {
        let region = CaptureRegion(
            sourceRect: CGRect(x: 12.7, y: 33.1, width: 199.4, height: 88.9),
            displayID: 3,
            scaleFactor: 2
        ).pixelAligned()

        XCTAssertEqual(region.pixelAligned().sourceRect, region.sourceRect)
    }

    func testPixelAlignedIsNoOpOnNonRetinaIntegralRect() {
        let sourceRect = CGRect(x: 10, y: 20, width: 300, height: 200)
        let region = CaptureRegion(sourceRect: sourceRect, displayID: 1, scaleFactor: 1)

        XCTAssertEqual(region.pixelAligned().sourceRect, sourceRect)
    }

    func testPixelAlignedKeepsAtLeastOneDevicePixel() {
        let region = CaptureRegion(
            sourceRect: CGRect(x: 5.1, y: 5.1, width: 0, height: 0),
            displayID: 1,
            scaleFactor: 2
        ).pixelAligned()

        XCTAssertEqual(region.pixelWidth, 1)
        XCTAssertEqual(region.pixelHeight, 1)
        XCTAssertEqual(region.sourceRect.width, 0.5)
        XCTAssertEqual(region.sourceRect.height, 0.5)
    }

    func testPixelAlignedRectIgnoresInvalidScaleFactor() {
        let sourceRect = CGRect(x: 1.5, y: 2.5, width: 10.5, height: 20.5)

        XCTAssertEqual(CaptureCoordinateMath.pixelAlignedRect(sourceRect, scaleFactor: 0), sourceRect)
    }

    func testWithScaleFactorReplacesScaleAndIgnoresInvalidValues() {
        let region = CaptureRegion(
            sourceRect: CGRect(x: 0, y: 0, width: 100, height: 50),
            displayID: 4,
            scaleFactor: 2
        )

        let rescaled = region.withScaleFactor(16.0 / 9.0)
        XCTAssertEqual(rescaled.scaleFactor, 16.0 / 9.0)
        XCTAssertEqual(rescaled.sourceRect, region.sourceRect)
        XCTAssertEqual(rescaled.displayID, 4)

        XCTAssertEqual(region.withScaleFactor(0).scaleFactor, 2)
        XCTAssertEqual(region.withScaleFactor(-1).scaleFactor, 2)
    }

    func testNonIntegralScaleStillProducesExactPixelMapping() {
        // Scaled Retina modes report a point/pixel ratio that is not 2.0.
        let scale = 2560.0 / 1440.0
        let region = CaptureRegion(
            sourceRect: CGRect(x: 412.3, y: 100.6, width: 100.25, height: 50.75),
            displayID: 9,
            scaleFactor: 2
        ).withScaleFactor(scale).pixelAligned()

        XCTAssertEqual(region.scaleFactor, scale)
        XCTAssertEqual(region.sourceRect.minX * scale, (region.sourceRect.minX * scale).rounded(), accuracy: 1e-9)
        XCTAssertEqual(CGFloat(region.pixelWidth), region.sourceRect.width * scale, accuracy: 1e-9)
        XCTAssertEqual(CGFloat(region.pixelHeight), region.sourceRect.height * scale, accuracy: 1e-9)
    }

    func testCropPixelRectMapsRegionIntoFullDisplayCapture() {
        let crop = CaptureCoordinateMath.cropPixelRect(
            forSourceRect: CGRect(x: 300.5, y: 200.5, width: 400, height: 301),
            contentOrigin: .zero,
            scaleFactor: 2,
            imagePixelSize: CGSize(width: 2880, height: 1800)
        )

        XCTAssertEqual(crop, CGRect(x: 601, y: 401, width: 800, height: 602))
    }

    func testCropPixelRectOffsetsByContentOriginAndClampsToImage() {
        let crop = CaptureCoordinateMath.cropPixelRect(
            forSourceRect: CGRect(x: 110, y: 60, width: 100, height: 100),
            contentOrigin: CGPoint(x: 10, y: 10),
            scaleFactor: 2,
            imagePixelSize: CGSize(width: 300, height: 200)
        )

        // Origin-relative pixel rect is (200, 100, 200, 200); the image is only 200 tall.
        XCTAssertEqual(crop, CGRect(x: 200, y: 100, width: 100, height: 100))
    }

    func testCropPixelRectIsNullWhenRegionFallsOutsideCapture() {
        let crop = CaptureCoordinateMath.cropPixelRect(
            forSourceRect: CGRect(x: 5000, y: 5000, width: 100, height: 100),
            contentOrigin: .zero,
            scaleFactor: 2,
            imagePixelSize: CGSize(width: 2880, height: 1800)
        )

        XCTAssertTrue(crop.isNull)
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

    func testScreenshotEditorZoomClampsAndStepsThroughPresets() {
        XCTAssertEqual(ScreenshotEditorZoomMath.clamp(0.1), 0.25)
        XCTAssertEqual(ScreenshotEditorZoomMath.clamp(8), 4)
        XCTAssertEqual(ScreenshotEditorZoomMath.steppedScale(from: 1, direction: 1), 1.25)
        XCTAssertEqual(ScreenshotEditorZoomMath.steppedScale(from: 1, direction: -1), 0.75)
    }

    func testScreenshotEditorZoomPreservesFocalPointAndClampsPan() {
        let adjusted = ScreenshotEditorZoomMath.focalAdjustedPan(
            .zero,
            oldScale: 1,
            newScale: 2,
            focalPoint: CGPoint(x: 75, y: 25),
            viewportSize: CGSize(width: 100, height: 100)
        )
        XCTAssertEqual(adjusted.width, -25)
        XCTAssertEqual(adjusted.height, 25)

        let clamped = ScreenshotEditorZoomMath.clampedPan(
            CGSize(width: -80, height: 90),
            contentSize: CGSize(width: 200, height: 160),
            viewportSize: CGSize(width: 100, height: 100)
        )
        XCTAssertEqual(clamped.width, -50)
        XCTAssertEqual(clamped.height, 30)
    }

    func testScreenshotEditorCropConvertsNormalizedRectToPixels() {
        let crop = ScreenshotEditorCropMath.pixelRect(
            for: CGRect(x: 0.125, y: 0.25, width: 0.5, height: 0.5),
            imageSize: CGSize(width: 1_000, height: 800)
        )

        XCTAssertEqual(crop, CGRect(x: 125, y: 200, width: 500, height: 400))
    }

    func testScreenshotEditorCropClampsBoundsAndRejectsEmptySelections() {
        let clamped = ScreenshotEditorCropMath.pixelRect(
            for: CGRect(x: -0.2, y: 0.25, width: 0.5, height: 1),
            imageSize: CGSize(width: 1_000, height: 800)
        )

        XCTAssertEqual(clamped, CGRect(x: 0, y: 200, width: 300, height: 600))
        XCTAssertNil(
            ScreenshotEditorCropMath.pixelRect(
                for: CGRect(x: 0.1255, y: 0.25, width: 0, height: 0.5),
                imageSize: CGSize(width: 1_000, height: 800)
            )
        )
    }

    func testExportFrameLayoutSnapsToWholePixels() {
        // Fractional padding and preset ratios must not produce fractional frame
        // sizes or image origins when snapping to pixels: sub-pixel slivers at the
        // canvas edge render as white hairlines in formats without alpha (e.g. JPEG).
        let cases: [(padding: CGFloat, preset: ExportFramePreset, h: ExportHorizontalAlignment, v: ExportVerticalAlignment)] = [
            (10.4, .square, .center, .center),
            (12.75, .landscapeSixteenByNine, .trailing, .bottom),
            (7.2, .portraitNineBySixteen, .leading, .top),
            (0.5, .original, .center, .center),
        ]

        for testCase in cases {
            let imageSize = CGSize(width: 503, height: 331)
            let layout = ExportFrameLayout.make(
                imageSize: imageSize,
                padding: testCase.padding,
                preset: testCase.preset,
                horizontalAlignment: testCase.h,
                verticalAlignment: testCase.v,
                snapsToPixels: true
            )

            XCTAssertEqual(
                layout.frameSize.width.rounded(), layout.frameSize.width,
                "frame width must be integral for padding \(testCase.padding)"
            )
            XCTAssertEqual(
                layout.frameSize.height.rounded(), layout.frameSize.height,
                "frame height must be integral for padding \(testCase.padding)"
            )
            XCTAssertEqual(
                layout.imageRect.minX.rounded(), layout.imageRect.minX,
                "image origin x must be integral"
            )
            XCTAssertEqual(
                layout.imageRect.minY.rounded(), layout.imageRect.minY,
                "image origin y must be integral"
            )
            // The image must sit fully inside the frame.
            XCTAssertTrue(layout.frameSize.width >= layout.imageRect.maxX)
            XCTAssertTrue(layout.frameSize.height >= layout.imageRect.maxY)
        }
    }

    func testExportFrameLayoutKeepsFractionalGeometryForDisplay() {
        // The display-space preview path keeps fractional geometry so preview
        // padding and scaling stay proportional to point-scaled sizes.
        let layout = ExportFrameLayout.make(
            imageSize: CGSize(width: 503.5, height: 331.25),
            padding: 10.4,
            preset: .original,
            horizontalAlignment: .center,
            verticalAlignment: .center
        )

        XCTAssertEqual(layout.frameSize.width, 524.3, accuracy: 0.001)
        XCTAssertEqual(layout.frameSize.height, 352.05, accuracy: 0.001)
        XCTAssertEqual(layout.imageRect.minX, 10.4, accuracy: 0.001)
        XCTAssertEqual(layout.imageRect.minY, 10.4, accuracy: 0.001)
    }

    func testExportFrameLayoutPlacesImageAlongAvailableAxis() {
        let horizontalOrigins: [ExportHorizontalAlignment: CGPoint] = [
            .leading: CGPoint(x: 10, y: 10),
            .center: CGPoint(x: 35, y: 10),
            .trailing: CGPoint(x: 60, y: 10),
        ]
        let verticalOrigins: [ExportVerticalAlignment: CGPoint] = [
            .top: CGPoint(x: 10, y: 10),
            .center: CGPoint(x: 10, y: 35),
            .bottom: CGPoint(x: 10, y: 60),
        ]

        for horizontal in ExportHorizontalAlignment.allCases {
            let layout = ExportFrameLayout.make(
                imageSize: CGSize(width: 50, height: 100),
                padding: 10,
                preset: .square,
                horizontalAlignment: horizontal,
                verticalAlignment: .top
            )

            XCTAssertEqual(layout.frameSize, CGSize(width: 120, height: 120))
            XCTAssertEqual(layout.imageRect.origin, horizontalOrigins[horizontal])
        }

        for vertical in ExportVerticalAlignment.allCases {
            let layout = ExportFrameLayout.make(
                imageSize: CGSize(width: 100, height: 50),
                padding: 10,
                preset: .square,
                horizontalAlignment: .leading,
                verticalAlignment: vertical
            )

            XCTAssertEqual(layout.frameSize, CGSize(width: 120, height: 120))
            XCTAssertEqual(layout.imageRect.origin, verticalOrigins[vertical])
        }
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
