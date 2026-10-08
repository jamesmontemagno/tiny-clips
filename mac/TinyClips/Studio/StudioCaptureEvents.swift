import Foundation

// MARK: - Capture Event Inputs

struct StudioCaptureClickSample: Equatable, Sendable {
    var t: Double
    var point: CGPoint
    var button: StudioMouseButton

    init(t: Double, point: CGPoint, button: StudioMouseButton = .left) {
        self.t = t
        self.point = point
        self.button = button
    }
}

struct StudioCaptureCursorSampleInput: Equatable, Sendable {
    var t: Double
    var point: CGPoint

    init(t: Double, point: CGPoint) {
        self.t = t
        self.point = point
    }
}

struct StudioCaptureMediaInfo: Equatable, Sendable {
    var width: Int
    var height: Int
    var duration: Double
    var frameRate: Double

    /// How many sound tracks the file has.
    var audioTrackCount: Int

    init(width: Int, height: Int, duration: Double, frameRate: Double = 30, audioTrackCount: Int = 0) {
        self.width = width
        self.height = height
        self.duration = duration
        self.frameRate = frameRate
        self.audioTrackCount = audioTrackCount
    }
}

// MARK: - Capture Events

enum StudioCaptureEvents {
    /// The frame rate a project is edited and exported at.
    ///
    /// A screen recording gets a frame only when something on screen changed, so the rate read
    /// from the file is an average and can be far below the rate the recording was made at. A
    /// project with that rate would export as a slide show. The rate the recording was configured
    /// with is used instead. The file's own rate is the fallback, and never below 30.
    static func projectFrameRate(configured: Double, measured: Double) -> Double {
        if configured.isFinite, configured >= 1 {
            return min(configured, 240)
        }
        guard measured.isFinite, measured > 0 else { return 30 }
        return min(max(measured.rounded(.up), 30), 240)
    }

    /// What a project says its screen file's sound tracks hold (`sources.screen.audioTracks`).
    ///
    /// The recorder knows which sound inputs it set up and in which order. The file is what
    /// counts, though: an input that never got a sample may or may not have become a track. So
    /// the list is only written when the file has exactly as many sound tracks as the recorder
    /// set up. Otherwise the project says nothing, and every track plays as recorded.
    static func screenAudioTracks(recorded: [StudioAudioTrackKind], trackCountInFile: Int) -> [String]? {
        guard recorded.count == trackCountInFile else { return nil }
        return recorded.map(\.rawValue)
    }

    static func normalizedPoint(
        _ point: CGPoint,
        in rect: CGRect,
        dropsOutside: Bool
    ) -> CGPoint? {
        guard rect.width > 0, rect.height > 0 else { return nil }
        if dropsOutside, !rect.contains(point) {
            return nil
        }

        return CGPoint(
            x: (point.x - rect.minX) / rect.width,
            y: (point.y - rect.minY) / rect.height
        )
    }

    static func normalizedClicks(
        _ samples: [StudioCaptureClickSample],
        captureRect: CGRect
    ) -> [StudioClickEvent] {
        samples.compactMap { sample in
            guard sample.t.isFinite,
                  let point = normalizedPoint(sample.point, in: captureRect, dropsOutside: true)
            else {
                return nil
            }

            return StudioClickEvent(
                t: max(0, sample.t),
                x: point.x,
                y: point.y,
                button: sample.button
            )
        }
        .sorted { $0.t < $1.t }
    }

    static func normalizedCursorSamples(
        _ samples: [StudioCaptureCursorSampleInput],
        captureRect: CGRect,
        maxSamplesPerSecond: Double = 60
    ) -> [StudioCursorSample] {
        guard maxSamplesPerSecond > 0 else { return [] }

        let minimumInterval = 1 / maxSamplesPerSecond
        var output: [StudioCursorSample] = []
        output.reserveCapacity(samples.count)

        for sample in samples.sorted(by: { $0.t < $1.t }) {
            guard sample.t.isFinite,
                  let point = normalizedPoint(sample.point, in: captureRect, dropsOutside: false)
            else {
                continue
            }

            if let last = output.last {
                if sample.t - last.t < minimumInterval - 1e-9 {
                    continue
                }
                if last.x == point.x, last.y == point.y {
                    continue
                }
            }

            output.append(StudioCursorSample(t: max(0, sample.t), x: point.x, y: point.y))
        }

        return output
    }

    static func cameraCornerEvents(
        initialCorner: StudioAnchor,
        changes: [StudioCameraCornerEvent]
    ) -> [StudioCameraCornerEvent] {
        var output = [StudioCameraCornerEvent(t: 0, corner: initialCorner)]
        var lastCorner = initialCorner

        for event in changes.sorted(by: { $0.t < $1.t }) {
            guard event.t.isFinite, event.t > 0, event.corner != lastCorner else {
                continue
            }

            output.append(StudioCameraCornerEvent(t: event.t, corner: event.corner))
            lastCorner = event.corner
        }

        return output
    }

