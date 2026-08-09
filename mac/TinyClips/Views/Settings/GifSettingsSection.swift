import SwiftUI

struct GifSettingsSection: View {
    @ObservedObject var settings: CaptureSettings
    let selectedTab: Binding<SettingsTab?>
    let gifMouseClickToggleBinding: Binding<Bool>

    var body: some View {
        Section("Capture Settings") {

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
            Toggle("Show capture region during recording", isOn: $settings.showRegionIndicator)
                .help("Show a visible border around the selected capture area while recording.")
            Toggle("Prevent display sleep while recording", isOn: $settings.preventDisplaySleepWhileRecording)
                .help("Keep the display awake and prevent the screen saver while recording video or GIFs.")
                .accessibilityHint("When enabled, TinyClips keeps the display awake while recording video or GIFs.")
            Toggle(
                settings.gifMouseClicksUseVideoSettings
                    ? "Show mouse clicks in recording (mirrors Video)"
                    : "Show mouse clicks in recording",
                isOn: gifMouseClickToggleBinding
            )
            .help(
                settings.gifMouseClicksUseVideoSettings
                    ? "Uses the Video mouse click on/off setting for GIF recordings."
                    : "Adds a subtle pulse at click positions in saved GIF recordings."
            )
            .accessibilityHint(
                settings.gifMouseClicksUseVideoSettings
                    ? "When enabled, GIF recordings use the same mouse click visibility setting as Video recordings."
                    : "When enabled, mouse clicks are shown as a pulse effect in saved GIF recordings."
            )
            Button("Customize mouse click effect…") {
                selectedTab.wrappedValue = .mouseClicks
            }
            .buttonStyle(.link)
        }

        Section("Before Capture") {
            Toggle("Show capture picker before recording", isOn: $settings.showGifCapturePicker)
                .help("When disabled, GIF recording goes straight to region selection.")
                .onChange(of: settings.showGifCapturePicker) { _, isEnabled in
                    if !isEnabled {
                        settings.showGifCapturePickerAfterCapture = false
                    }
                }
            Toggle("Show capture picker after recording", isOn: $settings.showGifCapturePickerAfterCapture)
                .help("Reopen the capture picker after each recording so you can quickly start another.")
                .disabled(!settings.showGifCapturePicker)
        }

        Section("After Capture") {
            Toggle("Open trimmer after recording", isOn: $settings.showGifTrimmer)
                .help("Open the trimmer when recording ends so you can trim before saving.")
                .onChange(of: settings.showGifTrimmer) { _, isEnabled in
                    if !isEnabled {
                        settings.saveImmediatelyGif = true
                    }
                }

            Toggle("Save immediately", isOn: $settings.saveImmediatelyGif)
                .help("Save immediately instead of waiting for actions in the trimmer.")
                .disabled(!settings.showGifTrimmer)
            Toggle("Copy to clipboard", isOn: $settings.copyGifToClipboard)
                .help("Copy saved GIFs to the clipboard as a file URL.")
        }

        Section("Countdown") {
            Toggle("Countdown before recording", isOn: $settings.gifCountdownEnabled)
                .help("Wait before recording starts so you can prepare the screen.")
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
    }
}
