import AppKit
import CoreImage
import CoreMedia
import ScreenCaptureKit

struct PanoramaCaptureLimits: Sendable {
    let maxFrames: Int
    let maxOutputHeight: Int
    let maxMemoryBytes: Int64
    let noMovementTimeout: TimeInterval

    static let `default` = PanoramaCaptureLimits(
        maxFrames: 120,
        maxOutputHeight: 30_000,
        maxMemoryBytes: 1_200_000_000,
        noMovementTimeout: 8
    )
}

enum PanoramaCaptureError: LocalizedError {
    case cancelled
    case noMovement
    case outputTooLarge
    case memoryLimit
    case noFrames
    case alignmentFailed

    var errorDescription: String? {
        switch self {
        case .cancelled: return "Scrolling capture was cancelled."
        case .noMovement: return "Scrolling capture stopped because no movement was detected."
        case .outputTooLarge: return "Scrolling capture reached its maximum output size."
        case .memoryLimit: return "Scrolling capture reached its memory limit."
        case .noFrames: return "No frames were captured."
        case .alignmentFailed: return "Could not align the scrolling frames."
        }
    }
}

struct PanoramaStitcher {
    struct Result {
        let image: CGImage
        let frameCount: Int
        let outputHeight: Int
    }

    let limits: PanoramaCaptureLimits

