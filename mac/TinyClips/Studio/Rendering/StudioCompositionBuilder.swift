import AVFoundation
import CoreGraphics
import Foundation

// MARK: - Composition Builder

struct StudioCompositionBuildResult {
    var composition: AVMutableComposition
    var videoComposition: AVMutableVideoComposition
    var renderState: StudioRenderState
    var duration: CMTime
    var screenTrackID: CMPersistentTrackID
    var cameraTrackID: CMPersistentTrackID?
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
        let screenTrackRange = try await screenTrack.load(.timeRange)
        var placements: [(source: CMTimeRange, at: CMTime)] = []
        var cursor = CMTime.zero
        for segment in timeMap.segments {
            let wanted = CMTimeRange(start: cmTime(segment.start), end: cmTime(segment.end))
            let source = wanted.intersection(screenTrackRange)
            guard source.duration > .zero else { continue }
            try compositionScreenTrack.insertTimeRange(source, of: screenTrack, at: cursor)
            placements.append((source: source, at: cursor))
            cursor = CMTimeAdd(cursor, source.duration)
        }
        guard cursor > .zero else {
            throw Error.emptyTimeline
        }

        if !project.audio.muted {
            for audioTrack in try await screenAsset.loadTracks(withMediaType: .audio) {
                guard let compositionAudioTrack = composition.addMutableTrack(
                    withMediaType: .audio,
                    preferredTrackID: kCMPersistentTrackID_Invalid
                ) else {
                    continue
                }
                let audioTrackRange = try await audioTrack.load(.timeRange)
                for placement in placements {
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

        let outputDuration = cursor
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
            cameraTrackID: cameraTrackID
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
