import AppKit
import Darwin
import ImageIO
import SwiftUI
import UniformTypeIdentifiers

enum TinyClipsRuntime {
    static var isRunningUnitTests: Bool {
        ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil
    }
}

enum TinyClipsActivationPolicy {
    private static var appKitPresentationWindows: Set<ObjectIdentifier> = []

    static func resolve(
        showInDock: Bool,
        hasOpenEditors: Bool,
        hasOpenUserWindows: Bool = false
    ) -> NSApplication.ActivationPolicy {
        showInDock || hasOpenEditors || hasOpenUserWindows ? .regular : .accessory
    }

    /// Whether a screenshot editor or Studio window is open. While one is, Tiny Clips shows its
    /// Dock icon and menu bar so the window can be found again and its menus can be used.
    @MainActor
    static var hasOpenEditors: Bool {
        ScreenshotEditorRegistry.shared.hasOpenSessions || StudioWindowRegistry.shared.hasOpenWindows
    }

    @MainActor
    private static var hasOpenUserWindows: Bool {
        !appKitPresentationWindows.isEmpty
    }

    @MainActor
    static func beginPresentation(of window: NSWindow) {
        appKitPresentationWindows.insert(ObjectIdentifier(window))
        applyCurrent()
    }

    @MainActor
    static func endPresentation(of window: NSWindow) {
        appKitPresentationWindows.remove(ObjectIdentifier(window))
        applyCurrent()
    }

    @MainActor
    static func applyCurrent() {
        let application = NSApplication.shared
        let policy = resolve(
            showInDock: CaptureSettings.shared.showInDock,
            hasOpenEditors: hasOpenEditors,
            hasOpenUserWindows: hasOpenUserWindows
        )
        let changed = apply(policy, current: application.activationPolicy()) { requested in
#if DEBUG
            NSLog("Tiny Clips activation policy: \(application.activationPolicy().rawValue) -> \(requested.rawValue), editors=\(hasOpenEditors), userWindows=\(appKitPresentationWindows.count)")
#endif
            return application.setActivationPolicy(requested)
        }
        if !changed {
            NSLog("Could not change Tiny Clips activation policy to \(policy.rawValue).")
        }
    }

    static func apply(
        _ policy: NSApplication.ActivationPolicy,
        current: NSApplication.ActivationPolicy,
        change: (NSApplication.ActivationPolicy) -> Bool
    ) -> Bool {
        current == policy || change(policy)
    }
}

enum TinyClipsTerminationPolicy {
    static func resolve(explicitQuitRequested: Bool) -> NSApplication.TerminateReply {
        explicitQuitRequested ? .terminateNow : .terminateCancel
    }
}

final class TinyClipsAppDelegate: NSObject, NSApplicationDelegate {
    private static var permitsFullTermination = false

    @MainActor
    static func requestFullTermination() {
        permitsFullTermination = true
        NSApplication.shared.terminate(nil)
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        let reply = TinyClipsTerminationPolicy.resolve(
            explicitQuitRequested: Self.permitsFullTermination
        )
        guard reply == .terminateCancel else { return reply }

        if let keyWindow = sender.keyWindow, keyWindow.isVisible {
            keyWindow.performClose(nil)
        }
        return .terminateCancel
    }

    func application(_ application: NSApplication, open urls: [URL]) {
        open(urls)
    }

    func application(_ sender: NSApplication, openFile filename: String) -> Bool {
        open([URL(fileURLWithPath: filename)])
    }

    /// Studio opens project files and supported videos; images still open in the screenshot editor.
    @discardableResult
    private func open(_ urls: [URL]) -> Bool {
        var imageCandidates: [URL] = []
        var handled = false
        for url in urls {
            // The folder of a saved project, dropped on the app, opens as its file does.
            if url.isFileURL && (url.pathExtension.lowercased() == StudioProjectStore.projectFileExtension
                                 || StudioProjectStore.shared.isSavedProjectFolder(url)) {
                ExternalImageOpenCoordinator.shared.beginClipsManagerSuppressionWindow()
                MainActor.assumeIsolated {
                    StudioWindowRegistry.shared.openProjectFile(at: url)
                }
                handled = true
            } else if StudioVideoFile.supports(url) {
                ExternalImageOpenCoordinator.shared.beginClipsManagerSuppressionWindow()
#if APPSTORE
                let hasAccess = url.startAccessingSecurityScopedResource()
#endif
                Task { @MainActor in
#if APPSTORE
                    defer { if hasAccess { url.stopAccessingSecurityScopedResource() } }
#endif
                    await StudioWindowRegistry.shared.openVideo(at: url)
                }
                handled = true
            } else {
                imageCandidates.append(url)
            }
        }
        let openedImages = ExternalImageOpenCoordinator.shared.handleOpen(urls: imageCandidates)
        return handled || openedImages
    }

