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

            Picker("Video codec:", selection: $settings.videoCodec) {
                ForEach(VideoCodec.allCases, id: \.self) { codec in
                    Text(codec.label).tag(codec)
                }
            }
            .help("Choose the video encoder used for MP4 recordings.")

            Text("H.265 / HEVC requires a hardware encoder. TinyClips falls back to H.264 if the encoder is unavailable.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }

        Section("Audio") {
            Toggle("Record output audio", isOn: $settings.recordAudio)
                .help("Include the current system output mix in the recording.")
            Text("Output audio records the current system mix. macOS does not provide a separate output-device picker here.")
                .font(.caption)
                .foregroundStyle(.secondary)

            Toggle("Record microphone", isOn: $settings.recordMicrophone)
                .help("Include microphone input in the recording.")

            Toggle("Limit microphone peaks", isOn: $settings.microphoneLimiterEnabled)
                .help("Softly compresses loud microphone peaks before encoding to prevent distortion.")

            Toggle("Reduce background and wind noise", isOn: $settings.windNoiseRemovalEnabled)
                .help("Uses macOS wind-noise removal when the selected microphone supports it.")
                .accessibilityHint("When enabled, supported microphones reduce background and wind noise before recording.")

            Picker("Microphone input:", selection: $settings.selectedMicrophoneID) {
                Text("System Default").tag("")
                ForEach(availableMicrophones) { device in
                    Text(device.name).tag(device.id)
                }
            }
            .help("Choose which microphone to use for recordings.")

            HStack {
                Stepper(
                    "Audio offset: \(settings.audioOffsetMs > 0 ? "+" : "")\(settings.audioOffsetMs) ms",
                    value: $settings.audioOffsetMs,
                    in: CaptureSettings.audioOffsetRangeMs,
                    step: 10
                )
                .help("Delay all recording audio with a positive value or play it earlier with a negative value.")

                Button("Reset") {
                    settings.audioOffsetMs = 0
                }
                .disabled(settings.audioOffsetMs == 0)
                .accessibilityLabel("Reset audio offset")
                .help("Reset the audio offset to 0 ms.")
            }
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

            Toggle("Prevent display sleep while recording", isOn: $settings.preventDisplaySleepWhileRecording)
                .help("Keep the display awake and prevent the screen saver while recording video or GIFs.")
                .accessibilityHint("When enabled, TinyClips keeps the display awake while recording video or GIFs.")

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
            if settings.studioPreviewEnabled {
                Picker("After recording:", selection: $settings.videoAfterRecording) {
                    Text("Save").tag(VideoAfterRecording.save)
                    Text("Open trimmer").tag(VideoAfterRecording.trimmer)
                    Text("Open in Studio (Preview)").tag(VideoAfterRecording.studio)
                }
                .help("Choose what happens when a video recording ends. Studio keeps the screen and camera as separate layers that you arrange before exporting.")
                .onChange(of: settings.videoAfterRecording) { _, choice in
                    // Studio leaves this alone, as it leaves the trimmer switch: both are what
                    // applies again once Studio is switched off.
                    if choice == .save {
                        settings.saveImmediatelyVideo = true
                    }
                }
            } else {
                Toggle("Open trimmer after recording", isOn: $settings.showTrimmer)
                    .help("Open the trimmer when recording ends so you can trim before saving.")
                    .onChange(of: settings.showTrimmer) { _, isEnabled in
                        if !isEnabled {
                            settings.saveImmediatelyVideo = true
                        }
                    }
            }

            Toggle("Save immediately", isOn: saveImmediatelyShown)
                .help(settings.videoAfterRecording == .trimmer
                    ? "Save immediately instead of waiting for actions in the trimmer."
                    : "Applies when the trimmer opens after a recording. Without the trimmer, a recording is saved as soon as it ends.")
                .disabled(settings.videoAfterRecording != .trimmer)
            Toggle("Copy to clipboard", isOn: $settings.copyVideoToClipboard)
                .help("Copy saved videos to the clipboard as a file URL.")
        }

        // Its switch is always there. The rest of it shows once Studio is switched on.
        StudioSettingsSection(settings: settings)

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

    /// What the Save immediately switch shows. With the trimmer it is the setting. Without it a
    /// recording is saved as soon as it ends, whatever the setting says: the switch is greyed
    /// then and shows on, which is what happens. Nothing is written while it is greyed, so the
    /// setting is as it was when the trimmer is chosen again.
    private var saveImmediatelyShown: Binding<Bool> {
        Binding(
            get: { settings.videoAfterRecording != .trimmer || settings.saveImmediatelyVideo },
            set: { isOn in
                if settings.videoAfterRecording == .trimmer {
                    settings.saveImmediatelyVideo = isOn
                }
            }
        )
    }
}
