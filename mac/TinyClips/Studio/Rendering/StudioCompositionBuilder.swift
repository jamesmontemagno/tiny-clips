import AVFoundation
import CoreGraphics
import Foundation

// MARK: - Composition Builder

/// A sound track of the composition and which of the screen file's sound tracks it plays.
struct StudioCompositionSoundTrack {
    var trackID: CMPersistentTrackID
    var indexInFile: Int
}

struct StudioCompositionBuildResult {
    var composition: AVMutableComposition
    var videoComposition: AVMutableVideoComposition
    var renderState: StudioRenderState
    var duration: CMTime
    var screenTrackID: CMPersistentTrackID
    var cameraTrackID: CMPersistentTrackID?

    /// The composition's sound tracks. Empty for a muted project, which has none.
    var soundTracks: [StudioCompositionSoundTrack] = []

    /// How many sound tracks the screen file has, which is what the project's list of them is
    /// matched against.
    var soundTrackCountInFile = 0

    /// The mix that plays each sound track at the gain the project gives it (section 7 of the
    /// project format). Nil when every track plays as recorded, which needs no mix: a player
    /// plays all of them together as they are.
    func audioMix(for project: StudioProject) -> AVAudioMix? {
        let gains = StudioSound.trackGains(project: project, trackCount: soundTrackCountInFile)
        guard gains.contains(where: { $0 != 1 }) else { return nil }
        return mix(gains: gains)
    }

    /// The mix an export is given, which also decides how many sound tracks the video gets. An
    /// export session that has no mix writes a sound track for each one of the composition, and
    /// one that has any mix adds them together into one. A video with the computer's sound and
    /// the microphone as two tracks plays without the voice wherever only the first is played.
    func exportAudioMix(for project: StudioProject) -> AVAudioMix? {
        let gains = StudioSound.trackGains(project: project, trackCount: soundTrackCountInFile)
        guard StudioSound.exportIsMixed(trackGains: gains, soundTracksInVideo: soundTracks.count) else { return nil }
        return mix(gains: gains)
    }

    private func mix(gains: [Double]) -> AVAudioMix {
        let mix = AVMutableAudioMix()
        mix.inputParameters = soundTracks.compactMap { soundTrack -> AVAudioMixInputParameters? in
            guard gains.indices.contains(soundTrack.indexInFile) else { return nil }
            let parameters = AVMutableAudioMixInputParameters()
            parameters.trackID = soundTrack.trackID
            parameters.setVolume(Float(gains[soundTrack.indexInFile]), at: .zero)
            return parameters
        }
        return mix
    }
}

enum StudioCompositionBuilder {
    enum Error: LocalizedError {
        case screenVideoTrackMissing(URL)
        case screenCompositionTrackCreationFailed
        case cameraCompositionTrackCreationFailed
        case cameraVideoTrackMissing(URL)
        case emptyTimeline
        case unsupportedInstruction
        case outputBufferUnavailable

        var errorDescription: String? {
            switch self {
            case let .screenVideoTrackMissing(url):
                return "Studio screen video track is missing or invalid: \(url.lastPathComponent)."
            case .screenCompositionTrackCreationFailed:
                return "Could not create the Studio screen composition track."
            case .cameraCompositionTrackCreationFailed:
                return "Could not create the Studio camera composition track."
            case let .cameraVideoTrackMissing(url):
                return "Studio camera video track is missing or invalid: \(url.lastPathComponent)."
            case .emptyTimeline:
                return "Nothing is left to show after trimming."
            case .unsupportedInstruction:
                return "The Studio compositor received an instruction it does not understand."
            case .outputBufferUnavailable:
                return "The Studio compositor could not get an output frame buffer."
            }
        }
    }

