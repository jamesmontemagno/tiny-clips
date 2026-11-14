import Foundation
import Combine
import SwiftUI
import AppKit
import ScreenCaptureKit
import UniformTypeIdentifiers

// MARK: - Capture Region

struct CaptureRegion: Sendable {
    let sourceRect: CGRect
    let displayID: CGDirectDisplayID
    let scaleFactor: CGFloat

    static func fullScreen(for screen: NSScreen) -> CaptureRegion? {
        guard let displayID = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID else {
            return nil
        }

        return CaptureRegion(
            sourceRect: CGRect(x: 0, y: 0, width: screen.frame.width, height: screen.frame.height),
            displayID: displayID,
            scaleFactor: screen.backingScaleFactor
        ).pixelAligned()
    }

    var pixelWidth: Int {
        max(1, Int((sourceRect.width * scaleFactor).rounded()))
    }

    var pixelHeight: Int {
        max(1, Int((sourceRect.height * scaleFactor).rounded()))
    }

    /// Returns a copy whose `sourceRect` sits on whole device-pixel boundaries. Idempotent.
    func pixelAligned() -> CaptureRegion {
        CaptureRegion(
            sourceRect: CaptureCoordinateMath.pixelAlignedRect(sourceRect, scaleFactor: scaleFactor),
            displayID: displayID,
            scaleFactor: scaleFactor
        )
    }

    func withScaleFactor(_ newScaleFactor: CGFloat) -> CaptureRegion {
        guard newScaleFactor > 0, newScaleFactor != scaleFactor else { return self }
        return CaptureRegion(sourceRect: sourceRect, displayID: displayID, scaleFactor: newScaleFactor)
    }

    /// Adopts the filter's point-to-pixel ratio, then snaps to that pixel grid.
    ///
    /// `NSScreen.backingScaleFactor` describes the render framebuffer, while `pointPixelScale` is what
    /// ScreenCaptureKit will actually hand back. They agree on every display mode measured so far, so
    /// this is defensive rather than a fix for a known case.
    func resolvingPixelScale(from filter: SCContentFilter) -> CaptureRegion {
        withScaleFactor(CGFloat(filter.pointPixelScale)).pixelAligned()
    }

    func makeStreamConfig() -> SCStreamConfiguration {
        let config = SCStreamConfiguration()
        config.sourceRect = sourceRect
        config.width = pixelWidth
        config.height = pixelHeight
        config.scalesToFit = true
        config.showsCursor = true
        return config
    }

    func makeFilter(
        excludingApplicationBundleIdentifiers bundleIdentifiers: Set<String> = [],
        alwaysExcluding windows: [SCWindow] = []
    ) async throws -> SCContentFilter {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        guard let display = content.displays.first(where: { $0.displayID == self.displayID }) else {
            throw CaptureError.displayNotFound
        }
        let includeTinyClips = CaptureSettings.shared.includeTinyClipsInCapture
        let excludedApps: [SCRunningApplication] = content.applications.filter {
            (!includeTinyClips && $0.bundleIdentifier == Bundle.main.bundleIdentifier)
                || bundleIdentifiers.contains($0.bundleIdentifier)
        }
        if excludedApps.isEmpty {
            return SCContentFilter(display: display, excludingWindows: windows)
        }
        return SCContentFilter(display: display, excludingApplications: excludedApps, exceptingWindows: [])
    }
}

struct CaptureTarget {
    let region: CaptureRegion
    let studioCaptureKind: StudioCaptureKind

    init(region: CaptureRegion, studioCaptureKind: StudioCaptureKind = .region) {
        self.region = region
        self.studioCaptureKind = studioCaptureKind
    }

    func prepare(alwaysExcluding windows: [SCWindow] = []) async throws -> PreparedCaptureTarget {
        let filter = try await region.makeFilter(alwaysExcluding: windows)
        let region = self.region.resolvingPixelScale(from: filter)
        return PreparedCaptureTarget(
            filter: filter,
            config: region.makeStreamConfig(),
            region: region,
            pixelWidth: region.pixelWidth,
            pixelHeight: region.pixelHeight
        )
    }
}

struct PreparedCaptureTarget {
    let filter: SCContentFilter
    let config: SCStreamConfiguration
    /// Geometry the stream is actually sized from; downstream coordinate mapping must use this, not
    /// the provisional `CaptureTarget.region`.
    let region: CaptureRegion
    let pixelWidth: Int
    let pixelHeight: Int
}

// MARK: - Capture Type

enum CaptureType: String {
    case screenshot, video, gif

    var fileExtension: String {
        switch self {
        case .screenshot: return CaptureSettings.shared.imageFormat.rawValue
        case .video: return "mp4"
        case .gif: return "gif"
        }
    }

    var label: String {
        switch self {
        case .screenshot: return "Screenshot"
        case .video: return "Video"
        case .gif: return "GIF"
        }
    }
}

// MARK: - Capture Picker Intent

/// The picker experience, not a stored capture type or output format.
enum CapturePickerIntent: Equatable {
    case screenshot, video, gif, studio

    init(captureType: CaptureType, forStudio: Bool = false) {
        switch captureType {
        case .screenshot: self = .screenshot
        case .video: self = forStudio ? .studio : .video
        case .gif: self = .gif
        }
    }

    var captureType: CaptureType {
        switch self {
        case .screenshot: return .screenshot
        case .video, .studio: return .video
        case .gif: return .gif
        }
    }

    var label: String {
        switch self {
        case .studio: return "Studio"
        case .screenshot, .video, .gif: return captureType.label
        }
    }
}

// MARK: - Capture Error

enum CaptureError: LocalizedError {
    case displayNotFound
    case regionCropFailed
    case saveFailed
    case noFrames
    case permissionDenied
    case microphoneUnavailable
    case microphoneConnectionFailed
    case microphoneReadFailed
    case webcamPermissionDenied
    case webcamUnavailable
    case webcamConnectionFailed
    case webcamReadFailed

    var errorDescription: String? {
        switch self {
        case .displayNotFound: return "Could not find the selected display."
        case .regionCropFailed: return "Could not crop the selected region from the capture."
        case .saveFailed: return "Failed to save the capture."
        case .noFrames: return "No frames were captured."
        case .permissionDenied: return "Screen recording permission is required."
        case .microphoneUnavailable: return "The selected microphone is unavailable. Choose another input device in Settings."
        case .microphoneConnectionFailed: return "Could not connect to the selected microphone."
        case .microphoneReadFailed: return "Could not read audio from the selected microphone."
        case .webcamPermissionDenied: return "Camera permission is required to record webcam video."
        case .webcamUnavailable: return "The selected webcam is unavailable. Choose another camera in Settings."
        case .webcamConnectionFailed: return "Could not connect to the selected webcam."
        case .webcamReadFailed: return "Could not read video from the selected webcam."
        }
    }
}

// MARK: - Image Format

enum ImageFormat: String, CaseIterable {
    case png = "png"
    case jpeg = "jpg"
    case webp = "webp"

    var label: String {
        switch self {
        case .png: return "PNG"
        case .jpeg: return "JPEG"
        case .webp: return "WebP"
        }
    }

    var utType: UTType {
        switch self {
        case .png: return .png
        case .jpeg: return .jpeg
        case .webp: return .webP
        }
    }
}

enum VideoCodec: String, CaseIterable {
    case h264 = "h264"
    case hevc = "hevc"

    var label: String {
        switch self {
        case .h264: return "H.264"
        case .hevc: return "H.265 / HEVC"
        }
    }
}

enum MultiMonitorCaptureMode: String, CaseIterable {
    case askEveryTime
    case displayUnderCursor
    case mainDisplay

