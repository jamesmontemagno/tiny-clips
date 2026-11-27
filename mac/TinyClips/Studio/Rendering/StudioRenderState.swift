import AVFoundation
import Foundation

// MARK: - Render State

struct StudioRenderSnapshot: Sendable {
    var project: StudioProject
    var events: StudioEvents
    var timeMap: StudioTimeMap
    /// What the video shows, in output time. Empty when the project does not draw its captions.
    var captions: [StudioResolvedCaption]

    init(project: StudioProject, events: StudioEvents) {
        self.project = project
        self.events = events
        timeMap = StudioTimeMap(sourceDuration: project.sources.screen.duration, edits: project.edits)
        captions = project.overlays.captions.enabled
            ? StudioCaptionResolver.resolve(captions: project.captions, timeMap: timeMap)
            : []
    }

    var timelineSignature: StudioTimelineSignature {
        StudioTimelineSignature(project: project)
    }
}

struct StudioTimelineSignature: Equatable, Sendable {
    struct Clip: Equatable, Sendable {
        let id: String
        let sourceID: String
        let start: Double
        let end: Double
        let transitionIn: StudioClipTransition
        let edits: StudioEdits
        let muted: Bool
    }

    var clips: [Clip]?
    var registry: [StudioMediaSource]?
    var screenFile: String
    var cameraFile: String?
    var screenDuration: Double
    var cameraDuration: Double?
    var cameraStartOffset: Double?
    var edits: StudioEdits
    var audioMuted: Bool
    var frameRate: Double

    init(project: StudioProject) {
        if project.schemaVersion == 2 {
            registry = project.mediaSources
            clips = project.timeline?.clips.map {
                Clip(id: $0.id, sourceID: $0.sourceId, start: $0.sourceStart, end: $0.sourceEnd,
                     transitionIn: $0.transitionIn, edits: $0.edits, muted: ($0.audio ?? project.audio).muted)
            }
        }
        screenFile = project.sources.screen.file
        cameraFile = project.sources.camera?.file
        screenDuration = project.sources.screen.duration
        cameraDuration = project.sources.camera?.duration
        cameraStartOffset = project.sources.camera?.startOffset
        edits = project.edits
        audioMuted = project.audio.muted
        frameRate = project.sources.screen.frameRate
    }
}

/// A v2 instruction feeds the existing single-recording resolver a source-local view. This
/// projection is never persisted. In particular, the registered dimensions remain coded
/// dimensions on disk; layout uses the displayed dimensions after the file's transform.
struct StudioClipRenderContext {
    let source: StudioMediaSource
    let displaySize: CGSize
    let state: StudioRenderState

    init(project: StudioProject, clip: StudioTimelineClip, source: StudioMediaSource,
         displaySize: CGSize, events: StudioEvents) {
        self.source = source
        self.displaySize = displaySize
        state = StudioRenderState(project: Self.projection(project, clip, source, displaySize), events: events)
    }

    func update(project: StudioProject, clip: StudioTimelineClip) {
        state.update(project: Self.projection(project, clip, source, displaySize))
    }

    private static func projection(_ project: StudioProject, _ clip: StudioTimelineClip,
                                   _ source: StudioMediaSource, _ displaySize: CGSize) -> StudioProject {
        var local = clip.resolvedProject(in: project, source: source)
        local.sources.screen.width = Int(displaySize.width.rounded())
        local.sources.screen.height = Int(displaySize.height.rounded())
        return local
    }
}

final class StudioRenderState: @unchecked Sendable {
    private let lock = NSLock()
    private var snapshot: StudioRenderSnapshot

    init(project: StudioProject, events: StudioEvents = StudioEvents()) {
        snapshot = StudioRenderSnapshot(project: project, events: events)
    }

    func read() -> StudioRenderSnapshot {
        lock.lock()
        let current = snapshot
        lock.unlock()
        return current
    }

    @discardableResult
    func update(project: StudioProject, events: StudioEvents? = nil) -> StudioTimelineSignature {
        lock.lock()
        snapshot = StudioRenderSnapshot(project: project, events: events ?? snapshot.events)
        let signature = snapshot.timelineSignature
        lock.unlock()
        return signature
    }

    @discardableResult
    func update(events: StudioEvents) -> StudioTimelineSignature {
        lock.lock()
        snapshot.events = events
        let signature = snapshot.timelineSignature
        lock.unlock()
        return signature
    }
}
