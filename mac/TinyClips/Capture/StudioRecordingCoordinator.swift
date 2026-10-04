import AppKit
import AVFoundation
import CoreMedia

// MARK: - Studio Recording Coordinator

/// Owns the project folder and pointer samples for one Studio recording. `CaptureManager` points the
/// recorders at `screenURL` and `cameraURL`, then calls `completeRecording` once both are finalized.
@MainActor
final class StudioRecordingCoordinator {
    let paths: StudioProjectPaths

    private static let cursorSampleInterval: TimeInterval = 1.0 / 30.0

    private let store: StudioProjectStore
    private var captureRegion: CaptureRegion
    private let captureKind: StudioCaptureKind
    private let initialCameraCorner: StudioAnchor
    private let clickVisualsEnabled: Bool
    private let configuredFrameRate: Double
    private var cursorTimer: Timer?
    private var cursorSamples: [StudioCaptureCursorSampleInput] = []
    private var completedProjectID: String?

    /// - Parameter frameRate: The frame rate the screen recording is made at.
    init(
        store: StudioProjectStore = .shared,
        captureRegion: CaptureRegion,
        captureKind: StudioCaptureKind,
        initialCameraCorner: StudioAnchor,
        clickVisualsEnabled: Bool,
        frameRate: Int
    ) throws {
        self.store = store
        self.captureRegion = captureRegion
        self.captureKind = captureKind
        self.initialCameraCorner = initialCameraCorner
        self.clickVisualsEnabled = clickVisualsEnabled
        self.configuredFrameRate = Double(frameRate)
        self.paths = try store.beginRecording()
        StudioMaintenance.recordingDidBegin(projectID: paths.id)
    }

    var screenURL: URL {
        paths.screenURL
    }

    var cameraURL: URL? {
        paths.cameraURL
    }

    /// Window captures move, so pointer positions cannot be mapped onto them.
    var recordsPointerEvents: Bool {
        captureKind != .window
    }

    /// Replaces the provisional region with the geometry the stream was actually configured from.
    func updateCaptureRegion(_ region: CaptureRegion) {
        captureRegion = region
    }

    /// - Parameters:
    ///   - timeProvider: The recording timeline time, or nil before the first frame is written.
    ///   - isPaused: Whether the recording is paused. Samples are skipped while it is.
    func startCursorSampling(
        timeProvider: @escaping () -> CMTime?,
        isPaused: @escaping () -> Bool
    ) {
        guard recordsPointerEvents else { return }
        stopCursorSampling()
        cursorSamples = []

        let timer = Timer(timeInterval: Self.cursorSampleInterval, repeats: true) { [weak self] timer in
            MainActor.assumeIsolated {
                guard let self else {
                    timer.invalidate()
                    return
                }
                self.sampleCursor(timeProvider: timeProvider, isPaused: isPaused)
            }
        }
        // Common modes keep sampling while Tiny Clips itself is tracking a drag or a menu.
        RunLoop.main.add(timer, forMode: .common)
        cursorTimer = timer
    }

    func stopCursorSampling() {
        cursorTimer?.invalidate()
        cursorTimer = nil
    }