    var label: String {
        switch self {
        case .askEveryTime: return "Ask every time"
        case .displayUnderCursor: return "Display under cursor"
        case .mainDisplay: return "Main display"
        }
    }
}

enum TeleprompterDisplaySize: String, CaseIterable {
    case small
    case medium
    case large

    var label: String {
        rawValue.capitalized
    }

    var fontSize: CGFloat {
        switch self {
        case .small: return 20
        case .medium: return 24
        case .large: return 30
        }
    }

    var panelHeight: CGFloat {
        switch self {
        case .small: return 120
        case .medium: return 140
        case .large: return 220
        }
    }

    var viewportHeight: CGFloat {
        panelHeight - 24
    }
}

// MARK: - Settings

struct MouseClickOverlayStyle: Sendable {
    let colorHex: String
    let size: CGFloat
    let strokeWidth: CGFloat
    let opacity: CGFloat
    let duration: TimeInterval
}

extension MouseClickOverlayStyle {
    var color: NSColor {
        NSColor(hexRGBString: colorHex) ?? .white
    }
}

private enum CaptureSettingsDefaultsKey {
    static let videoCodec = "videoCodec"
    static let confirmEditorEscape = "confirmEditorEscape"
    static let studioExportResolution = "studioExportResolution"
    static let studioExportQuality = "studioExportQuality"
}

extension NSColor {
    convenience init?(hexRGBString: String) {
        var value = hexRGBString.trimmingCharacters(in: .whitespacesAndNewlines)
        if value.hasPrefix("#") {
            value.removeFirst()
        }

        guard value.count == 6, let rgb = Int(value, radix: 16) else {
            return nil
        }

        let red = CGFloat((rgb >> 16) & 0xFF) / 255.0
        let green = CGFloat((rgb >> 8) & 0xFF) / 255.0
        let blue = CGFloat(rgb & 0xFF) / 255.0
        self.init(calibratedRed: red, green: green, blue: blue, alpha: 1.0)
    }

    var hexRGBString: String {
        guard let resolved = usingColorSpace(.sRGB) ?? usingColorSpace(.deviceRGB) else {
            return "#FFFFFF"
        }

        return String(
            format: "#%02X%02X%02X",
            Int((resolved.redComponent * 255.0).rounded()),
            Int((resolved.greenComponent * 255.0).rounded()),
            Int((resolved.blueComponent * 255.0).rounded())
        )
    }
}

/// Unlike AppStorage in a long-lived ObservableObject, this reads through to the store even
/// after a reset or a write from another view. SwiftUI views can still observe the same keys.
@propertyWrapper
struct CapturePreference<Value> {
    let key: String
    let fallback: Value
    private let decode: (Any?) -> Value?
    private let encode: (Value) -> Any

    init(wrappedValue: Value, _ key: String) {
        self.key = key
        fallback = wrappedValue
        decode = { $0 as? Value }
        encode = { $0 }
    }

    init(wrappedValue: Value, _ key: String) where Value: RawRepresentable, Value.RawValue == String {
        self.key = key
        fallback = wrappedValue
        decode = { ($0 as? String).flatMap(Value.init(rawValue:)) }
        encode = { $0.rawValue }
    }

    @available(*, unavailable, message: "Use only on CaptureSettings")
    var wrappedValue: Value {
        get { fatalError() }
        set { fatalError() }
    }

    static subscript(
        _enclosingInstance settings: CaptureSettings,
        wrapped wrappedKeyPath: ReferenceWritableKeyPath<CaptureSettings, Value>,
        storage storageKeyPath: ReferenceWritableKeyPath<CaptureSettings, Self>
    ) -> Value {
        get {
            let preference = settings[keyPath: storageKeyPath]
            return preference.decode(settings.defaults.object(forKey: preference.key)) ?? preference.fallback
        }
        set {
            let preference = settings[keyPath: storageKeyPath]
            settings.objectWillChange.send()
            settings.defaults.set(preference.encode(newValue), forKey: preference.key)
        }
    }
}

class CaptureSettings: ObservableObject {
    let objectWillChange = ObservableObjectPublisher()
    static let shared = CaptureSettings()
    // Bump when release highlights change, independently of the app version.
    static let whatsNewContentRevision = 3
    static let lastSeenWhatsNewRevisionKey = "lastSeenWhatsNewRevision"

    static func shouldShowWhatsNew(in defaults: UserDefaults = .standard) -> Bool {
        defaults.bool(forKey: "hasCompletedOnboarding")
            && defaults.integer(forKey: lastSeenWhatsNewRevisionKey) < whatsNewContentRevision
    }

    static func markWhatsNewSeen(in defaults: UserDefaults = .standard) {
        defaults.set(whatsNewContentRevision, forKey: lastSeenWhatsNewRevisionKey)
    }

    static let videoCodecKey = CaptureSettingsDefaultsKey.videoCodec
    static let confirmEditorEscapeKey = CaptureSettingsDefaultsKey.confirmEditorEscape
    static let studioExportResolutionKey = CaptureSettingsDefaultsKey.studioExportResolution
    static let screenshotEditorNativeSizeKey = "screenshotEditorNativeSize"
    /// The editor tool a new screenshot editor starts with, as an `EditTool.settingID`.
    static let screenshotEditorDefaultToolKey = "screenshotEditorDefaultTool"
    /// Where the Captions panel keeps the language captions are generated in, as a BCP 47 tag, or
    /// empty for the system's.
    static let studioCaptionLanguageKey = "studioCaptionLanguage"
    static let audioOffsetRangeMs: ClosedRange<Int> = -500...500
    fileprivate let defaults: UserDefaults

