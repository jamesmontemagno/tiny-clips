import AVFoundation
import Foundation

// MARK: - Render State

struct StudioRenderSnapshot: Sendable {
    var project: StudioProject
    var events: StudioEvents
    var timeMap: StudioTimeMap

    var timelineSignature: StudioTimelineSignature {
        StudioTimelineSignature(project: project)
    }
}

struct StudioTimelineSignature: Equatable, Sendable {
    var screenFile: String
    var cameraFile: String?
    var screenDuration: Double
    var cameraDuration: Double?
    var cameraStartOffset: Double?
    var edits: StudioEdits
    var audioMuted: Bool
    var frameRate: Double

    init(project: StudioProject) {
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

final class StudioRenderState: @unchecked Sendable {
    private let lock = NSLock()
    private var snapshot: StudioRenderSnapshot

    init(project: StudioProject, events: StudioEvents = StudioEvents()) {
        snapshot = StudioRenderSnapshot(
            project: project,
            events: events,
            timeMap: StudioTimeMap(sourceDuration: project.sources.screen.duration, edits: project.edits)
        )
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
        snapshot = StudioRenderSnapshot(
            project: project,
            events: events ?? snapshot.events,
            timeMap: StudioTimeMap(sourceDuration: project.sources.screen.duration, edits: project.edits)
        )
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
