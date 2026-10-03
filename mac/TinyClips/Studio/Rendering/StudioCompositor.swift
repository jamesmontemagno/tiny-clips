import AVFoundation
import CoreGraphics
import CoreImage
import Foundation
import ImageIO

// MARK: - Video Composition Instruction

final class StudioVideoCompositionInstruction: NSObject, AVVideoCompositionInstructionProtocol {
    let timeRange: CMTimeRange
    let enablePostProcessing = false
    let containsTweening = false
    let requiredSourceTrackIDs: [NSValue]?
    let passthroughTrackID: CMPersistentTrackID = kCMPersistentTrackID_Invalid
    let screenTrackID: CMPersistentTrackID
    let cameraTrackID: CMPersistentTrackID?
    let renderSize: CGSize
    let renderState: StudioRenderState
    let projectDirectory: URL

    init(
        timeRange: CMTimeRange,
        screenTrackID: CMPersistentTrackID,
        cameraTrackID: CMPersistentTrackID?,
        renderSize: CGSize,
        renderState: StudioRenderState,
        projectDirectory: URL
    ) {
        self.timeRange = timeRange
        self.screenTrackID = screenTrackID
        self.cameraTrackID = cameraTrackID
        self.renderSize = renderSize
        self.renderState = renderState
        self.projectDirectory = projectDirectory
        var trackIDs: [NSValue] = [NSNumber(value: screenTrackID)]
        if let cameraTrackID {
            trackIDs.append(NSNumber(value: cameraTrackID))
        }
        requiredSourceTrackIDs = trackIDs
        super.init()
    }
}

// MARK: - Image Cache

/// Small least-recently-used caches for the images the compositor draws with Core Graphics.
///
/// They are shared rather than owned by a compositor because AVFoundation can create a new
/// compositor whenever a player item gets a new video composition, which happens on every edit
/// made while paused.
private final class StudioCompositorImageCache: @unchecked Sendable {
    enum Kind: Hashable {
        case mask
        case shadow
        case border
        case background
        case branding
    }

    static let shared = StudioCompositorImageCache()

    private struct Bucket {
        var values: [String: CIImage] = [:]
        var order: [String] = []
        let capacity: Int
    }

    private let lock = NSLock()
    private var buckets: [Kind: Bucket] = [
        .mask: Bucket(capacity: 6),
        .shadow: Bucket(capacity: 6),
        .border: Bucket(capacity: 6),
        .background: Bucket(capacity: 2),
        .branding: Bucket(capacity: 2),
    ]

    /// Returns the cached image for `key`, or makes, stores, and returns it. `make` runs outside the
    /// lock, so two threads may build the same image once; the result is the same either way.
    func image(for key: String, in kind: Kind, make: () -> CIImage?) -> CIImage? {
        lock.lock()
        if var bucket = buckets[kind], let cached = bucket.values[key] {
            bucket.order.removeAll { $0 == key }
            bucket.order.append(key)
            buckets[kind] = bucket
            lock.unlock()
            return cached
        }
        lock.unlock()

        guard let image = make() else { return nil }

        lock.lock()
        if var bucket = buckets[kind] {
            bucket.values[key] = image
            bucket.order.removeAll { $0 == key }
            bucket.order.append(key)
            while bucket.order.count > bucket.capacity {
                let removed = bucket.order.removeFirst()
                bucket.values.removeValue(forKey: removed)
            }
            buckets[kind] = bucket
        }
        lock.unlock()
        return image
    }
}

// MARK: - Compositor

final class StudioCompositor: NSObject, AVVideoCompositing {
    // Color management is off so screen pixels pass through unchanged, and so gradients and
    // blending work on encoded values the way the Windows renderer and the screenshot editor do.
    private static let sharedContext = CIContext(options: [
        .workingColorSpace: NSNull(),
        .outputColorSpace: NSNull(),
    ])
    private static let backgroundImageMaxPixelSize = 3840

