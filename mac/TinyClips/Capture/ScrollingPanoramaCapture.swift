import Accelerate
import CoreImage
import CoreMedia
@preconcurrency import ScreenCaptureKit

struct PanoramaCaptureLimits: Sendable {
    let maxFrames: Int
    let maxOutputHeight: Int
    let maxMemoryBytes: Int64
    let noMovementTimeout: TimeInterval

    static let `default` = PanoramaCaptureLimits(
        maxFrames: 600,
        maxOutputHeight: 50_000,
        maxMemoryBytes: 1_500_000_000,
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

/// Reason a capture stopped growing before the user asked it to.
enum PanoramaCaptureLimitReason: Equatable {
    case memory
    case outputHeight
    case frameCount

    var message: String {
        switch self {
        case .memory: return "Memory limit reached, saving what was captured"
        case .outputHeight: return "Maximum height reached, saving what was captured"
        case .frameCount: return "Frame limit reached, saving what was captured"
        }
    }
}

struct PanoramaFrame: Sendable {
    let width: Int
    let height: Int
    let pixels: [UInt8]
    /// Mean luma per row, used for fast and repeatable vertical alignment.
    let rowLuma: [Float]

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
        self.rowLuma = Self.makeRowLuma(pixels: pixels, width: width, height: height)
    }

    init(width: Int, height: Int, pixels: [UInt8]) {
        self.width = width
        self.height = height
        self.pixels = pixels
        self.rowLuma = Self.makeRowLuma(pixels: pixels, width: width, height: height)
    }

    private static func makeRowLuma(pixels: [UInt8], width: Int, height: Int) -> [Float] {
        guard width > 0, height > 0, pixels.count >= width * height * 4 else { return [] }
        let columnStep = max(1, width / 160)
        var result = [Float](repeating: 0, count: height)
        pixels.withUnsafeBufferPointer { buffer in
            for y in 0..<height {
                var total = 0
                var samples = 0
                var x = 0
                let rowStart = y * width
                while x < width {
                    let index = (rowStart + x) * 4
                    // Integer approximation of luma keeps this hot loop cheap.
                    total += (77 * Int(buffer[index]))
                        + (150 * Int(buffer[index + 1]))
                        + (29 * Int(buffer[index + 2]))
                    samples += 1
                    x += columnStep
                }
                result[y] = samples > 0 ? Float(total) / Float(samples * 256) : 0
            }
        }
        return result
    }
}

/// Stitches frames into the output buffer as they arrive so that peak memory tracks
/// the size of the panorama instead of the number of captured frames.
struct PanoramaAccumulator {
    struct Result {
        let image: CGImage
        let frameCount: Int
        let outputHeight: Int
        let reachedLimit: Bool
    }

    struct Alignment {
        let shift: Int
        let score: Double
        let fixedBottomHeight: Int
    }

    enum Outcome: Equatable {
        case accepted
        case skipped
        case limitReached(PanoramaCaptureLimitReason)
    }

    let limits: PanoramaCaptureLimits

    private(set) var previousFrame: PanoramaFrame?
    private(set) var acceptedFrameCount = 0
    private(set) var limitReason: PanoramaCaptureLimitReason?

    private var output: [UInt8] = []
    private var committedRows = 0
    private var heldBottomBand = 0
    private var rejectedFrameCount = 0

    init(limits: PanoramaCaptureLimits) {
        self.limits = limits
    }

    var reachedLimit: Bool { limitReason != nil }

    /// Height the panorama would have if the capture stopped right now.
    var pendingOutputHeight: Int {
        guard let previousFrame else { return 0 }
        return committedRows == 0 ? previousFrame.height : committedRows + heldBottomBand
    }

