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

        var cursor = CMTime.zero
        for segment in timeMap.segments {
            let duration = cmTime(segment.end - segment.start)
            guard duration > .zero else { continue }
            try compositionScreenTrack.insertTimeRange(
                CMTimeRange(start: cmTime(segment.start), duration: duration),
                of: screenTrack,
                at: cursor
            )
            cursor = CMTimeAdd(cursor, duration)
        }

        if !project.audio.muted {
            for audioTrack in try await screenAsset.loadTracks(withMediaType: .audio) {
                guard let compositionAudioTrack = composition.addMutableTrack(
                    withMediaType: .audio,
                    preferredTrackID: kCMPersistentTrackID_Invalid
                ) else {
                    continue
                }
                var audioCursor = CMTime.zero
                for segment in timeMap.segments {
                    let duration = cmTime(segment.end - segment.start)
                    guard duration > .zero else { continue }
                    try compositionAudioTrack.insertTimeRange(
                        CMTimeRange(start: cmTime(segment.start), duration: duration),
                        of: audioTrack,
                        at: audioCursor
                    )
                    audioCursor = CMTimeAdd(audioCursor, duration)
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

            var outputCursor = 0.0
            for segment in timeMap.segments {
                let overlapStart = max(segment.start, camera.startOffset)
                let overlapEnd = min(segment.end, camera.startOffset + camera.duration)
                if overlapEnd > overlapStart {
                    try compositionCameraTrack.insertTimeRange(
                        CMTimeRange(
                            start: cmTime(overlapStart - camera.startOffset),
                            duration: cmTime(overlapEnd - overlapStart)
                        ),
                        of: cameraTrack,
                        at: cmTime(outputCursor + overlapStart - segment.start)
                    )
                }
                outputCursor += segment.end - segment.start
            }
        }

        let outputDuration = cmTime(timeMap.outputDuration)
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
