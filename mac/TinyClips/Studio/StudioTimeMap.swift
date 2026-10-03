import Foundation

// MARK: - Time Map

struct StudioTimeSegment: Codable, Equatable, Sendable {
    var start: Double
    var end: Double
}

struct StudioTimeMap: Equatable, Sendable {
    var sourceDuration: Double
    var trimStart: Double
    var trimEnd: Double
    var segments: [StudioTimeSegment]
    var outputDuration: Double

    init(sourceDuration: Double, edits: StudioEdits) {
        let duration = max(0, sourceDuration)
        let start = StudioCanvasMath.clamped(edits.trimStart, 0, duration)
        let end = StudioCanvasMath.clamped(edits.trimEnd ?? duration, start, duration)
        let cuts = Self.normalizedCuts(edits.cuts, trimStart: start, trimEnd: end)
        let kept = Self.keptSegments(trimStart: start, trimEnd: end, cuts: cuts)

        self.sourceDuration = duration
        self.trimStart = start
        self.trimEnd = end
        self.segments = kept
        self.outputDuration = kept.reduce(0) { $0 + ($1.end - $1.start) }
    }

    func sourceToOutput(_ sourceTime: Double) -> Double {
        guard !segments.isEmpty else { return 0 }
        var elapsed = 0.0
        for segment in segments {
            if sourceTime < segment.start {
                return elapsed
            }
            if sourceTime < segment.end {
                return elapsed + (sourceTime - segment.start)
            }
            elapsed += segment.end - segment.start
        }
        return outputDuration
    }

    func outputToSource(_ outputTime: Double) -> Double {
        guard !segments.isEmpty else { return trimStart }
        let clampedOutput = StudioCanvasMath.clamped(outputTime, 0, outputDuration)
        var elapsed = 0.0
        for segment in segments {
            let length = segment.end - segment.start
            if clampedOutput < elapsed + length {
                return segment.start + (clampedOutput - elapsed)
            }
            elapsed += length
        }
        return segments[segments.count - 1].end
    }

    // MARK: - Private

    private static func normalizedCuts(_ cuts: [StudioTimeRange], trimStart: Double, trimEnd: Double) -> [StudioTimeSegment] {
        var clampedCuts: [StudioTimeSegment] = []
        for cut in cuts {
            let start = StudioCanvasMath.clamped(cut.start, trimStart, trimEnd)
            let end = StudioCanvasMath.clamped(cut.end, trimStart, trimEnd)
            if end > start {
                clampedCuts.append(StudioTimeSegment(start: start, end: end))
            }
        }
        clampedCuts.sort {
            if $0.start == $1.start { return $0.end < $1.end }
            return $0.start < $1.start
        }

        var merged: [StudioTimeSegment] = []
        for cut in clampedCuts {
            if let last = merged.last, cut.start <= last.end {
                merged[merged.count - 1].end = max(last.end, cut.end)
            } else {
                merged.append(cut)
            }
        }
        return merged
    }

    private static func keptSegments(trimStart: Double, trimEnd: Double, cuts: [StudioTimeSegment]) -> [StudioTimeSegment] {
        var kept: [StudioTimeSegment] = []
        var cursor = trimStart
        for cut in cuts {
            if cut.start > cursor {
                kept.append(StudioTimeSegment(start: cursor, end: cut.start))
            }
            cursor = max(cursor, cut.end)
        }
        if cursor < trimEnd {
            kept.append(StudioTimeSegment(start: cursor, end: trimEnd))
        }
        return kept
    }
}
