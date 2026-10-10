import Foundation

#if APPSTORE
import AppKit
import StoreKit

// MARK: - Pro Plan

enum ProPlan: String, CaseIterable, Identifiable {
    case monthly = "com.refractored.tinyclips.pro.monthly"
    case yearly = "com.refractored.tinyclips.pro.yearly"
    case lifetime = "com.refractored.tinyclips.pro.lifetime"

    var id: String { rawValue }

    var label: String {
        switch self {
        case .monthly: return "Monthly Tip"
        case .yearly: return "Yearly Tip"
        case .lifetime: return "One-Time Tip"
        }
    }

    var badge: String? {
        switch self {
        case .yearly: return "Amazing"
        case .lifetime: return "Most Generous"
        default: return nil
        }
    }

    var isSubscription: Bool {
        self != .lifetime
    }
}

// MARK: - Store Service

@MainActor
class StoreService: ObservableObject {
    static let shared = StoreService()

    static let allProductIDs: Set<String> = Set(ProPlan.allCases.map(\.rawValue))

    @Published var hasProTip = false
    @Published var activeProPlan: ProPlan?
    @Published var products: [Product] = []
    @Published var isPurchasing = false
    @Published var isLoading = false
    @Published var purchaseError: String?

    private var updateListenerTask: Task<Void, Never>?

    private init() {
        updateListenerTask = listenForTransactions()
        Task {
            await loadProducts()
            await updatePurchaseStatus()
        }
    }

    deinit {
        updateListenerTask?.cancel()
    }

    // MARK: - Product Accessors

    var monthlyProduct: Product? { products.first { $0.id == ProPlan.monthly.rawValue } }
    var yearlyProduct: Product? { products.first { $0.id == ProPlan.yearly.rawValue } }
    var lifetimeProduct: Product? { products.first { $0.id == ProPlan.lifetime.rawValue } }

    func product(for plan: ProPlan) -> Product? {
        products.first { $0.id == plan.rawValue }
    }

    // MARK: - Product Loading

    func loadProducts() async {
        isLoading = true
        defer { isLoading = false }
        do {
            let fetched = try await Product.products(for: Self.allProductIDs)
            // Sort: yearly, monthly, lifetime
            let order: [ProPlan] = [.yearly, .monthly, .lifetime]
            products = fetched.sorted { a, b in
                let aIdx = order.firstIndex(where: { $0.rawValue == a.id }) ?? 99
                let bIdx = order.firstIndex(where: { $0.rawValue == b.id }) ?? 99
                return aIdx < bIdx
            }
        } catch {
            // Product not available or network error — fail silently
        }
    }

    // MARK: - Purchase

    func purchase(_ product: Product) async {
        isPurchasing = true
        purchaseError = nil
        defer { isPurchasing = false }
        do {
            let result = try await product.purchase()
            switch result {
            case .success(let verification):
                let transaction = try Self.checkVerified(verification)
                await updatePurchaseStatus()
                await transaction.finish()
            case .userCancelled, .pending:
                break
            @unknown default:
                break
            }
        } catch {
            purchaseError = error.localizedDescription
        }
    }

    // MARK: - Restore

    func restore() async {
        isPurchasing = true
        purchaseError = nil
        defer { isPurchasing = false }
        do {
            try await AppStore.sync()
            await updatePurchaseStatus()
            if !hasProTip {
                showNoPurchasesRestoredAlert()
            }
        } catch {
            purchaseError = error.localizedDescription
        }
    }

    func manageSubscriptions() {
        purchaseError = nil
        guard let url = URL(string: "https://apps.apple.com/account/subscriptions"),
              NSWorkspace.shared.open(url) else {
            purchaseError = "Could not open subscription management."
            return
        }
    }

    // MARK: - Entitlement Check

    func updatePurchaseStatus() async {
        var foundProTip = false
        var foundPlan: ProPlan?
        do {
            for await result in Transaction.currentEntitlements {
                let transaction = try Self.checkVerified(result)
                if Self.allProductIDs.contains(transaction.productID),
                   transaction.revocationDate == nil {
                    foundProTip = true
                    foundPlan = ProPlan(rawValue: transaction.productID)
                    break
                }
            }
            hasProTip = foundProTip
            activeProPlan = foundPlan
        } catch {
            hasProTip = false
            activeProPlan = nil
            purchaseError = error.localizedDescription
        }
    }

    private func showNoPurchasesRestoredAlert() {
        let alert = NSAlert()
        alert.messageText = "No Purchases Found"
        alert.informativeText = "We couldn't find any Tiny Clips supporter tips to restore for this App Store account. If you tipped with a different Apple Account, sign in with that account and try again."
        alert.alertStyle = .informational
        alert.addButton(withTitle: "OK")
        alert.runModal()
    }

    // MARK: - Helpers

    nonisolated private static func checkVerified<T>(_ result: VerificationResult<T>) throws -> T {
        switch result {
        case .unverified:
            throw StoreServiceError.failedVerification
        case .verified(let safe):
            return safe
        }
    }

    private func listenForTransactions() -> Task<Void, Never> {
        Task.detached { [weak self] in
            for await result in Transaction.updates {
                do {
                    let transaction = try Self.checkVerified(result)
                    await self?.updatePurchaseStatus()
                    await transaction.finish()
                } catch {
                    await MainActor.run { [weak self] in
                        self?.purchaseError = error.localizedDescription
                    }
                }
            }
        }
    }
}

// MARK: - Error

enum StoreServiceError: LocalizedError {
    case failedVerification

    var errorDescription: String? {
        switch self {
        case .failedVerification:
            return "The App Store could not verify this purchase. Please try again."
        }
    }
}

#endif
