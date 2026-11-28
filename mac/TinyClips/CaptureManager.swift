import SwiftUI
import ScreenCaptureKit
import AVFoundation
import Combine
import Darwin

struct ScreenshotOutputPolicy: Equatable {
    enum Destination: Equatable {
        case saved
        case temporary
    }

    let saveToDisk: Bool
    let copyToClipboard: Bool
    let openEditor: Bool

    init(saveToDisk: Bool, copyToClipboard: Bool, openEditor: Bool) {
        self.saveToDisk = saveToDisk
        self.copyToClipboard = copyToClipboard
        self.openEditor = openEditor
    }

    init(settings: CaptureSettings) {
        self.init(
            saveToDisk: settings.screenshotOutputIsEnabled(.saveToDisk),
            copyToClipboard: settings.screenshotOutputIsEnabled(.copyToClipboard),
            openEditor: settings.screenshotOutputIsEnabled(.openEditor)
        )
    }

    var destination: Destination? {
        guard saveToDisk || copyToClipboard || openEditor else { return nil }
        return saveToDisk ? .saved : .temporary
    }
}

enum ScreenshotOutputPolicyError: LocalizedError {
    case noOutput

    var errorDescription: String? {
        switch self {
        case .noOutput:
            return "No screenshot output is enabled. Turn on saving, copying, or the editor in Screenshot Settings."
        }
    }
}

enum ScreenshotCopyRecoveryResult {
    case available
    case retained
    case discarded
}

enum ScreenshotOutputRouter {
    static func deliver(
        policy: ScreenshotOutputPolicy,
        recordSaved: () -> Void,
        copy: () -> Bool,
        recoverCopy: () -> ScreenshotCopyRecoveryResult,
        openEditor: () -> Bool,
        releaseTemporary: () -> Void,
        outputSucceeded: () -> Void = {}
    ) throws -> Bool {
        guard policy.destination != nil else { throw ScreenshotOutputPolicyError.noOutput }
        if policy.saveToDisk {
            recordSaved()
            outputSucceeded()
        }
        if policy.copyToClipboard {
            if copy() {
                outputSucceeded()
            } else {
                switch recoverCopy() {
                case .available:
                    outputSucceeded()
                case .retained:
                    if !policy.openEditor { return false }
                case .discarded:
                    return false
                }
            }
        }
        if policy.openEditor {
            return openEditor()
        }
        releaseTemporary()
        return false
    }
}

enum ScreenshotMemoryRecoveryAction {
    case retry
    case copy
    case saveAs
    case discard
    case showAgain
}

enum ScreenshotMemoryRecoveryResult<Source> {
    case useSource(Source, copied: Bool, saved: Bool)
    case finished
}

enum ScreenshotMemoryRecovery {
    static func recover<Source>(
        initialError: Error,
        requiresSave: Bool,
        requiresEditor: Bool,
        choose: (_ error: String, _ copied: Bool) -> ScreenshotMemoryRecoveryAction,
        retry: () throws -> Source,
        copy: () -> Bool,
        saveAs: () -> Source?
    ) -> ScreenshotMemoryRecoveryResult<Source> {
        var error = initialError.localizedDescription
        var copied = false
        while true {
            switch choose(error, copied) {
            case .retry:
                do {
                    return .useSource(try retry(), copied: copied, saved: false)
                } catch let retryError {
                    error = retryError.localizedDescription
                }
            case .copy:
                if copy() {
                    copied = true
                    if !requiresSave && !requiresEditor { return .finished }
                } else {
                    error = "Could not copy the screenshot to the clipboard."
                }
            case .saveAs:
                if let source = saveAs() {
                    return .useSource(source, copied: copied, saved: true)
                }
            case .discard:
                return .finished
            case .showAgain:
                break
            }
        }
    }
}

enum ScreenshotUnavailableSourceRecovery {
    static func ifMissing<Source>(
        sourceExists: Bool,
        saveToDisk: Bool,
        recover: () -> ScreenshotMemoryRecoveryResult<Source>,
        recordSavedRetry: (Source) -> Void
    ) -> ScreenshotMemoryRecoveryResult<Source>? {
        guard !sourceExists else { return nil }
        let result = recover()
        if case let .useSource(source, _, saved) = result, saveToDisk, !saved {
            recordSavedRetry(source)
        }
        return result
    }
}

struct ScreenshotCaptureRequest {
    let policy: ScreenshotOutputPolicy
    let encoding: ScreenshotEncodingOptions
    let temporaryFile: ScreenshotTemporaryFileLease?
    let temporaryRoot: URL

    init(
        settings: CaptureSettings,
        temporaryRoot: URL = TinyClipsTemporaryFiles.directoryURL
    ) throws {
        let policy = ScreenshotOutputPolicy(settings: settings)
        guard let destination = policy.destination else { throw ScreenshotOutputPolicyError.noOutput }
        let encoding = ScreenshotEncodingOptions(settings: settings)
        self.policy = policy
        self.encoding = encoding
        self.temporaryRoot = temporaryRoot
        switch destination {
        case .saved:
            temporaryFile = nil
        case .temporary:
            temporaryFile = try TinyClipsTemporaryFiles.makeScreenshotLease(
                fileExtension: encoding.format.rawValue,
                root: temporaryRoot
            )
        }
    }
}

@MainActor
final class ScreenshotOutputSuccess {
    private var recorded = false
    private let record: () -> Void

    init(record: @escaping () -> Void) {
        self.record = record
    }

    func recordOnce() {
        guard !recorded else { return }
        recorded = true
        record()
    }
}

@MainActor
final class ScreenshotRecoveryFrame {
    var image: CGImage?
    let scaleFactor: CGFloat
    let request: ScreenshotCaptureRequest
    let outputSuccess = ScreenshotOutputSuccess {
        CaptureAnalyticsStore.shared.recordCapture(.screenshot)
    }

    init(image: CGImage, scaleFactor: CGFloat, request: ScreenshotCaptureRequest) {
        self.image = image
        self.scaleFactor = scaleFactor
        self.request = request
    }
}

enum ScreenshotTemporaryFileError: LocalizedError {
    case unsafeDirectory
    case invalidExtension
    case missingImage

    var errorDescription: String? {
        switch self {
        case .unsafeDirectory:
            return "The temporary screenshot folder is not available."
        case .invalidExtension:
            return "The temporary screenshot format is not supported."
        case .missingImage:
            return "The temporary screenshot is no longer available."
        }
    }
}

final class ScreenshotTemporaryFileLease: @unchecked Sendable {
    let url: URL
    private let lock = NSLock()
    private var isActive = true
    private var isRetained = false

    fileprivate init(url: URL) {
        self.url = url
    }

    func keepForRecovery() throws {
        lock.lock()
        defer { lock.unlock() }
        guard isActive || isRetained,
              FileManager.default.fileExists(atPath: url.path) else {
            throw ScreenshotTemporaryFileError.missingImage
        }
        TinyClipsTemporaryFiles.retainScreenshot(url)
        isActive = false
        isRetained = true
    }

    func discard() {
        lock.lock()
        defer { lock.unlock() }
        guard isActive else { return }
        do {
            try TinyClipsTemporaryFiles.releaseScreenshot(url)
        } catch {
            TinyClipsTemporaryFiles.retainScreenshot(url)
            NSLog("Could not remove temporary screenshot \(url.path): \(error.localizedDescription)")
            isRetained = true
        }
        isActive = false
    }

    func discardExplicitly() throws {
        lock.lock()
        defer { lock.unlock() }
        guard isActive || isRetained else { return }
        try TinyClipsTemporaryFiles.releaseScreenshot(url)
        isActive = false
        isRetained = false
    }

    deinit {
        discard()
    }
}

final class TemporaryEditorFileProtection: @unchecked Sendable {
    private let path: String
    private let lock = NSLock()
    private var isActive = true

    fileprivate init(path: String) {
        self.path = path
    }

    func release() {
        lock.lock()
        let shouldRelease = isActive
        isActive = false
        lock.unlock()
        if shouldRelease {
            TinyClipsTemporaryFiles.releaseEditorProtection(path)
        }
    }

    deinit {
        release()
    }
}

enum TinyClipsTemporaryFiles {
    private static let filenamePrefix = "TinyClips-"
    private static let screenshotDirectoryName = "TinyClips-Screenshots"
    private final class Ownership: @unchecked Sendable {
        let lock = NSLock()
        var activePaths: Set<String> = []
        var retainedPaths: Set<String> = []
        var editorPathCounts: [String: Int] = [:]
    }

    private static let ownership = Ownership()

    struct Summary: Sendable {
        let fileCount: Int
        let totalSize: Int64
        let protectedCount: Int
    }

    struct PurgeReport: Sendable {
        let removedCount: Int
        let protectedCount: Int
    }

    static var directoryURL: URL {
        FileManager.default.temporaryDirectory
    }

    private static func screenshotDirectory(in root: URL) -> URL {
        root.appendingPathComponent(screenshotDirectoryName, isDirectory: true)
    }

    static func makeURL(fileExtension: String) -> URL {
        directoryURL
            .appendingPathComponent("\(filenamePrefix)\(UUID().uuidString)")
            .appendingPathExtension(fileExtension)
    }

    static func makeScreenshotLease(
        fileExtension: String,
        root: URL = directoryURL
    ) throws -> ScreenshotTemporaryFileLease {
        let ext = fileExtension.lowercased()
        guard ["png", "jpg", "jpeg", "webp"].contains(ext) else {
            throw ScreenshotTemporaryFileError.invalidExtension
        }
        let directory = screenshotDirectory(in: root)
        try validateScreenshotDirectory(directory)
        try FileManager.default.createDirectory(
            at: directory,
            withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700]
        )
        try validateScreenshotDirectory(directory)

        var url: URL
        repeat {
            url = directory
                .appendingPathComponent("\(filenamePrefix)\(UUID().uuidString)")
                .appendingPathExtension(ext)
        } while FileManager.default.fileExists(atPath: url.path)

