import XCTest
@testable import TinyClips

@MainActor
final class CaptureAnalyticsStoreTests: XCTestCase {
    private var suiteName: String!
    private var defaults: UserDefaults!
    private var calendar: Calendar!

    override func setUpWithError() throws {
        suiteName = "TinyClipsTests.\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defaults.removePersistentDomain(forName: suiteName)
        calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
    }

    override func tearDownWithError() throws {
        defaults.removePersistentDomain(forName: suiteName)
        defaults = nil
        calendar = nil
        suiteName = nil
    }

    func testAggregationAndPersistenceWithIsolatedDefaults() throws {
        let store = CaptureAnalyticsStore(userDefaults: defaults, calendar: calendar)
        let date = try XCTUnwrap(
            calendar.date(from: DateComponents(
                year: 2026,
                month: 8,
                day: 12,
                hour: 14
            ))
        )

        store.recordCapture(.screenshot, on: date)
        store.recordCapture(.screenshot, on: date)
        store.recordCapture(.video, on: date)

        XCTAssertEqual(store.totalCount(for: .screenshot, days: 1, referenceDate: date), 2)
        XCTAssertEqual(store.lifetimeTotal(for: .video), 1)
        XCTAssertEqual(store.mostActiveHour()?.hour, 14)
        XCTAssertEqual(store.mostActiveHour()?.count, 3)

        let reloaded = CaptureAnalyticsStore(userDefaults: defaults, calendar: calendar)
        XCTAssertEqual(reloaded.lifetimeTotal(for: .screenshot), 2)
        XCTAssertEqual(reloaded.hourlyBreakdown()[14].count, 3)
    }

    func testReviewStateCountsThirtyCompletedCapturesAndClaimsOnlyOnce() {
        let reviewState = StoreReviewPromptState(defaults: defaults)
        let analytics = CaptureAnalyticsStore(
            userDefaults: defaults,
            calendar: calendar,
            reviewPromptState: reviewState
        )

        for _ in 0..<29 {
            analytics.recordCapture(.screenshot)
        }

        XCTAssertEqual(reviewState.completedCaptureCount, 29)
        XCTAssertFalse(reviewState.claimPromptAfterEditorWindowClosed())

        analytics.recordCapture(.video)
        XCTAssertEqual(reviewState.completedCaptureCount, StoreReviewPromptState.threshold)
        XCTAssertFalse(reviewState.claimPromptAfterEditorWindowClosed(isCaptureInProgress: true))
        XCTAssertFalse(reviewState.hasClaimedPrompt)
        XCTAssertFalse(reviewState.claimPromptAfterEditorWindowClosed(hasOpenCaptureEditor: true))
        XCTAssertFalse(reviewState.hasClaimedPrompt)
        XCTAssertTrue(reviewState.claimPromptAfterEditorWindowClosed())
        XCTAssertFalse(reviewState.claimPromptAfterEditorWindowClosed())

        CaptureSettings.resetStoredDefaults(defaults)
        let reloaded = StoreReviewPromptState(defaults: defaults)
        XCTAssertEqual(reloaded.completedCaptureCount, StoreReviewPromptState.threshold)
        XCTAssertTrue(reloaded.hasClaimedPrompt)
    }

    func testCapturesCanAccumulatePastThresholdWithoutClaiming() {
        let state = StoreReviewPromptState(defaults: defaults)
        for _ in 0..<35 {
            state.recordCompletedCapture()
        }

        XCTAssertEqual(state.completedCaptureCount, 35)
        XCTAssertTrue(state.isEligible)
        XCTAssertFalse(state.hasClaimedPrompt)
    }

    func testReviewStateSurvivesCaptureAnalyticsAndSettingsReset() {
        let reviewState = StoreReviewPromptState(defaults: defaults)
        reviewState.recordCompletedCapture()
        reviewState.recordCompletedCapture()

        let analytics = CaptureAnalyticsStore(
            userDefaults: defaults,
            calendar: calendar,
            reviewPromptState: reviewState
        )
        analytics.recordCapture(.gif)
        analytics.clear()
        CaptureSettings.resetStoredDefaults(defaults)

        let reloaded = StoreReviewPromptState(defaults: defaults)
        XCTAssertEqual(reloaded.completedCaptureCount, 3)
        XCTAssertFalse(reloaded.hasClaimedPrompt)
    }

    func testReviewStateStartsFreshInsteadOfMigratingLegacyCount() {
        defaults.set(47, forKey: "appStoreClipCountForReview")

        let state = StoreReviewPromptState(defaults: defaults)

        XCTAssertEqual(state.completedCaptureCount, 0)
        XCTAssertFalse(state.hasClaimedPrompt)
        XCTAssertNil(defaults.object(forKey: "appStoreClipCountForReview"))
    }

