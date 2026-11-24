#if APPSTORE
import AppKit
import StoreKit
import SwiftUI

@MainActor
final class StoreReviewPromptWindow: NSWindow, NSWindowDelegate {
    private static var current: StoreReviewPromptWindow?

    private let reviewController = NSViewController()
    private var onComplete: (@MainActor () -> Void)?
    private var didRequestReview = false
    private var didClose = false

    static func present(
        requestReview: @escaping StoreReviewPromptCoordinator.ReviewAction,
        onComplete: @escaping @MainActor () -> Void
    ) {
        let window = StoreReviewPromptWindow(requestReview: requestReview, onComplete: onComplete)
        current = window
        WindowPresenter.shared.present(window)
    }

    private init(
        requestReview: @escaping StoreReviewPromptCoordinator.ReviewAction,
        onComplete: @escaping @MainActor () -> Void
    ) {
        super.init(
            contentRect: NSRect(x: 0, y: 0, width: 540, height: 370),
            styleMask: [.titled, .closable],
            backing: .buffered,
            defer: false
        )
        self.onComplete = onComplete
        delegate = self
        isReleasedWhenClosed = false
        title = "Enjoying Tiny Clips?"
        center()
        reviewController.view = NSHostingView(rootView: StoreReviewPromptView(
            onReview: { [weak self] in
                guard let self else { return false }
                return requestReview {
                    self.didRequestReview = true
                    AppStore.requestReview(in: self.reviewController)
                }
            },
            onDismiss: { [weak self] in
                self?.close()
            }
        ))
        contentViewController = reviewController
    }

    func windowDidEndSheet(_ notification: Notification) {
        if didRequestReview {
            close()
        }
    }

    func windowWillClose(_ notification: Notification) {
        guard !didClose else { return }
        didClose = true
        let callback = onComplete
        onComplete = nil
        DispatchQueue.main.async {
            Self.current = nil
            callback?()
        }
    }

    override func cancelOperation(_ sender: Any?) {
        guard attachedSheet == nil else { return }
        close()
    }
}

private struct StoreReviewPromptView: View {
    let onReview: () -> Bool
    let onDismiss: () -> Void

    @State private var showFeedback = false
    @State private var feedbackFailedToOpen = false
    @State private var showFeedbackError = false
    @State private var reviewUnavailable = false
    @State private var reviewRequested = false

    var body: some View {
        VStack(spacing: 0) {
            Spacer()
            VStack(spacing: 12) {
                Image(nsImage: NSApp.applicationIconImage)
                    .resizable()
                    .scaledToFit()
                    .frame(width: 72, height: 72)
                    .accessibilityHidden(true)
                Text(reviewRequested ? "App Store review" : "Enjoying Tiny Clips?")
                    .font(.title2.bold())
                    .accessibilityAddTraits(.isHeader)
                Text(reviewRequested
                    ? "The App Store decides whether to show a review request. Close this window when you're finished."
                    : "An App Store review helps other people discover Tiny Clips. If something could be better, tell us directly.")
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 400)
                if reviewUnavailable && !reviewRequested {
                    Text("Finish any capture and close all editors before leaving a review.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .accessibilityIdentifier("StoreReviewUnavailableMessage")
                }
            }
            Spacer()
            Divider()
            HStack(spacing: 10) {
                Spacer()
                if reviewRequested {
                    Button("Done", action: onDismiss)
                        .buttonStyle(.borderedProminent)
                        .keyboardShortcut(.defaultAction)
                } else {
                    Button("Not now", action: onDismiss)
                        .keyboardShortcut(.cancelAction)
                        .accessibilityIdentifier("StoreReviewNotNowButton")
                    Button("Send feedback") {
                        showFeedback = true
                    }
                    .accessibilityIdentifier("StoreReviewFeedbackButton")
                    Button("Leave a review") {
                        reviewRequested = onReview()
                        reviewUnavailable = !reviewRequested
                    }
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
                    .accessibilityIdentifier("StoreReviewConfirmButton")
                }
            }
            .padding(.horizontal, 24)
            .padding(.vertical, 16)
        }
        .background(UserWindowPresentationObserver())
        .sheet(isPresented: $showFeedback, onDismiss: feedbackSheetClosed) {
            QuickBugReportFormView(context: .current) { type, title, description in
                let url = QuickBugReportURLBuilder.makeURL(
                    type: type,
                    title: title,
                    description: description,
                    context: .current
                )
                if !NSWorkspace.shared.open(url) {
                    feedbackFailedToOpen = true
                }
            }
        }
        .alert("Couldn't open GitHub", isPresented: $showFeedbackError) {
            Button("Close", role: .cancel, action: onDismiss)
        } message: {
            Text("Try Send Feedback from the Tiny Clips menu.")
        }
    }

    private func feedbackSheetClosed() {
        if feedbackFailedToOpen {
            showFeedbackError = true
        } else {
            onDismiss()
        }
    }
}
#endif
