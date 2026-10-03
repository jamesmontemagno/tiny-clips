import AVFoundation
import CoreGraphics
import Foundation

// MARK: - Playback

@MainActor
final class StudioPlayback {
    let player: AVPlayer

    private var project: StudioProject
    private var events: StudioEvents
    private var paths: StudioProjectPaths
    private var renderSize: CGSize
    private let renderState: StudioRenderState
    private var timelineSignature: StudioTimelineSignature
    private var currentBuild: StudioCompositionBuildResult?

    init(
        project: StudioProject,
        events: StudioEvents = StudioEvents(),
        paths: StudioProjectPaths,
        renderSize: CGSize
    ) {
        self.project = project
        self.events = events
        self.paths = paths
        self.renderSize = renderSize
        self.renderState = StudioRenderState(project: project, events: events)
        self.timelineSignature = StudioTimelineSignature(project: project)
        self.player = AVPlayer()
    }

    @discardableResult
    func makePlayerItem() async throws -> AVPlayerItem {
        let build = try await StudioCompositionBuilder.build(
            project: project,
            events: events,
            paths: paths,
            renderSize: renderSize,
            renderState: renderState
        )
        currentBuild = build
        timelineSignature = StudioTimelineSignature(project: project)

        let item = AVPlayerItem(asset: build.composition)
        item.videoComposition = build.videoComposition
        player.replaceCurrentItem(with: item)
        return item
    }

    @discardableResult
    func apply(
        project: StudioProject,
        events: StudioEvents? = nil,
        paths: StudioProjectPaths? = nil,
        renderSize: CGSize? = nil
    ) async throws -> AVPlayerItem {
        let oldSignature = timelineSignature
        self.project = project
        if let events {
            self.events = events
        }
        if let paths {
            self.paths = paths
        }
        if let renderSize {
            self.renderSize = renderSize
        }
        let newSignature = renderState.update(project: project, events: self.events)
        let sizeChanged = renderSize != nil && self.renderSize != currentBuild?.videoComposition.renderSize

        guard oldSignature != newSignature || paths != nil || sizeChanged || player.currentItem == nil else {
            forceRedraw()
            if let item = player.currentItem {
                return item
            }
            return try await makePlayerItem()
        }
        return try await makePlayerItem()
    }

    func forceRedraw() {
        guard let item = player.currentItem,
              let videoComposition = item.videoComposition else {
            return
        }
        if let copy = videoComposition.mutableCopy() as? AVMutableVideoComposition {
            copy.customVideoCompositorClass = videoComposition.customVideoCompositorClass
            copy.instructions = videoComposition.instructions
            item.videoComposition = copy
        } else {
            item.videoComposition = videoComposition
        }
    }
}
