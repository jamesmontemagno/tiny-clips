import AppKit
import SwiftUI

@MainActor
final class WhatsNewWindow: NSWindow, NSWindowDelegate {
    private var onDismiss: (() -> Void)?
    private var didClose = false

    convenience init(onDismiss: @escaping () -> Void) {
        self.init(
            contentRect: NSRect(x: 0, y: 0, width: 660, height: 820),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        self.onDismiss = onDismiss
        delegate = self
        isReleasedWhenClosed = false
        title = "What's new in Tiny Clips"
        contentMinSize = NSSize(width: 480, height: 520)
        if let screen = NSScreen.main {
            setContentSize(NSSize(width: min(660, screen.visibleFrame.width - 40),
                                  height: min(820, screen.visibleFrame.height - 80)))
        }
        center()
        contentView = NSHostingView(rootView: WhatsNewView(
            onOpenAgentSettings: { [weak self] in
                SettingsWindowManager.shared.selectedTab = .agentControl
                guard AutomationWindowOpener.shared.open(id: "settings-window", title: "Tiny Clips Settings") else { return }
                self?.close()
            },
            onDone: { [weak self] in
                self?.close()
            }
        ))
    }

    func windowWillClose(_ notification: Notification) {
        guard !didClose else { return }
        didClose = true
        let callback = onDismiss
        onDismiss = nil
        callback?()
    }

    override func cancelOperation(_ sender: Any?) {
        close()
    }
}

private struct WhatsNewView: View {
    @ObservedObject private var settings = CaptureSettings.shared
    let onOpenAgentSettings: () -> Void
    let onDone: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            ScrollView {
                VStack(spacing: 20) {
                    VStack(spacing: 6) {
                        Text("What's new")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        Text("Your AI agent can now use Tiny Clips")
                            .font(.largeTitle.bold())
                            .accessibilityAddTraits(.isHeader)
                        Text("Screenshots, recordings, and Studio edits, with you in control.")
                            .foregroundStyle(.secondary)
                    }
                    .multilineTextAlignment(.center)

                    AgentControlDiagram()

                    Text("Turn on Agent Control and AI agents on this Mac can take screenshots, record, edit Studio projects, add captions and annotations, and export the result. Bring your own agent; Tiny Clips provides the tools.")
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)

                    VStack(alignment: .leading, spacing: 10) {
                        Text("Off until you turn it on")
                            .font(.headline)
                        Text("Each agent needs your approval once and only gets the permissions you choose. Deleting, uploading, and discarding always ask first, and every request is logged on this Mac. You'll find the setup steps in Settings → Agent Control.")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                        Button("Set Up Agent Control", action: onOpenAgentSettings)
                            .accessibilityIdentifier("WhatsNewOpenAgentSettingsButton")
                            .accessibilityHint("Opens Settings to Agent Control. This does not turn it on.")
                    }
                    .padding(18)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 12))

                    sectionHeading("Tiny Clips Studio")

