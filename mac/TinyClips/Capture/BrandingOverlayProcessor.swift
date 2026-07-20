import AppKit
import AVFoundation
import CoreImage
import CoreGraphics
import CoreText
import QuartzCore

// MARK: - Branding Overlay Processor

/// Renders a "Captured on Tiny Clips" watermark in the bottom-right corner of
/// screenshots, video recordings, and GIFs.
enum BrandingOverlayProcessor {
    private static let overlayText = "Captured on Tiny Clips"

    struct WebcamPositionEvent: Sendable {
        let time: CMTime
        let corner: String
    }

    struct WebcamOverlayOptions: Sendable {
        let videoURL: URL
        let shape: String
        let corner: String
        let size: String
        let cornerRadiusOverride: CGFloat?
        /// Offset of the webcam's first frame relative to the screen/audio timeline
        /// origin (webcamFirstPTS - screenFirstPTS). A positive value means the
        /// webcam started recording later (e.g. camera warm-up) and must be shifted
        /// forward so it stays in sync with the audio. Defaults to `.zero`.
        var startOffset: CMTime = .zero
        var positionEvents: [WebcamPositionEvent] = []
    }

    enum CompositionError: LocalizedError {
        case sourceVideoTrackMissing
        case videoCompositionTrackCreationFailed
        case webcamCompositionTrackCreationFailed
        case webcamVideoTrackMissing(URL)
        case exportSessionCreationFailed

        var errorDescription: String? {
            switch self {
            case .sourceVideoTrackMissing:
                return "Could not load the source video track for export composition."
            case .videoCompositionTrackCreationFailed:
                return "Could not create a mutable video track for export composition."
            case .webcamCompositionTrackCreationFailed:
                return "Could not create a mutable webcam track for export composition."
            case let .webcamVideoTrackMissing(url):
                return "Webcam overlay video is missing or invalid: \(url.lastPathComponent)."
            case .exportSessionCreationFailed:
                return "Could not create the video export session."
            }
        }
    }

    private enum WebcamOverlayShape: String, Equatable {
        case circle
        case rounded
        case rectangle

        init(rawValue: String) {
            switch rawValue.lowercased() {
            case "rounded", "roundedrectangle":
                self = .rounded
            case "rectangle":
                self = .rectangle
            default:
                self = .circle
            }
        }
    }

    private enum WebcamOverlayCorner: String {
        case topLeft
        case topRight
        case bottomLeft
        case bottomRight

        init(rawValue: String) {
            switch rawValue.lowercased() {
            case "topleft":
                self = .topLeft
            case "topright":
                self = .topRight
            case "bottomleft":
                self = .bottomLeft
            default:
                self = .bottomRight
            }
        }
    }

    private enum WebcamOverlaySizePreset: String {
        case small
        case medium
        case large

        init(rawValue: String) {
            switch rawValue.lowercased() {
            case "small":
                self = .small
            case "large":
                self = .large
            default:
                self = .medium
            }
        }
    }

    // MARK: - Screenshot / Image

