import SwiftUI
import ScreenCaptureKit
import AVFoundation
import Combine

enum TinyClipsTemporaryFiles {
    private static let filenamePrefix = "TinyClips-"

    struct Summary: Sendable {
        let fileCount: Int
        let totalSize: Int64
    }

    static var directoryURL: URL {
        FileManager.default.temporaryDirectory
    }

    static func makeURL(fileExtension: String) -> URL {
        directoryURL
            .appendingPathComponent("\(filenamePrefix)\(UUID().uuidString)")
            .appendingPathExtension(fileExtension)
    }

    @discardableResult
    static func removeStaleFiles(olderThan date: Date) throws -> Int {
        try removeMatchingFiles { fileURL in
            guard let values = try? fileURL.resourceValues(forKeys: [.contentModificationDateKey]),
                  let modificationDate = values.contentModificationDate else {
                return false
            }
            return modificationDate < date
        }
    }

    @discardableResult
    static func purge() throws -> Int {
        try removeMatchingFiles { _ in true }
    }

    static func summary() throws -> Summary {
        let fileManager = FileManager.default
        let files = try fileManager.contentsOfDirectory(
            at: directoryURL,
            includingPropertiesForKeys: [.isRegularFileKey, .fileSizeKey],
            options: [.skipsHiddenFiles]
        )

        var fileCount = 0
        var totalSize: Int64 = 0
        for fileURL in files {
            guard isTinyClipsTemporaryFile(fileURL),
                  let values = try? fileURL.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey]),
                  values.isRegularFile == true else {
                continue
            }
            fileCount += 1
            totalSize += Int64(values.fileSize ?? 0)
        }
        return Summary(fileCount: fileCount, totalSize: totalSize)
    }

    private static func removeMatchingFiles(shouldRemove: (URL) -> Bool) throws -> Int {
        let fileManager = FileManager.default
        let files = try fileManager.contentsOfDirectory(
            at: directoryURL,
            includingPropertiesForKeys: [.isRegularFileKey],
            options: [.skipsHiddenFiles]
        )

        var removedCount = 0
        for fileURL in files {
            guard isTinyClipsTemporaryFile(fileURL),
                  let values = try? fileURL.resourceValues(forKeys: [.isRegularFileKey]),
                  values.isRegularFile == true,
                  shouldRemove(fileURL) else {
                continue
            }
            try fileManager.removeItem(at: fileURL)
            removedCount += 1
        }
        return removedCount
    }

    private static func isTinyClipsTemporaryFile(_ fileURL: URL) -> Bool {
        guard !fileURL.pathExtension.isEmpty else { return false }

        let stem = fileURL.deletingPathExtension().lastPathComponent
        guard stem.hasPrefix(filenamePrefix) else { return false }

        let identifier = String(stem.dropFirst(filenamePrefix.count))
        if UUID(uuidString: identifier) != nil {
            return true
        }

        let webcamSuffix = "-webcam"
        guard identifier.hasSuffix(webcamSuffix) else { return false }
        return UUID(uuidString: String(identifier.dropLast(webcamSuffix.count))) != nil
    }
}

struct VideoRecordingArtifacts {
    let screenRecordingURL: URL
    let webcamRecordingURL: URL?
}

private enum ActiveRecordingRequest {
    case video(
        target: CaptureTarget,
        systemAudio: Bool,
        microphone: Bool,
        selectedMicrophoneID: String,
        webcamSelection: StartRecordingPanel.WebcamSelection,
        mouseClicksEnabled: Bool,
        timeLimitMinutes: Int
    )
    case gif(target: CaptureTarget, mouseClicksEnabled: Bool)
}

@MainActor
class CaptureManager: ObservableObject {
    @Published var isRecording = false {
        didSet {
            updateStopHotKeyRegistration()
        }
    }
    @Published var isRecordingPaused = false
    @Published var recordingSystemAudioEnabled = false
    @Published var recordingMicrophoneEnabled = false
    @Published var isRecordingSystemAudioMuted = false
    @Published var isRecordingMicrophoneMuted = false
    @Published var activeMicrophoneName: String?
    @Published var activeWebcamName: String?
    @Published var microphoneLevel: Double = 0
    @Published var microphoneWarningMessage: String?
    @Published private(set) var isOCRInFlight = false
    @Published private(set) var isCapturePreparationInProgress = false
    @Published private(set) var isScreenshotCaptureInProgress = false
    @Published private(set) var isRecordingSetupInProgress = false
    @Published private(set) var captureHotKeyRegistrationError: String?
    @Published private(set) var stopHotKeyRegistrationError: String?

    var hotKeyRegistrationError: String? {
        captureHotKeyRegistrationError ?? stopHotKeyRegistrationError
    }

    var isCaptureActionInProgress: Bool {
        isOCRInFlight
            || isCapturePreparationInProgress
            || isScreenshotCaptureInProgress
            || isRecordingSetupInProgress
            || RegionSelector.isSelecting
            || WindowSelector.isSelecting
            || screenshotPickerPanel != nil
            || recordingPickerPanel != nil
            || startPanel != nil
            || countdownWindow != nil
            || screenPickerWindow != nil
            || scrollingCapturePanel != nil
    }

    private var videoRecorder: VideoRecorder?
    private var webcamRecorder: WebcamRecorder?
    private var gifWriter: GifWriter?
    private let idleSleepAssertion = IdleSleepAssertion()
    private(set) var lastVideoRecordingArtifacts: VideoRecordingArtifacts?
    @Published private var screenshotPickerPanel: CapturePickerPanel?
    private var screenshotPickerPosition: NSPoint?
    @Published private var recordingPickerPanel: CapturePickerPanel?
    private var recordingPickerPosition: NSPoint?
    private var shouldReturnToPickerAfterRecording = false
    @Published private var startPanel: StartRecordingPanel?
    private var stopPanel: StopRecordingPanel?
    private var teleprompterPanel: TeleprompterPanel?
    private var webcamPreviewPanel: WebcamPreviewPanel?
    private var regionIndicatorPanel: RegionIndicatorPanel?
    private var pendingRecordingTarget: CaptureTarget?
    private var pendingRecordingType: CaptureType?
    private var pendingRecordingCountdownEnabled: Bool = true
    private var pendingRecordingCountdownDuration: Int = 3
    private var pendingVideoTimeLimitMinutes: Int = 0
    private var activeRecordingRegion: CaptureRegion?
    private var recordPanelPosition: NSPoint?
    private var trimmerWindow: VideoTrimmerWindow?
    private var gifTrimmerWindow: GifTrimmerWindow?
    @Published private var countdownWindow: CountdownWindow?
    private var processingIndicatorWindow: ProcessingIndicatorWindow?
    @Published private var scrollingCapturePanel: ScrollingCapturePanel?
    private var scrollingCaptureSession: ScrollingPanoramaCapture?
    private var processingIndicatorShownAt: Date?
    private var isStoppingRecording = false
    private var stopRecordingTask: Task<Void, Never>?
    private var videoAutoStopTask: Task<Void, Never>?
    private var recordingSessionCounter: UInt64 = 0
    private var activeRecordingSessionID: UInt64?
    private var finalizingSessionID: UInt64?
    private var activeRecordingRequest: ActiveRecordingRequest?
    private var onboardingWindow: OnboardingWizardWindow?
    private var guideWindow: GuideWindow?
    @Published private var screenPickerWindow: ScreenPickerWindow?
    private var mouseClickMonitor: MouseClickMonitor?
    private var activeMouseClickRegion: CaptureRegion?
    private var activeMouseClickCaptureType: CaptureType?
    private var activeMouseClickCaptureEnabledOverride: Bool?
    private var activeWebcamOverlaySelection: StartRecordingPanel.WebcamSelection?
    private var webcamPositionEvents: [BrandingOverlayProcessor.WebcamPositionEvent] = []
    private let hotKeyManager = HotKeyManager()
    private var hotKeySettingsCancellable: AnyCancellable?
    private var shouldIgnoreNextHotKeySettingsChange = false

    init() {
        guard !TinyClipsRuntime.isRunningUnitTests else { return }

        configureGlobalHotKeys()

        // Re-register capture hotkeys whenever shortcut settings change.
        hotKeySettingsCancellable = CaptureSettings.shared.objectWillChange
            .debounce(for: .milliseconds(100), scheduler: DispatchQueue.main)
            .sink { [weak self] in
                guard let self else { return }
                if self.shouldIgnoreNextHotKeySettingsChange {
                    self.shouldIgnoreNextHotKeySettingsChange = false
                    return
                }
                self.configureGlobalHotKeys()
            }

        DispatchQueue.main.async { [weak self] in
            self?.showOnboardingIfNeeded()
        }
    }

