import SwiftUI
import os
import AppKit
#if APPSTORE
import StoreKit
#endif

// MARK: - Settings Window Manager

class SettingsWindowManager: ObservableObject {
    static let shared = SettingsWindowManager()
    @Published var selectedTab: SettingsTab? = nil
    @Published var showQuickBugReportForm = false
    
    private init() {}
}

// MARK: - Menu Bar Content

struct MenuBarContentView: View {
    @ObservedObject var captureManager: CaptureManager
    @ObservedObject var sparkleController: SparkleController
    @ObservedObject private var settings = CaptureSettings.shared
    @ObservedObject private var recentCaptures = RecentCaptureStore.shared
    @Environment(\.openWindow) private var openWindow
#if APPSTORE
    @Environment(\.requestReview) private var requestReview
    @AppStorage("appStoreClipCountForReview") private var appStoreClipCountForReview = 0
    @AppStorage("appStoreReviewRequested") private var appStoreReviewRequested = false
#endif

    var body: some View {
        if !captureManager.isRecording {
            Button {
                captureManager.takeScreenshot()
            } label: {
                Label("Screenshot…", systemImage: "camera")
            }
            .keyboardShortcut(screenshotKey, modifiers: screenshotModifiers)
            .accessibilityHint("Starts screenshot capture.")

            Button {
                captureManager.startVideoRecording()
            } label: {
                Label("Record Video...", systemImage: "video")
            }
            .keyboardShortcut(videoKey, modifiers: videoModifiers)
            .accessibilityHint("Starts video recording.")

            Button {
                captureManager.startGifRecording()
            } label: {
                Label("Record GIF...", systemImage: "photo.on.rectangle")
            }
            .keyboardShortcut(gifKey, modifiers: gifModifiers)
            .accessibilityHint("Starts GIF recording.")

            Divider()
        } else {
            Button {
                captureManager.stopRecording()
            } label: {
                Label("Stop Recording", systemImage: "stop.circle")
            }
            .keyboardShortcut(".", modifiers: .command)
            .accessibilityHint("Stops the current recording.")

            Divider()
        }

        folderActions

        if !recentCaptures.items.isEmpty {
            Menu {
                ForEach(recentCaptures.items) { item in
                    Button {
                        captureManager.openRecentCapture(item)
                    } label: {
                        Label(recentCaptureTitle(item), systemImage: recentCaptureIcon(item.type))
                    }
                    .accessibilityHint("Opens this \(item.type.label.lowercased()) in its editor.")
                }
            } label: {
                Label("Recent Captures", systemImage: "clock.arrow.circlepath")
            }
        }

        Divider()
#if !APPSTORE
        Button {
            SettingsWindowManager.shared.selectedTab = .about
            // Open Settings first so Sparkle has a parent window for its update dialog.
            // Without a key window (which doesn't exist after the menu bar menu closes),
            // Sparkle cannot present its UI and shows "Update failed" instead.
            openWindow(id: "settings-window")
            bringSettingsWindowToFront()
            checkForUpdatesAfterSettingsWindowAppears()
        } label: {
            Label("Check for Updates\u{2026}", systemImage: "arrow.trianglehead.clockwise")
        }
#endif
        Button {
            openWindow(id: "clips-manager")
            bringClipsManagerWindowToFront()
        } label: {
            Label("Clips Manager…", systemImage: "film.stack")
        }

#if APPSTORE
        if appStoreClipCountForReview >= 25 && !appStoreReviewRequested {
            Button {
                appStoreReviewRequested = true
                requestReview()
            } label: {
                Label("Rate TinyClips…", systemImage: "star.bubble")
            }
        }
#endif

        Button {
            captureManager.showGuide()
        } label: {
            Label("Guide…", systemImage: "book")
        }

        Button {
            SettingsWindowManager.shared.selectedTab = .about
            SettingsWindowManager.shared.showQuickBugReportForm = true
            openWindow(id: "settings-window")
            bringSettingsWindowToFront()
        } label: {
            Label("File a Bug…", systemImage: "ladybug")
        }

#if APPSTORE
        Button {
            SettingsWindowManager.shared.selectedTab = .pro
            openWindow(id: "settings-window")
            bringSettingsWindowToFront()
        } label: {
            Label("Support Tiny Clips…", systemImage: "heart")
        }
        .accessibilityHint("Opens support and tip options.")
#endif

        Button {
            PerformanceSignposts.beginSettingsOpen()
            openWindow(id: "settings-window")
            bringSettingsWindowToFront()
        } label: {
            Label("Settings…", systemImage: "gearshape")
        }
        .keyboardShortcut(",", modifiers: .command)
        .accessibilityHint("Opens TinyClips settings.")

        Divider()

        Button {
            NSApplication.shared.terminate(nil)
        } label: {
            Label("Quit", systemImage: "power")
        }
        .keyboardShortcut("q", modifiers: .command)
        .onAppear {
            recentCaptures.pruneMissing()
        }
    }

