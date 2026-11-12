import SwiftUI

struct GifSettingsSection: View {
    @ObservedObject var settings: CaptureSettings
    let selectedTab: Binding<SettingsTab?>
    let gifMouseClickToggleBinding: Binding<Bool>

    var body: some View {
        SettingsFormSection("GIF Quality", icon: "photo.on.rectangle.angled") {
            SettingsSliderRow(
                title: "Frame rate",
                value: $settings.gifFrameRate,
                range: 5...30,
                step: 1,
                valueText: "\(Int(settings.gifFrameRate)) fps",
                help: "Choose the frame rate for GIF recording."
            )
            SettingsSliderRow(
                title: "Max width",
                value: Binding(
                    get: { Double(settings.gifMaxWidth) },
                    set: { settings.gifMaxWidth = Int($0) }
                ),
                range: 320...1920,
                step: 40,
                valueText: "\(settings.gifMaxWidth) px",
                help: "Limit GIF output width to reduce file size."
            )
        }

        SettingsFormSection("Before Recording", icon: "timer") {
            SettingsToggleRow(
                title: "Show capture picker before recording",
                isOn: $settings.showGifCapturePicker,
                help: "When disabled, GIF recording goes straight to region selection."
            )
            .onChange(of: settings.showGifCapturePicker) { _, isEnabled in
                if !isEnabled {
                    settings.showGifCapturePickerAfterCapture = false
                }
            }

            SettingsToggleRow(
                title: "Countdown before recording",
                isOn: $settings.gifCountdownEnabled,
                help: "Wait before recording starts so you can prepare the screen."
            )
            if settings.gifCountdownEnabled {
                SettingsSliderRow(
                    title: "Countdown duration",
                    value: $settings.gifCountdownDuration.doubleValue,
                    range: 1...10,
                    step: 1,
                    valueText: "\(settings.gifCountdownDuration)s",
                    help: "Set the countdown duration in seconds."
                )
            }
        }

        SettingsFormSection("During Recording", icon: "record.circle") {
            SettingsToggleRow(
                title: "Show capture region during recording",
                isOn: $settings.showRegionIndicator,
                help: "Show a visible border around the selected capture area while recording."
            )
            SettingsToggleRow(
                title: "Prevent display sleep while recording",
                isOn: $settings.preventDisplaySleepWhileRecording,
                help: "Keep the display awake and prevent the screen saver while recording video or GIFs.",
                accessibilityHint: "When enabled, TinyClips keeps the display awake while recording video or GIFs."
            )
            SettingsToggleRow(
                title: "Show mouse clicks in recording",
                description: settings.gifMouseClicksUseVideoSettings
                    ? "Mirrors the Video setting."
                    : nil,
                isOn: gifMouseClickToggleBinding,
                help: settings.gifMouseClicksUseVideoSettings
                    ? "Uses the Video mouse click on/off setting for GIF recordings."
                    : "Adds a subtle pulse at click positions in saved GIF recordings.",
                accessibilityHint: settings.gifMouseClicksUseVideoSettings
                    ? "When enabled, GIF recordings use the same mouse click visibility setting as Video recordings."
                    : "When enabled, mouse clicks are shown as a pulse effect in saved GIF recordings."
            )

            SettingsActionRow(title: "Mouse click effect") {
                Button("Customize…") {
                    selectedTab.wrappedValue = .mouseClicks
                }
                .accessibilityLabel("Customize mouse click effect")
            }
        }

        SettingsFormSection("After Recording", icon: "arrow.right.square") {
            SettingsToggleRow(
                title: "Show capture picker after recording",
                isOn: $settings.showGifCapturePickerAfterCapture,
                help: "Reopen the capture picker after each recording so you can quickly start another.",
                disabled: !settings.showGifCapturePicker
            )

            SettingsToggleRow(
                title: "Open trimmer after recording",
                isOn: $settings.showGifTrimmer,
                help: "Open the trimmer when recording ends so you can trim before saving."
            )
            .onChange(of: settings.showGifTrimmer) { _, isEnabled in
                if !isEnabled {
                    settings.saveImmediatelyGif = true
                }
            }

            SettingsToggleRow(
                title: "Save immediately",
                isOn: $settings.saveImmediatelyGif,
                help: "Save immediately instead of waiting for actions in the trimmer.",
                disabled: !settings.showGifTrimmer
            )
            SettingsToggleRow(
                title: "Copy to clipboard",
                isOn: $settings.copyGifToClipboard,
                help: "Copy saved GIFs to the clipboard as a file URL."
            )
        }
    }
}
