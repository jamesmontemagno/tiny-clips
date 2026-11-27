import AVFoundation
import CoreGraphics
import Foundation

// MARK: - Composition Builder

/// A sound track of the composition and which of the screen file's sound tracks it plays.
struct StudioCompositionSoundTrack {
    var trackID: CMPersistentTrackID
    var indexInFile: Int
    struct ClipVolume {
        let range: CMTimeRange
        let state: StudioRenderState
        let trackCount: Int
        let fadeIn: CMTimeRange?
        let fadeOut: CMTimeRange?

        func apply(gain: Float, to parameters: AVMutableAudioMixInputParameters) {
            if let fadeIn {
                parameters.setVolumeRamp(fromStartVolume: 0, toEndVolume: gain, timeRange: fadeIn)
            }
            let hold = CMTimeRange(start: fadeIn?.end ?? range.start, end: fadeOut?.start ?? range.end)
            if hold.duration > .zero {
                parameters.setVolumeRamp(fromStartVolume: gain, toEndVolume: gain, timeRange: hold)
            }
            if let fadeOut {
                parameters.setVolumeRamp(fromStartVolume: gain, toEndVolume: 0, timeRange: fadeOut)
            }
        }
    }
    var clipVolumes: [ClipVolume] = []
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
    var clips: [StudioClipRenderContext] = []

    private var hasAudioRamps: Bool {
        soundTracks.contains { $0.clipVolumes.contains { $0.fadeIn != nil || $0.fadeOut != nil } }
    }

    func trackGains(for project: StudioProject) -> [[Double]] {
        let single = StudioSound.trackGains(project: project, trackCount: soundTrackCountInFile)
        return soundTracks.map { track in
            if clips.isEmpty { return [single[track.indexInFile]] }
            return track.clipVolumes.map {
                StudioSound.trackGains(project: $0.state.read().project, trackCount: $0.trackCount)[track.indexInFile]
            }
        }
    }

    /// The mix that plays each sound track at the gain the project gives it (section 7 of the
    /// project format). Nil when every track plays as recorded, which needs no mix: a player
    /// plays all of them together as they are.
    func audioMix(for project: StudioProject) -> AVAudioMix? {
        let gains = trackGains(for: project)
        guard hasAudioRamps || gains.joined().contains(where: { $0 != 1 }) else { return nil }
        return mix(gains: gains)
    }

    /// The mix an export is given, which also decides how many sound tracks the video gets. An
    /// export session that has no mix writes a sound track for each one of the composition, and
    /// one that has any mix adds them together into one. A video with the computer's sound and
    /// the microphone as two tracks plays without the voice wherever only the first is played.
    func exportAudioMix(for project: StudioProject) -> AVAudioMix? {
        let gains = trackGains(for: project)
        guard hasAudioRamps || StudioSound.exportIsMixed(trackGains: Array(gains.joined()), soundTracksInVideo: soundTracks.count) else { return nil }
        return mix(gains: gains)
    }

