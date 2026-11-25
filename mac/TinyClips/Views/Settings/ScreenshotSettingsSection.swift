import SwiftUI

struct ScreenshotSettingsSection: View {
    @ObservedObject var settings: CaptureSettings
    @State private var screenshotOutputRejectionCount = 0

    var body: some View {
        SettingsFormSection("Format", icon: "photo") {
            Picker("Default format", selection: $settings.screenshotFormat) {
                ForEach(ImageFormat.allCases, id: \.rawValue) { format in
                    Text(format.label).tag(format.rawValue)
                }
            }
            .help("Choose the default file format for screenshots.")

            if settings.imageFormat == .jpeg || settings.imageFormat == .webp {
                SettingsSliderRow(
                    title: "\(settings.imageFormat.label) quality",
                    value: $settings.jpegQuality,
                    range: 0.1...1.0,
                    step: 0.05,
                    valueText: "\(Int(settings.jpegQuality * 100))%",
                    help: "Adjust compression quality. Higher values keep more detail but create larger files."
                )
            }

            Picker("Default scale", selection: $settings.screenshotScale) {
                Text("100%").tag(100)
                Text("75%").tag(75)
                Text("50%").tag(50)
                Text("25%").tag(25)
            }
            .help("Resize the saved screenshot relative to captured pixels.")
        }

        SettingsFormSection("Before Capture", icon: "camera.aperture") {
            SettingsToggleRow(
                title: "Show capture picker before screenshot",
                isOn: $settings.showScreenshotCapturePicker,
                help: "When disabled, screenshots go straight to region selection."
            )
            .onChange(of: settings.showScreenshotCapturePicker) { _, isEnabled in
                if !isEnabled {
                    settings.showScreenshotCapturePickerAfterCapture = false
                }
            }

            SettingsToggleRow(
                title: "Countdown before screenshot",
                isOn: $settings.screenshotCountdownEnabled,
                help: "Wait before capturing so you can prepare the screen."
            )
            if settings.screenshotCountdownEnabled {
                SettingsSliderRow(
                    title: "Countdown duration",
                    value: $settings.screenshotCountdownDuration.doubleValue,
                    range: 1...10,
                    step: 1,
                    valueText: "\(settings.screenshotCountdownDuration)s",
                    help: "Set the countdown duration in seconds."
                )
            }
        }

        SettingsFormSection(
            "After Capture",
            icon: "arrow.right.square",
            footer: screenshotOutputRejectionCount > 0 ? CaptureSettings.screenshotOutputRequiredMessage : nil
        ) {
            SettingsToggleRow(
                title: "Show capture picker after screenshot",
                isOn: $settings.showScreenshotCapturePickerAfterCapture,
                help: "Reopen the capture picker after each screenshot so you can quickly take another.",
                disabled: !settings.showScreenshotCapturePicker
            )

            SettingsToggleRow(
                title: "Open editor after capture",
                isOn: screenshotOutputBinding(.openEditor),
                help: "Open the screenshot editor after capture so you can annotate or crop.",
                accessibilityHint: "Open the screenshot editor after capture. At least one screenshot output must stay on."
            )

            SettingsToggleRow(
                title: "Save file to disk",
                isOn: screenshotOutputBinding(.saveToDisk),
                help: "Save screenshots to the selected folder after capture.",
                accessibilityHint: "Save screenshots to the selected folder. At least one screenshot output must stay on."
            )

            Picker("Default editor tool", selection: $settings.screenshotEditorDefaultEditTool) {
                ForEach(EditTool.allCases, id: \.self) { tool in
                    Text(tool.label).tag(tool)
                }
            }
            .help("Choose the tool selected when the screenshot editor opens.")

            SettingsToggleRow(
                title: "Copy to clipboard",
                isOn: screenshotOutputBinding(.copyToClipboard),
                help: "Copy screenshots to the clipboard as an image, whether or not they are saved.",
                accessibilityHint: "Copy screenshots as an image. At least one screenshot output must stay on."
            )
        }

        SettingsAdvancedSection(startsExpanded: settings.excludeClickLightFromScreenshots) {
            SettingsToggleRow(
                title: "Exclude ClickLight overlays",
                description: "Keeps ClickLight rings and other ClickLight overlays out of screenshots. Window captures already include only the selected window.",
                isOn: $settings.excludeClickLightFromScreenshots,
                help: "Exclude ClickLight from region, screen, and scrolling screenshots without adding a delay. Video and GIF recordings are unchanged.",
                accessibilityHint: "Hides ClickLight overlays in screenshots only. Does not change video or GIF recordings."
            )
        }
    }

    private func screenshotOutputBinding(_ output: CaptureSettings.ScreenshotOutput) -> Binding<Bool> {
        settings.screenshotOutputBinding(
            output,
            onRejected: {
                screenshotOutputRejectionCount += 1
                AccessibilityAnnouncementService.shared.announce(
                    CaptureSettings.screenshotOutputRequiredMessage,
                    priority: .high
                )
            },
            onChanged: {
                if screenshotOutputRejectionCount != 0 {
                    screenshotOutputRejectionCount = 0
                }
            }
        )
    }
}