    @CapturePreference("saveDirectory") var saveDirectory: String = NSHomeDirectory() + "/Desktop"
    @CapturePreference("screenshotSaveDirectory") var screenshotSaveDirectory: String = defaultSaveDirectoryURL(for: .screenshot).path
    @CapturePreference("videoSaveDirectory") var videoSaveDirectory: String = defaultSaveDirectoryURL(for: .video).path
    @CapturePreference("gifSaveDirectory") var gifSaveDirectory: String = defaultSaveDirectoryURL(for: .gif).path
    @CapturePreference("videoGifSaveDirectory") var videoGifSaveDirectory: String = ""
    @CapturePreference("useDefaultSaveDirectories") var useDefaultSaveDirectories: Bool = true
#if APPSTORE
    @CapturePreference("saveDirectoryBookmark") var saveDirectoryBookmark: Data = Data()
    @CapturePreference("saveDirectoryDisplayPath") var saveDirectoryDisplayPath: String = ""
    @CapturePreference("screenshotSaveDirectoryBookmark") var screenshotSaveDirectoryBookmark: Data = Data()
    @CapturePreference("screenshotSaveDirectoryDisplayPath") var screenshotSaveDirectoryDisplayPath: String = defaultSaveDirectoryURL(for: .screenshot).path
    @CapturePreference("videoSaveDirectoryBookmark") var videoSaveDirectoryBookmark: Data = Data()
    @CapturePreference("videoSaveDirectoryDisplayPath") var videoSaveDirectoryDisplayPath: String = defaultSaveDirectoryURL(for: .video).path
    @CapturePreference("gifSaveDirectoryBookmark") var gifSaveDirectoryBookmark: Data = Data()
    @CapturePreference("gifSaveDirectoryDisplayPath") var gifSaveDirectoryDisplayPath: String = defaultSaveDirectoryURL(for: .gif).path
    @CapturePreference("videoGifSaveDirectoryBookmark") var videoGifSaveDirectoryBookmark: Data = Data()
    @CapturePreference("videoGifSaveDirectoryDisplayPath") var videoGifSaveDirectoryDisplayPath: String = ""
#endif
    @CapturePreference("copyScreenshotToClipboard") var copyScreenshotToClipboard: Bool = true
    @CapturePreference("copyVideoToClipboard") var copyVideoToClipboard: Bool = false
    @CapturePreference("copyGifToClipboard") var copyGifToClipboard: Bool = false
    @CapturePreference("showInFinder") var showInFinder: Bool = false
    @CapturePreference("showSaveNotifications") var showSaveNotifications: Bool = false
    @CapturePreference("showInDock") var showInDock: Bool = false
    @CapturePreference("fileNameTemplate") var fileNameTemplate: String = "TinyClips {date} at {time}"
    @CapturePreference("uploadcareEnabled") var uploadcareEnabled: Bool = false
    @CapturePreference("clipsManagerShowAutoTags") var clipsManagerShowAutoTags: Bool = true
    @CapturePreference("clipsManagerShowNotesPreview") var clipsManagerShowNotesPreview: Bool = true
    @CapturePreference("clipsManagerShowQuickActions") var clipsManagerShowQuickActions: Bool = true
    @CapturePreference("clipsManagerShowUploadStatus") var clipsManagerShowUploadStatus: Bool = true
    @CapturePreference("clipsManagerConfirmDelete") var clipsManagerConfirmDelete: Bool = true
    @CapturePreference("clipsManagerCompactListDensity") var clipsManagerCompactListDensity: Bool = false
    @CapturePreference("clipsManagerSelectionRowTapSelects") var clipsManagerSelectionRowTapSelects: Bool = true
    @CapturePreference("clipsManagerIgnoreNonTinyClipsFiles") var clipsManagerIgnoreNonTinyClipsFiles: Bool = false
    @CapturePreference("clipsManagerRememberLastState") var clipsManagerRememberLastState: Bool = true
    @CapturePreference("clipsManagerDefaultViewMode") var clipsManagerDefaultViewMode: String = "grid"
    @CapturePreference("clipsManagerDefaultSortOption") var clipsManagerDefaultSortOption: String = "Newest First"
    @CapturePreference("clipsManagerDefaultFilterType") var clipsManagerDefaultFilterType: String = "All"
    @CapturePreference("clipsManagerDefaultDateFilter") var clipsManagerDefaultDateFilter: String = "Any Date"
    @CapturePreference("clipsManagerAutoRefreshSeconds") var clipsManagerAutoRefreshSeconds: Int = 0
    @CapturePreference("clipsManagerArchiveOldClips") var clipsManagerArchiveOldClips: Bool = false
    @CapturePreference("clipsManagerArchiveAfterDays") var clipsManagerArchiveAfterDays: Int = 30
    @CapturePreference("clipsManagerAutoUploadAfterSave") var clipsManagerAutoUploadAfterSave: Bool = false
    @CapturePreference("clipsManagerAutoCopyUploadLink") var clipsManagerAutoCopyUploadLink: Bool = false
    @CapturePreference("clipsManagerShowInspector") var clipsManagerShowInspector: Bool = true
    @CapturePreference("recentCapturesLimit") private var storedRecentCapturesLimit: Int = 10
    var recentCapturesLimit: Int {
        get { min(max(storedRecentCapturesLimit, 5), 30) }
        set { storedRecentCapturesLimit = min(max(newValue, 5), 30) }
    }
    @CapturePreference("gifFrameRate") var gifFrameRate: Double = 10
    @CapturePreference("gifMaxWidth") var gifMaxWidth: Int = 640
    @CapturePreference("videoFrameRate") var videoFrameRate: Int = 30
    @CapturePreference(CaptureSettingsDefaultsKey.videoCodec) var videoCodec: VideoCodec = .h264
    @CapturePreference("showMouseClickVisualsInVideo") var showMouseClickVisualsInVideo: Bool = false
    @CapturePreference("showMouseClickVisualsInGif") var showMouseClickVisualsInGif: Bool = false
    @CapturePreference("gifMouseClicksUseVideoSettings") var gifMouseClicksUseVideoSettings: Bool = false
    @CapturePreference("videoMouseClickColorHex") var videoMouseClickColorHex: String = "#0A84FF"
    @CapturePreference("videoMouseClickSize") var videoMouseClickSize: Double = 40
    @CapturePreference("videoMouseClickStrokeWidth") var videoMouseClickStrokeWidth: Double = 3
    @CapturePreference("videoMouseClickOpacity") var videoMouseClickOpacity: Double = 0.85
    @CapturePreference("videoMouseClickDuration") var videoMouseClickDuration: Double = 0.45
    @CapturePreference("gifMouseClickColorHex") var gifMouseClickColorHex: String = "#0A84FF"
    @CapturePreference("gifMouseClickSize") var gifMouseClickSize: Double = 40
    @CapturePreference("gifMouseClickStrokeWidth") var gifMouseClickStrokeWidth: Double = 3
    @CapturePreference("gifMouseClickOpacity") var gifMouseClickOpacity: Double = 0.85
    @CapturePreference("gifMouseClickDuration") var gifMouseClickDuration: Double = 0.45
    @CapturePreference("showTrimmer") var showTrimmer: Bool = true
    @CapturePreference("studioDefaultLook") private var storedStudioDefaultLook: String = ""
    /// The look new Studio recordings start with, or nil for the built-in one.
    var studioDefaultLook: StudioLook? {
        get { StudioLook(settingsText: storedStudioDefaultLook) }
        set { storedStudioDefaultLook = newValue?.settingsText() ?? "" }
    }
    /// Days an exported Studio project is kept after it was last opened. Zero keeps it until deleted.
    @CapturePreference("studioSourceRetentionDays") var studioSourceRetentionDays: Int = 30
    /// The most disk space the Studio projects that cleanup may remove can use before the ones
    /// opened longest ago are removed. Drafts and kept projects are not counted. Zero is no limit.
    @CapturePreference("studioStorageCapGigabytes") var studioStorageCapGigabytes: Int = 10
    /// Where Save Project and Save Project As start, or empty for Studio inside the video folder.
    @CapturePreference(StudioProjectSaveLocation.settingsKey) var studioProjectFolder: String = ""
    @CapturePreference("studioRevealAfterFirstSave") var studioRevealAfterFirstSave = false
    @CapturePreference(CaptureSettingsDefaultsKey.studioExportResolution) var studioExportResolution: String = StudioExportChoice.default.resolution.rawValue
    @CapturePreference(CaptureSettingsDefaultsKey.studioExportQuality) var studioExportQuality: String = StudioExportChoice.default.quality.rawValue

