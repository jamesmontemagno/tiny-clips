import SwiftUI

struct VideoSettingsSection: View {
    @ObservedObject var settings: CaptureSettings
    let availableMicrophones: [MicrophoneDeviceOption]
    let availableWebcams: [WebcamDeviceOption]
    let selectedTab: Binding<SettingsTab?>

    var body: some View {
        SettingsFormSection("Video Quality", icon: "4k.tv") {
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
                .settingsDescription()
        }

        SettingsFormSection("Audio", icon: "waveform") {
            SettingsToggleRow(
                title: "Record output audio",
                description: "Include the current system output mix.",
                isOn: $settings.recordAudio,
                help: "Include the current system output mix in the recording."
            )

            SettingsToggleRow(
                title: "Record microphone",
                description: "Include input from the selected microphone.",
                isOn: $settings.recordMicrophone,
                help: "Include microphone input in the recording."
            )

            SettingsToggleRow(
                title: "Limit microphone peaks",
                description: "Reduce loud peaks before encoding.",
                isOn: $settings.microphoneLimiterEnabled,
                help: "Softly compresses loud microphone peaks before encoding to prevent distortion."
            )

            SettingsToggleRow(
                title: "Reduce background and wind noise",
                description: "Use supported macOS noise removal.",
                isOn: $settings.windNoiseRemovalEnabled,
                help: "Uses macOS wind-noise removal when the selected microphone supports it.",
                accessibilityHint: "When enabled, supported microphones reduce background and wind noise before recording."
            )

            Picker("Microphone input:", selection: $settings.selectedMicrophoneID) {
                Text("System Default").tag("")
                ForEach(availableMicrophones) { device in
                    Text(device.name).tag(device.id)
                }
            }
            .help("Choose which microphone to use for recordings.")

        }

        SettingsFormSection("Webcam Overlay", icon: "camera") {
            SettingsToggleRow(
                title: "Enable webcam overlay by default",
                description: "Add a picture-in-picture webcam to recordings.",
                isOn: $settings.webcamEnabled,
                help: "Show a webcam picture-in-picture overlay while recording."
            )
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

        SettingsFormSection("Before Recording", icon: "timer") {
            SettingsToggleRow(
                title: "Show capture picker before recording",
                description: "Choose a capture mode before starting a video.",
                isOn: $settings.showVideoCapturePicker,
                help: "When disabled, video recording goes straight to region selection."
            )
            .onChange(of: settings.showVideoCapturePicker) { _, isEnabled in
                if !isEnabled {
                    settings.showVideoCapturePickerAfterCapture = false
                }
            }

            SettingsToggleRow(
                title: "Countdown before recording",
                description: "Wait briefly for a countdown before recording.",
                isOn: $settings.videoCountdownEnabled,
                help: "Wait before recording starts so you can prepare the screen."
            )
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
                title: "Show mouse clicks in recording",
                description: "Add a subtle pulse at click positions.",
                isOn: $settings.showMouseClickVisualsInVideo,
                help: "Adds a subtle pulse at click positions in saved video recordings.",
                accessibilityHint: "When enabled, mouse clicks are shown as a pulse effect in saved video recordings."
            )
            Button("Customize mouse click effect…") {
                selectedTab.wrappedValue = .mouseClicks
            }
            .buttonStyle(.link)
        }

        SettingsFormSection("After Recording", icon: "arrow.right.square") {
            SettingsToggleRow(
                title: "Show capture picker after recording",
                description: "Return to the capture picker for another video.",
                isOn: $settings.showVideoCapturePickerAfterCapture,
                help: "Reopen the capture picker after each recording so you can quickly start another.",
                disabled: !settings.showVideoCapturePicker
            )

            SettingsToggleRow(
                title: "Open trimmer after recording",
                description: "Open the video trimmer when recording ends.",
                isOn: $settings.showTrimmer,
                help: "Open the trimmer when recording ends so you can trim before saving."
            )
            .onChange(of: settings.showTrimmer) { _, isEnabled in
                if !isEnabled {
                    settings.saveImmediatelyVideo = true
                }
            }

            SettingsToggleRow(
                title: "Save immediately",
                description: "Save without waiting for trimmer actions.",
                isOn: $settings.saveImmediatelyVideo,
                help: "Save immediately instead of waiting for actions in the trimmer.",
                disabled: !settings.showTrimmer
            )
            SettingsToggleRow(
                title: "Copy to clipboard",
                description: "Place a copy of the video on the clipboard.",
                isOn: $settings.copyVideoToClipboard,
                help: "Copy saved videos to the clipboard as a file URL."
            )
        }

        DisclosureGroup("Advanced") {
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
        .settingsCard()
    }
}
