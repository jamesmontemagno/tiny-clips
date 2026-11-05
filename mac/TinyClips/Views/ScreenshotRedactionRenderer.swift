import CoreGraphics
import CoreImage

/// Rendered pixels for one blur redaction. `normalizedRect` is the whole-pixel area the image
/// covers, which can be slightly larger than the annotation so no sharp source row survives.
struct RedactionPatch {
    let image: CGImage
    let normalizedRect: CGRect
}

/// Builds redaction pixels shared by the editor preview and every export.
///
/// The region is first reduced to a coarse 8-bit grid, which discards detail for good: a plain
/// Gaussian blur alone can be partly reversed. The result is always fully opaque so the sharp
/// image below can never show through.
enum ScreenshotRedactionRenderer {
    private static let context = CIContext(options: [.cacheIntermediates: false])

    static func patch(
        normalizedRect: CGRect,
        source: CGImage,
        preset: RedactionBlurPreset,
        isPixelated: Bool = false,
        pixelDensity: CGFloat = 1
    ) -> RedactionPatch? {
        let sourceWidth = CGFloat(source.width)
        let sourceHeight = CGFloat(source.height)
        let pixelRect = CGRect(
            x: normalizedRect.minX * sourceWidth,
            y: normalizedRect.minY * sourceHeight,
            width: normalizedRect.width * sourceWidth,
            height: normalizedRect.height * sourceHeight
        )
        .integral
        .intersection(CGRect(x: 0, y: 0, width: sourceWidth, height: sourceHeight))

        guard !pixelRect.isNull, pixelRect.width >= 1, pixelRect.height >= 1,
              let region = source.cropping(to: pixelRect) else {
            return nil
        }

        let colorSpace = outputColorSpace(for: source)
        let extent = CGRect(x: 0, y: 0, width: region.width, height: region.height)
        let cell = preset.cellSize(forRegion: extent.size, pixelDensity: pixelDensity)
        let gridRect = CGRect(
            x: 0,
            y: 0,
            width: ceil(extent.width / cell),
            height: ceil(extent.height / cell)
        )

        let padding = ceil(cell * 4)
        let reduced = CIImage(cgImage: region)
            .clampedToExtent()
            .cropped(to: extent.insetBy(dx: -padding, dy: -padding))
            .applyingFilter("CILanczosScaleTransform", parameters: [
                kCIInputScaleKey: 1 / cell,
                kCIInputAspectRatioKey: 1,
            ])
            .cropped(to: gridRect)

        guard let grid = context.createCGImage(reduced, from: gridRect, format: .RGBA8, colorSpace: colorSpace) else {
            return nil
        }

        let gridImage = CIImage(cgImage: grid)
        let enlarged = (isPixelated ? gridImage.samplingNearest() : gridImage.samplingLinear())
            .clampedToExtent()
            .transformed(by: CGAffineTransform(scaleX: cell, y: cell))
        let softened = isPixelated
            ? enlarged
            : enlarged.applyingFilter("CIGaussianBlur", parameters: [kCIInputRadiusKey: cell])
        let opaque = softened
            .composited(over: CIImage(color: CIColor(red: 0.5, green: 0.5, blue: 0.5)))
            .cropped(to: extent)

        guard let image = context.createCGImage(opaque, from: extent, format: .RGBA8, colorSpace: colorSpace) else {
            return nil
        }

        return RedactionPatch(
            image: image,
            normalizedRect: CGRect(
                x: pixelRect.minX / sourceWidth,
                y: pixelRect.minY / sourceHeight,
                width: pixelRect.width / sourceWidth,
                height: pixelRect.height / sourceHeight
            )
        )
    }

    private static func outputColorSpace(for source: CGImage) -> CGColorSpace {
        if let colorSpace = source.colorSpace, colorSpace.model == .rgb, colorSpace.supportsOutput {
            return colorSpace
        }
        return CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB()
    }
}