    func completeRecording(
        screenURL: URL,
        cameraURL: URL?,
        screenFirstSampleTime: CMTime?,
        cameraFirstSampleTime: CMTime?,
        recordedAudioTracks: [StudioAudioTrackKind],
        mouseClicks: [MouseClickEvent],
        cameraCornerChanges: [BrandingOverlayProcessor.WebcamPositionEvent],
        clickOverlayStyle: MouseClickOverlayStyle,
        branding: Bool,
        appVersion: String,
        look: StudioLook?
    ) async throws -> String {
        stopCursorSampling()

        var screenInfo = try await Self.mediaInfo(for: screenURL)
        screenInfo.frameRate = StudioCaptureEvents.projectFrameRate(
            configured: configuredFrameRate,
            measured: screenInfo.frameRate
        )
        var cameraInfo: StudioCaptureMediaInfo?
        if let cameraURL, FileManager.default.fileExists(atPath: cameraURL.path) {
            cameraInfo = try? await Self.mediaInfo(for: cameraURL)
            if cameraInfo == nil {
                // The project will not reference an unreadable camera track, so do not keep it.
                try? FileManager.default.removeItem(at: cameraURL)
            }
        }

        let cameraStartOffset = Self.startOffset(
            screenFirstSampleTime: screenFirstSampleTime,
            cameraFirstSampleTime: cameraFirstSampleTime
        )
        let clickSamples = recordsPointerEvents ? studioClickSamples(from: mouseClicks) : []
        let captureRect = CGRect(origin: .zero, size: captureRegion.sourceRect.size)
        let clicks = StudioCaptureEvents.normalizedClicks(clickSamples, captureRect: captureRect)
        let cursor = StudioCaptureEvents.normalizedCursorSamples(cursorSamples, captureRect: captureRect)
        let cameraCorners = StudioCaptureEvents.cameraCornerEvents(
            initialCorner: initialCameraCorner,
            changes: studioCameraCornerEvents(from: cameraCornerChanges)
        )
        let events = StudioCaptureEvents.makeEvents(
            captureWidth: screenInfo.width,
            captureHeight: screenInfo.height,
            captureScale: Double(captureRegion.scaleFactor),
            captureKind: captureKind,
            clicks: clicks,
            cursor: cursor,
            cameraCorners: cameraCorners
        )
        let request = StudioCaptureEvents.makeProjectCreationRequest(
            name: Self.recordingName(),
            screen: screenInfo,
            screenAudioTracks: StudioCaptureEvents.screenAudioTracks(
                recorded: recordedAudioTracks,
                trackCountInFile: screenInfo.audioTrackCount
            ),
            camera: cameraInfo,
            cameraStartOffset: cameraStartOffset,
            bubbleAnchor: initialCameraCorner,
            clickOverlay: Self.clickOverlay(from: clickOverlayStyle, enabled: clickVisualsEnabled),
            branding: branding,
            appVersion: appVersion,
            look: look,
            cameraCorners: cameraCorners
        )

        _ = try store.completeRecording(id: paths.id, request: request)
        completedProjectID = paths.id
        cursorSamples = []
        // A finished project is a draft, which cleanup never removes by itself.
        StudioMaintenance.recordingDidEnd(projectID: paths.id)

        do {
            try store.saveEvents(events, id: paths.id)
        } catch {
            // Events are optional: the project opens without them, it only loses click and cursor data.
            NSLog("TinyClips Studio could not save events for project \(paths.id): \(error.localizedDescription)")
        }
        return paths.id
    }

    /// Deletes the project folder unless the recording was completed. Safe to call more than once.
    func deleteUnfinishedProject() {
        stopCursorSampling()
        StudioMaintenance.recordingDidEnd(projectID: paths.id)
        guard completedProjectID == nil else { return }
        try? store.delete(id: paths.id)
    }

    /// The webcam corner settings are compared case-insensitively everywhere else in the app.
    static func studioAnchor(from corner: String) -> StudioAnchor {
        switch corner.lowercased() {
        case "topleft":
            return .topLeft
        case "topright":
            return .topRight
        case "bottomleft":
            return .bottomLeft
        default:
            return .bottomRight
        }
    }

    // MARK: - Private

    private func sampleCursor(timeProvider: () -> CMTime?, isPaused: () -> Bool) {
        guard !isPaused(),
              let time = timeProvider()?.seconds,
              time.isFinite,
              let point = sourcePoint(forGlobalPoint: NSEvent.mouseLocation)
        else {
            return
        }
        // A resting cursor adds nothing: samples are steps, so the last one still applies.
        if let last = cursorSamples.last, last.point == point {
            return
        }
        cursorSamples.append(StudioCaptureCursorSampleInput(t: max(0, time), point: point))
    }

