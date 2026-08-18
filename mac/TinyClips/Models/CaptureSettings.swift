import Foundation
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

    func makeStreamConfig() -> SCStreamConfiguration {
        let config = SCStreamConfiguration()
        config.sourceRect = sourceRect
        config.width = pixelWidth
        config.height = pixelHeight
        config.scalesToFit = true
        config.showsCursor = true
        return config
    }

    func makeFilter(alwaysExcluding windows: [SCWindow] = []) async throws -> SCContentFilter {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        guard let display = content.displays.first(where: { $0.displayID == self.displayID }) else {
            throw CaptureError.displayNotFound
        }
        let includeTinyClips = CaptureSettings.shared.includeTinyClipsInCapture
        if includeTinyClips {
            return SCContentFilter(display: display, excludingWindows: windows)
        }
        let excludedApps: [SCRunningApplication] = content.applications.filter {
            $0.bundleIdentifier == Bundle.main.bundleIdentifier
        }
        if excludedApps.isEmpty {
            return SCContentFilter(display: display, excludingWindows: windows)
        }
        return SCContentFilter(display: display, excludingApplications: excludedApps, exceptingWindows: [])
    }
}

struct CaptureTarget {
    let region: CaptureRegion

    init(region: CaptureRegion) {
        self.region = region
    }

    func prepare(alwaysExcluding windows: [SCWindow] = []) async throws -> PreparedCaptureTarget {
        PreparedCaptureTarget(
            filter: try await region.makeFilter(alwaysExcluding: windows),
            config: region.makeStreamConfig(),
            pixelWidth: region.pixelWidth,
            pixelHeight: region.pixelHeight
        )
    }
}

struct PreparedCaptureTarget {
    let filter: SCContentFilter
    let config: SCStreamConfiguration
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

// MARK: - Capture Error

enum CaptureError: LocalizedError {
    case displayNotFound
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

    var label: String {
        switch self {
        case .png: return "PNG"
        case .jpeg: return "JPEG"
        }
    }