    /// Composites the branding overlay onto a CGImage and returns the result.
    static func applyToImage(_ image: CGImage) -> CGImage {
        let width = image.width
        let height = image.height

        guard let context = CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue
        ) else { return image }

        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        drawTextOverlay(in: context, width: width, height: height)
        return context.makeImage() ?? image
    }

    // MARK: - GIF

    /// Composites the branding overlay onto every frame of a GIF.
    static func applyToGifData(_ gifData: GifCaptureData) -> GifCaptureData {
        guard !gifData.frames.isEmpty else { return gifData }
        let processedFrames = gifData.frames.map { applyToImage($0) }
        return GifCaptureData(frames: processedFrames, frameDelay: gifData.frameDelay, maxWidth: gifData.maxWidth)
    }

    // MARK: - Video

    /// Burns the branding overlay into a video file using AVVideoComposition and
    /// CoreAnimation layers, writing the result to `outputURL`.
    static func overlayOnVideo(
        sourceURL: URL,
        outputURL: URL,
        includeBranding: Bool = true,
        webcamOverlay: WebcamOverlayOptions? = nil,
        onProgress: ((Double) -> Void)? = nil
    ) async throws -> URL {
        let asset = AVURLAsset(url: sourceURL)
        onProgress?(0.1)
        guard let videoTrack = try await asset.loadTracks(withMediaType: .video).first else {
            throw CompositionError.sourceVideoTrackMissing
        }

        let assetDuration = try await asset.load(.duration)
        let preferredTransform = try await videoTrack.load(.preferredTransform)
        onProgress?(0.25)

        // When a webcam was recorded, ScreenCaptureKit typically starts capturing
        // before the camera (and microphone) finish warming up, leaving a leading
        // segment with screen-only video and silence. The webcam's positive start
        // offset measures that gap, so trim it from the screen and audio tracks to
        // begin the clip once everything is rolling.
        let leadingTrim: CMTime = {
            guard let webcamOverlay,
                  FileManager.default.fileExists(atPath: webcamOverlay.videoURL.path),
                  webcamOverlay.startOffset > .zero else {
                return .zero
            }
            return CMTimeMinimum(webcamOverlay.startOffset, assetDuration)
        }()
        let trimmedDuration = CMTimeSubtract(assetDuration, leadingTrim)

        let composition = AVMutableComposition()
        guard let compositionVideoTrack = composition.addMutableTrack(
            withMediaType: .video,
            preferredTrackID: kCMPersistentTrackID_Invalid
        ) else { throw CompositionError.videoCompositionTrackCreationFailed }

        try compositionVideoTrack.insertTimeRange(
            CMTimeRange(start: leadingTrim, duration: trimmedDuration),
            of: videoTrack,
            at: .zero
        )
        compositionVideoTrack.preferredTransform = preferredTransform

        for audioTrack in try await asset.loadTracks(withMediaType: .audio) {
            if let compositionAudioTrack = composition.addMutableTrack(
                withMediaType: .audio,
                preferredTrackID: kCMPersistentTrackID_Invalid
            ) {
                try compositionAudioTrack.insertTimeRange(
                    CMTimeRange(start: leadingTrim, duration: trimmedDuration),
                    of: audioTrack,
                    at: .zero
                )
            }
        }

        let naturalSize = try await videoTrack.load(.naturalSize)
        let transformedSize = naturalSize.applying(preferredTransform)
        let renderSize = CGSize(width: abs(transformedSize.width), height: abs(transformedSize.height))

        let videoComposition = AVMutableVideoComposition()
        videoComposition.renderSize = renderSize
        let nominalFrameRate = try await videoTrack.load(.nominalFrameRate)
        let sourceTimescale = max(30, Int32(nominalFrameRate.rounded(.up)))
        videoComposition.frameDuration = CMTime(value: 1, timescale: sourceTimescale)

        let instruction = AVMutableVideoCompositionInstruction()
        instruction.timeRange = CMTimeRange(start: .zero, duration: trimmedDuration)
        let screenLayerInstruction = AVMutableVideoCompositionLayerInstruction(assetTrack: compositionVideoTrack)
        screenLayerInstruction.setTransform(preferredTransform, at: .zero)

        if let webcamOverlay, FileManager.default.fileExists(atPath: webcamOverlay.videoURL.path) {
            let webcamAsset = AVURLAsset(url: webcamOverlay.videoURL)
            guard let webcamTrack = try await webcamAsset.loadTracks(withMediaType: .video).first else {
                throw CompositionError.webcamVideoTrackMissing(webcamOverlay.videoURL)
            }

            let webcamDuration = try await webcamAsset.load(.duration)
            let webcamPreferredTransform = try await webcamTrack.load(.preferredTransform)
            let webcamNaturalSize = try await webcamTrack.load(.naturalSize)
            let webcamOrientedSize = orientedSize(
                for: webcamNaturalSize,
                preferredTransform: webcamPreferredTransform
            )

            guard let compositionWebcamTrack = composition.addMutableTrack(
                withMediaType: .video,
                preferredTrackID: kCMPersistentTrackID_Invalid
            ) else {
                throw CompositionError.webcamCompositionTrackCreationFailed
            }

            // Place the webcam on the (already leading-trimmed) screen/audio timeline.
            // For a positive offset the leading gap was trimmed from the screen and
            // audio above, so the webcam now starts at composition time zero. For a
            // negative offset the webcam led the screen, so trim its leading frames.
            let offset = webcamOverlay.startOffset
            let webcamSourceStart = offset < .zero ? CMTimeMultiply(offset, multiplier: -1) : .zero
            let availableWebcam = CMTimeSubtract(webcamDuration, webcamSourceStart)
            let webcamUsableDuration = CMTimeMinimum(trimmedDuration, availableWebcam)

            if webcamUsableDuration > .zero {
                let webcamTimeRange = CMTimeRange(start: webcamSourceStart, duration: webcamUsableDuration)
                try compositionWebcamTrack.insertTimeRange(
                    webcamTimeRange,
                    of: webcamTrack,
                    at: .zero
                )
            }

            let webcamShape = WebcamOverlayShape(rawValue: webcamOverlay.shape)
            let positionEvents = webcamOverlay.positionEvents.isEmpty
                ? [WebcamPositionEvent(time: .zero, corner: webcamOverlay.corner)]
                : webcamOverlay.positionEvents
            let webcamPlacements = positionEvents.map { event in
                let overlayFrame = webcamOverlayFrame(
                    renderSize: renderSize,
                    webcamSize: webcamOrientedSize,
                    shape: webcamShape,
                    preset: WebcamOverlaySizePreset(rawValue: webcamOverlay.size),
                    corner: WebcamOverlayCorner(rawValue: event.corner)
                )
                let scale = max(
                    overlayFrame.width / max(webcamOrientedSize.width, 1),
                    overlayFrame.height / max(webcamOrientedSize.height, 1)
                )
                let scaledSize = CGSize(
                    width: webcamOrientedSize.width * scale,
                    height: webcamOrientedSize.height * scale
                )
                let ciOverlayFrame = ciFrame(forTopLeftFrame: overlayFrame, renderSize: renderSize)
                let webcamOffset = CGPoint(
                    x: ciOverlayFrame.midX - (scaledSize.width / 2),
                    y: ciOverlayFrame.midY - (scaledSize.height / 2)
                )
                let webcamTransform = normalizedTransform(
                    for: webcamPreferredTransform,
                    naturalSize: webcamNaturalSize
                )
                    .concatenating(CGAffineTransform(scaleX: scale, y: scale))
                    .concatenating(CGAffineTransform(translationX: webcamOffset.x, y: webcamOffset.y))

                return WebcamPlacement(
                    time: CMTimeMaximum(.zero, CMTimeSubtract(event.time, leadingTrim)),
                    transform: webcamTransform,
                    frame: ciOverlayFrame,
                    mask: webcamMaskImage(
                        renderSize: renderSize,
                        frame: ciOverlayFrame,
                        shape: webcamShape,
                        cornerRadius: webcamCornerRadius(
                            shape: webcamShape,
                            bounds: CGRect(origin: .zero, size: overlayFrame.size),
                            cornerRadiusOverride: webcamOverlay.cornerRadiusOverride
                        )
                    )
                )
            }

            videoComposition.customVideoCompositorClass = WebcamOverlayCompositor.self
            videoComposition.instructions = [
                WebcamOverlayInstruction(
                    timeRange: CMTimeRange(start: .zero, duration: trimmedDuration),
                    screenTrackID: compositionVideoTrack.trackID,
                    webcamTrackID: compositionWebcamTrack.trackID,
                    renderSize: renderSize,
                    webcamPlacements: webcamPlacements,
                    includeBranding: includeBranding
                )
            ]
        } else {
            instruction.layerInstructions = [screenLayerInstruction]
            videoComposition.instructions = [instruction]

            let parentLayer = CALayer()
            parentLayer.frame = CGRect(origin: .zero, size: renderSize)
            parentLayer.isGeometryFlipped = true

            let screenVideoLayer = CALayer()
            screenVideoLayer.frame = CGRect(origin: .zero, size: renderSize)
            parentLayer.addSublayer(screenVideoLayer)

            if includeBranding {
                addBrandingLayer(to: parentLayer, renderSize: renderSize)
            }

            videoComposition.animationTool = AVVideoCompositionCoreAnimationTool(
                postProcessingAsVideoLayer: screenVideoLayer,
                in: parentLayer
            )
        }

        try? FileManager.default.removeItem(at: outputURL)

        guard let exportSession = AVAssetExportSession(asset: composition, presetName: AVAssetExportPresetHighestQuality) else {
            throw CompositionError.exportSessionCreationFailed
        }
        exportSession.outputURL = outputURL
        exportSession.outputFileType = .mp4
        exportSession.videoComposition = videoComposition
        exportSession.shouldOptimizeForNetworkUse = true

        onProgress?(0.8)
        try await exportSession.export(to: outputURL, as: .mp4)

        if FileManager.default.fileExists(atPath: outputURL.path) {
            try? FileManager.default.removeItem(at: sourceURL)
        }
        onProgress?(1.0)
        return outputURL
    }

    // MARK: - Private helpers

    /// Adds the branding badge as a single image-backed CALayer.
    ///
    /// We render the entire pill (background + text) into a CGImage rather than
    /// using CATextLayer, because AVVideoCompositionCoreAnimationTool frequently
    /// fails to render CATextLayer text reliably.
    ///
    /// `parentLayer.isGeometryFlipped = true`, so (0,0) is the top-left corner and
    /// (renderSize.width, renderSize.height) is the bottom-right corner.
    private static func addBrandingLayer(to parentLayer: CALayer, renderSize: CGSize) {
        let scale: CGFloat = 2.0
        let fontSize = badgeFontSize(for: renderSize.height)
        let ctFont = makeBadgeFont(size: fontSize)
        let textSize = measureBadgeText(font: ctFont)
        let (bgWidth, bgHeight, _, margin) = badgePillSize(textSize: textSize, fontSize: fontSize)

        guard let badgeImage = renderBadgeImage(width: bgWidth, height: bgHeight, fontSize: fontSize, scale: scale) else {
            return
        }

        let bgX = renderSize.width - bgWidth - margin
        let bgY = renderSize.height - bgHeight - margin

        let badgeLayer = CALayer()
        badgeLayer.frame = CGRect(x: bgX, y: bgY, width: bgWidth, height: bgHeight)
        badgeLayer.contents = badgeImage
        badgeLayer.contentsScale = scale
        badgeLayer.contentsGravity = .resize
        parentLayer.addSublayer(badgeLayer)
    }

    /// Renders the full pill+text badge to a CGImage at `scale` density.
    private static func renderBadgeImage(width: CGFloat, height: CGFloat, fontSize: CGFloat, scale: CGFloat) -> CGImage? {
        let pixelWidth = Int(ceil(width * scale))
        let pixelHeight = Int(ceil(height * scale))
        guard pixelWidth > 0, pixelHeight > 0 else { return nil }

        guard let context = CGContext(
            data: nil,
            width: pixelWidth,
            height: pixelHeight,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue
        ) else { return nil }

        context.scaleBy(x: scale, y: scale)
        drawBadge(in: context, rect: CGRect(x: 0, y: 0, width: width, height: height), fontSize: fontSize)
        return context.makeImage()
    }

    /// Draws the branding badge directly into a CGContext using Core Text
    /// (thread-safe, unlike NSString/TextKit which silently no-ops off main).
    ///
    /// CGContext uses a bottom-left origin (y=0 at bottom), so the badge is placed
    /// with a small margin from the right and bottom edges.
    private static func drawTextOverlay(in context: CGContext, width: Int, height: Int) {
        let fontSize = badgeFontSize(for: CGFloat(height))
        let ctFont = makeBadgeFont(size: fontSize)
        let textSize = measureBadgeText(font: ctFont)
        let (bgWidth, bgHeight, _, margin) = badgePillSize(textSize: textSize, fontSize: fontSize)

        let bgRect = CGRect(x: CGFloat(width) - bgWidth - margin, y: margin, width: bgWidth, height: bgHeight)
        drawBadge(in: context, rect: bgRect, fontSize: fontSize)
    }

    /// Shared primitive: draws the pill background and centered text into `rect`.
    private static func drawBadge(in context: CGContext, rect: CGRect, fontSize: CGFloat) {
        let ctFont = makeBadgeFont(size: fontSize)
        let (_, _, paddingH, _) = badgePillSize(textSize: measureBadgeText(font: ctFont), fontSize: fontSize)

        // Background pill.
        let cornerRadius = rect.height / 3
        let path = CGPath(roundedRect: rect, cornerWidth: cornerRadius, cornerHeight: cornerRadius, transform: nil)
        context.setFillColor(NSColor.black.withAlphaComponent(0.5).cgColor)
        context.addPath(path)
        context.fillPath()

        // Text.
        let attributes: [NSAttributedString.Key: Any] = [
            kCTFontAttributeName as NSAttributedString.Key: ctFont,
            kCTForegroundColorAttributeName as NSAttributedString.Key: NSColor.white.cgColor,
        ]
        let attrString = NSAttributedString(string: overlayText, attributes: attributes)
        let line = CTLineCreateWithAttributedString(attrString)

        var ascent: CGFloat = 0
        var descent: CGFloat = 0
        var leading: CGFloat = 0
        _ = CTLineGetTypographicBounds(line, &ascent, &descent, &leading)

        let textX = rect.minX + paddingH
        let baselineY = rect.minY + (rect.height - (ascent + descent)) / 2 + descent

        context.saveGState()
        context.textMatrix = .identity
        context.textPosition = CGPoint(x: textX, y: baselineY)
        CTLineDraw(line, context)
        context.restoreGState()
    }

    // MARK: - Badge geometry helpers

    /// Font size scaled proportionally to image height, clamped to a sensible range.
    private static func badgeFontSize(for imageHeight: CGFloat) -> CGFloat {
        max(12.0, min(28.0, imageHeight / 50.0))
    }

    private static func makeBadgeFont(size: CGFloat) -> CTFont {
        CTFontCreateUIFontForLanguage(.system, size, nil)
            ?? CTFontCreateWithName("Helvetica" as CFString, size, nil)
    }

    private static func measureBadgeText(font: CTFont) -> CGSize {
        let attrString = NSAttributedString(string: overlayText, attributes: [
            kCTFontAttributeName as NSAttributedString.Key: font,
        ])
        let line = CTLineCreateWithAttributedString(attrString)
        var ascent: CGFloat = 0
        var descent: CGFloat = 0
        var leading: CGFloat = 0
        let width = CGFloat(CTLineGetTypographicBounds(line, &ascent, &descent, &leading))
        return CGSize(width: ceil(width), height: ceil(ascent + descent))
    }

    private static func ciFrame(forTopLeftFrame frame: CGRect, renderSize: CGSize) -> CGRect {
        CGRect(
            x: frame.minX,
            y: renderSize.height - frame.maxY,
            width: frame.width,
            height: frame.height
        )
    }

    private static func applyBranding(to image: CIImage, renderSize: CGSize) -> CIImage {
        let scale: CGFloat = 2.0
        let fontSize = badgeFontSize(for: renderSize.height)
        let ctFont = makeBadgeFont(size: fontSize)
        let textSize = measureBadgeText(font: ctFont)
        let (bgWidth, bgHeight, _, margin) = badgePillSize(textSize: textSize, fontSize: fontSize)

        guard let badgeImage = renderBadgeImage(width: bgWidth, height: bgHeight, fontSize: fontSize, scale: scale) else {
            return image
        }

        let badgeLayer = CIImage(cgImage: badgeImage)
            .transformed(by: CGAffineTransform(scaleX: 1 / scale, y: 1 / scale))
            .transformed(by: CGAffineTransform(
                translationX: renderSize.width - bgWidth - margin,
                y: margin
            ))

        return badgeLayer.composited(over: image)
    }

    private static func fillRenderExtent(with image: CIImage, renderSize: CGSize) -> CIImage {
        let outputExtent = CGRect(origin: .zero, size: renderSize)
        let extent = image.extent
        guard extent.width > 0, extent.height > 0, renderSize.width > 0, renderSize.height > 0 else {
            return CIImage(color: .black).cropped(to: outputExtent)
        }

        let scale = max(renderSize.width / extent.width, renderSize.height / extent.height)
        let scaledSize = CGSize(width: extent.width * scale, height: extent.height * scale)
        let transform = CGAffineTransform(translationX: -extent.minX, y: -extent.minY)
            .concatenating(CGAffineTransform(scaleX: scale, y: scale))
            .concatenating(CGAffineTransform(
                translationX: (renderSize.width - scaledSize.width) / 2,
                y: (renderSize.height - scaledSize.height) / 2
            ))

        return image
            .transformed(by: transform)
            .cropped(to: outputExtent)
    }

    private static func webcamMaskImage(
        renderSize: CGSize,
        frame: CGRect,
        shape: WebcamOverlayShape,
        cornerRadius: CGFloat?
    ) -> CIImage? {
        guard shape != .rectangle else { return nil }

        let width = Int(ceil(renderSize.width))
        let height = Int(ceil(renderSize.height))
        guard width > 0, height > 0 else { return nil }

        guard let context = CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceGray(),
            bitmapInfo: CGImageAlphaInfo.none.rawValue
        ) else {
            return nil
        }

        context.setFillColor(NSColor.black.cgColor)
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        context.setFillColor(NSColor.white.cgColor)

        switch shape {
        case .circle:
            context.fillEllipse(in: frame)
        case .rounded:
            let radius = cornerRadius ?? min(frame.width, frame.height) * 0.12
            let path = CGPath(
                roundedRect: frame,
                cornerWidth: max(0, min(radius, min(frame.width, frame.height) / 2)),
                cornerHeight: max(0, min(radius, min(frame.width, frame.height) / 2)),
                transform: nil
            )
            context.addPath(path)
            context.fillPath()
        case .rectangle:
            return nil
        }

        guard let image = context.makeImage() else { return nil }
        return CIImage(cgImage: image)
    }

    private struct WebcamPlacement {
        let time: CMTime
        let transform: CGAffineTransform
        let frame: CGRect
        let mask: CIImage?
    }

    private final class WebcamOverlayInstruction: NSObject, AVVideoCompositionInstructionProtocol {
        let timeRange: CMTimeRange
        let enablePostProcessing = false
        let containsTweening = false
        let requiredSourceTrackIDs: [NSValue]?
        let passthroughTrackID: CMPersistentTrackID = kCMPersistentTrackID_Invalid
        let screenTrackID: CMPersistentTrackID
        let webcamTrackID: CMPersistentTrackID
        let renderSize: CGSize
        let webcamPlacements: [WebcamPlacement]
        let includeBranding: Bool

        init(
            timeRange: CMTimeRange,
            screenTrackID: CMPersistentTrackID,
            webcamTrackID: CMPersistentTrackID,
            renderSize: CGSize,
            webcamPlacements: [WebcamPlacement],
            includeBranding: Bool
        ) {
            self.timeRange = timeRange
            self.screenTrackID = screenTrackID
            self.webcamTrackID = webcamTrackID
            self.renderSize = renderSize
            self.webcamPlacements = webcamPlacements
            self.includeBranding = includeBranding
            self.requiredSourceTrackIDs = [
                NSNumber(value: screenTrackID),
                NSNumber(value: webcamTrackID),
            ]
            super.init()
        }
    }

    private final class WebcamOverlayCompositor: NSObject, AVVideoCompositing {
        private let context = CIContext()
        private var renderContext: AVVideoCompositionRenderContext?
        private let lock = NSLock()

        var sourcePixelBufferAttributes: [String: Any]? {
            [
                kCVPixelBufferPixelFormatTypeKey as String: Int(kCVPixelFormatType_32BGRA),
            ]
        }

        var requiredPixelBufferAttributesForRenderContext: [String: Any] {
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
            guard
                let instruction = request.videoCompositionInstruction as? WebcamOverlayInstruction,
                let screenBuffer = request.sourceFrame(byTrackID: instruction.screenTrackID)
            else {
                request.finish(with: CompositionError.sourceVideoTrackMissing)
                return
            }

            lock.lock()
            let currentRenderContext = renderContext
            lock.unlock()

            guard let outputBuffer = currentRenderContext?.newPixelBuffer() else {
                request.finish(with: CompositionError.exportSessionCreationFailed)
                return
            }

            let outputExtent = CGRect(origin: .zero, size: instruction.renderSize)
            var outputImage = BrandingOverlayProcessor.fillRenderExtent(
                with: CIImage(cvPixelBuffer: screenBuffer),
                renderSize: instruction.renderSize
            )

            if let webcamBuffer = request.sourceFrame(byTrackID: instruction.webcamTrackID),
               let placement = instruction.webcamPlacements.last(where: {
                   CMTimeCompare($0.time, request.compositionTime) <= 0
               }) ?? instruction.webcamPlacements.first {
                let webcamImage = CIImage(cvPixelBuffer: webcamBuffer)
                    .transformed(by: placement.transform)
                    .cropped(to: placement.frame)

                if let mask = placement.mask {
                    outputImage = webcamImage.applyingFilter(
                        "CIBlendWithMask",
                        parameters: [
                            kCIInputBackgroundImageKey: outputImage,
                            kCIInputMaskImageKey: mask,
                        ]
                    )
                    .cropped(to: outputExtent)
                } else {
                    outputImage = webcamImage
                        .composited(over: outputImage)
                        .cropped(to: outputExtent)
                }
            }

            if instruction.includeBranding {
                outputImage = BrandingOverlayProcessor.applyBranding(
                    to: outputImage,
                    renderSize: instruction.renderSize
                )
                .cropped(to: outputExtent)
            }

            context.render(outputImage, to: outputBuffer, bounds: outputExtent, colorSpace: CGColorSpaceCreateDeviceRGB())
            request.finish(withComposedVideoFrame: outputBuffer)
        }

        func cancelAllPendingVideoCompositionRequests() {}
    }

    private static func badgePillSize(
        textSize: CGSize,
        fontSize: CGFloat
    ) -> (width: CGFloat, height: CGFloat, paddingH: CGFloat, margin: CGFloat) {
        let paddingH = fontSize * 0.7
        let paddingV = fontSize * 0.45
        let margin = fontSize
        return (textSize.width + paddingH * 2, textSize.height + paddingV * 2, paddingH, margin)
    }

    private static func webcamOverlayFrame(
        renderSize: CGSize,
        webcamSize: CGSize,
        shape: WebcamOverlayShape,
        preset: WebcamOverlaySizePreset,
        corner: WebcamOverlayCorner
    ) -> CGRect {
        let minDimension = min(renderSize.width, renderSize.height)
        let targetWidth = minDimension * webcamScale(for: preset)
        let aspectRatio = webcamSize.height / max(webcamSize.width, 1)
        let maxHeight = renderSize.height * 0.45
        let width: CGFloat
        let height: CGFloat
        if shape == .circle {
            let diameter = min(targetWidth, renderSize.width * 0.45, maxHeight)
            width = diameter
            height = diameter
        } else {
            width = min(targetWidth, renderSize.width * 0.45)
            height = min(width * aspectRatio, maxHeight)
        }
        let margin = max(16, minDimension * 0.03)

        let x: CGFloat
        let y: CGFloat
        switch corner {
        case .topLeft:
            x = margin
            y = margin
        case .topRight:
            x = renderSize.width - width - margin
            y = margin
        case .bottomLeft:
            x = margin
            y = renderSize.height - height - margin
        case .bottomRight:
            x = renderSize.width - width - margin
            y = renderSize.height - height - margin
        }

        return CGRect(x: x, y: y, width: width, height: height)
    }

    private static func webcamScale(for preset: WebcamOverlaySizePreset) -> CGFloat {
        switch preset {
        case .small: return 0.18
        case .medium: return 0.24
        case .large: return 0.30
        }
    }

    private static func webcamCornerRadius(
        shape: WebcamOverlayShape,
        bounds: CGRect,
        cornerRadiusOverride: CGFloat?
    ) -> CGFloat? {
        switch shape {
        case .rectangle:
            return nil
        case .circle:
            return min(bounds.width, bounds.height) / 2
        case .rounded:
            let radius = cornerRadiusOverride ?? (min(bounds.width, bounds.height) * 0.12)
            return max(0, min(radius, min(bounds.width, bounds.height) / 2))
        }
    }

    private static func orientedSize(
        for naturalSize: CGSize,
        preferredTransform: CGAffineTransform
    ) -> CGSize {
        let rect = CGRect(origin: .zero, size: naturalSize).applying(preferredTransform)
        return CGSize(width: abs(rect.width), height: abs(rect.height))
    }

    private static func normalizedTransform(
        for transform: CGAffineTransform,
        naturalSize: CGSize
    ) -> CGAffineTransform {
        let transformedRect = CGRect(origin: .zero, size: naturalSize).applying(transform)
        return transform.translatedBy(x: -transformedRect.minX, y: -transformedRect.minY)
    }
}
