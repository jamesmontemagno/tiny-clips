import ScreenCaptureKit
import ImageIO
import UniformTypeIdentifiers
import AppKit

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
        config.sourceRect = CGRect(origin: .zero, size: window.frame.size)
        config.width = max(1, Int(window.frame.width * scaleFactor))
        config.height = max(1, Int(window.frame.height * scaleFactor))
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
        let appKitFrame = CGRect(
            x: window.frame.origin.x,
            y: primaryHeight - window.frame.maxY,
            width: window.frame.width,
            height: window.frame.height
        )
        return NSScreen.screens
            .max { a, b in
                a.frame.intersection(appKitFrame).width < b.frame.intersection(appKitFrame).width
            }?
            .backingScaleFactor ?? 1.0
    }
}
