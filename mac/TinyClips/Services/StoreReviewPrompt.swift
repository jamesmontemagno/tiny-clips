import Foundation
#if APPSTORE
import AppKit
import StoreKit
import SwiftUI
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
    private var window: StoreReviewPromptWindow?
    private var continuations: [@MainActor () -> Void] = []

    init(state: StoreReviewPromptState = .shared) {
        self.state = state
    }

    func editorWindowClosed(continuingWith continuation: @escaping @MainActor () -> Void = {}) {
        if window != nil {
            continuations.append(continuation)
            return
        }

        if isCaptureInProgress {
            continuation()
            return
        }

        guard state.isEligible else {
            continuation()
            return
        }

        if hasOpenCaptureEditor {
            continuations.append(continuation)
            return
        }

        guard state.claimPromptAfterEditorWindowClosed(
            isCaptureInProgress: isCaptureInProgress,
            hasOpenCaptureEditor: hasOpenCaptureEditor
        ) else {
            continuation()
            return
        }
        continuations.append(continuation)

        let reviewWindow = StoreReviewPromptWindow { [weak self] in
            guard let self else { return }
            self.window = nil
            self.flushContinuations()
        }
        window = reviewWindow
        WindowPresenter.shared.present(reviewWindow)
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
}

@MainActor
private final class StoreReviewPromptWindow: NSWindow, NSWindowDelegate {
    private var onDismiss: (() -> Void)?
    private var didDismiss = false

    convenience init(onDismiss: @escaping () -> Void) {
        self.init(
            contentRect: NSRect(x: 0, y: 0, width: 470, height: 280),
            styleMask: [.titled, .closable],
            backing: .buffered,
            defer: false
        )
        self.onDismiss = onDismiss
        self.delegate = self
        self.title = "Enjoying Tiny Clips?"
        self.isReleasedWhenClosed = false
        self.contentMinSize = NSSize(width: 470, height: 280)
        self.center()

        self.contentView = NSHostingView(rootView: StoreReviewPromptView(
            onDismiss: { [weak self] in self?.close() },
            onReview: { [weak self] in
                DispatchQueue.main.async {
                    self?.close()
                }
            }
        ))
    }

    func windowWillClose(_ notification: Notification) {
        finish()
    }

    override func cancelOperation(_ sender: Any?) {
        close()
    }

    private func finish() {
        guard !didDismiss else { return }
        didDismiss = true
        let callback = onDismiss
        onDismiss = nil
        callback?()
    }
}

private struct StoreReviewPromptView: View {
    let onDismiss: () -> Void
    let onReview: () -> Void

    @Environment(\.requestReview) private var requestReview

    var body: some View {
        VStack(spacing: 18) {
            Image(nsImage: NSApplication.shared.applicationIconImage)
                .resizable()
                .frame(width: 64, height: 64)
                .accessibilityHidden(true)

            VStack(spacing: 7) {
                Text("Enjoying Tiny Clips?")
                    .font(.title2.bold())
                    .accessibilityAddTraits(.isHeader)
                Text("A quick App Store review helps other people discover Tiny Clips.")
                    .font(.body)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 360)
            }

            HStack(spacing: 10) {
                Button("Not now", action: onDismiss)
                    .keyboardShortcut(.cancelAction)
                    .accessibilityIdentifier("StoreReviewNotNowButton")

                Button("Review Tiny Clips") {
                    requestReview()
                    onReview()
                }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.defaultAction)
                .accessibilityIdentifier("StoreReviewConfirmButton")
            }
            .padding(.top, 2)
        }
        .padding(28)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(.background)
    }
}
#endif
