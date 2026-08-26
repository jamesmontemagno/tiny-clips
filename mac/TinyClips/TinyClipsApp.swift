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

final class TinyClipsAppDelegate: NSObject, NSApplicationDelegate {
    func application(_ application: NSApplication, open urls: [URL]) {
        ExternalImageOpenCoordinator.shared.handleOpen(urls: urls)
    }

    func application(_ sender: NSApplication, openFile filename: String) -> Bool {
        ExternalImageOpenCoordinator.shared.handleOpen(urls: [URL(fileURLWithPath: filename)])
    }

    func applicationWillTerminate(_ notification: Notification) {
        SingleInstanceCoordinator.shared.release()
    }
}

struct AppWindowCommands: Commands {
    var body: some Commands {
        CommandGroup(replacing: .newItem) {}
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
            NSApplication.shared.setActivationPolicy(CaptureSettings.shared.showInDock ? .regular : .accessory)
        case .alreadyRunning:
            exit(EXIT_SUCCESS)
        case let .failure(error):
            NSAlert(error: error).runModal()
            exit(EXIT_FAILURE)
        }

        _captureManager = StateObject(wrappedValue: CaptureManager())
    }

    var body: some Scene {
        MenuBarExtra {
            MenuBarContentView(captureManager: captureManager, sparkleController: sparkleController)
        } label: {
            MenuBarLabelView(captureManager: captureManager)
                .background(
                    SingleInstanceActivationHandler(coordinator: singleInstanceCoordinator)
                )
        }

        Window("Clips Manager", id: "clips-manager") {
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
            .onChange(of: coordinator.activationRequestID) { _ in
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

    private func beginClipsManagerSuppressionWindow() {
        suppressClipsManagerUntil = Date().addingTimeInterval(3)
        closeClipsManagerWindowsIfNeeded()
    }

    private func isClipsManagerWindow(_ window: NSWindow) -> Bool {
        window.identifier?.rawValue == "clips-manager" || window.title == "Clips Manager"
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