    func stitch(_ frames: [CGImage]) throws -> Result {
        guard let first = frames.first else { throw PanoramaCaptureError.noFrames }
        let width = first.width
        let height = first.height
        guard width > 0, height > 0 else { throw PanoramaCaptureError.noFrames }

        var images: [[UInt8]] = []
        images.reserveCapacity(frames.count)
        let bytesPerImage = Int64(width) * Int64(height) * 4
        for frame in frames {
            guard frame.width == width, frame.height == height else {
                throw PanoramaCaptureError.alignmentFailed
            }
            guard Int64(images.count + 1) * bytesPerImage <= limits.maxMemoryBytes else {
                throw PanoramaCaptureError.memoryLimit
            }
            images.append(try rgbaBytes(for: frame))
        }

        var outputHeight = height
        var placements: [(image: [UInt8], y: Int)] = [(images[0], 0)]
        for index in 1..<images.count {
            let previous = placements.last!.image
            let current = images[index]
            let shift = estimateVerticalShift(previous: previous, current: current, width: width, height: height)
            guard shift > 0 else { continue }
            outputHeight += shift
            guard outputHeight <= limits.maxOutputHeight else {
                throw PanoramaCaptureError.outputTooLarge
            }
            placements.append((current, outputHeight - height))
        }

        guard placements.count > 1 else { throw PanoramaCaptureError.noMovement }
        guard let context = CGContext(
            data: nil,
            width: width,
            height: outputHeight,
            bitsPerComponent: 8,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else {
            throw PanoramaCaptureError.alignmentFailed
        }

        context.setBlendMode(.copy)
        for placement in placements {
            placement.image.withUnsafeBytes { bytes in
                guard let baseAddress = bytes.baseAddress,
                      let image = CGImage(
                        width: width,
                        height: height,
                        bitsPerComponent: 8,
                        bitsPerPixel: 32,
                        bytesPerRow: width * 4,
                        space: CGColorSpaceCreateDeviceRGB(),
                        bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
                        provider: CGDataProvider(data: Data(bytes: baseAddress, count: bytes.count) as CFData)!,
                        decode: nil,
                        shouldInterpolate: false,
                        intent: .defaultIntent
                      ) else { return }
                context.draw(image, in: CGRect(x: 0, y: outputHeight - placement.y - height, width: width, height: height))
            }
        }

        guard let result = context.makeImage() else { throw PanoramaCaptureError.alignmentFailed }
        return Result(image: result, frameCount: placements.count, outputHeight: outputHeight)
    }

    private func rgbaBytes(for image: CGImage) throws -> [UInt8] {
        var bytes = [UInt8](repeating: 0, count: image.width * image.height * 4)
        guard let context = CGContext(
            data: &bytes,
            width: image.width,
            height: image.height,
            bitsPerComponent: 8,
            bytesPerRow: image.width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else {
            throw PanoramaCaptureError.alignmentFailed
        }
        context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        return bytes
    }

    private func estimateVerticalShift(previous: [UInt8], current: [UInt8], width: Int, height: Int) -> Int {
        let minimumShift = max(2, height / 40)
        let maximumShift = max(minimumShift, height - height / 10)
        let band = max(1, height / 10)
        let sampleStep = max(1, width / 160)
        var bestShift = 0
        var bestScore = Double.greatestFiniteMagnitude

        for shift in stride(from: minimumShift, through: maximumShift, by: max(1, height / 120)) {
            let overlap = height - shift
            var score = 0.0
            var samples = 0
            for y in stride(from: band, to: overlap - band, by: max(1, overlap / 80)) {
                let previousY = y + shift
                for x in stride(from: 0, to: width, by: sampleStep) {
                    let previousIndex = (previousY * width + x) * 4
                    let currentIndex = (y * width + x) * 4
                    score += abs(luma(previous, previousIndex) - luma(current, currentIndex))
                    samples += 1
                }
            }
            if samples > 0 {
                let normalizedScore = score / Double(samples)
                if normalizedScore < bestScore {
                    bestScore = normalizedScore
                    bestShift = shift
                }
            }
        }
        return bestShift
    }

    private func luma(_ bytes: [UInt8], _ index: Int) -> Double {
        (0.299 * Double(bytes[index])) + (0.587 * Double(bytes[index + 1])) + (0.114 * Double(bytes[index + 2]))
    }
}

final class ScrollingPanoramaCapture: NSObject, @unchecked Sendable {
    var onProgress: ((Int) -> Void)?
    var onFailure: ((Error) -> Void)?

    private let limits: PanoramaCaptureLimits
    private let processingQueue = DispatchQueue(label: "com.tinyclips.scrolling-panorama")
    private let context = CIContext()
    private var stream: SCStream?
    private var frames: [CGImage] = []
    private var lastFrameDate = Date()
    private var stopContinuation: CheckedContinuation<CGImage, Error>?
    private var didFinish = false

    init(limits: PanoramaCaptureLimits = .default) {
        self.limits = limits
    }

    func start(region: CaptureRegion) async throws {
        let filter = try await region.makeFilter()
        let configuration = region.makeStreamConfig()
        configuration.showsCursor = false
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: 12)
        let stream = SCStream(filter: filter, configuration: configuration, delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: processingQueue)
        try await stream.startCapture()
        self.stream = stream
        lastFrameDate = Date()
    }

    func stop() async throws -> CGImage {
        try await withCheckedThrowingContinuation { continuation in
            processingQueue.async {
                guard !self.didFinish else {
                    continuation.resume(throwing: PanoramaCaptureError.cancelled)
                    return
                }
                self.stopContinuation = continuation
                Task {
                    try? await self.stream?.stopCapture()
                    self.finish()
                }
            }
        }
    }

    func cancel() {
        processingQueue.async {
            guard !self.didFinish else { return }
            self.didFinish = true
            self.stream?.stopCapture { _ in }
            self.stopContinuation?.resume(throwing: PanoramaCaptureError.cancelled)
            self.stopContinuation = nil
            self.stream = nil
            self.frames.removeAll()
        }
    }

    private func finish() {
        guard !didFinish else { return }
        didFinish = true
        stream = nil
        do {
            let image = try PanoramaStitcher(limits: limits).stitch(frames).image
            stopContinuation?.resume(returning: image)
        } catch {
            stopContinuation?.resume(throwing: error)
        }
        stopContinuation = nil
        frames.removeAll()
    }

    private func process(_ sampleBuffer: CMSampleBuffer) {
        guard !didFinish,
              let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer),
              let image = context.createCGImage(CIImage(cvPixelBuffer: pixelBuffer), from: CIImage(cvPixelBuffer: pixelBuffer).extent) else {
            return
        }
        let frameBytes: Int64 = Int64(image.width) * Int64(image.height) * 4
        guard Int64(frames.count + 1) * frameBytes <= limits.maxMemoryBytes else {
            onFailure?(PanoramaCaptureError.memoryLimit)
            return
        }
        if let previous = frames.last, !isMeaningfullyDifferent(previous, image) {
            if Date().timeIntervalSince(lastFrameDate) > limits.noMovementTimeout {
                onFailure?(PanoramaCaptureError.noMovement)
            }
            return
        }
        frames.append(image)
        lastFrameDate = Date()
        onProgress?(frames.count)
        if frames.count >= limits.maxFrames {
            onFailure?(PanoramaCaptureError.outputTooLarge)
        }
    }

    private func isMeaningfullyDifferent(_ first: CGImage, _ second: CGImage) -> Bool {
        guard let firstData = try? PanoramaStitcher(limits: limits).rgbaBytesForComparison(first),
              let secondData = try? PanoramaStitcher(limits: limits).rgbaBytesForComparison(second) else {
            return true
        }
        let sampleStride = max(4, first.width / 80) * 4
        var difference = 0
        var samples = 0
        for index in Swift.stride(from: 0, to: min(firstData.count, secondData.count), by: sampleStride) {
            difference += abs(Int(firstData[index]) - Int(secondData[index]))
            samples += 1
        }
        return samples == 0 || Double(difference) / Double(samples) > 2.5
    }
}

private extension PanoramaStitcher {
    func rgbaBytesForComparison(_ image: CGImage) throws -> [UInt8] {
        try rgbaBytes(for: image)
    }
}

extension ScrollingPanoramaCapture: SCStreamOutput, SCStreamDelegate {
    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen else { return }
        process(sampleBuffer)
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        processingQueue.async {
            self.onFailure?(error)
        }
    }
}
