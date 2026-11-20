import SwiftUI

struct ShortcutsSettingsSection: View {
    @ObservedObject var settings: CaptureSettings
    @ObservedObject var captureManager: CaptureManager

    @State private var shortcutError: String?

    var body: some View {
        if let error = shortcutError ?? captureManager.hotKeyRegistrationError {
            SettingsFormSection("Shortcut status", icon: "exclamationmark.triangle") {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .accessibilityElement(children: .combine)
                    .accessibilityLabel("Shortcut error: \(error)")
            }
        }

        SettingsFormSection(
            "Screenshots",
            icon: "camera",
            footer: "Global shortcuts work even when Tiny Clips is in the background. Only Screenshot is assigned by default. Region, Screen, and Window skip the capture picker. Include at least one modifier (⌃ ⌥ ⇧ ⌘)."
        ) {
            shortcutRows([.screenshot, .screenshotRegion, .screenshotScreen, .screenshotWindow])
        }

        SettingsFormSection(
            "Recording",
            icon: "video",
            footer: "Optional global shortcuts, not assigned by default. Record Video makes an ordinary recording, not Studio. Stop Recording is active only during recording; the floating Stop button is always available."
        ) {
            shortcutRows([.recordVideo, .recordGif, .stopRecording])
        }

        SettingsFormSection(
            "Text recognition",
            icon: "text.viewfinder",
            footer: "An optional global shortcut, not assigned by default."
        ) {
            shortcutRows([.copyTextFromRegion])
        }

        SettingsFormSection(
            "Screenshot editor",
            icon: "pencil",
            footer: "Reference only. These fixed shortcuts apply in the screenshot editor, not in other apps."
        ) {
            fixedShortcutRow(label: "Save / Save As", iconName: "square.and.arrow.down", keys: "⌘S / ⇧⌘S")
            fixedShortcutRow(label: "Copy / pan", iconName: "doc.on.doc", keys: "⌘C / Hold Space")
            fixedShortcutRow(label: "Undo / Redo", iconName: "arrow.uturn.backward", keys: "⌘Z / ⇧⌘Z")
            fixedShortcutRow(label: "Zoom in / out / fit", iconName: "magnifyingglass", keys: "⌘+ / ⌘− / ⌘0")
            fixedShortcutRow(label: "Move / resize crop", iconName: "crop", keys: "Arrows / ⌥Arrows; ⇧ for 10 px")
            fixedShortcutRow(label: "Apply crop / delete annotation", iconName: "checkmark", keys: "Return / Delete")
        }

        SettingsFormSection(
            "Studio playback",
            icon: "square.stack.3d.up",
            footer: "Reference only. Fixed shortcuts apply in Studio when the focused control is not using the key."
        ) {
            fixedShortcutRow(label: "Play / pause", iconName: "playpause", keys: "Space")
            fixedShortcutRow(label: "Step frame / second", iconName: "forward.frame", keys: "← → / ⇧← ⇧→")
            fixedShortcutRow(label: "Trim start / end", iconName: "scissors", keys: "I / O; Home / End to seek")
        }

        SettingsFormSection(
            "Studio editing & projects",
            icon: "square.stack.3d.up",
            footer: "Reference only. Fixed shortcuts apply in Studio when you are not typing or dragging."
        ) {
            fixedShortcutRow(label: "Scene / cut / speed / zoom", iconName: "timeline.selection", keys: "S / X / R / Z")
            fixedShortcutRow(label: "Arrow / callout / text / box / ellipse / redact / emoji", iconName: "pencil", keys: "A / C / T / H / E / B / M")
            fixedShortcutRow(label: "Layouts", iconName: "rectangle.3.group", keys: "1 / 2 / 3 / 4")
            fixedShortcutRow(label: "Open / save / Save As / export", iconName: "square.and.arrow.up", keys: "⌘O / ⌘S / ⇧⌘S / ⌘E")
            fixedShortcutRow(label: "Undo / redo / duplicate / delete", iconName: "arrow.uturn.backward", keys: "⌘Z / ⇧⌘Z / ⌘D / Delete")
        }

        SettingsFormSection(
            "Video & GIF trimmers",
            icon: "scissors",
            footer: "Reference only. Fixed shortcuts apply in the active trimmer; focused controls keep their normal keys."
        ) {
            fixedShortcutRow(label: "Video: previous / next frame", iconName: "forward.frame", keys: "← / →")
            fixedShortcutRow(label: "Close trimmer", iconName: "xmark.circle", keys: "Esc")
        }

        SettingsFormSection(
            "Clips Library",
            icon: "photo.on.rectangle",
            footer: "Reference only. This fixed shortcut applies in the Clips Library."
        ) {
            fixedShortcutRow(label: "Show / hide details", iconName: "info.circle", keys: "⌥⌘I")
        }

        SettingsFormSection(
            "Navigation & application",
            icon: "keyboard",
            footer: "Reference only. Fixed shortcuts apply to the active picker, panel, or app menu. Escape closes editors using your confirmation preference."
        ) {
            fixedShortcutRow(label: "Picker: region / screen / window / scroll", iconName: "viewfinder", keys: "R / S / W / P")
            fixedShortcutRow(label: "Scrolling capture: done / cancel", iconName: "scroll", keys: "Return / Esc")
            fixedShortcutRow(label: "Recording panel: pause / restart / discard", iconName: "record.circle", keys: "P / R / Delete")
            fixedShortcutRow(label: "Close / cancel", iconName: "xmark.circle", keys: "Esc")
            fixedShortcutRow(label: "Settings", iconName: "gearshape", keys: "⌘,")
            fixedShortcutRow(label: "Close active window / quit from menu", iconName: "power", keys: "⌘Q")
        }
    }

    private func shortcutRows(_ actions: [HotKeyAction]) -> some View {
        ForEach(actions, id: \.self) { action in
            ShortcutRecorderField(
                label: action.displayName,
                iconName: action.iconName,
                binding: settings.hotKeyBinding(for: action),
                defaultBinding: HotKeyBinding.defaultBinding(for: action),
                onBindingRecorded: { apply($0, for: action) }
            )
            .accessibilityLabel("\(action.displayName) keyboard shortcut")
        }
    }

    private func fixedShortcutRow(label: String, iconName: String, keys: String) -> some View {
        HStack {
            Label {
                Text(label)
            } icon: {
                Image(systemName: iconName)
                    .frame(width: ShortcutRecorderField.iconWidth)
            }
            Spacer()
            Text(keys)
                .font(.system(.body, design: .monospaced))
                .multilineTextAlignment(.trailing)
                .padding(.horizontal, 8)
                .padding(.vertical, 4)
                .background(.quaternary, in: RoundedRectangle(cornerRadius: 6))
                .foregroundStyle(.secondary)
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(label), \(keys)")
    }

    private func apply(_ binding: HotKeyBinding, for action: HotKeyAction) {
        shortcutError = captureManager.applyCaptureHotKey(binding, for: action)
    }
}
