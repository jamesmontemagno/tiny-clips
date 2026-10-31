import ScreenCaptureKit
import ImageIO
import UniformTypeIdentifiers
import AppKit
import libwebp

struct CaptureDisplayGeometry {
    let frame: CGRect
    let scaleFactor: CGFloat
}

enum ImagePixelDensity {
    static let pointsPerInch: CGFloat = 72

    static func normalizedScaleFactor(_ scaleFactor: CGFloat) -> CGFloat {
        guard scaleFactor.isFinite, scaleFactor > 0 else { return 1 }
        return scaleFactor
    }

    static func dpi(for scaleFactor: CGFloat) -> CGFloat {
        pointsPerInch * normalizedScaleFactor(scaleFactor)
    }

    static func logicalSize(pixelWidth: Int, pixelHeight: Int, scaleFactor: CGFloat) -> NSSize {
        let scaleFactor = normalizedScaleFactor(scaleFactor)
        return NSSize(
            width: CGFloat(pixelWidth) / scaleFactor,
            height: CGFloat(pixelHeight) / scaleFactor
        )
    }

    static func scaleFactor(for image: NSImage) -> CGFloat {
        guard image.size.width.isFinite, image.size.width > 0,
              image.size.height.isFinite, image.size.height > 0,
              let representation = image.representations.max(by: {
                  $0.pixelsWide * $0.pixelsHigh < $1.pixelsWide * $1.pixelsHigh
              }) else {
            return 1
        }

        let widthScale = CGFloat(representation.pixelsWide) / image.size.width
        let heightScale = CGFloat(representation.pixelsHigh) / image.size.height
        return normalizedScaleFactor(max(widthScale, heightScale))
    }

    static func loadImage(contentsOf url: URL) -> NSImage? {
        guard let image = NSImage(contentsOf: url) else { return nil }
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil)
                as? [CFString: Any],
              let dpiWidth = properties[kCGImagePropertyDPIWidth] as? NSNumber,
              let dpiHeight = properties[kCGImagePropertyDPIHeight] as? NSNumber else {
            return image
        }

        let scaleFactor = normalizedScaleFactor(
            max(CGFloat(dpiWidth.doubleValue), CGFloat(dpiHeight.doubleValue)) / pointsPerInch
        )
        guard let representation = image.representations.max(by: {
            $0.pixelsWide * $0.pixelsHigh < $1.pixelsWide * $1.pixelsHigh
        }) else {
            return image
        }

        let size = logicalSize(
            pixelWidth: representation.pixelsWide,
            pixelHeight: representation.pixelsHigh,
            scaleFactor: scaleFactor
        )
        image.size = size
        image.representations.forEach { $0.size = size }
        return image
    }
}

enum CaptureCoordinateMath {
    /// Height of the display that anchors global coordinates: ScreenCaptureKit measures Y down from
    /// the top-left of the display whose AppKit origin is (0, 0), which is not guaranteed to be the
    /// first entry of `NSScreen.screens`.
    static func primaryDisplayHeight(forDisplayFrames displayFrames: [CGRect]) -> CGFloat {
        let primary = displayFrames.first { $0.origin == .zero } ?? displayFrames.first
        return primary?.height ?? 0
    }

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
    private struct CaptureResult {
        let image: CGImage
        let scaleFactor: CGFloat
    }

    static func capture(region: CaptureRegion) async throws -> URL {
        let destinationURL = SaveService.shared.generateURL(for: .screenshot)
        return try await capture(region: region, outputURL: destinationURL)
    }

    static func capture(region: CaptureRegion, outputURL: URL) async throws -> URL {
        let result = try await captureResult(region: region)
        return try saveImage(result.image, to: outputURL, scaleFactor: result.scaleFactor)
    }

    /// Captures the whole display natively and crops, because `SCStreamConfiguration.sourceRect`
    /// resamples the frame even when the rect is pixel-aligned and the buffer size matches exactly.
    /// Measured against a lossless crop of the same frame it loses ~5% gradient energy and leaves
    /// only ~86% of pixels identical. `CGImage.cropping(to:)` is a pure pixel copy.
    static func captureImage(region: CaptureRegion) async throws -> CGImage {
        (try await captureResult(region: region)).image
    }