    @ViewBuilder
    private var folderActions: some View {
        let types: [CaptureType] = [.screenshot, .video, .gif]
        let directories = types.map { SaveService.shared.outputDirectoryURL(for: $0) }
        if Set(directories.map { $0.standardizedFileURL.path }).count == 1,
           let directory = directories.first {
            Button {
                openDirectory(directory)
            } label: {
                Label("Open Save Folder", systemImage: "folder")
            }
            .accessibilityHint("Opens the folder where captures are saved.")
        } else {
            Menu {
                ForEach(types, id: \.rawValue) { type in
                    Button("Open \(type.label) Folder") {
                        openDirectory(SaveService.shared.outputDirectoryURL(for: type))
                    }
                }
            } label: {
                Label("Open Capture Folder", systemImage: "folder")
            }
        }
    }

    private func openDirectory(_ directory: URL) {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        NSWorkspace.shared.open(directory)
    }

    private func recentCaptureTitle(_ item: RecentCaptureItem) -> String {
        "\(item.url.lastPathComponent) — \(item.type.label), \(item.capturedAt.formatted(date: .abbreviated, time: .shortened))"
    }

    private func recentCaptureIcon(_ type: CaptureType) -> String {
        switch type {
        case .screenshot: "photo"
        case .video: "video"
        case .gif: "photo.stack"
        }
    }

    private func checkForUpdatesAfterSettingsWindowAppears() {
        // Poll until the Settings window becomes key (or a 2 s timeout elapses) so
        // Sparkle has a valid parent window for its update dialog. A menu bar app has
        // no key window after the menu closes, which causes Sparkle to show "Update
        // failed" when checkForUpdates is called immediately.
        let start = Date()
        let timeout: TimeInterval = 2.0
        func tryCheck() {
            let settingsIsKey = NSApp.keyWindow.map { $0.identifier?.rawValue == "settings-window" || $0.title == "Tiny Clips Settings" } ?? false
            if settingsIsKey || Date().timeIntervalSince(start) >= timeout {
                sparkleController.checkForUpdates()
            } else {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { tryCheck() }
            }
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { tryCheck() }
    }

    private func bringSettingsWindowToFront() {
        DispatchQueue.main.async {
            NSRunningApplication.current.activate(options: [.activateAllWindows])
            if let settingsWindow = NSApp.windows.first(where: { $0.identifier?.rawValue == "settings-window" || $0.title == "Tiny Clips Settings" }) {
                settingsWindow.makeKeyAndOrderFront(nil)
                settingsWindow.orderFrontRegardless()
            }
        }

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
            NSRunningApplication.current.activate(options: [.activateAllWindows])
            if let settingsWindow = NSApp.windows.first(where: { $0.identifier?.rawValue == "settings-window" || $0.title == "Tiny Clips Settings" }) {
                settingsWindow.makeKeyAndOrderFront(nil)
                settingsWindow.orderFrontRegardless()
            }
        }
    }

