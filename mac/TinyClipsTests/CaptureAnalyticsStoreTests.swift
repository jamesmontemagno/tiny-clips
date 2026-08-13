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
}
