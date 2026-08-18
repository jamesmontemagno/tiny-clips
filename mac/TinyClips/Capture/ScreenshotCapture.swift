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
    /// ScreenCaptureKit resamples the whole frame when a crop lands on a fractional pixel or when the
    /// requested buffer size implies a non-integral scale, which softens the capture. Region selections
    /// come from raw mouse coordinates, so they are almost always fractional.
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

    static func captureImage(region: CaptureRegion) async throws -> CGImage {
        let region = region.pixelAligned()
        let filter = try await region.makeFilter()
        let config = SCStreamConfiguration()
        config.sourceRect = region.sourceRect
        config.width = region.pixelWidth
        config.height = region.pixelHeight
        config.scalesToFit = false
        config.showsCursor = false

        return try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
    }

    static func captureWindow(_ window: SCWindow) async throws -> URL {
        let destinationURL = SaveService.shared.generateURL(for: .screenshot)
        return try await captureWindow(window, outputURL: destinationURL)
    }

    static func captureWindow(_ window: SCWindow, outputURL: URL) async throws -> URL {
        let filter = SCContentFilter(desktopIndependentWindow: window)
        let config = SCStreamConfiguration()
        let scaleFactor = scaleFactorForWindow(window)
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
