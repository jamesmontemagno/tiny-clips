import SwiftUI

struct TeleprompterSettingsSection: View {
    @ObservedObject var settings: CaptureSettings

    private var fontSize: TeleprompterDisplaySize {
        TeleprompterDisplaySize(rawValue: settings.teleprompterFontSize) ?? .medium
    }

    private var panelHeight: TeleprompterDisplaySize {
        TeleprompterDisplaySize(rawValue: settings.teleprompterPanelHeight) ?? .medium
    }

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
                    in: 0...100,
                    step: 1
                )
                .controlSize(.large)
                .frame(height: 28)
                .disabled(!settings.teleprompterEnabled)
                .onAppear {
                    settings.teleprompterScrollSpeed = min(max(settings.teleprompterScrollSpeed, 0), 100)
                }
                .accessibilityLabel("Teleprompter scroll speed")
                .accessibilityValue("\(Int(settings.teleprompterScrollSpeed)) points per second")
                Text("\(Int(settings.teleprompterScrollSpeed)) pt/s")
                    .monospacedDigit()
                    .frame(width: 56, alignment: .trailing)
            }
            .help("Set how fast the transcript scrolls, in points per second.")
        }

        Section("Appearance") {
            Picker("Text size:", selection: $settings.teleprompterFontSize) {
                ForEach(TeleprompterDisplaySize.allCases, id: \.rawValue) { size in
                    Text(size.label).tag(size.rawValue)
                }
            }
            .disabled(!settings.teleprompterEnabled)
            .help("Choose the teleprompter text size.")
            .accessibilityLabel("Teleprompter text size")

            Picker("Panel height:", selection: $settings.teleprompterPanelHeight) {
                ForEach(TeleprompterDisplaySize.allCases, id: \.rawValue) { size in
                    Text(size.label).tag(size.rawValue)
                }
            }
            .disabled(!settings.teleprompterEnabled)
            .help("Choose the teleprompter panel height.")
            .accessibilityLabel("Teleprompter panel height")
        }

        Section("Preview") {
            TeleprompterScrollPreview(
                transcript: settings.teleprompterTranscript,
                scrollSpeed: settings.teleprompterScrollSpeed,
                fontSize: fontSize.fontSize,
                viewportHeight: panelHeight.viewportHeight
            )
            Text("This preview uses the same size, height, and scroll speed as the recording overlay.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }
}

private struct TeleprompterScrollPreview: View {
    let transcript: String
    let scrollSpeed: Double
    let fontSize: CGFloat
    let viewportHeight: CGFloat

    @State private var contentHeight: CGFloat = 0
    @State private var scrollOffset: CGFloat = 0
    @State private var isPreviewing = false

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
            contentHeight: contentHeight,
            isPreviewing: isPreviewing,
            fontSize: fontSize,
            viewportHeight: viewportHeight
        )
    }

    private var isScrolling: Bool {
        isPreviewing && scrollSpeed > 0 && contentHeight > viewportHeight
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            GeometryReader { proxy in
                Text(previewTranscript)
                    .font(.system(size: fontSize, weight: .medium))
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
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("Teleprompter scroll speed preview")
            .accessibilityValue(
                isScrolling
                    ? "Scrolling at \(Int(scrollSpeed)) points per second"
                    : "Stopped"
            )

            Button(isPreviewing ? "Stop Preview" : "Start Preview") {
                isPreviewing.toggle()
                if !isPreviewing {
                    scrollOffset = 0
                }
            }
            .accessibilityHint(isPreviewing ? "Stops and resets the preview." : "Starts the preview from the beginning.")
        }
        .onPreferenceChange(PreviewContentHeightKey.self) { contentHeight = $0 }
        .task(id: configuration) {
            scrollOffset = 0
            guard isPreviewing, scrollSpeed > 0 else { return }
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
    }
}

private struct PreviewConfiguration: Hashable {
    let transcript: String
    let scrollSpeed: Double
    let contentHeight: CGFloat
    let isPreviewing: Bool
    let fontSize: CGFloat
    let viewportHeight: CGFloat
}

private struct PreviewContentHeightKey: PreferenceKey {
    static var defaultValue: CGFloat = 0

    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = nextValue()
    }
}
