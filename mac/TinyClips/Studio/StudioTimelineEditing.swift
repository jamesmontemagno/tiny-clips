import Foundation

extension StudioTimelineClip {
    /// A single-source view for the existing inspectors and compositor. Never persist this as the manifest.
    func resolvedProject(in project: StudioProject, source: StudioMediaSource) -> StudioProject {
        var local = project
        local.schemaVersion = 1
        local.timeline = nil
        local.mediaSources = nil
        local.sources = source.sources
        local.edits = boundedEdits
        local.screen = screen ?? project.screen
        local.camera = camera ?? project.camera
        local.scenes = scenes ?? project.scenes
        local.zooms = zooms ?? project.zooms
        local.audio = audio ?? project.audio
        local.overlays = overlays ?? project.overlays
        local.captions = captions ?? project.captions
        return local
    }

    var boundedEdits: StudioEdits {
        var result = edits
        result.trimStart = max(sourceStart, edits.trimStart)
        result.trimEnd = min(sourceEnd, edits.trimEnd ?? sourceEnd)
        return result
    }
}

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
        let sourceIDs = Set(sources.map(\.id))
        let clipIDs = Set(timeline.clips.map(\.id))
        guard !timeline.clips.isEmpty, sourceIDs.count == sources.count, clipIDs.count == timeline.clips.count,
              sourceIDs.isDisjoint(with: clipIDs),
              sourceIDs.union(clipIDs).allSatisfy({ !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }),
              sources.allSatisfy({ $0.kind != .flatVideo || ($0.sources.camera == nil && $0.sources.events == nil) }) else {
            throw StudioProjectError.invalidProject("A timeline requires at least one clip, distinct IDs, and valid source layers.")
        }
        let sourcesByID = Dictionary(uniqueKeysWithValues: sources.map { ($0.id, $0) })
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
            let timeMap = StudioTimeMap(sourceDuration: sourceDuration, edits: clip.boundedEdits)
            guard timeMap.outputDuration > 0 else {
                throw StudioProjectError.invalidProject("Timeline clip has no playable duration")
            }
            let projectEnd = projectStart + timeMap.outputDuration
            guard projectEnd.isFinite, projectEnd > projectStart else {
                throw StudioProjectError.invalidProject("Clip duration cannot be represented on the project timeline.")
            }
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

    /// The incoming source is already in `sources`. Inserting inside a clip divides it in the same edit.
    static func insert(_ clip: StudioTimelineClip, atProjectTime time: Double, in timeline: StudioTimeline,
                       sources: [StudioMediaSource]) throws -> StudioTimeline {
        let map = try StudioTimelineMap(timeline: timeline, sources: sources)
        guard time.isFinite, time >= 0, time <= map.duration else {
            throw StudioProjectError.invalidProject("Choose an insertion point inside the project.")
        }
        var result = timeline
        var index = result.clips.count
        if let target = map.clips.firstIndex(where: { time < $0.projectEnd }) {
            index = target
            if time > map.clips[target].projectStart {
                guard let position = map.sourcePosition(atProjectTime: time),
                      let split = splitClip(id: position.clipID, at: position.sourceTime, in: result) else {
                    throw StudioProjectError.invalidProject("The clip cannot be split at this position.")
                }
                result = split
                index += 1
            }
        }
        result.clips.insert(clip, at: index)
        _ = try StudioTimelineMap(timeline: result, sources: sources)
        return result
    }

    static func moveClip(id: String, to index: Int, in timeline: StudioTimeline) -> StudioTimeline? {
        guard let oldIndex = timeline.clips.firstIndex(where: { $0.id == id }) else { return nil }
        var updated = timeline
        let clip = updated.clips.remove(at: oldIndex)
        updated.clips.insert(clip, at: min(max(0, index), updated.clips.count))
        return updated
    }

    static func removeClip(id: String, from timeline: StudioTimeline) -> StudioTimeline? {
        guard timeline.clips.count > 1, timeline.clips.contains(where: { $0.id == id }) else { return nil }
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
        guard hasPlayableTime(updated.clips[index]) else { return nil }
        return updated
    }

    static func splitClip(id: String, at sourceTime: Double, in timeline: StudioTimeline) -> StudioTimeline? {
        guard let index = timeline.clips.firstIndex(where: { $0.id == id }) else { return nil }
        let clip = timeline.clips[index]
        guard sourceTime.isFinite, sourceTime > clip.sourceStart, sourceTime < clip.sourceEnd,
              !clip.edits.cuts.contains(where: { sourceTime > $0.start && sourceTime < $0.end }) else { return nil }

        var left = clip
        left.sourceEnd = sourceTime
        left.edits = clippedEdits(clip.edits, start: clip.sourceStart, end: sourceTime)

        var right = clip
        right.id = UUID().uuidString.lowercased()
        right.sourceStart = sourceTime
        right.edits = clippedEdits(clip.edits, start: sourceTime, end: clip.sourceEnd)
        guard hasPlayableTime(left), hasPlayableTime(right) else { return nil }

        var updated = timeline
        updated.clips.replaceSubrange(index...index, with: [left, right])
        return updated
    }

    private static func hasPlayableTime(_ clip: StudioTimelineClip) -> Bool {
        StudioTimeMap(sourceDuration: clip.sourceEnd, edits: clip.boundedEdits).outputDuration > 0
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
