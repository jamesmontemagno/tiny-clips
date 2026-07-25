---
description: "Use when editing capture-time AppKit windows or floating panels: StopRecordingPanel, StartRecordingPanel, CapturePickerPanel, CountdownWindow, ScreenPickerWindow, RegionIndicatorPanel, VideoTrimmerWindow, GifTrimmerWindow, ScreenshotEditorWindow, OnboardingWizardWindow, GuideWindow."
applyTo: "mac/TinyClips/Views/*Panel.swift, mac/TinyClips/Views/*Window.swift"
---

# Capture-Time Window & Panel Conventions

## Floating Panel Recipe

Floating capture panels (`NSPanel` subclass) use this setup in `init`:

```swift
self.init(
    contentRect: NSRect(x: 0, y: 0, width: ..., height: ...),
    styleMask: [.borderless, .nonactivatingPanel],
    backing: .buffered,
    defer: false
)
self.isReleasedWhenClosed = false
self.level = .floating
self.isOpaque = false
self.backgroundColor = .clear
self.hasShadow = true
self.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
self.isMovableByWindowBackground = true
```

Editor/trimmer windows (`NSWindow` subclass) use titled style instead:
`styleMask: [.titled, .closable, .miniaturizable, .resizable]`

## Callback Pattern with Double-Fire Guard

Every callback-driven window must prevent double invocations:

```swift
class SomeWindow: NSWindow, NSWindowDelegate {
    private var onComplete: ((ResultType?) -> Void)?
    private var didComplete = false

    private func completeWith(_ result: ResultType?) {
        guard !didComplete, let callback = onComplete else { return }
        didComplete = true
        onComplete = nil       // nil BEFORE calling to prevent re-entrancy
        callback(result)
        orderOut(nil)
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        completeWith(nil)      // nil payload = cancelled
        return true
    }
}
```

Rules:
- Set `didComplete = true` and `onComplete = nil` **before** invoking the callback.
- `nil` result payload always means the user cancelled.
- Set `isReleasedWhenClosed = false` — `CaptureManager` owns the lifecycle.
- Use `[weak self]` in all closures passed to hosted SwiftUI views.

## SwiftUI Integration

Host SwiftUI views via `NSHostingView`:

```swift
let hostingView = NSHostingView(rootView: SomeView(
    onAction: { [weak self] value in
        self?.completeWith(value)
    }
))
self.contentView = hostingView
```

Keep SwiftUI views as `private struct` inside the window file.

## macOS Menu Bar Integration

- When an editor or trimmer action maps to a familiar macOS command, expose it in the menu bar as well as the visible toolbar or footer control.
- Use focused SwiftUI `Commands` for `WindowGroup` scenes. For AppKit `NSWindow` hosts, use targetless `NSMenuItem` actions resolved through the key window's responder chain and validate items against the active window's available actions.
- Keep export commands explicit when a flow has multiple outputs: use titles such as `Save Frame`, `Save Trimmed`, or `Save All Frames` instead of an ambiguous generic `Save`.
- Keep menu commands scoped to the active editor or trimmer and disabled when that action is unavailable.

## Keyboard Interactivity (Picker Panels)

Panels that need keyboard input must:
- Override `var canBecomeKey: Bool { true }`
- Call `NSApp.activate()` after `makeKeyAndOrderFront`
- Install local + global event monitors (`NSEvent.addLocalMonitorForEvents` / `addGlobalMonitorForEvents`)
- Remove monitors in the completion/cancel path

## Lifecycle

- `CaptureManager` holds strong refs to capture-time windows.
- Defer `nil` releases with `DispatchQueue.main.async` — avoids deallocation mid-callback.
- Persist floating panel positions on dismiss and restore on reopen.
- Editor/trimmer windows open **after** recording resources are fully released.
