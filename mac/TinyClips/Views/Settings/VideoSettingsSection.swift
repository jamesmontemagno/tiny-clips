import SwiftUI

struct VideoSettingsSection: View {
    @ObservedObject var settings: CaptureSettings
    let availableMicrophones: [MicrophoneDeviceOption]
    let availableWebcams: [WebcamDeviceOption]
    let selectedTab: Binding<SettingsTab?>

    var body: some View {
        SettingsFormSection(
            "Video Quality",
            icon: "4k.tv",
            footer: "H.265 / HEVC requires a hardware encoder. TinyClips falls back to H.264 if the encoder is unavailable."
        ) {
            Picker("Frame rate", selection: $settings.videoFrameRate) {
                Text("24 fps").tag(24)
                Text("30 fps").tag(30)
                Text("60 fps").tag(60)
            }
            .help("Choose the target frame rate for video recordings.")

            Picker("Video codec", selection: $settings.videoCodec) {
                ForEach(VideoCodec.allCases, id: \.self) { codec in
                    Text(codec.label).tag(codec)
                }
            }
            .help("Choose the video encoder used for MP4 recordings.")
        }

        SettingsFormSection("Audio", icon: "waveform") {
            SettingsToggleRow(
                title: "Record output audio",
                description: "Records the current system mix. macOS has no separate output-device picker.",
                isOn: $settings.recordAudio,
                help: "Include the current system output mix in the recording."
            )

            SettingsToggleRow(
                title: "Record microphone",
                isOn: $settings.recordMicrophone,
                help: "Include microphone input in the recording."
            )

            Picker("Microphone input", selection: $settings.selectedMicrophoneID) {
                Text("System Default").tag("")
                ForEach(availableMicrophones) { device in
                    Text(device.name).tag(device.id)
                }
            }
            .help("Choose which microphone to use for recordings.")

            SettingsToggleRow(
                title: "Limit microphone peaks",
                description: "Softens loud peaks before encoding to prevent distortion.",
                isOn: $settings.microphoneLimiterEnabled,
                help: "Softly compresses loud microphone peaks before encoding to prevent distortion."
            )

            SettingsToggleRow(
                title: "Reduce background and wind noise",
                description: "Uses macOS noise removal when the microphone supports it.",
                isOn: $settings.windNoiseRemovalEnabled,
                help: "Uses macOS wind-noise removal when the selected microphone supports it.",
                accessibilityHint: "When enabled, supported microphones reduce background and wind noise before recording."
            )
        }

        SettingsFormSection("Webcam Overlay", icon: "camera") {
            SettingsToggleRow(
                title: "Enable webcam overlay by default",
                isOn: $settings.webcamEnabled,
                help: "Show a webcam picture-in-picture overlay while recording."
            )
            .onChange(of: settings.webcamEnabled) { _, isEnabled in
                if isEnabled {
                    settings.recordMicrophone = true
                }
            }

            Picker("Camera", selection: $settings.selectedWebcamID) {
                Text("System Default").tag("")
                ForEach(availableWebcams) { device in
                    Text(device.name).tag(device.id)
                }
            }
            .help("Choose which webcam to use for the overlay.")
            .accessibilityLabel("Default camera")

            Picker("Shape", selection: $settings.webcamShape) {
                Text("Circle").tag("circle")
                Text("Rounded rectangle").tag("rounded")
                Text("Rectangle").tag("rectangle")
            }
            .help("Choose the webcam overlay shape.")
            .accessibilityLabel("Webcam shape")

            Picker("Corner", selection: $settings.webcamCorner) {
                Text("Top left").tag("topLeft")
                Text("Top right").tag("topRight")
                Text("Bottom left").tag("bottomLeft")
                Text("Bottom right").tag("bottomRight")
            }
            .help("Choose the corner placement for the webcam overlay.")
            .accessibilityLabel("Webcam corner")

            Picker("Size", selection: $settings.webcamSize) {
                Text("Small").tag("small")
                Text("Medium").tag("medium")
                Text("Large").tag("large")
            }
            .help("Choose the webcam overlay size preset.")
            .accessibilityLabel("Webcam size")
        }

        SettingsFormSection("Before Recording", icon: "timer") {
            SettingsToggleRow(
                title: "Show capture picker before recording",
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
                isOn: $settings.videoCountdownEnabled,
                help: "Wait before recording starts so you can prepare the screen."
            )
            if settings.videoCountdownEnabled {
                SettingsSliderRow(
                    title: "Countdown duration",
                    value: $settings.videoCountdownDuration.doubleValue,
                    range: 1...10,
                    step: 1,
                    valueText: "\(settings.videoCountdownDuration)s",
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
                isOn: $settings.showMouseClickVisualsInVideo,
                help: "Adds a subtle pulse at click positions in saved video recordings.",
                accessibilityHint: "When enabled, mouse clicks are shown as a pulse effect in saved video recordings."
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
                isOn: $settings.showVideoCapturePickerAfterCapture,
                help: "Reopen the capture picker after each recording so you can quickly start another.",
                disabled: !settings.showVideoCapturePicker
            )

            SettingsToggleRow(
                title: "Open trimmer after recording",
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
                isOn: $settings.saveImmediatelyVideo,
                help: "Save immediately instead of waiting for actions in the trimmer.",
                disabled: !settings.showTrimmer
            )
            SettingsToggleRow(
                title: "Copy to clipboard",
                isOn: $settings.copyVideoToClipboard,
                help: "Copy saved videos to the clipboard as a file URL."
            )
        }

        SettingsAdvancedSection(startsExpanded: settings.audioOffsetMs != 0) {
            LabeledContent {
                HStack(spacing: 8) {
                    Text("\(settings.audioOffsetMs > 0 ? "+" : "")\(settings.audioOffsetMs) ms")
                        .monospacedDigit()
                        .foregroundStyle(.secondary)
                        .accessibilityHidden(true)

                    Stepper(
                        "Audio offset",
                        value: $settings.audioOffsetMs,
                        in: CaptureSettings.audioOffsetRangeMs,
                        step: 10
                    )
                    .labelsHidden()
                    .accessibilityValue("\(settings.audioOffsetMs) milliseconds")
                    .help("Delay all recording audio with a positive value or play it earlier with a negative value.")

                    Button("Reset") {
                        settings.audioOffsetMs = 0
                    }
                    .disabled(settings.audioOffsetMs == 0)
                    .accessibilityLabel("Reset audio offset")
                    .help("Reset the audio offset to 0 ms.")
                }
            } label: {
                Text("Audio offset")
                Text("A positive value delays recorded audio. A negative value plays it earlier.")
            }
        }
    }
}
