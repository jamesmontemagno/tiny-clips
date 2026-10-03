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

    init(width: Int, height: Int, duration: Double, frameRate: Double = 30) {
        self.width = width
        self.height = height
        self.duration = duration
        self.frameRate = frameRate
    }
}

// MARK: - Capture Events

enum StudioCaptureEvents {
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
        camera: StudioCaptureMediaInfo?,
        cameraStartOffset: Double,
        bubbleAnchor: StudioAnchor,
        clickOverlay: StudioClickOverlay,
        branding: Bool,
        appVersion: String
    ) -> StudioProjectCreationRequest {
        StudioProjectCreationRequest(
            name: name,
            screenWidth: screen.width,
            screenHeight: screen.height,
            screenDuration: screen.duration,
            screenFrameRate: screen.frameRate,
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
            appVersion: appVersion
        )
    }
}
