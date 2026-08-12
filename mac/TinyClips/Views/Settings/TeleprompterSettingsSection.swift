import AppKit
import SwiftUI
import UniformTypeIdentifiers

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

            HStack {
                Text("Paste or type the script you want to read while recording.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Spacer()
                Button("Load Text File...") {
                    loadTranscript()
                }
                .disabled(!settings.teleprompterEnabled)
                .help("Replace the transcript with the contents of a plain-text file.")
                .accessibilityLabel("Load teleprompter transcript from text file")
            }
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

    private func loadTranscript() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.plainText]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.canChooseFiles = true
        panel.title = "Load Teleprompter Transcript"
        panel.message = "Choose a plain-text file to use as the transcript."

        guard panel.runModal() == .OK, let url = panel.url else { return }

        let didAccessSecurityScopedResource = url.startAccessingSecurityScopedResource()
        defer {
            if didAccessSecurityScopedResource {
                url.stopAccessingSecurityScopedResource()
            }
        }

        do {
            settings.teleprompterTranscript = try String(contentsOf: url)
        } catch {
            NSAlert(error: error).runModal()
        }
    }
}

private struct TeleprompterScrollPreview: View {
    let transcript: String
    let scrollSpeed: Double
    let fontSize: CGFloat
    let viewportHeight: CGFloat

    @State private var contentHeight: CGFloat = 0
    @State private var isPreviewing = false
    @State private var previewStartedAt: Date?

    private var previewTranscript: String {
        let trimmed = transcript.trimmingCharacters(in: .whitespacesAndNewlines)
        let text = trimmed.isEmpty
            ? "Your transcript will scroll here while you record.\nAdjust the speed until it feels comfortable to read."
            : String(trimmed.prefix(600))
        return Array(repeating: text, count: 3).joined(separator: "\n\n")
    }

    private var maximumOffset: CGFloat {
        max(0, contentHeight - viewportHeight)
    }

    private var canScroll: Bool {
        scrollSpeed > 0 && maximumOffset > 0
    }

    private var isScrolling: Bool {
        isPreviewing && canScroll
    }

    private func scrollOffset(at date: Date) -> CGFloat {
        guard isScrolling, let previewStartedAt else { return 0 }
        let elapsed = max(0, date.timeIntervalSince(previewStartedAt))
        return CGFloat(elapsed * scrollSpeed).truncatingRemainder(dividingBy: maximumOffset)
    }

    private func togglePreview() {
        if isPreviewing {
            isPreviewing = false
            previewStartedAt = nil
        } else {
            guard canScroll else { return }
            previewStartedAt = .now
            isPreviewing = true
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Spacer()
                Button {
                    togglePreview()
                } label: {
                    Label(
                        isPreviewing ? "Stop Preview" : "Start Preview",
                        systemImage: isPreviewing ? "stop.fill" : "play.fill"
                    )
                }
                .buttonStyle(.borderedProminent)
                .disabled(!isPreviewing && !canScroll)
                .help(isPreviewing ? "Stop and reset the preview." : "Start the preview from the beginning.")
                .accessibilityHint(
                    isPreviewing
                        ? "Stops and resets the preview."
                        : canScroll
                            ? "Starts the preview from the beginning."
                            : "Set a scroll speed above zero to start the preview."
                )
            }

            TimelineView(.animation(minimumInterval: 1.0 / 60.0, paused: !isScrolling)) { context in
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
                        .offset(y: -scrollOffset(at: context.date))
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
            }
        }
        .onPreferenceChange(PreviewContentHeightKey.self) { contentHeight = $0 }
    }
}

private struct PreviewContentHeightKey: PreferenceKey {
    static var defaultValue: CGFloat = 0

    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = nextValue()
    }
}