    private static func captureResult(region: CaptureRegion) async throws -> CaptureResult {
        let filter = try await region.makeFilter(
            excludingApplicationBundleIdentifiers: CaptureSettings.shared.screenshotExcludedApplicationBundleIdentifiers
        )
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
        return CaptureResult(image: cropped, scaleFactor: scaleFactor)
    }

    static func captureWindow(_ window: SCWindow) async throws -> URL {
        let destinationURL = SaveService.shared.generateURL(for: .screenshot)
        return try await captureWindow(window, outputURL: destinationURL)
    }

    static func captureWindow(_ window: SCWindow, outputURL: URL) async throws -> URL {
        let filter = SCContentFilter(desktopIndependentWindow: window)
        let config = SCStreamConfiguration()
        let scaleFactor = pixelScale(for: window, filter: filter)
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
        return try saveImage(image, to: outputURL, scaleFactor: scaleFactor)
    }

    // MARK: - Helpers

    /// The points-to-pixels scale a window capture uses: ScreenCaptureKit's own scale for the
    /// window, falling back to the display that overlaps the window most.
    static func pixelScale(for window: SCWindow, filter: SCContentFilter? = nil) -> CGFloat {
        let filter = filter ?? SCContentFilter(desktopIndependentWindow: window)
        return filter.pointPixelScale > 0
            ? CGFloat(filter.pointPixelScale)
            : scaleFactorForWindow(window)
    }

