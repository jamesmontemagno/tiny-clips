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
    /// project format). Nil when every track plays as recorded, which needs no mix.
    func audioMix(for project: StudioProject) -> AVAudioMix? {
        let gains = StudioSound.trackGains(project: project, trackCount: soundTrackCountInFile)
        guard gains.contains(where: { $0 != 1 }) else { return nil }
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

        // Each kept piece of the source timeline is placed with exact CMTime arithmetic and clamped
        // to the media that really exists, so no track ever runs past another one. A track that
        // ended a frame early would otherwise leave the last frames without a picture.
        //
        // Every piece is first placed at the recording's own speed, one after the other. The
        // pieces that play at another speed are stretched or squeezed afterwards, all tracks at
        // once (see below), so the tracks cannot come apart by a rounding step.
        let screenTrackRange = try await screenTrack.load(.timeRange)
        var placements: [(source: CMTimeRange, at: CMTime, rate: Double)] = []
        var cursor = CMTime.zero
        for piece in timeMap.pieces {
            let wanted = CMTimeRange(start: cmTime(piece.start), end: cmTime(piece.end))
            let source = wanted.intersection(screenTrackRange)
            guard source.duration > .zero else { continue }
            try compositionScreenTrack.insertTimeRange(source, of: screenTrack, at: cursor)
            placements.append((source: source, at: cursor, rate: piece.rate))
            cursor = CMTimeAdd(cursor, source.duration)
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
                soundTracks.append(
                    StudioCompositionSoundTrack(trackID: compositionAudioTrack.trackID, indexInFile: indexInFile)
                )
                let audioTrackRange = try await audioTrack.load(.timeRange)
                for placement in placements where placement.rate == 1 {
                    let source = placement.source.intersection(audioTrackRange)
                    guard source.duration > .zero else { continue }
                    try compositionAudioTrack.insertTimeRange(
                        source,
                        of: audioTrack,
                        at: CMTimeAdd(placement.at, CMTimeSubtract(source.start, placement.source.start))
                    )
                }
            }
        }

        var cameraTrackID: CMPersistentTrackID?
        if let camera = project.sources.camera,
           let cameraURL = paths.cameraURL,
           FileManager.default.fileExists(atPath: cameraURL.path) {
            let cameraAsset = AVURLAsset(url: cameraURL)
            guard let cameraTrack = try await cameraAsset.loadTracks(withMediaType: .video).first else {
                throw Error.cameraVideoTrackMissing(cameraURL)
            }
            guard let compositionCameraTrack = composition.addMutableTrack(
                withMediaType: .video,
                preferredTrackID: kCMPersistentTrackID_Invalid
            ) else {
                throw Error.cameraCompositionTrackCreationFailed
            }
            compositionCameraTrack.preferredTransform = try await cameraTrack.load(.preferredTransform)
            cameraTrackID = compositionCameraTrack.trackID

            // The camera's own time 0 sits at `startOffset` on the source timeline (it can be
            // negative). `onTimeline` is the camera media expressed in source time.
            let cameraTrackRange = try await cameraTrack.load(.timeRange)
            let startOffset = CMTime(seconds: camera.startOffset.isFinite ? camera.startOffset : 0, preferredTimescale: 600)
            let onTimeline = CMTimeRange(
                start: CMTimeAdd(cameraTrackRange.start, startOffset),
                duration: cameraTrackRange.duration
            )
            for placement in placements {
                let overlap = placement.source.intersection(onTimeline)
                guard overlap.duration > .zero else { continue }
                try compositionCameraTrack.insertTimeRange(
                    CMTimeRange(start: CMTimeSubtract(overlap.start, startOffset), duration: overlap.duration),
                    of: cameraTrack,
                    at: CMTimeAdd(placement.at, CMTimeSubtract(overlap.start, placement.source.start))
                )
            }
        }

        // Now the speed. Going from the last piece to the first keeps the places of the pieces
        // before it, which were worked out at the recording's own speed, good until their turn.
        var outputDuration = cursor
        for placement in placements.reversed() where placement.rate != 1 {
            let range = CMTimeRange(start: placement.at, duration: placement.source.duration)
            let scaled = CMTimeMultiplyByFloat64(placement.source.duration, multiplier: 1 / placement.rate)
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

    static func cmTime(_ seconds: Double) -> CMTime {
        CMTime(seconds: max(0, seconds), preferredTimescale: 600)
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
