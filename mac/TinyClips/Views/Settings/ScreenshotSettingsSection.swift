import SwiftUI

struct ScreenshotSettingsSection: View {
    @ObservedObject var settings: CaptureSettings

    var body: some View {
        Section("Capture Settings") {

            Picker("Default format:", selection: $settings.screenshotFormat) {
                ForEach(ImageFormat.allCases, id: \.rawValue) { format in
                    Text(format.label).tag(format.rawValue)
                }
            }
            .help("Choose the default file format for screenshots.")

            if settings.imageFormat == .jpeg {
                HStack {
                    Text("JPEG quality:")
                    Slider(value: $settings.jpegQuality, in: 0.1...1.0, step: 0.05)
                    Text("\(Int(settings.jpegQuality * 100))%")
                        .monospacedDigit()
                        .frame(width: 40, alignment: .trailing)
                }
                .help("Adjust JPEG compression quality. Higher values keep more detail but create larger files.")
            }

            Picker("Default scale:", selection: $settings.screenshotScale) {
                Text("100%").tag(100)
                Text("75%").tag(75)
                Text("50%").tag(50)
                Text("25%").tag(25)
            }
            .help("Resize the saved screenshot relative to captured pixels.")
        }
        
        Section("Picker Settings") {
            Toggle("Show capture picker BEFORE screenshot", isOn: $settings.showScreenshotCapturePicker)
                .help("When disabled, screenshots go straight to region selection.")
                .onChange(of: settings.showScreenshotCapturePicker) { _, isEnabled in
                    if !isEnabled {
                        settings.showScreenshotCapturePickerAfterCapture = false
                    }
                }
            
            Toggle("Show capture picker AFTER screenshot", isOn: $settings.showScreenshotCapturePickerAfterCapture)
                .help("Reopen the capture picker after each screenshot so you can quickly take another.")
                .disabled(!settings.showScreenshotCapturePicker)
        }

        Section("After Capture") {
            Toggle("Open editor after capture", isOn: $settings.showScreenshotEditor)
                .help("Open the screenshot editor after capture so you can annotate or crop.")
                .onChange(of: settings.showScreenshotEditor) { _, isEnabled in
                    if !isEnabled {
                        settings.saveImmediatelyScreenshot = true
                    }
                }

            Toggle("Save immediately", isOn: $settings.saveImmediatelyScreenshot)
                .help("Save immediately instead of waiting for actions in the editor.")
                .disabled(!settings.showScreenshotEditor)

            Toggle("Copy to clipboard", isOn: $settings.copyScreenshotToClipboard)
                .help("Copy saved screenshots to the clipboard as an image.")
        }

        Section("Countdown") {
            Toggle("Countdown before screenshot", isOn: $settings.screenshotCountdownEnabled)
                .help("Wait before capturing so you can prepare the screen.")
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
    }
}
