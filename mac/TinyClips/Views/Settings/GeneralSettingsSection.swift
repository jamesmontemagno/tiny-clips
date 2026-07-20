import SwiftUI

struct GeneralSettingsSection: View {
    @ObservedObject var settings: CaptureSettings
    @ObservedObject var launchAtLogin: LaunchAtLoginManager
    let chooseSaveDirectory: () -> Void
    let resetSaveDirectory: () -> Void
    let resetAllSettings: () -> Void
    let showInDockBinding: Binding<Bool>
    @State private var showPurgeConfirmation = false

    var body: some View {
        Section("Output") {
#if APPSTORE
            VStack(alignment: .leading, spacing: 6) {
                Text("Default locations: Screenshots/GIFs → Pictures/TinyClips, Videos → Movies/TinyClips")
                    .font(.caption)
                    .foregroundStyle(.secondary)

                HStack {
                    Text(settings.saveDirectoryDisplayPath.isEmpty ? "Using default folders" : settings.saveDirectoryDisplayPath)
                        .lineLimit(1)
                        .truncationMode(.middle)

                    Spacer()

                    Button("Browse…") {
                        chooseSaveDirectory()
                    }

                    if settings.hasCustomSaveDirectory {
                        Button("Reset") {
                            resetSaveDirectory()
                        }
                    }
                }
            }
#else
            HStack {
                TextField("Save to", text: $settings.saveDirectory)
                    .textFieldStyle(.roundedBorder)
                Button("Browse…") {
                    chooseSaveDirectory()
                }
            }
#endif
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
            Toggle("Always capture main display", isOn: $settings.alwaysCaptureMainDisplay)
                .help("Skip the display picker when multiple monitors are connected")
            Toggle("Include TinyClips in captures", isOn: $settings.includeTinyClipsInCapture)
                .help("For developer/demo use. When enabled, TinyClips windows can appear in screenshots, recordings, and window selection.")
            Toggle("Show 'Captured on Tiny Clips' overlay", isOn: $settings.showBrandingOverlay)
                .help("Adds a 'Captured on Tiny Clips' watermark to the bottom-right corner of screenshots, recordings, and GIFs.")
            Link("Open TinyClips Temp Folder", destination: TinyClipsTemporaryFiles.directoryURL)
                .help("Open the temporary folder where TinyClips processes captures.")
                .accessibilityHint("Opens the temporary folder containing TinyClips processing files in Finder.")
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
                } catch {
                    SaveService.shared.showError("Could not purge temporary files: \(error.localizedDescription)")
                }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This permanently deletes temporary files created by TinyClips, including recent files. Close any active recording or unsaved screenshot editor before continuing.")
        }
    }
}
