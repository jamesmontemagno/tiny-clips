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
}
