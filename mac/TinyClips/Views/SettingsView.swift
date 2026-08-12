import SwiftUI
import AppKit
import AVFoundation
import Combine

enum SettingsTab: String, CaseIterable {
    case general = "General"
    case analytics = "Analytics"
    case screenshot = "Screenshot"
    case video = "Video"
    case teleprompter = "Teleprompter"
    case gif = "GIF"
    case mouseClicks = "Mouse Clicks"
    case branding = "Branding"
    case shortcuts = "Shortcuts"
    case pro = "Support"
    case about = "About"

    var icon: String {
        switch self {
        case .general: return "gearshape"
        case .analytics: return "chart.bar.xaxis"
        case .screenshot: return "camera"
        case .video: return "video"
        case .teleprompter: return "text.alignleft"
        case .gif: return "photo.on.rectangle"
        case .mouseClicks: return "cursorarrow.rays"
        case .branding: return "flag"
        case .shortcuts: return "command"
        case .pro: return "star"
        case .about: return "info.circle"
        }
    }

    static var displayCases: [SettingsTab] {
#if APPSTORE
        return allCases
#else
        return allCases.filter { $0 != .pro }
#endif
    }
}

struct SettingsView: View {
    @ObservedObject var captureManager: CaptureManager
    @ObservedObject private var settings = CaptureSettings.shared
    @ObservedObject private var captureAnalytics = CaptureAnalyticsStore.shared
    @ObservedObject private var sparkleController = SparkleController.shared
    @ObservedObject private var launchAtLogin = LaunchAtLoginManager.shared
    @ObservedObject private var settingsWindowManager = SettingsWindowManager.shared
#if APPSTORE
    @ObservedObject private var storeService = StoreService.shared
#endif
    @Environment(\.openWindow) private var openWindow
    @State private var selectedTab: SettingsTab? = .general
    @State private var splitVisibility: NavigationSplitViewVisibility = .all
    @State private var showDisableDockWarning = false
    @State private var showQuickBugReportForm = false
    @State private var availableMicrophones: [MicrophoneDeviceOption] = []
    @State private var availableWebcams: [WebcamDeviceOption] = []

