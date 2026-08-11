import SwiftUI

struct TeleprompterSettingsSection: View {
    @ObservedObject var settings: CaptureSettings

    var body: some View {
        Section("Teleprompter") {
            Toggle("Enable teleprompter", isOn: $settings.teleprompterEnabled)
                .help("Show an auto-scrolling transcript overlay while recording video. The overlay is never captured in the recording.")
                .accessibilityHint("When enabled, an auto-scrolling transcript overlay appears during video recordings only.")

            if settings.teleprompterEnabled {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Transcript:")
                    TextEditor(text: $settings.teleprompterTranscript)
                        .font(.system(size: 13))
                        .frame(minHeight: 80, maxHeight: 140)
                        .accessibilityLabel("Teleprompter transcript")
                        .accessibilityHint("Enter the text to scroll during video recordings.")
                    Text("The teleprompter only appears during video recordings, never GIFs or screenshots, and is never captured.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                HStack {
                    Text("Scroll speed:")
                    Slider(
                        value: $settings.teleprompterScrollSpeed,
                        in: 10...200,
                        step: 5
                    )
                    .accessibilityLabel("Teleprompter scroll speed")
                    .accessibilityValue("\(Int(settings.teleprompterScrollSpeed)) points per second")
                    Text("\(Int(settings.teleprompterScrollSpeed)) pt/s")
                        .monospacedDigit()
                        .frame(width: 56, alignment: .trailing)
                }
                .help("Set how fast the transcript scrolls, in points per second.")
            }
        }
    }
}
