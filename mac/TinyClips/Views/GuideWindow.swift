import AppKit
import SwiftUI

@MainActor
class GuideWindow: NSWindow, NSWindowDelegate {
    private var onDismiss: (() -> Void)?
    private var didClose = false

    convenience init(onDismiss: @escaping () -> Void) {
        self.init(
            contentRect: NSRect(x: 0, y: 0, width: 640, height: 520),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )

        self.onDismiss = onDismiss
        self.delegate = self
        self.isReleasedWhenClosed = false
        self.title = "TinyClips Guide"
        self.contentMinSize = NSSize(width: 560, height: 420)
        self.center()
        self.contentView = NSHostingView(rootView: GuideWindowView())
    }

    func windowWillClose(_ notification: Notification) {
        guard !didClose else { return }
        didClose = true
        onDismiss?()
        onDismiss = nil
    }
}

private struct GuideWindowView: View {
    @State private var selectedSection: GuideSection = .captureModes
    @ObservedObject private var settings = CaptureSettings.shared

    private var screenshotShortcut: String {
        HotKeyBinding(
            keyCode: settings.screenshotHotKeyCode,
            carbonModifiers: settings.screenshotHotKeyModifiers
        ).displayString
    }

    private var videoShortcut: String {
        HotKeyBinding(
            keyCode: settings.videoHotKeyCode,
            carbonModifiers: settings.videoHotKeyModifiers
        ).displayString
    }

    private var gifShortcut: String {
        HotKeyBinding(
            keyCode: settings.gifHotKeyCode,
            carbonModifiers: settings.gifHotKeyModifiers
        ).displayString
    }

    private var screenshotRegionShortcut: String {
        HotKeyBinding(
            keyCode: settings.screenshotRegionHotKeyCode,
            carbonModifiers: settings.screenshotRegionHotKeyModifiers
        ).displayString
    }

    private var screenshotWindowShortcut: String {
        HotKeyBinding(
            keyCode: settings.screenshotWindowHotKeyCode,
            carbonModifiers: settings.screenshotWindowHotKeyModifiers
        ).displayString
    }

    private var screenshotScreenShortcut: String {
        settings.hotKeyBinding(for: .screenshotScreen).displayString
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            header

            TabView(selection: $selectedSection) {
                ForEach(GuideSection.allCases) { section in
                    ScrollView {
                        sectionContent(for: section)
                            .padding(16)
                            .frame(maxWidth: .infinity, alignment: .topLeading)
                    }
                    .tabItem { Text(section.tabTitle) }
                    .tag(section)
                }
            }
            .accessibilityLabel("Guide topics")
        }
        .padding(24)
        .frame(minWidth: 560, maxWidth: .infinity, minHeight: 420, maxHeight: .infinity, alignment: .topLeading)
        .background(.background)
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("TinyClips Guide")
                .font(.title.bold())
            Text("Everything you need to capture screenshots, videos, and GIFs quickly from your menu bar.")
                .foregroundStyle(.secondary)
        }
    }

    @ViewBuilder
    private func sectionContent(for section: GuideSection) -> some View {
        switch section {
        case .captureModes:
            sectionCard(title: "Capture Modes", icon: "camera.aperture") {
                VStack(alignment: .leading, spacing: 10) {
                    bullet(text: "Screenshot: Capture an image of a selected area.")
                    bullet(text: "Record Video: Capture video, with optional system audio and microphone.")
                    bullet(text: "Record GIF: Capture a short looping clip and export as GIF.")
                }
            }
        case .howItWorks:
            sectionCard(title: "How It Works", icon: "list.number") {
                VStack(alignment: .leading, spacing: 10) {
                    step(number: "1", text: "Choose Screenshot, Record Video, or Record GIF from the menu bar.")
                    step(number: "2", text: "For video/GIF, use the picker to choose Region, Screen, or Window and set an optional countdown.")
                    step(number: "3", text: "After selecting a region, confirm audio/mic options in the start panel and click Record (or cancel).")
                    step(number: "4", text: "Stop from the floating Stop Recording panel or with the stop shortcut.")
                    step(number: "5", text: "Save directly, or use the editor/trimmer when enabled in Settings.")
                }
            }
        case .shortcuts:
            sectionCard(title: "Keyboard Shortcuts", icon: "command") {
                VStack(alignment: .leading, spacing: 8) {
                    Text("Screenshot, Record Video, and Record GIF show your current bindings. Change them anytime in Settings → Shortcuts.")
                        .font(.caption)
                        .foregroundStyle(.secondary)

                    shortcutRow(title: "Screenshot", keys: screenshotShortcut)
                    shortcutRow(title: "Screenshot Region (no picker)", keys: screenshotRegionShortcut)
                    shortcutRow(title: "Screenshot Window (no picker)", keys: screenshotWindowShortcut)
                    shortcutRow(title: "Screenshot Screen (no picker)", keys: screenshotScreenShortcut)
                    shortcutRow(title: "Record Video", keys: videoShortcut)
                    shortcutRow(title: "Record GIF", keys: gifShortcut)
                    shortcutRow(title: "Picker: Region", keys: "R")
                    shortcutRow(title: "Picker: Screen", keys: "S")
                    shortcutRow(title: "Picker: Window", keys: "W")
                    shortcutRow(title: "Picker: Cancel", keys: "Esc")
                    shortcutRow(title: "Stop Recording", keys: "⌘.")
                    shortcutRow(title: "Settings", keys: "⌘,")
                    shortcutRow(title: "Quit", keys: "⌘Q")
                }
            }
        case .quickTips:
            sectionCard(title: "Quick Tips", icon: "lightbulb") {
                VStack(alignment: .leading, spacing: 10) {
                    bullet(text: "Use Settings to enable countdowns, screenshot editor, and trimmer windows.")
                    bullet(text: "Your current capture shortcuts are \(screenshotShortcut) for Screenshot, \(videoShortcut) for Record Video, and \(gifShortcut) for Record GIF. You can change them in Settings → Shortcuts.")
                    bullet(text: "If Screen Recording permission changes, restart TinyClips.")
                    bullet(text: "For video/GIF, pick Region, Screen, or Window directly from the recording picker.")
                }
            }
        case .agents:
            sectionCard(title: "Agent Control", icon: "terminal") {
                AgentControlSetupView()
            }
        }
    }

    @ViewBuilder
    private func sectionCard<Content: View>(title: String, icon: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Label(title, systemImage: icon)
                .font(.headline)

            content()
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
    }

    @ViewBuilder
    private func bullet(text: String) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: "circle.fill")
                .font(.system(size: 6))
                .foregroundStyle(.secondary)
                .padding(.top, 6)
            Text(text)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    @ViewBuilder
    private func step(number: String, text: String) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Text(number)
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
                .frame(width: 20, alignment: .leading)
            Text(text)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    @ViewBuilder
    private func shortcutRow(title: String, keys: String) -> some View {
        HStack {
            Text(title)
            Spacer()
            Text(keys)
                .font(.system(.body, design: .monospaced))
                .foregroundStyle(.secondary)
        }
        .padding(.vertical, 2)
    }
}

private enum GuideSection: String, CaseIterable, Identifiable {
    case captureModes
    case howItWorks
    case shortcuts
    case quickTips
    case agents

    var id: String { rawValue }

    var tabTitle: String {
        switch self {
        case .captureModes:
            return "Capture"
        case .howItWorks:
            return "Steps"
        case .shortcuts:
            return "Shortcuts"
        case .quickTips:
            return "Tips"
        case .agents:
            return "Agents"
        }
    }
}
