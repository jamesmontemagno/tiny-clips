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

    func start(target: CaptureTarget) async throws {
        debugLifecycle("start requested")
        let preparedTarget = try await target.prepare()
        let filter = preparedTarget.filter
        let config = preparedTarget.config

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

        try GifWriter.writeGIF(frames: capturedFrames, frameDelay: frameDelay, maxWidth: maxWidth, to: outputURL)
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

        return GifCaptureData(frames: capturedFrames, frameDelay: frameDelay, maxWidth: maxWidth)
    }

    func finishAfterStreamFailure() throws -> GifCaptureData {
        debugLifecycle("finalizing after stream failure")
        stream = nil

        let capturedFrames = processingQueue.sync { self.frames }
        guard !capturedFrames.isEmpty else {
            throw CaptureError.noFrames
        }

        return GifCaptureData(frames: capturedFrames, frameDelay: frameDelay, maxWidth: maxWidth)
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

    static func writeGIF(frames: [CGImage], frameDelay: Double, maxWidth: CGFloat, to url: URL) throws {
        let processedFrames: [CGImage]
        if CGFloat(frames[0].width) > maxWidth {
            let scale = maxWidth / CGFloat(frames[0].width)
            let newWidth = Int(maxWidth)
            let newHeight = Int(CGFloat(frames[0].height) * scale)
            let size = CGSize(width: newWidth, height: newHeight)
            processedFrames = frames.compactMap { GifWriter.downscale($0, to: size) }
        } else {
            processedFrames = frames
        }

        guard let destination = CGImageDestinationCreateWithURL(
            url as CFURL,
            "com.compuserve.gif" as CFString,
            processedFrames.count,
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

        for frame in processedFrames {
            let frameProperties: [String: Any] = [
                kCGImagePropertyGIFDictionary as String: [
                    kCGImagePropertyGIFDelayTime: frameDelay,
                ],
            ]
            CGImageDestinationAddImage(destination, frame, frameProperties as CFDictionary)
        }

        guard CGImageDestinationFinalize(destination) else {
            throw CaptureError.saveFailed
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