    private let lock = NSLock()
    private var renderContext: AVVideoCompositionRenderContext?

    var sourcePixelBufferAttributes: [String: any Sendable]? {
        [
            kCVPixelBufferPixelFormatTypeKey as String: Int(kCVPixelFormatType_32BGRA),
        ]
    }

    var requiredPixelBufferAttributesForRenderContext: [String: any Sendable] {
        [
            kCVPixelBufferPixelFormatTypeKey as String: Int(kCVPixelFormatType_32BGRA),
        ]
    }

    func renderContextChanged(_ newRenderContext: AVVideoCompositionRenderContext) {
        lock.lock()
        renderContext = newRenderContext
        lock.unlock()
    }

    func startRequest(_ request: AVAsynchronousVideoCompositionRequest) {
        guard let instruction = request.videoCompositionInstruction as? StudioVideoCompositionInstruction else {
            request.finish(with: StudioCompositionBuilder.Error.unsupportedInstruction)
            return
        }

        lock.lock()
        let currentRenderContext = renderContext
        lock.unlock()

        guard let outputBuffer = currentRenderContext?.newPixelBuffer() else {
            request.finish(with: StudioCompositionBuilder.Error.outputBufferUnavailable)
            return
        }

        let renderSize = instruction.renderSize
        let snapshot = instruction.renderState.read()
        let sourceTime = snapshot.timeMap.outputToSource(request.compositionTime.seconds)
        let frame = StudioLayoutResolver.resolve(
            project: snapshot.project,
            time: sourceTime,
            canvasWidth: Double(renderSize.width),
            canvasHeight: Double(renderSize.height)
        )
        let outputExtent = CGRect(origin: .zero, size: renderSize)
        var output = drawBackground(
            project: snapshot.project,
            instruction: instruction,
            extent: outputExtent
        )

        if let screen = frame.screen,
           let screenBuffer = request.sourceFrame(byTrackID: instruction.screenTrackID) {
            // Whole-pixel layer rectangles keep the shape mask and the layer exactly on top of each other.
            var alignedScreen = screen
            alignedScreen.rect = pixelAligned(screen.rect)
            let screenShape: StudioCameraShape = screen.cornerRadius > 0 ? .roundedRectangle : .rectangle
            output = drawShadow(
                for: alignedScreen.rect,
                radius: screen.cornerRadius,
                shadow: screen.shadow,
                over: output,
                renderSize: renderSize,
                shape: screenShape
            )
            let screenImage = layerImage(
                sourceImage: CIImage(cvPixelBuffer: screenBuffer),
                source: screen.source,
                destination: alignedScreen.rect,
                renderSize: renderSize,
                mirrored: false
            )
            output = composite(
                screenImage,
                over: output,
                in: alignedScreen.rect,
                radius: screen.cornerRadius,
                shape: screenShape,
                renderSize: renderSize
            )

            if snapshot.project.overlays.clicks.enabled {
                output = drawClickRings(
                    StudioRenderGeometry.activeClickRings(
                        at: sourceTime,
                        events: snapshot.events,
                        frame: frame,
                        overlay: snapshot.project.overlays.clicks,
                        screenSourceWidth: snapshot.project.sources.screen.width
                    ),
                    color: snapshot.project.overlays.clicks.color,
                    clippedTo: alignedScreen,
                    shape: screenShape,
                    over: output,
                    renderSize: renderSize
                )
            }
        }

        if let camera = frame.camera,
           camera.visible,
           let cameraTrackID = instruction.cameraTrackID,
           let cameraBuffer = request.sourceFrame(byTrackID: cameraTrackID) {
            let cameraRect = pixelAligned(camera.rect)
            output = drawShadow(
                for: cameraRect,
                radius: camera.cornerRadius,
                shadow: camera.shadow,
                over: output,
                renderSize: renderSize,
                shape: camera.shape
            )
            let cameraImage = layerImage(
                sourceImage: CIImage(cvPixelBuffer: cameraBuffer),
                source: camera.source,
                destination: cameraRect,
                renderSize: renderSize,
                mirrored: camera.mirror
            )
            output = composite(
                cameraImage,
                over: output,
                in: cameraRect,
                radius: camera.cornerRadius,
                shape: camera.shape,
                renderSize: renderSize
            )
            if camera.borderWidth > 0 {
                output = drawBorder(
                    in: cameraRect,
                    radius: camera.cornerRadius,
                    shape: camera.shape,
                    width: camera.borderWidth,
                    color: snapshot.project.camera.borderColor,
                    over: output,
                    renderSize: renderSize
                )
            }
        }

        if snapshot.project.overlays.branding {
            output = brandingLayer(renderSize: renderSize)
                .composited(over: output)
        }

        output = output.cropped(to: outputExtent)
        Self.sharedContext.render(output, to: outputBuffer, bounds: outputExtent, colorSpace: nil)
        request.finish(withComposedVideoFrame: outputBuffer)
    }

