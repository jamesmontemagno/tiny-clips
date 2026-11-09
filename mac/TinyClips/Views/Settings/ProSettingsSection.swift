
#if APPSTORE
import SwiftUI

struct ProSettingsSection: View {
    @ObservedObject private var storeService = StoreService.shared

    var body: some View {
        if storeService.hasProTip {
            Section {
                HStack {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Supporter")
                            .font(.headline)
                        if let plan = storeService.activeProPlan {
                            Text("Plan: \(plan.label) — thank you for your support!")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        } else {
                            Text("Thank you for supporting independent development!")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                    }
                    Spacer()
                    Label("Supporter", systemImage: "star.fill")
                        .foregroundStyle(.orange)
                        .font(.callout)
                }

                HStack(spacing: 10) {
                    Button("Manage Subscription") {
                        storeService.manageSubscriptions()
                    }
                    .buttonStyle(.bordered)

                    Button("Restore Purchases") {
                        Task { await storeService.restore() }
                    }
                    .buttonStyle(.plain)
                    .disabled(storeService.isPurchasing)
                }
            }

        } else {
            ProSubscriptionView()
        }
    }
}
#endif