    var body: some View {
        NavigationSplitView(columnVisibility: $splitVisibility) {
            List(selection: $selectedTab) {
                ForEach(SettingsTab.displayCases, id: \.self) { tab in
                    Label(tab.rawValue, systemImage: tab.icon)
                        .tag(tab as SettingsTab?)
                }
            }
            .listStyle(.sidebar)
            .navigationSplitViewColumnWidth(min: 160, ideal: 180, max: 220)
        } detail: {
            Form {
                switch selectedTab ?? .general {
                case .general:
                    GeneralSettingsSection(
                        settings: settings,
                        launchAtLogin: launchAtLogin,
                        chooseSaveDirectory: chooseSaveDirectory,
                        resetSaveDirectory: resetSaveDirectory,
                        resetAllSettings: resetAllSettings,
                        showInDockBinding: showInDockBinding
                    )
                case .analytics:
                    AnalyticsSettingsSection(store: captureAnalytics)
                case .screenshot:
                    ScreenshotSettingsSection(settings: settings)
                case .video:
                    VideoSettingsSection(
                        settings: settings,
                        availableMicrophones: availableMicrophones,
                        availableWebcams: availableWebcams,
                        selectedTab: $selectedTab
                    )
                case .teleprompter:
                    TeleprompterSettingsSection(settings: settings)
                case .gif:
                    GifSettingsSection(
                        settings: settings,
                        selectedTab: $selectedTab,
                        gifMouseClickToggleBinding: gifMouseClickToggleBinding
                    )
                case .mouseClicks:
                    MouseClicksSettingsSection(settings: settings)
                case .branding:
                    BrandingSettingsSection(settings: settings)
                case .shortcuts:
                    ShortcutsSettingsSection(settings: settings, captureManager: captureManager)
                case .pro:
#if APPSTORE
                    ProSettingsSection()
#endif
                case .about:
                    AboutSettingsSection(
                        sparkleController: sparkleController,
                        reportIssueURL: reportIssueURL,
                        onFileBug: { showQuickBugReportForm = true },
                        appVersion: appVersion,
                        appBuild: appBuild
                    )
                }
            }
            .formStyle(.grouped)
        }
        .navigationSplitViewStyle(.balanced)
        .frame(minWidth: 720, minHeight: 460)
        .sheet(isPresented: $showQuickBugReportForm) {
            QuickBugReportFormView(context: quickBugReportContext) { title, happened in
                let url = QuickBugReportURLBuilder.makeURL(
                    title: title,
                    happened: happened,
                    context: quickBugReportContext
                )
                NSWorkspace.shared.open(url)
            }
        }
        .alert("Hide Dock icon?", isPresented: $showDisableDockWarning) {
            Button("Cancel", role: .cancel) {}
                .help("Keep TinyClips visible in the Dock.")
            Button("Hide Dock Icon", role: .destructive) {
                settings.showInDock = false
                applyDockVisibility(false)
                reopenSettingsWindow()
            }
            .help("Hide TinyClips from the Dock.")
        } message: {
            Text("TinyClips may briefly close the Settings window when switching out of Dock mode.")
        }
        .onAppear {
            PerformanceSignposts.endSettingsOpenIfNeeded()
            refreshCaptureDevicesIfNeeded()
            applyRequestedTabIfNeeded()
        }
        .onReceive(settingsWindowManager.$selectedTab.compactMap { $0 }) { requestedTab in
            selectedTab = requestedTab
            settingsWindowManager.selectedTab = nil
        }
        .onReceive(settingsWindowManager.$showQuickBugReportForm.filter { $0 }) { _ in
            showQuickBugReportForm = true
            settingsWindowManager.showQuickBugReportForm = false
        }
        .onChange(of: selectedTab) { _, newTab in
            if newTab == .video {
                PerformanceSignposts.markVideoTabOpened()
            }
            refreshCaptureDevicesIfNeeded()
        }
        .onReceive(NotificationCenter.default.publisher(for: AVCaptureDevice.wasConnectedNotification)) { _ in
            refreshCaptureDevicesIfNeeded()
        }
        .onReceive(NotificationCenter.default.publisher(for: AVCaptureDevice.wasDisconnectedNotification)) { _ in
            refreshCaptureDevicesIfNeeded()
        }
    }

    // MARK: - Helpers

    private func applyRequestedTabIfNeeded() {
        guard let requestedTab = settingsWindowManager.selectedTab else { return }
        selectedTab = requestedTab
        settingsWindowManager.selectedTab = nil
    }

    private func chooseSaveDirectory(for captureType: CaptureType?) {
        DispatchQueue.main.async {
            let panel = NSOpenPanel()
            panel.canChooseFiles = false
            panel.canChooseDirectories = true
            panel.allowsMultipleSelection = false
            panel.canCreateDirectories = true
#if APPSTORE
            panel.directoryURL = FileManager.default.urls(for: .moviesDirectory, in: .userDomainMask).first
#endif
            guard panel.runModal() == .OK, let url = panel.url else { return }
#if APPSTORE
            do {
                let bookmark = try url.bookmarkData(options: [.withSecurityScope], includingResourceValuesForKeys: nil, relativeTo: nil)
                settings.setSaveDirectoryBookmark(bookmark, displayPath: url.path, for: captureType)
                SaveService.shared.invalidateSaveDirectoryBookmark(for: captureType)
            } catch {
                SaveService.shared.showError("Could not save folder permission: \(error.localizedDescription)")
            }
#else
            switch captureType {
            case .screenshot:
                settings.screenshotSaveDirectory = url.path
            case .video, .gif:
                settings.videoGifSaveDirectory = url.path
            case nil:
                settings.saveDirectory = url.path
            }
#endif
        }
    }

#if APPSTORE
    private func resetSaveDirectory(for captureType: CaptureType?) {
        settings.resetSaveDirectory(for: captureType)
        SaveService.shared.invalidateSaveDirectoryBookmark(for: captureType)
    }
#else
    private func resetSaveDirectory(for captureType: CaptureType?) {
        switch captureType {
        case .screenshot:
            settings.screenshotSaveDirectory = ""
        case .video, .gif:
            settings.videoGifSaveDirectory = ""
        case nil:
            break
        }
    }
#endif