    mutating func append(_ frame: PanoramaFrame) -> Outcome {
        guard limitReason == nil else { return .skipped }

        guard let previous = previousFrame else {
            guard fits(outputHeight: frame.height, width: frame.width, frame: frame) else {
                limitReason = .memory
                return .limitReached(.memory)
            }
            previousFrame = frame
            acceptedFrameCount = 1
            return .accepted
        }

        guard frame.width == previous.width, frame.height == previous.height else {
            rejectedFrameCount += 1
            return .skipped
        }
        guard let alignment = Self.estimateVerticalShift(previous: previous, current: frame) else {
            rejectedFrameCount += 1
            return .skipped
        }

        let height = frame.height
        let isFirstCommit = committedRows == 0
        let baseRows = isFirstCommit ? height - alignment.fixedBottomHeight : committedRows
        let previousBand = isFirstCommit ? alignment.fixedBottomHeight : heldBottomBand
        let appendCount = alignment.shift + previousBand - alignment.fixedBottomHeight
        let sourceStartRow = height - previousBand - alignment.shift
        guard appendCount > 0, sourceStartRow >= 0, baseRows >= 0 else {
            rejectedFrameCount += 1
            return .skipped
        }

        let prospectiveHeight = baseRows + appendCount + alignment.fixedBottomHeight
        guard prospectiveHeight <= limits.maxOutputHeight else {
            limitReason = .outputHeight
            return .limitReached(.outputHeight)
        }
        guard fits(outputHeight: prospectiveHeight, width: frame.width, frame: frame) else {
            limitReason = .memory
            return .limitReached(.memory)
        }

        if isFirstCommit {
            output.reserveCapacity(baseRows * frame.width * 4)
            appendRows(from: previous, startRow: 0, rowCount: baseRows)
            committedRows = baseRows
        }
        appendRows(from: frame, startRow: sourceStartRow, rowCount: appendCount)
        committedRows += appendCount
        heldBottomBand = alignment.fixedBottomHeight
        previousFrame = frame
        acceptedFrameCount += 1

        if acceptedFrameCount >= limits.maxFrames {
            limitReason = .frameCount
            return .limitReached(.frameCount)
        }
        return .accepted
    }

    /// Flushes the held footer band and materializes the panorama image.
    mutating func finish() throws -> Result {
        guard let last = previousFrame else { throw PanoramaCaptureError.noFrames }
        guard committedRows > 0 else {
            if let limitReason {
                throw limitReason == .memory
                    ? PanoramaCaptureError.memoryLimit
                    : PanoramaCaptureError.outputTooLarge
            }
            throw rejectedFrameCount > 0
                ? PanoramaCaptureError.alignmentFailed
                : PanoramaCaptureError.noMovement
        }

        var pixels = output
        output = []
        if heldBottomBand > 0 {
            let bytesPerRow = last.width * 4
            let start = (last.height - heldBottomBand) * bytesPerRow
            pixels.append(contentsOf: last.pixels[start..<last.pixels.count])
        }
        let outputHeight = committedRows + heldBottomBand

        let data = Data(pixels) as CFData
        guard let provider = CGDataProvider(data: data),
              let image = CGImage(
                width: last.width,
                height: outputHeight,
                bitsPerComponent: 8,
                bitsPerPixel: 32,
                bytesPerRow: last.width * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
                provider: provider,
                decode: nil,
                shouldInterpolate: false,
                intent: .defaultIntent
              ) else {
            throw PanoramaCaptureError.alignmentFailed
        }
        return Result(
            image: image,
            frameCount: acceptedFrameCount,
            outputHeight: outputHeight,
            reachedLimit: limitReason != nil
        )
    }

    private mutating func appendRows(from frame: PanoramaFrame, startRow: Int, rowCount: Int) {
        let bytesPerRow = frame.width * 4
        let start = startRow * bytesPerRow
        let end = start + rowCount * bytesPerRow
        output.append(contentsOf: frame.pixels[start..<end])
    }

    /// Peak memory is the output buffer plus the copy made for the final image, plus the retained and incoming frames.
    private func fits(outputHeight: Int, width: Int, frame: PanoramaFrame) -> Bool {
        let outputBytes = Int64(width) * Int64(outputHeight) * 4
        return outputBytes * 2 + frame.byteCount * 2 <= limits.maxMemoryBytes
    }

