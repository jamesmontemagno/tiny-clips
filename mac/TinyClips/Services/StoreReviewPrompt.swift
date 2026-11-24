import Foundation
#if APPSTORE
import AppKit
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

@MainActor
final class StoreReviewPromptCoordinator {
    typealias ReviewAction = @MainActor (@MainActor () -> Void) -> Bool
    typealias PromptPresenter = @MainActor (
        @escaping ReviewAction,
        @escaping @MainActor () -> Void
    ) -> Void

#if APPSTORE
    static let shared = StoreReviewPromptCoordinator(
        state: .shared,
        isCaptureInProgress: { CaptureManager.current?.isAnyCaptureInProgress == true },
        hasActiveCaptureWork: { CaptureManager.current?.isCaptureWorkActive == true },
        hasOpenCaptureEditor: {
            if ScreenshotEditorRegistry.shared.hasOpenSessions
                || StudioWindowRegistry.shared.hasOpenWindows {
                return true
            }
            return NSApp.windows.contains { window in
                window.isVisible && (window is VideoTrimmerWindow || window is GifTrimmerWindow)
            }
        },
        presentPrompt: { requestReview, completion in
            StoreReviewPromptWindow.present(requestReview: requestReview, onComplete: completion)
        }
    )
#endif

    private let state: StoreReviewPromptState
    private let isCaptureInProgress: @MainActor () -> Bool
    private let hasActiveCaptureWork: @MainActor () -> Bool
    private let hasOpenCaptureEditor: @MainActor () -> Bool
    private let presentPrompt: PromptPresenter
    private var continuations: [@MainActor () -> Void] = []
    private var isPromptOpen = false
    private var didRequestReview = false

    init(
        state: StoreReviewPromptState,
        isCaptureInProgress: @escaping @MainActor () -> Bool,
        hasActiveCaptureWork: @escaping @MainActor () -> Bool,
        hasOpenCaptureEditor: @escaping @MainActor () -> Bool,
        presentPrompt: @escaping PromptPresenter
    ) {
        self.state = state
        self.isCaptureInProgress = isCaptureInProgress
        self.hasActiveCaptureWork = hasActiveCaptureWork
        self.hasOpenCaptureEditor = hasOpenCaptureEditor
        self.presentPrompt = presentPrompt
    }

    func editorWindowClosed(continuingWith continuation: @escaping @MainActor () -> Void = {}) {
        continuations.append(continuation)

        guard !isPromptOpen,
              !isCaptureInProgress(),
              !hasActiveCaptureWork(),
              !hasOpenCaptureEditor() else { return }

        if state.claimPromptAfterEditorWindowClosed() {
            isPromptOpen = true
            presentPrompt(
                { [weak self] requestReview in
                    guard let self, self.isPromptOpen, !self.didRequestReview,
                          !self.isCaptureInProgress(),
                          !self.hasActiveCaptureWork(),
                          !self.hasOpenCaptureEditor() else { return false }
                    self.didRequestReview = true
                    requestReview()
                    return true
                },
                { [weak self] in
                    self?.promptClosed()
                }
            )
            return
        }

        resumePendingActionsWhenIdle()
    }

    func captureWorkBecameIdle() {
        resumePendingActionsWhenIdle()
    }

    private func promptClosed() {
        guard isPromptOpen else { return }
        isPromptOpen = false
        resumePendingActionsWhenIdle()
    }

    private func resumePendingActionsWhenIdle() {
        guard !isPromptOpen, !isCaptureInProgress(),
              !hasActiveCaptureWork(), !hasOpenCaptureEditor() else { return }
        flushContinuations()
    }

    private func flushContinuations() {
        let pending = continuations
        continuations.removeAll()
        pending.forEach { $0() }
    }
}