    /// What the Export popover offers first: the last choice made there.
    var studioExportChoice: StudioExportChoice {
        get {
            StudioExportChoice(
                resolution: StudioExportResolution(stored: studioExportResolution),
                quality: StudioExportQuality(stored: studioExportQuality)
            )
        }
        set {
            studioExportResolution = newValue.resolution.rawValue
            studioExportQuality = newValue.quality.rawValue
        }
    }
    var studioCleanupOptions: StudioCleanupOptions {
        StudioCleanupOptions(retentionDays: studioSourceRetentionDays, sizeCapGigabytes: studioStorageCapGigabytes)
    }
    @CapturePreference("recordAudio") var recordAudio: Bool = false
    @CapturePreference("recordMicrophone") var recordMicrophone: Bool = false
    @CapturePreference("audioOffsetMs") private var storedAudioOffsetMs: Int = 0
    var audioOffsetMs: Int {
        get { Self.clampedAudioOffsetMs(storedAudioOffsetMs) }
        set { storedAudioOffsetMs = Self.clampedAudioOffsetMs(newValue) }
    }
    @CapturePreference("microphoneLimiterEnabled") var microphoneLimiterEnabled: Bool = true
    @CapturePreference("windNoiseRemovalEnabled") var windNoiseRemovalEnabled: Bool = false
    @CapturePreference("selectedMicrophoneID") var selectedMicrophoneID: String = ""
    @CapturePreference("webcamEnabled") var webcamEnabled: Bool = false
    @CapturePreference("selectedWebcamID") var selectedWebcamID: String = ""
    @CapturePreference("webcamShape") var webcamShape: String = "circle"
    @CapturePreference("webcamSize") var webcamSize: String = "medium"
    @CapturePreference("webcamCorner") var webcamCorner: String = "bottomRight"
    // Negative values indicate no explicit corner radius/factor is set.
    @CapturePreference("webcamCornerRadius") var webcamCornerRadius: Double = -1
    @CapturePreference("showScreenshotEditor") var showScreenshotEditor: Bool = true
    @CapturePreference("screenshotEditorNativeSize") var screenshotEditorNativeSize: Bool = false
    @CapturePreference("screenshotEditorDefaultTool") var screenshotEditorDefaultTool: String = EditTool.background.settingID
    @CapturePreference(CaptureSettingsDefaultsKey.confirmEditorEscape) var confirmEditorEscape: Bool = true
    @CapturePreference("showGifTrimmer") var showGifTrimmer: Bool = true
    @CapturePreference("saveImmediatelyScreenshot") var saveImmediatelyScreenshot: Bool = true
    @CapturePreference("saveImmediatelyVideo") var saveImmediatelyVideo: Bool = true
    @CapturePreference("saveImmediatelyGif") var saveImmediatelyGif: Bool = true
    @CapturePreference("showScreenshotCapturePicker") var showScreenshotCapturePicker: Bool = true {
        didSet {
            if !showScreenshotCapturePicker {
                showScreenshotCapturePickerAfterCapture = false
            }
        }
    }
    @CapturePreference("showScreenshotCapturePickerAfterCapture") var showScreenshotCapturePickerAfterCapture: Bool = false {
        didSet {
            if showScreenshotCapturePickerAfterCapture && !showScreenshotCapturePicker {
                showScreenshotCapturePickerAfterCapture = false
            }
        }
    }
    @CapturePreference("showVideoCapturePicker") var showVideoCapturePicker: Bool = true {
        didSet {
            if !showVideoCapturePicker {
                showVideoCapturePickerAfterCapture = false
            }
        }
    }
    @CapturePreference("showVideoCapturePickerAfterCapture") var showVideoCapturePickerAfterCapture: Bool = false {
        didSet {
            if showVideoCapturePickerAfterCapture && !showVideoCapturePicker {
                showVideoCapturePickerAfterCapture = false
            }
        }
    }
    @CapturePreference("showGifCapturePicker") var showGifCapturePicker: Bool = true {
        didSet {
            if !showGifCapturePicker {
                showGifCapturePickerAfterCapture = false
            }
        }
    }
    @CapturePreference("showGifCapturePickerAfterCapture") var showGifCapturePickerAfterCapture: Bool = false {
        didSet {
            if showGifCapturePickerAfterCapture && !showGifCapturePicker {
                showGifCapturePickerAfterCapture = false
            }
        }
    }
    @CapturePreference("screenshotFormat") var screenshotFormat: String = ImageFormat.jpeg.rawValue
    @CapturePreference("screenshotScale") var screenshotScale: Int = 100
    @CapturePreference("excludeClickLightFromScreenshots") var excludeClickLightFromScreenshots: Bool = false
    var screenshotExcludedApplicationBundleIdentifiers: Set<String> {
        excludeClickLightFromScreenshots ? ["com.aurorascharff.ClickLight"] : []
    }
    @CapturePreference("jpegQuality") var jpegQuality: Double = 0.85
    @CapturePreference("videoCountdownEnabled") var videoCountdownEnabled: Bool = true
    @CapturePreference("videoCountdownDuration") var videoCountdownDuration: Int = 3
    @CapturePreference("videoRecordingTimeLimitMinutes") var videoRecordingTimeLimitMinutes: Int = 0
    @CapturePreference("gifCountdownEnabled") var gifCountdownEnabled: Bool = true
    @CapturePreference("gifCountdownDuration") var gifCountdownDuration: Int = 3

    func setRecordingCountdown(_ enabled: Bool) {
        videoCountdownEnabled = enabled
        gifCountdownEnabled = enabled
    }
    @CapturePreference("screenshotCountdownEnabled") var screenshotCountdownEnabled: Bool = false
    @CapturePreference("screenshotCountdownDuration") var screenshotCountdownDuration: Int = 3
    @CapturePreference("hasCompletedOnboarding") var hasCompletedOnboarding: Bool = false
    @CapturePreference("multiMonitorCaptureMode") var multiMonitorCaptureMode: MultiMonitorCaptureMode = .askEveryTime
    @CapturePreference("showRegionIndicator") var showRegionIndicator: Bool = true
    @CapturePreference("preventDisplaySleepWhileRecording") var preventDisplaySleepWhileRecording: Bool = true
    @CapturePreference("includeTinyClipsInCapture") var includeTinyClipsInCapture: Bool = false
    @CapturePreference("showBrandingOverlay") var showBrandingOverlay: Bool = false
    @CapturePreference("teleprompterEnabled") var teleprompterEnabled: Bool = false
    @CapturePreference("teleprompterTranscript") var teleprompterTranscript: String = ""
    @CapturePreference("teleprompterScrollSpeed") var teleprompterScrollSpeed: Double = 50
    @CapturePreference("teleprompterFontSize") var teleprompterFontSize: String = TeleprompterDisplaySize.medium.rawValue
    @CapturePreference("teleprompterPanelHeight") var teleprompterPanelHeight: String = TeleprompterDisplaySize.medium.rawValue
    // Custom global hotkeys (stored as Carbon keyCode + modifiers bitmask).
    // Only Screenshot (⌃⌥⌘5) is bound by default.
    // 6400 = controlKey (4096) | optionKey (2048) | cmdKey (256)
    @CapturePreference("screenshotHotKeyCode") var screenshotHotKeyCode: Int = 23      // kVK_ANSI_5
    @CapturePreference("screenshotHotKeyModifiers") var screenshotHotKeyModifiers: Int = 6400
    @CapturePreference("videoHotKeyCode") var videoHotKeyCode: Int = -1
    @CapturePreference("videoHotKeyModifiers") var videoHotKeyModifiers: Int = 0
    @CapturePreference("studioRecordingHotKeyCode") var studioRecordingHotKeyCode: Int = -1
    @CapturePreference("studioRecordingHotKeyModifiers") var studioRecordingHotKeyModifiers: Int = 0
    @CapturePreference("gifHotKeyCode") var gifHotKeyCode: Int = -1
    @CapturePreference("gifHotKeyModifiers") var gifHotKeyModifiers: Int = 0
    @CapturePreference("stopRecordingHotKeyCode") var stopRecordingHotKeyCode: Int = -1
    @CapturePreference("stopRecordingHotKeyModifiers") var stopRecordingHotKeyModifiers: Int = 0
    @CapturePreference("copyTextFromRegionHotKeyCode") var copyTextFromRegionHotKeyCode: Int = -1
    @CapturePreference("copyTextFromRegionHotKeyModifiers") var copyTextFromRegionHotKeyModifiers: Int = 0
    // Direct-to-mode screenshot shortcuts (skip the capture picker); all unbound by default.
    @CapturePreference("screenshotRegionHotKeyCode") var screenshotRegionHotKeyCode: Int = -1
    @CapturePreference("screenshotRegionHotKeyModifiers") var screenshotRegionHotKeyModifiers: Int = 0
    @CapturePreference("screenshotWindowHotKeyCode") var screenshotWindowHotKeyCode: Int = -1
    @CapturePreference("screenshotWindowHotKeyModifiers") var screenshotWindowHotKeyModifiers: Int = 0
    @CapturePreference("screenshotScreenHotKeyCode") var screenshotScreenHotKeyCode: Int = -1
    @CapturePreference("screenshotScreenHotKeyModifiers") var screenshotScreenHotKeyModifiers: Int = 0