    static func build(
        project: StudioProject,
        events: StudioEvents = StudioEvents(),
        paths: StudioProjectPaths,
        renderSize: CGSize,
        renderState: StudioRenderState? = nil
    ) async throws -> StudioCompositionBuildResult {
        let state = renderState ?? StudioRenderState(project: project, events: events)
        _ = state.update(project: project, events: events)

        let timeMap = StudioTimeMap(sourceDuration: project.sources.screen.duration, edits: project.edits)
        let composition = AVMutableComposition()
        let screenAsset = AVURLAsset(url: paths.screenURL)
        guard let screenTrack = try await screenAsset.loadTracks(withMediaType: .video).first else {
            throw Error.screenVideoTrackMissing(paths.screenURL)
        }
        guard let compositionScreenTrack = composition.addMutableTrack(
            withMediaType: .video,
            preferredTrackID: kCMPersistentTrackID_Invalid
        ) else {
            throw Error.screenCompositionTrackCreationFailed
        }
        compositionScreenTrack.preferredTransform = try await screenTrack.load(.preferredTransform)

        // Each kept piece of the source timeline is placed with exact CMTime arithmetic, one
        // after the other at the recording's own speed. The pieces that play at another speed
        // are stretched or squeezed afterwards, all tracks at once (see below), so the tracks
        // cannot come apart by a rounding step.
        //
        // The screen's picture has to cover every piece from its first instant to its last, and
        // the screen track can be shorter than the recording. The recorder writes a frame only
        // when the screen changes, so the track ends with the last change while the sound and
        // the camera go on, and it can start a moment after the sound does. A frame shows until
        // the next one starts, so what comes before the first frame shows the first frame and
        // what comes after the last one shows the last: one instant of the track, held for as
        // long as is missing. Without that the video would end where the screen last changed,
        // and take the sound and the camera after it along.
        let screenTrackRange = try await screenTrack.load(.timeRange)
        let instant = CMTime(value: 1, timescale: 600)
        let canHoldFrame = screenTrackRange.duration >= instant
        let firstInstant = screenTrackRange.start
        let lastInstant = CMTimeSubtract(screenTrackRange.end, instant)

        // The camera, when the project has one that is still there. It is found before the
        // pieces are placed, because where it begins and ends is where a piece at another speed
        // is divided. The asset is kept with its track: a track does not keep its asset alive.
        var cameraSource: (asset: AVURLAsset, track: AVAssetTrack, startOffset: CMTime, onTimeline: CMTimeRange)?
        if let camera = project.sources.camera,
           let cameraURL = paths.cameraURL,
           FileManager.default.fileExists(atPath: cameraURL.path) {
            let cameraAsset = AVURLAsset(url: cameraURL)
            guard let cameraTrack = try await cameraAsset.loadTracks(withMediaType: .video).first else {
                throw Error.cameraVideoTrackMissing(cameraURL)
            }
            // The camera's own time 0 sits at `startOffset` on the source timeline (it can be
            // negative). `onTimeline` is the camera media expressed in source time.
            let cameraTrackRange = try await cameraTrack.load(.timeRange)
            let startOffset = CMTime(seconds: camera.startOffset.isFinite ? camera.startOffset : 0, preferredTimescale: 600)
            cameraSource = (
                asset: cameraAsset,
                track: cameraTrack,
                startOffset: startOffset,
                onTimeline: CMTimeRange(start: CMTimeAdd(cameraTrackRange.start, startOffset), duration: cameraTrackRange.duration)
            )
        }

        // A piece at another speed is stretched or squeezed as a whole further down, every
        // track at once. Where such a stretch holds more than one thing in a track (the end of
        // the screen's picture and the frame held after it, or the camera and what comes after
        // it), AVFoundation divides the new length among them itself, in nanoseconds, and an
        // export of the result fails ("The video could not be composed", -17390). So a piece at
        // another speed is divided beforehand wherever a track begins or ends inside it, and
        // each part then holds one thing in each track, or nothing.
        var trackEdges = [screenTrackRange.start, screenTrackRange.end]
        if let cameraSource {
            trackEdges += [cameraSource.onTimeline.start, cameraSource.onTimeline.end]
        }

        var placements: [(source: CMTimeRange, at: CMTime, rate: Double)] = []
        var cursor = CMTime.zero
        let wantedParts: [(range: CMTimeRange, rate: Double)] = timeMap.pieces.flatMap { piece in
            let whole = CMTimeRange(start: cmTime(piece.start), end: cmTime(piece.end))
            let parts = piece.rate == 1 ? [whole] : divided(whole, at: trackEdges)
            return parts.map { (range: $0, rate: piece.rate) }
        }
        for piece in wantedParts {
            let wanted = piece.range
            guard wanted.duration > .zero else { continue }
            let onTrack = wanted.intersection(screenTrackRange)
            guard canHoldFrame else {
                // A screen track without a frame's worth of picture has nothing to hold.
                guard onTrack.duration > .zero else { continue }
                try compositionScreenTrack.insertTimeRange(onTrack, of: screenTrack, at: cursor)
                placements.append((source: onTrack, at: cursor, rate: piece.rate))
                cursor = CMTimeAdd(cursor, onTrack.duration)
                continue
            }

            // What is missing by less than the instant that would be stretched over it is a
            // rounding step between two clocks, not a stretch of video: it is left out.
            var at = cursor
            var covered = wanted
            if onTrack.duration > .zero {
                let before = CMTimeSubtract(onTrack.start, wanted.start)
                if before >= instant {
                    try holdFrame(at: firstInstant, of: screenTrack, in: compositionScreenTrack, from: at, for: before)
                    at = CMTimeAdd(at, before)
                } else {
                    covered = CMTimeRange(start: onTrack.start, end: covered.end)
                }
                try compositionScreenTrack.insertTimeRange(onTrack, of: screenTrack, at: at)
                at = CMTimeAdd(at, onTrack.duration)
                let after = CMTimeSubtract(wanted.end, onTrack.end)
                if after >= instant {
                    try holdFrame(at: lastInstant, of: screenTrack, in: compositionScreenTrack, from: at, for: after)
                } else {
                    covered = CMTimeRange(start: covered.start, end: onTrack.end)
                }
            } else {
                // The whole piece lies before the first frame or after the last one.
                guard wanted.duration >= instant else { continue }
                let held = wanted.start >= screenTrackRange.end ? lastInstant : firstInstant
                try holdFrame(at: held, of: screenTrack, in: compositionScreenTrack, from: at, for: wanted.duration)
            }
            placements.append((source: covered, at: cursor, rate: piece.rate))
            cursor = CMTimeAdd(cursor, covered.duration)
        }
        guard cursor > .zero else {
            throw Error.emptyTimeline
        }

        // A piece at another speed has no sound (section 7 of the project format), so its stretch
        // of the sound tracks is left empty.
        var soundTracks: [StudioCompositionSoundTrack] = []
        var soundTrackCountInFile = 0
        if !project.audio.muted {
            let audioTracks = try await screenAsset.loadTracks(withMediaType: .audio)
            soundTrackCountInFile = audioTracks.count
            for (indexInFile, audioTrack) in audioTracks.enumerated() {
                guard let compositionAudioTrack = composition.addMutableTrack(
                    withMediaType: .audio,
                    preferredTrackID: kCMPersistentTrackID_Invalid
                ) else {
                    continue
                }
                let audioTrackRange = try await audioTrack.load(.timeRange)
                var hasSound = false
                for placement in placements where placement.rate == 1 {
                    let source = placement.source.intersection(audioTrackRange)
                    guard source.duration > .zero else { continue }
                    try compositionAudioTrack.insertTimeRange(
                        source,
                        of: audioTrack,
                        at: CMTimeAdd(placement.at, CMTimeSubtract(source.start, placement.source.start))
                    )
                    hasSound = true
                }
                // A track with nothing in it, as when every kept piece plays at another speed,
                // is taken out again: an export of a composition with an empty track can fail.
                if hasSound {
                    soundTracks.append(
                        StudioCompositionSoundTrack(trackID: compositionAudioTrack.trackID, indexInFile: indexInFile)
                    )
                } else {
                    composition.removeTrack(compositionAudioTrack)
                }
            }
        }

        var cameraTrackID: CMPersistentTrackID?
        if let cameraSource {
            let cameraTrack = cameraSource.track
            let startOffset = cameraSource.startOffset
            let onTimeline = cameraSource.onTimeline
            guard let compositionCameraTrack = composition.addMutableTrack(
                withMediaType: .video,
                preferredTrackID: kCMPersistentTrackID_Invalid
            ) else {
                throw Error.cameraCompositionTrackCreationFailed
            }
            compositionCameraTrack.preferredTransform = try await cameraTrack.load(.preferredTransform)
            cameraTrackID = compositionCameraTrack.trackID

            var hasCamera = false
            for placement in placements {
                let overlap = placement.source.intersection(onTimeline)
                guard overlap.duration > .zero else { continue }
                try compositionCameraTrack.insertTimeRange(
                    CMTimeRange(start: CMTimeSubtract(overlap.start, startOffset), duration: overlap.duration),
                    of: cameraTrack,
                    at: CMTimeAdd(placement.at, CMTimeSubtract(overlap.start, placement.source.start))
                )
                hasCamera = true
            }
            // No kept piece has any of the camera in it: the video is then made without a camera
            // track, for the same reason as a sound track with nothing in it.
            if !hasCamera {
                composition.removeTrack(compositionCameraTrack)
                cameraTrackID = nil
            }
        }

        // Now the speed. Going from the last piece to the first keeps the places of the pieces
        // before it, which were worked out at the recording's own speed, good until their turn.
        var outputDuration = cursor
        for placement in placements.reversed() where placement.rate != 1 {
            let range = CMTimeRange(start: placement.at, duration: placement.source.duration)
            // The product comes back in nanoseconds, and every time after it in the composition
            // is then a nanosecond time too. An export of that fails where a held frame follows
            // ("The video could not be composed", -17390), as a run on a real recording showed.
            // So the new length is put on a clock the pieces themselves are on: the times of a
            // project are six-hundredths of a second and a track's are, as a rule, 48,000ths,
            // and both are whole steps of this one.
            let scaled = CMTimeConvertScale(
                CMTimeMultiplyByFloat64(placement.source.duration, multiplier: 1 / placement.rate),
                timescale: speedTimescale,
                method: .roundHalfAwayFromZero
            )
            if scaled > .zero {
                composition.scaleTimeRange(range, toDuration: scaled)
                outputDuration = CMTimeAdd(CMTimeSubtract(outputDuration, placement.source.duration), scaled)
            } else {
                // A sliver that is over before the clock's next step at its speed. A range cannot
                // be scaled to nothing, so it is left out.
                composition.removeTimeRange(range)
                outputDuration = CMTimeSubtract(outputDuration, placement.source.duration)
            }
        }
        guard outputDuration > .zero else {
            throw Error.emptyTimeline
        }

        let videoComposition = AVMutableVideoComposition()
        videoComposition.renderSize = renderSize
        videoComposition.frameDuration = try await frameDuration(project: project, track: screenTrack)
        videoComposition.customVideoCompositorClass = StudioCompositor.self
        videoComposition.instructions = [
            StudioVideoCompositionInstruction(
                timeRange: CMTimeRange(start: .zero, duration: outputDuration),
                screenTrackID: compositionScreenTrack.trackID,
                cameraTrackID: cameraTrackID,
                renderSize: renderSize,
                renderState: state,
                projectDirectory: paths.projectDirectory
            )
        ]

        return StudioCompositionBuildResult(
            composition: composition,
            videoComposition: videoComposition,
            renderState: state,
            duration: outputDuration,
            screenTrackID: compositionScreenTrack.trackID,
            cameraTrackID: cameraTrackID,
            soundTracks: soundTracks,
            soundTrackCountInFile: soundTrackCountInFile
        )
    }