    func cancelAllPendingVideoCompositionRequests() {}

    // MARK: - Drawing

    private func drawBackground(
        project: StudioProject,
        instruction: StudioVideoCompositionInstruction,
        extent: CGRect
    ) -> CIImage {
        let background = project.canvas.background
        switch background.style {
        case .none:
            return CIImage(color: .black).cropped(to: extent)
        case .solid:
            return CIImage(color: ciColor(background.primary, fallback: .black)).cropped(to: extent)
        case .gradient:
            let endpoints = StudioRenderGeometry.gradientEndpoints(renderSize: instruction.renderSize)
            let gradient = CIFilter(name: "CILinearGradient", parameters: [
                "inputPoint0": CIVector(cgPoint: endpoints.start),
                "inputPoint1": CIVector(cgPoint: endpoints.end),
                "inputColor0": ciColor(background.primary, fallback: .black),
                "inputColor1": ciColor(background.secondary ?? background.primary, fallback: .black),
            ])?.outputImage
            return (gradient ?? CIImage(color: .black)).cropped(to: extent)
        case .image:
            guard let imageName = background.image,
                  StudioJSON.isPlainFileName(imageName),
                  let image = backgroundImage(at: instruction.projectDirectory.appendingPathComponent(imageName)) else {
                return CIImage(color: ciColor(background.primary, fallback: .black)).cropped(to: extent)
            }
            return aspectFill(image, in: extent)
        }
    }

    /// The source scaled and positioned so its crop fills `destination`. The result extends a little
    /// past `destination` (edge pixels repeated) so a shape mask never uncovers transparency.
    private func layerImage(
        sourceImage: CIImage,
        source: StudioRect,
        destination: StudioRect,
        renderSize: CGSize,
        mirrored: Bool
    ) -> CIImage {
        let transform = StudioRenderGeometry.sourceToDestinationTransform(
            sourceSize: sourceImage.extent.size,
            sourceCrop: source,
            destinationRect: destination,
            renderSize: renderSize,
            mirrored: mirrored
        )
        let destinationRect = StudioRenderGeometry.ciRect(forTopLeftRect: destination, renderSize: renderSize)
        return sourceImage
            .clampedToExtent()
            .transformed(by: transform.cgAffineTransform)
            .cropped(to: destinationRect.insetBy(dx: -2, dy: -2))
    }

    private func composite(
        _ foreground: CIImage,
        over background: CIImage,
        in rect: StudioRect,
        radius: Double,
        shape: StudioCameraShape,
        renderSize: CGSize
    ) -> CIImage {
        guard let mask = translatedMask(for: rect, radius: radius, shape: shape, renderSize: renderSize) else {
            let destinationRect = StudioRenderGeometry.ciRect(forTopLeftRect: rect, renderSize: renderSize)
            return foreground.cropped(to: destinationRect).composited(over: background)
        }
        // Masks are white on transparent, so the alpha channel carries the coverage.
        return foreground.applyingFilter(
            "CIBlendWithAlphaMask",
            parameters: [
                kCIInputBackgroundImageKey: background,
                kCIInputMaskImageKey: mask,
            ]
        )
    }

