import SwiftUI
import Charts

struct AnalyticsSettingsSection: View {
    @ObservedObject var store: CaptureAnalyticsStore
    @State private var selectedRange = 7
    @State private var hiddenTypes: Set<CaptureType> = []
    @State private var hoveredDate: Date?
    @State private var showResetConfirmation = false
    @State private var didCopySummary = false

    private var summaries: [CaptureAnalyticsStore.DaySummary] {
        store.summaries(days: selectedRange)
    }

    private var maxDailyTotal: Int {
        let visibleTotal: (CaptureAnalyticsStore.DailyCounts) -> Int = { counts in
            CaptureType.allCases
                .filter { !hiddenTypes.contains($0) }
                .reduce(0) { $0 + counts.count(for: $1) }
        }
        return max(1, summaries.map { visibleTotal($0.counts) }.max() ?? 0)
    }

    private var hoveredSummary: CaptureAnalyticsStore.DaySummary? {
        guard let hoveredDate else { return nil }
        return summaries.min { lhs, rhs in
            abs(lhs.date.timeIntervalSince(hoveredDate)) < abs(rhs.date.timeIntervalSince(hoveredDate))
        }
    }

    var body: some View {
        Section("Capture Analytics") {
            Picker("Time range", selection: $selectedRange) {
                Text("Last 7 Days").tag(7)
                Text("Last 30 Days").tag(30)
            }
            .pickerStyle(.segmented)

            HStack(spacing: 8) {
                ForEach(CaptureType.allCases, id: \.self) { type in
                    TypeToggleChip(
                        title: type.label,
                        color: type.color,
                        isOn: !hiddenTypes.contains(type),
                        action: { toggleType(type) }
                    )
                }
            }

            hoverDetailText
                .font(.caption)
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)

            Chart(chartEntries) { entry in
                BarMark(
                    x: .value("Day", entry.date, unit: .day),
                    y: .value("Captures", entry.count)
                )
                .foregroundStyle(entry.type.color)
                .accessibilityLabel("\(entry.type.label) on \(entry.date.formatted(date: .abbreviated, time: .omitted))")
                .accessibilityValue("\(entry.count)")
            }
            .chartXAxis {
                AxisMarks(values: .stride(by: .day, count: selectedRange == 7 ? 1 : 5)) { value in
                    AxisGridLine()
                    AxisTick()
                    AxisValueLabel {
                        if let date = value.as(Date.self) {
                            Text(date, format: selectedRange == 7
                                ? .dateTime.weekday(.narrow)
                                : .dateTime.month(.abbreviated).day())
                        }
                    }
                }
            }
            .chartYScale(domain: 0...maxDailyTotal)
            .frame(height: 220)
            .accessibilityLabel("Capture analytics chart")
            .chartOverlay { proxy in
                GeometryReader { geometry in
                    Rectangle()
                        .fill(.clear)
                        .contentShape(Rectangle())
                        .onContinuousHover { phase in
                            switch phase {
                            case .active(let location):
                                guard let plotFrame = proxy.plotFrame else { return }
                                let frame = geometry[plotFrame]
                                let originX = frame.origin.x
                                if let date: Date = proxy.value(atX: location.x - originX) {
                                    hoveredDate = date
                                }
                            case .ended:
                                hoveredDate = nil
                            }
                        }
                }
            }

            HStack(spacing: 12) {
                AnalyticsTotalCard(title: "Screenshots", count: store.totalCount(for: .screenshot, days: selectedRange), color: .blue)
                AnalyticsTotalCard(title: "Videos", count: store.totalCount(for: .video, days: selectedRange), color: .red)
                AnalyticsTotalCard(title: "GIFs", count: store.totalCount(for: .gif, days: selectedRange), color: .green)
            }

            if summaries.allSatisfy({ $0.counts.totalCount == 0 }) {
                Text("No captures yet for this range. Take a screenshot, video, or GIF to start the chart.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else {
                Text("Daily totals are stored locally on this device and roll over automatically after 30 days.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Button("Reset Capture Analytics…", role: .destructive) {
                showResetConfirmation = true
            }
        }
        .confirmationDialog(
            "Reset capture analytics?",
            isPresented: $showResetConfirmation,
            titleVisibility: .visible
        ) {
            Button("Reset", role: .destructive) {
                store.clear()
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This clears all local screenshot, video, and GIF counts — including lifetime totals. This can't be undone.")
        }

        Section("Lifetime Totals") {
            HStack(spacing: 12) {
                AnalyticsTotalCard(title: "Screenshots", count: store.lifetimeTotal(for: .screenshot), color: .blue)
                AnalyticsTotalCard(title: "Videos", count: store.lifetimeTotal(for: .video), color: .red)
                AnalyticsTotalCard(title: "GIFs", count: store.lifetimeTotal(for: .gif), color: .green)
            }
            Text("\(store.lifetimeTotals.totalCount) captures since you started using Tiny Clips.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }

        Section("Insights") {
            VStack(alignment: .leading, spacing: 6) {
                Text("Busiest day (\(selectedRange == 7 ? "last 7 days" : "last 30 days"))")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Text(busiestWeekdayText)
                    .font(.body.weight(.medium))

                Chart(weekdayTotals) { item in
                    BarMark(
                        x: .value("Weekday", item.shortSymbol),
                        y: .value("Captures", item.count)
                    )
                    .foregroundStyle(item.weekday == store.busiestWeekday(days: selectedRange)?.weekday ? Color.accentColor : Color.accentColor.opacity(0.35))
                    .accessibilityLabel(item.fullSymbol)
                    .accessibilityValue("\(item.count) captures")
                }
                .frame(height: 100)
                .accessibilityLabel("Captures by day of week")
            }

            VStack(alignment: .leading, spacing: 6) {
                Text("Most active hour (all-time)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Text(mostActiveHourText)
                    .font(.body.weight(.medium))

                Chart(hourlyTotals) { item in
                    BarMark(
                        x: .value("Hour", item.hour),
                        y: .value("Captures", item.count)
                    )
                    .foregroundStyle(item.hour == store.mostActiveHour()?.hour ? Color.accentColor : Color.accentColor.opacity(0.35))
                    .accessibilityLabel(item.label)
                    .accessibilityValue("\(item.count) captures")
                }
                .chartXAxis {
                    AxisMarks(values: .stride(by: 4)) { value in
                        AxisGridLine()
                        AxisTick()
                        AxisValueLabel {
                            if let hour = value.as(Int.self) {
                                Text(HourTotalDisplay.label(for: hour))
                            }
                        }
                    }
                }
                .frame(height: 100)
                .accessibilityLabel("Captures by hour of day, all-time")
            }
        }

        Section {
            HStack(spacing: 12) {
                Button {
                    copySummaryToClipboard()
                } label: {
                    Label(didCopySummary ? "Copied!" : "Copy Summary", systemImage: didCopySummary ? "checkmark" : "doc.on.doc")
                }

                ShareLink(item: summaryText) {
                    Label("Share…", systemImage: "square.and.arrow.up")
                }
            }
        } header: {
            Text("Share")
        } footer: {
            Text("Share a quick text summary of your capture activity for the selected range.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    // MARK: - Chart data

    private var chartEntries: [AnalyticsChartEntry] {
        summaries.flatMap { summary in
            CaptureType.allCases
                .filter { !hiddenTypes.contains($0) }
                .map { type in
                    AnalyticsChartEntry(date: summary.date, type: type, count: summary.counts.count(for: type))
                }
        }
    }

    private var weekdayTotals: [WeekdayTotalDisplay] {
        store.weekdayTotals(days: selectedRange).map(WeekdayTotalDisplay.init)
    }

    private var hourlyTotals: [HourTotalDisplay] {
        store.hourlyBreakdown().map(HourTotalDisplay.init)
    }

    private var hoverDetailText: Text {
        guard let hoveredSummary else {
            return Text("Hover over the chart for exact daily counts.")
        }
        let counts = hoveredSummary.counts
        let dateText = hoveredSummary.date.formatted(date: .abbreviated, time: .omitted)
        return Text("\(dateText): \(counts.screenshotCount) screenshots, \(counts.videoCount) videos, \(counts.gifCount) GIFs")
    }

    private var busiestWeekdayText: String {
        guard let busiest = store.busiestWeekday(days: selectedRange) else {
            return "No captures yet for this range."
        }
        return "\(WeekdayTotalDisplay(busiest).fullSymbol) · \(busiest.count) capture\(busiest.count == 1 ? "" : "s")"
    }

    private var mostActiveHourText: String {
        guard let busiest = store.mostActiveHour() else {
            return "No captures yet."
        }
        return "\(HourTotalDisplay.label(for: busiest.hour)) · \(busiest.count) capture\(busiest.count == 1 ? "" : "s")"
    }

    private var summaryText: String {
        let screenshotCount = store.totalCount(for: .screenshot, days: selectedRange)
        let videoCount = store.totalCount(for: .video, days: selectedRange)
        let gifCount = store.totalCount(for: .gif, days: selectedRange)
        let rangeLabel = selectedRange == 7 ? "the last 7 days" : "the last 30 days"

        var lines = [
            "📊 Tiny Clips capture activity — \(rangeLabel)",
            "📸 \(screenshotCount) screenshot\(screenshotCount == 1 ? "" : "s")",
            "🎥 \(videoCount) video\(videoCount == 1 ? "" : "s")",
            "🎞️ \(gifCount) GIF\(gifCount == 1 ? "" : "s")",
        ]

        if let busiestDay = store.busiestWeekday(days: selectedRange) {
            lines.append("Busiest day: \(WeekdayTotalDisplay(busiestDay).fullSymbol) (\(busiestDay.count) captures)")
        }
        if let busiestHour = store.mostActiveHour() {
            lines.append("Most active hour (all-time): \(HourTotalDisplay.label(for: busiestHour.hour))")
        }
        lines.append("Lifetime total: \(store.lifetimeTotals.totalCount) captures")

        return lines.joined(separator: "\n")
    }

    // MARK: - Actions

    private func toggleType(_ type: CaptureType) {
        if hiddenTypes.contains(type) {
            hiddenTypes.remove(type)
        } else if hiddenTypes.count < CaptureType.allCases.count - 1 {
            // Keep at least one series visible so the chart never goes fully blank.
            hiddenTypes.insert(type)
        }
    }

    private func copySummaryToClipboard() {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(summaryText, forType: .string)

        didCopySummary = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
            didCopySummary = false
        }
    }
}

// MARK: - Supporting views

private struct TypeToggleChip: View {
    let title: String
    let color: Color
    let isOn: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Label(title, systemImage: isOn ? "checkmark.circle.fill" : "circle")
                .font(.caption)
        }
        .buttonStyle(.plain)
        .padding(.horizontal, 10)
        .padding(.vertical, 5)
        .background(isOn ? color.opacity(0.18) : Color.gray.opacity(0.12), in: Capsule())
        .foregroundStyle(isOn ? color : .secondary)
        .accessibilityAddTraits(isOn ? [.isSelected] : [])
        .accessibilityHint(isOn ? "Activate to hide \(title) from the chart." : "Activate to show \(title) in the chart.")
    }
}

struct AnalyticsTotalCard: View {
    let title: String
    let count: Int
    let color: Color

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Label(title, systemImage: "circle.fill")
                .font(.caption)
                .foregroundStyle(color)
            Text("\(count)")
                .font(.title2.weight(.semibold))
                .monospacedDigit()
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(12)
        .background(color.opacity(0.12), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(title)
        .accessibilityValue("\(count)")
    }
}

// MARK: - Chart entry models

private struct AnalyticsChartEntry: Identifiable {
    let date: Date
    let type: CaptureType
    let count: Int

    var id: String {
        "\(date.timeIntervalSinceReferenceDate)-\(type.rawValue)"
    }
}

private struct WeekdayTotalDisplay: Identifiable {
    let weekday: Int
    let count: Int

    init(_ total: CaptureAnalyticsStore.WeekdayTotal) {
        weekday = total.weekday
        count = total.count
    }

    var id: Int { weekday }

    /// `Calendar` weekday is 1-based (1 = Sunday); `shortWeekdaySymbols` is 0-based.
    var shortSymbol: String {
        let symbols = Calendar.current.shortWeekdaySymbols
        let index = weekday - 1
        guard symbols.indices.contains(index) else { return "" }
        return symbols[index]
    }

    var fullSymbol: String {
        let symbols = Calendar.current.weekdaySymbols
        let index = weekday - 1
        guard symbols.indices.contains(index) else { return "" }
        return symbols[index]
    }
}

private struct HourTotalDisplay: Identifiable {
    let hour: Int
    let count: Int

    init(_ total: CaptureAnalyticsStore.HourTotal) {
        hour = total.hour
        count = total.count
    }

    var id: Int { hour }

    var label: String {
        Self.label(for: hour)
    }

    static func label(for hour: Int) -> String {
        var components = DateComponents()
        components.hour = hour
        components.minute = 0
        guard let date = Calendar.current.date(from: components) else {
            return "\(hour)"
        }
        return date.formatted(.dateTime.hour())
    }
}

// MARK: - CaptureType display helpers

extension CaptureType: CaseIterable {
    public static var allCases: [CaptureType] { [.screenshot, .video, .gif] }
}

extension CaptureType {
    var color: Color {
        switch self {
        case .screenshot:
            return .blue
        case .video:
            return .red
        case .gif:
            return .green
        }
    }
}
