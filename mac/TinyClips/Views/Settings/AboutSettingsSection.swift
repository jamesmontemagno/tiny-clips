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
        SettingsFormSection {
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
                        .settingsDescription()
                }
                Spacer()
            }
            .padding(.vertical, 8)
        }

        SettingsFormSection("Resources", icon: "link") {
            linkRow(
                "GitHub Repository",
                destination: URL(string: "https://github.com/jamesmontemagno/tiny-clips")!,
                hint: "Opens the TinyClips GitHub repository in your browser."
            )

            SettingsActionRow(title: "Feedback", description: "Report a bug or request a feature.") {
                Button("Send Feedback…", action: onFileBug)
                    .accessibilityHint("Choose a bug report or feature request, then open a pre-filled GitHub issue.")
            }

            linkRow(
                "Report Detailed Issue",
                destination: reportIssueURL,
                hint: "Opens the detailed issue reporter in your browser."
            )
            if let privacyURL = URL(string: "https://tinyclips.app/privacy.html") {
                linkRow("Privacy Policy", destination: privacyURL, hint: "Opens Privacy Policy in your browser.")
            }
            if let termsURL = URL(string: "https://www.apple.com/legal/internet-services/itunes/dev/stdeula/") {
                linkRow("Terms of Use", destination: termsURL, hint: "Opens Terms of Use in your browser.")
            }
        }

#if !APPSTORE
        SettingsFormSection("Updates", icon: "arrow.down.circle") {
            SettingsToggleRow(
                title: "Automatically check for updates",
                isOn: Binding(
                    get: { sparkleController.automaticallyChecksForUpdates },
                    set: { sparkleController.automaticallyChecksForUpdates = $0 }
                ),
                help: "When enabled, TinyClips periodically checks for updates and Sparkle presents the standard update alert when one is available."
            )

            SettingsActionRow(title: "Check for updates") {
                Button("Check Now\u{2026}") {
                    sparkleController.checkForUpdates()
                }
                .help("Manually check for updates now.")
                .accessibilityLabel("Check for updates")
            }

            if !isInstalledInApplicationsFolder {
                SettingsActionRow(
                    title: "Move TinyClips to Applications",
                    description: "Outside the Applications folder, macOS may ask for extra permission before an update can replace the app."
                ) {
                    Link("Open Applications Folder", destination: applicationsFolderURL)
                        .accessibilityHint("Opens the Applications folder in Finder so you can move TinyClips there for smoother updates.")
                }
            }

            linkRow(
                "Download the Latest Version",
                destination: latestReleaseURL,
                hint: "Opens the latest TinyClips release on GitHub so you can update manually if the in-app update fails."
            )
            .help("If the automatic update check fails, download the newest release directly from GitHub.")
        }
#endif
    }

    /// A whole row that opens a page in the browser.
    private func linkRow(_ title: String, destination: URL, hint: String) -> some View {
        Link(destination: destination) {
            HStack {
                Text(title)
                    .foregroundStyle(.primary)
                Spacer()
                Image(systemName: "arrow.up.right")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.secondary)
                    .accessibilityHidden(true)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityHint(hint)
    }
}
