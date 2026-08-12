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

    var body: some View {
        Section("Output") {
            VStack(alignment: .leading, spacing: 6) {
                Toggle("Use default folders", isOn: $settings.useDefaultSaveDirectories)
                    .accessibilityLabel("Use default capture folders")

                Text("Screenshots → Pictures/TinyClips, Videos and GIFs → Movies/TinyClips")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

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
                    .font(.caption)
                    .foregroundStyle(.secondary)

                Text("Preview: \(SaveService.shared.namingPreview(for: .screenshot))")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }

            Toggle("Show in Finder after save", isOn: $settings.showInFinder)
            Toggle("Show notification after save", isOn: $settings.showSaveNotifications)
        }

        Section("Advanced") {
            Toggle("Launch at login", isOn: Binding(
                get: { launchAtLogin.isEnabled },
                set: { launchAtLogin.setEnabled($0) }
            ))
            Toggle("Show TinyClips in Dock (enables ⌘⇥)", isOn: showInDockBinding)
                .help("When enabled, TinyClips appears in Command-Tab and can participate in normal app/window switching.")
            Picker("When capturing a display", selection: $settings.multiMonitorCaptureMode) {
                ForEach(MultiMonitorCaptureMode.allCases, id: \.self) { mode in
                    Text(mode.label).tag(mode)
                }
            }
            .help("Choose whether TinyClips asks for a display or captures one automatically when multiple monitors are connected.")
            Toggle("Include TinyClips in captures", isOn: $settings.includeTinyClipsInCapture)
                .help("For developer/demo use. When enabled, TinyClips windows can appear in screenshots, recordings, and window selection.")
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
            Button("Purge", role: .destructive) {
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
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .truncationMode(.middle)

            Text(saveDirectoryHint(for: type))
                .font(.caption2)
                .foregroundStyle(.secondary)
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
            .font(.caption)
            .foregroundStyle(.secondary)
        } else if let temporaryFilesSummary {
            let fileLabel = temporaryFilesSummary.fileCount == 1 ? "file" : "files"
            Text("\(temporaryFilesSummary.fileCount) temporary \(fileLabel) using \(ByteCountFormatter.string(fromByteCount: temporaryFilesSummary.totalSize, countStyle: .file))")
                .font(.caption)
                .foregroundStyle(.secondary)
                .accessibilityLabel("\(temporaryFilesSummary.fileCount) TinyClips temporary \(fileLabel), using \(ByteCountFormatter.string(fromByteCount: temporaryFilesSummary.totalSize, countStyle: .file))")
        } else {
            Text("Could not calculate TinyClips temporary files.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private func loadTemporaryFilesSummary() async {
        isLoadingTemporaryFiles = true
        temporaryFilesSummary = await Task.detached(priority: .utility) {
            try? TinyClipsTemporaryFiles.summary()
        }.value
        isLoadingTemporaryFiles = false
    }
}

struct BrandingSettingsSection: View {
    @ObservedObject var settings: CaptureSettings

    var body: some View {
        Section("Branding") {
            Toggle("Show 'Captured on Tiny Clips' overlay", isOn: $settings.showBrandingOverlay)
                .help("Adds a 'Captured on Tiny Clips' watermark to the bottom-right corner of screenshots, recordings, and GIFs.")
        }
    }
}
