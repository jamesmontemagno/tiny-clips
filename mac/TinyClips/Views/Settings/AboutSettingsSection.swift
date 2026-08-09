import SwiftUI

struct AboutSettingsSection: View {
    @ObservedObject var sparkleController: SparkleController
    let reportIssueURL: URL
    let onFileBug: () -> Void
    let appVersion: String
    let appBuild: String

    private var applicationsFolderURL: URL {
        FileManager.default.urls(for: .applicationDirectory, in: .localDomainMask).first
            ?? URL(fileURLWithPath: "/Applications", isDirectory: true)
    }

    private var isInstalledInApplicationsFolder: Bool {
        let bundleURL = Bundle.main.bundleURL.resolvingSymlinksInPath().standardizedFileURL
        let applicationDirectories = [
            FileManager.default.urls(for: .applicationDirectory, in: .localDomainMask).first,
            FileManager.default.urls(for: .applicationDirectory, in: .userDomainMask).first
        ]
            .compactMap { $0?.resolvingSymlinksInPath().standardizedFileURL }

        return applicationDirectories.contains { applicationDirectory in
            bundleURL.path == applicationDirectory.path ||
            bundleURL.path.hasPrefix(applicationDirectory.path + "/")
        }
    }

    private var latestReleaseURL: URL {
        URL(string: "https://github.com/jamesmontemagno/tiny-clips/releases?q=-mac&expanded=true")!
    }

    var body: some View {
        Section {
            HStack {
                Spacer()
                VStack(spacing: 8) {
                    if let appIcon = NSImage(named: "AppIcon") {
                        Image(nsImage: appIcon)
                            .resizable()
                            .frame(width: 64, height: 64)
                            .cornerRadius(14)
                    }
                    Text("TinyClips")
                        .font(.headline)
                    Text("v\(appVersion) (\(appBuild))")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer()
            }
            .padding(.vertical, 4)
        }

        Section {
            Link("GitHub Repository", destination: URL(string: "https://github.com/jamesmontemagno/tiny-clips")!)
                .accessibilityHint("Opens the TinyClips GitHub repository in your browser.")
            Button("File a Bug…", action: onFileBug)
                .accessibilityHint("Opens a quick bug form, then starts a pre-filled issue on GitHub.")
            Link("Report Detailed Issue", destination: reportIssueURL)
                .accessibilityHint("Opens the detailed issue reporter in your browser.")
            if let privacyURL = URL(string: "https://tinyclips.app/privacy.html") {
                Link("Privacy Policy", destination: privacyURL)
                    .accessibilityHint("Opens Privacy Policy in your browser.")
            }
            if let termsURL = URL(string: "https://www.apple.com/legal/internet-services/itunes/dev/stdeula/") {
                Link("Terms of Use", destination: termsURL)
                    .accessibilityHint("Opens Terms of Use in your browser.")
            }
        }

#if !APPSTORE
        Section {
            Toggle("Automatically check for updates", isOn: Binding(
                get: { sparkleController.automaticallyChecksForUpdates },
                set: { sparkleController.automaticallyChecksForUpdates = $0 }
            ))
            .help("When enabled, TinyClips periodically checks for updates and Sparkle presents the standard update alert when one is available.")

            Button("Check for Updates\u{2026}") {
                sparkleController.checkForUpdates()
            }
            .help("Manually check for updates now.")

            if !isInstalledInApplicationsFolder {
                VStack(alignment: .leading, spacing: 6) {
                    Label("Move TinyClips to Applications for smoother updates.", systemImage: "folder.badge.plus")

                    Text("When TinyClips runs outside the Applications folder, macOS may ask for extra permission before Sparkle can replace the app. Moving it into Applications makes future updates much smoother.")
                        .font(.caption)
                        .foregroundStyle(.secondary)

                    Link("Open Applications Folder", destination: applicationsFolderURL)
                        .accessibilityHint("Opens the Applications folder in Finder so you can move TinyClips there for smoother updates.")
                }
                .padding(.vertical, 2)
            }

            Link("Download the Latest Version", destination: latestReleaseURL)
                .help("If the automatic update check fails, download the newest release directly from GitHub.")
                .accessibilityHint("Opens the latest TinyClips release on GitHub so you can update manually if the in-app update fails.")
        }
#endif
    }
}