    var utType: UTType {
        switch self {
        case .png: return .png
        case .jpeg: return .jpeg
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

class CaptureSettings: ObservableObject {
    static let shared = CaptureSettings()
    private let defaults: UserDefaults

    @AppStorage("saveDirectory") var saveDirectory: String = NSHomeDirectory() + "/Desktop"
    @AppStorage("screenshotSaveDirectory") var screenshotSaveDirectory: String = defaultSaveDirectoryURL(for: .screenshot).path
    @AppStorage("videoSaveDirectory") var videoSaveDirectory: String = defaultSaveDirectoryURL(for: .video).path
    @AppStorage("gifSaveDirectory") var gifSaveDirectory: String = defaultSaveDirectoryURL(for: .gif).path
    @AppStorage("videoGifSaveDirectory") var videoGifSaveDirectory: String = ""
    @AppStorage("useDefaultSaveDirectories") var useDefaultSaveDirectories: Bool = true
#if APPSTORE
    @AppStorage("saveDirectoryBookmark") var saveDirectoryBookmark: Data = Data()
    @AppStorage("saveDirectoryDisplayPath") var saveDirectoryDisplayPath: String = ""
    @AppStorage("screenshotSaveDirectoryBookmark") var screenshotSaveDirectoryBookmark: Data = Data()
    @AppStorage("screenshotSaveDirectoryDisplayPath") var screenshotSaveDirectoryDisplayPath: String = defaultSaveDirectoryURL(for: .screenshot).path
    @AppStorage("videoSaveDirectoryBookmark") var videoSaveDirectoryBookmark: Data = Data()
    @AppStorage("videoSaveDirectoryDisplayPath") var videoSaveDirectoryDisplayPath: String = defaultSaveDirectoryURL(for: .video).path
    @AppStorage("gifSaveDirectoryBookmark") var gifSaveDirectoryBookmark: Data = Data()
    @AppStorage("gifSaveDirectoryDisplayPath") var gifSaveDirectoryDisplayPath: String = defaultSaveDirectoryURL(for: .gif).path
    @AppStorage("videoGifSaveDirectoryBookmark") var videoGifSaveDirectoryBookmark: Data = Data()
    @AppStorage("videoGifSaveDirectoryDisplayPath") var videoGifSaveDirectoryDisplayPath: String = ""
#endif
    @AppStorage("copyScreenshotToClipboard") var copyScreenshotToClipboard: Bool = true
    @AppStorage("copyVideoToClipboard") var copyVideoToClipboard: Bool = false
    @AppStorage("copyGifToClipboard") var copyGifToClipboard: Bool = false
    @AppStorage("showInFinder") var showInFinder: Bool = false
    @AppStorage("showSaveNotifications") var showSaveNotifications: Bool = false
    @AppStorage("showInDock") var showInDock: Bool = false
    @AppStorage("fileNameTemplate") var fileNameTemplate: String = "TinyClips {date} at {time}"
    @AppStorage("uploadcareEnabled") var uploadcareEnabled: Bool = false
    @AppStorage("clipsManagerShowAutoTags") var clipsManagerShowAutoTags: Bool = true
    @AppStorage("clipsManagerShowNotesPreview") var clipsManagerShowNotesPreview: Bool = true
    @AppStorage("clipsManagerShowQuickActions") var clipsManagerShowQuickActions: Bool = true
    @AppStorage("clipsManagerShowUploadStatus") var clipsManagerShowUploadStatus: Bool = true
    @AppStorage("clipsManagerConfirmDelete") var clipsManagerConfirmDelete: Bool = true
    @AppStorage("clipsManagerCompactListDensity") var clipsManagerCompactListDensity: Bool = false
    @AppStorage("clipsManagerSelectionRowTapSelects") var clipsManagerSelectionRowTapSelects: Bool = true
    @AppStorage("clipsManagerIgnoreNonTinyClipsFiles") var clipsManagerIgnoreNonTinyClipsFiles: Bool = false
    @AppStorage("clipsManagerRememberLastState") var clipsManagerRememberLastState: Bool = true
    @AppStorage("clipsManagerDefaultViewMode") var clipsManagerDefaultViewMode: String = "grid"
    @AppStorage("clipsManagerDefaultSortOption") var clipsManagerDefaultSortOption: String = "Newest First"
    @AppStorage("clipsManagerDefaultFilterType") var clipsManagerDefaultFilterType: String = "All"
    @AppStorage("clipsManagerDefaultDateFilter") var clipsManagerDefaultDateFilter: String = "Any Date"
    @AppStorage("clipsManagerAutoRefreshSeconds") var clipsManagerAutoRefreshSeconds: Int = 0
    @AppStorage("clipsManagerArchiveOldClips") var clipsManagerArchiveOldClips: Bool = false
    @AppStorage("clipsManagerArchiveAfterDays") var clipsManagerArchiveAfterDays: Int = 30
    @AppStorage("clipsManagerAutoUploadAfterSave") var clipsManagerAutoUploadAfterSave: Bool = false
    @AppStorage("clipsManagerAutoCopyUploadLink") var clipsManagerAutoCopyUploadLink: Bool = false
    @AppStorage("gifFrameRate") var gifFrameRate: Double = 10
    @AppStorage("gifMaxWidth") var gifMaxWidth: Int = 640
    @AppStorage("videoFrameRate") var videoFrameRate: Int = 30
    @AppStorage("showMouseClickVisualsInVideo") var showMouseClickVisualsInVideo: Bool = false
    @AppStorage("showMouseClickVisualsInGif") var showMouseClickVisualsInGif: Bool = false
    @AppStorage("gifMouseClicksUseVideoSettings") var gifMouseClicksUseVideoSettings: Bool = false
    @AppStorage("videoMouseClickColorHex") var videoMouseClickColorHex: String = "#0A84FF"
    @AppStorage("videoMouseClickSize") var videoMouseClickSize: Double = 40
    @AppStorage("videoMouseClickStrokeWidth") var videoMouseClickStrokeWidth: Double = 3
    @AppStorage("videoMouseClickOpacity") var videoMouseClickOpacity: Double = 0.85
    @AppStorage("videoMouseClickDuration") var videoMouseClickDuration: Double = 0.45
    @AppStorage("gifMouseClickColorHex") var gifMouseClickColorHex: String = "#0A84FF"
    @AppStorage("gifMouseClickSize") var gifMouseClickSize: Double = 40
    @AppStorage("gifMouseClickStrokeWidth") var gifMouseClickStrokeWidth: Double = 3
    @AppStorage("gifMouseClickOpacity") var gifMouseClickOpacity: Double = 0.85
    @AppStorage("gifMouseClickDuration") var gifMouseClickDuration: Double = 0.45
    @AppStorage("showTrimmer") var showTrimmer: Bool = true
    @AppStorage("recordAudio") var recordAudio: Bool = false
    @AppStorage("recordMicrophone") var recordMicrophone: Bool = false
    @AppStorage("microphoneLimiterEnabled") var microphoneLimiterEnabled: Bool = true
    @AppStorage("windNoiseRemovalEnabled") var windNoiseRemovalEnabled: Bool = false
    @AppStorage("selectedMicrophoneID") var selectedMicrophoneID: String = ""
    @AppStorage("webcamEnabled") var webcamEnabled: Bool = false
    @AppStorage("selectedWebcamID") var selectedWebcamID: String = ""
    @AppStorage("webcamShape") var webcamShape: String = "circle"
    @AppStorage("webcamSize") var webcamSize: String = "medium"
    @AppStorage("webcamCorner") var webcamCorner: String = "bottomRight"
    // Negative values indicate no explicit corner radius/factor is set.
    @AppStorage("webcamCornerRadius") var webcamCornerRadius: Double = -1
    @AppStorage("showScreenshotEditor") var showScreenshotEditor: Bool = true
    @AppStorage("showGifTrimmer") var showGifTrimmer: Bool = true
    @AppStorage("saveImmediatelyScreenshot") var saveImmediatelyScreenshot: Bool = true
    @AppStorage("saveImmediatelyVideo") var saveImmediatelyVideo: Bool = true
    @AppStorage("saveImmediatelyGif") var saveImmediatelyGif: Bool = true
    @AppStorage("showScreenshotCapturePicker") var showScreenshotCapturePicker: Bool = true {
        didSet {
            if !showScreenshotCapturePicker {
                showScreenshotCapturePickerAfterCapture = false
            }
        }
    }
    @AppStorage("showScreenshotCapturePickerAfterCapture") var showScreenshotCapturePickerAfterCapture: Bool = false {
        didSet {
            if showScreenshotCapturePickerAfterCapture && !showScreenshotCapturePicker {
                showScreenshotCapturePickerAfterCapture = false
            }
        }
    }
    @AppStorage("showVideoCapturePicker") var showVideoCapturePicker: Bool = true {
        didSet {
            if !showVideoCapturePicker {
                showVideoCapturePickerAfterCapture = false
            }
        }
    }
    @AppStorage("showVideoCapturePickerAfterCapture") var showVideoCapturePickerAfterCapture: Bool = false {
        didSet {
            if showVideoCapturePickerAfterCapture && !showVideoCapturePicker {
                showVideoCapturePickerAfterCapture = false
            }
        }
    }
    @AppStorage("showGifCapturePicker") var showGifCapturePicker: Bool = true {
        didSet {
            if !showGifCapturePicker {
                showGifCapturePickerAfterCapture = false
            }
        }
    }
    @AppStorage("showGifCapturePickerAfterCapture") var showGifCapturePickerAfterCapture: Bool = false {
        didSet {
            if showGifCapturePickerAfterCapture && !showGifCapturePicker {
                showGifCapturePickerAfterCapture = false
            }
        }
    }
    @AppStorage("screenshotFormat") var screenshotFormat: String = ImageFormat.jpeg.rawValue
    @AppStorage("screenshotScale") var screenshotScale: Int = 100
    @AppStorage("jpegQuality") var jpegQuality: Double = 0.85
    @AppStorage("videoCountdownEnabled") var videoCountdownEnabled: Bool = true
    @AppStorage("videoCountdownDuration") var videoCountdownDuration: Int = 3
    @AppStorage("videoRecordingTimeLimitMinutes") var videoRecordingTimeLimitMinutes: Int = 0
    @AppStorage("gifCountdownEnabled") var gifCountdownEnabled: Bool = true
    @AppStorage("gifCountdownDuration") var gifCountdownDuration: Int = 3
    @AppStorage("screenshotCountdownEnabled") var screenshotCountdownEnabled: Bool = false
    @AppStorage("screenshotCountdownDuration") var screenshotCountdownDuration: Int = 3
    @AppStorage("hasCompletedOnboarding") var hasCompletedOnboarding: Bool = false
    @AppStorage("multiMonitorCaptureMode") var multiMonitorCaptureMode: MultiMonitorCaptureMode = .askEveryTime
    @AppStorage("showRegionIndicator") var showRegionIndicator: Bool = true
    @AppStorage("preventDisplaySleepWhileRecording") var preventDisplaySleepWhileRecording: Bool = true
    @AppStorage("includeTinyClipsInCapture") var includeTinyClipsInCapture: Bool = false
    @AppStorage("showBrandingOverlay") var showBrandingOverlay: Bool = false
    @AppStorage("teleprompterEnabled") var teleprompterEnabled: Bool = false
    @AppStorage("teleprompterTranscript") var teleprompterTranscript: String = ""
    @AppStorage("teleprompterScrollSpeed") var teleprompterScrollSpeed: Double = 50
    @AppStorage("teleprompterFontSize") var teleprompterFontSize: String = TeleprompterDisplaySize.medium.rawValue
    @AppStorage("teleprompterPanelHeight") var teleprompterPanelHeight: String = TeleprompterDisplaySize.medium.rawValue
    // Custom global hotkeys (stored as Carbon keyCode + modifiers bitmask).
    // Defaults: ⌃⌥⌘5 / ⌃⌥⌘6 / ⌃⌥⌘7 / ⌃⌥⌘8
    // 6400 = controlKey (4096) | optionKey (2048) | cmdKey (256)
    @AppStorage("screenshotHotKeyCode") var screenshotHotKeyCode: Int = 23      // kVK_ANSI_5
    @AppStorage("screenshotHotKeyModifiers") var screenshotHotKeyModifiers: Int = 6400
    @AppStorage("videoHotKeyCode") var videoHotKeyCode: Int = 22                // kVK_ANSI_6
    @AppStorage("videoHotKeyModifiers") var videoHotKeyModifiers: Int = 6400
    @AppStorage("gifHotKeyCode") var gifHotKeyCode: Int = 26                    // kVK_ANSI_7
    @AppStorage("gifHotKeyModifiers") var gifHotKeyModifiers: Int = 6400
    @AppStorage("copyTextFromRegionHotKeyCode") var copyTextFromRegionHotKeyCode: Int = 28 // kVK_ANSI_8
    @AppStorage("copyTextFromRegionHotKeyModifiers") var copyTextFromRegionHotKeyModifiers: Int = 6400

    func resolvedSaveDirectory(for captureType: CaptureType) -> URL {
        URL(fileURLWithPath: saveDirectoryPath(for: captureType), isDirectory: true)
    }

    static func defaultSaveDirectoryURL(for captureType: CaptureType) -> URL {
        let fallbackBase = URL(fileURLWithPath: NSHomeDirectory(), isDirectory: true)
        let searchPath: FileManager.SearchPathDirectory = captureType == .screenshot
            ? .picturesDirectory
            : .moviesDirectory
        let baseURL = FileManager.default.urls(for: searchPath, in: .userDomainMask).first ?? fallbackBase
        return baseURL.appendingPathComponent("TinyClips", isDirectory: true)
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

    func hasCustomSaveDirectory(for captureType: CaptureType) -> Bool {
#if APPSTORE
        switch captureType {
        case .screenshot:
            return !screenshotSaveDirectoryBookmark.isEmpty
        case .video:
            return !videoSaveDirectoryBookmark.isEmpty
        case .gif:
            return !gifSaveDirectoryBookmark.isEmpty
        }
#else
        return !saveDirectoryPath(for: captureType).isEmpty
#endif
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

    func resetSaveDirectory(for captureType: CaptureType?) {
        setSaveDirectoryBookmark(Data(), displayPath: "", for: captureType)
    }
#endif

    var imageFormat: ImageFormat {
        get { Self.imageFormat(from: screenshotFormat) }
        set { screenshotFormat = newValue.rawValue }
    }

    static func imageFormat(from rawValue: String) -> ImageFormat {
        ImageFormat(rawValue: rawValue) ?? .jpeg
    }

    static func hotKeyBinding(for action: HotKeyAction, defaults: UserDefaults) -> HotKeyBinding {
        let fallback = HotKeyBinding.defaultBinding(for: action)
        let keys = hotKeyDefaultsKeys(for: action)
        return HotKeyBinding(
            keyCode: defaults.object(forKey: keys.keyCode) as? Int ?? fallback.keyCode,
            carbonModifiers: defaults.object(forKey: keys.modifiers) as? Int ?? fallback.carbonModifiers
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
        case .recordGif:
            return ("gifHotKeyCode", "gifHotKeyModifiers")
        case .copyTextFromRegion:
            return ("copyTextFromRegionHotKeyCode", "copyTextFromRegionHotKeyModifiers")
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
        // Remove all keys in one pass so only a single objectWillChange fires
        let keys: [String] = [
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
            "clipsManagerAutoUploadAfterSave", "clipsManagerAutoCopyUploadLink",
            "clipsManagerLastViewMode", "clipsManagerLastSortOption", "clipsManagerLastFilterType", "clipsManagerLastDateFilter",
            "clipsManagerLastSmartCollection", "clipsManagerLastSearchText", "clipsManagerLastSelectedTag", "clipsManagerLastSelectedCollection",
            "gifFrameRate", "gifMaxWidth", "videoFrameRate", "showMouseClickVisualsInVideo", "showMouseClickVisualsInGif",
            "gifMouseClicksUseVideoSettings",
            "videoMouseClickColorHex", "videoMouseClickSize", "videoMouseClickStrokeWidth", "videoMouseClickOpacity", "videoMouseClickDuration",
            "gifMouseClickColorHex", "gifMouseClickSize", "gifMouseClickStrokeWidth", "gifMouseClickOpacity", "gifMouseClickDuration",
            "showTrimmer",
            "recordAudio", "recordMicrophone", "microphoneLimiterEnabled", "windNoiseRemovalEnabled", "selectedMicrophoneID",
            "webcamEnabled", "selectedWebcamID", "webcamShape", "webcamSize", "webcamCorner", "webcamCornerRadius",
            "showScreenshotEditor", "showGifTrimmer",
            "saveImmediatelyScreenshot", "saveImmediatelyVideo", "saveImmediatelyGif",
            "showScreenshotCapturePicker", "showScreenshotCapturePickerAfterCapture",
            "showVideoCapturePicker", "showVideoCapturePickerAfterCapture",
            "showGifCapturePicker", "showGifCapturePickerAfterCapture",
            "screenshotFormat", "screenshotScale", "jpegQuality",
            "videoCountdownEnabled", "videoCountdownDuration",
            "videoRecordingTimeLimitMinutes",
            "gifCountdownEnabled", "gifCountdownDuration",
            "screenshotCountdownEnabled", "screenshotCountdownDuration",
            "hasCompletedOnboarding", "alwaysCaptureMainDisplay", "multiMonitorCaptureMode", "showRegionIndicator",
            "includeTinyClipsInCapture", "showBrandingOverlay",
            "teleprompterEnabled", "teleprompterTranscript", "teleprompterScrollSpeed", "teleprompterFontSize", "teleprompterPanelHeight",
            "teleprompterPanelX", "teleprompterPanelY",
            "appStoreClipCountForReview", "appStoreReviewRequested"
        ]
        let hotKeyKeys = [
            "screenshotHotKeyCode", "screenshotHotKeyModifiers",
            "videoHotKeyCode", "videoHotKeyModifiers",
            "gifHotKeyCode", "gifHotKeyModifiers",
            "copyTextFromRegionHotKeyCode", "copyTextFromRegionHotKeyModifiers"
        ]
#if APPSTORE
        let masKeys: [String] = [
            "saveDirectoryBookmark", "saveDirectoryDisplayPath",
            "screenshotSaveDirectoryBookmark", "screenshotSaveDirectoryDisplayPath",
            "videoSaveDirectoryBookmark", "videoSaveDirectoryDisplayPath",
            "gifSaveDirectoryBookmark", "gifSaveDirectoryDisplayPath",
            "videoGifSaveDirectoryBookmark", "videoGifSaveDirectoryDisplayPath"
        ]
#else
        let masKeys: [String] = []
#endif
        for key in keys + (preservingHotKeys ? [] : hotKeyKeys) + masKeys {
            UserDefaults.standard.removeObject(forKey: key)
        }
        screenshotSaveDirectory = Self.defaultSaveDirectoryURL(for: .screenshot).path
        videoSaveDirectory = Self.defaultSaveDirectoryURL(for: .video).path
        gifSaveDirectory = Self.defaultSaveDirectoryURL(for: .gif).path
#if APPSTORE
        screenshotSaveDirectoryDisplayPath = Self.defaultSaveDirectoryURL(for: .screenshot).path
        videoSaveDirectoryDisplayPath = Self.defaultSaveDirectoryURL(for: .video).path
        gifSaveDirectoryDisplayPath = Self.defaultSaveDirectoryURL(for: .gif).path
#endif
#if APPSTORE
        SaveService.shared.invalidateAllSaveDirectoryBookmarks()
#endif
        UploadcareCredentialsStore.shared.clearAll()
        Task { @MainActor in
            CaptureAnalyticsStore.shared.clear()
        }
        objectWillChange.send()
    }

    init(defaults: UserDefaults = .standard, performMigrations: Bool = true) {
        self.defaults = defaults
        guard performMigrations else { return }

        migrateSaveDirectorySettings(defaults)
        ensureSaveDirectoryDefaults(defaults)

        guard defaults.object(forKey: "multiMonitorCaptureMode") == nil else { return }

        multiMonitorCaptureMode = defaults.bool(forKey: "alwaysCaptureMainDisplay")
            ? .mainDisplay
            : .askEveryTime
        defaults.removeObject(forKey: "alwaysCaptureMainDisplay")
    }

    private func migrateSaveDirectorySettings(_ defaults: UserDefaults) {
        guard defaults.object(forKey: "useDefaultSaveDirectories") == nil else { return }

        let legacySharedDirectory = defaults.string(forKey: "saveDirectory") ?? ""
        let legacyVideoGifDirectory = defaults.string(forKey: "videoGifSaveDirectory") ?? ""
        let legacyScreenshotDirectory = defaults.string(forKey: "screenshotSaveDirectory") ?? ""
        let hasCustomDirectory = !legacySharedDirectory.isEmpty ||
            !legacyScreenshotDirectory.isEmpty ||
            !legacyVideoGifDirectory.isEmpty
#if APPSTORE
        let hasCustomBookmark = !saveDirectoryBookmark.isEmpty ||
            !screenshotSaveDirectoryBookmark.isEmpty ||
            !videoGifSaveDirectoryBookmark.isEmpty
#else
        let hasCustomBookmark = false
#endif

        guard hasCustomDirectory || hasCustomBookmark else { return }

        useDefaultSaveDirectories = false
        if legacyScreenshotDirectory.isEmpty {
            screenshotSaveDirectory = legacySharedDirectory
        } else {
            screenshotSaveDirectory = legacyScreenshotDirectory
        }
        videoSaveDirectory = legacyVideoGifDirectory.isEmpty ? legacySharedDirectory : legacyVideoGifDirectory
        gifSaveDirectory = legacyVideoGifDirectory.isEmpty ? legacySharedDirectory : legacyVideoGifDirectory

#if APPSTORE
        if screenshotSaveDirectoryBookmark.isEmpty, !saveDirectoryBookmark.isEmpty {
            screenshotSaveDirectoryBookmark = saveDirectoryBookmark
            screenshotSaveDirectoryDisplayPath = saveDirectoryDisplayPath
        }
        let videoGifBookmark = videoGifSaveDirectoryBookmark.isEmpty ? saveDirectoryBookmark : videoGifSaveDirectoryBookmark
        let videoGifDisplayPath = videoGifSaveDirectoryDisplayPath.isEmpty ? saveDirectoryDisplayPath : videoGifSaveDirectoryDisplayPath
        videoSaveDirectoryBookmark = videoGifBookmark
        videoSaveDirectoryDisplayPath = videoGifDisplayPath
        gifSaveDirectoryBookmark = videoGifBookmark
        gifSaveDirectoryDisplayPath = videoGifDisplayPath
#endif
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
