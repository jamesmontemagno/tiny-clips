import CoreImage
import CoreMedia
@preconcurrency import ScreenCaptureKit

struct PanoramaCaptureLimits: Sendable {
    let maxFrames: Int
    let maxOutputHeight: Int
    let maxMemoryBytes: Int64
    let noMovementTimeout: TimeInterval

    static let `default` = PanoramaCaptureLimits(
        maxFrames: 120,
        maxOutputHeight: 30_000,
        maxMemoryBytes: 600_000_000,
        noMovementTimeout: 8
    )
}

enum PanoramaCaptureError: LocalizedError, Equatable {
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

struct PanoramaFrame: Sendable {
    let width: Int
    let height: Int
    let pixels: [UInt8]

    var byteCount: Int64 {
        Int64(pixels.count)
    }

    init(image: CGImage) throws {
        width = image.width
        height = image.height
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        guard width > 0,
              height > 0,
              let context = CGContext(
                data: &pixels,
                width: width,
                height: height,
                bitsPerComponent: 8,
                bytesPerRow: width * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
              ) else {
            throw PanoramaCaptureError.noFrames
        }
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        self.pixels = pixels
    }

    init(width: Int, height: Int, pixels: [UInt8]) {
        self.width = width
        self.height = height
        self.pixels = pixels
    }
}

struct PanoramaStitcher {
    struct Result {
        let image: CGImage
        let frameCount: Int
        let outputHeight: Int
    }

    private struct Alignment {
        let shift: Int
        let score: Double
        let fixedBottomHeight: Int
    }

    let limits: PanoramaCaptureLimits

    func stitch(_ frames: [PanoramaFrame]) throws -> Result {
        guard let first = frames.first else { throw PanoramaCaptureError.noFrames }
        guard first.width > 0, first.height > 0 else { throw PanoramaCaptureError.noFrames }

        var acceptedFrames = [first]
        var alignments: [Alignment] = []
        var outputHeight = first.height

        for frame in frames.dropFirst() {
            guard frame.width == first.width, frame.height == first.height else {
                throw PanoramaCaptureError.alignmentFailed
            }
            guard let alignment = estimateVerticalShift(previous: acceptedFrames.last!, current: frame) else {
                continue
            }
            outputHeight += alignment.shift
            guard outputHeight <= limits.maxOutputHeight else {
                throw PanoramaCaptureError.outputTooLarge
            }
            acceptedFrames.append(frame)
            alignments.append(alignment)
        }

        guard acceptedFrames.count > 1 else {
            throw frames.count > 1 ? PanoramaCaptureError.alignmentFailed : PanoramaCaptureError.noMovement
        }

        let frameBytes = frames.reduce(Int64(0)) { $0 + $1.byteCount }
        let outputBytes = Int64(first.width) * Int64(outputHeight) * 4
        // The final CGImage provider may copy the output Data, so budget two output buffers.
        guard frameBytes + outputBytes * 2 <= limits.maxMemoryBytes else {
            throw PanoramaCaptureError.memoryLimit
        }

        let fixedBottomHeight = alignments.map(\.fixedBottomHeight).max() ?? 0
        var output = [UInt8](repeating: 0, count: Int(outputBytes))
        copyRows(
            from: first,
            sourceStartRow: 0,
            rowCount: first.height - fixedBottomHeight,
            to: &output,
            destinationStartRow: 0
        )
        var destinationRow = first.height - fixedBottomHeight
        for (index, alignment) in alignments.enumerated() {
            let frame = acceptedFrames[index + 1]
            let rowCount = alignment.shift
            copyRows(
                from: frame,
                sourceStartRow: frame.height - fixedBottomHeight - alignment.shift,
                rowCount: rowCount,
                to: &output,
                destinationStartRow: destinationRow
            )
            destinationRow += rowCount
        }
        if fixedBottomHeight > 0, let last = acceptedFrames.last {
            copyRows(
                from: last,
                sourceStartRow: last.height - fixedBottomHeight,
                rowCount: fixedBottomHeight,
                to: &output,
                destinationStartRow: destinationRow
            )
        }

        let data = Data(output) as CFData
        guard let provider = CGDataProvider(data: data),
              let image = CGImage(
                width: first.width,
                height: outputHeight,
                bitsPerComponent: 8,
                bitsPerPixel: 32,
                bytesPerRow: first.width * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
                provider: provider,
                decode: nil,
                shouldInterpolate: false,
                intent: .defaultIntent
              ) else {
            throw PanoramaCaptureError.alignmentFailed
        }
        return Result(image: image, frameCount: acceptedFrames.count, outputHeight: outputHeight)
    }