    func applicationWillTerminate(_ notification: Notification) {
        // Quitting closes no window, so an editor's last edit would otherwise be lost.
        MainActor.assumeIsolated {
            StudioWindowRegistry.shared.saveAllBeforeQuitting()
            AutomationController.shared.shutdown()
        }
        SingleInstanceCoordinator.shared.release()
    }
}

struct AppWindowCommands: Commands {
    var body: some Commands {
        CommandGroup(replacing: .newItem) {}
        CommandGroup(replacing: .appTermination) {
            Button("Close Window") {
                NSApp.keyWindow?.performClose(nil)
            }
            .keyboardShortcut("q", modifiers: .command)
        }
    }
}

@main
struct TinyClipsApp: App {
    @NSApplicationDelegateAdaptor(TinyClipsAppDelegate.self) private var appDelegate
    @StateObject private var captureManager: CaptureManager
    @ObservedObject private var sparkleController = SparkleController.shared
    @ObservedObject private var singleInstanceCoordinator = SingleInstanceCoordinator.shared

    init() {
        if TinyClipsRuntime.isRunningUnitTests {
            _captureManager = StateObject(wrappedValue: CaptureManager())
            return
        }

        switch SingleInstanceCoordinator.shared.acquire() {
        case .primary:
            _ = try? TinyClipsTemporaryFiles.removeStaleFiles(
                olderThan: Date().addingTimeInterval(-24 * 60 * 60)
            )
            _ = SparkleController.shared
            NSApplication.shared.setActivationPolicy(
                TinyClipsActivationPolicy.resolve(
                    showInDock: CaptureSettings.shared.showInDock,
                    hasOpenEditors: false
                )
            )
        case .alreadyRunning:
            exit(EXIT_SUCCESS)
        case let .failure(error):
            NSAlert(error: error).runModal()
            exit(EXIT_FAILURE)
        }

        let captureManager = CaptureManager()
        AutomationController.shared.activate(captureManager: captureManager)
        _captureManager = StateObject(wrappedValue: captureManager)
    }

    var body: some Scene {
        MenuBarExtra {
            MenuBarContentView(captureManager: captureManager, sparkleController: sparkleController)
                .labelStyle(.titleAndIcon)
        } label: {
            MenuBarLabelView(captureManager: captureManager)
                .background(
                    SingleInstanceActivationHandler(coordinator: singleInstanceCoordinator)
                )
        }

        Window("Clips Library", id: "clips-manager") {
            clipsManagerRootView()
        }
        // Sidebar (~210) + two 220 pt grid columns + inspector (~320) fit comfortably at this size.
        .defaultSize(width: 1180, height: 680)
        .commands {
            AppWindowCommands()
        }

        Window("Tiny Clips Settings", id: "settings-window") {
            SettingsView(captureManager: captureManager)
        }
        .defaultSize(width: 720, height: 460)
        .commands {
            AppWindowCommands()
        }

        ScreenshotEditorScene()
    }
}

private struct SingleInstanceActivationHandler: View {
    @ObservedObject var coordinator: SingleInstanceCoordinator
    @Environment(\.openWindow) private var openWindow
    @State private var handledActivationRequestID: UInt = 0

    var body: some View {
        Color.clear
            .frame(width: 0, height: 0)
            .onAppear {
                handleActivationRequestIfNeeded()
            }
            .onChange(of: coordinator.activationRequestID) {
                handleActivationRequestIfNeeded()
            }
    }