    static func saveImage(
        _ image: CGImage,
        to outputURL: URL,
        scaleFactor: CGFloat = 1
    ) throws -> URL {
        let settings = CaptureSettings.shared
        let imageType = settings.imageFormat.utType
        let dpi = ImagePixelDensity.dpi(for: scaleFactor)
        var destinationProperties: [CFString: Any] = [
            kCGImagePropertyDPIWidth: dpi,
            kCGImagePropertyDPIHeight: dpi
        ]
        if settings.imageFormat == .jpeg {
            destinationProperties[kCGImageDestinationLossyCompressionQuality] = settings.jpegQuality
        }

        let imageToSave = settings.showBrandingOverlay
            ? BrandingOverlayProcessor.applyToImage(image)
            : image

        if settings.imageFormat == .webp {
            guard let data = WebPImageEncoder.encode(
                imageToSave,
                quality: settings.jpegQuality,
                scaleFactor: scaleFactor
            ) else {
                throw CaptureError.saveFailed
            }
            try data.write(to: outputURL, options: .atomic)
            return outputURL
        }

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
        let primaryHeight = CaptureCoordinateMath.primaryDisplayHeight(
            forDisplayFrames: NSScreen.screens.map(\.frame)
        )
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

enum WebPImageEncoder {
    static func encode(
        _ image: CGImage,
        quality: Double,
        scaleFactor: CGFloat = 1
    ) -> Data? {
        let width = image.width
        let height = image.height
        guard width > 0, height > 0, width <= Int(Int32.max) / 4,
              height <= Int(Int32.max), height <= Int.max / (width * 4) else {
            return nil
        }
        let stride = width * 4
        var pixels = [UInt8](repeating: 0, count: stride * height)
        let rendered = pixels.withUnsafeMutableBytes { bytes -> Bool in
            guard let context = CGContext(
                data: bytes.baseAddress,
                width: width,
                height: height,
                bitsPerComponent: 8,
                bytesPerRow: stride,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue | CGBitmapInfo.byteOrder32Big.rawValue
            ) else { return false }
            context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
            return true
        }
        guard rendered else { return nil }

        // Core Graphics supplies premultiplied RGBA; libwebp expects straight alpha.
        for offset in Swift.stride(from: 0, to: pixels.count, by: 4) {
            let alpha = Int(pixels[offset + 3])
            if alpha > 0 && alpha < 255 {
                for channel in 0..<3 {
                    pixels[offset + channel] = UInt8(min(255, Int(pixels[offset + channel]) * 255 / alpha))
                }
            }
        }

        var output: UnsafeMutablePointer<UInt8>?
        let length = pixels.withUnsafeBufferPointer { bytes in
            WebPEncodeRGBA(bytes.baseAddress, Int32(width), Int32(height), Int32(stride),
                           Float(max(0, min(1, quality)) * 100), &output)
        }
        guard length > 0, let output else { return nil }
        defer { WebPFree(output) }
        let encoded = Data(bytes: output, count: length)
        return addingPixelDensity(
            to: encoded,
            pixelWidth: width,
            pixelHeight: height,
            scaleFactor: scaleFactor
        )
    }

    private static func addingPixelDensity(
        to data: Data,
        pixelWidth: Int,
        pixelHeight: Int,
        scaleFactor: CGFloat
    ) -> Data? {
        guard data.count >= 20,
              data.prefix(4) == Data("RIFF".utf8),
              data.subdata(in: 8..<12) == Data("WEBP".utf8),
              pixelWidth > 0, pixelWidth <= 1 << 24,
              pixelHeight > 0, pixelHeight <= 1 << 24 else {
            return nil
        }

        var result: Data
        if data.subdata(in: 12..<16) == Data("VP8X".utf8) {
            guard littleEndianUInt32(in: data, at: 16) >= 10 else { return nil }
            result = data
            result[20] |= 0x08
        } else {
            result = Data(data.prefix(12))
            result.append(contentsOf: "VP8X".utf8)
            result.appendLittleEndian(UInt32(10))
            result.append(0x08)
            result.append(contentsOf: [0, 0, 0])
            result.appendUInt24(UInt32(pixelWidth - 1))
            result.appendUInt24(UInt32(pixelHeight - 1))
            result.append(data.dropFirst(12))
        }

        let exif = exifPixelDensity(scaleFactor: scaleFactor)
        result.append(contentsOf: "EXIF".utf8)
        result.appendLittleEndian(UInt32(exif.count))
        result.append(exif)
        if exif.count.isMultiple(of: 2) == false {
            result.append(0)
        }

        guard result.count - 8 <= Int(UInt32.max) else { return nil }
        result.replaceSubrange(4..<8, with: UInt32(result.count - 8).littleEndianBytes)
        return result
    }

    private static func exifPixelDensity(scaleFactor: CGFloat) -> Data {
        let resolutionDenominator: UInt32 = 1_000
        let resolutionNumerator = UInt32(
            (ImagePixelDensity.dpi(for: scaleFactor) * CGFloat(resolutionDenominator)).rounded()
        )
        let firstRationalOffset: UInt32 = 50
        var data = Data()
        data.append(contentsOf: [0x49, 0x49])
        data.appendLittleEndian(UInt16(42))
        data.appendLittleEndian(UInt32(8))
        data.appendLittleEndian(UInt16(3))
        data.appendTIFFEntry(tag: 0x011A, type: 5, count: 1, value: firstRationalOffset)
        data.appendTIFFEntry(tag: 0x011B, type: 5, count: 1, value: firstRationalOffset + 8)
        data.appendTIFFEntry(tag: 0x0128, type: 3, count: 1, value: 2)
        data.appendLittleEndian(UInt32(0))
        data.appendLittleEndian(resolutionNumerator)
        data.appendLittleEndian(resolutionDenominator)
        data.appendLittleEndian(resolutionNumerator)
        data.appendLittleEndian(resolutionDenominator)
        return data
    }

    private static func littleEndianUInt32(in data: Data, at offset: Int) -> UInt32 {
        guard offset >= 0, offset + 4 <= data.count else { return 0 }
        return data[offset..<(offset + 4)]
            .enumerated()
            .reduce(UInt32(0)) { result, element in
                result | UInt32(element.element) << (element.offset * 8)
            }
    }
}

private extension FixedWidthInteger {
    var littleEndianBytes: [UInt8] {
        withUnsafeBytes(of: littleEndian) { Array($0) }
    }
}

private extension Data {
    mutating func appendLittleEndian<T: FixedWidthInteger>(_ value: T) {
        append(contentsOf: value.littleEndianBytes)
    }

    mutating func appendUInt24(_ value: UInt32) {
        append(UInt8(value & 0xFF))
        append(UInt8((value >> 8) & 0xFF))
        append(UInt8((value >> 16) & 0xFF))
    }

    mutating func appendTIFFEntry(tag: UInt16, type: UInt16, count: UInt32, value: UInt32) {
        appendLittleEndian(tag)
        appendLittleEndian(type)
        appendLittleEndian(count)
        appendLittleEndian(value)
    }
}
