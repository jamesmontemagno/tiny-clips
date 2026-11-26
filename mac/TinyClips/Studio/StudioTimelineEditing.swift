import Foundation

struct StudioTimelineMap: Equatable, Sendable {
    struct Clip: Equatable, Sendable {
        var id: String
        var sourceId: String
        var projectStart: Double
        var projectEnd: Double
        var sourceStart: Double
        var sourceEnd: Double
        var timeMap: StudioTimeMap
    }

    let clips: [Clip]
    let duration: Double

    init(timeline: StudioTimeline, sources: [StudioMediaSource]) throws {
        var mapped: [Clip] = []
        var projectStart = 0.0
        let sourcesByID = Dictionary(sources.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        for clip in timeline.clips {
            guard clip.sourceStart.isFinite,
                  clip.sourceEnd.isFinite,
                  clip.sourceStart >= 0,
                  clip.sourceEnd > clip.sourceStart,
                  let source = sourcesByID[clip.sourceId] else {
                throw StudioProjectError.invalidProject("Timeline clip has an invalid source range or unknown source")
            }
            let sourceDuration = source.sources.screen.duration
            guard sourceDuration.isFinite, clip.sourceEnd <= sourceDuration else {
                throw StudioProjectError.invalidProject("Timeline clip source range exceeds its source duration")
            }
            var edits = clip.edits
            edits.trimStart = max(clip.sourceStart, edits.trimStart)
            edits.trimEnd = min(clip.sourceEnd, edits.trimEnd ?? clip.sourceEnd)
            let timeMap = StudioTimeMap(sourceDuration: sourceDuration, edits: edits)
            guard timeMap.outputDuration > 0 else {
                throw StudioProjectError.invalidProject("Timeline clip has no playable duration")
            }
            let projectEnd = projectStart + timeMap.outputDuration
            mapped.append(Clip(
                id: clip.id,
                sourceId: clip.sourceId,
                projectStart: projectStart,
                projectEnd: projectEnd,
                sourceStart: timeMap.trimStart,
                sourceEnd: timeMap.trimEnd,
                timeMap: timeMap
            ))
            projectStart = projectEnd
        }
        clips = mapped
        duration = projectStart
    }

    func sourcePosition(atProjectTime time: Double) -> (clipID: String, sourceID: String, sourceTime: Double)? {
        guard !clips.isEmpty, time.isFinite else { return nil }
        let clampedTime = min(max(0, time), duration)
        let clip = clips.first(where: { clampedTime < $0.projectEnd }) ?? clips[clips.count - 1]
        let localOutput = min(max(0, clampedTime - clip.projectStart), clip.timeMap.outputDuration)
        return (clip.id, clip.sourceId, clip.timeMap.outputToSource(localOutput))
    }

    func projectTime(clipID: String, sourceTime: Double) -> Double? {
        guard sourceTime.isFinite, let clip = clips.first(where: { $0.id == clipID }) else { return nil }
        return clip.projectStart + clip.timeMap.sourceToOutput(sourceTime)
    }
}

enum StudioTimelineEditing {
    static func timelineProject(from project: StudioProject) -> StudioProject {
        guard project.schemaVersion < 2 else { return project }
        let sourceID = "source-\(project.id)"
        let clip = StudioTimelineClip(
            id: "clip-\(project.id)",
            sourceId: sourceID,
            name: project.name,
            sourceStart: 0,
            sourceEnd: project.sources.screen.duration,
            edits: project.edits,
            screen: project.screen,
            camera: project.camera,
            scenes: project.scenes,
            zooms: project.zooms,
            audio: project.audio,
            overlays: project.overlays,
            captions: project.captions
        )
        var upgraded = project
        upgraded.schemaVersion = 2
        upgraded.mediaSources = [
            StudioMediaSource(
                id: sourceID,
                kind: project.sources.screen.external ? .flatVideo : .nativeRecording,
                sources: project.sources
            )
        ]
        upgraded.timeline = StudioTimeline(clips: [clip])
        return upgraded
    }

    static func append(_ clip: StudioTimelineClip, to timeline: StudioTimeline) -> StudioTimeline {
        var updated = timeline
        updated.clips.append(clip)
        return updated
    }

    static func insert(_ clip: StudioTimelineClip, at index: Int, into timeline: StudioTimeline) -> StudioTimeline {
        var updated = timeline
        let safeIndex = min(max(0, index), updated.clips.count)
        updated.clips.insert(clip, at: safeIndex)
        return updated
    }

    static func moveClip(id: String, to index: Int, in timeline: StudioTimeline) -> StudioTimeline? {
        guard let oldIndex = timeline.clips.firstIndex(where: { $0.id == id }) else { return nil }
        var updated = timeline
        let clip = updated.clips.remove(at: oldIndex)
        updated.clips.insert(clip, at: min(max(0, index), updated.clips.count))
        return updated
    }

    static func removeClip(id: String, from timeline: StudioTimeline) -> StudioTimeline? {
        guard timeline.clips.contains(where: { $0.id == id }) else { return nil }
        var updated = timeline
        updated.clips.removeAll { $0.id == id }
        return updated
    }

    static func trimClip(id: String, toStart start: Double, end: Double, in timeline: StudioTimeline) -> StudioTimeline? {
        guard start.isFinite,
              end.isFinite,
              let index = timeline.clips.firstIndex(where: { $0.id == id }) else { return nil }
        let clip = timeline.clips[index]
        guard start >= clip.sourceStart, end <= clip.sourceEnd, end > start else { return nil }
        var updated = timeline
        updated.clips[index].sourceStart = start
        updated.clips[index].sourceEnd = end
        updated.clips[index].edits = clippedEdits(clip.edits, start: start, end: end)
        return updated
    }

    static func splitClip(id: String, at sourceTime: Double, in timeline: StudioTimeline) -> StudioTimeline? {
        guard let index = timeline.clips.firstIndex(where: { $0.id == id }) else { return nil }
        let clip = timeline.clips[index]
        guard sourceTime.isFinite, sourceTime > clip.sourceStart, sourceTime < clip.sourceEnd else { return nil }

        var left = clip
        left.sourceEnd = sourceTime
        left.edits = clippedEdits(clip.edits, start: clip.sourceStart, end: sourceTime)

        var right = clip
        right.id = UUID().uuidString.lowercased()
        right.sourceStart = sourceTime
        right.edits = clippedEdits(clip.edits, start: sourceTime, end: clip.sourceEnd)

        var updated = timeline
        updated.clips.replaceSubrange(index...index, with: [left, right])
        return updated
    }

    private static func clippedEdits(_ edits: StudioEdits, start: Double, end: Double) -> StudioEdits {
        var result = edits
        result.trimStart = max(start, edits.trimStart)
        result.trimEnd = min(end, edits.trimEnd ?? end)
        result.cuts = edits.cuts.compactMap { range in
            let lower = max(start, range.start)
            let upper = min(end, range.end)
            return upper > lower ? StudioTimeRange(start: lower, end: upper, extra: range.extra) : nil
        }
        result.speed = edits.speed.compactMap { range in
            let lower = max(start, range.start)
            let upper = min(end, range.end)
            return upper > lower ? StudioSpeedRange(start: lower, end: upper, rate: range.rate, extra: range.extra) : nil
        }
        return result
    }
}