    static func defaultSaveDirectoryURL(for captureType: CaptureType) -> URL {
        let fallbackBase = URL(fileURLWithPath: NSHomeDirectory(), isDirectory: true)
        let searchPath: FileManager.SearchPathDirectory = captureType == .screenshot
            ? .picturesDirectory
            : .moviesDirectory
        let baseURL = FileManager.default.urls(for: searchPath, in: .userDomainMask).first ?? fallbackBase
        return baseURL.appendingPathComponent("TinyClips", isDirectory: true)
    }

    /// The path to show people for a folder. In the App Store sandbox, Pictures and Movies are
    /// links from the app's container to the person's own folders, so show where they lead.
    static func displayPath(for url: URL) -> String {
#if APPSTORE
        return resolvingSymlinksInExistingPath(url).path
#else
        return url.path
#endif
    }

    /// Resolves links in the part of the path that exists, keeping folders not yet created.
    static func resolvingSymlinksInExistingPath(_ url: URL) -> URL {
        var existing = url.standardizedFileURL
        var missingComponents: [String] = []
        while existing.path != "/", !FileManager.default.fileExists(atPath: existing.path) {
            missingComponents.insert(existing.lastPathComponent, at: 0)
            existing.deleteLastPathComponent()
        }
        return missingComponents.reduce(existing.resolvingSymlinksInPath()) {
            $0.appendingPathComponent($1, isDirectory: true)
        }
    }

    static func isSameDirectory(_ path: String, as url: URL) -> Bool {
        resolvingSymlinksInExistingPath(URL(fileURLWithPath: path, isDirectory: true)).path ==
            resolvingSymlinksInExistingPath(url).path
    }

    func saveDirectoryPath(for captureType: CaptureType) -> String {
        switch captureType {
        case .screenshot:
            return screenshotSaveDirectory
        case .video:
            return videoSaveDirectory
        case .gif:
            return gifSaveDirectory
        }
    }

#if APPSTORE
    func saveDirectoryBookmark(for captureType: CaptureType) -> Data {
        switch captureType {
        case .screenshot:
            return screenshotSaveDirectoryBookmark
        case .video:
            return videoSaveDirectoryBookmark
        case .gif:
            return gifSaveDirectoryBookmark
        }
    }

    func saveDirectoryDisplayPath(for captureType: CaptureType) -> String {
        switch captureType {
        case .screenshot:
            return screenshotSaveDirectoryDisplayPath
        case .video:
            return videoSaveDirectoryDisplayPath
        case .gif:
            return gifSaveDirectoryDisplayPath
        }
    }

    func setSaveDirectoryBookmark(_ bookmark: Data, displayPath: String, for captureType: CaptureType?) {
        switch captureType {
        case .screenshot:
            screenshotSaveDirectoryBookmark = bookmark
            screenshotSaveDirectoryDisplayPath = displayPath
        case .video:
            videoSaveDirectoryBookmark = bookmark
            videoSaveDirectoryDisplayPath = displayPath
        case .gif:
            gifSaveDirectoryBookmark = bookmark
            gifSaveDirectoryDisplayPath = displayPath
        case nil:
            saveDirectoryBookmark = bookmark
            saveDirectoryDisplayPath = displayPath
        }
    }

#endif

    var imageFormat: ImageFormat {
        get { Self.imageFormat(from: screenshotFormat) }
        set { screenshotFormat = newValue.rawValue }
    }

    static func imageFormat(from rawValue: String) -> ImageFormat {
        ImageFormat(rawValue: rawValue) ?? .jpeg
    }

    static func videoCodec(from rawValue: String?) -> VideoCodec {
        guard let rawValue else { return .h264 }
        return VideoCodec(rawValue: rawValue) ?? .h264
    }

    static func videoCodec(in defaults: UserDefaults) -> VideoCodec {
        videoCodec(from: defaults.string(forKey: videoCodecKey))
    }

    static func setVideoCodec(_ codec: VideoCodec, in defaults: UserDefaults) {
        defaults.set(codec.rawValue, forKey: videoCodecKey)
    }

    static func clampedAudioOffsetMs(_ offset: Int) -> Int {
        min(audioOffsetRangeMs.upperBound, max(audioOffsetRangeMs.lowerBound, offset))
    }

    static func hotKeyBinding(for action: HotKeyAction, defaults: UserDefaults) -> HotKeyBinding {
        let fallback = HotKeyBinding.defaultBinding(for: action)
        let keys = hotKeyDefaultsKeys(for: action)
        let keyCode = defaults.object(forKey: keys.keyCode) as? Int ?? fallback.keyCode
        let carbonModifiers = defaults.object(forKey: keys.modifiers) as? Int ?? fallback.carbonModifiers

        if fallback.isUnbound, keyCode == 0, carbonModifiers == 0 {
            return fallback
        }

        return HotKeyBinding(
            keyCode: keyCode,
            carbonModifiers: carbonModifiers
        )
    }

    static func setHotKeyBinding(
        _ binding: HotKeyBinding,
        for action: HotKeyAction,
        defaults: UserDefaults
    ) {
        let keys = hotKeyDefaultsKeys(for: action)
        defaults.set(binding.keyCode, forKey: keys.keyCode)
        defaults.set(binding.carbonModifiers, forKey: keys.modifiers)
    }

    private static func hotKeyDefaultsKeys(
        for action: HotKeyAction
    ) -> (keyCode: String, modifiers: String) {
        switch action {
        case .screenshot:
            return ("screenshotHotKeyCode", "screenshotHotKeyModifiers")
        case .recordVideo:
            return ("videoHotKeyCode", "videoHotKeyModifiers")
        case .studioRecording:
            return ("studioRecordingHotKeyCode", "studioRecordingHotKeyModifiers")
        case .recordGif:
            return ("gifHotKeyCode", "gifHotKeyModifiers")
        case .copyTextFromRegion:
            return ("copyTextFromRegionHotKeyCode", "copyTextFromRegionHotKeyModifiers")
        case .screenshotRegion:
            return ("screenshotRegionHotKeyCode", "screenshotRegionHotKeyModifiers")
        case .screenshotWindow:
            return ("screenshotWindowHotKeyCode", "screenshotWindowHotKeyModifiers")
        case .screenshotScreen:
            return ("screenshotScreenHotKeyCode", "screenshotScreenHotKeyModifiers")
        case .stopRecording:
            return ("stopRecordingHotKeyCode", "stopRecordingHotKeyModifiers")
        }
    }

    func shouldCopyToClipboard(for type: CaptureType) -> Bool {
        switch type {
        case .screenshot:
            return copyScreenshotToClipboard
        case .video:
            return copyVideoToClipboard
        case .gif:
            return copyGifToClipboard
        }
    }

    func hotKeyBinding(for action: HotKeyAction) -> HotKeyBinding {
        Self.hotKeyBinding(for: action, defaults: defaults)
    }