    func areMeaningfullyDifferent(_ first: PanoramaFrame, _ second: PanoramaFrame) -> Bool {
        guard first.width == second.width, first.height == second.height else { return true }
        let rowStep = max(1, first.height / 80)
        let columnStep = max(1, first.width / 80)
        var difference = 0.0
        var samples = 0
        for y in stride(from: 0, to: first.height, by: rowStep) {
            for x in stride(from: 0, to: first.width, by: columnStep) {
                let index = (y * first.width + x) * 4
                difference += abs(luma(first.pixels, index) - luma(second.pixels, index))
                samples += 1
            }
        }
        return samples == 0 || difference / Double(samples) > 2.5
    }

    private func estimateVerticalShift(previous: PanoramaFrame, current: PanoramaFrame) -> Alignment? {
        let height = previous.height
        let width = previous.width
        let minimumShift = max(2, height / 40)
        let maximumShift = max(minimumShift, height - height / 10)
        let ignoredTopBand = max(1, height / 10)
        let columnStep = max(1, width / 160)
        var bestShift = 0
        var bestScore = Double.greatestFiniteMagnitude

        for shift in stride(from: minimumShift, through: maximumShift, by: max(1, height / 120)) {
            let overlap = height - shift
            let comparisonEnd = overlap - ignoredTopBand
            guard comparisonEnd > ignoredTopBand else { continue }
            var score = 0.0
            var samples = 0
            for y in stride(from: ignoredTopBand, to: comparisonEnd, by: max(1, overlap / 80)) {
                for x in stride(from: 0, to: width, by: columnStep) {
                    let previousIndex = ((y + shift) * width + x) * 4
                    let currentIndex = (y * width + x) * 4
                    score += abs(luma(previous.pixels, previousIndex) - luma(current.pixels, currentIndex))
                    samples += 1
                }
            }
            guard samples > 0 else { continue }
            let normalizedScore = score / Double(samples)
            if normalizedScore < bestScore {
                bestScore = normalizedScore
                bestShift = shift
            }
        }

        guard bestShift > 0, bestScore <= 18 else { return nil }
        let fixedBottomHeight = stationaryBottomBand(
            previous: previous,
            current: current,
            maximumHeight: bestShift / 2
        )
        guard bestShift + fixedBottomHeight <= height else { return nil }
        return Alignment(
            shift: bestShift,
            score: bestScore,
            fixedBottomHeight: fixedBottomHeight
        )
    }

    private func stationaryBottomBand(
        previous: PanoramaFrame,
        current: PanoramaFrame,
        maximumHeight: Int
    ) -> Int {
        let maximum = min(maximumHeight, previous.height / 4)
        guard maximum > 0 else { return 0 }
        let columnStep = max(1, previous.width / 160)
        var stationaryRows = 0
        for offset in 0..<maximum {
            let y = previous.height - 1 - offset
            var difference = 0.0
            var samples = 0
            for x in stride(from: 0, to: previous.width, by: columnStep) {
                let index = (y * previous.width + x) * 4
                difference += abs(luma(previous.pixels, index) - luma(current.pixels, index))
                samples += 1
            }
            guard samples > 0, difference / Double(samples) <= 2 else { break }
            stationaryRows += 1
        }
        return stationaryRows
    }

    private func copyRows(
        from frame: PanoramaFrame,
        sourceStartRow: Int,
        rowCount: Int,
        to output: inout [UInt8],
        destinationStartRow: Int
    ) {
        let bytesPerRow = frame.width * 4
        let sourceStart = sourceStartRow * bytesPerRow
        let sourceEnd = sourceStart + rowCount * bytesPerRow
        let destinationStart = destinationStartRow * bytesPerRow
        output.replaceSubrange(
            destinationStart..<(destinationStart + rowCount * bytesPerRow),
            with: frame.pixels[sourceStart..<sourceEnd]
        )
    }

