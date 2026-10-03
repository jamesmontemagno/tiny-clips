import Foundation

// MARK: - Cleanup

struct StudioCleanupOptions: Equatable, Sendable {
    var retentionDays: Int
    var sizeCapBytes: Int64

    init(retentionDays: Int = 30, sizeCapBytes: Int64 = 10 * 1_024 * 1_024 * 1_024) {
        self.retentionDays = retentionDays
        self.sizeCapBytes = sizeCapBytes
    }
}

enum StudioCleanupPolicy {
    static func plan(
        summaries: [StudioProjectSummary],
        currentDate: Date,
        options: StudioCleanupOptions = StudioCleanupOptions()
    ) -> [String] {
        var ids: [String] = []
        var deleted = Set<String>()

        for summary in summaries where summary.isFlat && !summary.sourceExists {
            ids.append(summary.id)
            deleted.insert(summary.id)
        }

        if options.retentionDays > 0 {
            let cutoff = currentDate.addingTimeInterval(-Double(options.retentionDays) * 24 * 60 * 60)
            for summary in summaries where !deleted.contains(summary.id) {
                if isEligible(summary), summary.lastOpenedAt < cutoff {
                    ids.append(summary.id)
                    deleted.insert(summary.id)
                }
            }
        }

        if options.sizeCapBytes > 0 {
            var totalBytes = summaries.reduce(Int64(0)) { $0 + max(0, $1.sizeOnDisk) }
            let candidates = summaries
                .filter { !deleted.contains($0.id) && isEligible($0) }
                .sorted {
                    if $0.lastOpenedAt == $1.lastOpenedAt { return $0.id < $1.id }
                    return $0.lastOpenedAt < $1.lastOpenedAt
                }
            for summary in candidates where totalBytes > options.sizeCapBytes {
                ids.append(summary.id)
                deleted.insert(summary.id)
                totalBytes -= max(0, summary.sizeOnDisk)
            }
        }

        return ids
    }

    private static func isEligible(_ summary: StudioProjectSummary) -> Bool {
        !summary.isDraft && !summary.keepSources && !summary.isFlat
    }
}