    func setHotKeyBinding(_ binding: HotKeyBinding, for action: HotKeyAction) {
        Self.setHotKeyBinding(binding, for: action, defaults: defaults)
        objectWillChange.send()
    }

    var hotKeyBindings: [HotKeyAction: HotKeyBinding] {
        Dictionary(
            uniqueKeysWithValues: HotKeyAction.allCases.map {
                ($0, hotKeyBinding(for: $0))
            }
        )
    }

    func shouldShowCapturePicker(for type: CaptureType) -> Bool {
        switch type {
        case .screenshot:
            return showScreenshotCapturePicker
        case .video:
            return showVideoCapturePicker
        case .gif:
            return showGifCapturePicker
        }
    }

    func shouldShowCapturePickerAfterCapture(for type: CaptureType) -> Bool {
        switch type {
        case .screenshot:
            return showScreenshotCapturePicker && showScreenshotCapturePickerAfterCapture
        case .video:
            return showVideoCapturePicker && showVideoCapturePickerAfterCapture
        case .gif:
            return showGifCapturePicker && showGifCapturePickerAfterCapture
        }
    }

    func mouseClickOverlayStyle(for type: CaptureType) -> MouseClickOverlayStyle {
        switch type {
        case .video:
            return MouseClickOverlayStyle(
                colorHex: videoMouseClickColorHex,
                size: CGFloat(videoMouseClickSize),
                strokeWidth: CGFloat(videoMouseClickStrokeWidth),
                opacity: CGFloat(videoMouseClickOpacity),
                duration: videoMouseClickDuration
            )
        case .gif:
            if gifMouseClicksUseVideoSettings {
                return mouseClickOverlayStyle(for: .video)
            }
            return MouseClickOverlayStyle(
                colorHex: gifMouseClickColorHex,
                size: CGFloat(gifMouseClickSize),
                strokeWidth: CGFloat(gifMouseClickStrokeWidth),
                opacity: CGFloat(gifMouseClickOpacity),
                duration: gifMouseClickDuration
            )
        case .screenshot:
            return MouseClickOverlayStyle(
                colorHex: "#FFFFFF",
                size: 32,
                strokeWidth: 3,
                opacity: 0.85,
                duration: 0.45
            )
        }
    }

    func shouldShowMouseClickVisuals(for type: CaptureType) -> Bool {
        switch type {
        case .video:
            return showMouseClickVisualsInVideo
        case .gif:
            return gifMouseClicksUseVideoSettings ? showMouseClickVisualsInVideo : showMouseClickVisualsInGif
        case .screenshot:
            return false
        }
    }

    func setShowMouseClickVisuals(_ isEnabled: Bool, for type: CaptureType) {
        switch type {
        case .video:
            showMouseClickVisualsInVideo = isEnabled
        case .gif:
            if gifMouseClicksUseVideoSettings {
                showMouseClickVisualsInVideo = isEnabled
            } else {
                showMouseClickVisualsInGif = isEnabled
            }
        case .screenshot:
            break
        }
    }

    var videoMouseClickColor: NSColor {
        get { NSColor(hexRGBString: videoMouseClickColorHex) ?? .white }
        set { videoMouseClickColorHex = newValue.hexRGBString }
    }

    var gifMouseClickColor: NSColor {
        get { NSColor(hexRGBString: gifMouseClickColorHex) ?? .white }
        set { gifMouseClickColorHex = newValue.hexRGBString }
    }

    func resetToDefaults(preservingHotKeys: Bool = false) {
        resetPreferences(preservingHotKeys: preservingHotKeys)
#if APPSTORE
        SaveService.shared.invalidateAllSaveDirectoryBookmarks()
#endif
        UploadcareCredentialsStore.shared.clearAll()
        Task { @MainActor in
            CaptureAnalyticsStore.shared.clear()
        }
    }

    func resetPreferences(preservingHotKeys: Bool = false) {
        objectWillChange.send()
        Self.resetStoredDefaults(defaults, preservingHotKeys: preservingHotKeys)
        // Store it so finishing onboarding again can't make the migration treat this as an upgrade.
        screenshotEditorDefaultTool = EditTool.background.settingID
        defaults.set(true, forKey: Self.useDefaultSaveDirectoriesKey)
        ensureSaveDirectoryDefaults(defaults)
    }

    static func resetStoredDefaults(_ defaults: UserDefaults, preservingHotKeys: Bool = false) {
        for key in resettableDefaultsKeys + (preservingHotKeys ? [] : resetHotKeyDefaultsKeys) + appStoreDefaultsKeys {
            defaults.removeObject(forKey: key)
        }
    }

    static let resettableDefaultsKeys: [String] = [
        "saveDirectory", "screenshotSaveDirectory", "videoSaveDirectory", "gifSaveDirectory",
        "videoGifSaveDirectory", "useDefaultSaveDirectories",
        "copyToClipboard", "copyScreenshotToClipboard", "copyVideoToClipboard", "copyGifToClipboard",
        "showInFinder", "showSaveNotifications", "showInDock",
        "autoUpdateEnabled",
        "fileNameTemplate",
        "uploadcareEnabled", "clipsManagerShowAutoTags", "clipsManagerShowNotesPreview", "clipsManagerShowQuickActions",
        "clipsManagerShowUploadStatus", "clipsManagerConfirmDelete", "clipsManagerCompactListDensity",
        "clipsManagerSelectionRowTapSelects", "clipsManagerIgnoreNonTinyClipsFiles", "clipsManagerRememberLastState",
        "clipsManagerDefaultViewMode", "clipsManagerDefaultSortOption", "clipsManagerDefaultFilterType", "clipsManagerDefaultDateFilter",
        "clipsManagerAutoRefreshSeconds", "clipsManagerArchiveOldClips", "clipsManagerArchiveAfterDays",
        "clipsManagerAutoUploadAfterSave", "clipsManagerAutoCopyUploadLink", "clipsManagerShowInspector",
        "recentCapturesLimit",
        "clipsManagerLastViewMode", "clipsManagerLastSortOption", "clipsManagerLastFilterType", "clipsManagerLastDateFilter",
        "clipsManagerLastSmartCollection", "clipsManagerLastSearchText", "clipsManagerLastSelectedTag", "clipsManagerLastSelectedCollection",
        "gifFrameRate", "gifMaxWidth", "videoFrameRate", videoCodecKey, "showMouseClickVisualsInVideo", "showMouseClickVisualsInGif",
        "gifMouseClicksUseVideoSettings",
        "videoMouseClickColorHex", "videoMouseClickSize", "videoMouseClickStrokeWidth", "videoMouseClickOpacity", "videoMouseClickDuration",
        "gifMouseClickColorHex", "gifMouseClickSize", "gifMouseClickStrokeWidth", "gifMouseClickOpacity", "gifMouseClickDuration",
        "showTrimmer", "studioPreviewEnabled", "videoAfterRecording", "studioDefaultLook",
        "studioSourceRetentionDays", "studioStorageCapGigabytes", StudioProjectSaveLocation.settingsKey, studioCaptionLanguageKey,
        "studioRevealAfterFirstSave", "preventDisplaySleepWhileRecording",
        studioExportResolutionKey, CaptureSettingsDefaultsKey.studioExportQuality,
        "recordAudio", "recordMicrophone", "audioOffsetMs", "microphoneLimiterEnabled", "windNoiseRemovalEnabled", "selectedMicrophoneID",
        "webcamEnabled", "selectedWebcamID", "webcamShape", "webcamSize", "webcamCorner", "webcamCornerRadius",
        "showScreenshotEditor", screenshotEditorNativeSizeKey, screenshotEditorDefaultToolKey, confirmEditorEscapeKey, "showGifTrimmer",
        "saveImmediatelyScreenshot", "saveImmediatelyVideo", "saveImmediatelyGif",
        "showScreenshotCapturePicker", "showScreenshotCapturePickerAfterCapture",
        "showVideoCapturePicker", "showVideoCapturePickerAfterCapture",
        "showGifCapturePicker", "showGifCapturePickerAfterCapture",
        "screenshotFormat", "screenshotScale", "excludeClickLightFromScreenshots", "jpegQuality",
        "videoCountdownEnabled", "videoCountdownDuration",
        "videoRecordingTimeLimitMinutes",
        "gifCountdownEnabled", "gifCountdownDuration",
        "screenshotCountdownEnabled", "screenshotCountdownDuration",
        "hasCompletedOnboarding", "alwaysCaptureMainDisplay", "multiMonitorCaptureMode", "showRegionIndicator",
        "includeTinyClipsInCapture", "showBrandingOverlay",
        "teleprompterEnabled", "teleprompterTranscript", "teleprompterScrollSpeed", "teleprompterFontSize", "teleprompterPanelHeight",
        "teleprompterPanelX", "teleprompterPanelY"
    ]

