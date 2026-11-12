
#if APPSTORE
import SwiftUI

struct ProSettingsSection: View {
    @ObservedObject private var storeService = StoreService.shared

    var body: some View {
        if storeService.hasProTip {
            SettingsFormSection("Supporter", icon: "star.fill") {
                LabeledContent {
                    Label("Supporter", systemImage: "star.fill")
                        .foregroundStyle(.orange)
                } label: {
                    Text("Thank you for supporting independent development!")
                    if let plan = storeService.activeProPlan {
                        Text("Plan: \(plan.label)")
                    }
                }

                SettingsActionRow(title: "Subscription") {
                    Button("Manage Subscription") {
                        storeService.manageSubscriptions()
                    }

                    Button("Restore Purchases") {
                        Task { await storeService.restore() }
                    }
                    .disabled(storeService.isPurchasing)
                }
            }

        } else {
            ProSubscriptionView()
        }
    }
}
#endif