    static func areMeaningfullyDifferent(_ first: PanoramaFrame, _ second: PanoramaFrame) -> Bool {
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

    static func estimateVerticalShift(previous: PanoramaFrame, current: PanoramaFrame) -> Alignment? {
        let height = previous.height
        guard height > 8,
              previous.rowLuma.count == height,
              current.rowLuma.count == height else {
            return nil
        }
        // Scrolling content is usually periodic (repeating rows, cards, table
        // stripes), so the globally lowest score is often a later alias of the
        // real shift. Overshooting duplicates rows that are already committed,
        // so the smallest credible shift is always the right choice.
        let ignoredTopBand = max(1, height / 20)
        let ignoredBottomBand = max(1, height / 20)
        let minimumShift = 2
        let maximumShift = max(minimumShift, height - height / 10)
        let minimumSamples = max(8, height / 8)

        var scores = [Float](repeating: .greatestFiniteMagnitude, count: maximumShift + 1)
        var bestScore = Float.greatestFiniteMagnitude
        var differences = [Float](repeating: 0, count: height)
        previous.rowLuma.withUnsafeBufferPointer { previousRows in
            current.rowLuma.withUnsafeBufferPointer { currentRows in
                guard let previousBase = previousRows.baseAddress,
                      let currentBase = currentRows.baseAddress else { return }
                differences.withUnsafeMutableBufferPointer { scratch in
                    guard let scratchBase = scratch.baseAddress else { return }
                    for shift in minimumShift...maximumShift {
                        let comparisonEnd = height - ignoredBottomBand - shift
                        let samples = comparisonEnd - ignoredTopBand
                        guard samples >= minimumSamples else { continue }
                        let count = vDSP_Length(samples)
                        vDSP_vsub(
                            currentBase + ignoredTopBand, 1,
                            previousBase + ignoredTopBand + shift, 1,
                            scratchBase, 1,
                            count
                        )
                        var total: Float = 0
                        vDSP_svemg(scratchBase, 1, &total, count)
                        let score = total / Float(samples)
                        scores[shift] = score
                        if score < bestScore {
                            bestScore = score
                        }
                    }
                }
            }
        }
        guard bestScore < .greatestFiniteMagnitude else { return nil }

        // Anything within this band of the best score is statistically the same
        // match, so prefer the earliest one.
        let acceptanceScore = max(bestScore * 1.6, bestScore + 0.5)
        var candidate: Int?
        var verificationAttempts = 0
        for shift in minimumShift...maximumShift where scores[shift] <= acceptanceScore {
            let isLocalMinimum = (shift == minimumShift || scores[shift] <= scores[shift - 1])
                && (shift == maximumShift || scores[shift] <= scores[shift + 1])
            guard isLocalMinimum else { continue }
            if pixelAlignmentScore(previous: previous, current: current, shift: shift) <= 12 {
                candidate = shift
                break
            }
            verificationAttempts += 1
            // Verification is the expensive part, so give up rather than scan a
            // frame that is not a scroll of the previous one.
            if verificationAttempts >= 6 { break }
        }
        guard let bestShift = candidate else { return nil }

        let fixedBottomHeight = stationaryBottomBand(
            previous: previous,
            current: current,
            maximumHeight: bestShift / 2
        )
        guard bestShift + fixedBottomHeight <= height else { return nil }
        return Alignment(shift: bestShift, score: Double(scores[bestShift]), fixedBottomHeight: fixedBottomHeight)
    }

    /// Confirms a row-signature candidate against real pixels, which rejects
    /// shifts where unrelated rows happen to share the same average brightness.
    private static func pixelAlignmentScore(
        previous: PanoramaFrame,
        current: PanoramaFrame,
        shift: Int
    ) -> Double {
        let height = previous.height
        let width = previous.width
        let ignoredTopBand = max(1, height / 20)
        let comparisonEnd = height - max(1, height / 20) - shift
        guard comparisonEnd > ignoredTopBand, width > 0 else { return .greatestFiniteMagnitude }
        let columnStep = max(1, width / 160)
        let rowStep = max(1, (comparisonEnd - ignoredTopBand) / 80)
        var score = 0.0
        var samples = 0
        for y in stride(from: ignoredTopBand, to: comparisonEnd, by: rowStep) {
            for x in stride(from: 0, to: width, by: columnStep) {
                let previousIndex = ((y + shift) * width + x) * 4
                let currentIndex = (y * width + x) * 4
                score += abs(luma(previous.pixels, previousIndex) - luma(current.pixels, currentIndex))
                samples += 1
            }
        }
        return samples > 0 ? score / Double(samples) : .greatestFiniteMagnitude
    }

    private static func stationaryBottomBand(
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

    private static func luma(_ bytes: [UInt8], _ index: Int) -> Double {
        (0.299 * Double(bytes[index]))
            + (0.587 * Double(bytes[index + 1]))
            + (0.114 * Double(bytes[index + 2]))
    }
}

/// Convenience wrapper that stitches an already-captured sequence of frames.
struct PanoramaStitcher {
    let limits: PanoramaCaptureLimits

    func stitch(_ frames: [PanoramaFrame]) throws -> PanoramaAccumulator.Result {
        var accumulator = PanoramaAccumulator(limits: limits)
        for frame in frames {
            if case .limitReached = accumulator.append(frame) { break }
        }
        return try accumulator.finish()
    }
}

final class ScrollingPanoramaCapture: NSObject, @unchecked Sendable {
    var onProgress: ((Int) -> Void)?
    var onFailure: ((Error) -> Void)?
    /// Fired once when a guardrail stops growth; the caller should stop and keep what exists.
    var onLimitReached: ((PanoramaCaptureLimitReason) -> Void)?

    private let limits: PanoramaCaptureLimits
    private let processingQueue = DispatchQueue(label: "com.tinyclips.scrolling-panorama")
    private let context = CIContext()
    private var stream: SCStream?
    private var accumulator: PanoramaAccumulator
    private var lastFrameDate = Date()
    private var stopContinuation: CheckedContinuation<CGImage, Error>?
    private var didFinish = false
    private var didReportFailure = false
    private var didReportLimit = false

    init(limits: PanoramaCaptureLimits = .default) {
        self.limits = limits
        self.accumulator = PanoramaAccumulator(limits: limits)
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
            self.accumulator = PanoramaAccumulator(limits: self.limits)
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
            let image = try accumulator.finish().image
            stopContinuation?.resume(returning: image)
        } catch {
            stopContinuation?.resume(throwing: error)
        }
        stopContinuation = nil
        accumulator = PanoramaAccumulator(limits: limits)
    }

    private func process(_ sampleBuffer: CMSampleBuffer) {
        guard !didFinish,
              !accumulator.reachedLimit,
              let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else {
            return
        }
        let ciImage = CIImage(cvPixelBuffer: pixelBuffer)
        guard let image = context.createCGImage(ciImage, from: ciImage.extent),
              let frame = try? PanoramaFrame(image: image) else {
            return
        }
        if let previous = accumulator.previousFrame,
           !PanoramaAccumulator.areMeaningfullyDifferent(previous, frame) {
            if Date().timeIntervalSince(lastFrameDate) > limits.noMovementTimeout {
                reportFailure(PanoramaCaptureError.noMovement)
            }
            return
        }
        switch accumulator.append(frame) {
        case .accepted:
            lastFrameDate = Date()
            onProgress?(accumulator.acceptedFrameCount)
        case .skipped:
            break
        case .limitReached(let reason):
            onProgress?(accumulator.acceptedFrameCount)
            reportLimit(reason)
        }
    }

    private func reportFailure(_ error: Error) {
        guard !didReportFailure else { return }
        didReportFailure = true
        onFailure?(error)
    }

    private func reportLimit(_ reason: PanoramaCaptureLimitReason) {
        guard !didReportLimit else { return }
        didReportLimit = true
        onLimitReached?(reason)
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