        let ownershipPath = canonicalPath(url)
        ownership.lock.lock()
        ownership.activePaths.insert(ownershipPath)
        ownership.lock.unlock()
        return ScreenshotTemporaryFileLease(url: url)
    }

    private static func validateScreenshotDirectory(_ directory: URL) throws {
        if let attributes = try? FileManager.default.attributesOfItem(atPath: directory.path) {
            guard attributes[.type] as? FileAttributeType == .typeDirectory else {
                throw ScreenshotTemporaryFileError.unsafeDirectory
            }
        }
    }

    fileprivate static func retainScreenshot(_ url: URL) {
        ownership.lock.lock()
        defer { ownership.lock.unlock() }
        let path = canonicalPath(url)
        ownership.activePaths.remove(path)
        ownership.retainedPaths.insert(path)
    }

    fileprivate static func releaseScreenshot(_ url: URL) throws {
        ownership.lock.lock()
        defer { ownership.lock.unlock() }
        let path = canonicalPath(url)
        guard ownership.activePaths.contains(path) || ownership.retainedPaths.contains(path) else { return }
        do {
            try FileManager.default.removeItem(at: url)
        } catch {
            let fileError = error as NSError
            guard fileError.domain == NSCocoaErrorDomain,
                  fileError.code == NSFileNoSuchFileError || fileError.code == NSFileReadNoSuchFileError else {
                throw error
            }
        }
        ownership.activePaths.remove(path)
        ownership.retainedPaths.remove(path)
    }

    static func protectEditorFile(_ url: URL) -> TemporaryEditorFileProtection {
        let path = canonicalPath(url)
        ownership.lock.lock()
        ownership.editorPathCounts[path, default: 0] += 1
        ownership.lock.unlock()
        return TemporaryEditorFileProtection(path: path)
    }

    fileprivate static func releaseEditorProtection(_ path: String) {
        ownership.lock.lock()
        defer { ownership.lock.unlock() }
        guard let count = ownership.editorPathCounts[path] else { return }
        if count == 1 {
            ownership.editorPathCounts.removeValue(forKey: path)
        } else {
            ownership.editorPathCounts[path] = count - 1
        }
    }

    @discardableResult
    static func removeStaleFiles(
        olderThan date: Date,
        avoiding directories: [URL] = [],
        root: URL = directoryURL
    ) throws -> Int {
        try removeMatchingFiles(olderThan: date, avoiding: directories, root: root).removedCount
    }

    @discardableResult
    static func purge(
        avoiding directories: [URL] = [],
        root: URL = directoryURL
    ) throws -> PurgeReport {
        try removeMatchingFiles(olderThan: nil, avoiding: directories, root: root)
    }

    static func summary(
        avoiding directories: [URL] = [],
        root: URL = directoryURL
    ) throws -> Summary {
        var fileCount = 0
        var totalSize: Int64 = 0
        var protectedCount = 0
        for fileURL in try candidateFiles(in: root) {
            guard let values = try? fileURL.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey]),
                  values.isRegularFile == true, values.isSymbolicLink != true else { continue }
            fileCount += 1
            totalSize += Int64(values.fileSize ?? 0)
            ownership.lock.lock()
            let protected = isProtected(fileURL, root: root, avoiding: directories)
            ownership.lock.unlock()
            if protected {
                protectedCount += 1
            }
        }
        return Summary(fileCount: fileCount, totalSize: totalSize, protectedCount: protectedCount)
    }

    private static func removeMatchingFiles(
        olderThan date: Date?,
        avoiding directories: [URL],
        root: URL
    ) throws -> PurgeReport {
        let fileManager = FileManager.default
        var removedCount = 0
        var protectedCount = 0
        for fileURL in try candidateFiles(in: root) {
            guard let values = try? fileURL.resourceValues(
                forKeys: [.isRegularFileKey, .isSymbolicLinkKey, .contentModificationDateKey]
            ), values.isRegularFile == true, values.isSymbolicLink != true else { continue }
            if let date {
                guard let modified = values.contentModificationDate, modified < date else { continue }
            }
            ownership.lock.lock()
            if isProtected(fileURL, root: root, avoiding: directories) {
                protectedCount += 1
                ownership.lock.unlock()
                continue
            }
            do {
                try fileManager.removeItem(at: fileURL)
                removedCount += 1
                ownership.lock.unlock()
            } catch {
                ownership.lock.unlock()
                throw error
            }
        }
        return PurgeReport(removedCount: removedCount, protectedCount: protectedCount)
    }

    private static func candidateFiles(in root: URL) throws -> [URL] {
        let fileManager = FileManager.default
        let keys: [URLResourceKey] = [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey, .contentModificationDateKey]
        let rootFiles = try fileManager.contentsOfDirectory(
            at: root,
            includingPropertiesForKeys: keys,
            options: [.skipsHiddenFiles]
        ).filter(isTinyClipsTemporaryFile)
        let screenshots = screenshotDirectory(in: root)
        guard let attributes = try? fileManager.attributesOfItem(atPath: screenshots.path),
              attributes[.type] as? FileAttributeType == .typeDirectory else {
            return rootFiles
        }
        let screenshotFiles = try fileManager.contentsOfDirectory(
            at: screenshots,
            includingPropertiesForKeys: keys,
            options: [.skipsHiddenFiles]
        ).filter(isScreenshotTemporaryCandidate)
        return rootFiles + screenshotFiles
    }

    private static func isManagedScreenshotCandidate(_ url: URL, root: URL) -> Bool {
        isScreenshotTemporaryCandidate(url)
            && canonicalPath(url.deletingLastPathComponent()) == canonicalPath(screenshotDirectory(in: root))
    }

    private static func isProtected(
        _ url: URL,
        root: URL,
        avoiding directories: [URL]
    ) -> Bool {
        let path = canonicalPath(url)
        if ownership.activePaths.contains(path)
            || ownership.editorPathCounts[path] != nil
            || ownership.retainedPaths.contains(path) {
            return true
        }
        // Capture folders protect legacy root files, not the app-private screenshot directory.
        return !isManagedScreenshotCandidate(url, root: root)
            && isInsideAnyDirectory(url, directories: directories)
    }

    private static func canonicalPath(_ url: URL) -> String {
        CaptureSettings.resolvingSymlinksInExistingPath(url).standardizedFileURL.path
    }

    static func isInsideAnyDirectory(_ url: URL, directories: [URL]) -> Bool {
        let path = CaptureSettings.resolvingSymlinksInExistingPath(url).pathComponents.map { $0.lowercased() }
        return directories.contains { directory in
            let parent = CaptureSettings.resolvingSymlinksInExistingPath(directory).pathComponents.map { $0.lowercased() }
            return path.count >= parent.count && Array(path.prefix(parent.count)) == parent
        }
    }

    static func canSaveScreenshotPermanently(at url: URL, root: URL = directoryURL) -> Bool {
        !isInsideAnyDirectory(url, directories: [screenshotDirectory(in: root)])
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

    private static func isScreenshotTemporaryCandidate(_ fileURL: URL) -> Bool {
        guard ["png", "jpg", "jpeg", "webp"].contains(fileURL.pathExtension.lowercased()) else { return false }
        let stem = fileURL.deletingPathExtension().lastPathComponent
        guard stem.hasPrefix(filenamePrefix) else { return false }
        return UUID(uuidString: String(stem.dropFirst(filenamePrefix.count))) != nil
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
        timeLimitMinutes: Int,
        studioModeEnabled: Bool,
        automationDevices: AutomationRecordingDevices?
    )
    case gif(target: CaptureTarget, mouseClicksEnabled: Bool)
}

private enum StudioSegmentReceiver {
    case editor(CheckedContinuation<StudioProject?, Error>)
    case automation(validate: @MainActor () throws -> Void, attach: @MainActor (String) async throws -> Void)

    @MainActor
    func validateStart() throws {
        if case .automation(let validate, _) = self { try validate() }
    }
}

@MainActor
class CaptureManager: ObservableObject {
    static weak var current: CaptureManager?