    private func drawShadow(
        for rect: StudioRect,
        radius: Double,
        shadow: StudioResolvedShadow,
        over image: CIImage,
        renderSize: CGSize,
        shape: StudioCameraShape
    ) -> CIImage {
        let opacity = StudioCanvasMath.clamped(shadow.opacity, 0, 1)
        guard opacity > 0 else { return image }
        var shadowRect = rect
        shadowRect.y += shadow.offsetY
        let layerSize = roundedSize(for: shadowRect)
        guard let mask = blurredShadowMask(size: layerSize, radius: radius, shape: shape, blur: max(0, shadow.blur)) else {
            return image
        }
        let destination = StudioRenderGeometry.ciRect(forTopLeftRect: shadowRect, renderSize: renderSize)
        // The mask is white with alpha equal to coverage. Zeroing the color and scaling the alpha
        // turns it into black at the shadow's opacity, which is then drawn source-over.
        let shadowImage = mask
            .transformed(by: CGAffineTransform(translationX: destination.minX, y: destination.minY))
            .applyingFilter(
                "CIColorMatrix",
                parameters: [
                    "inputRVector": CIVector(x: 0, y: 0, z: 0, w: 0),
                    "inputGVector": CIVector(x: 0, y: 0, z: 0, w: 0),
                    "inputBVector": CIVector(x: 0, y: 0, z: 0, w: 0),
                    "inputAVector": CIVector(x: 0, y: 0, z: 0, w: CGFloat(opacity)),
                ]
            )
            .cropped(to: CGRect(origin: .zero, size: renderSize))
        return shadowImage.composited(over: image)
    }

    private func drawClickRings(
        _ rings: [StudioRenderClickRing],
        color: String,
        clippedTo screen: StudioResolvedScreen,
        shape: StudioCameraShape,
        over image: CIImage,
        renderSize: CGSize
    ) -> CIImage {
        guard !rings.isEmpty else { return image }
        var output = image
        let clipRect = StudioRenderGeometry.ciRect(forTopLeftRect: screen.rect, renderSize: renderSize)
        let clipMask = translatedMask(
            for: screen.rect,
            radius: screen.cornerRadius,
            shape: shape,
            renderSize: renderSize
        )
        let ringColor = cgColor(color, alpha: 1)
        let clear = CIImage(color: CIColor(red: 0, green: 0, blue: 0, alpha: 0))
            .cropped(to: CGRect(origin: .zero, size: renderSize))
        for ring in rings {
            guard let ringImage = clickRingImage(ring, color: ringColor, renderSize: renderSize) else { continue }
            let clippedRing: CIImage
            if let clipMask {
                clippedRing = ringImage.applyingFilter(
                    "CIBlendWithAlphaMask",
                    parameters: [
                        kCIInputBackgroundImageKey: clear,
                        kCIInputMaskImageKey: clipMask,
                    ]
                )
            } else {
                clippedRing = ringImage.cropped(to: clipRect)
            }
            output = clippedRing.composited(over: output)
        }
        return output
    }

    private func drawBorder(
        in rect: StudioRect,
        radius: Double,
        shape: StudioCameraShape,
        width: Double,
        color: String,
        over image: CIImage,
        renderSize: CGSize
    ) -> CIImage {
        let layerSize = roundedSize(for: rect)
        guard let border = borderImage(
            size: layerSize,
            radius: radius,
            shape: shape,
            width: width,
            color: color
        ) else {
            return image
        }
        let destination = StudioRenderGeometry.ciRect(forTopLeftRect: rect, renderSize: renderSize)
        return border
            .transformed(by: CGAffineTransform(translationX: destination.minX, y: destination.minY))
            .composited(over: image)
    }