    private func resetAllSettings() {
        DispatchQueue.main.async {
            let alert = NSAlert()
            alert.messageText = "Reset all settings?"
            alert.informativeText = "This will restore TinyClips settings to defaults, including onboarding state."
            alert.alertStyle = .warning
            alert.addButton(withTitle: "Reset")
            alert.addButton(withTitle: "Cancel")

            guard alert.runModal() == .alertFirstButtonReturn else { return }
            let hotKeyResetError = captureManager.resetCaptureHotKeysToDefaults()
            settings.resetToDefaults(preservingHotKeys: true)
            sparkleController.resetPreferencesToDefaults()
            applyDockVisibility(settings.showInDock)

            if let hotKeyResetError {
                SaveService.shared.showError(hotKeyResetError)
            }
        }
    }

    private func applyDockVisibility(_ showInDock: Bool) {
        NSApplication.shared.setActivationPolicy(showInDock ? .regular : .accessory)
        if showInDock {
            NSRunningApplication.current.activate(options: [.activateAllWindows])
        }
    }

    private var showInDockBinding: Binding<Bool> {
        Binding(
            get: { settings.showInDock },
            set: { isEnabled in
                if isEnabled {
                    settings.showInDock = true
                    applyDockVisibility(true)
                } else {
                    showDisableDockWarning = true
                }
            }
        )
    }

    private func reopenSettingsWindow() {
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
            openWindow(id: "settings-window")
            NSRunningApplication.current.activate(options: [.activateAllWindows])
        }
    }

    private var appVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "Unknown"
    }

    private var appBuild: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "Unknown"
    }

    private var distributionChannel: String {
#if APPSTORE
        return "Mac App Store"
#else
        return "Direct Download"
#endif
    }

    private var reportIssueURL: URL {
        var components = URLComponents(string: "https://github.com/jamesmontemagno/tiny-clips/issues/new")!
        components.queryItems = [
            URLQueryItem(name: "template", value: "bug_report.yml"),
            URLQueryItem(name: "labels", value: "bug"),
            URLQueryItem(name: "title", value: "[Bug]: "),
            URLQueryItem(name: "version", value: appVersion),
            URLQueryItem(name: "build", value: appBuild),
            URLQueryItem(name: "distribution", value: distributionChannel),
            URLQueryItem(name: "macos", value: ProcessInfo.processInfo.operatingSystemVersionString)
        ]
        return components.url!
    }

    private var quickBugReportContext: QuickBugReportContext {
        QuickBugReportContext(
            platform: "macOS",
            version: appVersion,
            build: appBuild,
            distribution: distributionChannel,
            osVersion: ProcessInfo.processInfo.operatingSystemVersionString
        )
    }

    private func refreshCaptureDevices() {
        availableMicrophones = PerformanceSignposts.measureMicrophoneEnumeration {
            MicrophoneDeviceCatalog.availableOptions()
        }
        availableWebcams = WebcamDeviceCatalog.availableOptions()
        if !settings.selectedMicrophoneID.isEmpty,
           !availableMicrophones.contains(where: { $0.id == settings.selectedMicrophoneID }) {
            settings.selectedMicrophoneID = ""
        }
        if !settings.selectedWebcamID.isEmpty, !availableWebcams.contains(where: { $0.id == settings.selectedWebcamID }) {
            settings.selectedWebcamID = ""
        }
    }

    private func refreshCaptureDevicesIfNeeded() {
        guard selectedTab == .video else { return }
        refreshCaptureDevices()
    }

    private var gifMouseClickToggleBinding: Binding<Bool> {
        Binding(
            get: { settings.shouldShowMouseClickVisuals(for: .gif) },
            set: { settings.setShowMouseClickVisuals($0, for: .gif) }
        )
    }
}
