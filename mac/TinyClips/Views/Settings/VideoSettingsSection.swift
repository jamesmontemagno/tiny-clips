import SwiftUI

struct VideoSettingsSection: View {
    @ObservedObject var settings: CaptureSettings
    let availableMicrophones: [MicrophoneDeviceOption]
    let availableWebcams: [WebcamDeviceOption]
    let selectedTab: Binding<SettingsTab?>

    var body: some View {
        Section("Video Quality") {
            Picker("Frame rate:", selection: $settings.videoFrameRate) {
                Text("24 fps").tag(24)
                Text("30 fps").tag(30)
                Text("60 fps").tag(60)
            }
            .help("Choose the target frame rate for video recordings.")
        }

        Section("Audio") {
            Toggle("Record output audio", isOn: $settings.recordAudio)
                .help("Include the current system output mix in the recording.")
            Text("Output audio records the current system mix. macOS does not provide a separate output-device picker here.")
                .font(.caption)
                .foregroundStyle(.secondary)

            Toggle("Record microphone", isOn: $settings.recordMicrophone)
                .help("Include microphone input in the recording.")

            Picker("Microphone input:", selection: $settings.selectedMicrophoneID) {
                Text("System Default").tag("")
                ForEach(availableMicrophones) { device in
                    Text(device.name).tag(device.id)
                }
            }
            .help("Choose which microphone to use for recordings.")
        }

        Section("Webcam Overlay") {
            Toggle("Enable webcam overlay by default", isOn: $settings.webcamEnabled)
                .help("Show a webcam picture-in-picture overlay while recording.")
                .onChange(of: settings.webcamEnabled) { _, isEnabled in
                    if isEnabled {
                        settings.recordMicrophone = true
                    }
                }

            Picker("Default camera:", selection: $settings.selectedWebcamID) {
                Text("System Default").tag("")
                ForEach(availableWebcams) { device in
                    Text(device.name).tag(device.id)
                }
            }
            .help("Choose which webcam to use for the overlay.")

            Picker("Webcam shape:", selection: $settings.webcamShape) {
                Text("Circle").tag("circle")
                Text("Rounded rectangle").tag("rounded")
                Text("Rectangle").tag("rectangle")
            }
            .help("Choose the webcam overlay shape.")

            Picker("Webcam corner:", selection: $settings.webcamCorner) {
                Text("Top left").tag("topLeft")
                Text("Top right").tag("topRight")
                Text("Bottom left").tag("bottomLeft")
                Text("Bottom right").tag("bottomRight")
            }
            .help("Choose the corner placement for the webcam overlay.")

            Picker("Webcam size:", selection: $settings.webcamSize) {
                Text("Small").tag("small")
                Text("Medium").tag("medium")
                Text("Large").tag("large")
            }
            .help("Choose the webcam overlay size preset.")
        }

        Section("Effects") {
            Toggle("Show capture region during recording", isOn: $settings.showRegionIndicator)
                .help("Show a visible border around the selected capture area while recording.")

            Toggle("Show mouse clicks in recording", isOn: $settings.showMouseClickVisualsInVideo)
                .help("Adds a subtle pulse at click positions in saved video recordings.")
                .accessibilityHint("When enabled, mouse clicks are shown as a pulse effect in saved video recordings.")
            Button("Customize mouse click effect…") {
                selectedTab.wrappedValue = .mouseClicks
            }
            .buttonStyle(.link)
        }
        
        Section("Before Capture") {
            Toggle("Show capture picker before recording", isOn: $settings.showVideoCapturePicker)
                .help("When disabled, video recording goes straight to region selection.")
                .onChange(of: settings.showVideoCapturePicker) { _, isEnabled in
                    if !isEnabled {
                        settings.showVideoCapturePickerAfterCapture = false
                    }
                }
            Toggle("Show capture picker after recording", isOn: $settings.showVideoCapturePickerAfterCapture)
                .help("Reopen the capture picker after each recording so you can quickly start another.")
                .disabled(!settings.showVideoCapturePicker)
        }

        Section("After Capture") {
            Toggle("Open trimmer after recording", isOn: $settings.showTrimmer)
                .help("Open the trimmer when recording ends so you can trim before saving.")
                .onChange(of: settings.showTrimmer) { _, isEnabled in
                    if !isEnabled {
                        settings.saveImmediatelyVideo = true
                    }
                }

            Toggle("Save immediately", isOn: $settings.saveImmediatelyVideo)
                .help("Save immediately instead of waiting for actions in the trimmer.")
                .disabled(!settings.showTrimmer)
            Toggle("Copy to clipboard", isOn: $settings.copyVideoToClipboard)
                .help("Copy saved videos to the clipboard as a file URL.")
        }

        Section("Countdown") {
            Toggle("Countdown before recording", isOn: $settings.videoCountdownEnabled)
                .help("Wait before recording starts so you can prepare the screen.")
            if settings.videoCountdownEnabled {
                HStack {
                    Text("Duration:")
                    Slider(
                        value: $settings.videoCountdownDuration.doubleValue,
                        in: 1...10,
                        step: 1
                    )
                    Text("\(settings.videoCountdownDuration)s")
                        .monospacedDigit()
                        .frame(width: 30, alignment: .trailing)
                }
                .help("Set the countdown duration in seconds.")
            }
        }
    }
}