    private func studioClickSamples(from events: [MouseClickEvent]) -> [StudioCaptureClickSample] {
        events.compactMap { event in
            guard let point = sourcePoint(forGlobalPoint: event.globalLocation) else {
                return nil
            }
            return StudioCaptureClickSample(t: event.timeOffset, point: point, button: event.button)
        }
    }

    private func studioCameraCornerEvents(from events: [BrandingOverlayProcessor.WebcamPositionEvent]) -> [StudioCameraCornerEvent] {
        events.compactMap { event in
            let seconds = event.time.seconds
            guard seconds.isFinite else { return nil }
            return StudioCameraCornerEvent(t: max(0, seconds), corner: Self.studioAnchor(from: event.corner))
        }
    }

    /// Converts a global AppKit point (bottom-left origin) to points inside the captured rectangle
    /// (top-left origin). The result may lie outside the rectangle.
    private func sourcePoint(forGlobalPoint globalPoint: CGPoint) -> CGPoint? {
        guard let screen = NSScreen.screens.first(where: { screen in
            guard let displayID = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID else {
                return false
            }
            return displayID == captureRegion.displayID
        }) else {
            return nil
        }

        let localPoint = CGPoint(
            x: globalPoint.x - screen.frame.minX,
            y: screen.frame.maxY - globalPoint.y
        )
        return CGPoint(
            x: localPoint.x - captureRegion.sourceRect.minX,
            y: localPoint.y - captureRegion.sourceRect.minY
        )
    }

    private static func mediaInfo(for url: URL) async throws -> StudioCaptureMediaInfo {
        let asset = AVURLAsset(url: url)
        guard let videoTrack = try await asset.loadTracks(withMediaType: .video).first else {
            throw CaptureError.noFrames
        }

        let naturalSize = try await videoTrack.load(.naturalSize)
        let preferredTransform = try await videoTrack.load(.preferredTransform)
        let transformedSize = naturalSize.applying(preferredTransform)
        let duration = try await asset.load(.duration)
        let nominalFrameRate = try await videoTrack.load(.nominalFrameRate)
        // Sound tracks that cannot be listed count as none. The project then does not say what
        // its sound tracks hold, and they play as recorded.
        let audioTrackCount = (try? await asset.loadTracks(withMediaType: .audio))?.count ?? 0

        return StudioCaptureMediaInfo(
            width: max(1, Int(abs(transformedSize.width).rounded())),
            height: max(1, Int(abs(transformedSize.height).rounded())),
            duration: max(0, duration.seconds.isFinite ? duration.seconds : 0),
            frameRate: nominalFrameRate > 0 ? Double(nominalFrameRate) : 30,
            audioTrackCount: audioTrackCount
        )
    }

    private static func startOffset(
        screenFirstSampleTime: CMTime?,
        cameraFirstSampleTime: CMTime?
    ) -> Double {
        guard let screenFirstSampleTime,
              let cameraFirstSampleTime,
              screenFirstSampleTime.isNumeric,
              cameraFirstSampleTime.isNumeric
        else {
            return 0
        }

        let offset = CMTimeSubtract(cameraFirstSampleTime, screenFirstSampleTime).seconds
        return offset.isFinite ? offset : 0
    }

    private static func recordingName(date: Date = Date()) -> String {
        let filename = SaveService.shared.generatedFileName(
            for: .video,
            fileExtension: CaptureType.video.fileExtension,
            date: date
        )
        return (filename as NSString).deletingPathExtension
    }

    private static func clickOverlay(from style: MouseClickOverlayStyle, enabled: Bool) -> StudioClickOverlay {
        StudioClickOverlay(
            enabled: enabled,
            color: style.colorHex,
            size: Double(style.size),
            strokeWidth: Double(style.strokeWidth),
            opacity: Double(style.opacity),
            duration: style.duration
        )
    }
}