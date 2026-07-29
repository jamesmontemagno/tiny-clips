import SwiftUI

#if APPSTORE
import StoreKit

// MARK: - Subscription View

struct ProSubscriptionView: View {
    @ObservedObject private var storeService = StoreService.shared
    @State private var selectedPlan: ProPlan = .yearly

    var body: some View {
        ScrollView {
            VStack(spacing: 24) {
                heroSection
                if storeService.hasProTip {
                    ProActiveView()
                    accountActions
                } else {
                    tipMessage
                    planCards
                    purchaseButton
                    restoreLink
                }
                errorMessage
                if !storeService.hasProTip {
                    legalLinks
                }
            }
            .padding(32)
            .frame(maxWidth: 520)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    // MARK: - Hero

    private var heroSection: some View {
        VStack(spacing: 10) {
            if let appIcon = NSImage(named: "AppIcon") {
                Image(nsImage: appIcon)
                    .resizable()
                    .frame(width: 72, height: 72)
                    .cornerRadius(16)
            }

            Text("Support Tiny Clips")
                .font(.largeTitle.bold())

            Text("Tiny Clips is always free. Consider tipping to support independent development.")
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 380)
        }
    }

    // MARK: - Tip Message

    private var tipMessage: some View {
        VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 8) {
                Label("All Features Included", systemImage: "checkmark.circle.fill")
                    .font(.headline)
                    .foregroundStyle(.green)

                Text("Every feature in Tiny Clips is completely free and available to all users. Your tip simply shows your support and appreciation for the work that goes into keeping Tiny Clips great.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(16)
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
        }
    }

    // MARK: - Plan Cards

    private var planCards: some View {
        HStack(spacing: 12) {
            ForEach(ProPlan.allCases) { plan in
                PlanCard(
                    plan: plan,
                    product: storeService.product(for: plan),
                    isSelected: selectedPlan == plan,
                    monthlyEquivalent: monthlyEquivalent(for: plan)
                )
                .onTapGesture { selectedPlan = plan }
            }
        }
    }

    private func monthlyEquivalent(for plan: ProPlan) -> String? {
        guard plan == .yearly,
              let product = storeService.yearlyProduct else { return nil }
        let monthly = product.price / 12
        return product.priceFormatStyle.format(monthly) + "/mo"
    }

    // MARK: - Purchase Button

    private var purchaseButton: some View {
        VStack(spacing: 8) {
            if storeService.isLoading {
                ProgressView("Loading plans…")
            } else if let product = storeService.product(for: selectedPlan) {
                Button {
                    Task { await storeService.purchase(product) }
                } label: {
                    HStack {
                        if storeService.isPurchasing {
                            ProgressView().controlSize(.small)
                        } else {
                            Text(purchaseButtonTitle(for: product))
                                .font(.headline)
                        }
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 4)
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .disabled(storeService.isPurchasing)
            } else {
                Button("Leave a Tip") {}
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                    .disabled(true)
            }
        }
    }

    private func purchaseButtonTitle(for product: Product) -> String {
        if selectedPlan == .lifetime {
            return "Leave a Tip — \(product.displayPrice)"
        }
        return "Tip — \(product.displayPrice)/\(selectedPlan == .yearly ? "year" : "month")"
    }

    // MARK: - Restore

    private var restoreLink: some View {
        Button {
            Task { await storeService.restore() }
        } label: {
            Text("Restore Purchases")
                .foregroundStyle(.secondary)
        }
        .buttonStyle(.plain)
        .disabled(storeService.isPurchasing)
    }

    private var accountActions: some View {
        VStack(spacing: 10) {
            Button {
                storeService.manageSubscriptions()
            } label: {
                Text("Manage Subscription")
                    .font(.headline)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 4)
            }
            .buttonStyle(.bordered)

            restoreLink
        }
    }

    // MARK: - Legal Links

    private var legalLinks: some View {
        HStack(spacing: 16) {
            if let privacyURL = URL(string: "https://tinyclips.app/privacy.html") {
                Link("Privacy Policy", destination: privacyURL)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .accessibilityHint("Opens Privacy Policy in your browser.")
            }

            Text("·")
                .font(.caption)
                .foregroundStyle(.tertiary)
                .accessibilityHidden(true)

            if let termsURL = URL(string: "https://www.apple.com/legal/internet-services/itunes/dev/stdeula/") {
                Link("Terms of Use", destination: termsURL)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .accessibilityHint("Opens Terms of Use in your browser.")
            }
        }
    }

    // MARK: - Error

    @ViewBuilder
    private var errorMessage: some View {
        if let error = storeService.purchaseError {
            Text(error)
                .foregroundStyle(.red)
                .font(.caption)
                .multilineTextAlignment(.center)
        }
    }
}

// MARK: - Plan Card

private struct PlanCard: View {
    let plan: ProPlan
    let product: Product?
    let isSelected: Bool
    let monthlyEquivalent: String?

    var body: some View {
        VStack(spacing: 8) {
            if let badge = plan.badge {
                Text(badge)
                    .font(.caption2.weight(.bold))
                    .textCase(.uppercase)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 2)
                    .background(isSelected ? Color.accentColor : Color.secondary.opacity(0.3), in: Capsule())
                    .foregroundStyle(isSelected ? .white : .secondary)
            } else {
                Text(" ")
                    .font(.caption2.weight(.bold))
                    .padding(.vertical, 2)
            }

            Text(plan.label)
                .font(.headline)

            if let product {
                Text(product.displayPrice)
                    .font(.title3.weight(.semibold))
                    .foregroundStyle(isSelected ? .primary : .secondary)

                if plan.isSubscription {
                    Text(plan == .yearly ? "per year" : "per month")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                } else {
                    Text("one-time")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                if let monthlyEquivalent {
                    Text(monthlyEquivalent)
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            } else {
                Text("—")
                    .font(.title3)
                    .foregroundStyle(.tertiary)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 16)
        .padding(.horizontal, 8)
        .background(
            RoundedRectangle(cornerRadius: 12)
                .fill(isSelected ? Color.accentColor.opacity(0.1) : Color.clear)
        )
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .stroke(isSelected ? Color.accentColor : Color.secondary.opacity(0.3), lineWidth: isSelected ? 2 : 1)
        )
        .contentShape(Rectangle())
    }
}

// MARK: - Pro Active View

struct ProActiveView: View {
    @ObservedObject private var storeService = StoreService.shared

    var body: some View {
        VStack(spacing: 12) {
            Label("Pro Supporter", systemImage: "star.fill")
                .font(.headline)
                .foregroundStyle(.orange)

            if let plan = storeService.activeProPlan {
                Text("Plan: \(plan.label)")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            } else {
                Text("Thank you for supporting independent development!")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 20)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
    }
}

#endif
