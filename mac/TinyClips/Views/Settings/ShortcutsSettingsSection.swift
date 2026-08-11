import SwiftUI

struct ShortcutsSettingsSection: View {
    @ObservedObject var settings: CaptureSettings
    @ObservedObject var captureManager: CaptureManager

    @State private var shortcutError: String?

    var body: some View {
        Section("Global Keyboard Shortcuts") {
            Text("These shortcuts work system-wide, even when the menu is closed. At least one modifier key (⌃ ⌥ ⇧ ⌘) is required.")
                .font(.caption)
                .foregroundStyle(.secondary)

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
                    binding: settings.hotKeyBinding(for: action),
                    defaultBinding: HotKeyBinding.defaultBinding(for: action),
                    onBindingRecorded: { apply($0, for: action) }
                )
                .accessibilityLabel("\(action.displayName) keyboard shortcut")
            }
        }

        Section("Fixed Shortcuts") {
            Text("The following shortcuts are fixed and cannot be changed.")
                .font(.caption)
                .foregroundStyle(.secondary)

            fixedShortcutRow(label: "Stop Recording", keys: "⌘.")
            fixedShortcutRow(label: "Settings", keys: "⌘,")
            fixedShortcutRow(label: "Quit", keys: "⌘Q")
        }
    }

    private func fixedShortcutRow(label: String, keys: String) -> some View {
        HStack {
            Text(label)
            Spacer()
            Text(keys)
                .font(.system(.body, design: .monospaced))
                .padding(.horizontal, 8)
                .padding(.vertical, 4)
                .background(.quaternary, in: RoundedRectangle(cornerRadius: 6))
                .foregroundStyle(.secondary)
        }
    }

    private func apply(_ binding: HotKeyBinding, for action: HotKeyAction) {
        shortcutError = captureManager.applyCaptureHotKey(binding, for: action)
    }
}
