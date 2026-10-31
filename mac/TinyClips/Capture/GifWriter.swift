import ScreenCaptureKit
import CoreMedia
import CoreImage
import ImageIO

class GifWriter: NSObject, @unchecked Sendable {
    private let writerID = UUID().uuidString
    private var stream: SCStream?
    private var frames: [CGImage] = []
    private var frameDelay: Double = 0.1
    private var maxWidth: CGFloat = 640
    private var isPaused = false
    private var didReportStreamFailure = false
    private let processingQueue = DispatchQueue(label: "com.tinyclips.gif-processing")
    private let ciContext = CIContext()
    var onStreamFailure: ((Error) -> Void)?
    private(set) var resolvedRegion: CaptureRegion?

    func start(target: CaptureTarget) async throws {
        debugLifecycle("start requested")
        let preparedTarget = try await target.prepare()
        let filter = preparedTarget.filter
        let config = preparedTarget.config
        resolvedRegion = preparedTarget.region

        let settings = CaptureSettings.shared
        let fps = settings.gifFrameRate
        self.frameDelay = 1.0 / fps
        self.maxWidth = CGFloat(settings.gifMaxWidth)

        config.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(fps))
        config.showsCursor = true
        config.queueDepth = 5

        frames = []
        didReportStreamFailure = false

        let stream = SCStream(filter: filter, configuration: config, delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: processingQueue)
        self.stream = stream
        do {
            try await stream.startCapture()
        } catch {
            self.stream = nil
            throw error
        }
        debugLifecycle("stream started")
    }

    func stop(outputURL: URL) async throws {
        debugLifecycle("stop requested (write to file)")
        try await stream?.stopCapture()
        stream = nil
        debugLifecycle("stream stopped")

        let capturedFrames = processingQueue.sync { self.frames }
        guard !capturedFrames.isEmpty else {
            throw CaptureError.noFrames
        }

        try GifWriter.writeGIF(GifCaptureData(frames: capturedFrames, frameDelay: frameDelay, maxWidth: maxWidth), to: outputURL)
    }

    func stopAndReturnData() async throws -> GifCaptureData {
        debugLifecycle("stop requested (return data)")
        try await stream?.stopCapture()
        stream = nil
        debugLifecycle("stream stopped")

        let capturedFrames = processingQueue.sync { self.frames }
        guard !capturedFrames.isEmpty else {
            throw CaptureError.noFrames
        }

        return try GifCaptureData(frames: capturedFrames, frameDelay: frameDelay, maxWidth: maxWidth)
    }

    func finishAfterStreamFailure() throws -> GifCaptureData {
        debugLifecycle("finalizing after stream failure")
        stream = nil

        let capturedFrames = processingQueue.sync { self.frames }
        guard !capturedFrames.isEmpty else {
            throw CaptureError.noFrames
        }

        return try GifCaptureData(frames: capturedFrames, frameDelay: frameDelay, maxWidth: maxWidth)
    }

    func pause() {
        processingQueue.async {
            self.isPaused = true
        }
    }

    func resume() {
        processingQueue.async {
            self.isPaused = false
        }
    }

    func cancel() async {
        try? await stream?.stopCapture()
        stream = nil
        processingQueue.sync {
            frames.removeAll()
            isPaused = false
            didReportStreamFailure = false
        }
    }

    static func writeGIF(_ gifData: GifCaptureData, to url: URL) throws {
        let frames = gifData.frames
        let maxWidth = gifData.maxWidth
        guard maxWidth.isFinite, maxWidth >= 1 else { throw CaptureError.saveFailed }
        let outputSize: CGSize?
        if CGFloat(frames[0].width) > maxWidth {
            let scale = maxWidth / CGFloat(frames[0].width)
            let newWidth = Int(maxWidth)
            let newHeight = max(1, Int(CGFloat(frames[0].height) * scale))
            outputSize = CGSize(width: newWidth, height: newHeight)
        } else {
            outputSize = nil
        }

        // Finalize beside the destination before replacing an existing capture.
        let stagedURL = url.deletingLastPathComponent().appendingPathComponent(".\(UUID().uuidString).gif")
        defer {
            if FileManager.default.fileExists(atPath: stagedURL.path) {
                do {
                    try FileManager.default.removeItem(at: stagedURL)
                } catch {
                    NSLog("Could not remove temporary GIF: %@", error.localizedDescription)
                }
            }
        }
        guard let destination = CGImageDestinationCreateWithURL(
            stagedURL as CFURL,
            "com.compuserve.gif" as CFString,
            frames.count,
            nil
        ) else {
            throw CaptureError.saveFailed
        }

        let gifProperties: [String: Any] = [
            kCGImagePropertyGIFDictionary as String: [
                kCGImagePropertyGIFLoopCount: 0,
            ],
        ]
        CGImageDestinationSetProperties(destination, gifProperties as CFDictionary)

        for (index, frame) in frames.enumerated() {
            let processedFrame: CGImage
            if let outputSize {
                guard let scaled = GifWriter.downscale(frame, to: outputSize) else {
                    throw CaptureError.saveFailed
                }
                processedFrame = scaled
            } else {
                processedFrame = frame
            }
            let frameProperties: [String: Any] = [
                kCGImagePropertyGIFDictionary as String: [
                    kCGImagePropertyGIFDelayTime: gifData.timing.frameDelays[index],
                ],
            ]
            CGImageDestinationAddImage(destination, processedFrame, frameProperties as CFDictionary)
        }

        guard CGImageDestinationFinalize(destination) else {
            throw CaptureError.saveFailed
        }
        if FileManager.default.fileExists(atPath: url.path) {
            _ = try FileManager.default.replaceItemAt(url, withItemAt: stagedURL)
        } else {
            try FileManager.default.moveItem(at: stagedURL, to: url)
        }
    }

    private static func downscale(_ image: CGImage, to size: CGSize) -> CGImage? {
        guard let context = CGContext(
            data: nil,
            width: Int(size.width),
            height: Int(size.height),
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue
        ) else { return nil }

        context.interpolationQuality = .high
        context.draw(image, in: CGRect(origin: .zero, size: size))
        return context.makeImage()
    }

    private func debugLifecycle(_ message: String) {
#if DEBUG
        print("[GifWriter \(writerID)] \(message)")
#endif
    }
}

extension GifWriter: SCStreamOutput, SCStreamDelegate {
    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, sampleBuffer.isValid else { return }
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }

        let ciImage = CIImage(cvPixelBuffer: pixelBuffer)
        guard let cgImage = ciContext.createCGImage(ciImage, from: ciImage.extent) else { return }

        guard !isPaused else { return }
        frames.append(cgImage)
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        processingQueue.async { [weak self] in
            guard let self, self.stream === stream, !self.didReportStreamFailure else { return }
            self.didReportStreamFailure = true
            self.debugLifecycle("stream stopped unexpectedly: \(error.localizedDescription)")
            self.onStreamFailure?(error)
        }
    }
}