    /// A range divided at each of the given times that lies inside it, in order. A time at
    /// either end of the range, or outside it, divides nothing.
    static func divided(_ range: CMTimeRange, at edges: [CMTime]) -> [CMTimeRange] {
        var parts: [CMTimeRange] = []
        var start = range.start
        for edge in edges.filter({ $0.isNumeric && $0 > range.start && $0 < range.end }).sorted() where edge > start {
            parts.append(CMTimeRange(start: start, end: edge))
            start = edge
        }
        parts.append(CMTimeRange(start: start, end: range.end))
        return parts
    }

    /// The clock the length of a piece at another speed is rounded to.
    private static let speedTimescale: CMTimeScale = 48_000

    static func cmTime(_ seconds: Double) -> CMTime {
        CMTime(seconds: max(0, seconds), preferredTimescale: 600)
    }

    /// Shows the frame that is on screen at `instant` of `source` for `duration`, starting at
    /// `start` of the composition track: one six-hundredth of a second of the track, stretched.
    private static func holdFrame(
        at instant: CMTime,
        of source: AVAssetTrack,
        in track: AVMutableCompositionTrack,
        from start: CMTime,
        for duration: CMTime
    ) throws {
        let sliver = CMTime(value: 1, timescale: 600)
        try track.insertTimeRange(CMTimeRange(start: instant, duration: sliver), of: source, at: start)
        track.scaleTimeRange(CMTimeRange(start: start, duration: sliver), toDuration: duration)
    }

    private static func frameDuration(project: StudioProject, track: AVAssetTrack) async throws -> CMTime {
        let nominalFrameRate = try await track.load(.nominalFrameRate)
        let frameRate = project.sources.screen.frameRate.isFinite && project.sources.screen.frameRate > 0
            ? project.sources.screen.frameRate
            : Double(nominalFrameRate > 0 ? nominalFrameRate : 30)
        // Clamped so a damaged project file cannot ask for an impossible rate.
        let framesPerSecond = min(240, max(1, frameRate.rounded(.up)))
        return CMTime(value: 1, timescale: CMTimeScale(framesPerSecond))
    }
}