    private func bringClipsManagerWindowToFront() {
        DispatchQueue.main.async {
            NSRunningApplication.current.activate(options: [.activateAllWindows])
            if let clipsWindow = NSApp.windows.first(where: { $0.identifier?.rawValue == "clips-manager" || $0.title == "Clips Manager" }) {
                clipsWindow.makeKeyAndOrderFront(nil)
                clipsWindow.orderFrontRegardless()
            }
        }

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
            NSRunningApplication.current.activate(options: [.activateAllWindows])
            if let clipsWindow = NSApp.windows.first(where: { $0.identifier?.rawValue == "clips-manager" || $0.title == "Clips Manager" }) {
                clipsWindow.makeKeyAndOrderFront(nil)
                clipsWindow.orderFrontRegardless()
            }
        }
    }

    // MARK: - Dynamic Shortcut Keys

    private var screenshotKey: KeyEquivalent {
        keyEquivalent(for: settings.screenshotHotKeyCode, fallback: "5")
    }

    private var screenshotModifiers: EventModifiers {
        HotKeyBinding(keyCode: settings.screenshotHotKeyCode, carbonModifiers: settings.screenshotHotKeyModifiers).swiftUIModifiers
    }

    private var videoKey: KeyEquivalent {
        keyEquivalent(for: settings.videoHotKeyCode, fallback: "6")
    }

    private var videoModifiers: EventModifiers {
        HotKeyBinding(keyCode: settings.videoHotKeyCode, carbonModifiers: settings.videoHotKeyModifiers).swiftUIModifiers
    }

    private var gifKey: KeyEquivalent {
        keyEquivalent(for: settings.gifHotKeyCode, fallback: "7")
    }

    private var gifModifiers: EventModifiers {
        HotKeyBinding(keyCode: settings.gifHotKeyCode, carbonModifiers: settings.gifHotKeyModifiers).swiftUIModifiers
    }

    private func keyEquivalent(for keyCode: Int, fallback: Character) -> KeyEquivalent {
        // Only use UCKeyTranslate result when it produces a single letter or digit —
        // this avoids passing multi-char strings (e.g. "Space", "Esc") or symbol
        // characters (e.g. "←") to SwiftUI's KeyEquivalent.
        guard let str = HotKeyBinding.keyCodeToDisplayString(keyCode),
              str.count == 1,
              let ch = str.lowercased().first,
              ch.isLetter || ch.isNumber else {
            return KeyEquivalent(fallback)
        }
        return KeyEquivalent(ch)
    }

}

// MARK: - Menu Bar Label

struct MenuBarLabelView: View {
    @ObservedObject var captureManager: CaptureManager
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Image(systemName: captureManager.isRecording ? "record.circle.fill" : "camera.viewfinder")
            .foregroundStyle(captureManager.isRecording ? .red : .primary)
            .onAppear {
                ScreenshotEditorRegistry.shared.installOpener { sessionID in
                    openWindow(id: ScreenshotEditorRegistry.windowID, value: sessionID)
                }
            }
    }
}

// MARK: - Performance Signposts

enum PerformanceSignposts {
    private static let signposter = OSSignposter(subsystem: Bundle.main.bundleIdentifier ?? "com.tinyclips.app", category: "Performance")
    private static let stateQueue = DispatchQueue(label: "com.tinyclips.performance-signposts")
    private static var pendingSettingsOpenState: OSSignpostIntervalState?

    static func beginSettingsOpen() {
        let state = signposter.beginInterval("SettingsOpen")
        stateQueue.sync {
            pendingSettingsOpenState = state
        }
    }

    static func endSettingsOpenIfNeeded() {
        let state: OSSignpostIntervalState? = stateQueue.sync {
            defer { pendingSettingsOpenState = nil }
            return pendingSettingsOpenState
        }

        guard let state else { return }
        signposter.endInterval("SettingsOpen", state)
    }

    static func markVideoTabOpened() {
        signposter.emitEvent("VideoSettingsTabOpened")
    }

    static func measureMicrophoneEnumeration<T>(_ operation: () -> T) -> T {
        let state = signposter.beginInterval("MicrophoneEnumeration")
        defer { signposter.endInterval("MicrophoneEnumeration", state) }
        return operation()
    }
}
