import SwiftUI

struct ScreenshotSettingsSection: View {
    @ObservedObject var settings: CaptureSettings

    var body: some View {
        SettingsFormSection("Image & Quality", icon: "photo") {
            Picker("Default format:", selection: $settings.screenshotFormat) {
                ForEach(ImageFormat.allCases, id: \.rawValue) { format in
                    Text(format.label).tag(format.rawValue)
                }
            }
            .help("Choose the default file format for screenshots.")

            if settings.imageFormat == .jpeg || settings.imageFormat == .webp {
                HStack {
                    Text("\(settings.imageFormat.label) quality:")
                    Slider(value: $settings.jpegQuality, in: 0.1...1.0, step: 0.05)
                    Text("\(Int(settings.jpegQuality * 100))%")
                        .monospacedDigit()
                        .frame(width: 40, alignment: .trailing)
                }
                .help("Adjust compression quality. Higher values keep more detail but create larger files.")
            }

            Picker("Default scale:", selection: $settings.screenshotScale) {
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
                description: "Choose a capture mode before taking a screenshot.",
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
                description: "Wait briefly for a countdown before capturing.",
                isOn: $settings.screenshotCountdownEnabled,
                help: "Wait before capturing so you can prepare the screen."
            )
            if settings.screenshotCountdownEnabled {
                HStack {
                    Text("Duration:")
                    Slider(
                        value: $settings.screenshotCountdownDuration.doubleValue,
                        in: 1...10,
                        step: 1
                    )
                    Text("\(settings.screenshotCountdownDuration)s")
                        .monospacedDigit()
                        .frame(width: 30, alignment: .trailing)
                }
                .help("Set the countdown duration in seconds.")
            }
        }

        SettingsFormSection("After Capture", icon: "arrow.right.square") {
            SettingsToggleRow(
                title: "Show capture picker after screenshot",
                description: "Return to the capture picker for another shot.",
                isOn: $settings.showScreenshotCapturePickerAfterCapture,
                help: "Reopen the capture picker after each screenshot so you can quickly take another.",
                disabled: !settings.showScreenshotCapturePicker
            )

            SettingsToggleRow(
                title: "Open editor after capture",
                description: "Open the screenshot editor to annotate or crop.",
                isOn: $settings.showScreenshotEditor,
                help: "Open the screenshot editor after capture so you can annotate or crop."
            )
            .onChange(of: settings.showScreenshotEditor) { _, isEnabled in
                if !isEnabled {
                    settings.saveImmediatelyScreenshot = true
                }
            }

            SettingsToggleRow(
                title: "Save immediately",
                description: "Save without waiting for editor actions.",
                isOn: $settings.saveImmediatelyScreenshot,
                help: "Save immediately instead of waiting for actions in the editor.",
                disabled: !settings.showScreenshotEditor
            )

            SettingsToggleRow(
                title: "Copy to clipboard",
                description: "Place a copy of the image on the clipboard.",
                isOn: $settings.copyScreenshotToClipboard,
                help: "Copy screenshots to the clipboard as an image, even before saving in the editor."
            )
        }

        DisclosureGroup("Advanced") {
            SettingsToggleRow(
                title: "Exclude ClickLight overlays",
                description: "Keep ClickLight rings out of screenshots.",
                isOn: $settings.excludeClickLightFromScreenshots,
                help: "Exclude ClickLight from region, screen, and scrolling screenshots without adding a delay. Video and GIF recordings are unchanged.",
                accessibilityHint: "Hides ClickLight overlays in screenshots only. Does not change video or GIF recordings."
            )

            Text("Keeps ClickLight rings and other ClickLight overlays out of screenshots. Window captures already include only the selected window. No ClickLight shortcut setup is required.")
                .settingsDescription()
        }
        .settingsCard()
    }
}
