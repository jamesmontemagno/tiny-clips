import ScreenCaptureKit
import ImageIO
import UniformTypeIdentifiers
import AppKit

struct CaptureDisplayGeometry {
    let frame: CGRect
    let scaleFactor: CGFloat
}

enum CaptureCoordinateMath {
    static func appKitFrame(for screenCaptureFrame: CGRect, primaryDisplayHeight: CGFloat) -> CGRect {
        CGRect(
            x: screenCaptureFrame.origin.x,
            y: primaryDisplayHeight - screenCaptureFrame.maxY,
            width: screenCaptureFrame.width,
            height: screenCaptureFrame.height
        )
    }

    static func scaleFactor(
        forWindowFrame windowFrame: CGRect,
        primaryDisplayHeight: CGFloat,
        displays: [CaptureDisplayGeometry]
    ) -> CGFloat {
        let appKitFrame = appKitFrame(
            for: windowFrame,
            primaryDisplayHeight: primaryDisplayHeight
        )
        return displays
            .compactMap { display -> (geometry: CaptureDisplayGeometry, area: CGFloat)? in
                let intersection = display.frame.intersection(appKitFrame)
                guard !intersection.isNull, intersection.width > 0, intersection.height > 0 else {
                    return nil
                }
                return (display, intersection.width * intersection.height)
            }
            .max { $0.area < $1.area }?
            .geometry.scaleFactor ?? 1.0
    }

    /// Snaps a point-space rect onto whole device-pixel boundaries.
    ///
    /// Region selections come from raw mouse coordinates, so the rect is almost always fractional in
    /// point space, which produces off-by-one output dimensions and a size readout that disagrees with
    /// the saved file. This is about predictable geometry, not sharpness.
    static func pixelAlignedRect(_ rect: CGRect, scaleFactor: CGFloat) -> CGRect {
        guard scaleFactor > 0, rect.width.isFinite, rect.height.isFinite else { return rect }

        let minX = (rect.minX * scaleFactor).rounded()
        let minY = (rect.minY * scaleFactor).rounded()
        let maxX = max(minX + 1, (rect.maxX * scaleFactor).rounded())
        let maxY = max(minY + 1, (rect.maxY * scaleFactor).rounded())

        return CGRect(
            x: minX / scaleFactor,
            y: minY / scaleFactor,
            width: (maxX - minX) / scaleFactor,
            height: (maxY - minY) / scaleFactor
        )
    }

    /// Pixel rect to crop out of a full-display capture, clamped to the captured image.
    static func cropPixelRect(
        forSourceRect sourceRect: CGRect,
        contentOrigin: CGPoint,
        scaleFactor: CGFloat,
        imagePixelSize: CGSize
    ) -> CGRect {
        let rect = CGRect(
            x: ((sourceRect.minX - contentOrigin.x) * scaleFactor).rounded(),
            y: ((sourceRect.minY - contentOrigin.y) * scaleFactor).rounded(),
            width: max(1, (sourceRect.width * scaleFactor).rounded()),
            height: max(1, (sourceRect.height * scaleFactor).rounded())
        )
        return rect.intersection(CGRect(origin: .zero, size: imagePixelSize))
    }

    static func capturePoint(
        for globalPoint: CGPoint,
        screenFrame: CGRect,
        sourceRect: CGRect,
        scaleFactor: CGFloat
    ) -> CGPoint? {
        let localPoint = CGPoint(
            x: globalPoint.x - screenFrame.minX,
            y: screenFrame.maxY - globalPoint.y
        )
        guard sourceRect.contains(localPoint) else {
            return nil
        }

        return CGPoint(
            x: (localPoint.x - sourceRect.minX) * scaleFactor,
            y: (localPoint.y - sourceRect.minY) * scaleFactor
        )
    }
}

struct ScreenshotCapture {
    static func capture(region: CaptureRegion) async throws -> URL {
        let destinationURL = SaveService.shared.generateURL(for: .screenshot)
        return try await capture(region: region, outputURL: destinationURL)
    }

    static func capture(region: CaptureRegion, outputURL: URL) async throws -> URL {
        let image = try await captureImage(region: region)
        return try saveImage(image, to: outputURL)
    }