    private func luma(_ bytes: [UInt8], _ index: Int) -> Double {
        (0.299 * Double(bytes[index]))
            + (0.587 * Double(bytes[index + 1]))
            + (0.114 * Double(bytes[index + 2]))
    }
}

final class ScrollingPanoramaCapture: NSObject, @unchecked Sendable {
    var onProgress: ((Int) -> Void)?
    var onFailure: ((Error) -> Void)?

    private let limits: PanoramaCaptureLimits
    private let processingQueue = DispatchQueue(label: "com.tinyclips.scrolling-panorama")
    private let context = CIContext()
    private var stream: SCStream?
    private var frames: [PanoramaFrame] = []
    private var retainedFrameBytes: Int64 = 0
    private var lastFrameDate = Date()
    private var stopContinuation: CheckedContinuation<CGImage, Error>?
    private var didFinish = false
    private var didReportFailure = false

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

        let mayStart = await withCheckedContinuation { continuation in
            processingQueue.async {
                guard !self.didFinish else {
                    continuation.resume(returning: false)
                    return
                }
                self.stream = stream
                continuation.resume(returning: true)
            }
        }
        guard mayStart else { throw PanoramaCaptureError.cancelled }

        do {
            try await stream.startCapture()
        } catch {
            processingQueue.async {
                if self.stream === stream {
                    self.stream = nil
                }
            }
            throw error
        }

        let cancelledDuringStartup = await withCheckedContinuation { continuation in
            processingQueue.async {
                self.lastFrameDate = Date()
                continuation.resume(returning: self.didFinish)
            }
        }
        if cancelledDuringStartup {
            try? await stream.stopCapture()
            throw PanoramaCaptureError.cancelled
        }
    }

    func stop() async throws -> CGImage {
        try await withCheckedThrowingContinuation { continuation in
            processingQueue.async {
                guard !self.didFinish else {
                    continuation.resume(throwing: PanoramaCaptureError.cancelled)
                    return
                }
                self.stopContinuation = continuation
                let stream = self.stream
                Task {
                    try? await stream?.stopCapture()
                    self.processingQueue.async {
                        self.finish()
                    }
                }
            }
        }
    }

    func cancel() {
        processingQueue.async {
            guard !self.didFinish else { return }
            self.didFinish = true
            let stream = self.stream
            self.stream = nil
            self.stopContinuation?.resume(throwing: PanoramaCaptureError.cancelled)
            self.stopContinuation = nil
            self.frames.removeAll()
            self.retainedFrameBytes = 0
            Task {
                try? await stream?.stopCapture()
            }
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
        retainedFrameBytes = 0
    }

    private func process(_ sampleBuffer: CMSampleBuffer) {
        guard !didFinish,
              let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else {
            return
        }
        let ciImage = CIImage(cvPixelBuffer: pixelBuffer)
        guard let image = context.createCGImage(ciImage, from: ciImage.extent),
              let frame = try? PanoramaFrame(image: image) else {
            return
        }
        guard retainedFrameBytes + frame.byteCount <= limits.maxMemoryBytes / 2 else {
            reportFailure(PanoramaCaptureError.memoryLimit)
            return
        }
        if let previous = frames.last,
           !PanoramaStitcher(limits: limits).areMeaningfullyDifferent(previous, frame) {
            if Date().timeIntervalSince(lastFrameDate) > limits.noMovementTimeout {
                reportFailure(PanoramaCaptureError.noMovement)
            }
            return
        }
        frames.append(frame)
        retainedFrameBytes += frame.byteCount
        lastFrameDate = Date()
        onProgress?(frames.count)
        if frames.count >= limits.maxFrames {
            reportFailure(PanoramaCaptureError.outputTooLarge)
        }
    }

    private func reportFailure(_ error: Error) {
        guard !didReportFailure else { return }
        didReportFailure = true
        onFailure?(error)
    }
}

extension ScrollingPanoramaCapture: SCStreamOutput, SCStreamDelegate {
    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen else { return }
        process(sampleBuffer)
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        processingQueue.async {
            self.reportFailure(error)
        }
    }
}
