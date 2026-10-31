import Foundation

enum GifTimingError: LocalizedError {
    case noFrames
    case misalignedFrames
    case invalidRange

    var errorDescription: String? {
        switch self {
        case .noFrames: return "The GIF contains no frames."
        case .misalignedFrames: return "The GIF frame timing does not match its images."
        case .invalidRange: return "The selected GIF frame range is invalid."
        }
    }
}

struct GifTiming: Equatable, Sendable {
    let frameDelays: [Double]
    private let frameStarts: [Double]

    init(frameDelays: [Double], frameCount: Int) throws {
        guard frameCount > 0 else { throw GifTimingError.noFrames }
        guard frameDelays.count == frameCount else { throw GifTimingError.misalignedFrames }
        self.init(normalizedDelays: frameDelays.map { Self.decodedDelay(unclamped: $0) })
    }

    init(frameCount: Int, frameDelay: Double) throws {
        guard frameCount > 0 else { throw GifTimingError.noFrames }
        try self.init(frameDelays: Array(repeating: frameDelay, count: frameCount), frameCount: frameCount)
    }

    private init(normalizedDelays: [Double]) {
        frameDelays = normalizedDelays
        var starts = [0.0]
        starts.reserveCapacity(normalizedDelays.count + 1)
        for delay in normalizedDelays {
            starts.append(starts[starts.count - 1] + delay)
        }
        frameStarts = starts
    }

    static func decodedDelay(unclamped: Double?, clamped: Double? = nil, fallback: Double = 0.1) -> Double {
        let delay = unclamped ?? clamped ?? fallback
        return max(0.01, delay.isFinite ? delay : fallback)
    }

    var duration: Double { frameStarts[frameDelays.count] }

    func startTime(of frame: Int) -> Double {
        frameStarts[frame]
    }

    func duration(in range: ClosedRange<Int>) -> Double {
        frameStarts[range.upperBound + 1] - frameStarts[range.lowerBound]
    }

    func selecting(_ range: ClosedRange<Int>) throws -> GifTiming {
        guard range.lowerBound >= 0, range.upperBound < frameDelays.count else {
            throw GifTimingError.invalidRange
        }
        return GifTiming(normalizedDelays: Array(frameDelays[range]))
    }

    func scaled(by speed: Double) -> GifTiming {
        precondition(speed.isFinite && speed > 0, "GIF speed must be finite and positive.")
        return GifTiming(normalizedDelays: frameDelays.map { max(0.01, $0 / speed) })
    }
}
