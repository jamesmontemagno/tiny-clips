import SwiftUI

struct ShortcutsSettingsSection: View {
    @ObservedObject var settings: CaptureSettings
    @ObservedObject var captureManager: CaptureManager

    @State private var shortcutError: String?

    var body: some View {
        SettingsFormSection(
            "Global Keyboard Shortcuts",
            icon: "keyboard",
            footer: "These shortcuts work system-wide, even when the menu is closed. At least one modifier key (⌃ ⌥ ⇧ ⌘) is required. Screenshot Region, Screenshot Screen, and Screenshot Window skip the capture picker."
        ) {
            if let shortcutError {
                Label(shortcutError, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .accessibilityElement(children: .combine)
                    .accessibilityLabel("Shortcut error: \(shortcutError)")
            } else if let registrationError = captureManager.hotKeyRegistrationError {
                Label(registrationError, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .accessibilityElement(children: .combine)
                    .accessibilityLabel("Shortcut error: \(registrationError)")
            }

            ForEach(HotKeyAction.allCases, id: \.self) { action in
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

        SettingsFormSection(
            "Fixed Shortcuts",
            icon: "lock",
            footer: "These shortcuts are fixed and cannot be changed."
        ) {
            fixedShortcutRow(label: "Stop Recording", iconName: "stop.circle", keys: "⌘.")
            fixedShortcutRow(label: "Settings", iconName: "gearshape", keys: "⌘,")
            fixedShortcutRow(label: "Quit", iconName: "power", keys: "⌘Q")
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
