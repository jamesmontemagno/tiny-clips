import Foundation
#if APPSTORE
import AppKit
import StoreKit
#endif

@MainActor
final class StoreReviewPromptState {
    static let shared = StoreReviewPromptState()
    static let threshold = 30
    static let captureCountKey = "storeReviewCaptureCountV1"
    static let promptClaimedKey = "storeReviewPromptClaimedV1"

    private static let legacyCaptureCountKey = "appStoreClipCountForReview"
    private static let legacyPromptRequestedKey = "appStoreReviewRequested"

    private let defaults: UserDefaults

    private(set) var completedCaptureCount: Int
    private(set) var hasClaimedPrompt: Bool
    var isEligible: Bool {
        !hasClaimedPrompt && completedCaptureCount >= Self.threshold
    }

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        completedCaptureCount = max(0, defaults.integer(forKey: Self.captureCountKey))

        let wasPreviouslyRequested = defaults.bool(forKey: Self.legacyPromptRequestedKey)
        hasClaimedPrompt = defaults.bool(forKey: Self.promptClaimedKey) || wasPreviouslyRequested
        if wasPreviouslyRequested {
            defaults.set(true, forKey: Self.promptClaimedKey)
        }

        defaults.removeObject(forKey: Self.legacyCaptureCountKey)
        defaults.removeObject(forKey: Self.legacyPromptRequestedKey)
    }

    func recordCompletedCapture() {
        guard !hasClaimedPrompt else { return }
        completedCaptureCount += 1
        defaults.set(completedCaptureCount, forKey: Self.captureCountKey)
    }

    func claimPromptAfterEditorWindowClosed(
        isCaptureInProgress: Bool = false,
        hasOpenCaptureEditor: Bool = false
    ) -> Bool {
        guard !isCaptureInProgress,
              !hasOpenCaptureEditor,
              !hasClaimedPrompt,
              completedCaptureCount >= Self.threshold else {
            return false
        }
        hasClaimedPrompt = true
        defaults.set(true, forKey: Self.promptClaimedKey)
        return true
    }
}

#if APPSTORE
@MainActor
final class StoreReviewPromptCoordinator {
    static let shared = StoreReviewPromptCoordinator()

    private let state: StoreReviewPromptState
    private var continuations: [@MainActor () -> Void] = []

    init(state: StoreReviewPromptState = .shared) {
        self.state = state
    }

    func editorWindowClosed(continuingWith continuation: @escaping @MainActor () -> Void = {}) {
        continuations.append(continuation)

        if isCaptureInProgress || hasOpenCaptureEditor {
            return
        }

        if state.isEligible,
           state.claimPromptAfterEditorWindowClosed(
                isCaptureInProgress: false,
                hasOpenCaptureEditor: false
           ) {
            SKStoreReviewController.requestReview()
        }

        flushContinuations()
    }

    /// Resumes picker work that was deferred during another capture without evaluating review
    /// eligibility or attempting to show the system prompt.
    func captureWorkBecameIdle() {
        guard !hasActiveCaptureWork, !hasOpenCaptureEditor else {
            return
        }

        flushContinuations()
    }

    private func flushContinuations() {
        let pending = continuations
        continuations.removeAll()
        pending.forEach { $0() }
    }

    private var hasOpenCaptureEditor: Bool {
        if ScreenshotEditorRegistry.shared.hasOpenSessions
            || StudioWindowRegistry.shared.hasOpenWindows {
            return true
        }

        return NSApp.windows.contains { window in
            window.isVisible && (window is VideoTrimmerWindow || window is GifTrimmerWindow)
        }
    }

    private var isCaptureInProgress: Bool {
        CaptureManager.current?.isAnyCaptureInProgress == true
    }

    private var hasActiveCaptureWork: Bool {
        CaptureManager.current?.isCaptureWorkActive == true
    }
}
#endif