    // MARK: - Cached Images

    /// The shape mask moved onto `rect`, or nil for a plain rectangle, which needs no mask.
    private func translatedMask(
        for rect: StudioRect,
        radius: Double,
        shape: StudioCameraShape,
        renderSize: CGSize
    ) -> CIImage? {
        guard shape != .rectangle else { return nil }
        let layerSize = roundedSize(for: rect)
        guard let mask = maskAtOrigin(size: layerSize, radius: radius, shape: shape) else { return nil }
        let destination = StudioRenderGeometry.ciRect(forTopLeftRect: rect, renderSize: renderSize)
        return mask.transformed(by: CGAffineTransform(translationX: destination.minX, y: destination.minY))
    }

    /// Masks are built at the origin and moved into place when drawn, so dragging a layer reuses them.
    private func maskAtOrigin(size: CGSize, radius: Double, shape: StudioCameraShape) -> CIImage? {
        let key = "\(shape.rawValue)-\(cacheSizeKey(size))-\(Int(rounded(radius)))"
        return StudioCompositorImageCache.shared.image(for: key, in: .mask) {
            shapeImage(
                size: size,
                radius: radius,
                shape: shape,
                fillColor: CGColor(red: 1, green: 1, blue: 1, alpha: 1),
                strokeColor: nil,
                lineWidth: 0
            )
        }
    }

    private func blurredShadowMask(
        size: CGSize,
        radius: Double,
        shape: StudioCameraShape,
        blur: Double
    ) -> CIImage? {
        guard let mask = maskAtOrigin(size: size, radius: radius, shape: shape) else { return nil }
        let roundedBlur = rounded(blur)
        guard roundedBlur > 0 else { return mask }
        let key = "\(shape.rawValue)-\(cacheSizeKey(size))-\(Int(rounded(radius)))-\(Int(roundedBlur))"
        return StudioCompositorImageCache.shared.image(for: key, in: .shadow) {
            mask.applyingFilter("CIGaussianBlur", parameters: [kCIInputRadiusKey: roundedBlur])
        }
    }

    private func borderImage(
        size: CGSize,
        radius: Double,
        shape: StudioCameraShape,
        width: Double,
        color: String
    ) -> CIImage? {
        let strokeWidth = max(1, rounded(width))
        let key = "\(shape.rawValue)-\(cacheSizeKey(size))-\(Int(rounded(radius)))-\(Int(strokeWidth))-\(color)"
        return StudioCompositorImageCache.shared.image(for: key, in: .border) {
            shapeImage(
                size: size,
                radius: radius,
                shape: shape,
                fillColor: nil,
                strokeColor: cgColor(color, alpha: 1),
                lineWidth: strokeWidth
            )
        }
    }

    /// The same badge normal recordings get, drawn once per render size on a transparent canvas.
    private func brandingLayer(renderSize: CGSize) -> CIImage {
        let extent = CGRect(origin: .zero, size: renderSize)
        let transparent = CIImage(color: CIColor(red: 0, green: 0, blue: 0, alpha: 0)).cropped(to: extent)
        let branded = StudioCompositorImageCache.shared.image(for: cacheSizeKey(renderSize), in: .branding) {
            BrandingOverlayProcessor.applyBranding(to: transparent, renderSize: renderSize).cropped(to: extent)
        }
        return branded ?? transparent
    }

    /// Decodes the background image once, upright and no larger than a 4K canvas needs. The file's
    /// modification date is part of the key so a replaced image with the same name is picked up.
    private func backgroundImage(at url: URL) -> CIImage? {
        let modified = (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
        let key = "\(url.standardizedFileURL.path)|\(modified?.timeIntervalSince1970 ?? 0)"
        return StudioCompositorImageCache.shared.image(for: key, in: .background) {
            let options: [CFString: Any] = [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceThumbnailMaxPixelSize: Self.backgroundImageMaxPixelSize,
                kCGImageSourceShouldCacheImmediately: true,
            ]
            guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
                  let image = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) else {
                return nil
            }
            return CIImage(cgImage: image)
        }
    }

