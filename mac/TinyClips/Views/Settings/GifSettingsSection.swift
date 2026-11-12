import SwiftUI

struct GifSettingsSection: View {
    @ObservedObject var settings: CaptureSettings
    let selectedTab: Binding<SettingsTab?>
    let gifMouseClickToggleBinding: Binding<Bool>

    var body: some View {
        SettingsFormSection("GIF Quality", icon: "photo.on.rectangle.angled") {
            HStack {
                Text("Frame rate:")
                Slider(value: $settings.gifFrameRate, in: 5...30, step: 1)
                Text("\(Int(settings.gifFrameRate)) fps")
                    .monospacedDigit()
                    .frame(width: 50, alignment: .trailing)
            }
            .help("Choose the frame rate for GIF recording.")
            HStack {
                Text("Max width:")
                Slider(
                    value: Binding(
                        get: { Double(settings.gifMaxWidth) },
                        set: { settings.gifMaxWidth = Int($0) }
                    ),
                    in: 320...1920,
                    step: 40
                )
                Text("\(settings.gifMaxWidth)px")
                    .monospacedDigit()
                    .frame(width: 60, alignment: .trailing)
            }
            .help("Limit GIF output width to reduce file size.")
        }

        SettingsFormSection("Before Recording", icon: "timer") {
            SettingsToggleRow(
                title: "Show capture picker before recording",
                description: "Choose a capture mode before starting a GIF.",
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
                description: "Wait briefly for a countdown before recording.",
                isOn: $settings.gifCountdownEnabled,
                help: "Wait before recording starts so you can prepare the screen."
            )
            if settings.gifCountdownEnabled {
                HStack {
                    Text("Duration:")
                    Slider(
                        value: $settings.gifCountdownDuration.doubleValue,
                        in: 1...10,
                        step: 1
                    )
                    Text("\(settings.gifCountdownDuration)s")
                        .monospacedDigit()
                        .frame(width: 30, alignment: .trailing)
                }
                .help("Set the countdown duration in seconds.")
            }
        }

        SettingsFormSection("During Recording", icon: "record.circle") {
            SettingsToggleRow(
                title: "Show capture region during recording",
                description: "Draw a visible border around the capture area.",
                isOn: $settings.showRegionIndicator,
                help: "Show a visible border around the selected capture area while recording."
            )
            SettingsToggleRow(
                title: "Prevent display sleep while recording",
                description: "Keep the display awake during capture.",
                isOn: $settings.preventDisplaySleepWhileRecording,
                help: "Keep the display awake and prevent the screen saver while recording video or GIFs.",
                accessibilityHint: "When enabled, TinyClips keeps the display awake while recording video or GIFs."
            )
            SettingsToggleRow(
                title: settings.gifMouseClicksUseVideoSettings
                    ? "Show mouse clicks in recording (mirrors Video)"
                    : "Show mouse clicks in recording",
                description: settings.gifMouseClicksUseVideoSettings
                    ? "GIF clicks follow the Video mouse click setting."
                    : "Add a subtle pulse at click positions.",
                isOn: gifMouseClickToggleBinding,
                help: settings.gifMouseClicksUseVideoSettings
                    ? "Uses the Video mouse click on/off setting for GIF recordings."
                    : "Adds a subtle pulse at click positions in saved GIF recordings.",
                accessibilityHint: settings.gifMouseClicksUseVideoSettings
                    ? "When enabled, GIF recordings use the same mouse click visibility setting as Video recordings."
                    : "When enabled, mouse clicks are shown as a pulse effect in saved GIF recordings."
            )
            Button("Customize mouse click effect…") {
                selectedTab.wrappedValue = .mouseClicks
            }
            .buttonStyle(.link)
        }

        SettingsFormSection("After Recording", icon: "arrow.right.square") {
            SettingsToggleRow(
                title: "Show capture picker after recording",
                description: "Return to the capture picker for another GIF.",
                isOn: $settings.showGifCapturePickerAfterCapture,
                help: "Reopen the capture picker after each recording so you can quickly start another.",
                disabled: !settings.showGifCapturePicker
            )

            SettingsToggleRow(
                title: "Open trimmer after recording",
                description: "Open the GIF trimmer when recording ends.",
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
                description: "Save without waiting for trimmer actions.",
                isOn: $settings.saveImmediatelyGif,
                help: "Save immediately instead of waiting for actions in the trimmer.",
                disabled: !settings.showGifTrimmer
            )
            SettingsToggleRow(
                title: "Copy to clipboard",
                description: "Place a copy of the GIF on the clipboard.",
                isOn: $settings.copyGifToClipboard,
                help: "Copy saved GIFs to the clipboard as a file URL."
            )
        }
    }
}