    @Published var isRecording = false {
        didSet {
            configureGlobalHotKeys()
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
    @Published private(set) var isOCRInFlight = false {
        didSet {
            if oldValue && !isOCRInFlight {
                scheduleStoreReviewWhenCaptureIsIdle()
            }
        }
    }
    @Published private(set) var isCapturePreparationInProgress = false {
        didSet {
            if oldValue && !isCapturePreparationInProgress {
                scheduleStoreReviewWhenCaptureIsIdle()
            }
        }
    }
    @Published private(set) var isScreenshotCaptureInProgress = false
    private var screenshotRecoveryDepth = 0
    var isScreenshotRecoveryInProgress: Bool { screenshotRecoveryDepth > 0 }
    @Published private(set) var isRecordingSetupInProgress = false {
        didSet {
            if !isRecordingSetupInProgress {
                scheduleStoreReviewWhenCaptureIsIdle()
            }
        }
    }
    @Published private(set) var captureHotKeyRegistrationError: String?

    var hotKeyRegistrationError: String? {
        captureHotKeyRegistrationError
    }

    var isCaptureActionInProgress: Bool {
        studioSegmentReceiver != nil
            || isDiscardingRecording
            || isOCRInFlight
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

    // MARK: - Automation state

    var isCountdownActive: Bool { countdownWindow != nil }
    var isStudioSegmentCountdown: Bool { studioSegmentReceiver != nil && isCountdownActive }

    /// True while a stopped recording is still being finalized and saved.
    var isFinalizingRecording: Bool { isStoppingRecording }
    var isAnyCaptureInProgress: Bool {
        isCaptureActionInProgress || isRecording || isFinalizingRecording
    }
    var isCaptureWorkActive: Bool {
        isOCRInFlight
            || isCapturePreparationInProgress
            || isScreenshotCaptureInProgress
            || isRecordingSetupInProgress
            || isRecording
            || isFinalizingRecording
            || RegionSelector.isSelecting
            || WindowSelector.isSelecting
            || scrollingCaptureSession != nil
            || videoRecorder != nil
            || webcamRecorder != nil
            || gifWriter != nil
    }

    private(set) var automationRecordingError: AutomationError?

    /// The take a studio.timeline.record recording left, kept for recording.status until the next
    /// capture starts. The take is always a separate Library draft; inserted says whether the
    /// destination project received its own copy of it.
    private(set) var automationStudioTake: (takeProjectID: String, inserted: Bool)?

    var automationRecordingDevices: AutomationRecordingDevices {
        AutomationRecordingDevices(
            microphone: isRecording && videoRecorder?.isMicrophoneCaptureRunning == true,
            camera: isRecording && webcamRecorder?.previewSession?.isRunning == true
        )
    }

    var automationScrollingCaptureStatus: (active: Bool, stopping: Bool, frameCount: Int) {
        (scrollingCaptureSession != nil, isStoppingScrollingCapture, scrollingCaptureFrameCount)
    }

    var activeRecordingType: CaptureType? {
        switch activeRecordingRequest {
        case .video: return .video
        case .gif: return .gif
        case nil: return nil
        }
    }

    /// Runs an unattended agent capture while marking a screenshot as in progress, so other
    /// captures and agent commands see the app as busy.
    func performAutomationCapture<T>(_ work: () async throws -> T) async throws -> T {
        isScreenshotCaptureInProgress = true
        defer { isScreenshotCaptureInProgress = false }
        return try await work()
    }

    private func showVideoCodecFallbackNoticeOnce(_ message: String) {
        guard !didShowVideoCodecFallbackNotice else { return }
        didShowVideoCodecFallbackNotice = true
        SaveService.shared.showNotice(message)
    }

    private var videoRecorder: VideoRecorder?
    private var webcamRecorder: WebcamRecorder?
    private var gifWriter: GifWriter?
    private let idleSleepAssertion = IdleSleepAssertion()
    private(set) var lastVideoRecordingArtifacts: VideoRecordingArtifacts?
    private var didShowVideoCodecFallbackNotice = false
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
    /// Whether the video recording being set up was started with Studio Recording. Set by
    /// `startVideoRecording(forStudio:)` and kept through the picker, the Record panel, and a
    /// picker that comes back after the recording, until a recording is asked for again.
    private var pendingVideoIsForStudio = false
    private var pendingRecordingCountdownEnabled: Bool = true
    private var pendingRecordingCountdownDuration: Int = 3
    private var pendingVideoTimeLimitMinutes: Int = 0
    private var recordPanelPosition: NSPoint?
    private var trimmerWindow: VideoTrimmerWindow?
    private var gifTrimmerWindow: GifTrimmerWindow?
    @Published private var countdownWindow: CountdownWindow?
    private var processingIndicatorWindow: ProcessingIndicatorWindow?
    @Published private var scrollingCapturePanel: ScrollingCapturePanel?
    private var scrollingCaptureSession: ScrollingPanoramaCapture?
    private var scrollingCapturePanelPosition: NSPoint?
    private var scrollingCaptureScaleFactor: CGFloat = 1
    private var scrollingCaptureFrameCount = 0
    private var scrollingCaptureIsAutomation = false
    private var scrollingScreenshotRequest: ScreenshotCaptureRequest?
    private var isStoppingScrollingCapture = false
    private var processingIndicatorShownAt: Date?
    private var isStoppingRecording = false
    @Published private var isDiscardingRecording = false
    private var stopRecordingTask: Task<Void, Never>?
    private var videoAutoStopTask: Task<Void, Never>?
    private var recordingSessionCounter: UInt64 = 0
    private var activeRecordingSessionID: UInt64?
    private var finalizingSessionID: UInt64?
    private var activeRecordingRequest: ActiveRecordingRequest?
    private var activeRecordingWasStartedByAutomation = false
    private var onboardingWindow: OnboardingWizardWindow?
    private var whatsNewWindow: WhatsNewWindow?
    private var guideWindow: GuideWindow?
    @Published private var screenPickerWindow: ScreenPickerWindow?
    private var mouseClickMonitor: MouseClickMonitor?
    private var activeMouseClickRegion: CaptureRegion?
    private var activeMouseClickCaptureType: CaptureType?
    private var activeMouseClickCaptureEnabledOverride: Bool?
    private var studioRecordingCoordinator: StudioRecordingCoordinator?
    /// Reserved from target selection until every recorder has stopped. Other captures must not
    /// replace this request while a panel is handing off to the next step.
    @Published private var studioSegmentReceiver: StudioSegmentReceiver?
    private var studioSegmentInputError: Error?
    private var activeWebcamOverlaySelection: StartRecordingPanel.WebcamSelection?
    private var webcamPositionEvents: [BrandingOverlayProcessor.WebcamPositionEvent] = []
    private let hotKeyManager = HotKeyManager()
    private var hotKeySettingsCancellable: AnyCancellable?
    private var shouldIgnoreNextHotKeySettingsChange = false

    init() {
        guard !TinyClipsRuntime.isRunningUnitTests else { return }
        Self.current = self

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

        // Old Studio project sources are removed by the storage settings, shortly after launch.
        Task { @MainActor in
            try? await Task.sleep(nanoseconds: 10_000_000_000)
            StudioMaintenance.cleanUp()
        }
    }

    private func bringWindowToFront(_ window: NSWindow) {
        WindowPresenter.shared.present(window)
    }

    private func configureGlobalHotKeys() {
        let captureActions = HotKeyAction.allCases.filter { $0 != .stopRecording }
        let result: HotKeyManager.RegistrationResult

        if !isRecording {
            hotKeyManager.unregisterAction(.stopRecording)
            result = registerCaptureHotKeys(
                CaptureSettings.shared.hotKeyBindings,
                actions: captureActions
            )
        } else {
            let bindings = CaptureSettings.shared.hotKeyBindings
            let captureResult = registerCaptureHotKeys(bindings, actions: captureActions)
            let stopResult = registerCaptureHotKeys(bindings, actions: [.stopRecording])
            result = HotKeyManager.RegistrationResult(
                failures: captureResult.failures + stopResult.failures,
                restorationFailures: captureResult.restorationFailures + stopResult.restorationFailures
            )
        }

        if isRecording, let error = result.errorMessage, error != captureHotKeyRegistrationError {
            SaveService.shared.showError("\(error) Use the floating Stop button or menu to stop recording.")
        }
        captureHotKeyRegistrationError = result.errorMessage
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
        _ bindings: [HotKeyAction: HotKeyBinding],
        actions: [HotKeyAction] = HotKeyAction.allCases
    ) -> HotKeyManager.RegistrationResult {
        let registrations = actions.compactMap { action -> HotKeyManager.ActionRegistration? in
            guard let binding = bindings[action] else { return nil }
            return HotKeyManager.ActionRegistration(
                action: action,
                binding: binding,
                isEnabled: action.isGloballyActive(isRecording: isRecording),
                handler: { [weak self] in
                    guard let self else { return }
                    if action != .stopRecording && (self.isRecording || self.isCaptureActionInProgress) {
                        return
                    }
                    switch action {
                    case .screenshot:
                        self.takeScreenshot()
                    case .recordVideo:
                        self.startVideoRecording()
                    case .studioRecording:
                        self.startVideoRecording(forStudio: true)
                    case .recordGif:
                        self.startGifRecording()
                    case .copyTextFromRegion:
                        self.copyTextFromRegion()
                    case .screenshotRegion:
                        self.takeScreenshotInMode(.region)
                    case .screenshotWindow:
                        self.takeScreenshotInMode(.window)
                    case .screenshotScreen:
                        self.takeScreenshotInMode(.screen)
                    case .stopRecording:
                        if self.isRecording {
                            self.stopRecording()
                        }
                    }
                }
            )
        }
        return hotKeyManager.registerActionHotKeys(registrations)
    }

    func takeScreenshot() {
        beginScreenshot(directMode: nil)
    }

    /// Takes a screenshot in the given mode without showing the capture picker.
    /// Used by the dedicated region/screen/window screenshot hotkeys.
    func takeScreenshotInMode(_ mode: CapturePickerMode) {
        beginScreenshot(directMode: mode)
    }

    /// Shared screenshot entry point. With `directMode == nil` the capture-picker setting decides
    /// between the picker and a direct region capture; a non-nil mode always bypasses the picker.
    private func beginScreenshot(directMode: CapturePickerMode?) {
        guard !isCaptureActionInProgress else { return }

        isCapturePreparationInProgress = true
        let cursorScreen = screenUnderMouseCursor()
        Task {
            defer { isCapturePreparationInProgress = false }
            guard await prepareForNewCaptureRequest() else { return }
            guard await PermissionManager.shared.checkPermission() else { return }
            guard !isOCRInFlight else { return }
            let settings = CaptureSettings.shared
            if directMode == nil, settings.shouldShowCapturePicker(for: .screenshot) {
                showScreenshotPicker(cursorScreen: cursorScreen)
            } else {
                await performScreenshotCapture(
                    mode: directMode ?? .region,
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
        // A previous editor's close callback may request its picker during a new Studio take.
        guard screenshotPickerPanel == nil, studioSegmentReceiver == nil else { return }
        dismissRecordingPicker()
        let settings = CaptureSettings.shared
        let panel = CapturePickerPanel(
            intent: .screenshot,
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
                self?.scheduleStoreReviewWhenCaptureIsIdle()
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

        let request: ScreenshotCaptureRequest
        do {
            request = try ScreenshotCaptureRequest(
                settings: .shared
            )
        } catch {
            SaveService.shared.showError("Screenshot could not start: \(error.localizedDescription)")
            if shouldReturnToPicker {
                showScreenshotPicker(cursorScreen: cursorScreen)
            }
            return
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
                request: request,
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
                request: request,
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
                request: request,
                countdownEnabled: countdownEnabled,
                countdownDuration: countdownDuration,
                shouldReturnToPickerAfterCapture: shouldReturnToPicker
            )

        case .windowArea:
            guard let window = await WindowSelector.selectWindowArea(),
                  let selection = WindowAreaCaptureResolver.resolve(window: window)
            else {
                if shouldReturnToPicker {
                    showScreenshotPicker(cursorScreen: cursorScreen)
                }
                return
            }
            doScreenshotCapture(
                region: selection.region,
                window: nil,
                request: request,
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
            do {
                try await startScrollingCapture(
                    region: region,
                    shouldReturnToPickerAfterCapture: shouldReturnToPicker,
                    screenshotRequest: request
                )
            } catch {
                debugPrint("Scrolling capture could not start: \(error)")
            }
        }
    }

    private func startScrollingCapture(
        region: CaptureRegion,
        shouldReturnToPickerAfterCapture: Bool,
        screenshotRequest: ScreenshotCaptureRequest? = nil,
        isAutomation: Bool = false
    ) async throws {
        guard scrollingCaptureSession == nil else {
            throw AutomationError(.busy, "A scrolling capture is already active.")
        }
        scrollingCaptureScaleFactor = region.scaleFactor
        scrollingCaptureFrameCount = 0
        scrollingCaptureIsAutomation = isAutomation
        scrollingScreenshotRequest = screenshotRequest
        isScreenshotCaptureInProgress = true
        isStoppingScrollingCapture = false
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
                guard let session else { return }
                self.finishScrollingCapture(
                    session: session,
                    with: error,
                    shouldReturnToPicker: shouldReturnToPickerAfterCapture
                )
            }
        }
        session.onProgress = { [weak self, weak session] count in
            Task { @MainActor [weak self, weak session] in
                guard let self, let session, self.scrollingCaptureSession === session else { return }
                self.scrollingCaptureFrameCount = count
                self.scrollingCapturePanel?.updateFrameCount(count)
            }
        }
        session.onLimitReached = { [weak self, weak session] reason in
            Task { @MainActor [weak self, weak session] in
                guard let self, let session, self.scrollingCaptureSession === session else { return }
                self.scrollingCapturePanel?.showStatus(reason.message)
                AccessibilityAnnouncementService.shared.announce(reason.message, priority: .high)
                if self.scrollingCaptureIsAutomation {
                    do {
                        _ = try await self.stopAutomationScrollingCapture()
                    } catch {
                        SaveService.shared.showError("Scrolling capture failed: \(error.localizedDescription)")
                    }
                    return
                }
                self.stopScrollingCapture(
                    session: session,
                    shouldReturnToPicker: shouldReturnToPickerAfterCapture
                )
            }
        }
        let panel = ScrollingCapturePanel(
            onStop: { [weak self, weak session] in
                guard let self, let session else { return }
                if self.scrollingCaptureIsAutomation {
                    Task {
                        do {
                            _ = try await self.stopAutomationScrollingCapture()
                        } catch {
                            SaveService.shared.showError("Scrolling capture failed: \(error.localizedDescription)")
                        }
                    }
                    return
                }
                self.stopScrollingCapture(
                    session: session,
                    shouldReturnToPicker: shouldReturnToPickerAfterCapture
                )
            },
            onCancel: { [weak self, weak session] in
                guard let self, let session else { return }
                session.cancel()
                self.finishScrollingCapture(
                    session: session,
                    with: PanoramaCaptureError.cancelled,
                    shouldReturnToPicker: shouldReturnToPickerAfterCapture
                )
            }
        )
        scrollingCapturePanel = panel
        panel.show(at: scrollingCapturePanelPosition)

        dismissRegionIndicator()
        if CaptureSettings.shared.showRegionIndicator {
            let indicator = RegionIndicatorPanel(region: region)
            indicator.show()
            regionIndicatorPanel = indicator
        }

        do {
            try await session.start(region: region)
        } catch {
            finishScrollingCapture(
                session: session,
                with: error,
                shouldReturnToPicker: shouldReturnToPickerAfterCapture
            )
            throw AutomationError(.internal, "Scrolling capture failed to start: \(error.localizedDescription)")
        }
    }

    func startAutomationScrollingCapture(region: CaptureRegion) async throws {
        guard !isCaptureActionInProgress else {
            throw AutomationError(.busy, "Tiny Clips is already capturing or recording.")
        }
        isCapturePreparationInProgress = true
        defer { isCapturePreparationInProgress = false }
        guard await prepareForNewCaptureRequest() else {
            throw AutomationError(.busy, "Tiny Clips is already capturing or recording.")
        }
        try await startScrollingCapture(
            region: region,
            shouldReturnToPickerAfterCapture: false,
            isAutomation: true
        )
    }

    func stopAutomationScrollingCapture() async throws -> URL {
        guard scrollingCaptureIsAutomation,
              let session = scrollingCaptureSession,
              !isStoppingScrollingCapture else {
            throw AutomationError(.busy, "No automated scrolling capture is active.")
        }
        isStoppingScrollingCapture = true
        scrollingCapturePanel?.markCompleted()
        let scaleFactor = scrollingCaptureScaleFactor
        do {
            let image = try await session.stop()
            finishScrollingCapture(session: session, shouldReturnToPicker: false)
            let outputURL = SaveService.shared.generateURL(for: .screenshot)
            let url = try ScreenshotCapture.saveImage(image, to: outputURL, scaleFactor: scaleFactor)
            CaptureAnalyticsStore.shared.recordCapture(.screenshot)
            SaveService.shared.handleSavedFile(url: url, type: .screenshot)
            isScreenshotCaptureInProgress = false
            notifyStoreReviewCaptureWorkBecameIdle()
            return url
        } catch {
            finishScrollingCapture(session: session, with: error, shouldReturnToPicker: false)
            isScreenshotCaptureInProgress = false
            notifyStoreReviewCaptureWorkBecameIdle()
            throw AutomationError(.internal, "Scrolling capture failed: \(error.localizedDescription)")
        }
    }

    func cancelAutomationScrollingCapture() throws {
        guard scrollingCaptureIsAutomation, let session = scrollingCaptureSession else {
            throw AutomationError(.busy, "No automated scrolling capture is active.")
        }
        session.cancel()
        finishScrollingCapture(
            session: session,
            with: PanoramaCaptureError.cancelled,
            shouldReturnToPicker: false
        )
        isScreenshotCaptureInProgress = false
        notifyStoreReviewCaptureWorkBecameIdle()
    }

    @MainActor
    private func stopScrollingCapture(
        session: ScrollingPanoramaCapture,
        shouldReturnToPicker: Bool
    ) {
        guard scrollingCaptureSession === session, !isStoppingScrollingCapture else { return }
        isStoppingScrollingCapture = true
        scrollingCapturePanel?.markCompleted()
        Task {
            do {
                let image = try await session.stop()
                self.finishScrollingCapture(
                    session: session,
                    image: image,
                    shouldReturnToPicker: shouldReturnToPicker
                )
            } catch {
                self.finishScrollingCapture(
                    session: session,
                    with: error,
                    shouldReturnToPicker: shouldReturnToPicker
                )
            }
        }
    }

    @MainActor
    private func finishScrollingCapture(
        session: ScrollingPanoramaCapture,
        image: CGImage? = nil,
        with error: Error? = nil,
        shouldReturnToPicker: Bool
    ) {
        guard scrollingCaptureSession === session else { return }
        let wasAutomation = scrollingCaptureIsAutomation
        let imageScaleFactor = scrollingCaptureScaleFactor
        let request = scrollingScreenshotRequest
        if let panel = scrollingCapturePanel {
            scrollingCapturePanelPosition = panel.frame.origin
        }
        scrollingCapturePanel?.dismiss()
        scrollingCapturePanel = nil
        dismissRegionIndicator()
        scrollingCaptureSession = nil
        scrollingCaptureScaleFactor = 1
        scrollingCaptureFrameCount = 0
        scrollingCaptureIsAutomation = false
        scrollingScreenshotRequest = nil
        isStoppingScrollingCapture = false
        if !wasAutomation, image == nil {
            isScreenshotCaptureInProgress = false
            notifyStoreReviewCaptureWorkBecameIdle()
        }

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
        guard let request else {
            isScreenshotCaptureInProgress = false
            notifyStoreReviewCaptureWorkBecameIdle()
            SaveService.shared.showError("Scrolling capture failed: Screenshot output settings are unavailable.")
            if shouldReturnToPicker,
               CaptureSettings.shared.shouldShowCapturePickerAfterCapture(for: .screenshot) {
                showScreenshotPicker()
            }
            return
        }

        Task {
            defer {
                self.isScreenshotCaptureInProgress = false
                self.notifyStoreReviewCaptureWorkBecameIdle()
            }
            var didPresentEditor = false
            do {
                didPresentEditor = try self.deliverScreenshot(
                    image,
                    scaleFactor: imageScaleFactor,
                    request: request,
                    reopenPickerAfterClose: shouldReturnToPicker
                )
            } catch {
                SaveService.shared.showError("Scrolling capture failed: \(error.localizedDescription)")
            }
            if !didPresentEditor {
                if shouldReturnToPicker,
                   CaptureSettings.shared.shouldShowCapturePickerAfterCapture(for: .screenshot) {
                    self.showScreenshotPicker()
                }
            }
        }
    }

    private func doScreenshotCapture(
        region: CaptureRegion?,
        window: SCWindow?,
        request: ScreenshotCaptureRequest,
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
                defer {
                    self.isScreenshotCaptureInProgress = false
                    self.notifyStoreReviewCaptureWorkBecameIdle()
                }
                var didPresentEditor = false
                do {
                    let captured: (image: CGImage, scaleFactor: CGFloat)
                    if let window {
                        captured = try await ScreenshotCapture.captureWindowImage(window)
                    } else if let region {
                        captured = try await ScreenshotCapture.captureImageWithScale(region: region)
                    } else {
                        return
                    }

                    didPresentEditor = try self.deliverScreenshot(
                        captured.image,
                        scaleFactor: captured.scaleFactor,
                        request: request,
                        reopenPickerAfterClose: shouldReturnToPickerAfterCapture
                    )
                } catch {
                    SaveService.shared.showError("Screenshot failed: \(error.localizedDescription)")
                }
                if !didPresentEditor {
                    if shouldReturnToPickerAfterCapture,
                       CaptureSettings.shared.shouldShowCapturePickerAfterCapture(for: .screenshot) {
                        self.showScreenshotPicker()
                    }
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

    private func deliverScreenshot(
        _ image: CGImage,
        scaleFactor: CGFloat,
        request: ScreenshotCaptureRequest,
        reopenPickerAfterClose: Bool
    ) throws -> Bool {
        let frame = ScreenshotRecoveryFrame(image: image, scaleFactor: scaleFactor, request: request)
        var temporaryFile = request.temporaryFile
        var sourceURL: URL
        var copiedInRecovery = false
        var savedInRecovery = false
        do {
            sourceURL = try writeScreenshot(
                image,
                scaleFactor: scaleFactor,
                encoding: request.encoding,
                temporaryFile: temporaryFile
            )
        } catch {
            temporaryFile?.discard()
            temporaryFile = nil
            let recovery = recoverScreenshotInMemory(
                image, scaleFactor: scaleFactor, request: request,
                initialError: error, requiresEditor: request.policy.openEditor,
                frame: frame
            )
            switch recovery {
            case let .useSource(source, copied, saved):
                sourceURL = source.0
                temporaryFile = source.1
                copiedInRecovery = copied
                savedInRecovery = saved
            case .finished:
                return false
            }
        }

        let remainingPolicy = ScreenshotOutputPolicy(
            saveToDisk: request.policy.saveToDisk && !savedInRecovery,
            copyToClipboard: request.policy.copyToClipboard && !copiedInRecovery,
            openEditor: request.policy.openEditor
        )
        guard remainingPolicy.destination != nil else {
            temporaryFile?.discard()
            return false
        }

        return try ScreenshotOutputRouter.deliver(
            policy: remainingPolicy,
            recordSaved: {
                SaveService.shared.handleSavedFile(url: sourceURL, type: .screenshot, copyToClipboard: false)
            },
            copy: {
                self.copyScreenshotOutput(from: sourceURL, frame: frame)
            },
            recoverCopy: {
                self.recoverScreenshotCopy(
                    sourceURL: &sourceURL,
                    temporaryFile: &temporaryFile,
                    format: request.encoding.format,
                    willOpenEditor: remainingPolicy.openEditor,
                    frame: frame
                )
            },
            openEditor: {
                self.presentScreenshotEditorOrRecover(
                    sourceURL: sourceURL,
                    temporaryFile: temporaryFile,
                    format: request.encoding.format,
                    reopenPickerAfterClose: reopenPickerAfterClose,
                    frame: frame
                )
            },
            releaseTemporary: {
                temporaryFile?.discard()
                temporaryFile = nil
            },
            outputSucceeded: { frame.outputSuccess.recordOnce() }
        )
    }

    private func recoverScreenshotInMemory(
        _ image: CGImage,
        scaleFactor: CGFloat,
        request: ScreenshotCaptureRequest,
        initialError: Error,
        requiresEditor: Bool,
        frame: ScreenshotRecoveryFrame
    ) -> ScreenshotMemoryRecoveryResult<(URL, ScreenshotTemporaryFileLease?)> {
        screenshotRecoveryDepth += 1
        defer { finishScreenshotRecovery() }
        return ScreenshotMemoryRecovery.recover(
            initialError: initialError,
            requiresSave: request.policy.saveToDisk,
            requiresEditor: requiresEditor,
            choose: { error, copied in
                self.screenshotMemoryRecoveryAction(
                    error: error,
                    copied: copied,
                    requiresSave: request.policy.saveToDisk,
                    requiresEditor: requiresEditor
                )
            },
            retry: {
                let replacement: ScreenshotTemporaryFileLease?
                if request.policy.saveToDisk {
                    replacement = nil
                } else {
                    replacement = try TinyClipsTemporaryFiles.makeScreenshotLease(
                        fileExtension: request.encoding.format.rawValue,
                        root: request.temporaryRoot
                    )
                }
                do {
                    let url = try self.writeScreenshot(
                        image,
                        scaleFactor: scaleFactor,
                        encoding: request.encoding,
                        temporaryFile: replacement
                    )
                    if request.policy.saveToDisk { frame.outputSuccess.recordOnce() }
                    return (url, replacement)
                } catch {
                    replacement?.discard()
                    throw error
                }
            },
            copy: {
                let copied = SaveService.shared.copyImageToClipboard(ScreenshotCapture.clipboardImage(
                    image,
                    scaleFactor: scaleFactor,
                    options: request.encoding
                ))
                if copied { frame.outputSuccess.recordOnce() }
                return copied
            },
            saveAs: {
                guard let url = self.saveScreenshotRecoveryImage(
                    image,
                    scaleFactor: scaleFactor,
                    encoding: request.encoding,
                    frame: frame
                ) else { return nil }
                return (url, nil)
            }
        )
    }

    private func writeScreenshot(
        _ image: CGImage,
        scaleFactor: CGFloat,
        encoding: ScreenshotEncodingOptions,
        temporaryFile: ScreenshotTemporaryFileLease?
    ) throws -> URL {
        if let temporaryFile {
            return try ScreenshotCapture.saveImage(
                image, to: temporaryFile.url, scaleFactor: scaleFactor, options: encoding
            )
        }
        let outputURL = SaveService.shared.generateURL(
            for: .screenshot,
            fileExtension: encoding.format.rawValue
        )
        return try ScreenshotCapture.saveImage(
            image,
            to: outputURL,
            scaleFactor: scaleFactor,
            options: encoding
        )
    }

    private func screenshotMemoryRecoveryAction(
        error: String,
        copied: Bool,
        requiresSave: Bool,
        requiresEditor: Bool
    ) -> ScreenshotMemoryRecoveryAction {
        let problem = "The screenshot could not be prepared."
        AccessibilityAnnouncementService.shared.announceError(problem)
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = problem
        var detail = "The screenshot is still available in memory.\n\(error)"
        if copied {
            detail += "\n\nThe screenshot was copied to the clipboard."
            if requiresSave || requiresEditor {
                detail += " Retry or save it to complete the remaining output."
            }
        } else if requiresEditor {
            detail += "\n\nRetry or save the screenshot to open it in the editor."
        }
        alert.informativeText = detail
        _ = alert.addButton(withTitle: "Retry")
        _ = alert.addButton(withTitle: "Copy")
        _ = alert.addButton(withTitle: "Save As…")
        _ = alert.addButton(withTitle: "Discard Screenshot")
        let response = alert.runModal()
        switch response {
        case .alertFirstButtonReturn:
            return .retry
        case .alertSecondButtonReturn:
            return .copy
        case .alertThirdButtonReturn:
            return .saveAs
        case NSApplication.ModalResponse(
            rawValue: NSApplication.ModalResponse.alertFirstButtonReturn.rawValue + 3
        ):
            return .discard
        default:
            return .showAgain
        }
    }

    private enum ScreenshotRecoveryAction {
        case retry
        case saveAs
        case keep
        case discard
    }

    private func screenshotRecoveryAction(
        forEditor: Bool,
        sourceURL: URL,
        isTemporary: Bool,
        willOpenEditor: Bool = false
    ) -> ScreenshotRecoveryAction {
        let problem = forEditor
            ? "The screenshot editor could not be opened."
            : "The screenshot could not be copied to the clipboard."
        AccessibilityAnnouncementService.shared.announceError(problem)
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = problem
        var detail = isTemporary
            ? "Retry, save a permanent copy, keep for recovery, or discard the temporary image at:\n\(sourceURL.path)"
            : "Retry, save another copy, or keep the screenshot already saved at:\n\(sourceURL.path)"
        if willOpenEditor {
            detail += "\n\nThe editor will still open unless you discard the screenshot."
        }
        alert.informativeText = detail
        _ = alert.addButton(withTitle: forEditor ? "Retry Editor" : "Retry Copy")
        _ = alert.addButton(withTitle: "Save As…")
        let keepButton = alert.addButton(withTitle: isTemporary ? "Keep for Recovery" : "Keep Saved Screenshot")
        keepButton.keyEquivalent = "\u{1b}"
        if isTemporary {
            _ = alert.addButton(withTitle: "Discard Screenshot")
        }
        switch alert.runModal() {
        case .alertFirstButtonReturn:
            return .retry
        case .alertSecondButtonReturn:
            return .saveAs
        case NSApplication.ModalResponse(
            rawValue: NSApplication.ModalResponse.alertFirstButtonReturn.rawValue + 3
        ) where isTemporary:
            return .discard
        default:
            return .keep
        }
    }

    private func keepScreenshotForRecovery(_ temporaryFile: ScreenshotTemporaryFileLease?) -> Bool {
        guard let temporaryFile else { return true }
        do {
            try temporaryFile.keepForRecovery()
            return true
        } catch {
            SaveService.shared.showError("Could not keep the screenshot for recovery: \(error.localizedDescription)")
            return false
        }
    }

    private func discardScreenshotForRecovery(_ temporaryFile: ScreenshotTemporaryFileLease?) -> Bool {
        guard let temporaryFile else { return false }
        do {
            try temporaryFile.discardExplicitly()
            return true
        } catch {
            SaveService.shared.showError("Could not discard the temporary screenshot: \(error.localizedDescription)")
            return false
        }
    }

    private func chooseScreenshotRecoveryDestination(format: ImageFormat) -> URL? {
        let panel = NSSavePanel()
        panel.title = "Save screenshot"
        panel.message = "Choose where to save the recovered screenshot."
        panel.nameFieldLabel = "Name"
        panel.canCreateDirectories = true
        panel.allowedContentTypes = [format.utType]
        panel.directoryURL = SaveService.shared.outputDirectoryURL(for: .screenshot)
        panel.nameFieldStringValue = SaveService.shared.generatedFileName(
            for: .screenshot,
            fileExtension: format.rawValue
        )
        guard panel.runModal() == .OK, var destination = panel.url else { return nil }
        if destination.pathExtension.isEmpty {
            destination.appendPathExtension(format.rawValue)
        }
        guard TinyClipsTemporaryFiles.canSaveScreenshotPermanently(at: destination) else {
            SaveService.shared.showError("Choose a location outside the temporary screenshot folder.")
            return nil
        }
        return destination
    }

    private func saveScreenshotRecoveryCopy(
        sourceURL: URL, format: ImageFormat, frame: ScreenshotRecoveryFrame? = nil
    ) -> URL? {
        guard let destination = chooseScreenshotRecoveryDestination(format: format) else { return nil }
        do {
            let data = try Data(contentsOf: sourceURL, options: .mappedIfSafe)
            try data.write(to: destination, options: .atomic)
            frame?.outputSuccess.recordOnce()
            SaveService.shared.handleSavedFile(url: destination, type: .screenshot, copyToClipboard: false)
            return destination
        } catch {
            SaveService.shared.showError("Could not save the screenshot: \(error.localizedDescription)")
            return nil
        }
    }

    private func saveScreenshotRecoveryImage(
        _ image: CGImage,
        scaleFactor: CGFloat,
        encoding: ScreenshotEncodingOptions,
        frame: ScreenshotRecoveryFrame
    ) -> URL? {
        guard let destination = chooseScreenshotRecoveryDestination(format: encoding.format) else { return nil }
        do {
            let url = try ScreenshotCapture.saveImage(
                image, to: destination, scaleFactor: scaleFactor, options: encoding
            )
            frame.outputSuccess.recordOnce()
            SaveService.shared.handleSavedFile(url: url, type: .screenshot, copyToClipboard: false)
            return url
        } catch {
            SaveService.shared.showError("Could not save the screenshot: \(error.localizedDescription)")
            return nil
        }
    }

    private func recoverScreenshotCopy(
        sourceURL: inout URL,
        temporaryFile: inout ScreenshotTemporaryFileLease?,
        format: ImageFormat,
        willOpenEditor: Bool = false,
        frame: ScreenshotRecoveryFrame? = nil
    ) -> ScreenshotCopyRecoveryResult {
        screenshotRecoveryDepth += 1
        defer { finishScreenshotRecovery() }
        while true {
            if let recovery = recoverUnavailableScreenshotSource(
                sourceURL: sourceURL, frame: frame,
                temporaryFile: temporaryFile, requiresEditor: willOpenEditor
            ) {
                temporaryFile = nil
                switch recovery {
                case .finished:
                    return .discarded
                case let .useSource(source, copied, saved):
                    sourceURL = source.0
                    temporaryFile = source.1
                    if copied || saved || copyScreenshotOutput(from: sourceURL, frame: frame) {
                        return .available
                    }
                    continue
                }
            }
            switch screenshotRecoveryAction(
                forEditor: false,
                sourceURL: sourceURL,
                isTemporary: temporaryFile != nil,
                willOpenEditor: willOpenEditor
            ) {
            case .retry:
                if copyScreenshotOutput(from: sourceURL, frame: frame) {
                    return .available
                }
            case .saveAs:
                if let savedURL = saveScreenshotRecoveryCopy(sourceURL: sourceURL, format: format, frame: frame) {
                    temporaryFile?.discard()
                    temporaryFile = nil
                    sourceURL = savedURL
                    return .available
                }
            case .keep:
                if keepScreenshotForRecovery(temporaryFile) {
                    return .retained
                }
            case .discard:
                if discardScreenshotForRecovery(temporaryFile) {
                    temporaryFile = nil
                    return .discarded
                }
            }
        }
    }

    private func presentScreenshotEditorOrRecover(
        sourceURL: URL,
        temporaryFile: ScreenshotTemporaryFileLease?,
        format: ImageFormat,
        reopenPickerAfterClose: Bool,
        frame: ScreenshotRecoveryFrame
    ) -> Bool {
        if tryShowScreenshotEditor(
            sourceURL: sourceURL,
            temporaryFile: temporaryFile,
            format: format,
            reopenPickerAfterClose: reopenPickerAfterClose,
            frame: frame
        ) {
            return true
        }
        return recoverScreenshotEditor(
            sourceURL: sourceURL,
            temporaryFile: temporaryFile,
            format: format,
            reopenPickerAfterClose: reopenPickerAfterClose,
            frame: frame
        )
    }

    private func tryShowScreenshotEditor(
        sourceURL: URL,
        temporaryFile: ScreenshotTemporaryFileLease?,
        format: ImageFormat,
        reopenPickerAfterClose: Bool,
        frame: ScreenshotRecoveryFrame
    ) -> Bool {
        guard let image = ImagePixelDensity.loadImage(contentsOf: sourceURL),
              image.cgImage(forProposedRect: nil, context: nil, hints: nil) != nil else {
            return false
        }
        let initialSaveURL: URL
        if let temporaryFile {
            initialSaveURL = temporaryFile.url.deletingLastPathComponent().appendingPathComponent(
                SaveService.shared.generatedFileName(for: .screenshot, fileExtension: format.rawValue)
            )
        } else {
            initialSaveURL = sourceURL
        }
        showScreenshotEditor(
            for: sourceURL,
            initialSaveURL: initialSaveURL,
            deleteSourceOnCancel: temporaryFile != nil,
            reopenPickerAfterClose: reopenPickerAfterClose,
            temporaryFile: temporaryFile,
            onPresentationFailure: { [self] in
                let presented = self.recoverScreenshotEditor(
                    sourceURL: sourceURL,
                    temporaryFile: temporaryFile,
                    format: format,
                    reopenPickerAfterClose: reopenPickerAfterClose,
                    frame: frame
                )
                if !presented {
                    self.editorWindowClosed { [weak self] in
                        if reopenPickerAfterClose,
                           CaptureSettings.shared.shouldShowCapturePickerAfterCapture(for: .screenshot) {
                            self?.showScreenshotPicker()
                        }
                    }
                }
            },
            onPresentationSuccess: {
                frame.outputSuccess.recordOnce()
                frame.image = nil
            }
        )
        return true
    }

    private func recoverScreenshotEditor(
        sourceURL: URL,
        temporaryFile: ScreenshotTemporaryFileLease?,
        format: ImageFormat,
        reopenPickerAfterClose: Bool,
        frame: ScreenshotRecoveryFrame
    ) -> Bool {
        screenshotRecoveryDepth += 1
        defer { finishScreenshotRecovery() }
        while true {
            if let recovery = recoverUnavailableScreenshotSource(
                sourceURL: sourceURL, frame: frame, temporaryFile: temporaryFile, requiresEditor: true
            ) {
                switch recovery {
                case .finished:
                    return false
                case let .useSource(source, _, _):
                    return presentScreenshotEditorOrRecover(
                        sourceURL: source.0,
                        temporaryFile: source.1,
                        format: format,
                        reopenPickerAfterClose: reopenPickerAfterClose,
                        frame: frame
                    )
                }
            }
            switch screenshotRecoveryAction(
                forEditor: true,
                sourceURL: sourceURL,
                isTemporary: temporaryFile != nil
            ) {
            case .retry:
                if tryShowScreenshotEditor(
                    sourceURL: sourceURL,
                    temporaryFile: temporaryFile,
                    format: format,
                    reopenPickerAfterClose: reopenPickerAfterClose,
                    frame: frame
                ) {
                    return true
                }
            case .saveAs:
                if saveScreenshotRecoveryCopy(sourceURL: sourceURL, format: format, frame: frame) != nil {
                    temporaryFile?.discard()
                    return false
                }
            case .keep:
                if keepScreenshotForRecovery(temporaryFile) {
                    return false
                }
            case .discard:
                if discardScreenshotForRecovery(temporaryFile) {
                    return false
                }
            }
        }
    }

    private func recoverUnavailableScreenshotSource(
        sourceURL: URL,
        frame: ScreenshotRecoveryFrame?,
        temporaryFile: ScreenshotTemporaryFileLease?,
        requiresEditor: Bool
    ) -> ScreenshotMemoryRecoveryResult<(URL, ScreenshotTemporaryFileLease?)>? {
        ScreenshotUnavailableSourceRecovery.ifMissing(
            sourceExists: FileManager.default.fileExists(atPath: sourceURL.path),
            saveToDisk: frame?.request.policy.saveToDisk == true,
            recover: {
                temporaryFile?.discard()
                guard let frame, let image = frame.image else {
                    SaveService.shared.showError("The screenshot is no longer available for recovery.")
                    return .finished
                }
                return recoverScreenshotInMemory(
                    image,
                    scaleFactor: frame.scaleFactor,
                    request: frame.request,
                    initialError: ScreenshotTemporaryFileError.missingImage,
                    requiresEditor: requiresEditor,
                    frame: frame
                )
            },
            recordSavedRetry: { source in
                SaveService.shared.handleSavedFile(
                    url: source.0, type: .screenshot, copyToClipboard: false
                )
            }
        )
    }

    private func retainScreenshotAfterUnexpectedClose(
        _ temporaryFile: ScreenshotTemporaryFileLease,
        format: ImageFormat
    ) {
        screenshotRecoveryDepth += 1
        defer { finishScreenshotRecovery() }
        while FileManager.default.fileExists(atPath: temporaryFile.url.path) {
            do {
                try temporaryFile.keepForRecovery()
                let message = "The unsaved screenshot was kept for recovery at \(temporaryFile.url.path)."
                AccessibilityAnnouncementService.shared.announce(message, priority: .high)
                SaveService.shared.showNotice(message)
                return
            } catch {
                let problem = "The unsaved screenshot could not be kept for recovery."
                AccessibilityAnnouncementService.shared.announceError(problem)
                let alert = NSAlert()
                alert.alertStyle = .warning
                alert.messageText = problem
                alert.informativeText = "\(error.localizedDescription)\n\nRetry, save a permanent copy, or discard the screenshot."
                _ = alert.addButton(withTitle: "Retry")
                _ = alert.addButton(withTitle: "Save As…")
                _ = alert.addButton(withTitle: "Discard Screenshot")
                switch alert.runModal() {
                case .alertSecondButtonReturn:
                    if saveScreenshotRecoveryCopy(sourceURL: temporaryFile.url, format: format) != nil {
                        temporaryFile.discard()
                        return
                    }
                case .alertThirdButtonReturn:
                    if discardScreenshotForRecovery(temporaryFile) { return }
                default:
                    break
                }
            }
        }
        SaveService.shared.showError("The screenshot is no longer available for recovery.")
    }

    private func copyScreenshotOutput(from url: URL, frame: ScreenshotRecoveryFrame?) -> Bool {
        let copied = SaveService.shared.copyScreenshotToClipboard(from: url)
        if copied { frame?.outputSuccess.recordOnce() }
        return copied
    }

    private func finishScreenshotRecovery() {
        screenshotRecoveryDepth -= 1
        scheduleStoreReviewWhenCaptureIsIdle()
    }

    /// Starts setting up a video recording. `forStudio` makes it a Studio recording: the screen
    /// and the camera are kept as separate layers and the Studio editor opens when it ends.
    func startVideoRecording(forStudio: Bool = false) {
        guard !isCaptureActionInProgress else { return }
        setUpVideoRecording(forStudio: forStudio)
    }

    /// Uses the interactive Studio setup, but returns the take instead of opening another editor.
    func recordStudioSegment() async throws -> StudioProject? {
        guard !isAnyCaptureInProgress, !isCaptureWorkActive else {
            throw StudioEditorInputError("Finish the current capture before recording another segment.")
        }
        return try await withCheckedThrowingContinuation { continuation in
            studioSegmentReceiver = .editor(continuation)
            setUpVideoRecording(forStudio: true)
        }
    }

    private func finishStudioSegment(_ result: Result<StudioProject?, Error>) {
        guard let receiver = studioSegmentReceiver else { return }
        studioSegmentReceiver = nil
        studioSegmentInputError = nil
        switch receiver {
        case .editor(let continuation):
            continuation.resume(with: result)
        case .automation:
            if case .failure(let error) = result {
                automationRecordingError = error as? AutomationError ?? AutomationError(.internal, error.localizedDescription)
            }
        }
    }

    private func setUpVideoRecording(forStudio: Bool) {
        activeRecordingWasStartedByAutomation = false
        pendingVideoIsForStudio = forStudio

        isCapturePreparationInProgress = true
        let cursorScreen = screenUnderMouseCursor()
        Task {
            defer { isCapturePreparationInProgress = false }
            guard await prepareForNewCaptureRequest() else {
                finishStudioSegment(.failure(StudioEditorInputError("Finish the current capture and try again.")))
                return
            }
            guard await PermissionManager.shared.checkPermission() else {
                finishStudioSegment(.failure(StudioEditorInputError("Allow Screen Recording in System Settings, then try again.")))
                return
            }
            guard !isOCRInFlight else {
                finishStudioSegment(.failure(StudioEditorInputError("Finish the current text capture and try again.")))
                return
            }
            let settings = CaptureSettings.shared
            if settings.shouldShowCapturePicker(for: .video) {
                showRecordingPicker(for: .video, cursorScreen: cursorScreen, forSegment: studioSegmentReceiver != nil)
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

    func startAutomationVideoRecording(
        forStudio: Bool,
        target: CaptureTarget,
        devices: AutomationRecordingDevices,
        validateStart: @escaping @MainActor () throws -> Void = {},
        onStudioSegment: (@MainActor (String) async throws -> Void)? = nil
    ) async throws {
        guard !isCaptureActionInProgress, onStudioSegment == nil || !isAnyCaptureInProgress else {
            throw AutomationError(.busy, "Tiny Clips is already capturing or recording.")
        }
        if onStudioSegment != nil, !forStudio {
            throw AutomationError(.invalidParams, "Recording a timeline segment requires Studio recording.")
        }
        isCapturePreparationInProgress = true
        defer { isCapturePreparationInProgress = false }
        guard await prepareForNewCaptureRequest() else {
            throw AutomationError(.busy, "Tiny Clips is already capturing or recording.")
        }
        try validateStart()
        guard PermissionManager.shared.hasScreenRecordingPermission() else {
            throw AutomationError(.permissionRequired, "Screen Recording permission is not granted.")
        }
        try devices.requireCurrentAuthorization()
        automationRecordingError = nil
        automationStudioTake = nil

        let settings = CaptureSettings.shared
        pendingVideoIsForStudio = forStudio
        shouldReturnToPickerAfterRecording = false
        if target.studioCaptureKind != .display && settings.showRegionIndicator {
            let panel = RegionIndicatorPanel(region: target.region)
            panel.show()
            regionIndicatorPanel = panel
        }

        let microphones = devices.microphone ? MicrophoneDeviceCatalog.availableOptions() : []
        let microphoneID = settings.selectedMicrophoneID.isEmpty
            || microphones.contains(where: { $0.id == settings.selectedMicrophoneID })
            ? settings.selectedMicrophoneID
            : ""
        let webcams = devices.camera ? WebcamDeviceCatalog.availableOptions() : []
        let webcamID = settings.selectedWebcamID.isEmpty
            || webcams.contains(where: { $0.id == settings.selectedWebcamID })
            ? settings.selectedWebcamID
            : ""
        activeRecordingWasStartedByAutomation = true
        studioSegmentReceiver = onStudioSegment.map { .automation(validate: validateStart, attach: $0) }
        beginVideoRecording(
            target: target,
            systemAudio: settings.recordAudio,
            microphone: devices.microphone,
            selectedMicrophoneID: microphoneID,
            webcamSelection: StartRecordingPanel.WebcamSelection(
                enabled: devices.camera,
                deviceID: webcamID,
                shape: settings.webcamShape,
                corner: settings.webcamCorner,
                size: settings.webcamSize
            ),
            mouseClicksEnabled: settings.shouldShowMouseClickVisuals(for: .video),
            timeLimitMinutes: settings.videoRecordingTimeLimitMinutes,
            countdownEnabled: settings.videoCountdownEnabled,
            countdownDuration: settings.videoCountdownDuration,
            studioModeEnabled: pendingVideoIsForStudio,
            automationDevices: devices
        )
    }

    func startAutomationGifRecording(target: CaptureTarget) async throws {
        guard !isCaptureActionInProgress else {
            throw AutomationError(.busy, "Tiny Clips is already capturing or recording.")
        }
        isCapturePreparationInProgress = true
        defer { isCapturePreparationInProgress = false }
        guard await prepareForNewCaptureRequest() else {
            throw AutomationError(.busy, "Tiny Clips is already capturing or recording.")
        }
        guard PermissionManager.shared.hasScreenRecordingPermission() else {
            throw AutomationError(.permissionRequired, "Screen Recording permission is not granted.")
        }

        let settings = CaptureSettings.shared
        shouldReturnToPickerAfterRecording = false
        if target.studioCaptureKind != .display && settings.showRegionIndicator {
            let panel = RegionIndicatorPanel(region: target.region)
            panel.show()
            regionIndicatorPanel = panel
        }
        activeRecordingWasStartedByAutomation = true
        beginGifRecording(
            target: target,
            mouseClicksEnabled: settings.shouldShowMouseClickVisuals(for: .gif),
            countdownEnabled: settings.gifCountdownEnabled,
            countdownDuration: settings.gifCountdownDuration
        )
    }

    func resolveAutomationCaptureTarget(_ target: AutomationCaptureTarget) async throws -> CaptureTarget {
        switch target {
        case .screen(let displayID):
            let display = try AutomationCaptureEnvironment.display(displayID)
            return CaptureTarget(
                region: CaptureRegion(
                    sourceRect: CGRect(origin: .zero, size: display.pointSize),
                    displayID: display.displayID,
                    scaleFactor: display.scaleFactor
                ),
                studioCaptureKind: .display
            )
        case .region(let displayID, let pixelRect):
            let display = try AutomationCaptureEnvironment.display(displayID)
            let sourceRect = try AutomationCaptureTargets.sourceRect(forPixelRect: pixelRect, on: display)
            return CaptureTarget(
                region: CaptureRegion(
                    sourceRect: sourceRect,
                    displayID: display.displayID,
                    scaleFactor: display.scaleFactor
                ).pixelAligned(),
                studioCaptureKind: .region
            )
        case .window(let windowID):
            let window = try await AutomationCaptureEnvironment.window(windowID)
            guard let region = captureRegion(for: window) else {
                throw AutomationError(.notFound, "That window is not on a capturable display.")
            }
            return CaptureTarget(region: region, studioCaptureKind: .window)
        case .windowArea(let windowID):
            let window = try await AutomationCaptureEnvironment.window(windowID)
            guard let selection = WindowAreaCaptureResolver.resolve(window: window) else {
                throw AutomationError(.notFound, "That window does not overlap a capturable display.")
            }
            return CaptureTarget(
                region: selection.region,
                studioCaptureKind: .window,
                windowAreaSelection: selection
            )
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
        countdownDuration: Int,
        studioModeEnabled: Bool = false,
        automationDevices: AutomationRecordingDevices? = nil
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
                let webcamEnabled = webcamSelection.enabled
                let isStudioRecording = studioModeEnabled
                let studioCoordinator: StudioRecordingCoordinator?
                do {
                    try self.studioSegmentReceiver?.validateStart()
                    try automationDevices?.requireCurrentAuthorization()
                    if isStudioRecording {
                        studioCoordinator = try StudioRecordingCoordinator(
                            captureRegion: target.region,
                            captureKind: target.studioCaptureKind,
                            initialCameraCorner: StudioRecordingCoordinator.studioAnchor(from: webcamSelection.corner),
                            clickVisualsEnabled: mouseClicksEnabled,
                            frameRate: settings.videoFrameRate
                        )
                    } else {
                        studioCoordinator = nil
                    }
                } catch {
                    if automationDevices != nil {
                        self.automationRecordingError = error as? AutomationError
                            ?? AutomationError(.internal, error.localizedDescription)
                    }
                    self.activeRecordingWasStartedByAutomation = false
                    if self.studioSegmentReceiver != nil {
                        self.finishStudioSegment(.failure(error))
                    } else {
                        SaveService.shared.showError("Video recording could not start: \(error.localizedDescription)")
                    }
                    // Nothing is being recorded, so the outline of the region must not stay up.
                    self.dismissRegionIndicator()
                    return
                }

                let shouldSaveImmediately = !settings.showTrimmer || settings.saveImmediatelyVideo
                let url: URL
                if let studioCoordinator {
                    url = studioCoordinator.screenURL
                } else if shouldSaveImmediately {
                    url = SaveService.shared.generateURL(for: .video)
                } else {
                    url = TinyClipsTemporaryFiles.makeURL(fileExtension: CaptureType.video.fileExtension)
                }
                let webcamOutputURL = webcamEnabled
                    ? (studioCoordinator?.cameraURL ?? self.webcamCompanionURL(for: url))
                    : nil
                self.activeWebcamOverlaySelection = webcamEnabled ? webcamSelection : nil
                self.webcamPositionEvents = webcamEnabled
                    ? [.init(time: .zero, corner: webcamSelection.corner)]
                    : []

                let recorder = VideoRecorder()
                let webcamRecorder = WebcamRecorder()
                let sessionID = self.nextRecordingSessionID()
                do {
                    self.activeRecordingSessionID = sessionID
                    self.didShowVideoCodecFallbackNotice = false
                    self.debugRecordingLifecycle("Starting video session \(sessionID)")
                    let captureManager = self
                    recorder.onStreamFailure = { [weak captureManager] error in
                        let message = error.localizedDescription
                        Task { @MainActor [weak captureManager] in
                            captureManager?.handleStreamFailure(
                                for: sessionID,
                                type: .video,
                                message: message
                            )
                        }
                    }
                    recorder.onMicrophoneLevel = { [weak captureManager] level in
                        DispatchQueue.main.async { [weak captureManager] in
                            captureManager?.microphoneLevel = level
                        }
                    }
                    recorder.onMicrophoneWarning = { [weak captureManager] warning in
                        DispatchQueue.main.async { [weak captureManager] in
                            captureManager?.microphoneWarningMessage = warning
                        }
                    }
                    recorder.onMicrophoneDeviceName = { [weak captureManager] name in
                        DispatchQueue.main.async { [weak captureManager] in
                            captureManager?.activeMicrophoneName = name.isEmpty ? nil : name
                        }
                    }
                    recorder.onMicrophoneError = { [weak captureManager] message in
                        DispatchQueue.main.async { [weak captureManager] in
                            captureManager?.microphoneWarningMessage = message
                            if automationDevices != nil, captureManager?.activeRecordingSessionID == sessionID {
                                captureManager?.automationRecordingError = AutomationError(.internal, message)
                            }
                            if captureManager?.studioSegmentReceiver != nil, captureManager?.activeRecordingSessionID == sessionID {
                                captureManager?.studioSegmentInputError = StudioEditorInputError("The microphone recording failed: \(message)")
                            }
                            SaveService.shared.showError("Microphone error: \(message)")
                        }
                    }
                    recorder.onVideoCodecFallback = { [weak captureManager] message in
                        Task { @MainActor [weak captureManager] in
                            captureManager?.showVideoCodecFallbackNoticeOnce(message)
                        }
                    }
                    webcamRecorder.onWebcamDeviceName = { [weak captureManager] name in
                        DispatchQueue.main.async { [weak captureManager] in
                            captureManager?.activeWebcamName = name.isEmpty ? nil : name
                        }
                    }
                    webcamRecorder.onWebcamError = { [weak captureManager] message in
                        DispatchQueue.main.async { [weak captureManager] in
                            if captureManager?.studioSegmentReceiver != nil, captureManager?.activeRecordingSessionID == sessionID {
                                captureManager?.studioSegmentInputError = StudioEditorInputError("The camera recording failed: \(message)")
                            }
                            SaveService.shared.showError("Webcam error: \(message)")
                        }
                    }
                    webcamRecorder.onVideoCodecFallback = { [weak captureManager] message in
                        Task { @MainActor [weak captureManager] in
                            captureManager?.showVideoCodecFallbackNoticeOnce(message)
                        }
                    }

                    self.videoRecorder = recorder
                    self.webcamRecorder = nil
                    self.studioRecordingCoordinator = studioCoordinator
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
                        timeLimitMinutes: timeLimitMinutes,
                        studioModeEnabled: isStudioRecording,
                        automationDevices: automationDevices
                    )
                    self.activeMouseClickCaptureEnabledOverride = isStudioRecording || mouseClicksEnabled
                    self.startMouseClickMonitoringIfNeeded(for: .video, region: target.region)
                    studioCoordinator?.startCursorSampling(
                        timeProvider: { recorder.currentTimelineTimeIfStarted() },
                        isPaused: { [weak self = self] in self?.isRecordingPaused ?? true }
                    )
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
                    try self.studioSegmentReceiver?.validateStart()
                    try automationDevices?.requireCurrentAuthorization()
                    try await recorder.start(
                        target: target,
                        alwaysExcludedWindows: teleprompterWindow.map { [$0] } ?? [],
                        outputURL: url,
                        recordSystemAudio: systemAudio,
                        recordMicrophone: microphone,
                        selectedMicrophoneID: selectedMicrophoneID,
                        requestMicrophonePermission: automationDevices == nil
                    )
                    guard self.isRecording,
                          self.activeRecordingSessionID == sessionID,
                          self.videoRecorder === recorder else {
                        return
                    }
                    self.adoptResolvedRecordingRegion(recorder.resolvedRegion)
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
                            let companionVideoCodec = recorder.videoCodecForCompanionWriters
                            try await webcamRecorder.start(
                                outputURL: webcamOutputURL,
                                selectedWebcamID: webcamSelection.deviceID,
                                // Keep the companion webcam track aligned with the completed screen writer setup.
                                videoCodec: companionVideoCodec,
                                requestPermission: automationDevices == nil
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
                            if automationDevices != nil {
                                self.automationRecordingError = error as? AutomationError
                                    ?? AutomationError(.internal, error.localizedDescription)
                            }
                            if self.studioSegmentReceiver != nil {
                                self.studioSegmentInputError = error
                            }
                            SaveService.shared.showError("Webcam recording was not started: \(error.localizedDescription). Screen recording will continue without webcam.")
                        }
                    }

                    guard self.activeRecordingSessionID == sessionID else {
                        return
                    }
                    self.showStopPanel()
                    self.scheduleVideoAutoStopIfNeeded(timeLimitMinutes: timeLimitMinutes, sessionID: sessionID)
                } catch {
                    // A stop, discard or restart already owns cleanup for this recorder.
                    guard self.activeRecordingSessionID == sessionID else { return }
                    if automationDevices != nil {
                        self.automationRecordingError = error as? AutomationError
                            ?? AutomationError(.internal, error.localizedDescription)
                    }
                    self.endIdleSleepAssertion()
                    self.isRecordingSetupInProgress = true
                    self.cancelVideoAutoStopTask()
                    _ = self.stopMouseClickMonitoring()
                    self.activeMouseClickCaptureEnabledOverride = nil
                    self.resetRecordingAudioStatus()
                    self.activeWebcamName = nil
                    self.isRecording = false
                    self.isRecordingPaused = false
                    self.activeRecordingRequest = nil
                    self.activeRecordingWasStartedByAutomation = false
                    self.dismissRegionIndicator()
                    self.dismissTeleprompter()
                    self.activeRecordingSessionID = nil
                    self.videoRecorder = nil
                    self.webcamRecorder = nil
                    self.studioRecordingCoordinator = nil
                    self.activeWebcamOverlaySelection = nil
                    self.webcamPositionEvents = []
                    self.debugRecordingLifecycle("Video session failed to start: \(error.localizedDescription)")
                    await recorder.cancel()
                    await webcamRecorder.cancel()
                    studioCoordinator?.deleteUnfinishedProject()
                    if self.studioSegmentReceiver != nil {
                        self.finishStudioSegment(.failure(error))
                    } else {
                        SaveService.shared.showError("Video recording failed: \(error.localizedDescription)")
                    }
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
        activeRecordingWasStartedByAutomation = false

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
                    let captureManager = self
                    writer.onStreamFailure = { [weak captureManager] error in
                        let message = error.localizedDescription
                        Task { @MainActor [weak captureManager] in
                            captureManager?.handleStreamFailure(
                                for: sessionID,
                                type: .gif,
                                message: message
                            )
                        }
                    }
                    self.gifWriter = writer
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
                    self.adoptResolvedRecordingRegion(writer.resolvedRegion)
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
                    self.activeRecordingWasStartedByAutomation = false
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
        if studioSegmentReceiver != nil, studioSegmentInputError == nil,
           case .video(_, _, _, _, let webcam, _, _, _, _) = activeRecordingRequest,
           webcam.enabled && webcamRecorder == nil {
            studioSegmentInputError = StudioEditorInputError("Recording stopped before the camera was ready.")
        }
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

    private func authorizedContinuationDevices(forAutomation: Bool) throws -> AutomationRecordingDevices? {
        guard case let .video(_, _, mic, _, webcam, _, _, _, original) = activeRecordingRequest else { return nil }
        do {
            return try AutomationRecordingDevices.forContinuation(
                original: original, initiatedByAgent: forAutomation,
                takeDevices: .init(microphone: mic, camera: webcam.enabled)
            ) { try $0.requireCurrentAuthorization() }
        } catch let error as AutomationError {
            automationRecordingError = error
            throw error
        }
    }

    func togglePauseRecording(forAutomation: Bool = false) throws {
        guard isRecording else { return }
        if isRecordingPaused {
            try studioSegmentReceiver?.validateStart()
            _ = try authorizedContinuationDevices(forAutomation: forAutomation)
            if automationRecordingError?.code == .permissionRequired { automationRecordingError = nil }
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

    func restartRecording(forAutomation: Bool = false) async throws {
        guard isRecording, !isDiscardingRecording, let request = activeRecordingRequest else { return }
        try studioSegmentReceiver?.validateStart()
        let devices = try authorizedContinuationDevices(forAutomation: forAutomation)
        let draggedWebcamSelection = activeWebcamOverlaySelection
        isDiscardingRecording = true
        defer { isDiscardingRecording = false }
        await discardRecording(clearActiveRequest: false)
        if forAutomation {
            activeRecordingWasStartedByAutomation = true
        }
        switch request {
        case let .video(target, systemAudio, microphone, selectedMicrophoneID, webcamSelection, mouseClicksEnabled, timeLimitMinutes, studioModeEnabled, _):
            automationRecordingError = nil
            automationStudioTake = nil
            beginVideoRecording(
                target: target,
                systemAudio: systemAudio,
                microphone: microphone,
                selectedMicrophoneID: selectedMicrophoneID,
                webcamSelection: draggedWebcamSelection ?? webcamSelection,
                mouseClicksEnabled: mouseClicksEnabled,
                timeLimitMinutes: timeLimitMinutes,
                countdownEnabled: false,
                countdownDuration: 0,
                studioModeEnabled: studioModeEnabled,
                automationDevices: devices
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

    func discardRecording() {
        if isStudioSegmentCountdown {
            countdownWindow?.cancel()
            countdownWindow = nil
            dismissRegionIndicator()
            finishStudioSegment(.success(nil))
            return
        }
        guard isRecording, !isDiscardingRecording, let sessionID = activeRecordingSessionID else { return }
        isDiscardingRecording = true
        Task {
            defer { isDiscardingRecording = false }
            guard activeRecordingSessionID == sessionID else { return }
            await discardRecording(clearActiveRequest: true)
        }
    }

    private func discardRecording(clearActiveRequest: Bool) async {
        defer { notifyStoreReviewCaptureWorkBecameIdle() }
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
        isRecording = false
        isRecordingPaused = false
        activeRecordingSessionID = nil

        let recorder = videoRecorder
        let webcam = webcamRecorder
        let writer = gifWriter
        let studioCoordinator = studioRecordingCoordinator
        videoRecorder = nil
        webcamRecorder = nil
        gifWriter = nil
        studioRecordingCoordinator = nil
        lastVideoRecordingArtifacts = nil

        if clearActiveRequest {
            activeRecordingRequest = nil
        }

        await recorder?.cancel()
        await webcam?.cancel()
        await writer?.cancel()
        studioCoordinator?.deleteUnfinishedProject()
        if clearActiveRequest {
            finishStudioSegment(.success(nil))
        } else {
            studioSegmentInputError = nil
        }
    }

    private func stopRecordingFlow(stoppingSessionID: UInt64?, streamFailureMessage: String? = nil) async {
        defer {
            isStoppingRecording = false
            activeRecordingWasStartedByAutomation = false
            if finalizingSessionID == stoppingSessionID {
                finalizingSessionID = nil
            }
            stopRecordingTask = nil
            debugRecordingLifecycle("Finished finalizing session \(stoppingSessionID.map(String.init) ?? "unknown")")
            notifyStoreReviewCaptureWorkBecameIdle()
        }
        defer { activeMouseClickCaptureEnabledOverride = nil }

        let videoRecorderAtStop = videoRecorder
        let webcamRecorderAtStop = webcamRecorder
        let gifWriterAtStop = gifWriter
        let studioCoordinatorAtStop = studioRecordingCoordinator
        studioCoordinatorAtStop?.stopCursorSampling()

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
            if studioCoordinatorAtStop != nil {
                return true
            }
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
        let isStudioSegment = studioSegmentReceiver != nil
        let videoShowTrimmer = !isStudioSegment && streamFailureMessage == nil
            && !activeRecordingWasStartedByAutomation
            && CaptureSettings.shared.showTrimmer
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
        let videoCodec = videoRecorderAtStop?.videoCodecForCompanionWriters ?? .h264

        var savedVideoURL: URL?
        var savedWebcamURL: URL?
        var completedStudioProject: StudioProject?
        var segmentError = studioSegmentInputError
        if let streamFailureMessage {
            segmentError = StudioEditorInputError("Screen capture stopped unexpectedly. Check Screen Recording permission and try again. \(streamFailureMessage)")
        }
        var partialOutputSaveError: String?
        var noPartialFramesWereCaptured = false

        // A Studio recording's camera goes on until its screen track is finished. Outside the
        // camera track's own time the camera is not drawn, so a camera that stopped first, as it
        // does in an ordinary recording, would be missing from the last moments of every Studio
        // recording, for as long as its file took to finish.
        let stopsCameraAfterScreen = studioCoordinatorAtStop != nil && videoRecorderAtStop != nil

        if !stopsCameraAfterScreen, let recorder = webcamRecorderAtStop {
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
                updateProcessingProgress(
                    0.55,
                    status: studioCoordinatorAtStop == nil ? "Applying overlays..." : "Saving Studio project..."
                )
            } catch {
                if isStudioSegment {
                    if segmentError == nil { segmentError = error }
                } else if streamFailureMessage == nil {
                    SaveService.shared.showError("Video save failed: \(error.localizedDescription)")
                } else if let captureError = error as? CaptureError, case .noFrames = captureError {
                    noPartialFramesWereCaptured = true
                } else {
                    partialOutputSaveError = error.localizedDescription
                }
            }

            if studioCoordinatorAtStop == nil,
               let currentURL = savedVideoURL,
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
                        codec: videoCodec,
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

            if studioCoordinatorAtStop == nil, let currentURL = savedVideoURL {
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
                            codec: videoCodec,
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

                // The passes above mix the sounds as they export. A recording that went through
                // neither is still the recorder's own file, with a track for each sound.
                if let recordedURL = savedVideoURL {
                    updateProcessingProgress(0.97, status: "Finalizing...")
                    if await Self.mixDownAudioOffMain(at: recordedURL) == .failed {
                        SaveService.shared.showError("The system audio and microphone could not be combined into one audio track. The recording was kept with two audio tracks, so some players may play it without the microphone.")
                    }
                }
            }

            if studioCoordinatorAtStop == nil {
                updateProcessingProgress(1.0, status: "Done")
            }
            if videoRecorder === recorder {
                videoRecorder = nil
            } else {
                debugRecordingLifecycle("Skipped clearing stale video recorder reference")
            }
        }

        if stopsCameraAfterScreen, let recorder = webcamRecorderAtStop {
            do {
                savedWebcamURL = try await recorder.stop()
            } catch {
                if isStudioSegment {
                    segmentError = StudioEditorInputError("The camera recording could not be saved: \(error.localizedDescription)")
                } else {
                    SaveService.shared.showError("Webcam save failed: \(error.localizedDescription). The Studio project will have the screen only.")
                }
            }

            if webcamRecorder === recorder {
                webcamRecorder = nil
            } else {
                debugRecordingLifecycle("Skipped clearing stale webcam recorder reference")
            }
        }

        if let studioCoordinatorAtStop {
            if let studioScreenURL = savedVideoURL {
                do {
                    updateProcessingProgress(0.75, status: "Saving Studio project...")
                    completedStudioProject = try await studioCoordinatorAtStop.completeRecording(
                        screenURL: studioScreenURL,
                        cameraURL: savedWebcamURL,
                        screenFirstSampleTime: videoRecorderAtStop?.firstScreenSampleTime,
                        cameraFirstSampleTime: webcamRecorderAtStop?.firstSampleTime,
                        recordedAudioTracks: videoRecorderAtStop?.audioTrackKinds ?? [],
                        mouseClicks: capturedMouseClickData?.events ?? [],
                        cameraCornerChanges: webcamPositionEvents,
                        clickOverlayStyle: videoOverlayStyle,
                        branding: showBrandingOverlay,
                        appVersion: Self.appVersionString(),
                        look: CaptureSettings.shared.studioDefaultLook,
                        requiresCompleteLayers: isStudioSegment
                    )
                } catch where isStudioSegment {
                    // The caller needs the native take, never a flattened substitute. Preserve the
                    // separate recordings in place if a project or its event file could not be saved.
                    studioCoordinatorAtStop.leaveUnfinishedProject()
                    let recovery: String
                    if case .editor = studioSegmentReceiver {
                        recovery = "The source recordings are kept for a day at \(studioCoordinatorAtStop.paths.projectDirectory.path)."
                    } else {
                        recovery = "The source recordings are kept in Studio storage for a day."
                    }
                    segmentError = StudioEditorInputError("The Studio recording could not be saved: \(error.localizedDescription) \(recovery)")
                    savedVideoURL = nil
                } catch {
                    // Keep the screen recording as an ordinary video instead of losing it with the project.
                    let rescueURL = videoShouldSaveImmediately
                        ? SaveService.shared.generateURL(for: .video)
                        : TinyClipsTemporaryFiles.makeURL(fileExtension: CaptureType.video.fileExtension)
                    var wasRescued = false
                    do {
                        try FileManager.default.moveItem(at: studioScreenURL, to: rescueURL)
                        savedVideoURL = rescueURL
                        wasRescued = true
                        SaveService.shared.showError("The Studio project could not be saved, so the recording was kept as a regular video. \(error.localizedDescription)")
                    } catch {
                        savedVideoURL = nil
                        SaveService.shared.showError("Studio project save failed: \(error.localizedDescription) The screen recording is still at \(studioScreenURL.path), where it is kept for a day.")
                    }
                    savedWebcamURL = nil
                    if wasRescued {
                        studioCoordinatorAtStop.deleteUnfinishedProject()
                    } else {
                        // The recording could not be moved out either, so the project's folder
                        // holds the only copy of it. Deleting the folder would lose the recording.
                        studioCoordinatorAtStop.leaveUnfinishedProject()
                    }
                }
            } else {
                studioCoordinatorAtStop.deleteUnfinishedProject()
            }
            updateProcessingProgress(1.0, status: "Done")
            if studioRecordingCoordinator === studioCoordinatorAtStop {
                studioRecordingCoordinator = nil
            }
        }

        if streamFailureMessage != nil,
           studioCoordinatorAtStop == nil,
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

        if let completedStudioProject {
            CaptureAnalyticsStore.shared.recordCapture(.video)
            if let savedVideoURL {
                lastVideoRecordingArtifacts = VideoRecordingArtifacts(
                    screenRecordingURL: savedVideoURL,
                    webcamRecordingURL: savedWebcamURL
                )
            } else {
                lastVideoRecordingArtifacts = nil
            }
            if isStudioSegment {
                NotificationCenter.default.post(name: .studioProjectsDidChange, object: nil)
            } else {
                openStudio(
                    projectID: completedStudioProject.id,
                    reopenPickerAfterClose: shouldReturnToPickerAfterRecording
                )
            }
        } else if let savedVideoURL {
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
        var completedCaptureWithoutEditor: CaptureType?
        if let writer = gifWriterAtStop {
            let url = SaveService.shared.generateURL(for: .gif)
            do {
                let settings = CaptureSettings.shared
                let gifShowTrimmer = streamFailureMessage == nil
                    && !activeRecordingWasStartedByAutomation
                    && settings.showGifTrimmer
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
                        gifData = try await Self.runOffMainThrowing {
                            try MouseClickOverlayProcessor.overlayOnGif(
                                gifData: inputGifData,
                                region: region,
                                events: events,
                                style: overlayStyle
                            )
                        }
                    }

                    if settings.showBrandingOverlay {
                        let inputGifData = gifData
                        gifData = try await Self.runOffMainThrowing {
                            try BrandingOverlayProcessor.applyToGifData(inputGifData)
                        }
                    }

                    updateProcessingProgress(0.8, status: shouldSaveImmediately ? "Saving…" : "Opening trimmer…")

                    if shouldSaveImmediately {
                        let dataToWrite = gifData
                        try await Self.runOffMainThrowing {
                            try GifWriter.writeGIF(dataToWrite, to: url)
                        }
                        SaveService.shared.handleSavedFile(url: url, type: .gif)
                    }

                    updateProcessingProgress(1.0, status: "Done")
                    CaptureAnalyticsStore.shared.recordCapture(.gif)
                    showGifTrimmer(
                        gifData: gifData,
                        outputURL: url,
                        discardsCaptureOnCancel: !shouldSaveImmediately,
                        reopenPickerAfterClose: shouldReturnToPickerAfterRecording
                    )
                } else {
                    if streamFailureMessage == nil {
                        try await writer.stop(outputURL: url)
                    } else {
                        let gifData = try writer.finishAfterStreamFailure()
                        try await Self.runOffMainThrowing {
                            try GifWriter.writeGIF(gifData, to: url)
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
                                let processedGifData = try MouseClickOverlayProcessor.overlayOnGif(
                                    gifData: gifData,
                                    region: region,
                                    events: events,
                                    style: overlayStyle
                                )
                                try GifWriter.writeGIF(processedGifData, to: tempURL)
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
                                let processedGifData = try BrandingOverlayProcessor.applyToGifData(gifData)
                                try GifWriter.writeGIF(processedGifData, to: tempURL)
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
                    completedCaptureWithoutEditor = .gif
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
        if !isStudioSegment, completedStudioProject == nil, let savedVideoURL {
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
                completedCaptureWithoutEditor = .video
            }
        }

        dismissProcessingIndicator()

        if isStudioSegment {
            if let completedStudioProject, case .automation = studioSegmentReceiver {
                automationStudioTake = (completedStudioProject.id, false)
            }
            if let segmentError {
                let recovery = completedStudioProject == nil ? "" : " The partial take is available in Studio drafts."
                let reason = segmentError.localizedDescription + recovery
                if let error = segmentError as? AutomationError {
                    finishStudioSegment(.failure(AutomationError(error.code, reason)))
                } else {
                    finishStudioSegment(.failure(StudioEditorInputError(reason)))
                }
            } else if let completedStudioProject {
                do {
                    if case .automation(_, let attach) = studioSegmentReceiver {
                        try await attach(completedStudioProject.id)
                        automationStudioTake = (completedStudioProject.id, true)
                    }
                    finishStudioSegment(.success(completedStudioProject))
                } catch {
                    let code = (error as? AutomationError)?.code ?? .internal
                    finishStudioSegment(.failure(AutomationError(code, "\(error.localizedDescription) The recording was kept as a Studio draft.")))
                }
            } else {
                finishStudioSegment(.failure(StudioEditorInputError("No recording was saved. Check available disk space and try again.")))
            }
            return
        }

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

        if let completedCaptureWithoutEditor {
            if shouldReturnToPickerAfterRecording,
               CaptureSettings.shared.shouldShowCapturePickerAfterCapture(for: completedCaptureWithoutEditor) {
                self.showRecordingPicker(for: completedCaptureWithoutEditor)
            }
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
        activeRecordingWasStartedByAutomation = false
        automationRecordingError = nil
        automationStudioTake = nil

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

    private func editorWindowClosed(
        continuingWith continuation: @escaping @MainActor () -> Void = {}
    ) {
#if APPSTORE
        StoreReviewPromptCoordinator.shared.editorWindowClosed(continuingWith: continuation)
#else
        continuation()
#endif
    }

    func notifyStoreReviewCaptureWorkBecameIdle() {
#if APPSTORE
        StoreReviewPromptCoordinator.shared.captureWorkBecameIdle()
#endif
    }

    private func scheduleStoreReviewWhenCaptureIsIdle() {
#if APPSTORE
        DispatchQueue.main.async { [weak self] in
            guard let self, !self.isAnyCaptureInProgress, !self.isCaptureWorkActive else { return }
            self.notifyStoreReviewCaptureWorkBecameIdle()
        }
#endif
    }

    private func showScreenshotEditor(
        for url: URL,
        initialSaveURL: URL,
        deleteSourceOnCancel: Bool,
        reopenPickerAfterClose: Bool,
        temporaryFile: ScreenshotTemporaryFileLease? = nil,
        onPresentationFailure: (() -> Void)? = nil,
        onPresentationSuccess: (() -> Void)? = nil
    ) {
        let onSaved: ((URL) -> Void)?
        let onUnexpectedClose: (() -> Void)?
        if let temporaryFile {
            onSaved = { [weak self] savedURL in
                temporaryFile.discard()
                if let self {
                    self.handleEditedScreenshotSave(savedURL)
                } else {
                    SaveService.shared.handleSavedFile(url: savedURL, type: .screenshot)
                }
            }
            onUnexpectedClose = { [self] in
                retainScreenshotAfterUnexpectedClose(
                    temporaryFile,
                    format: ImageFormat(rawValue: temporaryFile.url.pathExtension.lowercased()) ?? .png
                )
            }
        } else {
            onSaved = nil
            onUnexpectedClose = nil
        }
        ScreenshotEditorRegistry.shared.present(
            imageURL: url,
            initialSaveURL: initialSaveURL,
            deleteSourceAfterSave: deleteSourceOnCancel,
            onSaved: onSaved,
            onPresentationFailure: onPresentationFailure,
            onPresentationSuccess: onPresentationSuccess,
            onUnexpectedClose: onUnexpectedClose
        ) { [weak self] resultURL in
            if let resultURL, temporaryFile == nil {
                if let self {
                    self.handleEditedScreenshotSave(resultURL)
                } else {
                    SaveService.shared.handleSavedFile(url: resultURL, type: .screenshot)
                }
            }
            if let temporaryFile {
                temporaryFile.discard()
            } else if resultURL == nil && deleteSourceOnCancel {
                try? FileManager.default.removeItem(at: url)
            }
            DispatchQueue.main.async { [weak self] in
                self?.editorWindowClosed { [weak self] in
                    if reopenPickerAfterClose,
                       CaptureSettings.shared.shouldShowCapturePickerAfterCapture(for: .screenshot) {
                        self?.showScreenshotPicker()
                    }
                }
            }
        }
    }

    private func handleEditedScreenshotSave(_ url: URL) {
        SaveService.shared.handleSavedFile(url: url, type: .screenshot, copyToClipboard: false)
        guard CaptureSettings.shared.shouldCopyToClipboard(for: .screenshot),
              !SaveService.shared.copyScreenshotToClipboard(from: url) else { return }
        var sourceURL = url
        var temporaryFile: ScreenshotTemporaryFileLease? = nil
        let ext = url.pathExtension.lowercased()
        let format = ext == "jpeg" ? ImageFormat.jpeg : ImageFormat(rawValue: ext) ?? .png
        _ = recoverScreenshotCopy(sourceURL: &sourceURL, temporaryFile: &temporaryFile, format: format)
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
            // A video exported from Studio reopens its project, which is still editable.
            if StudioWindowRegistry.shared.openProject(forExportedVideoAt: item.url) {
                return
            }
            showTrimmer(for: item.url, saveImmediately: true)
        case .gif:
            do {
                let gifData = try GifCaptureData(contentsOf: item.url)
                showGifTrimmer(gifData: gifData, outputURL: item.url)
            } catch {
                SaveService.shared.showError("GIF could not be opened: \(error.localizedDescription)")
            }
        }
    }

    private func showTrimmer(for url: URL, saveImmediately: Bool, reopenPickerAfterClose: Bool = false) {
        let window = VideoTrimmerWindow(
            videoURL: url,
            discardsCaptureOnCancel: !saveImmediately
        ) { [weak self] resultURL in
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
            DispatchQueue.main.async { [self] in
                self.trimmerWindow = nil
                self.editorWindowClosed { [weak self] in
                    if reopenPickerAfterClose,
                       CaptureSettings.shared.shouldShowCapturePickerAfterCapture(for: .video) {
                        self?.showRecordingPicker(for: .video)
                    }
                }
            }
        }
        self.trimmerWindow = window
        // Defer showing to next run loop to avoid issues with menu tracking
        DispatchQueue.main.async {
            self.bringWindowToFront(window)
        }
    }

    private func openStudio(projectID: String, reopenPickerAfterClose: Bool = false) {
        StudioWindowRegistry.shared.open(projectID: projectID) { [weak self] in
            self?.editorWindowClosed { [weak self] in
                if reopenPickerAfterClose,
                   CaptureSettings.shared.shouldShowCapturePickerAfterCapture(for: .video) {
                    self?.showRecordingPicker(for: .video)
                }
            }
        }
    }

    private func showGifTrimmer(
        gifData: GifCaptureData,
        outputURL: URL,
        discardsCaptureOnCancel: Bool = false,
        reopenPickerAfterClose: Bool = false
    ) {
        let window = GifTrimmerWindow(
            gifData: gifData,
            outputURL: outputURL,
            discardsCaptureOnCancel: discardsCaptureOnCancel
        ) { [weak self] resultURL in
            guard let self else { return }
            if let resultURL {
                SaveService.shared.handleSavedFile(url: resultURL, type: .gif)
            }
            DispatchQueue.main.async { [self] in
                self.gifTrimmerWindow = nil
                self.editorWindowClosed { [weak self] in
                    if reopenPickerAfterClose,
                       CaptureSettings.shared.shouldShowCapturePickerAfterCapture(for: .gif) {
                        self?.showRecordingPicker(for: .gif)
                    }
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
            forStudio: pendingVideoIsForStudio,
            windowAreaSummary: pendingRecordingTarget?.windowAreaSelection.map {
                $0.wasClipped ? "Window area · fixed · clipped to display" : "Window area · fixed"
            },
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
                        countdownDuration: countdownDuration,
                        studioModeEnabled: self.pendingVideoIsForStudio
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
                self?.finishStudioSegment(.success(nil))
                self?.scheduleStoreReviewWhenCaptureIsIdle()
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
                do { try self?.togglePauseRecording() }
                catch { SaveService.shared.showError(error.localizedDescription) }
            },
            onRestart: { [weak self] in
                Task { @MainActor [weak self] in
                    do { try await self?.restartRecording() }
                    catch { SaveService.shared.showError(error.localizedDescription) }
                }
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
        let onCancel: (() -> Void)? = studioSegmentReceiver == nil ? nil : { [weak self] in
            self?.discardRecording()
        }
        let window = CountdownWindow(duration: duration, onCancel: onCancel) { [weak self] in
            self?.countdownWindow = nil
            action()
        }
        self.countdownWindow = window
        window.show()
    }

    private func showOnboardingIfNeeded() {
        let settings = CaptureSettings.shared
        if settings.hasCompletedOnboarding {
            showWhatsNewIfNeeded()
            return
        }
        guard onboardingWindow == nil else { return }

        // New installations get the tour, not a second introduction on their next launch.
        CaptureSettings.markWhatsNewSeen()

        let window = OnboardingWizardWindow(applyShortcut: { [weak self] binding, action in
            guard let self else { return "The capture shortcut service is unavailable." }
            return self.applyCaptureHotKey(binding, for: action)
        }) { [weak self] completed in
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

    private func showWhatsNewIfNeeded() {
        guard whatsNewWindow == nil, CaptureSettings.shouldShowWhatsNew() else { return }
        let window = WhatsNewWindow { [weak self] in
            CaptureSettings.markWhatsNewSeen()
            DispatchQueue.main.async {
                self?.whatsNewWindow = nil
            }
        }
        whatsNewWindow = window
        DispatchQueue.main.async { [weak self] in
            self?.bringWindowToFront(window)
        }
    }

    func showGuide() {
        _ = showGuide(topic: nil)
    }

    func showGuide(topic: GuideSection?) -> GuideSection {
        if let guideWindow {
            if let topic { guideWindow.selectTopic(topic) }
            DispatchQueue.main.async {
                self.bringWindowToFront(guideWindow)
            }
            return guideWindow.selectedTopic
        }

        let window = GuideWindow(onDismiss: { [weak self] in
            DispatchQueue.main.async {
                self?.guideWindow = nil
            }
        })

        if let topic { window.selectTopic(topic) }
        self.guideWindow = window
        DispatchQueue.main.async {
            self.bringWindowToFront(window)
        }
        return window.selectedTopic
    }

    private func showRecordingPicker(for type: CaptureType, cursorScreen: NSScreen? = nil, forSegment: Bool = false) {
        guard studioSegmentReceiver == nil || forSegment else { return }
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

        let intent = CapturePickerIntent(
            captureType: type,
            forStudio: pendingVideoIsForStudio
        )
        let panel = CapturePickerPanel(
            intent: intent,
            countdownEnabled: countdownEnabled,
            countdownDuration: countdownDuration,
            videoTimeLimitMinutes: settings.videoRecordingTimeLimitMinutes,
            onCapture: { [weak self] mode, enabled, duration, videoTimeLimitMinutes in
                guard let self else { return }
                self.dismissRecordingPicker()
                if intent.captureType == .video {
                    settings.videoRecordingTimeLimitMinutes = videoTimeLimitMinutes
                }
                Task {
                    await self.performRecordingSetup(
                        type: intent.captureType,
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
                self?.finishStudioSegment(.success(nil))
                self?.scheduleStoreReviewWhenCaptureIsIdle()
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
            if studioSegmentReceiver != nil {
                finishStudioSegment(.success(nil))
            } else if shouldReturnToPicker {
                showRecordingPicker(for: type, cursorScreen: cursorScreen)
            }
            return
        }

        pendingRecordingTarget = target
        pendingRecordingType = type
        shouldReturnToPickerAfterRecording = shouldReturnToPicker && studioSegmentReceiver == nil
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
            return CaptureTarget(region: region, studioCaptureKind: .region)
        case .screen:
            let screen = await chooseScreenForCapture(cursorScreen: cursorScreen)
            guard let screen else { return nil }
            guard let region = CaptureRegion.fullScreen(for: screen) else { return nil }
            return CaptureTarget(region: region, studioCaptureKind: .display)
        case .window:
            guard let window = await WindowSelector.selectWindow(),
                  let region = captureRegion(for: window)
            else {
                return nil
            }
            return CaptureTarget(region: region, studioCaptureKind: .window)
        case .windowArea:
            guard let window = await WindowSelector.selectWindowArea(),
                  let selection = WindowAreaCaptureResolver.resolve(window: window)
            else {
                return nil
            }
            return CaptureTarget(
                region: selection.region,
                studioCaptureKind: .window,
                windowAreaSelection: selection
            )
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
        ).pixelAligned()
    }

    private func appKitRect(fromSCFrame scFrame: CGRect, screens: [NSScreen]) -> CGRect {
        let primaryScreenTop = CaptureCoordinateMath.primaryDisplayHeight(
            forDisplayFrames: screens.map(\.frame)
        )
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
            self.scheduleStoreReviewWhenCaptureIsIdle()
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

    /// Updates consumers that depend on the geometry the stream was actually configured from.
    private func adoptResolvedRecordingRegion(_ region: CaptureRegion?) {
        guard let region else { return }
        if activeMouseClickRegion != nil {
            activeMouseClickRegion = region
        }
        studioRecordingCoordinator?.updateCaptureRegion(region)
    }

    private func startMouseClickMonitoringIfNeeded(for type: CaptureType, region: CaptureRegion) {
        guard shouldCaptureMouseClicks(for: type) else {
            _ = stopMouseClickMonitoring()
            return
        }

        _ = stopMouseClickMonitoring()

        let monitor = MouseClickMonitor()
        // Studio stores clicks on the recording timeline, and drops the ones made while paused or
        // before the first frame because nothing in the video corresponds to them.
        var timelineTimeProvider: (() -> TimeInterval?)?
        if type == .video, studioRecordingCoordinator != nil {
            timelineTimeProvider = { [weak self] in
                guard let self,
                      !self.isRecordingPaused,
                      let seconds = self.videoRecorder?.currentTimelineTimeIfStarted()?.seconds,
                      seconds.isFinite else {
                    return nil
                }
                return seconds
            }
        }
        monitor.start(timelineTimeProvider: timelineTimeProvider)
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

    nonisolated private static func appVersionString() -> String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
    }

    // Bridges synchronous CPU-heavy work to a background queue so it does not
    // block the @MainActor run loop (which would freeze the processing indicator).
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
        codec: VideoCodec,
        onProgress: ((Double) -> Void)? = nil
    ) async throws -> URL {
        try await Task.detached(priority: .userInitiated) {
            try await MouseClickOverlayProcessor.overlayOnVideo(
                sourceURL: sourceURL,
                region: region,
                events: events,
                outputURL: outputURL,
                style: style,
                codec: codec,
                onProgress: onProgress
            )
        }.value
    }

    nonisolated private static func mixDownAudioOffMain(at url: URL) async -> RecordingAudioMixdown.Outcome {
        await Task.detached(priority: .userInitiated) {
            await RecordingAudioMixdown.mixDownIfNeeded(at: url)
        }.value
    }

    nonisolated private static func overlayBrandingVideoOffMain(
        sourceURL: URL,
        outputURL: URL,
        includeBranding: Bool,
        webcamOverlay: BrandingOverlayProcessor.WebcamOverlayOptions?,
        codec: VideoCodec,
        onProgress: ((Double) -> Void)? = nil
    ) async throws -> URL {
        try await Task.detached(priority: .userInitiated) {
            try await BrandingOverlayProcessor.overlayOnVideo(
                sourceURL: sourceURL,
                outputURL: outputURL,
                includeBranding: includeBranding,
                webcamOverlay: webcamOverlay,
                codec: codec,
                onProgress: onProgress
            )
        }.value
    }
}
