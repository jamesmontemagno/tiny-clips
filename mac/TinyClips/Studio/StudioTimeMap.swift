import Foundation

// MARK: - Time Map

struct StudioTimeSegment: Codable, Equatable, Sendable {
    var start: Double
    var end: Double
}

/// A stretch of the recording that the video keeps and plays at one rate. `rate` is how many
/// seconds of the recording pass in one second of video.
struct StudioTimePiece: Codable, Equatable, Sendable {
    var start: Double
    var end: Double
    var rate: Double

    /// How long the piece lasts in the video.
    var outputDuration: Double { (end - start) / rate }
}

/// Converts between source time and output time: the trim, the cuts, and speed (section 7 of the
/// project format).
struct StudioTimeMap: Equatable, Sendable {
    /// The slowest and the fastest rate a speed entry can have. One outside counts as the nearer.
    static let slowestRate = 0.25
    static let fastestRate = 8.0

    var sourceDuration: Double
    var trimStart: Double
    var trimEnd: Double

    /// The stretches of the recording the video keeps: the trim without the cuts.
    var segments: [StudioTimeSegment]

    /// The kept stretches divided where the speed changes, each with its rate. Without speed
    /// entries these are the `segments`, each at rate 1.
    var pieces: [StudioTimePiece]
    var outputDuration: Double

    init(sourceDuration: Double, edits: StudioEdits) {
        let duration = max(0, sourceDuration)
        let start = StudioCanvasMath.clamped(edits.trimStart, 0, duration)
        let end = StudioCanvasMath.clamped(edits.trimEnd ?? duration, start, duration)
        let cuts = Self.normalizedCuts(edits.cuts, trimStart: start, trimEnd: end)
        let kept = Self.keptSegments(trimStart: start, trimEnd: end, cuts: cuts)
        let speed = Self.normalizedSpeed(edits.speed)
        let pieces = Self.pieces(of: kept, speed: speed)

        self.sourceDuration = duration
        self.trimStart = start
        self.trimEnd = end
        self.segments = kept
        self.pieces = pieces
        self.outputDuration = pieces.reduce(0) { $0 + $1.outputDuration }
    }

    /// How many seconds of the recording pass in one second of video at a source time. 1 where no
    /// speed entry is, and where nothing is kept.
    func rate(at sourceTime: Double) -> Double {
        for piece in pieces where sourceTime >= piece.start && sourceTime < piece.end {
            return piece.rate
        }
        return 1
    }

    func sourceToOutput(_ sourceTime: Double) -> Double {
        guard !pieces.isEmpty else { return 0 }
        var elapsed = 0.0
        for piece in pieces {
            if sourceTime < piece.start {
                return elapsed
            }
            if sourceTime < piece.end {
                return elapsed + (sourceTime - piece.start) / piece.rate
            }
            elapsed += piece.outputDuration
        }
        return outputDuration
    }

    func outputToSource(_ outputTime: Double) -> Double {
        guard !pieces.isEmpty else { return trimStart }
        let clampedOutput = StudioCanvasMath.clamped(outputTime, 0, outputDuration)
        var elapsed = 0.0
        for piece in pieces {
            let length = piece.outputDuration
            if clampedOutput < elapsed + length {
                return piece.start + (clampedOutput - elapsed) * piece.rate
            }
            elapsed += length
        }
        return pieces[pieces.count - 1].end
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

    /// The speed entries that count, in time order and clear of each other. The trim and the cuts
    /// play no part here, so the rate at a source time is the same wherever they are.
    private static func normalizedSpeed(_ speed: [StudioSpeedRange]) -> [StudioTimePiece] {
        var entries: [(piece: StudioTimePiece, index: Int)] = []
        for (index, entry) in speed.enumerated() {
            guard entry.rate.isFinite, entry.rate > 0 else { continue }
            let rate = StudioCanvasMath.clamped(entry.rate, slowestRate, fastestRate)
            if entry.end > entry.start, rate != 1 {
                entries.append((StudioTimePiece(start: entry.start, end: entry.end, rate: rate), index))
            }
        }

        // The place in the file decides between entries with the same start and end.
        entries.sort { first, second in
            if first.piece.start != second.piece.start { return first.piece.start < second.piece.start }
            if first.piece.end != second.piece.end { return first.piece.end < second.piece.end }
            return first.index < second.index
        }

        var result: [StudioTimePiece] = []
        for entry in entries {
            var piece = entry.piece
            if let last = result.last, piece.start < last.end {
                piece.start = last.end
            }
            if piece.end > piece.start {
                result.append(piece)
            }
        }
        return result
    }

    /// The kept segments divided where the rate changes inside them.
    private static func pieces(of segments: [StudioTimeSegment], speed: [StudioTimePiece]) -> [StudioTimePiece] {
        var pieces: [StudioTimePiece] = []
        for segment in segments {
            var points = [segment.start, segment.end]
            for entry in speed {
                if entry.start > segment.start, entry.start < segment.end {
                    points.append(entry.start)
                }
                if entry.end > segment.start, entry.end < segment.end {
                    points.append(entry.end)
                }
            }
            points.sort()

            let first = pieces.count
            for index in 0..<(points.count - 1) {
                let partStart = points[index]
                let partEnd = points[index + 1]
                guard partEnd > partStart else { continue }

                var rate = 1.0
                for entry in speed where partStart >= entry.start && partStart < entry.end {
                    rate = entry.rate
                    break
                }

                if pieces.count > first, pieces[pieces.count - 1].rate == rate {
                    pieces[pieces.count - 1].end = partEnd
                } else {
                    pieces.append(StudioTimePiece(start: partStart, end: partEnd, rate: rate))
                }
            }
        }
        return pieces
    }
}