    static let resetHotKeyDefaultsKeys = [
        "screenshotHotKeyCode", "screenshotHotKeyModifiers",
        "videoHotKeyCode", "videoHotKeyModifiers",
        "studioRecordingHotKeyCode", "studioRecordingHotKeyModifiers",
        "gifHotKeyCode", "gifHotKeyModifiers",
        "stopRecordingHotKeyCode", "stopRecordingHotKeyModifiers",
        "copyTextFromRegionHotKeyCode", "copyTextFromRegionHotKeyModifiers",
        "screenshotRegionHotKeyCode", "screenshotRegionHotKeyModifiers",
        "screenshotWindowHotKeyCode", "screenshotWindowHotKeyModifiers",
        "screenshotScreenHotKeyCode", "screenshotScreenHotKeyModifiers"
    ]

#if APPSTORE
    static let appStoreDefaultsKeys: [String] = [
        "saveDirectoryBookmark", "saveDirectoryDisplayPath",
        "screenshotSaveDirectoryBookmark", "screenshotSaveDirectoryDisplayPath",
        "videoSaveDirectoryBookmark", "videoSaveDirectoryDisplayPath",
        "gifSaveDirectoryBookmark", "gifSaveDirectoryDisplayPath",
        "videoGifSaveDirectoryBookmark", "videoGifSaveDirectoryDisplayPath"
    ]
#else
    static let appStoreDefaultsKeys: [String] = []
#endif

    init(defaults: UserDefaults = .standard, performMigrations: Bool = true) {
        self.defaults = defaults
        audioOffsetMs = audioOffsetMs
        guard performMigrations else { return }

        if defaults.object(forKey: "studioPreviewEnabled") != nil,
           !defaults.bool(forKey: "studioPreviewEnabled") {
            // Studio used to suspend cleanup while disabled. Preserve those sources on upgrade.
            defaults.set(0, forKey: "studioSourceRetentionDays")
            defaults.set(0, forKey: "studioStorageCapGigabytes")
            defaults.removeObject(forKey: "studioPreviewEnabled")
        }

        Self.migrateSaveDirectorySettings(in: defaults)
        ensureSaveDirectoryDefaults(defaults)
        Self.migrateScreenshotEditorDefaultTool(in: defaults)

        guard defaults.object(forKey: "multiMonitorCaptureMode") == nil else { return }

        multiMonitorCaptureMode = defaults.bool(forKey: "alwaysCaptureMainDisplay")
            ? .mainDisplay
            : .askEveryTime
        defaults.removeObject(forKey: "alwaysCaptureMainDisplay")
    }

    static let useDefaultSaveDirectoriesKey = "useDefaultSaveDirectories"
    /// Marks the one-time repair of "Use default folders" turned off by older builds. Not reset.
    static let restoredDefaultSaveDirectoriesKey = "restoredDefaultSaveDirectories"

    static func migrateSaveDirectorySettings(in defaults: UserDefaults) {
        let hasCustomFolder = hasCustomSaveDirectory(in: defaults)
        let needsRepair = defaults.object(forKey: restoredDefaultSaveDirectoriesKey) == nil
        defaults.set(true, forKey: restoredDefaultSaveDirectoriesKey)

        if defaults.object(forKey: useDefaultSaveDirectoriesKey) != nil {
            // Older builds saved the default folders, then read them back on the next launch as
            // chosen folders and turned this off. No folder was chosen, so turn it back on.
            if needsRepair, !hasCustomFolder, !defaults.bool(forKey: useDefaultSaveDirectoriesKey) {
                defaults.set(true, forKey: useDefaultSaveDirectoriesKey)
            }
            return
        }

        guard hasCustomFolder else {
            defaults.set(true, forKey: useDefaultSaveDirectoriesKey)
            return
        }

        let string = { defaults.string(forKey: $0) ?? "" }
        let legacySharedDirectory = string("saveDirectory")
        let legacyVideoGifDirectory = string("videoGifSaveDirectory")
        let legacyScreenshotDirectory = string("screenshotSaveDirectory")
        let videoGifDirectory = legacyVideoGifDirectory.isEmpty ? legacySharedDirectory : legacyVideoGifDirectory
        let screenshotDirectory = legacyScreenshotDirectory.isEmpty ? legacySharedDirectory : legacyScreenshotDirectory

        defaults.set(false, forKey: useDefaultSaveDirectoriesKey)
        for (key, path) in [
            ("screenshotSaveDirectory", screenshotDirectory),
            ("videoSaveDirectory", videoGifDirectory),
            ("gifSaveDirectory", videoGifDirectory)
        ] where !path.isEmpty {
            defaults.set(path, forKey: key)
        }

#if APPSTORE
        let data = { defaults.data(forKey: $0) ?? Data() }
        let sharedBookmark = data("saveDirectoryBookmark")
        let sharedDisplayPath = string("saveDirectoryDisplayPath")
        if data("screenshotSaveDirectoryBookmark").isEmpty, !sharedBookmark.isEmpty {
            defaults.set(sharedBookmark, forKey: "screenshotSaveDirectoryBookmark")
            defaults.set(sharedDisplayPath, forKey: "screenshotSaveDirectoryDisplayPath")
        }
        let legacyVideoGifBookmark = data("videoGifSaveDirectoryBookmark")
        let legacyVideoGifDisplayPath = string("videoGifSaveDirectoryDisplayPath")
        let videoGifBookmark = legacyVideoGifBookmark.isEmpty ? sharedBookmark : legacyVideoGifBookmark
        let videoGifDisplayPath = legacyVideoGifDisplayPath.isEmpty ? sharedDisplayPath : legacyVideoGifDisplayPath
        if !videoGifBookmark.isEmpty {
            defaults.set(videoGifBookmark, forKey: "videoSaveDirectoryBookmark")
            defaults.set(videoGifDisplayPath, forKey: "videoSaveDirectoryDisplayPath")
            defaults.set(videoGifBookmark, forKey: "gifSaveDirectoryBookmark")
            defaults.set(videoGifDisplayPath, forKey: "gifSaveDirectoryDisplayPath")
        }
#endif
    }