    private func handleActivationRequestIfNeeded() {
        guard handledActivationRequestID != coordinator.activationRequestID else { return }

        handledActivationRequestID = coordinator.activationRequestID
        openWindow(id: "settings-window")
        coordinator.bringSettingsWindowToFront()
    }
}

private final class ExternalImageOpenCoordinator {
    static let shared = ExternalImageOpenCoordinator()

    private var suppressClipsManagerUntil: Date = .distantPast

    private init() {
        NotificationCenter.default.addObserver(
            forName: NSWindow.didBecomeMainNotification,
            object: nil,
            queue: .main
        ) { [weak self] note in
            guard let self,
                  self.isSuppressingClipsManager,
                  let window = note.object as? NSWindow,
                  self.isClipsManagerWindow(window) else { return }
            window.close()
        }
        NotificationCenter.default.addObserver(
            forName: NSWindow.didBecomeKeyNotification,
            object: nil,
            queue: .main
        ) { [weak self] note in
            guard let self,
                  self.isSuppressingClipsManager,
                  let window = note.object as? NSWindow,
                  self.isClipsManagerWindow(window) else { return }
            window.close()
        }
    }

    private let alwaysSupportedExtensions: Set<String> = ["png", "jpg", "jpeg"]
    private let webpTypeIdentifier = "org.webmproject.webp"

    private var decodableTypeIdentifiers: Set<String> {
        Set((CGImageSourceCopyTypeIdentifiers() as? [String]) ?? [])
    }

    private var supportsHEIC: Bool {
        decodableTypeIdentifiers.contains(UTType.heic.identifier)
    }

    private var supportsWebP: Bool {
        decodableTypeIdentifiers.contains(webpTypeIdentifier)
    }

    private var supportedExtensionsDescription: String {
        var values = ["PNG", "JPG", "JPEG"]
        if supportsHEIC {
            values.append("HEIC")
        }
        if supportsWebP {
            values.append("WebP")
        }
        return values.joined(separator: ", ")
    }

    @discardableResult
    func handleOpen(urls: [URL]) -> Bool {
        var opened = false

        for url in urls where isSupportedExtension(url) {
            opened = true
            beginClipsManagerSuppressionWindow()
            openInScreenshotEditor(url)
        }

        if !opened, let firstURL = urls.first {
            presentError("Unsupported image format for editor: \(firstURL.lastPathComponent). Supported types: \(supportedExtensionsDescription).")
        }

        return opened
    }

    private func isSupportedExtension(_ url: URL) -> Bool {
        guard url.isFileURL else { return false }

        let ext = url.pathExtension.lowercased()
        if alwaysSupportedExtensions.contains(ext) {
            return true
        }

        if ext == "heic" {
            return supportsHEIC
        }

        if ext == "webp" {
            return supportsWebP
        }

        return false
    }

    private func openInScreenshotEditor(_ url: URL) {
        guard NSImage(contentsOf: url) != nil else {
            presentError("Could not open \(url.lastPathComponent). The file may be corrupted or unsupported on this macOS version.")
            return
        }

        DispatchQueue.main.async {
            self.closeClipsManagerWindowsIfNeeded()
            NSApp.activate(ignoringOtherApps: true)
            ScreenshotEditorRegistry.shared.present(imageURL: url) { _ in }
        }
    }

    private var isSuppressingClipsManager: Bool {
        Date() <= suppressClipsManagerUntil
    }

    /// Closes the Clips Library that the system opens for a file handed to the app, now and for
    /// the next moments. The file opens in an editor of its own.
    func beginClipsManagerSuppressionWindow() {
        suppressClipsManagerUntil = Date().addingTimeInterval(3)
        closeClipsManagerWindowsIfNeeded()
    }

    private func isClipsManagerWindow(_ window: NSWindow) -> Bool {
        window.identifier?.rawValue == "clips-manager" || window.title == "Clips Library"
    }

    private func closeClipsManagerWindowsIfNeeded() {
        for window in NSApp.windows where isClipsManagerWindow(window) {
            window.close()
        }
    }

    private func presentError(_ message: String) {
        Task { @MainActor in
            SaveService.shared.showError(message)
        }
    }
}