    func testReviewStatePreservesLegacyRequestWithoutCountingItsHistory() {
        defaults.set(47, forKey: "appStoreClipCountForReview")
        defaults.set(true, forKey: "appStoreReviewRequested")

        let state = StoreReviewPromptState(defaults: defaults)
        XCTAssertEqual(state.completedCaptureCount, 0)
        XCTAssertTrue(state.hasClaimedPrompt)
        XCTAssertFalse(state.claimPromptAfterEditorWindowClosed())

        state.recordCompletedCapture()
        XCTAssertEqual(state.completedCaptureCount, 0)
        XCTAssertTrue(defaults.bool(forKey: StoreReviewPromptState.promptClaimedKey))
    }

    func testReviewIsRequestedOnlyAfterChoosingItAndOnlyOnce() {
        let harness = eligibleReviewHarness()
        var pickerContinuations = 0

        harness.coordinator.editorWindowClosed { pickerContinuations += 1 }
        XCTAssertEqual(harness.promptCount, 1)
        XCTAssertEqual(harness.reviewRequestCount, 0)
        XCTAssertEqual(pickerContinuations, 0)
        XCTAssertTrue(harness.state.hasClaimedPrompt)

        XCTAssertEqual(harness.chooseReview(), true)
        XCTAssertEqual(harness.reviewRequestCount, 1)
        XCTAssertEqual(pickerContinuations, 0)
        XCTAssertEqual(harness.chooseReview(), false)
        harness.promptCompletion?()
        XCTAssertEqual(pickerContinuations, 1)

        harness.promptCompletion?()
        harness.coordinator.editorWindowClosed()
        XCTAssertEqual(harness.promptCount, 1)
        XCTAssertEqual(harness.reviewRequestCount, 1)
        XCTAssertEqual(pickerContinuations, 1)
    }

    func testDismissOrFeedbackKeepsContinuationsUntilThePromptFlowCloses() {
        let harness = eligibleReviewHarness()
        var pickerContinuations = 0

        harness.coordinator.editorWindowClosed { pickerContinuations += 1 }
        harness.coordinator.captureWorkBecameIdle()
        harness.coordinator.editorWindowClosed { pickerContinuations += 1 }
        XCTAssertEqual(harness.promptCount, 1)
        XCTAssertEqual(pickerContinuations, 0)

        harness.promptCompletion?()
        XCTAssertEqual(harness.reviewRequestCount, 0)
        XCTAssertEqual(pickerContinuations, 2)
        XCTAssertEqual(harness.chooseReview(), false)

        harness.coordinator.editorWindowClosed()
        XCTAssertEqual(harness.promptCount, 1)
    }

    func testIdleDoesNotShowEligiblePromptWithoutAnotherEditorClose() {
        let harness = eligibleReviewHarness()
        var pickerContinuations = 0
        harness.isCaptureInProgress = true
        harness.hasActiveCaptureWork = true

        harness.coordinator.editorWindowClosed { pickerContinuations += 1 }
        XCTAssertEqual(harness.promptCount, 0)
        XCTAssertFalse(harness.state.hasClaimedPrompt)

        harness.isCaptureInProgress = false
        harness.hasActiveCaptureWork = false
        harness.coordinator.captureWorkBecameIdle()
        XCTAssertEqual(pickerContinuations, 1)
        XCTAssertEqual(harness.promptCount, 0)

        harness.coordinator.editorWindowClosed()
        XCTAssertEqual(harness.promptCount, 1)
    }

    func testReviewChoiceIsUnavailableIfCaptureOrEditingStarts() {
        let harness = eligibleReviewHarness()
        harness.coordinator.editorWindowClosed()

        harness.isCaptureInProgress = true
        XCTAssertEqual(harness.chooseReview(), false)
        harness.isCaptureInProgress = false
        harness.hasActiveCaptureWork = true
        XCTAssertEqual(harness.chooseReview(), false)
        harness.hasActiveCaptureWork = false
        harness.hasOpenCaptureEditor = true
        XCTAssertEqual(harness.chooseReview(), false)
        XCTAssertEqual(harness.reviewRequestCount, 0)
        harness.hasOpenCaptureEditor = false
        harness.hasScreenshotRecovery = true
        XCTAssertEqual(harness.chooseReview(), false)
        harness.hasScreenshotRecovery = false
        XCTAssertEqual(harness.chooseReview(), true)
        XCTAssertEqual(harness.reviewRequestCount, 1)
    }

    func testScreenshotRecoveryBlocksPromptAndPickerContinuation() {
        let harness = eligibleReviewHarness()
        var pickerContinuations = 0
        harness.hasScreenshotRecovery = true
        harness.coordinator.editorWindowClosed { pickerContinuations += 1 }
        harness.coordinator.captureWorkBecameIdle()
        XCTAssertEqual(harness.promptCount, 0)
        XCTAssertEqual(pickerContinuations, 0)
        XCTAssertFalse(harness.state.hasClaimedPrompt)

        harness.hasScreenshotRecovery = false
        harness.coordinator.captureWorkBecameIdle()
        XCTAssertEqual(pickerContinuations, 1)
        harness.coordinator.editorWindowClosed()
        XCTAssertEqual(harness.promptCount, 1)
    }