                    Image("WhatsNewStudio")
                        .resizable()
                        .scaledToFit()
                        .frame(maxHeight: 150)
                        .padding(16)
                        .frame(maxWidth: .infinity)
                        .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 20))
                        .accessibilityLabel("Studio illustration: a screen recording and camera bubble above a colorful editing timeline")

                    Text("Record your screen and camera as separate layers. Shape the story afterward with layouts, backgrounds and a timeline, then export an MP4. Your project stays editable.")
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, alignment: .leading)

                    VStack(alignment: .leading, spacing: 10) {
                        Toggle("Enable Tiny Clips Studio (Preview)", isOn: $settings.studioPreviewEnabled)
                            .toggleStyle(.switch)
                            .font(.headline)
                            .accessibilityIdentifier("WhatsNewStudioToggle")
                            .accessibilityHint("Adds Studio Recording to the menu bar menu. Ordinary video recording is unchanged.")
                        Text("Choose Studio Recording in the menu bar menu when Studio is on. Record Video and its shortcut still make ordinary recordings.")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                        Text("Studio is on by default unless you turned it off. Your choice is kept, and you can change it here or in Settings → Studio at any time. Your projects are kept.")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                    .padding(18)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 12))

                    sectionHeading("New in Studio")

                    VStack(alignment: .leading, spacing: 16) {
                        feature("Captions", icon: "captions.bubble",
                                detail: "Generate captions on this Mac, correct them, draw them on the video, or export SRT and WebVTT. Generating needs macOS 26 or later on a supported Mac.")
                        feature("Annotations", icon: "pencil.tip",
                                detail: "Add arrows, callouts, text, shapes, emoji, and redactions that can follow your zooms.")
                        feature("Export your way", icon: "square.and.arrow.up",
                                detail: "Choose the resolution and quality, and see the estimated file size first.")
                        feature("Open any video", icon: "video",
                                detail: "Bring a video into Studio from the trimmer or the Clips Manager to add zooms, backgrounds, cuts, and speed changes.")
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)

                    sectionHeading("More to try")

                    VStack(alignment: .leading, spacing: 16) {
                        feature("Find sensitive text", icon: "text.magnifyingglass",
                                detail: "The screenshot editor can suggest emails, phone numbers, and IP addresses to black out. Approve each one and check the image yourself, because it can miss things.")
                        feature("Redaction with real blur", icon: "eye.slash",
                                detail: "The Redact tool now blurs your screenshot at Light, Medium, or Heavy, or covers it with Solid black. Use Solid for passwords and other secrets, because Light can stay readable.")
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .padding(28)
                .frame(maxWidth: 660)
                .frame(maxWidth: .infinity)
            }

            Divider()
            HStack {
                Link("Full changelog", destination: URL(string: "https://github.com/jamesmontemagno/tiny-clips/blob/main/CHANGELOG.md")!)
                Spacer()
                Button("Got it", action: onDone)
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
                    .accessibilityIdentifier("WhatsNewDoneButton")
            }
            .padding(.horizontal, 24)
            .padding(.vertical, 16)
        }
        .background(.background)
    }

    private func sectionHeading(_ title: String) -> some View {
        Text(title)
            .font(.title2.bold())
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.top, 8)
            .accessibilityAddTraits(.isHeader)
    }

    private func feature(_ title: String, icon: String, detail: String) -> some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: icon)
                .font(.title3)
                .foregroundStyle(.tint)
                .frame(width: 40, height: 40)
                .background(Color.accentColor.opacity(0.12), in: RoundedRectangle(cornerRadius: 10))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 4) {
                Text(title).font(.headline)
                Text(detail).font(.callout).foregroundStyle(.secondary)
            }
        }
        .accessibilityElement(children: .combine)
    }
}

private struct AgentControlDiagram: View {
    private let capabilities: [(title: String, icon: String)] = [
        ("Screenshots", "camera.viewfinder"),
        ("Recordings", "video"),
        ("Studio", "film"),
        ("Captions", "captions.bubble"),
        ("Annotations", "pencil.tip"),
    ]

    var body: some View {
        HStack(spacing: 8) {
            node(icon: "sparkles", title: "Your agent", detail: "Plugin, MCP, or CLI",
                 background: Color.primary.opacity(0.06))
            arrow
            node(icon: "terminal", title: "Agent Control", detail: "Permissions you choose",
                 background: Color.accentColor.opacity(0.18))
            arrow
            VStack(alignment: .leading, spacing: 8) {
                ForEach(capabilities, id: \.title) { capability in
                    Label(capability.title, systemImage: capability.icon)
                        .font(.caption)
                }
            }
            .fixedSize()
            .padding(.horizontal, 12)
            .padding(.vertical, 10)
            .background(Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 8))
        }
        .padding(16)
        .frame(maxWidth: .infinity)
        .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 12))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Diagram: your AI agent connects through Agent Control, with the permissions you choose, to screenshots, recordings, Studio, captions, and annotations.")
        .accessibilityAddTraits(.isImage)
        .accessibilityIdentifier("WhatsNewAgentDiagram")
    }

    private var arrow: some View {
        Image(systemName: "arrow.right")
            .font(.caption.bold())
            .foregroundStyle(.secondary)
    }

    private func node(icon: String, title: String, detail: String, background: Color) -> some View {
        VStack(spacing: 6) {
            Image(systemName: icon)
                .font(.title2)
                .foregroundStyle(.tint)
            Text(title)
                .font(.headline)
            Text(detail)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .multilineTextAlignment(.center)
        .padding(.horizontal, 10)
        .padding(.vertical, 14)
        .frame(maxWidth: .infinity)
        .background(background, in: RoundedRectangle(cornerRadius: 8))
    }
}
