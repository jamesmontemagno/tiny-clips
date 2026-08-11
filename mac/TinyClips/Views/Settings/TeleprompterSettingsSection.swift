import SwiftUI

struct TeleprompterSettingsSection: View {
    @ObservedObject var settings: CaptureSettings

    var body: some View {
        Section("Teleprompter") {
            Toggle("Enable teleprompter", isOn: $settings.teleprompterEnabled)
                .help("Show an auto-scrolling transcript overlay while recording video. The overlay is never captured in the recording.")
                .accessibilityHint("When enabled, an auto-scrolling transcript overlay appears during video recordings only.")

            Text("The teleprompter appears during video recordings only and is never included in the captured video.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }

        Section("Transcript") {
            TextEditor(text: $settings.teleprompterTranscript)
                .font(.system(size: 13))
                .frame(minHeight: 120, maxHeight: 180)
                .disabled(!settings.teleprompterEnabled)
                .accessibilityLabel("Teleprompter transcript")
                .accessibilityHint("Enter the text to scroll during video recordings.")

            Text("Paste or type the script you want to read while recording.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }

        Section("Scroll Speed") {
            HStack {
                Slider(
                    value: $settings.teleprompterScrollSpeed,
                    in: 10...200,
                    step: 5
                )
                .disabled(!settings.teleprompterEnabled)
                .accessibilityLabel("Teleprompter scroll speed")
                .accessibilityValue("\(Int(settings.teleprompterScrollSpeed)) points per second")
                Text("\(Int(settings.teleprompterScrollSpeed)) pt/s")
                    .monospacedDigit()
                    .frame(width: 56, alignment: .trailing)
            }
            .help("Set how fast the transcript scrolls, in points per second.")
        }

        Section("Preview") {
            TeleprompterScrollPreview(
                transcript: settings.teleprompterTranscript,
                scrollSpeed: settings.teleprompterScrollSpeed
            )
            Text("This preview uses the same scroll speed as the recording overlay.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }
}

private struct TeleprompterScrollPreview: View {
    let transcript: String
    let scrollSpeed: Double

    @State private var contentHeight: CGFloat = 0
    @State private var scrollOffset: CGFloat = 0

    private let viewportHeight: CGFloat = 140
    private let frameInterval = 1.0 / 60.0

    private var previewTranscript: String {
        let trimmed = transcript.trimmingCharacters(in: .whitespacesAndNewlines)
        let text = trimmed.isEmpty
            ? "Your transcript will scroll here while you record.\nAdjust the speed until it feels comfortable to read."
            : String(trimmed.prefix(600))
        return Array(repeating: text, count: 3).joined(separator: "\n\n")
    }

    private var configuration: PreviewConfiguration {
        PreviewConfiguration(
            transcript: previewTranscript,
            scrollSpeed: scrollSpeed,
            contentHeight: contentHeight
        )
    }

    var body: some View {
        GeometryReader { proxy in
            Text(previewTranscript)
                .font(.system(size: 24, weight: .medium))
                .foregroundStyle(.white)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.horizontal, 20)
                .padding(.vertical, 12)
                .frame(width: proxy.size.width, alignment: .top)
                .background {
                    GeometryReader { contentProxy in
                        Color.clear.preference(
                            key: PreviewContentHeightKey.self,
                            value: contentProxy.size.height
                        )
                    }
                }
                .offset(y: -scrollOffset)
        }
        .frame(height: viewportHeight)
        .clipped()
        .background {
            RoundedRectangle(cornerRadius: 12)
                .fill(Color.black.opacity(0.7))
        }
        .overlay {
            RoundedRectangle(cornerRadius: 12)
                .stroke(Color(nsColor: .separatorColor), lineWidth: 1)
        }
        .onPreferenceChange(PreviewContentHeightKey.self) { contentHeight = $0 }
        .task(id: configuration) {
            scrollOffset = 0
            while !Task.isCancelled {
                do {
                    try await Task.sleep(nanoseconds: 16_666_667)
                } catch {
                    return
                }

                let maximumOffset = max(0, contentHeight - viewportHeight)
                guard maximumOffset > 0 else { continue }
                let nextOffset = scrollOffset + CGFloat(scrollSpeed * frameInterval)
                scrollOffset = nextOffset >= maximumOffset ? 0 : nextOffset
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Teleprompter scroll speed preview")
        .accessibilityValue("Scrolling at \(Int(scrollSpeed)) points per second")
    }
}

private struct PreviewConfiguration: Hashable {
    let transcript: String
    let scrollSpeed: Double
    let contentHeight: CGFloat
}

private struct PreviewContentHeightKey: PreferenceKey {
    static var defaultValue: CGFloat = 0

    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = nextValue()
    }
}