    func testScreenshotAnalyticsRequireSuccessfulOutputAndCountRecoveryOnlyOnce() throws {
        for saved in [false, true] {
            for copied in [false, true] {
                for recovered in [false, true] {
                    var captures = 0
                    let success = ScreenshotOutputSuccess { captures += 1 }
                    _ = try ScreenshotOutputRouter.deliver(
                        policy: ScreenshotOutputPolicy(
                            saveToDisk: saved, copyToClipboard: true, openEditor: true
                        ),
                        recordSaved: {},
                        copy: { copied },
                        recoverCopy: { recovered ? .available : .retained },
                        openEditor: { false },
                        releaseTemporary: {},
                        outputSucceeded: { success.recordOnce() }
                    )
                    XCTAssertEqual(captures, saved || copied || recovered ? 1 : 0)

                    // A later editor attachment or recovery must not count the same image twice.
                    success.recordOnce()
                    success.recordOnce()
                    XCTAssertEqual(captures, 1)
                }
            }
        }
    }

    func testChosenReviewAndPickerWaitUntilCaptureAndEditorsAreIdle() {
        let harness = eligibleReviewHarness()
        var pickerContinuations = 0
        harness.coordinator.editorWindowClosed { pickerContinuations += 1 }
        XCTAssertEqual(harness.chooseReview(), true)

        harness.isCaptureInProgress = true
        harness.hasActiveCaptureWork = true
        harness.hasOpenCaptureEditor = true
        harness.promptCompletion?()
        XCTAssertEqual(harness.reviewRequestCount, 1)
        XCTAssertEqual(pickerContinuations, 0)

        harness.isCaptureInProgress = false
        harness.hasActiveCaptureWork = false
        harness.coordinator.captureWorkBecameIdle()
        XCTAssertEqual(harness.reviewRequestCount, 1)
        XCTAssertEqual(pickerContinuations, 0)

        harness.hasOpenCaptureEditor = false
        harness.hasScreenshotRecovery = true
        harness.coordinator.captureWorkBecameIdle()
        XCTAssertEqual(pickerContinuations, 0)
        harness.hasScreenshotRecovery = false
        harness.coordinator.captureWorkBecameIdle()
        XCTAssertEqual(harness.reviewRequestCount, 1)
        XCTAssertEqual(pickerContinuations, 1)
    }

    func testChosenReviewAndContinuationWaitUntilCapturePickerCloses() {
        let harness = eligibleReviewHarness()
        var pickerContinuations = 0
        harness.coordinator.editorWindowClosed { pickerContinuations += 1 }
        XCTAssertEqual(harness.chooseReview(), true)

        harness.isCaptureInProgress = true
        harness.promptCompletion?()
        harness.coordinator.captureWorkBecameIdle()
        XCTAssertEqual(harness.reviewRequestCount, 1)
        XCTAssertEqual(pickerContinuations, 0)

        harness.isCaptureInProgress = false
        harness.coordinator.captureWorkBecameIdle()
        XCTAssertEqual(harness.reviewRequestCount, 1)
        XCTAssertEqual(pickerContinuations, 1)
    }

    private func eligibleReviewHarness() -> StoreReviewPromptHarness {
        let harness = StoreReviewPromptHarness(defaults: defaults)
        for _ in 0..<StoreReviewPromptState.threshold {
            harness.state.recordCompletedCapture()
        }
        return harness
    }
}

@MainActor
private final class StoreReviewPromptHarness {
    let state: StoreReviewPromptState
    var isCaptureInProgress = false
    var hasActiveCaptureWork = false
    var hasScreenshotRecovery = false
    var hasOpenCaptureEditor = false
    var promptCount = 0
    var reviewRequestCount = 0
    var requestReview: StoreReviewPromptCoordinator.ReviewAction?
    var promptCompletion: (@MainActor () -> Void)?

    lazy var coordinator = StoreReviewPromptCoordinator(
        state: state,
        isCaptureInProgress: { [unowned self] in self.isCaptureInProgress },
        hasActiveCaptureWork: { [unowned self] in self.hasActiveCaptureWork },
        hasScreenshotRecovery: { [unowned self] in self.hasScreenshotRecovery },
        hasOpenCaptureEditor: { [unowned self] in self.hasOpenCaptureEditor },
        presentPrompt: { [unowned self] requestReview, completion in
            self.promptCount += 1
            self.requestReview = requestReview
            self.promptCompletion = completion
        }
    )

    func chooseReview() -> Bool? {
        requestReview?({ self.reviewRequestCount += 1 })
    }

    init(defaults: UserDefaults) {
        state = StoreReviewPromptState(defaults: defaults)
    }
}
