import Foundation
import SwiftUI

@MainActor
final class CaptureAnalyticsStore: ObservableObject {
    static let shared = CaptureAnalyticsStore()

    struct DailyCounts: Codable {
        var screenshotCount = 0
        var videoCount = 0
        var gifCount = 0

        var totalCount: Int {
            screenshotCount + videoCount + gifCount
        }

        mutating func increment(for type: CaptureType) {
            switch type {
            case .screenshot:
                screenshotCount += 1
            case .video:
                videoCount += 1
            case .gif:
                gifCount += 1
            }
        }

        func count(for type: CaptureType) -> Int {
            switch type {
            case .screenshot:
                return screenshotCount
            case .video:
                return videoCount
            case .gif:
                return gifCount
            }
        }
    }

    struct DaySummary: Identifiable {
        let date: Date
        let counts: DailyCounts

        var id: Date { date }
    }

    /// A single weekday's aggregate total across all capture types, over some day range.
    struct WeekdayTotal: Identifiable {
        /// `Calendar` weekday component: 1 = Sunday ... 7 = Saturday.
        let weekday: Int
        let count: Int

        var id: Int { weekday }
    }

    /// A single hour-of-day's aggregate total across all capture types, all-time.
    struct HourTotal: Identifiable {
        /// 0...23, in the current calendar's time zone.
        let hour: Int
        let count: Int

        var id: Int { hour }
    }

    private enum Storage {
        static let key = "captureAnalyticsHistoryV1"
        static let lifetimeKey = "captureAnalyticsLifetimeV1"
        static let hourlyKey = "captureAnalyticsHourlyV1"
        static let retainedDays = 30
    }

    @Published private var history: [String: DailyCounts]
    @Published private(set) var lifetimeTotals: DailyCounts
    @Published private var hourlyTotals: [Int: Int]

    private let userDefaults: UserDefaults
    private let calendar: Calendar
    private let decoder = JSONDecoder()
    private let encoder = JSONEncoder()
    private lazy var formatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.calendar = calendar
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd"
        formatter.isLenient = false
        return formatter
    }()

    init(
        userDefaults: UserDefaults = .standard,
        calendar: Calendar = .autoupdatingCurrent
    ) {
        self.userDefaults = userDefaults
        self.calendar = calendar

        if let data = userDefaults.data(forKey: Storage.key),
           let decoded = try? decoder.decode([String: DailyCounts].self, from: data) {
            history = decoded
        } else {
            history = [:]
        }

        if let data = userDefaults.data(forKey: Storage.lifetimeKey),
           let decoded = try? decoder.decode(DailyCounts.self, from: data) {
            lifetimeTotals = decoded
        } else {
            lifetimeTotals = DailyCounts()
        }

        if let data = userDefaults.data(forKey: Storage.hourlyKey),
           let decoded = try? decoder.decode([Int: Int].self, from: data) {
            hourlyTotals = decoded
        } else {
            hourlyTotals = [:]
        }

        pruneAndPersistIfNeeded()
    }

    func recordCapture(_ type: CaptureType, on date: Date = Date()) {
        pruneIfNeeded(referenceDate: date)

        let key = dayKey(for: date)
        var counts = history[key] ?? DailyCounts()
        counts.increment(for: type)
        history[key] = counts

        lifetimeTotals.increment(for: type)

        let hour = calendar.component(.hour, from: date)
        hourlyTotals[hour, default: 0] += 1

        persist()
    }

    func summaries(days: Int, referenceDate: Date = Date()) -> [DaySummary] {
        let clampedDays = max(1, min(Storage.retainedDays, days))
        let startDate = calendar.date(byAdding: .day, value: -(clampedDays - 1), to: startOfDay(for: referenceDate)) ?? startOfDay(for: referenceDate)

        return (0..<clampedDays).compactMap { offset in
            guard let date = calendar.date(byAdding: .day, value: offset, to: startDate) else {
                return nil
            }
            let counts = history[dayKey(for: date)] ?? DailyCounts()
            return DaySummary(date: date, counts: counts)
        }
    }

    func totalCount(for type: CaptureType, days: Int, referenceDate: Date = Date()) -> Int {
        summaries(days: days, referenceDate: referenceDate)
            .reduce(0) { partialResult, summary in
                partialResult + summary.counts.count(for: type)
            }
    }

    func lifetimeTotal(for type: CaptureType) -> Int {
        lifetimeTotals.count(for: type)
    }

    /// Aggregate totals by weekday (all capture types combined) across the given day range,
    /// drawn from the retained (up to 30-day) daily history.
    func weekdayTotals(days: Int, referenceDate: Date = Date()) -> [WeekdayTotal] {
        let dailySummaries = summaries(days: days, referenceDate: referenceDate)
        var totalsByWeekday: [Int: Int] = [:]
        for summary in dailySummaries {
            let weekday = calendar.component(.weekday, from: summary.date)
            totalsByWeekday[weekday, default: 0] += summary.counts.totalCount
        }
        return (1...7).map { weekday in
            WeekdayTotal(weekday: weekday, count: totalsByWeekday[weekday] ?? 0)
        }
    }

    /// The single busiest weekday across the given day range, or `nil` if there is no data yet.
    func busiestWeekday(days: Int, referenceDate: Date = Date()) -> WeekdayTotal? {
        let totals = weekdayTotals(days: days, referenceDate: referenceDate)
        guard let busiest = totals.max(by: { $0.count < $1.count }), busiest.count > 0 else {
            return nil
        }
        return busiest
    }

    /// All-time (lifetime, never pruned) totals for each hour of the day, 0...23.
    func hourlyBreakdown() -> [HourTotal] {
        (0..<24).map { hour in
            HourTotal(hour: hour, count: hourlyTotals[hour] ?? 0)
        }
    }

    /// The single most active hour of the day, all-time, or `nil` if there is no data yet.
    func mostActiveHour() -> HourTotal? {
        let breakdown = hourlyBreakdown()
        guard let busiest = breakdown.max(by: { $0.count < $1.count }), busiest.count > 0 else {
            return nil
        }
        return busiest
    }

    func clear() {
        history.removeAll()
        lifetimeTotals = DailyCounts()
        hourlyTotals.removeAll()
        userDefaults.removeObject(forKey: Storage.key)
        userDefaults.removeObject(forKey: Storage.lifetimeKey)
        userDefaults.removeObject(forKey: Storage.hourlyKey)
    }

    private func pruneAndPersistIfNeeded() {
        if pruneIfNeeded(referenceDate: Date()) {
            persist()
        }
    }

    @discardableResult
    private func pruneIfNeeded(referenceDate: Date) -> Bool {
        let earliestDay = calendar.date(byAdding: .day, value: -(Storage.retainedDays - 1), to: startOfDay(for: referenceDate)) ?? startOfDay(for: referenceDate)
        let previousCount = history.count
        history = history.filter { key, _ in
            guard let date = formatter.date(from: key) else {
                return false
            }
            return startOfDay(for: date) >= earliestDay
        }
        return history.count != previousCount
    }

    private func persist() {
        if let data = try? encoder.encode(history) {
            userDefaults.set(data, forKey: Storage.key)
        }
        if let data = try? encoder.encode(lifetimeTotals) {
            userDefaults.set(data, forKey: Storage.lifetimeKey)
        }
        if let data = try? encoder.encode(hourlyTotals) {
            userDefaults.set(data, forKey: Storage.hourlyKey)
        }
    }

    private func startOfDay(for date: Date) -> Date {
        calendar.startOfDay(for: date)
    }

    private func dayKey(for date: Date) -> String {
        formatter.string(from: startOfDay(for: date))
    }
}