    private func mix(gains: [[Double]]) -> AVAudioMix {
        let mix = AVMutableAudioMix()
        mix.inputParameters = zip(soundTracks, gains).map { soundTrack, volumes in
            let parameters = AVMutableAudioMixInputParameters()
            parameters.trackID = soundTrack.trackID
            if soundTrack.clipVolumes.isEmpty {
                parameters.setVolume(Float(volumes[0]), at: .zero)
            } else {
                for (volume, clip) in zip(volumes, soundTrack.clipVolumes) {
                    clip.apply(gain: Float(volume), to: parameters)
                }
            }
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
        case timelineTrackCreationFailed
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
            case .timelineTrackCreationFailed:
                return "Could not create a media track for the Studio timeline."
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

        if project.schemaVersion == 2 {
            return try await buildTimeline(project, paths: paths, renderSize: renderSize, state: state)
        }
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
        let videoRange = CMTimeRange(start: .zero, duration: outputDuration)
        videoComposition.instructions = [
            StudioVideoCompositionInstruction(
                timeRange: videoRange,
                primary: StudioVideoLayer(timeRange: videoRange, screenTrackID: compositionScreenTrack.trackID,
                                          cameraTrackID: cameraTrackID, renderState: state,
                                          screenTransform: compositionScreenTrack.preferredTransform),
                renderSize: renderSize,
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
        return frameDuration(configured: project.sources.screen.frameRate, fallback: Double(nominalFrameRate > 0 ? nominalFrameRate : 30))
    }

    private static func frameDuration(configured: Double, fallback: Double = 30) -> CMTime {
        let frameRate = configured.isFinite && configured > 0 ? configured : fallback
        // Clamped so a damaged project file cannot ask for an impossible rate.
        let framesPerSecond = min(240, max(1, frameRate.rounded(.up)))
        return CMTime(value: 1, timescale: CMTimeScale(framesPerSecond))
    }
}

// MARK: - Sequential clip lane

extension StudioCompositionBuilder {
    private struct MediaTrack {
        let url: URL
        let id: CMPersistentTrackID
        let range: CMTimeRange

        init(_ track: AVAssetTrack, url: URL) async throws {
            self.url = url
            id = track.trackID
            range = try await track.load(.timeRange)
        }
    }

    /// Only track metadata is needed to make edit lists. The player/reader opens the source
    /// media for the active segments; neither a player nor a decoder is kept for every clip.
    private struct TimelineMedia {
        let screen: MediaTrack
        let transform: CGAffineTransform
        let displaySize: CGSize
        let camera: MediaTrack?
        let sound: [MediaTrack]
        let events: StudioEvents

        init(source: StudioMediaSource, directory: URL) async throws {
            func file(_ name: String) throws -> URL {
                let url = directory.appendingPathComponent(try StudioJSON.requirePlainFileName(name, "mediaSources file"))
                guard url.resolvingSymlinksInPath().deletingLastPathComponent().standardizedFileURL.path
                        == directory.resolvingSymlinksInPath().standardizedFileURL.path else {
                    throw StudioProjectError.invalidProject("A timeline source is outside the project folder")
                }
                return url
            }
            let url = try file(source.sources.screen.file)
            let asset = AVURLAsset(url: url, options: [AVURLAssetPreferPreciseDurationAndTimingKey: true])
            guard let video = try await asset.loadTracks(withMediaType: .video).first else {
                throw Error.screenVideoTrackMissing(url)
            }
            screen = try await MediaTrack(video, url: url)
            guard screen.range.isValid, screen.range.duration > .zero else { throw Error.screenVideoTrackMissing(url) }
            transform = try await video.load(.preferredTransform)
            let size = try await video.load(.naturalSize)
            displaySize = CGRect(origin: .zero, size: size).applying(transform).standardized.size
            guard displaySize.width.isFinite, displaySize.width >= 1,
                  displaySize.height.isFinite, displaySize.height >= 1 else { throw Error.screenVideoTrackMissing(url) }

            var audio: [MediaTrack] = []
            for track in try await asset.loadTracks(withMediaType: .audio) {
                audio.append(try await MediaTrack(track, url: url))
            }
            sound = audio
            if let name = source.sources.camera?.file {
                let url = try file(name)
                let asset = AVURLAsset(url: url, options: [AVURLAssetPreferPreciseDurationAndTimingKey: true])
                guard let track = try await asset.loadTracks(withMediaType: .video).first else {
                    throw Error.cameraVideoTrackMissing(url)
                }
                camera = try await MediaTrack(track, url: url)
            } else {
                camera = nil
            }
            if let name = source.sources.events {
                let url = try file(name)
                events = try StudioJSON.makeDecoder().decode(StudioEvents.self, from: Data(contentsOf: url))
            } else {
                events = StudioEvents()
            }
        }
    }

    /// One reusable composition track. Unlike stretching a whole single-recording composition,
    /// an absolute edit list never moves a preceding clip. Empty segments are silence/absent
    /// camera, never a freeze of the previous clip. Commit once per track.
    private struct LaneTrack {
        let track: AVMutableCompositionTrack
        var segments: [AVCompositionTrackSegment] = []
        var volumes: [StudioCompositionSoundTrack.ClipVolume] = []

        init(in composition: AVMutableComposition, type: AVMediaType) throws {
            guard let track = composition.addMutableTrack(withMediaType: type, preferredTrackID: kCMPersistentTrackID_Invalid)
            else { throw Error.timelineTrackCreationFailed }
            self.track = track
        }

        mutating func append(_ media: MediaTrack, source: CMTimeRange, target: CMTimeRange) {
            guard target.duration > .zero, source.duration > .zero else { return }
            let end = segments.last?.timeMapping.target.end ?? .zero
            if end < target.start {
                segments.append(AVCompositionTrackSegment(timeRange: CMTimeRange(start: end, end: target.start)))
            }
            segments.append(AVCompositionTrackSegment(url: media.url, trackID: media.id,
                                                      sourceTimeRange: source, targetTimeRange: target))
        }

        func commit() throws {
            try track.validateSegments(segments)
            track.segments = segments
        }
    }

    /// Only a real overlap needs a second set of tracks. At cuts the next clip reuses the same set.
    private struct TrackBank {
        var screen: LaneTrack
        var camera: LaneTrack?
        var sounds: [Int: LaneTrack] = [:]
        var layers: [StudioVideoLayer] = []

        init(in composition: AVMutableComposition) throws {
            screen = try LaneTrack(in: composition, type: .video)
        }
    }

    private static func buildTimeline(_ project: StudioProject, paths: StudioProjectPaths,
                                      renderSize: CGSize, state: StudioRenderState) async throws -> StudioCompositionBuildResult {
        guard let sources = project.mediaSources, let timeline = project.timeline, !timeline.clips.isEmpty else {
            throw Error.emptyTimeline
        }
        let map = try StudioTimelineMap(timeline: timeline, sources: sources)
        let registry = Dictionary(sources.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        let composition = AVMutableComposition()
        var banks = [try TrackBank(in: composition)]
        var bank = 0
        var mediaByID: [String: TimelineMedia] = [:]
        var contexts: [StudioClipRenderContext] = []
        func time(_ seconds: Double) -> CMTime { CMTime(seconds: seconds, preferredTimescale: speedTimescale) }
        let joins = map.clips.map { clip in
            clip.entry.flatMap { join -> (kind: StudioClipTransition, range: CMTimeRange)? in
                let range = CMTimeRange(start: time(join.start), end: time(join.end))
                return range.duration > .zero ? (join.kind, range) : nil
            }
        }

        for (index, clip) in timeline.clips.enumerated() {
            try Task.checkCancellation()
            let span = map.clips[index]
            guard let source = registry[clip.sourceId] else { throw Error.emptyTimeline }
            let media: TimelineMedia
            if let cached = mediaByID[source.id] {
                media = cached
            } else {
                media = try await TimelineMedia(source: source, directory: paths.projectDirectory)
                mediaByID[source.id] = media
            }
            let context = StudioClipRenderContext(project: project, clip: clip, source: source,
                                                 displaySize: media.displaySize, events: media.events)
            contexts.append(context)
            let start = time(span.projectStart)
            let range = CMTimeRange(start: start, end: time(span.projectEnd))
            guard range.duration > .zero else { continue }
            let fadeIn = joins[index].flatMap { $0.kind == .crossDissolve ? $0.range : nil }
            let next = index + 1 < joins.count ? joins[index + 1] : nil
            let fadeOut = next.flatMap { $0.kind == .crossDissolve ? $0.range : nil }
            if fadeIn != nil {
                bank = 1 - bank
                if bank == banks.count { banks.append(try TrackBank(in: composition)) }
            }
            var usedSound = Set<Int>()
            var elapsed = 0.0
            for piece in span.timeMap.pieces {
                var cameraID: CMPersistentTrackID?
                let interval = CMTimeRange(start: CMTime(seconds: piece.start, preferredTimescale: 600_000_000),
                                           end: CMTime(seconds: piece.end, preferredTimescale: 600_000_000))
                // Round absolute boundaries on one clock, not the durations of individual
                // segments. Splits, gaps and mixed source clocks cannot accumulate lane drift.
                func output(_ sourceTime: CMTime) -> CMTime {
                    let offset: Double
                    if sourceTime == interval.start { offset = 0 }
                    else if sourceTime == interval.end { offset = piece.outputDuration }
                    else { offset = (sourceTime.seconds - piece.start) / piece.rate }
                    return time(span.projectStart + (elapsed + offset))
                }
                func target(_ part: CMTimeRange) -> CMTimeRange {
                    CMTimeRange(start: output(part.start), end: output(part.end))
                }
                let pieceRange = target(interval)
                guard pieceRange.duration > .zero else {
                    elapsed += piece.outputDuration
                    continue
                }

                // Sparse native captures hold the first/last screen frame over missing video,
                // just as in v1. Camera and audio only play where their own media exists.
                let picture = media.screen.range
                let instant = min(CMTime(value: 1, timescale: 600), picture.duration)
                for part in divided(interval, at: [picture.start, picture.end]) {
                    var samples = part
                    if part.end <= picture.start { samples = CMTimeRange(start: picture.start, duration: instant) }
                    if part.start >= picture.end { samples = CMTimeRange(start: picture.end - instant, duration: instant) }
                    banks[bank].screen.append(media.screen, source: samples, target: target(part))
                }
                if let feed = media.camera {
                    let offset = time(source.sources.camera?.startOffset ?? 0)
                    let available = CMTimeRange(start: feed.range.start + offset, duration: feed.range.duration)
                    let overlap = interval.intersection(available)
                    if overlap.duration > .zero, target(overlap).duration > .zero {
                        if banks[bank].camera == nil { banks[bank].camera = try LaneTrack(in: composition, type: .video) }
                        banks[bank].camera?.append(feed, source: CMTimeRange(start: overlap.start - offset, duration: overlap.duration),
                                                  target: target(overlap))
                        cameraID = banks[bank].camera?.track.trackID
                    }
                }
                if piece.rate == 1, !(clip.audio ?? project.audio).muted {
                    for (index, track) in media.sound.enumerated() {
                        let overlap = interval.intersection(track.range)
                        guard overlap.duration > .zero, target(overlap).duration > .zero else { continue }
                        if banks[bank].sounds[index] == nil { banks[bank].sounds[index] = try LaneTrack(in: composition, type: .audio) }
                        banks[bank].sounds[index]?.append(track, source: overlap, target: target(overlap))
                        if usedSound.insert(index).inserted {
                            banks[bank].sounds[index]?.volumes.append(.init(range: range, state: context.state, trackCount: media.sound.count,
                                                                            fadeIn: fadeIn, fadeOut: fadeOut))
                        }
                    }
                }
                let layer = StudioVideoLayer(timeRange: pieceRange, screenTrackID: banks[bank].screen.track.trackID,
                                             cameraTrackID: cameraID, renderState: context.state, screenTransform: media.transform,
                                             piece: piece, clipOutputStart: elapsed, clipIndex: index)
                banks[bank].layers.append(layer)
                elapsed += piece.outputDuration
            }
        }
        let instructions = try timelineInstructions(banks: banks, joins: joins.compactMap { $0 },
                                                    renderSize: renderSize, directory: paths.projectDirectory)
        guard !instructions.isEmpty else { throw Error.emptyTimeline }
        let soundTracks = try banks.flatMap { bank in
            try bank.screen.commit()
            try bank.camera?.commit()
            return try bank.sounds.sorted(by: { $0.key < $1.key }).map { index, lane in
                try lane.commit()
                return StudioCompositionSoundTrack(trackID: lane.track.trackID, indexInFile: index, clipVolumes: lane.volumes)
            }
        }
        let video = AVMutableVideoComposition()
        // Canvas and cadence belong to the destination, not the first or selected lane clip.
        video.renderSize = renderSize
        video.frameDuration = frameDuration(configured: project.sources.screen.frameRate)
        video.customVideoCompositorClass = StudioCompositor.self
        video.instructions = instructions
        return StudioCompositionBuildResult(
            composition: composition, videoComposition: video, renderState: state, duration: time(map.duration),
            screenTrackID: banks[0].screen.track.trackID, cameraTrackID: banks[0].camera?.track.trackID,
            soundTracks: soundTracks, clips: contexts
        )
    }

    /// Partition on both clips' piece edges and the join edges. A cut or speed change
    /// inside a dissolve changes its source piece, not the progress of the dissolve.
    private static func timelineInstructions(banks: [TrackBank], joins: [(kind: StudioClipTransition, range: CMTimeRange)],
                                             renderSize: CGSize, directory: URL) throws -> [StudioVideoCompositionInstruction] {
        let layers = banks.map(\.layers)
        if joins.isEmpty {
            return layers[0].map { .init(timeRange: $0.timeRange, primary: $0, renderSize: renderSize, projectDirectory: directory) }
        }
        let edges = (layers.flatMap { $0.flatMap { [$0.timeRange.start, $0.timeRange.end] } }
                     + joins.flatMap { [$0.range.start, $0.range.end] }).sorted()
        var cursors = Array(repeating: 0, count: banks.count)
        var joinIndex = 0
        var instructions: [StudioVideoCompositionInstruction] = []
        for (start, end) in zip(edges, edges.dropFirst()) where end > start {
            var active: [StudioVideoLayer] = []
            for bank in layers.indices {
                while cursors[bank] < layers[bank].count, layers[bank][cursors[bank]].timeRange.end <= start {
                    cursors[bank] += 1
                }
                if cursors[bank] < layers[bank].count {
                    let layer = layers[bank][cursors[bank]]
                    if layer.timeRange.start <= start { active.append(layer) }
                }
            }
            active.sort { $0.clipIndex < $1.clipIndex }
            guard let primary = active.first else { throw Error.emptyTimeline }
            while joinIndex < joins.count, joins[joinIndex].range.end <= start { joinIndex += 1 }
            var effect = StudioVideoCompositionInstruction.Effect.none
            if joinIndex < joins.count, joins[joinIndex].range.start <= start {
                let join = joins[joinIndex]
                switch join.kind {
                case .fadeThroughBlack, .fadeThroughWhite:
                    effect = .fadeThrough(white: join.kind == .fadeThroughWhite, range: join.range)
                case .crossDissolve:
                    guard active.count == 2 else { throw Error.unsupportedInstruction }
                    effect = .dissolve(to: active[1], range: join.range)
                case .cut: break
                }
            }
            instructions.append(.init(timeRange: CMTimeRange(start: start, end: end), primary: primary,
                                      effect: effect, renderSize: renderSize, projectDirectory: directory))
        }
        return instructions
    }
}
