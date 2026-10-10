---
description: "Use when editing Mac App Store StoreKit or subscription code: StoreService, ProPlan, SubscriptionView, Pro-gating, in-app purchases."
applyTo: "mac/TinyClips/Services/StoreService.swift, mac/TinyClips/Views/SubscriptionView.swift"
---

# Mac App Store & StoreKit Conventions

## `#if APPSTORE` Guard

These files are **entirely** wrapped in `#if APPSTORE ... #endif`. All new code must stay inside that guard. Never add code outside it — the direct-distribution target must compile with these files empty.

**Never** import or reference Sparkle in these files. Sparkle is only for the direct-distribution target (`#if canImport(Sparkle)`).

## StoreKit 2 Patterns

- Use **StoreKit 2** APIs only (`Product`, `Transaction`, `VerificationResult`, `AppStore.sync()`). No legacy `SKPaymentQueue` or StoreKit 1.
- `StoreService` is a `@MainActor` singleton (`StoreService.shared`) using `ObservableObject` / `@Published`.
- Transaction verification: always call `checkVerified(_:)` and handle `.unverified` as an error.
- Listen for `Transaction.updates` in a detached `Task` — cancel in `deinit`.
- Iterate `Transaction.currentEntitlements` for entitlement checks; verify `revocationDate == nil`.

## ProPlan Enum

`ProPlan` enumerates all plan tiers: `.monthly`, `.yearly`, `.lifetime`. Each case's `rawValue` is its App Store product identifier. Add new tiers here if needed — keep in sync with App Store Connect.

## Build and Warning Checks

Any change in a Mac App Store or StoreKit-related file must compile the app for both direct and MAS targets before completion.

Run at least:

```bash
xcodebuild build -project mac/TinyClips.xcodeproj -scheme TinyClips -configuration Debug \
  CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO
xcodebuild build -project mac/TinyClips.xcodeproj -scheme TinyClipsMAS -configuration Debug \
  CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO
```

Rules:
- Check the build output for warnings and fix them before finishing the change.
- Swift 6 concurrency issues such as `Sendability`, capture-list errors, and `self` ownership warnings are not optional cleanup; resolve them in the same patch.
- If the environment blocks the build, rerun unsandboxed before concluding the code is ready.

## Pro-Gating

- Gate Pro features on `StoreService.shared.isPro` (a `@Published Bool`).
- In non-StoreKit files, wrap Pro UI with `#if APPSTORE` and check `isPro` at runtime.
- Never expose Pro-only UI without a fallback or paywall for non-Pro users.

## SubscriptionView Patterns

- Use `@ObservedObject` (not `@StateObject`) when referencing the shared `StoreService.shared` singleton.
- Keep helper views (e.g., `PlanCard`) as `private struct` inside the file.
- Purchase and restore buttons must disable during `isPurchasing`.
- Display `purchaseError` when non-nil; clear on new actions.
- Include legal links (Privacy Policy / Terms of Use) below the paywall.