    /// The editor used to open with Move. Keep that for people upgrading and start new installs on
    /// Background. The choice is always stored, so finishing onboarding later doesn't change it.
    static func migrateScreenshotEditorDefaultTool(in defaults: UserDefaults) {
        guard defaults.object(forKey: screenshotEditorDefaultToolKey) == nil else { return }
        let tool: EditTool = defaults.bool(forKey: "hasCompletedOnboarding") ? .move : .background
        defaults.set(tool.settingID, forKey: screenshotEditorDefaultToolKey)
    }

    var screenshotEditorDefaultEditTool: EditTool {
        get { EditTool(settingID: screenshotEditorDefaultTool) ?? .background }
        set { screenshotEditorDefaultTool = newValue.settingID }
    }

    /// Whether someone chose a save folder, as opposed to the default folders saved for them.
    static func hasCustomSaveDirectory(in defaults: UserDefaults) -> Bool {
        let string = { defaults.string(forKey: $0) ?? "" }
        if !string("saveDirectory").isEmpty || !string("videoGifSaveDirectory").isEmpty {
            return true
        }
        let typedDirectories: [(String, CaptureType)] = [
            ("screenshotSaveDirectory", .screenshot),
            ("videoSaveDirectory", .video),
            ("gifSaveDirectory", .gif)
        ]
        let hasCustomPath = typedDirectories.contains { key, type in
            let path = string(key)
            return !path.isEmpty && !isSameDirectory(path, as: defaultSaveDirectoryURL(for: type))
        }
#if APPSTORE
        let bookmarkKeys = [
            "saveDirectoryBookmark", "screenshotSaveDirectoryBookmark", "videoSaveDirectoryBookmark",
            "gifSaveDirectoryBookmark", "videoGifSaveDirectoryBookmark"
        ]
        let hasBookmark = bookmarkKeys.contains { !(defaults.data(forKey: $0) ?? Data()).isEmpty }
#else
        let hasBookmark = false
#endif
        return hasCustomPath || hasBookmark
    }

    private func ensureSaveDirectoryDefaults(_ defaults: UserDefaults) {
        let paths: [(String, URL)] = [
            ("screenshotSaveDirectory", Self.defaultSaveDirectoryURL(for: .screenshot)),
            ("videoSaveDirectory", Self.defaultSaveDirectoryURL(for: .video)),
            ("gifSaveDirectory", Self.defaultSaveDirectoryURL(for: .gif))
        ]

        for (key, url) in paths where (defaults.string(forKey: key) ?? "").isEmpty {
            defaults.set(url.path, forKey: key)
        }

#if APPSTORE
        let displayPaths: [(String, URL)] = [
            ("screenshotSaveDirectoryDisplayPath", Self.defaultSaveDirectoryURL(for: .screenshot)),
            ("videoSaveDirectoryDisplayPath", Self.defaultSaveDirectoryURL(for: .video)),
            ("gifSaveDirectoryDisplayPath", Self.defaultSaveDirectoryURL(for: .gif))
        ]

        for (key, url) in displayPaths where (defaults.string(forKey: key) ?? "").isEmpty {
            defaults.set(url.path, forKey: key)
        }
#endif
    }
}

// MARK: - Uploadcare credentials

final class UploadcareCredentialsStore {
    static let shared = UploadcareCredentialsStore()

    struct Credentials: Codable {
        var publicKey: String
        var secretKey: String

        static let empty = Credentials(publicKey: "", secretKey: "")
    }

    private enum Account {
        static let credentials = "uploadcare-credentials"
    }

    private let service = "com.refractored.tinyclips.uploadcare"
    private var cachedCredentials: Credentials?
    private init() {}

    func credentials() -> Credentials {
        if let cachedCredentials {
            return cachedCredentials
        }
        let loaded = loadCredentialsFromKeychain() ?? .empty
        cachedCredentials = loaded
        return loaded
    }

    func setPublicKey(_ value: String) {
        var updated = credentials()
        updated.publicKey = value
        persistCredentials(updated)
    }

    func setSecretKey(_ value: String) {
        var updated = credentials()
        updated.secretKey = value
        persistCredentials(updated)
    }

    func clearAll() {
        cachedCredentials = .empty
        removeKeychainValue(for: Account.credentials)
    }

    private func loadCredentialsFromKeychain() -> Credentials? {
        guard let data = keychainData(for: Account.credentials),
              let decoded = try? JSONDecoder().decode(Credentials.self, from: data) else {
            return nil
        }
        return Credentials(
            publicKey: decoded.publicKey.trimmingCharacters(in: .whitespacesAndNewlines),
            secretKey: decoded.secretKey.trimmingCharacters(in: .whitespacesAndNewlines)
        )
    }

    private func persistCredentials(_ credentials: Credentials) {
        let normalized = Credentials(
            publicKey: credentials.publicKey.trimmingCharacters(in: .whitespacesAndNewlines),
            secretKey: credentials.secretKey.trimmingCharacters(in: .whitespacesAndNewlines)
        )
        cachedCredentials = normalized

        if normalized.publicKey.isEmpty && normalized.secretKey.isEmpty {
            removeKeychainValue(for: Account.credentials)
            return
        }

        guard let data = try? JSONEncoder().encode(normalized) else { return }
        setKeychainData(data, for: Account.credentials)
    }

    private func keychainData(for account: String) -> Data? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]

        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        guard status == errSecSuccess,
              let data = item as? Data else {
            return nil
        }
        return data
    }

    private func setKeychainData(_ data: Data, for account: String) {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
        let attributes: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlock
        ]

        let updateStatus = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        if updateStatus == errSecItemNotFound {
            var add = query
            add[kSecValueData as String] = data
            add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
            _ = SecItemAdd(add as CFDictionary, nil)
        }
    }

    private func removeKeychainValue(for account: String) {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
        SecItemDelete(query as CFDictionary)
    }
}

/// Where Studio's Save Project and Save Project As start: the folder chosen in Settings, or a
/// Studio folder inside the video folder. A project already saved to a folder keeps saving there,
/// and drafts and recordings stay in Studio's own storage.
enum StudioProjectSaveLocation {
    static let settingsKey = "studioProjectFolder"
    static let defaultFolderName = "Studio"

    static func defaultFolder(in videoFolder: URL) -> URL {
        videoFolder.appendingPathComponent(defaultFolderName, isDirectory: true)
    }

    /// Whether the setting names a folder of the user's own choosing.
    static func isCustom(_ configured: String) -> Bool {
        configured.hasPrefix("/")
    }

    /// The folder Save Project starts in. Does not touch the disk.
    static func resolve(configured: String, videoFolder: URL, useDefaults: Bool = false) -> URL {
        !useDefaults && isCustom(configured)
            ? URL(fileURLWithPath: configured, isDirectory: true)
            : defaultFolder(in: videoFolder)
    }

    /// The folder Save Project starts in, made ready to open. A chosen folder that is gone falls
    /// back to the default, which is created when missing; the video folder is used when it cannot
    /// be. `folderExists` is false-negative on the App Store build for folders outside the
    /// sandbox, so that build trusts a chosen folder and lets the save panel find it.
    static func prepare(
        configured: String,
        videoFolder: URL,
        useDefaults: Bool = false,
        trustChosenFolder: Bool = false,
        fileManager: FileManager = .default
    ) -> URL {
        if !useDefaults && isCustom(configured) {
            let chosen = resolve(configured: configured, videoFolder: videoFolder)
            var isDirectory: ObjCBool = false
            if trustChosenFolder || (fileManager.fileExists(atPath: chosen.path, isDirectory: &isDirectory) && isDirectory.boolValue) {
                return chosen
            }
        }
        let studio = defaultFolder(in: videoFolder)
        do {
            try fileManager.createDirectory(at: studio, withIntermediateDirectories: true)
            return studio
        } catch {
            return videoFolder
        }
    }
}
