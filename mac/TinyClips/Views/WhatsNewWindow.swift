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
        contentView = NSHostingView(rootView: WhatsNewView(onDone: { [weak self] in
            self?.close()
        }))
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
    let onDone: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            ScrollView {
                VStack(spacing: 20) {
                    VStack(spacing: 6) {
                        Text("What's new")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        Text("Meet Tiny Clips Studio")
                            .font(.largeTitle.bold())
                        Text("Your recording. Your finishing touches.")
                            .foregroundStyle(.secondary)
                    }
                    .multilineTextAlignment(.center)

                    Image("WhatsNewStudio")
                        .resizable()
                        .scaledToFit()
                        .frame(maxHeight: 230)
                        .padding(16)
                        .frame(maxWidth: .infinity)
                        .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 20))
                        .accessibilityLabel("Studio illustration: a screen recording and camera bubble above a colorful editing timeline")

                    Text("Record your screen and camera as separate layers. Shape the story afterward with layouts, backgrounds and a timeline, then export an MP4. Your project stays editable.")
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)

                    VStack(alignment: .leading, spacing: 16) {
                        feature("Give every recording a look", icon: "rectangle.on.rectangle",
                                detail: "Frame your screen with a background, padding and rounded corners. Move your camera bubble or switch to side by side.")
                        feature("Keep the good parts", icon: "scissors",
                                detail: "Trim, cut, add zooms and change the layout with scenes. Adjust the volume before you export.")
                        feature("Export a video. Keep the possibilities.", icon: "folder",
                                detail: "Share an MP4, reopen your project, or save a project folder to carry on editing on another Mac.")
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)

                    VStack(alignment: .leading, spacing: 10) {
                        Toggle("Enable Tiny Clips Studio (Preview)", isOn: $settings.studioPreviewEnabled)
                            .toggleStyle(.switch)
                            .font(.headline)
                            .accessibilityIdentifier("WhatsNewStudioToggle")
                            .accessibilityHint("Adds Studio Recording to the menu bar menu. Ordinary video recording is unchanged.")
                        Text("Once enabled, choose Studio Recording in the menu bar menu. Record Video and its shortcut still make ordinary recordings.")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                        Text("Studio is an early preview and is off by default. You can turn it off in Settings > Studio at any time; your projects are kept.")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                    .padding(18)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 12))
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