    /// The shortest scene that a change made while recording gets (section 9.1 of the project
    /// format). It is the shortest scene the editor makes.
    static let shortestRecordedScene = StudioEditorModel.minimumSceneDuration

    /// The scenes a new project gets from what was changed while recording (section 9.1 of the
    /// project format): a scene for each move of the camera to another corner and for each change
    /// of layout, entered by moving, after `first`, which starts at 0.
    static func scenes(
        first: StudioScene,
        corners: [StudioCameraCornerEvent],
        markers: [StudioLayoutMarker],
        duration: Double
    ) -> [StudioScene] {
        var changes: [RecordedChange] = []
        for (index, corner) in corners.enumerated() where corner.t.isFinite {
            changes.append(RecordedChange(time: corner.t, isCorner: true, index: index, corner: corner.corner, layout: .bubble))
        }
        for (index, marker) in markers.enumerated() where marker.t.isFinite {
            changes.append(RecordedChange(time: marker.t, isCorner: false, index: index, corner: .bottomRight, layout: marker.layout))
        }
        // By time. At the same time a corner comes before a marker, and two of a kind keep the
        // order of their list.
        changes.sort { a, b in
            if a.time != b.time { return a.time < b.time }
            if a.isCorner != b.isCorner { return a.isCorner }
            return a.index < b.index
        }

        var opening = first
        opening.start = 0
        var scenes = [opening]
        for change in changes {
            let last = scenes[scenes.count - 1]
            var changed = last
            if !change.isCorner {
                changed.layout = change.layout
            } else if last.bubble.anchor != change.corner {
                // The offsets are from the corner the bubble was in, so they do not go with it.
                changed.bubble.anchor = change.corner
                changed.bubble.offsetX = 0
                changed.bubble.offsetY = 0
            }

            if showsTheSame(changed, last) {
                continue
            }
            if change.isCorner, last.layout != .bubble {
                // No bubble is showing. The corner counts from when one shows again.
                scenes[scenes.count - 1] = changed
                continue
            }
            if change.time < last.start + shortestRecordedScene {
                // Too soon after the last scene started for that one to last: it takes the change.
                scenes[scenes.count - 1] = changed
                if scenes.count > 1, showsTheSame(changed, scenes[scenes.count - 2]) {
                    scenes.removeLast()
                }
                continue
            }
            if change.time > duration - shortestRecordedScene {
                continue
            }
            changed.start = change.time
            changed.transition = StudioTransition(kind: .morph)
            scenes.append(changed)
        }
        return scenes
    }

    /// Whether two scenes have the same layout and the bubble in the same place. A recording
    /// changes nothing else about a scene.
    private static func showsTheSame(_ a: StudioScene, _ b: StudioScene) -> Bool {
        a.layout == b.layout
            && a.bubble.anchor == b.bubble.anchor
            && a.bubble.offsetX == b.bubble.offsetX
            && a.bubble.offsetY == b.bubble.offsetY
    }

    private struct RecordedChange {
        var time: Double
        var isCorner: Bool
        var index: Int
        var corner: StudioAnchor
        var layout: StudioLayout
    }

    static func makeEvents(
        captureWidth: Int,
        captureHeight: Int,
        captureScale: Double,
        captureKind: StudioCaptureKind,
        clicks: [StudioClickEvent],
        cursor: [StudioCursorSample],
        cameraCorners: [StudioCameraCornerEvent]
    ) -> StudioEvents {
        StudioEvents(
            capture: StudioCaptureInfo(
                width: captureWidth,
                height: captureHeight,
                scale: captureScale,
                kind: captureKind
            ),
            clicks: clicks,
            cursor: cursor,
            cameraCorners: cameraCorners
        )
    }

    static func makeProjectCreationRequest(
        name: String,
        screen: StudioCaptureMediaInfo,
        screenAudioTracks: [String]? = nil,
        camera: StudioCaptureMediaInfo?,
        cameraStartOffset: Double,
        bubbleAnchor: StudioAnchor,
        clickOverlay: StudioClickOverlay,
        branding: Bool,
        appVersion: String,
        look: StudioLook? = nil,
        cameraCorners: [StudioCameraCornerEvent] = [],
        layoutMarkers: [StudioLayoutMarker] = []
    ) -> StudioProjectCreationRequest {
        StudioProjectCreationRequest(
            name: name,
            screenWidth: screen.width,
            screenHeight: screen.height,
            screenDuration: screen.duration,
            screenFrameRate: screen.frameRate,
            screenAudioTracks: screenAudioTracks,
            camera: camera.map {
                StudioCameraCreationInfo(
                    width: $0.width,
                    height: $0.height,
                    duration: $0.duration,
                    startOffset: cameraStartOffset
                )
            },
            bubbleAnchor: bubbleAnchor,
            clickOverlay: clickOverlay,
            branding: branding,
            appVersion: appVersion,
            look: look,
            cameraCorners: cameraCorners,
            layoutMarkers: layoutMarkers
        )
    }
}