    // MARK: - Image Drawing Helpers

    private func clickRingImage(_ ring: StudioRenderClickRing, color: CGColor, renderSize: CGSize) -> CIImage? {
        let lineWidth = max(0, ring.strokeWidth)
        let radius = max(0, ring.radius)
        guard lineWidth > 0, ring.opacity > 0 else { return nil }
        // One spare pixel on every side keeps the antialiased edge inside the bitmap.
        let extent = ((radius + lineWidth / 2 + 1) * 2).rounded(.up)
        guard extent.isFinite, extent >= 1, extent <= 8192 else { return nil }
        let side = Int(extent)
        guard let context = CGContext(
            data: nil,
            width: side,
            height: side,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue
        ) else {
            return nil
        }
        let center = extent / 2
        context.setStrokeColor(color.copy(alpha: CGFloat(ring.opacity)) ?? color)
        context.setLineWidth(CGFloat(lineWidth))
        context.strokeEllipse(in: CGRect(x: center - radius, y: center - radius, width: radius * 2, height: radius * 2))
        guard let cgImage = context.makeImage() else { return nil }
        let ciCenter = StudioRenderGeometry.ciPoint(forTopLeftPoint: ring.center, renderSize: renderSize)
        return CIImage(cgImage: cgImage).transformed(by: CGAffineTransform(
            translationX: ciCenter.x - CGFloat(center),
            y: ciCenter.y - CGFloat(center)
        ))
    }

