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
        SettingsFormSection("Output", icon: "folder") {
            SettingsToggleRow(
                title: "Use default folders",
                description: "Screenshots → Pictures/TinyClips; videos and GIFs → Movies/TinyClips.",
                isOn: $settings.useDefaultSaveDirectories,
                accessibilityHint: "Use the default capture folders for screenshots, videos, and GIFs."
            )

            if !settings.useDefaultSaveDirectories {
                saveDirectoryRow(title: "Screenshots folder", type: .screenshot)
                saveDirectoryRow(title: "Videos folder", type: .video)
                saveDirectoryRow(title: "GIFs folder", type: .gif)
            }

            VStack(alignment: .leading, spacing: 6) {
                TextField("File name template", text: $settings.fileNameTemplate)
                    .textFieldStyle(.roundedBorder)

                HStack(spacing: 10) {
                    Button("Classic") {
                        settings.fileNameTemplate = "TinyClips {date} at {time}"
                    }
                    .buttonStyle(.link)

                    Button("Type + Date") {
                        settings.fileNameTemplate = "{type} {date} at {time}"
                    }
                    .buttonStyle(.link)

                    Button("Date First") {
                        settings.fileNameTemplate = "{date} {time} {type}"
                    }
                    .buttonStyle(.link)
                }
                .font(.caption)

                Text("Tokens: {app}, {type}, {date}, {time}, {datetime}")
                    .settingsDescription()

                Text("Preview: \(SaveService.shared.namingPreview(for: .screenshot))")
                    .settingsDescription()
                    .lineLimit(1)
                    .truncationMode(.middle)
            }

            SettingsToggleRow(
                title: "Show in Finder after save",
                description: "Reveal the saved capture in Finder.",
                isOn: $settings.showInFinder
            )
        }

        SettingsFormSection("Notifications", icon: "bell") {
            LabeledContent("System notifications", value: notificationPermission?.summary ?? "Checking…")
                .accessibilityElement(children: .combine)

            if let notificationPermission {
                if notificationPermission.canRequest {
                    Button("Enable Notifications") {
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
                    .accessibilityHint("Requests macOS permission for TinyClips notifications.")
                } else {
                    Button("Open Notification Settings") {
                        guard PermissionManager.shared.openNotificationSettings() else {
                            SaveService.shared.showError("Could not open macOS Notification Settings.")
                            return
                        }
                    }
                    .accessibilityHint("Opens macOS notification settings to manage TinyClips alerts.")
                }
            }

            Text("System permission allows notifications for saved captures and background tasks, including Studio captions. macOS controls how they are delivered.")
                .settingsDescription()

            SettingsToggleRow(
                title: "Show notification after save",
                description: "Display a notification when a capture is saved.",
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

        SettingsFormSection("Editors", icon: "pencil.and.outline") {
            SettingsToggleRow(
                title: "Confirm before closing editors with Esc",
                description: "Ask before closing an editor with unsaved work.",
                isOn: $settings.confirmEditorEscape,
                help: "Ask before Esc closes the screenshot editor, video trimmer, or GIF trimmer. Turn off to close them right away, discarding anything unsaved."
            )
        }

        SettingsFormSection("Advanced", icon: "slider.horizontal.3") {
            SettingsToggleRow(
                title: "Launch at login",
                description: "Open TinyClips automatically after sign-in.",
                isOn: Binding(
                    get: { launchAtLogin.isEnabled },
                    set: { launchAtLogin.setEnabled($0) }
                )
            )
            SettingsToggleRow(
                title: "Show TinyClips in Dock (enables ⌘⇥)",
                description: "Make TinyClips visible in the Dock and app switcher.",
                isOn: showInDockBinding,
                help: "When enabled, TinyClips appears in Command-Tab and can participate in normal app/window switching."
            )
            Picker("When capturing a display", selection: $settings.multiMonitorCaptureMode) {
                ForEach(MultiMonitorCaptureMode.allCases, id: \.self) { mode in
                    Text(mode.label).tag(mode)
                }
            }
            .help("Choose whether TinyClips asks for a display or captures one automatically when multiple monitors are connected.")
            SettingsToggleRow(
                title: "Include TinyClips in captures",
                description: "Let TinyClips windows appear in captures.",
                isOn: $settings.includeTinyClipsInCapture,
                help: "For developer/demo use. When enabled, TinyClips windows can appear in screenshots, recordings, and window selection."
            )
            Button {
                NSWorkspace.shared.open(TinyClipsTemporaryFiles.directoryURL)
            } label: {
                Label("Open TinyClips Temp Folder", systemImage: "folder")
            }
                .help("Open the temporary folder where TinyClips processes captures.")
                .accessibilityHint("Opens the temporary folder containing TinyClips processing files in Finder.")
            temporaryFilesSummaryView
            Button("Purge Temp Files Now…", role: .destructive) {
                showPurgeConfirmation = true
            }
            .help("Delete all temporary files currently created by TinyClips.")
            .accessibilityHint("Deletes temporary TinyClips files after confirmation.")
            Button("Reset All Settings to Defaults…") {
                resetAllSettings()
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

    @ViewBuilder
    private func saveDirectoryRow(title: String, type: CaptureType) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(title)
                Spacer()
                Button("Choose…") {
                    chooseSaveDirectory(type)
                }
            }

            Text(saveDirectoryPath(for: type))
                .settingsDescription()
                .lineLimit(1)
                .truncationMode(.middle)

            Text(saveDirectoryHint(for: type))
                .settingsDescription()
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

    private func saveDirectoryHint(for type: CaptureType) -> String {
        "Saved captures of this type use this folder."
    }

    @ViewBuilder
    private var temporaryFilesSummaryView: some View {
        if isLoadingTemporaryFiles {
            HStack(spacing: 6) {
                ProgressView()
                    .controlSize(.small)
                Text("Calculating TinyClips temporary files…")
            }
            .settingsDescription()
        } else if let temporaryFilesSummary {
            let fileLabel = temporaryFilesSummary.fileCount == 1 ? "file" : "files"
            Text("\(temporaryFilesSummary.fileCount) temporary \(fileLabel) using \(ByteCountFormatter.string(fromByteCount: temporaryFilesSummary.totalSize, countStyle: .file))")
                .settingsDescription()
                .accessibilityLabel("\(temporaryFilesSummary.fileCount) TinyClips temporary \(fileLabel), using \(ByteCountFormatter.string(fromByteCount: temporaryFilesSummary.totalSize, countStyle: .file))")
        } else {
            Text("Could not calculate TinyClips temporary files.")
                .settingsDescription()
        }
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
                description: "Add a TinyClips watermark to captures.",
                isOn: $settings.showBrandingOverlay,
                help: "Adds a 'Captured on Tiny Clips' watermark to the bottom-right corner of screenshots, recordings, and GIFs."
            )
        }
    }
}
