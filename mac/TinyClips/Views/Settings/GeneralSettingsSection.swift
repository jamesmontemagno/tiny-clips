import AppKit
import SwiftUI

struct GeneralSettingsSection: View {
    @ObservedObject var settings: CaptureSettings
    @ObservedObject var launchAtLogin: LaunchAtLoginManager
    let chooseSaveDirectory: (CaptureType) -> Void
    let resetAllSettings: () -> Void
    let showInDockBinding: Binding<Bool>
    @State private var showPurgeConfirmation = false
    @State private var temporaryFilesSummary: TinyClipsTemporaryFiles.Summary?
    @State private var isLoadingTemporaryFiles = true
    @State private var notificationPermission: NotificationPermissionState?
    @State private var isRequestingNotifications = false

    var body: some View {
        SettingsFormSection("Save Location", icon: "folder") {
            SettingsToggleRow(
                title: "Use default folders",
                description: "Screenshots go to Pictures/TinyClips. Videos and GIFs go to Movies/TinyClips.",
                isOn: $settings.useDefaultSaveDirectories,
                accessibilityHint: "Use the default capture folders for screenshots, videos, and GIFs."
            )

            if !settings.useDefaultSaveDirectories {
                saveDirectoryRow(title: "Screenshots folder", type: .screenshot)
                saveDirectoryRow(title: "Videos folder", type: .video)
                saveDirectoryRow(title: "GIFs folder", type: .gif)
            }

            SettingsToggleRow(
                title: "Show in Finder after save",
                isOn: $settings.showInFinder
            )
        }

        SettingsFormSection(
            "File Names",
            icon: "textformat",
            footer: "Tokens: {app}, {type}, {date}, {time}, {datetime}"
        ) {
            TextField("File name template", text: $settings.fileNameTemplate)

            LabeledContent("Presets") {
                HStack(spacing: 8) {
                    Button("Classic") {
                        settings.fileNameTemplate = "TinyClips {date} at {time}"
                    }
                    Button("Type + Date") {
                        settings.fileNameTemplate = "{type} {date} at {time}"
                    }
                    Button("Date First") {
                        settings.fileNameTemplate = "{date} {time} {type}"
                    }
                }
                .controlSize(.small)
            }

            LabeledContent("Preview") {
                Text(SaveService.shared.namingPreview(for: .screenshot))
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
        }

        SettingsFormSection(
            "Notifications",
            icon: "bell",
            footer: "System permission allows notifications for saved captures and background tasks, including Studio captions. macOS controls how they are delivered."
        ) {
            SettingsActionRow(
                title: "System notifications",
                description: notificationPermission?.summary ?? "Checking…"
            ) {
                if let notificationPermission {
                    if notificationPermission.canRequest {
                        Button("Enable…") {
                            isRequestingNotifications = true
                            Task {
                                defer { isRequestingNotifications = false }
                                _ = await PermissionManager.shared.requestNotificationPermission()
                                await refreshNotificationPermission()
                                if self.notificationPermission?.canRequest == true {
                                    SaveService.shared.showError("Could not request notification permission. Please try again or enable TinyClips notifications in macOS System Settings.")
                                }
                            }
                        }
                        .disabled(isRequestingNotifications)
                        .accessibilityLabel("Enable notifications")
                        .accessibilityHint("Requests macOS permission for TinyClips notifications.")
                    } else {
                        Button("Open Settings…") {
                            guard PermissionManager.shared.openNotificationSettings() else {
                                SaveService.shared.showError("Could not open macOS Notification Settings.")
                                return
                            }
                        }
                        .accessibilityLabel("Open notification settings")
                        .accessibilityHint("Opens macOS notification settings to manage TinyClips alerts.")
                    }
                }
            }

            SettingsToggleRow(
                title: "Show notification after save",
                isOn: $settings.showSaveNotifications,
                help: "Only controls save notifications. Does not grant system permission or change background-task notifications.",
                accessibilityHint: "Show a notification after saving a capture."
            )
        }
        .task {
            await refreshNotificationPermission()
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            Task { await refreshNotificationPermission() }
        }

        SettingsFormSection("Capture", icon: "camera.viewfinder") {
            Picker("When capturing a display", selection: $settings.multiMonitorCaptureMode) {
                ForEach(MultiMonitorCaptureMode.allCases, id: \.self) { mode in
                    Text(mode.label).tag(mode)
                }
            }
            .help("Choose whether TinyClips asks for a display or captures one automatically when multiple monitors are connected.")

            SettingsToggleRow(
                title: "Include TinyClips in captures",
                description: "For demos and development. TinyClips windows can appear in captures and in window selection.",
                isOn: $settings.includeTinyClipsInCapture,
                help: "For developer/demo use. When enabled, TinyClips windows can appear in screenshots, recordings, and window selection."
            )
        }

        SettingsFormSection("App Behavior", icon: "macwindow") {
            SettingsToggleRow(
                title: "Launch at login",
                isOn: Binding(
                    get: { launchAtLogin.isEnabled },
                    set: { launchAtLogin.setEnabled($0) }
                )
            )
            SettingsToggleRow(
                title: "Show TinyClips in Dock",
                description: "Also adds TinyClips to the ⌘⇥ app switcher.",
                isOn: showInDockBinding,
                help: "When enabled, TinyClips appears in Command-Tab and can participate in normal app/window switching."
            )
            SettingsToggleRow(
                title: "Confirm before closing editors with Esc",
                description: "Ask before Esc closes an editor with unsaved work.",
                isOn: $settings.confirmEditorEscape,
                help: "Ask before Esc closes the screenshot editor, video trimmer, or GIF trimmer. Turn off to close them right away, discarding anything unsaved."
            )
        }

        SettingsFormSection("Maintenance", icon: "wrench.and.screwdriver") {
            SettingsActionRow(title: "Temporary files", description: temporaryFilesSummaryText) {
                Button("Open Folder") {
                    NSWorkspace.shared.open(TinyClipsTemporaryFiles.directoryURL)
                }
                .help("Open the temporary folder where TinyClips processes captures.")
                .accessibilityLabel("Open TinyClips temp folder")
                .accessibilityHint("Opens the temporary folder containing TinyClips processing files in Finder.")

                Button("Purge…", role: .destructive) {
                    showPurgeConfirmation = true
                }
                .help("Delete all temporary files currently created by TinyClips.")
                .accessibilityLabel("Purge temp files now")
                .accessibilityHint("Deletes temporary TinyClips files after confirmation.")
            }

            SettingsActionRow(title: "Reset all settings", description: "Return every setting to its default.") {
                Button("Reset…") {
                    resetAllSettings()
                }
                .accessibilityLabel("Reset all settings to defaults")
            }
        }
        .confirmationDialog(
            "Purge TinyClips temporary files?",
            isPresented: $showPurgeConfirmation,
            titleVisibility: .visible
        ) {
            Button("Purge") {
                do {
                    try TinyClipsTemporaryFiles.purge()
                    Task {
                        await loadTemporaryFilesSummary()
                    }
                } catch {
                    SaveService.shared.showError("Could not purge temporary files: \(error.localizedDescription)")
                }
            }
            Button("Cancel", role: .cancel) {}
                .keyboardShortcut(.defaultAction)
        } message: {
            Text("This permanently deletes temporary files created by TinyClips, including recent files. Close any active recording or unsaved screenshot editor before continuing.")
        }
        .task {
            await loadTemporaryFilesSummary()
        }
    }

    private func saveDirectoryRow(title: String, type: CaptureType) -> some View {
        LabeledContent {
            Button("Choose…") {
                chooseSaveDirectory(type)
            }
            .accessibilityLabel("Choose \(title.lowercased())")
        } label: {
            Text(title)
            Text(saveDirectoryPath(for: type))
                .lineLimit(1)
                .truncationMode(.middle)
        }
    }

    private func saveDirectoryPath(for type: CaptureType) -> String {
#if APPSTORE
        let path = settings.saveDirectoryDisplayPath(for: type)
        return path.isEmpty ? "No folder selected" : path
#else
        let path = settings.saveDirectoryPath(for: type)
        return path.isEmpty ? "No folder selected" : path
#endif
    }

    private var temporaryFilesSummaryText: String {
        if isLoadingTemporaryFiles {
            return "Calculating…"
        }
        guard let temporaryFilesSummary else {
            return "Could not calculate the size."
        }
        let fileLabel = temporaryFilesSummary.fileCount == 1 ? "file" : "files"
        let size = ByteCountFormatter.string(fromByteCount: temporaryFilesSummary.totalSize, countStyle: .file)
        return "\(temporaryFilesSummary.fileCount) \(fileLabel) using \(size)"
    }

    private func loadTemporaryFilesSummary() async {
        isLoadingTemporaryFiles = true
        temporaryFilesSummary = await Task.detached(priority: .utility) {
            try? TinyClipsTemporaryFiles.summary()
        }.value
        isLoadingTemporaryFiles = false
    }

    private func refreshNotificationPermission() async {
        notificationPermission = await PermissionManager.shared.notificationPermissionState()
    }
}

struct BrandingSettingsSection: View {
    @ObservedObject var settings: CaptureSettings

    var body: some View {
        SettingsFormSection("Branding", icon: "flag") {
            SettingsToggleRow(
                title: "Show 'Captured on Tiny Clips' overlay",
                description: "Adds a small watermark to the bottom-right corner of screenshots, recordings, and GIFs.",
                isOn: $settings.showBrandingOverlay,
                help: "Adds a 'Captured on Tiny Clips' watermark to the bottom-right corner of screenshots, recordings, and GIFs."
            )
        }
    }
}