    /// Captures the whole display natively and crops, because `SCStreamConfiguration.sourceRect`
    /// resamples the frame even when the rect is pixel-aligned and the buffer size matches exactly.
    /// Measured against a lossless crop of the same frame it loses ~5% gradient energy and leaves
    /// only ~86% of pixels identical. `CGImage.cropping(to:)` is a pure pixel copy.
    static func captureImage(region: CaptureRegion) async throws -> CGImage {
        let filter = try await region.makeFilter()
        let region = region.resolvingPixelScale(from: filter)
        let scaleFactor = region.scaleFactor
        let contentRect = filter.contentRect

        let config = SCStreamConfiguration()
        config.sourceRect = CGRect(origin: .zero, size: contentRect.size)
        config.width = max(1, Int((contentRect.width * scaleFactor).rounded()))
        config.height = max(1, Int((contentRect.height * scaleFactor).rounded()))
        config.scalesToFit = false
        config.showsCursor = false

        let fullImage = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)

        let cropRect = CaptureCoordinateMath.cropPixelRect(
            forSourceRect: region.sourceRect,
            contentOrigin: contentRect.origin,
            scaleFactor: scaleFactor,
            imagePixelSize: CGSize(width: fullImage.width, height: fullImage.height)
        )
        // Never fall back to the full image: that would silently save (or OCR) the whole desktop.
        guard !cropRect.isNull, let cropped = fullImage.cropping(to: cropRect) else {
            throw CaptureError.regionCropFailed
        }
        return cropped
    }

    static func captureWindow(_ window: SCWindow) async throws -> URL {
        let destinationURL = SaveService.shared.generateURL(for: .screenshot)
        return try await captureWindow(window, outputURL: destinationURL)
    }

    static func captureWindow(_ window: SCWindow, outputURL: URL) async throws -> URL {
        let filter = SCContentFilter(desktopIndependentWindow: window)
        let config = SCStreamConfiguration()
        let scaleFactor = filter.pointPixelScale > 0
            ? CGFloat(filter.pointPixelScale)
            : scaleFactorForWindow(window)
        let sourceRect = CaptureCoordinateMath.pixelAlignedRect(
            CGRect(origin: .zero, size: window.frame.size),
            scaleFactor: scaleFactor
        )
        config.sourceRect = sourceRect
        config.width = max(1, Int((sourceRect.width * scaleFactor).rounded()))
        config.height = max(1, Int((sourceRect.height * scaleFactor).rounded()))
        config.scalesToFit = false
        config.showsCursor = false

        let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
        return try saveImage(image, to: outputURL)
    }

    // MARK: - Helpers

    static func saveImage(_ image: CGImage, to outputURL: URL) throws -> URL {
        let settings = CaptureSettings.shared
        let imageType = settings.imageFormat.utType
        var destinationProperties: [CFString: Any] = [:]
        if settings.imageFormat == .jpeg {
            destinationProperties[kCGImageDestinationLossyCompressionQuality] = settings.jpegQuality
        }

        let imageToSave = settings.showBrandingOverlay
            ? BrandingOverlayProcessor.applyToImage(image)
            : image

        guard let destination = CGImageDestinationCreateWithURL(
            outputURL as CFURL,
            imageType.identifier as CFString,
            1,
            nil
        ) else {
            throw CaptureError.saveFailed
        }
        CGImageDestinationAddImage(destination, imageToSave, destinationProperties as CFDictionary)
        guard CGImageDestinationFinalize(destination) else {
            throw CaptureError.saveFailed
        }
        return outputURL
    }

    /// Returns the backing scale factor of the screen that most overlaps the given SCWindow.
    private static func scaleFactorForWindow(_ window: SCWindow) -> CGFloat {
        let primaryHeight = NSScreen.screens.first?.frame.height ?? 0
        let displays = NSScreen.screens.map {
            CaptureDisplayGeometry(frame: $0.frame, scaleFactor: $0.backingScaleFactor)
        }
        return CaptureCoordinateMath.scaleFactor(
            forWindowFrame: window.frame,
            primaryDisplayHeight: primaryHeight,
            displays: displays
        )
    }
}