    private func bringWindowToFront(_ window: NSWindow) {
        window.collectionBehavior.insert(.moveToActiveSpace)

        NSRunningApplication.current.activate(options: [.activateAllWindows])
        window.makeKeyAndOrderFront(nil)
        window.orderFrontRegardless()

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
            NSRunningApplication.current.activate(options: [.activateAllWindows])
            window.makeKeyAndOrderFront(nil)
            window.orderFrontRegardless()
        }
    }

    private func configureGlobalHotKeys() {
        let result = registerCaptureHotKeys(CaptureSettings.shared.hotKeyBindings)
        captureHotKeyRegistrationError = result.errorMessage

        updateStopHotKeyRegistration()
    }

    @discardableResult
    func applyCaptureHotKey(_ binding: HotKeyBinding, for action: HotKeyAction) -> String? {
        let settings = CaptureSettings.shared
        var bindings = settings.hotKeyBindings
        guard let currentBinding = bindings[action] else { return nil }

        if let validationError = HotKeyBinding.validationError(
            for: binding,
            action: action,
            bindings: bindings
        ) {
            captureHotKeyRegistrationError = validationError
            return validationError
        }

        if binding == currentBinding {
            let result = registerCaptureHotKeys(bindings)
            captureHotKeyRegistrationError = result.errorMessage
            return result.errorMessage
        }

        bindings[action] = binding
        let result = registerCaptureHotKeys(bindings)
        guard let registrationError = result.errorMessage else {
            persistCaptureHotKeys(bindings, settings: settings)
            captureHotKeyRegistrationError = nil
            return nil
        }

        captureHotKeyRegistrationError = registrationError
        return registrationError
    }

    @discardableResult
    func resetCaptureHotKeysToDefaults() -> String? {
        let bindings = Dictionary(
            uniqueKeysWithValues: HotKeyAction.allCases.map {
                ($0, HotKeyBinding.defaultBinding(for: $0))
            }
        )
        let result = registerCaptureHotKeys(bindings)

        guard let registrationError = result.errorMessage else {
            persistCaptureHotKeys(bindings, settings: CaptureSettings.shared)
            captureHotKeyRegistrationError = nil
            return nil
        }

        captureHotKeyRegistrationError = registrationError
        return registrationError
    }

    private func persistCaptureHotKeys(
        _ bindings: [HotKeyAction: HotKeyBinding],
        settings: CaptureSettings
    ) {
        shouldIgnoreNextHotKeySettingsChange = true
        for action in HotKeyAction.allCases {
            guard let binding = bindings[action] else { continue }
            settings.setHotKeyBinding(binding, for: action)
        }
    }

    private func registerCaptureHotKeys(
        _ bindings: [HotKeyAction: HotKeyBinding]
    ) -> HotKeyManager.RegistrationResult {
        let registrations = HotKeyAction.allCases.compactMap { action -> HotKeyManager.ActionRegistration? in
            guard let binding = bindings[action] else { return nil }
            return HotKeyManager.ActionRegistration(
                action: action,
                binding: binding,
                handler: { [weak self] in
                    guard let self, !self.isRecording, !self.isCaptureActionInProgress else { return }
                    switch action {
                    case .screenshot:
                        self.takeScreenshot()
                    case .recordVideo:
                        self.startVideoRecording()
                    case .recordGif:
                        self.startGifRecording()
                    case .copyTextFromRegion:
                        self.copyTextFromRegion()
                    }
                }
            )
        }
        return hotKeyManager.registerActionHotKeys(registrations)
    }

    private func updateStopHotKeyRegistration() {
        if isRecording {
            let result = hotKeyManager.registerStopHotKey { [weak self] in
                guard let self, self.isRecording else { return }
                self.stopRecording()
            }
            stopHotKeyRegistrationError = result.errorMessage
        } else {
            hotKeyManager.unregisterStopHotKey()
            stopHotKeyRegistrationError = nil
        }
    }

    func takeScreenshot() {
        guard !isCaptureActionInProgress else { return }

        isCapturePreparationInProgress = true
        let cursorScreen = screenUnderMouseCursor()
        Task {
            defer { isCapturePreparationInProgress = false }
            guard await prepareForNewCaptureRequest() else { return }
            guard await PermissionManager.shared.checkPermission() else { return }
            guard !isOCRInFlight else { return }
            let settings = CaptureSettings.shared
            if settings.shouldShowCapturePicker(for: .screenshot) {
                showScreenshotPicker(cursorScreen: cursorScreen)
            } else {
                await performScreenshotCapture(
                    mode: .region,
                    countdownEnabled: settings.screenshotCountdownEnabled,
                    countdownDuration: settings.screenshotCountdownDuration,
                    shouldReturnToPicker: false,
                    cursorScreen: cursorScreen
                )
            }
        }
    }

    func copyTextFromRegion() {
        guard !isRecording, !isCaptureActionInProgress else { return }

        isOCRInFlight = true
        Task {
            defer {
                dismissProcessingIndicator()
                isOCRInFlight = false
            }

            guard await prepareForNewCaptureRequest(allowOCRInFlight: true) else { return }
            guard await PermissionManager.shared.checkPermission() else { return }
            guard let region = await RegionSelector.selectRegion() else { return }

            do {
                let image = try await ScreenshotCapture.captureImage(region: region)
                AccessibilityAnnouncementService.shared.announce(
                    "Recognizing text from selected region.",
                    priority: .high
                )
                showProcessingIndicator(message: "Copying Text...", status: "Recognizing text...")
                updateProcessingProgress(0.5, status: "Recognizing text...")

                switch try await TextRecognitionService.recognizeText(in: image) {
                case .success(let text):
                    guard SaveService.shared.copyTextToClipboard(text) else {
                        SaveService.shared.showError("Could not copy recognized text to the clipboard.")
                        return
                    }
                    AccessibilityAnnouncementService.shared.announce(
                        "Text copied to clipboard.",
                        priority: .medium
                    )
                case .noTextFound:
                    SaveService.shared.showError("No text found in the selected region.")
                }
            } catch {
                SaveService.shared.showError("Text recognition failed: \(error.localizedDescription)")
            }
        }
    }

    private func showScreenshotPicker(cursorScreen: NSScreen? = nil) {
        if screenshotPickerPanel != nil {
            return
        }
        dismissRecordingPicker()
        let settings = CaptureSettings.shared
        let panel = CapturePickerPanel(
            captureType: .screenshot,
            countdownEnabled: settings.screenshotCountdownEnabled,
            countdownDuration: settings.screenshotCountdownDuration,
            onCapture: { [weak self] mode, countdownEnabled, countdownDuration, _ in
                guard let self else { return }
                self.dismissScreenshotPicker()
                Task {
                    await self.performScreenshotCapture(
                        mode: mode,
                        countdownEnabled: countdownEnabled,
                        countdownDuration: countdownDuration,
                        shouldReturnToPicker: true,
                        cursorScreen: cursorScreen
                    )
                }
            },
            onCancel: { [weak self] in
                self?.dismissScreenshotPicker()
            }
        )
        panel.show(at: screenshotPickerPosition)
        self.screenshotPickerPanel = panel
    }

    private func dismissScreenshotPicker() {
        if let panel = screenshotPickerPanel {
            screenshotPickerPosition = panel.frame.origin
        }
        screenshotPickerPanel?.dismiss()
        screenshotPickerPanel = nil
    }

    private func performScreenshotCapture(
        mode: CapturePickerMode,
        countdownEnabled: Bool,
        countdownDuration: Int,
        shouldReturnToPicker: Bool,
        cursorScreen: NSScreen? = nil
    ) async {
        let ownsPreparationState = !isCapturePreparationInProgress
        if ownsPreparationState {
            isCapturePreparationInProgress = true
        }
        defer {
            if ownsPreparationState {
                isCapturePreparationInProgress = false
            }
        }

        switch mode {
        case .region:
            guard let region = await RegionSelector.selectRegion() else {
                if shouldReturnToPicker {
                    showScreenshotPicker(cursorScreen: cursorScreen)
                }
                return
            }
            doScreenshotCapture(
                region: region,
                window: nil,
                countdownEnabled: countdownEnabled,
                countdownDuration: countdownDuration,
                shouldReturnToPickerAfterCapture: shouldReturnToPicker
            )

        case .screen:
            let screen = await chooseScreenForCapture(cursorScreen: cursorScreen)
            guard let screen, let region = CaptureRegion.fullScreen(for: screen) else {
                if shouldReturnToPicker {
                    showScreenshotPicker(cursorScreen: cursorScreen)
                }
                return
            }
            doScreenshotCapture(
                region: region,
                window: nil,
                countdownEnabled: countdownEnabled,
                countdownDuration: countdownDuration,
                shouldReturnToPickerAfterCapture: shouldReturnToPicker
            )

        case .window:
            guard let window = await WindowSelector.selectWindow() else {
                if shouldReturnToPicker {
                    showScreenshotPicker(cursorScreen: cursorScreen)
                }
                return
            }
            doScreenshotCapture(
                region: nil,
                window: window,
                countdownEnabled: countdownEnabled,
                countdownDuration: countdownDuration,
                shouldReturnToPickerAfterCapture: shouldReturnToPicker
            )

        case .scrolling:
            guard let region = await RegionSelector.selectRegion() else {
                if shouldReturnToPicker {
                    showScreenshotPicker(cursorScreen: cursorScreen)
                }
                return
            }
            startScrollingCapture(region: region, shouldReturnToPickerAfterCapture: shouldReturnToPicker)
        }
    }

    private func startScrollingCapture(region: CaptureRegion, shouldReturnToPickerAfterCapture: Bool) {
        guard scrollingCaptureSession == nil else { return }
        isScreenshotCaptureInProgress = true
        AccessibilityAnnouncementService.shared.announce(
            "Scrolling capture started. Scroll the page, then press Return to finish.",
            priority: .high
        )

        let session = ScrollingPanoramaCapture()
        scrollingCaptureSession = session
        session.onFailure = { [weak self, weak session] error in
            Task { @MainActor [weak self, weak session] in
                guard let self, self.scrollingCaptureSession === session else { return }
                session?.cancel()
                self.finishScrollingCapture(with: error, shouldReturnToPicker: shouldReturnToPickerAfterCapture)
            }
        }
        let panel = ScrollingCapturePanel(
            onStop: { [weak self, weak session] in
                guard let self, let session else { return }
                Task {
                    do {
                        let image = try await session.stop()
                        await self.finishScrollingCapture(image: image, shouldReturnToPicker: shouldReturnToPickerAfterCapture)
                    } catch {
                        await self.finishScrollingCapture(with: error, shouldReturnToPicker: shouldReturnToPickerAfterCapture)
                    }
                }
            },
            onCancel: { [weak self, weak session] in
                session?.cancel()
                self?.finishScrollingCapture(with: PanoramaCaptureError.cancelled, shouldReturnToPicker: shouldReturnToPickerAfterCapture)
            }
        )
        scrollingCapturePanel = panel
        panel.show()

        Task {
            do {
                try await session.start(region: region)
            } catch {
                await self.finishScrollingCapture(with: error, shouldReturnToPicker: shouldReturnToPickerAfterCapture)
            }
        }
    }

    @MainActor
    private func finishScrollingCapture(
        image: CGImage? = nil,
        with error: Error? = nil,
        shouldReturnToPicker: Bool
    ) {
        scrollingCapturePanel?.dismiss()
        scrollingCapturePanel = nil
        scrollingCaptureSession = nil
        isScreenshotCaptureInProgress = false

        if let error {
            let wasCancelled: Bool
            if let panoramaError = error as? PanoramaCaptureError {
                if case .cancelled = panoramaError {
                    wasCancelled = true
                } else {
                    wasCancelled = false
                }
            } else {
                wasCancelled = false
            }
            if !wasCancelled {
                SaveService.shared.showError("Scrolling capture failed: \(error.localizedDescription)")
            }
            if shouldReturnToPicker,
               CaptureSettings.shared.shouldShowCapturePickerAfterCapture(for: .screenshot) {
                showScreenshotPicker()
            }
            return
        }
        guard let image else { return }

        Task {
            do {
                let settings = CaptureSettings.shared
                let shouldSaveImmediately = !settings.showScreenshotEditor || settings.saveImmediatelyScreenshot
                let outputURL = shouldSaveImmediately
                    ? SaveService.shared.generateURL(for: .screenshot)
                    : TinyClipsTemporaryFiles.makeURL(fileExtension: settings.imageFormat.rawValue)
                let url = try ScreenshotCapture.saveImage(image, to: outputURL)
                CaptureAnalyticsStore.shared.recordCapture(.screenshot)
                if settings.showScreenshotEditor {
                    if shouldSaveImmediately {
                        SaveService.shared.handleSavedFile(url: url, type: .screenshot)
                    }
                    let initialSaveURL = shouldSaveImmediately
                        ? url
                        : SaveService.shared.generateURL(for: .screenshot, fileExtension: settings.imageFormat.rawValue)
                    showScreenshotEditor(
                        for: url,
                        initialSaveURL: initialSaveURL,
                        deleteSourceOnCancel: !shouldSaveImmediately,
                        reopenPickerAfterClose: shouldReturnToPicker
                    )
                } else {
                    SaveService.shared.handleSavedFile(url: url, type: .screenshot)
                }
            } catch {
                SaveService.shared.showError("Scrolling capture failed: \(error.localizedDescription)")
            }
            if shouldReturnToPicker,
               CaptureSettings.shared.shouldShowCapturePickerAfterCapture(for: .screenshot) {
                showScreenshotPicker()
            }
        }
    }

    private func doScreenshotCapture(
        region: CaptureRegion?,
        window: SCWindow?,
        countdownEnabled: Bool,
        countdownDuration: Int,
        shouldReturnToPickerAfterCapture: Bool
    ) {
        let doCapture = { [weak self] in
            guard let self else { return }
            self.dismissRegionIndicator()
            self.isScreenshotCaptureInProgress = true
            AccessibilityAnnouncementService.shared.announceCaptureStart(
                for: .screenshot,
                countdownCompleted: countdownEnabled
            )
            Task {
                defer { self.isScreenshotCaptureInProgress = false }
                var didPresentEditor = false
                do {
                    let settings = CaptureSettings.shared
                    let shouldSaveImmediately = !settings.showScreenshotEditor || settings.saveImmediatelyScreenshot
                    let outputURL: URL = shouldSaveImmediately
                        ? SaveService.shared.generateURL(for: .screenshot)
                        : TinyClipsTemporaryFiles.makeURL(fileExtension: settings.imageFormat.rawValue)

                    let url: URL
                    if let window {
                        url = try await ScreenshotCapture.captureWindow(window, outputURL: outputURL)
                    } else if let region {
                        url = try await ScreenshotCapture.capture(region: region, outputURL: outputURL)
                    } else {
                        return
                    }

                    CaptureAnalyticsStore.shared.recordCapture(.screenshot)

                    if settings.showScreenshotEditor {
                        if shouldSaveImmediately {
                            SaveService.shared.handleSavedFile(url: url, type: .screenshot)
                        }
                        let initialSaveURL = shouldSaveImmediately
                            ? url
                            : SaveService.shared.generateURL(
                                for: .screenshot,
                                fileExtension: settings.imageFormat.rawValue
                            )
                        self.showScreenshotEditor(
                            for: url,
                            initialSaveURL: initialSaveURL,
                            deleteSourceOnCancel: !shouldSaveImmediately,
                            reopenPickerAfterClose: shouldReturnToPickerAfterCapture
                        )
                        didPresentEditor = true
                    } else {
                        SaveService.shared.handleSavedFile(url: url, type: .screenshot)
                    }
                } catch {
                    SaveService.shared.showError("Screenshot failed: \(error.localizedDescription)")
                }
                if !didPresentEditor,
                   shouldReturnToPickerAfterCapture,
                   CaptureSettings.shared.shouldShowCapturePickerAfterCapture(for: .screenshot) {
                    self.showScreenshotPicker()
                }
            }
        }
        if let region,
           countdownEnabled,
           CaptureSettings.shared.showRegionIndicator {
            let panel = RegionIndicatorPanel(region: region)
            panel.show()
            self.regionIndicatorPanel = panel
        }
        guard countdownEnabled else {
            doCapture()
            return
        }
        let window = CountdownWindow(duration: countdownDuration) { [weak self] in
            self?.countdownWindow = nil
            doCapture()
        }
        self.countdownWindow = window
        window.show()
    }

    func startVideoRecording() {
        guard !isCaptureActionInProgress else { return }

        isCapturePreparationInProgress = true
        let cursorScreen = screenUnderMouseCursor()
        Task {
            defer { isCapturePreparationInProgress = false }
            guard await prepareForNewCaptureRequest() else { return }
            guard await PermissionManager.shared.checkPermission() else { return }
            guard !isOCRInFlight else { return }
            let settings = CaptureSettings.shared
            if settings.shouldShowCapturePicker(for: .video) {
                showRecordingPicker(for: .video, cursorScreen: cursorScreen)
            } else {
                await performRecordingSetup(
                    type: .video,
                    mode: .region,
                    countdownEnabled: settings.videoCountdownEnabled,
                    countdownDuration: settings.videoCountdownDuration,
                    videoTimeLimitMinutes: settings.videoRecordingTimeLimitMinutes,
                    shouldReturnToPicker: false,
                    cursorScreen: cursorScreen
                )
            }
        }
    }

    private func beginVideoRecording(
        target: CaptureTarget,
        systemAudio: Bool,
        microphone: Bool,
        selectedMicrophoneID: String,
        webcamSelection: StartRecordingPanel.WebcamSelection,
        mouseClicksEnabled: Bool,
        timeLimitMinutes: Int,
        countdownEnabled: Bool,
        countdownDuration: Int
    ) {
        let settings = CaptureSettings.shared

        let doRecord = { [weak self] in
            guard let self else { return }
            self.isRecordingSetupInProgress = true
            AccessibilityAnnouncementService.shared.announceCaptureStart(
                for: .video,
                countdownCompleted: countdownEnabled
            )
            Task {
                defer {
                    if !self.isRecording {
                        self.isRecordingSetupInProgress = false
                    }
                }
                let shouldSaveImmediately = !settings.showTrimmer || settings.saveImmediatelyVideo
                let url = shouldSaveImmediately
                    ? SaveService.shared.generateURL(for: .video)
                    : TinyClipsTemporaryFiles.makeURL(fileExtension: CaptureType.video.fileExtension)
                let webcamEnabled = webcamSelection.enabled
                let webcamOutputURL = webcamEnabled ? self.webcamCompanionURL(for: url) : nil
                self.activeWebcamOverlaySelection = webcamEnabled ? webcamSelection : nil
                self.webcamPositionEvents = webcamEnabled
                    ? [.init(time: .zero, corner: webcamSelection.corner)]
                    : []

                let recorder = VideoRecorder()
                let webcamRecorder = WebcamRecorder()
                do {
                    let sessionID = self.nextRecordingSessionID()
                    self.activeRecordingSessionID = sessionID
                    self.debugRecordingLifecycle("Starting video session \(sessionID)")
                    recorder.onStreamFailure = { [weak self] error in
                        let message = error.localizedDescription
                        Task { @MainActor [weak self] in
                            self?.handleStreamFailure(
                                for: sessionID,
                                type: .video,
                                message: message
                            )
                        }
                    }
                    recorder.onMicrophoneLevel = { [weak self] level in
                        DispatchQueue.main.async {
                            self?.microphoneLevel = level
                        }
                    }
                    recorder.onMicrophoneWarning = { [weak self] warning in
                        DispatchQueue.main.async {
                            self?.microphoneWarningMessage = warning
                        }
                    }
                    recorder.onMicrophoneDeviceName = { [weak self] name in
                        DispatchQueue.main.async {
                            self?.activeMicrophoneName = name.isEmpty ? nil : name
                        }
                    }
                    recorder.onMicrophoneError = { [weak self] message in
                        DispatchQueue.main.async {
                            self?.microphoneWarningMessage = message
                            SaveService.shared.showError("Microphone error: \(message)")
                        }
                    }
                    webcamRecorder.onWebcamDeviceName = { [weak self] name in
                        DispatchQueue.main.async {
                            self?.activeWebcamName = name.isEmpty ? nil : name
                        }
                    }
                    webcamRecorder.onWebcamError = { message in
                        DispatchQueue.main.async {
                            SaveService.shared.showError("Webcam error: \(message)")
                        }
                    }

                    self.videoRecorder = recorder
                    self.webcamRecorder = nil
                    self.activeRecordingRegion = target.region
                    self.isRecording = true
                    self.isRecordingSetupInProgress = false
                    self.isRecordingPaused = false
                    self.activeRecordingRequest = .video(
                        target: target,
                        systemAudio: systemAudio,
                        microphone: microphone,
                        selectedMicrophoneID: selectedMicrophoneID,
                        webcamSelection: webcamSelection,
                        mouseClicksEnabled: mouseClicksEnabled,
                        timeLimitMinutes: timeLimitMinutes
                    )
                    self.activeMouseClickCaptureEnabledOverride = mouseClicksEnabled
                    self.startMouseClickMonitoringIfNeeded(for: .video, region: target.region)
                    self.recordingMicrophoneEnabled = false
                    self.recordingSystemAudioEnabled = false
                    self.isRecordingSystemAudioMuted = false
                    self.isRecordingMicrophoneMuted = false
                    self.microphoneWarningMessage = nil
                    self.microphoneLevel = 0
                    self.activeMicrophoneName = nil
                    self.activeWebcamName = nil
                    self.lastVideoRecordingArtifacts = nil

                    let teleprompterWindow = await self.prepareTeleprompterIfNeeded(
                        region: target.region,
                        sessionID: sessionID
                    )
                    guard self.isRecording,
                          self.activeRecordingSessionID == sessionID,
                          self.videoRecorder === recorder else {
                        return
                    }
                    try await recorder.start(
                        target: target,
                        alwaysExcludedWindows: teleprompterWindow.map { [$0] } ?? [],
                        outputURL: url,
                        recordSystemAudio: systemAudio,
                        recordMicrophone: microphone,
                        selectedMicrophoneID: selectedMicrophoneID
                    )
                    guard self.isRecording,
                          self.activeRecordingSessionID == sessionID,
                          self.videoRecorder === recorder else {
                        return
                    }
                    if CaptureSettings.shared.preventDisplaySleepWhileRecording {
                        try self.idleSleepAssertion.begin()
                    }
                    self.debugRecordingLifecycle("Video session \(sessionID) started")
                    self.recordingSystemAudioEnabled = recorder.isSystemAudioCaptureActive
                    self.recordingMicrophoneEnabled = recorder.isMicrophoneCaptureActive
                    self.teleprompterPanel?.reveal()
                    if self.isRecordingPaused {
                        self.teleprompterPanel?.pause()
                    } else {
                        self.teleprompterPanel?.resume()
                    }

                    if webcamEnabled, let webcamOutputURL {
                        do {
                            try await webcamRecorder.start(
                                outputURL: webcamOutputURL,
                                selectedWebcamID: webcamSelection.deviceID
                            )
                            guard self.activeRecordingSessionID == sessionID else {
                                await webcamRecorder.cancel()
                                return
                            }
                            self.webcamRecorder = webcamRecorder
                            self.showWebcamPreview(
                                session: webcamRecorder.previewSession,
                                selection: webcamSelection,
                                region: target.region
                            )
                            self.debugRecordingLifecycle("Webcam session started at \(webcamOutputURL.path)")
                        } catch {
                            guard self.activeRecordingSessionID == sessionID else {
                                return
                            }
                            self.webcamRecorder = nil
                            self.activeWebcamName = nil
                            self.activeWebcamOverlaySelection = nil
                            self.webcamPositionEvents = []
                            SaveService.shared.showError("Webcam recording was not started: \(error.localizedDescription). Screen recording will continue without webcam.")
                        }
                    }

                    guard self.activeRecordingSessionID == sessionID else {
                        return
                    }
                    self.showStopPanel()
                    self.scheduleVideoAutoStopIfNeeded(timeLimitMinutes: timeLimitMinutes, sessionID: sessionID)
                } catch {
                    self.endIdleSleepAssertion()
                    await recorder.cancel()
                    await webcamRecorder.cancel()
                    self.cancelVideoAutoStopTask()
                    _ = self.stopMouseClickMonitoring()
                    self.activeMouseClickCaptureEnabledOverride = nil
                    self.resetRecordingAudioStatus()
                    self.activeWebcamName = nil
                    self.isRecording = false
                    self.isRecordingPaused = false
                    self.activeRecordingRequest = nil
                    self.activeRecordingRegion = nil
                    self.dismissRegionIndicator()
                    self.dismissTeleprompter()
                    self.activeRecordingSessionID = nil
                    self.videoRecorder = nil
                    self.webcamRecorder = nil
                    self.activeWebcamOverlaySelection = nil
                    self.webcamPositionEvents = []
                    self.debugRecordingLifecycle("Video session failed to start: \(error.localizedDescription)")
                    SaveService.shared.showError("Video recording failed: \(error.localizedDescription)")
                }
            }
        }

        showCountdownThen(
            for: .video,
            countdownEnabled: countdownEnabled,
            countdownDuration: countdownDuration,
            action: doRecord
        )
    }

    func startGifRecording() {
        guard !isCaptureActionInProgress else { return }

        isCapturePreparationInProgress = true
        let cursorScreen = screenUnderMouseCursor()
        Task {
            defer { isCapturePreparationInProgress = false }
            guard await prepareForNewCaptureRequest() else { return }
            guard await PermissionManager.shared.checkPermission() else { return }
            guard !isOCRInFlight else { return }
            let settings = CaptureSettings.shared
            if settings.shouldShowCapturePicker(for: .gif) {
                showRecordingPicker(for: .gif, cursorScreen: cursorScreen)
            } else {
                await performRecordingSetup(
                    type: .gif,
                    mode: .region,
                    countdownEnabled: settings.gifCountdownEnabled,
                    countdownDuration: settings.gifCountdownDuration,
                    videoTimeLimitMinutes: settings.videoRecordingTimeLimitMinutes,
                    shouldReturnToPicker: false,
                    cursorScreen: cursorScreen
                )
            }
        }
    }

    private func beginGifRecording(target: CaptureTarget, mouseClicksEnabled: Bool, countdownEnabled: Bool, countdownDuration: Int) {
        resetRecordingAudioStatus()
        let doRecord = { [weak self] in
            guard let self else { return }
            self.isRecordingSetupInProgress = true
            AccessibilityAnnouncementService.shared.announceCaptureStart(
                for: .gif,
                countdownCompleted: countdownEnabled
            )
            Task {
                defer {
                    if !self.isRecording {
                        self.isRecordingSetupInProgress = false
                    }
                }
                let writer = GifWriter()
                do {
                    let sessionID = self.nextRecordingSessionID()
                    self.activeRecordingSessionID = sessionID
                    self.debugRecordingLifecycle("Starting GIF session \(sessionID)")
                    writer.onStreamFailure = { [weak self] error in
                        let message = error.localizedDescription
                        Task { @MainActor [weak self] in
                            self?.handleStreamFailure(
                                for: sessionID,
                                type: .gif,
                                message: message
                            )
                        }
                    }
                    self.gifWriter = writer
                    self.activeRecordingRegion = target.region
                    self.isRecording = true
                    self.isRecordingSetupInProgress = false
                    self.isRecordingPaused = false
                    self.activeRecordingRequest = .gif(target: target, mouseClicksEnabled: mouseClicksEnabled)
                    self.activeMouseClickCaptureEnabledOverride = mouseClicksEnabled
                    self.startMouseClickMonitoringIfNeeded(for: .gif, region: target.region)

                    try await writer.start(target: target)
                    guard self.isRecording,
                          self.activeRecordingSessionID == sessionID,
                          self.gifWriter === writer else {
                        return
                    }
                    if CaptureSettings.shared.preventDisplaySleepWhileRecording {
                        try self.idleSleepAssertion.begin()
                    }
                    self.debugRecordingLifecycle("GIF session \(sessionID) started")
                    self.showStopPanel()
                } catch {
                    self.endIdleSleepAssertion()
                    await writer.cancel()
                    _ = self.stopMouseClickMonitoring()
                    self.activeMouseClickCaptureEnabledOverride = nil
                    self.isRecording = false
                    self.isRecordingPaused = false
                    self.activeRecordingRequest = nil
                    self.activeRecordingRegion = nil
                    self.dismissRegionIndicator()
                    self.activeRecordingSessionID = nil
                    self.gifWriter = nil
                    self.debugRecordingLifecycle("GIF session failed to start: \(error.localizedDescription)")
                    SaveService.shared.showError("GIF recording failed: \(error.localizedDescription)")
                }
            }
        }

        showCountdownThen(
            for: .gif,
            countdownEnabled: countdownEnabled,
            countdownDuration: countdownDuration,
            action: doRecord
        )
    }

    func stopRecording() {
        stopRecording(streamFailureMessage: nil)
    }

    private func stopRecording(streamFailureMessage: String?) {
        // Tear down all recording UI synchronously so the user sees an
        // immediate response (menu bar icon flips, stop panel, webcam preview,
        // and region indicator disappear, stop hotkey unregisters) regardless of
        // whatever the async export flow does next. If the export later
        // hangs, the user can still interact with the app.
        dismissWebcamPreview()
        dismissRegionIndicator()
        guard !isStoppingRecording else { return }
        guard isRecording || videoRecorder != nil || webcamRecorder != nil || gifWriter != nil else { return }

        isStoppingRecording = true
        cancelVideoAutoStopTask()

        dismissStopPanel()
        dismissTeleprompter()
        resetRecordingAudioStatus()
        activeRecordingRegion = nil
        isRecording = false
        isRecordingPaused = false
        activeRecordingRequest = nil
        endIdleSleepAssertion()
        let stoppingSessionID = activeRecordingSessionID
        activeRecordingSessionID = nil
        finalizingSessionID = stoppingSessionID
        if let streamFailureMessage {
            debugRecordingLifecycle("Stopping session \(stoppingSessionID.map(String.init) ?? "unknown") after stream failure: \(streamFailureMessage)")
        } else {
            debugRecordingLifecycle("Stopping session \(stoppingSessionID.map(String.init) ?? "unknown")")
        }

        let task = Task<Void, Never> { [weak self] in
            guard let self else { return }
            await self.stopRecordingFlow(
                stoppingSessionID: stoppingSessionID,
                streamFailureMessage: streamFailureMessage
            )
        }
        stopRecordingTask = task
    }

    private func handleStreamFailure(for sessionID: UInt64, type: CaptureType, message: String) {
        guard activeRecordingSessionID == sessionID, !isStoppingRecording, finalizingSessionID != sessionID else {
            debugRecordingLifecycle("Ignored stale \(type.label.lowercased()) stream failure for session \(sessionID)")
            return
        }

        debugRecordingLifecycle("\(type.label) stream failed for session \(sessionID): \(message)")
        stopRecording(streamFailureMessage: message)
    }

    func togglePauseRecording() {
        guard isRecording else { return }
        if isRecordingPaused {
            videoRecorder?.resume()
            webcamRecorder?.resume()
            gifWriter?.resume()
            teleprompterPanel?.resume()
            isRecordingPaused = false
        } else {
            videoRecorder?.pause()
            webcamRecorder?.pause()
            gifWriter?.pause()
            teleprompterPanel?.pause()
            isRecordingPaused = true
        }
    }

    func toggleRecordingSystemAudioMute() {
        guard recordingSystemAudioEnabled else { return }
        isRecordingSystemAudioMuted.toggle()
        videoRecorder?.setSystemAudioMuted(isRecordingSystemAudioMuted)
    }

    func toggleRecordingMicrophoneMute() {
        guard recordingMicrophoneEnabled else { return }
        isRecordingMicrophoneMuted.toggle()
        videoRecorder?.setMicrophoneMuted(isRecordingMicrophoneMuted)
    }

    func restartRecording() {
        guard let request = activeRecordingRequest else { return }
        let draggedWebcamSelection = activeWebcamOverlaySelection
        Task {
            await discardRecording(clearActiveRequest: false)
            switch request {
            case let .video(target, systemAudio, microphone, selectedMicrophoneID, webcamSelection, mouseClicksEnabled, timeLimitMinutes):
                beginVideoRecording(
                    target: target,
                    systemAudio: systemAudio,
                    microphone: microphone,
                    selectedMicrophoneID: selectedMicrophoneID,
                    webcamSelection: draggedWebcamSelection ?? webcamSelection,
                    mouseClicksEnabled: mouseClicksEnabled,
                    timeLimitMinutes: timeLimitMinutes,
                    countdownEnabled: false,
                    countdownDuration: 0
                )
            case let .gif(target, mouseClicksEnabled):
                beginGifRecording(
                    target: target,
                    mouseClicksEnabled: mouseClicksEnabled,
                    countdownEnabled: false,
                    countdownDuration: 0
                )
            }
        }
    }

    func discardRecording() {
        Task {
            await discardRecording(clearActiveRequest: true)
        }
    }

    private func discardRecording(clearActiveRequest: Bool) async {
        dismissWebcamPreview()
        dismissRegionIndicator()
        endIdleSleepAssertion()
        guard isRecording || videoRecorder != nil || webcamRecorder != nil || gifWriter != nil else { return }
        cancelVideoAutoStopTask()
        dismissStopPanel()
        dismissTeleprompter()
        _ = stopMouseClickMonitoring()
        activeMouseClickCaptureEnabledOverride = nil
        activeWebcamOverlaySelection = nil
        self.webcamPositionEvents = []
        resetRecordingAudioStatus()
        activeRecordingRegion = nil
        isRecording = false
        isRecordingPaused = false
        activeRecordingSessionID = nil

        let recorder = videoRecorder
        let webcam = webcamRecorder
        let writer = gifWriter
        videoRecorder = nil
        webcamRecorder = nil
        gifWriter = nil
        lastVideoRecordingArtifacts = nil

        if clearActiveRequest {
            activeRecordingRequest = nil
        }

        await recorder?.cancel()
        await webcam?.cancel()
        await writer?.cancel()
    }

    private func stopRecordingFlow(stoppingSessionID: UInt64?, streamFailureMessage: String? = nil) async {
        defer {
            isStoppingRecording = false
            if finalizingSessionID == stoppingSessionID {
                finalizingSessionID = nil
            }
            stopRecordingTask = nil
            debugRecordingLifecycle("Finished finalizing session \(stoppingSessionID.map(String.init) ?? "unknown")")
        }
        defer { activeMouseClickCaptureEnabledOverride = nil }

        let videoRecorderAtStop = videoRecorder
        let webcamRecorderAtStop = webcamRecorder
        let gifWriterAtStop = gifWriter

        let capturedMouseClickData = stopMouseClickMonitoring()
        let shortVideoIndicatorBypassThreshold: TimeInterval = 120

        let stoppedRecordingType: CaptureType?
        if videoRecorderAtStop != nil || webcamRecorderAtStop != nil {
            stoppedRecordingType = .video
        } else if gifWriterAtStop != nil {
            stoppedRecordingType = .gif
        } else {
            stoppedRecordingType = nil
        }

        let shouldShowProcessingIndicator: Bool = {
            guard let videoRecorderAtStop, gifWriterAtStop == nil else { return true }
            let mouseClicksEnabled = shouldCaptureMouseClicks(for: .video)
            if mouseClicksEnabled {
                return true
            }
            if CaptureSettings.shared.showBrandingOverlay {
                return true
            }
            return videoRecorderAtStop.currentRecordingDuration >= shortVideoIndicatorBypassThreshold
        }()

        if shouldShowProcessingIndicator {
            showProcessingIndicator()
            updateProcessingMessage("Processing...")
            updateProcessingProgress(0.05, status: "Preparing export...")
        }

        // Snapshot video settings before any suspension so that overlay output URL
        // selection and downstream trimmer/save decisions stay consistent even if
        // the user changes preferences while export is in progress.
        let videoShowTrimmer = streamFailureMessage == nil && CaptureSettings.shared.showTrimmer
        let videoShouldSaveImmediately = streamFailureMessage != nil || !videoShowTrimmer || CaptureSettings.shared.saveImmediatelyVideo
        let shouldReturnToPickerAfterRecording = streamFailureMessage == nil && self.shouldReturnToPickerAfterRecording
        let videoOverlayStyle = CaptureSettings.shared.mouseClickOverlayStyle(for: .video)
        let showBrandingOverlay = CaptureSettings.shared.showBrandingOverlay
        let webcamShapeSetting = CaptureSettings.shared.webcamShape
        let webcamCornerSetting = CaptureSettings.shared.webcamCorner
        let webcamSizeSetting = CaptureSettings.shared.webcamSize
        let webcamCornerRadiusSetting = CaptureSettings.shared.webcamCornerRadius
        let webcamOverlaySelection = activeWebcamOverlaySelection
        let webcamPositionEvents = self.webcamPositionEvents

        var savedVideoURL: URL?
        var savedWebcamURL: URL?
        var partialOutputSaveError: String?
        var noPartialFramesWereCaptured = false

        if let recorder = webcamRecorderAtStop {
            do {
                savedWebcamURL = try await recorder.stop()
            } catch {
                SaveService.shared.showError("Webcam save failed: \(error.localizedDescription). Continuing with screen-only export.")
            }

            if webcamRecorder === recorder {
                webcamRecorder = nil
            } else {
                debugRecordingLifecycle("Skipped clearing stale webcam recorder reference")
            }
        }

        if let recorder = videoRecorderAtStop {
            do {
                updateProcessingProgress(0.15, status: "Exporting video...")
                savedVideoURL = try await (
                    streamFailureMessage == nil
                        ? recorder.stop()
                        : recorder.finishAfterStreamFailure()
                )
                updateProcessingProgress(0.55, status: "Applying overlays...")
            } catch {
                if streamFailureMessage == nil {
                    SaveService.shared.showError("Video save failed: \(error.localizedDescription)")
                } else if let captureError = error as? CaptureError, case .noFrames = captureError {
                    noPartialFramesWereCaptured = true
                } else {
                    partialOutputSaveError = error.localizedDescription
                }
            }

            if let currentURL = savedVideoURL,
               let capturedMouseClickData,
               capturedMouseClickData.type == .video,
               !capturedMouseClickData.events.isEmpty {
                let overlayOutputURL = videoShouldSaveImmediately
                    ? SaveService.shared.generateURL(for: .video)
                    : TinyClipsTemporaryFiles.makeURL(fileExtension: "mp4")
                do {
                    // Use the final save URL as the overlay output when saving immediately,
                    // so the processed file lands in the user's save directory rather than
                    // a temp location that the OS can delete.
                    savedVideoURL = try await Self.overlayVideoOffMain(
                        sourceURL: currentURL,
                        region: capturedMouseClickData.region,
                        events: capturedMouseClickData.events,
                        outputURL: overlayOutputURL,
                        style: videoOverlayStyle,
                        onProgress: { [weak self] overlayProgress in
                            guard let self else { return }
                            // Map exporter 0...1 progress into the overlay phase range.
                            let normalized = min(max(overlayProgress, 0), 1)
                            let mapped = 0.55 + (normalized * 0.29)
                            Task { @MainActor in
                                self.updateProcessingProgress(mapped, status: "Applying overlays...")
                            }
                        }
                    )
                    updateProcessingProgress(0.85, status: "Finalizing...")
                } catch {
                    try? FileManager.default.removeItem(at: overlayOutputURL)
                    SaveService.shared.showError("Mouse click overlay failed for video: \(error.localizedDescription)")
                }
            }

            if let currentURL = savedVideoURL {
                let webcamOverlayOptions: BrandingOverlayProcessor.WebcamOverlayOptions? = {
                    guard let savedWebcamURL, FileManager.default.fileExists(atPath: savedWebcamURL.path) else {
                        return nil
                    }

                    let shape = webcamOverlaySelection?.shape ?? webcamShapeSetting
                    let corner = webcamOverlaySelection?.corner ?? webcamCornerSetting
                    let size = webcamOverlaySelection?.size ?? webcamSizeSetting
                    let cornerRadiusOverride: CGFloat? = webcamCornerRadiusSetting >= 0
                        ? CGFloat(webcamCornerRadiusSetting)
                        : nil

                    // Align the webcam track to the audio/screen timeline using the
                    // host-clock timestamps of each source's first captured frame. The
                    // webcam camera typically warms up later than ScreenCaptureKit, so
                    // without this offset the overlay drifts out of sync with the audio.
                    var webcamStartOffset = CMTime.zero
                    if let webcamFirst = webcamRecorderAtStop?.firstSampleTime,
                       let screenFirst = videoRecorderAtStop?.firstScreenSampleTime {
                        webcamStartOffset = CMTimeSubtract(webcamFirst, screenFirst)
                    }

                    return BrandingOverlayProcessor.WebcamOverlayOptions(
                        videoURL: savedWebcamURL,
                        shape: shape,
                        corner: corner,
                        size: size,
                        cornerRadiusOverride: cornerRadiusOverride,
                        startOffset: webcamStartOffset,
                        positionEvents: webcamPositionEvents
                    )
                }()

                if showBrandingOverlay || webcamOverlayOptions != nil {
                    let brandingOutputURL = videoShouldSaveImmediately
                        ? SaveService.shared.generateURL(for: .video)
                        : TinyClipsTemporaryFiles.makeURL(fileExtension: "mp4")
                    do {
                        savedVideoURL = try await Self.overlayBrandingVideoOffMain(
                            sourceURL: currentURL,
                            outputURL: brandingOutputURL,
                            includeBranding: showBrandingOverlay,
                            webcamOverlay: webcamOverlayOptions,
                            onProgress: { [weak self] overlayProgress in
                                guard let self else { return }
                                let normalized = min(max(overlayProgress, 0), 1)
                                let mapped = 0.85 + (normalized * 0.1)
                                Task { @MainActor in
                                    let status = showBrandingOverlay
                                        ? (webcamOverlayOptions == nil ? "Applying branding..." : "Applying branding + webcam...")
                                        : "Applying webcam overlay..."
                                    self.updateProcessingProgress(mapped, status: status)
                                }
                            }
                        )
                        updateProcessingProgress(0.95, status: "Finalizing...")
                    } catch {
                        try? FileManager.default.removeItem(at: brandingOutputURL)
                        SaveService.shared.showError("Video compositing failed: \(error.localizedDescription)")
                    }
                }
            }

            updateProcessingProgress(1.0, status: "Done")
            if videoRecorder === recorder {
                videoRecorder = nil
            } else {
                debugRecordingLifecycle("Skipped clearing stale video recorder reference")
            }
        }

        if streamFailureMessage != nil,
           let currentURL = savedVideoURL,
           currentURL.deletingLastPathComponent().standardizedFileURL == TinyClipsTemporaryFiles.directoryURL.standardizedFileURL {
            let recoveryURL = SaveService.shared.generateURL(for: .video)
            do {
                try FileManager.default.moveItem(at: currentURL, to: recoveryURL)
                savedVideoURL = recoveryURL
            } catch {
                savedVideoURL = nil
                partialOutputSaveError = error.localizedDescription
            }
        }

        if let savedVideoURL {
            CaptureAnalyticsStore.shared.recordCapture(.video)
            lastVideoRecordingArtifacts = VideoRecordingArtifacts(
                screenRecordingURL: savedVideoURL,
                webcamRecordingURL: savedWebcamURL
            )
        } else if videoRecorderAtStop != nil || webcamRecorderAtStop != nil {
            lastVideoRecordingArtifacts = nil
        }

        activeWebcamOverlaySelection = nil
        self.webcamPositionEvents = []

        var savedGifURL: URL?
        if let writer = gifWriterAtStop {
            let url = SaveService.shared.generateURL(for: .gif)
            do {
                let settings = CaptureSettings.shared
                let gifShowTrimmer = streamFailureMessage == nil && settings.showGifTrimmer
                let shouldSaveImmediately = streamFailureMessage != nil || !gifShowTrimmer || settings.saveImmediatelyGif

                updateProcessingProgress(0.1, status: "Exporting GIF…")

                if gifShowTrimmer {
                    var gifData = try await writer.stopAndReturnData()
                    updateProcessingProgress(0.5, status: "Applying overlays…")

                    if let capturedMouseClickData,
                       capturedMouseClickData.type == .gif,
                       !capturedMouseClickData.events.isEmpty {
                        let inputGifData = gifData
                        let overlayStyle = settings.mouseClickOverlayStyle(for: .gif)
                        let region = capturedMouseClickData.region
                        let events = capturedMouseClickData.events
                        gifData = await Self.runOffMain {
                            MouseClickOverlayProcessor.overlayOnGif(
                                gifData: inputGifData,
                                region: region,
                                events: events,
                                style: overlayStyle
                            )
                        }
                    }

                    if settings.showBrandingOverlay {
                        let inputGifData = gifData
                        gifData = await Self.runOffMain {
                            BrandingOverlayProcessor.applyToGifData(inputGifData)
                        }
                    }

                    updateProcessingProgress(0.8, status: shouldSaveImmediately ? "Saving…" : "Opening trimmer…")

                    if shouldSaveImmediately {
                        let dataToWrite = gifData
                        try await Self.runOffMainThrowing {
                            try GifWriter.writeGIF(
                                frames: dataToWrite.frames,
                                frameDelay: dataToWrite.frameDelay,
                                maxWidth: dataToWrite.maxWidth,
                                to: url
                            )
                        }
                        SaveService.shared.handleSavedFile(url: url, type: .gif)
                    }

                    updateProcessingProgress(1.0, status: "Done")
                    CaptureAnalyticsStore.shared.recordCapture(.gif)
                    showGifTrimmer(
                        gifData: gifData,
                        outputURL: url,
                        reopenPickerAfterClose: shouldReturnToPickerAfterRecording
                    )
                } else {
                    if streamFailureMessage == nil {
                        try await writer.stop(outputURL: url)
                    } else {
                        let gifData = try writer.finishAfterStreamFailure()
                        try await Self.runOffMainThrowing {
                            try GifWriter.writeGIF(
                                frames: gifData.frames,
                                frameDelay: gifData.frameDelay,
                                maxWidth: gifData.maxWidth,
                                to: url
                            )
                        }
                    }
                    savedGifURL = url
                    updateProcessingProgress(0.5, status: "Applying overlays…")

                    if let capturedMouseClickData,
                       capturedMouseClickData.type == .gif,
                       !capturedMouseClickData.events.isEmpty {
                        do {
                            let overlayStyle = settings.mouseClickOverlayStyle(for: .gif)
                            let region = capturedMouseClickData.region
                            let events = capturedMouseClickData.events
                            let tempURL = TinyClipsTemporaryFiles.makeURL(fileExtension: "gif")
                            defer { try? FileManager.default.removeItem(at: tempURL) }

                            try await Self.runOffMainThrowing {
                                let gifData = try MouseClickOverlayProcessor.loadGifCaptureData(from: url)
                                let processedGifData = MouseClickOverlayProcessor.overlayOnGif(
                                    gifData: gifData,
                                    region: region,
                                    events: events,
                                    style: overlayStyle
                                )
                                try GifWriter.writeGIF(
                                    frames: processedGifData.frames,
                                    frameDelay: processedGifData.frameDelay,
                                    maxWidth: processedGifData.maxWidth,
                                    to: tempURL
                                )
                            }

                            if FileManager.default.fileExists(atPath: tempURL.path) {
                                _ = try FileManager.default.replaceItemAt(url, withItemAt: tempURL)
                            }
                        } catch {
                            SaveService.shared.showError("Mouse click overlay failed for GIF: \(error.localizedDescription)")
                        }
                    }

                    if settings.showBrandingOverlay {
                        do {
                            let tempURL = TinyClipsTemporaryFiles.makeURL(fileExtension: "gif")
                            defer { try? FileManager.default.removeItem(at: tempURL) }

                            try await Self.runOffMainThrowing {
                                let gifData = try MouseClickOverlayProcessor.loadGifCaptureData(from: url)
                                let processedGifData = BrandingOverlayProcessor.applyToGifData(gifData)
                                try GifWriter.writeGIF(
                                    frames: processedGifData.frames,
                                    frameDelay: processedGifData.frameDelay,
                                    maxWidth: processedGifData.maxWidth,
                                    to: tempURL
                                )
                            }

                            if FileManager.default.fileExists(atPath: tempURL.path) {
                                _ = try FileManager.default.replaceItemAt(url, withItemAt: tempURL)
                            }
                        } catch {
                            SaveService.shared.showError("Branding overlay failed for GIF: \(error.localizedDescription)")
                        }
                    }

                    updateProcessingProgress(0.9, status: "Finalizing…")
                    CaptureAnalyticsStore.shared.recordCapture(.gif)
                    SaveService.shared.handleSavedFile(url: url, type: .gif)
                    updateProcessingProgress(1.0, status: "Done")
                    if shouldReturnToPickerAfterRecording,
                       settings.shouldShowCapturePickerAfterCapture(for: .gif) {
                        showRecordingPicker(for: .gif)
                    }
                }
            } catch {
                if streamFailureMessage == nil {
                    SaveService.shared.showError("GIF save failed: \(error.localizedDescription)")
                } else if let captureError = error as? CaptureError, case .noFrames = captureError {
                    noPartialFramesWereCaptured = true
                } else {
                    partialOutputSaveError = error.localizedDescription
                }
            }
            if gifWriter === writer {
                gifWriter = nil
            } else {
                debugRecordingLifecycle("Skipped clearing stale GIF writer reference")
            }
        }

        if let stoppedRecordingType {
            AccessibilityAnnouncementService.shared.announceRecordingStopped(for: stoppedRecordingType)
        }

        // Show editor windows AFTER all recording resources are released
        // and UI state is cleaned up, so AVPlayer doesn't contend with
        // AVAssetWriter for the same file.
        // The processing indicator is dismissed here, after trimmer/save calls,
        // so there is no blank gap between the indicator closing and the trimmer appearing.
        if let savedVideoURL {
            if videoShowTrimmer {
                if videoShouldSaveImmediately {
                    SaveService.shared.handleSavedFile(url: savedVideoURL, type: .video)
                }

                showTrimmer(
                    for: savedVideoURL,
                    saveImmediately: videoShouldSaveImmediately,
                    reopenPickerAfterClose: shouldReturnToPickerAfterRecording
                )
            } else {
                SaveService.shared.handleSavedFile(url: savedVideoURL, type: .video)
                if shouldReturnToPickerAfterRecording,
                   CaptureSettings.shared.shouldShowCapturePickerAfterCapture(for: .video) {
                    showRecordingPicker(for: .video)
                }
            }
        }

        dismissProcessingIndicator()

        if let streamFailureMessage {
            let didSavePartialOutput = savedVideoURL != nil || savedGifURL != nil
            let partialOutputMessage: String
            if didSavePartialOutput {
                partialOutputMessage = "A partial recording was saved."
            } else if let partialOutputSaveError {
                partialOutputMessage = "Tiny Clips could not save the partial recording: \(partialOutputSaveError)"
            } else if noPartialFramesWereCaptured {
                partialOutputMessage = "No captured frames could be saved."
            } else {
                partialOutputMessage = "No partial recording could be saved."
            }
            SaveService.shared.showError(
                "Screen capture stopped unexpectedly. \(partialOutputMessage) Check Screen Recording permission in System Settings, then start a new recording. Details: \(streamFailureMessage)"
            )
        }
    }

    private func prepareForNewCaptureRequest(allowOCRInFlight: Bool = false) async -> Bool {
        guard allowOCRInFlight || !isOCRInFlight else { return false }

        if let stopRecordingTask {
            debugRecordingLifecycle("Waiting for ongoing finalize before new capture")
            await stopRecordingTask.value
        }

        dismissScreenshotPicker()
        dismissRecordingPicker()
        dismissStartPanel()
        countdownWindow?.cancel()
        countdownWindow = nil
        dismissWebcamPreview()
        dismissRegionIndicator()

        pendingRecordingTarget = nil
        pendingRecordingType = nil
        shouldReturnToPickerAfterRecording = false
        lastVideoRecordingArtifacts = nil
        activeWebcamOverlaySelection = nil
        webcamPositionEvents = []
        activeRecordingRequest = nil
        isRecordingPaused = false

        _ = stopMouseClickMonitoring()

        if isStoppingRecording {
            debugRecordingLifecycle("Capture request blocked while finalize in progress")
            return false
        }

        if videoRecorder != nil || webcamRecorder != nil || gifWriter != nil || isRecording {
            endIdleSleepAssertion()
            isStoppingRecording = true
            let stoppingSessionID = activeRecordingSessionID
            activeRecordingSessionID = nil
            finalizingSessionID = stoppingSessionID
            debugRecordingLifecycle("Stopping existing session before new capture")
            await stopRecordingFlow(stoppingSessionID: stoppingSessionID)
        } else {
            dismissStopPanel()
        }

        return true
    }

    private func endIdleSleepAssertion() {
        do {
            try idleSleepAssertion.end()
        } catch {
            NSLog("Unable to release TinyClips idle sleep assertion: \(error.localizedDescription)")
        }
    }

    private func showScreenshotEditor(
        for url: URL,
        initialSaveURL: URL,
        deleteSourceOnCancel: Bool,
        reopenPickerAfterClose: Bool
    ) {
        ScreenshotEditorRegistry.shared.present(
            imageURL: url,
            initialSaveURL: initialSaveURL,
            deleteSourceAfterSave: deleteSourceOnCancel
        ) { [weak self] resultURL in
            if let resultURL {
                SaveService.shared.handleSavedFile(url: resultURL, type: .screenshot)
            } else if deleteSourceOnCancel {
                try? FileManager.default.removeItem(at: url)
            }
            if reopenPickerAfterClose,
               CaptureSettings.shared.shouldShowCapturePickerAfterCapture(for: .screenshot) {
                self?.showScreenshotPicker()
            }
        }
    }

    func openRecentCapture(_ item: RecentCaptureItem) {
        guard FileManager.default.fileExists(atPath: item.path) else {
            RecentCaptureStore.shared.remove(item)
            return
        }

        switch item.type {
        case .screenshot:
            showScreenshotEditor(
                for: item.url,
                initialSaveURL: item.url,
                deleteSourceOnCancel: false,
                reopenPickerAfterClose: false
            )
        case .video:
            showTrimmer(for: item.url, saveImmediately: true)
        case .gif:
            guard let gifData = try? GifCaptureData(contentsOf: item.url) else {
                SaveService.shared.showError("GIF could not be opened.")
                return
            }
            showGifTrimmer(gifData: gifData, outputURL: item.url)
        }
    }

    private func showTrimmer(for url: URL, saveImmediately: Bool, reopenPickerAfterClose: Bool = false) {
        let window = VideoTrimmerWindow(videoURL: url) { [weak self] resultURL in
            guard let self else { return }
            if let resultURL {
                if saveImmediately {
                    SaveService.shared.handleSavedFile(url: resultURL, type: .video)
                } else {
                    let finalURL = SaveService.shared.generateURL(for: .video)
                    try? FileManager.default.removeItem(at: finalURL)
                    do {
                        try FileManager.default.moveItem(at: resultURL, to: finalURL)
                        SaveService.shared.handleSavedFile(url: finalURL, type: .video)
                    } catch {
                        SaveService.shared.showError("Video save failed: \(error.localizedDescription)")
                    }
                }
            } else {
                if !saveImmediately {
                    try? FileManager.default.removeItem(at: url)
                }
            }
            // Defer release so the window isn't deallocated mid-callback
            DispatchQueue.main.async {
                self.trimmerWindow = nil
                if reopenPickerAfterClose,
                   CaptureSettings.shared.shouldShowCapturePickerAfterCapture(for: .video) {
                    self.showRecordingPicker(for: .video)
                }
            }
        }
        self.trimmerWindow = window
        // Defer showing to next run loop to avoid issues with menu tracking
        DispatchQueue.main.async {
            self.bringWindowToFront(window)
        }
    }

    private func showGifTrimmer(
        gifData: GifCaptureData,
        outputURL: URL,
        reopenPickerAfterClose: Bool = false
    ) {
        let window = GifTrimmerWindow(gifData: gifData, outputURL: outputURL) { [weak self] resultURL in
            guard let self else { return }
            if let resultURL {
                SaveService.shared.handleSavedFile(url: resultURL, type: .gif)
            }
            DispatchQueue.main.async {
                self.gifTrimmerWindow = nil
                if reopenPickerAfterClose,
                   CaptureSettings.shared.shouldShowCapturePickerAfterCapture(for: .gif) {
                    self.showRecordingPicker(for: .gif)
                }
            }
        }
        self.gifTrimmerWindow = window
        DispatchQueue.main.async {
            self.bringWindowToFront(window)
        }
    }

    private func showStartPanel() {
        let panel = StartRecordingPanel(
            captureType: pendingRecordingType ?? .video,
            onStart: { [weak self] systemAudio, microphoneSelection, webcamSelection, mouseClicksEnabled, _ in
                guard
                    let self,
                    let target = self.pendingRecordingTarget,
                    let type = self.pendingRecordingType
                else { return }

                let countdownEnabled = self.pendingRecordingCountdownEnabled
                let countdownDuration = self.pendingRecordingCountdownDuration
                let videoTimeLimitMinutes = self.pendingVideoTimeLimitMinutes

                self.pendingRecordingTarget = nil
                self.pendingRecordingType = nil
                self.dismissStartPanel()

                switch type {
                case .video:
                    self.beginVideoRecording(
                        target: target,
                        systemAudio: systemAudio,
                        microphone: microphoneSelection.enabled,
                        selectedMicrophoneID: microphoneSelection.deviceID,
                        webcamSelection: webcamSelection,
                        mouseClicksEnabled: mouseClicksEnabled,
                        timeLimitMinutes: videoTimeLimitMinutes,
                        countdownEnabled: countdownEnabled,
                        countdownDuration: countdownDuration
                    )
                case .gif:
                    self.beginGifRecording(
                        target: target,
                        mouseClicksEnabled: mouseClicksEnabled,
                        countdownEnabled: countdownEnabled,
                        countdownDuration: countdownDuration
                    )
                case .screenshot:
                    break
                }
            },
            onCancel: { [weak self] in
                self?.pendingRecordingTarget = nil
                self?.pendingRecordingType = nil
                self?.pendingVideoTimeLimitMinutes = CaptureSettings.shared.videoRecordingTimeLimitMinutes
                self?.dismissStartPanel()
                self?.dismissRegionIndicator()
            }
        )
        panel.show()
        self.startPanel = panel
    }

    private func dismissStartPanel() {
        // Save the panel position before dismissing
        if let panel = startPanel {
            recordPanelPosition = panel.frame.origin
        }
        startPanel?.dismiss()
        startPanel = nil
    }

    private func showStopPanel() {
        let panel = StopRecordingPanel(
            captureManager: self,
            onPauseResume: { [weak self] in
                self?.togglePauseRecording()
            },
            onRestart: { [weak self] in
                self?.restartRecording()
            },
            onDiscard: { [weak self] in
                self?.discardRecording()
            },
            onStop: { [weak self] in
                self?.stopRecording()
            }
        )
        panel.show(at: recordPanelPosition)
        self.stopPanel = panel
    }

    private func dismissStopPanel() {
        stopPanel?.close()
        stopPanel = nil
        recordPanelPosition = nil
    }

    private func prepareTeleprompterIfNeeded(region: CaptureRegion, sessionID: UInt64) async -> SCWindow? {
        let settings = CaptureSettings.shared
        let transcript = settings.teleprompterTranscript.trimmingCharacters(in: .whitespacesAndNewlines)
        guard settings.teleprompterEnabled, !transcript.isEmpty else { return nil }

        dismissTeleprompter()
        let panel = TeleprompterPanel(
            transcript: transcript,
            scrollSpeed: settings.teleprompterScrollSpeed,
            fontSize: TeleprompterDisplaySize(rawValue: settings.teleprompterFontSize) ?? .medium,
            panelHeight: TeleprompterDisplaySize(rawValue: settings.teleprompterPanelHeight) ?? .medium
        )
        teleprompterPanel = panel
        panel.prepareHidden(relativeTo: region)

        let windowID = CGWindowID(panel.windowNumber)
        for attempt in 0..<3 {
            if let content = try? await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false),
               let window = content.windows.first(where: { $0.windowID == windowID }) {
                guard teleprompterPanel === panel,
                      isRecording,
                      activeRecordingSessionID == sessionID else {
                    return nil
                }
                return window
            }
            if attempt < 2 {
                try? await Task.sleep(nanoseconds: 50_000_000)
            }
        }

        if teleprompterPanel === panel {
            dismissTeleprompter()
        }
        debugRecordingLifecycle("Teleprompter was hidden because ScreenCaptureKit could not exclude its window")
        return nil
    }

    private func dismissTeleprompter() {
        teleprompterPanel?.orderOut(nil)
        let panel = teleprompterPanel
        teleprompterPanel = nil
        // Defer the final release so the panel isn't deallocated mid-callback.
        DispatchQueue.main.async {
            panel?.close()
        }
    }

    private func showWebcamPreview(
        session: AVCaptureSession?,
        selection: StartRecordingPanel.WebcamSelection,
        region: CaptureRegion
    ) {
        dismissWebcamPreview()
        guard let session else { return }

        let panel = WebcamPreviewPanel(
            session: session,
            selection: selection,
            region: region
        ) { [weak self] corner in
            guard let self, var selection = self.activeWebcamOverlaySelection else { return }
            selection.corner = corner
            self.activeWebcamOverlaySelection = selection
            if self.webcamPositionEvents.last?.corner.lowercased() != corner.lowercased() {
                self.webcamPositionEvents.append(.init(
                    time: self.videoRecorder?.currentTimelineTime() ?? .zero,
                    corner: corner
                ))
            }
            CaptureSettings.shared.webcamCorner = corner
        }
        panel.show()
        webcamPreviewPanel = panel
    }

    private func dismissWebcamPreview() {
        webcamPreviewPanel?.close()
        webcamPreviewPanel = nil
    }

    private func showProcessingIndicator(
        message: String = "Processing...",
        status: String? = "Preparing export..."
    ) {
        guard processingIndicatorWindow == nil else { return }
        let window = ProcessingIndicatorWindow(message: message, status: status, progress: 0.0)
        processingIndicatorWindow = window
        processingIndicatorShownAt = Date()
        window.show()
    }

    private func updateProcessingProgress(_ progress: Double, status: String? = nil) {
        guard let window = processingIndicatorWindow else { return }
        let clampedProgress = min(max(progress, 0), 1)
        window.updateProgress(clampedProgress)
        if let status {
            window.updateStatus(status)
        }
    }

    private func updateProcessingMessage(_ message: String) {
        processingIndicatorWindow?.updateMessage(message)
    }

    private func dismissProcessingIndicator() {
        guard let window = processingIndicatorWindow else { return }

        // Ensure users can see the bar reach completion before the panel closes.
        updateProcessingProgress(1.0, status: "Done")

        let minimumVisibleDuration: TimeInterval = 0.35
        let minimumCompletionVisibleDuration: TimeInterval = 0.2
        let elapsed = Date().timeIntervalSince(processingIndicatorShownAt ?? .distantPast)
        let remaining = max(
            minimumCompletionVisibleDuration,
            max(0, minimumVisibleDuration - elapsed)
        )

        func dismissNow() {
            guard self.processingIndicatorWindow === window else { return }
            window.close()
            processingIndicatorWindow = nil
            processingIndicatorShownAt = nil
        }

        if remaining > 0 {
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: UInt64(remaining * 1_000_000_000))
                dismissNow()
            }
        } else {
            dismissNow()
        }
    }

    private func dismissRegionIndicator() {
        regionIndicatorPanel?.close()
        regionIndicatorPanel = nil
    }

    private func resetRecordingAudioStatus() {
        recordingSystemAudioEnabled = false
        recordingMicrophoneEnabled = false
        isRecordingSystemAudioMuted = false
        isRecordingMicrophoneMuted = false
        activeMicrophoneName = nil
        activeWebcamName = nil
        microphoneLevel = 0
        microphoneWarningMessage = nil
    }

    private func scheduleVideoAutoStopIfNeeded(timeLimitMinutes: Int, sessionID: UInt64) {
        cancelVideoAutoStopTask()

        guard timeLimitMinutes > 0 else { return }
        let durationInNanoseconds = UInt64(timeLimitMinutes) * 60 * 1_000_000_000

        videoAutoStopTask = Task { @MainActor [weak self] in
            guard let self else { return }
            try? await Task.sleep(nanoseconds: durationInNanoseconds)
            guard !Task.isCancelled else { return }
            guard self.isRecording, self.videoRecorder != nil, self.activeRecordingSessionID == sessionID else { return }
            self.stopRecording()
        }
    }

    private func cancelVideoAutoStopTask() {
        videoAutoStopTask?.cancel()
        videoAutoStopTask = nil
    }

    private func showCountdownThen(
        for type: CaptureType,
        countdownEnabled: Bool? = nil,
        countdownDuration: Int? = nil,
        action: @escaping () -> Void
    ) {
        let settings = CaptureSettings.shared
        let defaultEnabled: Bool
        let defaultDuration: Int

        switch type {
        case .video:
            defaultEnabled = settings.videoCountdownEnabled
            defaultDuration = settings.videoCountdownDuration
        case .gif:
            defaultEnabled = settings.gifCountdownEnabled
            defaultDuration = settings.gifCountdownDuration
        case .screenshot:
            defaultEnabled = settings.screenshotCountdownEnabled
            defaultDuration = settings.screenshotCountdownDuration
        }

        let enabled = countdownEnabled ?? defaultEnabled
        let duration = countdownDuration ?? defaultDuration

        guard enabled else {
            action()
            return
        }
        let window = CountdownWindow(duration: duration) { [weak self] in
            self?.countdownWindow = nil
            action()
        }
        self.countdownWindow = window
        window.show()
    }

    private func showOnboardingIfNeeded() {
        let settings = CaptureSettings.shared
        guard !settings.hasCompletedOnboarding, onboardingWindow == nil else { return }

        let window = OnboardingWizardWindow { [weak self] completed in
            if completed {
                settings.hasCompletedOnboarding = true
            }
            DispatchQueue.main.async {
                self?.onboardingWindow = nil
            }
        }
        onboardingWindow = window

        DispatchQueue.main.async {
            self.bringWindowToFront(window)
        }
    }

    func showGuide() {
        if let guideWindow {
            DispatchQueue.main.async {
                self.bringWindowToFront(guideWindow)
            }
            return
        }

        let window = GuideWindow(onDismiss: { [weak self] in
            DispatchQueue.main.async {
                self?.guideWindow = nil
            }
        })

        self.guideWindow = window
        DispatchQueue.main.async {
            self.bringWindowToFront(window)
        }
    }

    private func showRecordingPicker(for type: CaptureType, cursorScreen: NSScreen? = nil) {
        dismissScreenshotPicker()
        dismissRecordingPicker()

        let settings = CaptureSettings.shared
        let countdownEnabled: Bool
        let countdownDuration: Int

        switch type {
        case .video:
            countdownEnabled = settings.videoCountdownEnabled
            countdownDuration = settings.videoCountdownDuration
        case .gif:
            countdownEnabled = settings.gifCountdownEnabled
            countdownDuration = settings.gifCountdownDuration
        case .screenshot:
            countdownEnabled = settings.screenshotCountdownEnabled
            countdownDuration = settings.screenshotCountdownDuration
        }

        let panel = CapturePickerPanel(
            captureType: type,
            countdownEnabled: countdownEnabled,
            countdownDuration: countdownDuration,
            videoTimeLimitMinutes: settings.videoRecordingTimeLimitMinutes,
            onCapture: { [weak self] mode, enabled, duration, videoTimeLimitMinutes in
                guard let self else { return }
                self.dismissRecordingPicker()
                if type == .video {
                    settings.videoRecordingTimeLimitMinutes = videoTimeLimitMinutes
                }
                Task {
                    await self.performRecordingSetup(
                        type: type,
                        mode: mode,
                        countdownEnabled: enabled,
                        countdownDuration: duration,
                        videoTimeLimitMinutes: videoTimeLimitMinutes,
                        shouldReturnToPicker: true,
                        cursorScreen: cursorScreen
                    )
                }
            },
            onCancel: { [weak self] in
                self?.dismissRecordingPicker()
            }
        )
        panel.show(at: recordingPickerPosition)
        self.recordingPickerPanel = panel
    }

    private func dismissRecordingPicker() {
        if let panel = recordingPickerPanel {
            recordingPickerPosition = panel.frame.origin
        }
        recordingPickerPanel?.dismiss()
        recordingPickerPanel = nil
    }

    private func performRecordingSetup(
        type: CaptureType,
        mode: CapturePickerMode,
        countdownEnabled: Bool,
        countdownDuration: Int,
        videoTimeLimitMinutes: Int,
        shouldReturnToPicker: Bool,
        cursorScreen: NSScreen? = nil
    ) async {
        let ownsPreparationState = !isCapturePreparationInProgress
        if ownsPreparationState {
            isCapturePreparationInProgress = true
        }
        defer {
            if ownsPreparationState {
                isCapturePreparationInProgress = false
            }
        }

        guard let target = await chooseCaptureTarget(for: mode, cursorScreen: cursorScreen) else {
            if shouldReturnToPicker {
                showRecordingPicker(for: type, cursorScreen: cursorScreen)
            }
            return
        }

        pendingRecordingTarget = target
        pendingRecordingType = type
        shouldReturnToPickerAfterRecording = shouldReturnToPicker
        pendingRecordingCountdownEnabled = countdownEnabled
        pendingRecordingCountdownDuration = countdownDuration
        pendingVideoTimeLimitMinutes = videoTimeLimitMinutes

        dismissRegionIndicator()

        if mode != .screen, CaptureSettings.shared.showRegionIndicator {
            let panel = RegionIndicatorPanel(region: target.region)
            panel.show()
            regionIndicatorPanel = panel
        }

        showStartPanel()
    }

    private func chooseCaptureTarget(for mode: CapturePickerMode, cursorScreen: NSScreen?) async -> CaptureTarget? {
        switch mode {
        case .region:
            guard let region = await RegionSelector.selectRegion() else { return nil }
            return CaptureTarget(region: region)
        case .screen:
            let screen = await chooseScreenForCapture(cursorScreen: cursorScreen)
            guard let screen else { return nil }
            guard let region = CaptureRegion.fullScreen(for: screen) else { return nil }
            return CaptureTarget(region: region)
        case .window:
            guard let window = await WindowSelector.selectWindow(),
                  let region = captureRegion(for: window)
            else {
                return nil
            }
            return CaptureTarget(region: region)
        case .scrolling:
            return nil
        }
    }

    private func captureRegion(for window: SCWindow) -> CaptureRegion? {
        let screens = NSScreen.screens
        let windowRect = appKitRect(fromSCFrame: window.frame, screens: screens)
        let windowCenter = NSPoint(x: windowRect.midX, y: windowRect.midY)

        guard let screen = screens.first(where: { $0.frame.contains(windowCenter) })
            ?? screens.first(where: { $0.frame.intersects(windowRect) })
        else {
            return nil
        }

        guard let displayID = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID else {
            return nil
        }

        let clippedWindowRect = windowRect.intersection(screen.frame)
        guard !clippedWindowRect.isNull, !clippedWindowRect.isEmpty else {
            return nil
        }

        let localX = clippedWindowRect.minX - screen.frame.minX
        let localY = screen.frame.maxY - clippedWindowRect.maxY

        return CaptureRegion(
            sourceRect: CGRect(x: localX, y: localY, width: clippedWindowRect.width, height: clippedWindowRect.height),
            displayID: displayID,
            scaleFactor: screen.backingScaleFactor
        )
    }

    private func appKitRect(fromSCFrame scFrame: CGRect, screens: [NSScreen]) -> CGRect {
        let primaryScreenTop = screens.first?.frame.maxY ?? 0
        return CGRect(
            x: scFrame.minX,
            y: primaryScreenTop - scFrame.maxY,
            width: scFrame.width,
            height: scFrame.height
        )
    }

    private func pickScreen() async -> NSScreen? {
        let screen = await withCheckedContinuation { (continuation: CheckedContinuation<NSScreen?, Never>) in
            let picker = ScreenPickerWindow { screen in
                continuation.resume(returning: screen)
            }
            self.screenPickerWindow = picker
            picker.show()
        }
        DispatchQueue.main.async {
            self.screenPickerWindow = nil
        }
        return screen
    }

    private func chooseScreenForCapture(cursorScreen: NSScreen?) async -> NSScreen? {
        switch CaptureSettings.shared.multiMonitorCaptureMode {
        case .askEveryTime:
            if NSScreen.screens.count > 1 {
                return await pickScreen()
            }
            return mainScreen()
        case .displayUnderCursor:
            return cursorScreen ?? screenUnderMouseCursor() ?? mainScreen()
        case .mainDisplay:
            return mainScreen()
        }
    }

    private func mainScreen() -> NSScreen? {
        let mainDisplayID = CGMainDisplayID()
        return NSScreen.screens.first {
            ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID) == mainDisplayID
        } ?? NSScreen.screens.first
    }

    private func screenUnderMouseCursor() -> NSScreen? {
        let mouseLocation = NSEvent.mouseLocation
        return NSScreen.screens.first(where: { $0.frame.contains(mouseLocation) })
    }

    private func startMouseClickMonitoringIfNeeded(for type: CaptureType, region: CaptureRegion) {
        guard shouldCaptureMouseClicks(for: type) else {
            _ = stopMouseClickMonitoring()
            return
        }

        _ = stopMouseClickMonitoring()

        let monitor = MouseClickMonitor()
        monitor.start()
        mouseClickMonitor = monitor
        activeMouseClickRegion = region
        activeMouseClickCaptureType = type
    }

    private func stopMouseClickMonitoring() -> (type: CaptureType, region: CaptureRegion, events: [MouseClickEvent])? {
        guard let mouseClickMonitor, let activeMouseClickRegion, let activeMouseClickCaptureType else {
            return nil
        }

        let events = mouseClickMonitor.stop()
        self.mouseClickMonitor = nil
        self.activeMouseClickRegion = nil
        self.activeMouseClickCaptureType = nil

        return (activeMouseClickCaptureType, activeMouseClickRegion, events)
    }

    private func shouldCaptureMouseClicks(for type: CaptureType) -> Bool {
        if let activeMouseClickCaptureEnabledOverride {
            return activeMouseClickCaptureEnabledOverride
        }
        return CaptureSettings.shared.shouldShowMouseClickVisuals(for: type)
    }

    private func webcamCompanionURL(for primaryVideoURL: URL) -> URL {
        let stem = primaryVideoURL.deletingPathExtension().lastPathComponent
        return primaryVideoURL
            .deletingLastPathComponent()
            .appendingPathComponent("\(stem)-webcam")
            .appendingPathExtension("mp4")
    }

    private func nextRecordingSessionID() -> UInt64 {
        recordingSessionCounter += 1
        return recordingSessionCounter
    }

    private func debugRecordingLifecycle(_ message: String) {
#if DEBUG
        print("[CaptureManager] \(message)")
#endif
    }

    // Bridges synchronous CPU-heavy work to a background queue so it does not
    // block the @MainActor run loop (which would freeze the processing indicator).
    nonisolated private static func runOffMain<T>(_ work: @escaping () -> T) async -> T {
        await withCheckedContinuation { (continuation: CheckedContinuation<T, Never>) in
            DispatchQueue.global(qos: .userInitiated).async {
                continuation.resume(returning: work())
            }
        }
    }

    nonisolated private static func runOffMainThrowing<T>(_ work: @escaping () throws -> T) async throws -> T {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<T, Error>) in
            DispatchQueue.global(qos: .userInitiated).async {
                do {
                    continuation.resume(returning: try work())
                } catch {
                    continuation.resume(throwing: error)
                }
            }
        }
    }

    nonisolated private static func overlayVideoOffMain(
        sourceURL: URL,
        region: CaptureRegion,
        events: [MouseClickEvent],
        outputURL: URL,
        style: MouseClickOverlayStyle,
        onProgress: ((Double) -> Void)? = nil
    ) async throws -> URL {
        try await Task.detached(priority: .userInitiated) {
            try await MouseClickOverlayProcessor.overlayOnVideo(
                sourceURL: sourceURL,
                region: region,
                events: events,
                outputURL: outputURL,
                style: style,
                onProgress: onProgress
            )
        }.value
    }

    nonisolated private static func overlayBrandingVideoOffMain(
        sourceURL: URL,
        outputURL: URL,
        includeBranding: Bool,
        webcamOverlay: BrandingOverlayProcessor.WebcamOverlayOptions?,
        onProgress: ((Double) -> Void)? = nil
    ) async throws -> URL {
        try await Task.detached(priority: .userInitiated) {
            try await BrandingOverlayProcessor.overlayOnVideo(
                sourceURL: sourceURL,
                outputURL: outputURL,
                includeBranding: includeBranding,
                webcamOverlay: webcamOverlay,
                onProgress: onProgress
            )
        }.value
    }
}