    /// A filled or stroked shape at the origin, white or colored on a transparent background.
    private func shapeImage(
        size: CGSize,
        radius: Double,
        shape: StudioCameraShape,
        fillColor: CGColor?,
        strokeColor: CGColor?,
        lineWidth: Double
    ) -> CIImage? {
        let width = Double(size.width).rounded(.up)
        let height = Double(size.height).rounded(.up)
        guard width.isFinite, height.isFinite, width <= 16384, height <= 16384 else { return nil }
        let pixelWidth = max(1, Int(width))
        let pixelHeight = max(1, Int(height))
        guard let context = CGContext(
            data: nil,
            width: pixelWidth,
            height: pixelHeight,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else {
            return nil
        }

        let inset: Double = strokeColor == nil ? 0 : max(0, lineWidth / 2)
        let rect = CGRect(
            x: inset,
            y: inset,
            width: max(0, Double(pixelWidth) - inset * 2),
            height: max(0, Double(pixelHeight) - inset * 2)
        )
        context.addPath(cgPath(for: rect, radius: max(0, radius - inset), shape: shape))
        if let fillColor {
            context.setFillColor(fillColor)
            context.fillPath()
        } else if let strokeColor {
            context.setStrokeColor(strokeColor)
            context.setLineWidth(CGFloat(lineWidth))
            context.strokePath()
        }
        guard let cgImage = context.makeImage() else { return nil }
        return CIImage(cgImage: cgImage)
    }

    private func cgPath(for rect: CGRect, radius: Double, shape: StudioCameraShape) -> CGPath {
        switch shape {
        case .circle:
            return CGPath(ellipseIn: rect, transform: nil)
        case .roundedRectangle:
            let limit = Double(min(rect.width, rect.height)) / 2
            let clampedRadius = CGFloat(max(0, min(radius, limit)))
            return CGPath(roundedRect: rect, cornerWidth: clampedRadius, cornerHeight: clampedRadius, transform: nil)
        case .squircle:
            let path = CGMutablePath()
            let points = StudioRenderGeometry.squirclePoints(
                in: StudioRect(
                    x: Double(rect.minX),
                    y: Double(rect.minY),
                    width: Double(rect.width),
                    height: Double(rect.height)
                ),
                pointCount: 96
            )
            if let first = points.first {
                path.move(to: first)
                for point in points.dropFirst() {
                    path.addLine(to: point)
                }
                path.closeSubpath()
            }
            return path
        case .rectangle:
            return CGPath(rect: rect, transform: nil)
        }
    }

    private func aspectFill(_ image: CIImage, in extent: CGRect) -> CIImage {
        guard image.extent.width > 0, image.extent.height > 0, extent.width > 0, extent.height > 0 else {
            return CIImage(color: .black).cropped(to: extent)
        }
        let scale = max(extent.width / image.extent.width, extent.height / image.extent.height)
        let scaled = CGSize(width: image.extent.width * scale, height: image.extent.height * scale)
        return image
            .transformed(by: CGAffineTransform(translationX: -image.extent.minX, y: -image.extent.minY))
            .transformed(by: CGAffineTransform(scaleX: scale, y: scale))
            .transformed(by: CGAffineTransform(
                translationX: extent.minX + (extent.width - scaled.width) / 2,
                y: extent.minY + (extent.height - scaled.height) / 2
            ))
            .cropped(to: extent)
    }

    // MARK: - Colors and Sizes

    private func ciColor(_ hex: String, fallback: CIColor) -> CIColor {
        guard let components = colorComponents(hex) else { return fallback }
        return CIColor(red: components.r, green: components.g, blue: components.b, alpha: components.a)
    }

    private func cgColor(_ hex: String, alpha: CGFloat) -> CGColor {
        let components = colorComponents(hex)
        return CGColor(
            red: components?.r ?? 1,
            green: components?.g ?? 1,
            blue: components?.b ?? 1,
            alpha: alpha * (components?.a ?? 1)
        )
    }

    /// Parses `#RRGGBB` or `#RRGGBBAA`.
    private func colorComponents(_ hex: String) -> (r: CGFloat, g: CGFloat, b: CGFloat, a: CGFloat)? {
        var value = hex.trimmingCharacters(in: .whitespacesAndNewlines)
        if value.hasPrefix("#") {
            value.removeFirst()
        }
        guard value.count == 6 || value.count == 8,
              let integer = UInt64(value, radix: 16) else {
            return nil
        }
        if value.count == 8 {
            return (
                r: CGFloat((integer >> 24) & 0xFF) / 255,
                g: CGFloat((integer >> 16) & 0xFF) / 255,
                b: CGFloat((integer >> 8) & 0xFF) / 255,
                a: CGFloat(integer & 0xFF) / 255
            )
        }
        return (
            r: CGFloat((integer >> 16) & 0xFF) / 255,
            g: CGFloat((integer >> 8) & 0xFF) / 255,
            b: CGFloat(integer & 0xFF) / 255,
            a: 1
        )
    }

    /// The rectangle with its edges moved to the nearest whole pixel.
    private func pixelAligned(_ rect: StudioRect) -> StudioRect {
        let minX = rect.x.rounded()
        let minY = rect.y.rounded()
        let maxX = (rect.x + rect.width).rounded()
        let maxY = (rect.y + rect.height).rounded()
        return StudioRect(x: minX, y: minY, width: max(1, maxX - minX), height: max(1, maxY - minY))
    }

    private func roundedSize(for rect: StudioRect) -> CGSize {
        CGSize(width: max(1, rounded(rect.width)), height: max(1, rounded(rect.height)))
    }

    private func rounded(_ value: Double) -> Double {
        value.rounded()
    }

    private func cacheSizeKey(_ size: CGSize) -> String {
        "\(Int(Double(size.width).rounded()))x\(Int(Double(size.height).rounded()))"
    }
}

private extension StudioRenderAffineTransform {
    var cgAffineTransform: CGAffineTransform {
        CGAffineTransform(
            a: CGFloat(a),
            b: CGFloat(b),
            c: CGFloat(c),
            d: CGFloat(d),
            tx: CGFloat(tx),
            ty: CGFloat(ty)
        )
    }
}