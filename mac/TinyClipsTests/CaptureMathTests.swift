import CoreMedia
import XCTest
@testable import TinyClips

final class CaptureMathTests: XCTestCase {
    func testTextBoxStyleDecoratesClampsAndScalesBounds() {
        let style = TextBoxStyle(
            preset: .dark,
            backgroundColor: .black,
            borderColor: .white,
            borderWidth: 4,
            padding: 8,
            cornerRadius: 6
        )

        XCTAssertEqual(
            TextBoxStyle.decoratedRect(
                for: CGRect(x: 10, y: 20, width: 30, height: 40),
                scale: 1,
                style: style
            ),
            CGRect(x: 0, y: 10, width: 50, height: 60)
        )

        let scaled = style.scaled(by: 2)
        XCTAssertEqual(scaled.preset, .custom)
        XCTAssertEqual(scaled.borderWidth, 8)
        XCTAssertEqual(scaled.padding, 16)
        XCTAssertEqual(scaled.cornerRadius, 12)

        var clamped = style
        clamped.borderWidth = 100
        clamped.padding = -10
        clamped.cornerRadius = 100
        clamped.markCustom()
        XCTAssertEqual(clamped.borderWidth, TextBoxStyle.borderWidthRange.upperBound)
        XCTAssertEqual(clamped.padding, 0)
        XCTAssertEqual(clamped.cornerRadius, TextBoxStyle.cornerRadiusRange.upperBound)
    }

    func testTextBoxStyleNormalizesVerticalInsetForNonSquareImages() {
        let style = TextBoxStyle(
            preset: .dark,
            backgroundColor: .black,
            borderColor: .white,
            borderWidth: 4,
            padding: 8,
            cornerRadius: 6
        )
        let content = CGRect(x: 0.25, y: 0.25, width: 0.5, height: 0.5)
        let imageSize = CGSize(width: 1600, height: 800)

        let decorated = TextBoxStyle.normalizedDecoratedRect(
            for: content,
            imageSize: imageSize,
            style: style
        )

        XCTAssertEqual(decorated.minX, 0.2375, accuracy: 0.0001)
        XCTAssertEqual(decorated.minY, 0.225, accuracy: 0.0001)
        XCTAssertEqual(
            TextBoxStyle.normalizedContentRect(
                for: decorated,
                imageSize: imageSize,
                style: style
            ),
            content
        )
    }

    func testHostedAppDetectsUnitTestRuntime() {
        XCTAssertTrue(TinyClipsRuntime.isRunningUnitTests)
    }

    func testAudioOffsetMovesTimestampsAndDropsAudioBeforeVideoSessionOrigin() {
        let timestamp = CMTime(value: 2_000, timescale: 1_000)
        let origin = CMTime(value: 1_800, timescale: 1_000)

        XCTAssertEqual(
            RecordingTimelineMath.shiftedAudioTimestamp(
                timestamp,
                offset: RecordingTimelineMath.audioOffsetTime(milliseconds: 200)
            ),
            CMTime(value: 2_200, timescale: 1_000)
        )
        XCTAssertEqual(
            RecordingTimelineMath.shiftedAudioTimestamp(
                timestamp,
                offset: RecordingTimelineMath.audioOffsetTime(milliseconds: -500)
            ),
            CMTime(value: 1_500, timescale: 1_000)
        )
        XCTAssertEqual(
            RecordingTimelineMath.shouldWriteAudioTimestamp(
                CMTime(value: 1_500, timescale: 1_000),
                atOrAfter: origin
            ),
            false
        )
        XCTAssertTrue(
            RecordingTimelineMath.shouldWriteAudioTimestamp(
                origin,
                atOrAfter: origin
            )
        )
        XCTAssertTrue(
            RecordingTimelineMath.shouldWriteAudioTimestamp(
                timestamp,
                atOrAfter: nil
            )
        )
        XCTAssertTrue(
            RecordingTimelineMath.shouldWriteAudioTimestamp(
                .invalid,
                atOrAfter: origin
            )
        )
        XCTAssertEqual(
            RecordingTimelineMath.shiftedAudioTimestamp(
                .invalid,
                offset: RecordingTimelineMath.audioOffsetTime(milliseconds: 200)
            ),
            .invalid
        )
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

    func testPrimaryDisplayHeightUsesZeroOriginDisplayRegardlessOfOrder() {
        let height = CaptureCoordinateMath.primaryDisplayHeight(forDisplayFrames: [
            CGRect(x: -1920, y: 0, width: 1920, height: 1080),
            CGRect(x: 0, y: 0, width: 1440, height: 900),
        ])

        XCTAssertEqual(height, 900)
    }

    func testPrimaryDisplayHeightFallsBackToFirstDisplayAndZero() {
        XCTAssertEqual(
            CaptureCoordinateMath.primaryDisplayHeight(forDisplayFrames: [
                CGRect(x: 10, y: 20, width: 1920, height: 1080),
            ]),
            1080
        )
        XCTAssertEqual(CaptureCoordinateMath.primaryDisplayHeight(forDisplayFrames: []), 0)
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

    func testScreenshotEditorEscapeTakesOneStepAtATimeBeforeClosing() {
        func action(annotation: Bool, textField: Bool, crop: Bool) -> ScreenshotEditorEscapeAction {
            ScreenshotEditorEscapeAction.resolve(
                isEditingTextAnnotation: annotation,
                textFieldHasFocus: textField,
                hasCropSelection: crop
            )
        }

        XCTAssertEqual(action(annotation: true, textField: true, crop: true), .cancelTextAnnotation)
        XCTAssertEqual(action(annotation: true, textField: false, crop: false), .cancelTextAnnotation)
        XCTAssertEqual(action(annotation: false, textField: true, crop: true), .leaveTextField)
        XCTAssertEqual(action(annotation: false, textField: true, crop: false), .leaveTextField)
        XCTAssertEqual(action(annotation: false, textField: false, crop: true), .clearCropSelection)
        XCTAssertEqual(action(annotation: false, textField: false, crop: false), .close)
    }

    func testScreenshotEditorEscapeConfirmsEveryCloseWhenEnabled() {
        func escape(unsaved: Bool, discardsCapture: Bool) -> ScreenshotEditorClosePrompt? {
            ScreenshotEditorClosePrompt.resolve(
                trigger: .escapeKey,
                confirmOnEscape: true,
                hasUnsavedChanges: unsaved,
                discardsUnsavedCapture: discardsCapture
            )
        }

        XCTAssertEqual(escape(unsaved: false, discardsCapture: false), .closeEditor)
        XCTAssertEqual(escape(unsaved: true, discardsCapture: false), .discardChanges)
        XCTAssertEqual(escape(unsaved: false, discardsCapture: true), .discardUnsavedCapture)
        XCTAssertEqual(escape(unsaved: true, discardsCapture: true), .discardUnsavedCapture)
        XCTAssertFalse(ScreenshotEditorClosePrompt.closeEditor.isDestructive)
        XCTAssertTrue(ScreenshotEditorClosePrompt.discardUnsavedCapture.isDestructive)
    }

    func testScreenshotEditorEscapeClosesWithoutPromptWhenConfirmationIsOff() {
        for unsaved in [false, true] {
            for discardsCapture in [false, true] {
                XCTAssertNil(
                    ScreenshotEditorClosePrompt.resolve(
                        trigger: .escapeKey,
                        confirmOnEscape: false,
                        hasUnsavedChanges: unsaved,
                        discardsUnsavedCapture: discardsCapture
                    )
                )
            }
        }
    }

    func testScreenshotEditorCloseCommandOnlyPromptsForUnsavedChanges() {
        for confirmOnEscape in [false, true] {
            XCTAssertNil(
                ScreenshotEditorClosePrompt.resolve(
                    trigger: .closeCommand,
                    confirmOnEscape: confirmOnEscape,
                    hasUnsavedChanges: false,
                    discardsUnsavedCapture: true
                )
            )
            XCTAssertEqual(
                ScreenshotEditorClosePrompt.resolve(
                    trigger: .closeCommand,
                    confirmOnEscape: confirmOnEscape,
                    hasUnsavedChanges: true,
                    discardsUnsavedCapture: false
                ),
                .discardChanges
            )
        }
    }

    func testTrimmerEscapeConfirmsEveryCloseWhenEnabled() {
        func escape(unsaved: Bool, discardsCapture: Bool) -> TrimmerEscapePrompt? {
            TrimmerEscapePrompt.resolve(
                confirmOnEscape: true,
                hasUnsavedChanges: unsaved,
                discardsUnsavedCapture: discardsCapture
            )
        }

        XCTAssertEqual(escape(unsaved: false, discardsCapture: false), .closeTrimmer)
        XCTAssertEqual(escape(unsaved: true, discardsCapture: false), .discardChanges)
        XCTAssertEqual(escape(unsaved: false, discardsCapture: true), .discardUnsavedCapture)
        XCTAssertEqual(escape(unsaved: true, discardsCapture: true), .discardUnsavedCapture)
        XCTAssertFalse(TrimmerEscapePrompt.closeTrimmer.isDestructive)
        XCTAssertTrue(TrimmerEscapePrompt.discardChanges.isDestructive)
        XCTAssertTrue(TrimmerEscapePrompt.discardUnsavedCapture.isDestructive)
    }

    func testTrimmerEscapeClosesWithoutPromptWhenConfirmationIsOff() {
        for unsaved in [false, true] {
            for discardsCapture in [false, true] {
                XCTAssertNil(
                    TrimmerEscapePrompt.resolve(
                        confirmOnEscape: false,
                        hasUnsavedChanges: unsaved,
                        discardsUnsavedCapture: discardsCapture
                    )
                )
            }
        }
    }

    func testTrimmerEscapePromptNamesWhatIsDiscarded() {
        let prompt = TrimmerEscapePrompt.discardUnsavedCapture
        XCTAssertEqual(prompt.title(for: .video), "Discard recording?")
        XCTAssertEqual(prompt.confirmTitle(for: .video), "Discard Recording")
        XCTAssertEqual(prompt.title(for: .gif), "Discard GIF?")
        XCTAssertEqual(prompt.confirmTitle(for: .gif), "Discard GIF")
    }

    func testScreenshotEditorZoomClampsAndStepsThroughPresets() {
        XCTAssertEqual(ScreenshotEditorZoomMath.clamp(0.1), 0.25)
        XCTAssertEqual(ScreenshotEditorZoomMath.clamp(8), 4)
        XCTAssertEqual(ScreenshotEditorZoomMath.clamp(6, maximumScale: 6), 6)
        XCTAssertEqual(ScreenshotEditorZoomMath.steppedScale(from: 1, direction: 1), 1.25)
        XCTAssertEqual(ScreenshotEditorZoomMath.steppedScale(from: 1, direction: -1), 0.75)
        XCTAssertEqual(ScreenshotEditorZoomMath.steppedScale(from: 4, direction: 1, maximumScale: 6), 6)
        XCTAssertEqual(ScreenshotEditorZoomMath.steppedScale(from: 6, direction: -1, maximumScale: 6), 4)
    }

    func testScreenshotEditorNativeSizeZoomUsesBackingScale() {
        XCTAssertEqual(ScreenshotEditorZoomMath.nativeSizeScale(fitScale: 0.25, backingScale: 2), 2)
        XCTAssertEqual(ScreenshotEditorZoomMath.nativeSizeScale(fitScale: 0.5, backingScale: 2), 1)
        XCTAssertNil(ScreenshotEditorZoomMath.nativeSizeScale(fitScale: 0, backingScale: 2))
        XCTAssertNil(ScreenshotEditorZoomMath.nativeSizeScale(fitScale: .infinity, backingScale: 2))
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

    func testCropDragModePrefersHandlesThenInteriorThenNewSelection() {
        let selection = CGRect(x: 0.2, y: 0.2, width: 0.4, height: 0.4)
        let tolerance = CGSize(width: 0.02, height: 0.02)
        func mode(_ x: CGFloat, _ y: CGFloat) -> CropDragMode {
            ScreenshotEditorCropMath.dragMode(at: CGPoint(x: x, y: y), selection: selection, tolerance: tolerance)
        }

        XCTAssertEqual(mode(0.21, 0.19), .resize(.topLeft))
        XCTAssertEqual(mode(0.61, 0.59), .resize(.bottomRight))
        XCTAssertEqual(mode(0.4, 0.21), .resize(.top))
        XCTAssertEqual(mode(0.19, 0.4), .resize(.left))
        XCTAssertEqual(mode(0.4, 0.4), .move)
        XCTAssertEqual(mode(0.8, 0.8), .create)
        // Level with the left edge but well below the selection: not a handle.
        XCTAssertEqual(mode(0.19, 0.9), .create)
        XCTAssertEqual(
            ScreenshotEditorCropMath.dragMode(at: CGPoint(x: 0.4, y: 0.4), selection: nil, tolerance: tolerance),
            .create
        )
    }

    func testCropDragModePicksCloserEdgeWhenSelectionIsSmallerThanTolerance() {
        let selection = CGRect(x: 0.5, y: 0.5, width: 0.01, height: 0.01)
        let tolerance = CGSize(width: 0.02, height: 0.02)

        XCTAssertEqual(
            ScreenshotEditorCropMath.dragMode(at: CGPoint(x: 0.512, y: 0.512), selection: selection, tolerance: tolerance),
            .resize(.bottomRight)
        )
        XCTAssertEqual(
            ScreenshotEditorCropMath.dragMode(at: CGPoint(x: 0.498, y: 0.498), selection: selection, tolerance: tolerance),
            .resize(.topLeft)
        )
    }

    func testCropHitToleranceIsMeasuredInScreenPoints() {
        let tolerance = ScreenshotEditorCropMath.hitTolerance(forDisplaySize: CGSize(width: 400, height: 200))
        XCTAssertEqual(tolerance.width, 0.02, accuracy: 1e-9)
        XCTAssertEqual(tolerance.height, 0.04, accuracy: 1e-9)

        let fallback = ScreenshotEditorCropMath.hitTolerance(forDisplaySize: .zero)
        XCTAssertGreaterThan(fallback.width, 0)
        XCTAssertGreaterThan(fallback.height, 0)
    }

    func testCropResizeMovesOnlyTheHandleEdgesFlipsAndClamps() {
        let selection = CGRect(x: 0.2, y: 0.2, width: 0.4, height: 0.4)

        assertRect(
            ScreenshotEditorCropMath.resized(selection, handle: .right, to: CGPoint(x: 0.8, y: 0.9)),
            CGRect(x: 0.2, y: 0.2, width: 0.6, height: 0.4)
        )
        assertRect(
            ScreenshotEditorCropMath.resized(selection, handle: .topLeft, to: CGPoint(x: 0.1, y: 0.3)),
            CGRect(x: 0.1, y: 0.3, width: 0.5, height: 0.3)
        )
        // Dragging the left edge past the right edge flips the selection.
        assertRect(
            ScreenshotEditorCropMath.resized(selection, handle: .left, to: CGPoint(x: 0.9, y: 0.5)),
            CGRect(x: 0.6, y: 0.2, width: 0.3, height: 0.4)
        )
        // Targets outside the image stop at its edges.
        assertRect(
            ScreenshotEditorCropMath.resized(selection, handle: .bottomRight, to: CGPoint(x: 1.5, y: -0.5)),
            CGRect(x: 0.2, y: 0, width: 0.8, height: 0.2)
        )
    }

    func testCropResizeWithLockedAspectKeepsShapeInsideImage() {
        let selection = CGRect(x: 0.2, y: 0.2, width: 0.4, height: 0.2)

        // The pointer asks for more height than fits; the result is the largest 2:1 rect available.
        assertRect(
            ScreenshotEditorCropMath.resized(
                selection,
                handle: .bottomRight,
                to: CGPoint(x: 0.5, y: 0.9),
                lockedAspect: 2
            ),
            CGRect(x: 0.2, y: 0.2, width: 0.8, height: 0.4)
        )
        assertRect(
            ScreenshotEditorCropMath.resized(
                selection,
                handle: .topLeft,
                to: CGPoint(x: 0.5, y: 0.3),
                lockedAspect: 2
            ),
            CGRect(x: 0.4, y: 0.3, width: 0.2, height: 0.1)
        )
    }

    func testCropMoveKeepsSizeAndStopsAtImageEdges() {
        let selection = CGRect(x: 0.2, y: 0.2, width: 0.4, height: 0.4)

        assertRect(
            ScreenshotEditorCropMath.moved(selection, by: CGSize(width: 0.1, height: -0.1)),
            CGRect(x: 0.3, y: 0.1, width: 0.4, height: 0.4)
        )
        assertRect(
            ScreenshotEditorCropMath.moved(selection, by: CGSize(width: 0.9, height: 0.9)),
            CGRect(x: 0.6, y: 0.6, width: 0.4, height: 0.4)
        )
        assertRect(
            ScreenshotEditorCropMath.moved(selection, by: CGSize(width: -1, height: -1)),
            CGRect(x: 0, y: 0, width: 0.4, height: 0.4)
        )
    }

    func testCropCreateNormalizesDirectionAndClampsToImage() {
        let imageSize = CGSize(width: 1_000, height: 500)

        assertRect(
            ScreenshotEditorCropMath.created(
                from: CGPoint(x: 0.6, y: 0.7),
                to: CGPoint(x: 0.2, y: 0.1),
                square: false,
                imageSize: imageSize
            ),
            CGRect(x: 0.2, y: 0.1, width: 0.4, height: 0.6)
        )
        assertRect(
            ScreenshotEditorCropMath.created(
                from: CGPoint(x: -0.5, y: 0.5),
                to: CGPoint(x: 2, y: 0.75),
                square: false,
                imageSize: imageSize
            ),
            CGRect(x: 0, y: 0.5, width: 1, height: 0.25)
        )
    }

    func testCropCreateWithShiftIsSquareInImagePixels() {
        let imageSize = CGSize(width: 1_000, height: 500)

        let square = ScreenshotEditorCropMath.created(
            from: CGPoint(x: 0.1, y: 0.1),
            to: CGPoint(x: 0.3, y: 0.2),
            square: true,
            imageSize: imageSize
        )
        assertRect(square, CGRect(x: 0.1, y: 0.1, width: 0.2, height: 0.4))
        XCTAssertEqual(square.width * imageSize.width, square.height * imageSize.height, accuracy: 1e-6)

        // Only 100 px remain below the start point, so the square stops growing there.
        let clamped = ScreenshotEditorCropMath.created(
            from: CGPoint(x: 0.1, y: 0.8),
            to: CGPoint(x: 0.9, y: 0.9),
            square: true,
            imageSize: imageSize
        )
        assertRect(clamped, CGRect(x: 0.1, y: 0.8, width: 0.1, height: 0.2))
    }

    func testCropSelectionUsabilityDependsOnOnScreenSize() {
        let displaySize = CGSize(width: 300, height: 200)

        XCTAssertFalse(
            ScreenshotEditorCropMath.isUsableSelection(
                CGRect(x: 0, y: 0, width: 0.01, height: 0.5),
                displaySize: displaySize
            )
        )
        XCTAssertTrue(
            ScreenshotEditorCropMath.isUsableSelection(
                CGRect(x: 0, y: 0, width: 0.02, height: 0.5),
                displaySize: displaySize
            )
        )
    }

    func testCropHandlePointsSitOnCornersAndEdgeMidpoints() {
        let selection = CGRect(x: 0.2, y: 0.2, width: 0.4, height: 0.4)

        let top = ScreenshotEditorCropMath.handlePoint(.top, in: selection)
        XCTAssertEqual(top.x, 0.4, accuracy: 1e-9)
        XCTAssertEqual(top.y, 0.2, accuracy: 1e-9)

        let bottomRight = ScreenshotEditorCropMath.handlePoint(.bottomRight, in: selection)
        XCTAssertEqual(bottomRight.x, 0.6, accuracy: 1e-9)
        XCTAssertEqual(bottomRight.y, 0.6, accuracy: 1e-9)

        XCTAssertEqual(CropHandle.allCases.filter(\.isCorner).count, 4)
    }

    func testCropKeyboardAdjustmentMovesAndGrowsInWholePixels() {
        // Dimensions that do not divide evenly, so normalized edges never land exactly on a pixel.
        let imageSize = CGSize(width: 1_237, height: 733)
        var selection = CGRect(x: 100.0 / 1_237, y: 50.0 / 733, width: 300.0 / 1_237, height: 200.0 / 733)
        func pixels() -> CGRect? {
            ScreenshotEditorCropMath.pixelRect(for: selection, imageSize: imageSize)
        }

        XCTAssertEqual(pixels(), CGRect(x: 100, y: 50, width: 300, height: 200))

        // Repeated one-pixel steps must not drift or widen the selection.
        for _ in 0..<50 {
            selection = ScreenshotEditorCropMath.adjusted(
                selection,
                movingBy: CGSize(width: 1, height: 0),
                imageSize: imageSize
            )
        }
        XCTAssertEqual(pixels(), CGRect(x: 150, y: 50, width: 300, height: 200))

        selection = ScreenshotEditorCropMath.adjusted(
            selection,
            growingBy: CGSize(width: -10, height: 5),
            imageSize: imageSize
        )
        XCTAssertEqual(pixels(), CGRect(x: 150, y: 50, width: 290, height: 205))
    }

    func testCropKeyboardAdjustmentStaysInsideImageAndKeepsOnePixel() {
        let imageSize = CGSize(width: 1_237, height: 733)
        var selection = CGRect(x: 150.0 / 1_237, y: 50.0 / 733, width: 290.0 / 1_237, height: 205.0 / 733)
        func pixels() -> CGRect? {
            ScreenshotEditorCropMath.pixelRect(for: selection, imageSize: imageSize)
        }

        selection = ScreenshotEditorCropMath.adjusted(
            selection,
            movingBy: CGSize(width: 5_000, height: 5_000),
            imageSize: imageSize
        )
        XCTAssertEqual(pixels(), CGRect(x: 947, y: 528, width: 290, height: 205))

        // Growing against the bottom-right corner pushes the origin back instead of stopping.
        selection = ScreenshotEditorCropMath.adjusted(
            selection,
            growingBy: CGSize(width: 10, height: 10),
            imageSize: imageSize
        )
        XCTAssertEqual(pixels(), CGRect(x: 937, y: 518, width: 300, height: 215))

        selection = ScreenshotEditorCropMath.adjusted(
            selection,
            growingBy: CGSize(width: 5_000, height: 5_000),
            imageSize: imageSize
        )
        XCTAssertEqual(pixels(), CGRect(x: 0, y: 0, width: 1_237, height: 733))

        selection = ScreenshotEditorCropMath.adjusted(
            selection,
            growingBy: CGSize(width: -5_000, height: -5_000),
            imageSize: imageSize
        )
        XCTAssertEqual(pixels(), CGRect(x: 0, y: 0, width: 1, height: 1))
    }

    func testEmojiAnnotationRectIsSquareInPixelsAndCentered() {
        let imageSize = CGSize(width: 2_000, height: 1_000)
        let side = EmojiAnnotationMath.defaultSidePixels(forImageSize: imageSize)
        XCTAssertEqual(side, 200)

        let rect = EmojiAnnotationMath.normalizedRect(
            centeredAt: CGPoint(x: 0.5, y: 0.5),
            sidePixels: side,
            imageSize: imageSize
        )
        XCTAssertEqual(rect.width * imageSize.width, 200, accuracy: 0.001)
        XCTAssertEqual(rect.height * imageSize.height, 200, accuracy: 0.001)
        XCTAssertEqual(rect.midX, 0.5, accuracy: 0.0001)
        XCTAssertEqual(rect.midY, 0.5, accuracy: 0.0001)

        XCTAssertEqual(EmojiAnnotationMath.clampSidePixels(2, imageSize: imageSize), emojiMinimumSidePixels)
        XCTAssertEqual(EmojiAnnotationMath.clampSidePixels(5_000, imageSize: imageSize), 1_000)
    }

    func testRotatableGeometryRotatesCornersAndHandleClockwise() {
        let size = CGSize(width: 1_000, height: 1_000)
        let rect = CGRect(x: 0.4, y: 0.4, width: 0.2, height: 0.2) // 200px square centered at (500, 500)

        let quarterTurn = RotatableAnnotationGeometry.corners(of: rect, rotation: .pi / 2, in: size)
        // Top-left (400, 400) rotates clockwise to the top-right position (600, 400).
        XCTAssertEqual(quarterTurn[0].x, 600, accuracy: 0.001)
        XCTAssertEqual(quarterTurn[0].y, 400, accuracy: 0.001)

        let handle = RotatableAnnotationGeometry.rotationHandle(for: rect, rotation: .pi / 2, in: size)
        let offset = RotatableAnnotationGeometry.rotationHandleOffset(forScaledHeight: 200)
        // Grip starts above the top edge and ends up to the right after a clockwise quarter turn.
        XCTAssertEqual(handle.x, 500 + 100 + offset, accuracy: 0.001)
        XCTAssertEqual(handle.y, 500, accuracy: 0.001)

        XCTAssertEqual(
            RotatableAnnotationGeometry.angle(from: CGPoint(x: 500, y: 500), to: CGPoint(x: 900, y: 500)),
            .pi / 2,
            accuracy: 0.0001
        )
        XCTAssertEqual(
            RotatableAnnotationGeometry.angle(from: CGPoint(x: 500, y: 500), to: CGPoint(x: 500, y: 100)),
            0,
            accuracy: 0.0001
        )
    }

    func testRotatableGeometryHitTestFollowsRotation() {
        let size = CGSize(width: 1_000, height: 500)
        // 100×100 px square centered at (500, 250).
        let rect = CGRect(x: 0.45, y: 0.4, width: 0.1, height: 0.2)

        // Unrotated: a point 65px to the right of center is outside the 50px half-width.
        XCTAssertFalse(RotatableAnnotationGeometry.contains(CGPoint(x: 565, y: 250), rect: rect, rotation: 0, in: size))
        // Rotated 45°, the corner reaches ~70px out along the diagonal, so the same point is inside.
        XCTAssertTrue(RotatableAnnotationGeometry.contains(CGPoint(x: 565, y: 250), rect: rect, rotation: .pi / 4, in: size))
        // Padding extends the hit area.
        XCTAssertTrue(RotatableAnnotationGeometry.contains(CGPoint(x: 565, y: 250), rect: rect, rotation: 0, in: size, padding: 20))
    }

    func testRotatableGeometryNormalizesAndSnapsAngles() {
        XCTAssertEqual(RotatableAnnotationGeometry.normalizedAngle(3 * .pi), .pi, accuracy: 0.0001)
        XCTAssertEqual(RotatableAnnotationGeometry.normalizedAngle(-3 * .pi), -.pi, accuracy: 0.0001)
        XCTAssertEqual(RotatableAnnotationGeometry.snapped(0.30, to: .pi / 12, shouldSnap: true), .pi / 12, accuracy: 0.0001)
        XCTAssertEqual(RotatableAnnotationGeometry.snapped(0.30, to: .pi / 12, shouldSnap: false), 0.30, accuracy: 0.0001)
    }

    func testEmojiExtractionAcceptsEmojiAndRejectsPlainText() {
        XCTAssertEqual(EmojiAnnotationMath.emoji(from: "😀"), "😀")
        XCTAssertEqual(EmojiAnnotationMath.emoji(from: "abc🚀"), "🚀")
        XCTAssertEqual(EmojiAnnotationMath.emoji(from: "❤️"), "❤️")
        XCTAssertEqual(EmojiAnnotationMath.emoji(from: "👍🏽"), "👍🏽")
        XCTAssertNil(EmojiAnnotationMath.emoji(from: "7"))
        XCTAssertNil(EmojiAnnotationMath.emoji(from: "hello"))
        XCTAssertNil(EmojiAnnotationMath.emoji(from: ""))
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

    func testExportFrameLayoutSnapsCenteredImageOriginToWholePixels() {
        // Padding comes from a `step: 2` slider and the crop rect is `.integral`, so
        // the only fractional value the export path can produce is a centered origin
        // when the preset leaves an odd amount of extra space (e.g. 101 / 2 = 50.5).
        let imageSize = CGSize(width: 503, height: 331)

        // Extra horizontal space is 624 - 523 = 101 (odd).
        let landscape = ExportFrameLayout.make(
            imageSize: imageSize,
            padding: 10,
            preset: .landscapeSixteenByNine,
            horizontalAlignment: .center,
            verticalAlignment: .center,
            snapsToPixels: true
        )
        XCTAssertEqual(landscape.frameSize, CGSize(width: 624, height: 351))
        XCTAssertEqual(landscape.imageRect.origin, CGPoint(x: 61, y: 10))

        // Extra vertical space is 930 - 351 = 579 (odd).
        let portrait = ExportFrameLayout.make(
            imageSize: imageSize,
            padding: 10,
            preset: .portraitNineBySixteen,
            horizontalAlignment: .center,
            verticalAlignment: .center,
            snapsToPixels: true
        )
        XCTAssertEqual(portrait.frameSize, CGSize(width: 523, height: 930))
        XCTAssertEqual(portrait.imageRect.origin, CGPoint(x: 10, y: 300))

        // Even leftover space (523 - 351 = 172) is unchanged by the snap.
        let square = ExportFrameLayout.make(
            imageSize: imageSize,
            padding: 10,
            preset: .square,
            horizontalAlignment: .center,
            verticalAlignment: .center,
            snapsToPixels: true
        )
        XCTAssertEqual(square.frameSize, CGSize(width: 523, height: 523))
        XCTAssertEqual(square.imageRect.origin, CGPoint(x: 10, y: 96))

        for layout in [landscape, portrait, square] {
            XCTAssertEqual(layout.imageRect.size, imageSize)
            XCTAssertLessThanOrEqual(layout.imageRect.maxX, layout.frameSize.width)
            XCTAssertLessThanOrEqual(layout.imageRect.maxY, layout.frameSize.height)
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

    private func assertRect(
        _ actual: CGRect,
        _ expected: CGRect,
        accuracy: CGFloat = 1e-9,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        XCTAssertEqual(actual.origin.x, expected.origin.x, accuracy: accuracy, "x", file: file, line: line)
        XCTAssertEqual(actual.origin.y, expected.origin.y, accuracy: accuracy, "y", file: file, line: line)
        XCTAssertEqual(actual.width, expected.width, accuracy: accuracy, "width", file: file, line: line)
        XCTAssertEqual(actual.height, expected.height, accuracy: accuracy, "height", file: file, line: line)
    }

    private func redValue(in image: CGImage, x: Int, y: Int) -> UInt8? {
        guard let data = image.dataProvider?.data,
              let bytes = CFDataGetBytePtr(data) else {
            return nil
        }
        return bytes[(y * image.width + x) * 4]
    }
}
