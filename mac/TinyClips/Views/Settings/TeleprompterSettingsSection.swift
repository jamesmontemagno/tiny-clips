import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct TeleprompterSettingsSection: View {
    @ObservedObject var settings: CaptureSettings

    // Keeps the AppStorage-backed transcript small enough for UserDefaults.
    private static let maximumTranscriptByteCount = 1_000_000

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
        panel.message = "Choose a plain-text file up to 1 MB to use as the transcript."

        guard panel.runModal() == .OK, let url = panel.url else { return }

        Task { @MainActor in
            do {
                settings.teleprompterTranscript = try await Task.detached(priority: .userInitiated) {
                    try Self.transcriptContents(from: url)
                }.value
            } catch {
                NSAlert(error: error).runModal()
            }
        }
    }

    private static func transcriptContents(from url: URL) throws -> String {
        let didAccessSecurityScopedResource = url.startAccessingSecurityScopedResource()
        defer {
            if didAccessSecurityScopedResource {
                url.stopAccessingSecurityScopedResource()
            }
        }

        let resourceValues = try url.resourceValues(forKeys: [.fileSizeKey])
        guard let fileSize = resourceValues.fileSize else {
            throw TranscriptLoadError.unavailableFileSize
        }
        guard fileSize <= maximumTranscriptByteCount else {
            throw TranscriptLoadError.fileTooLarge
        }

        return try String(contentsOf: url)
    }
}

private enum TranscriptLoadError: LocalizedError {
    case fileTooLarge
    case unavailableFileSize

    var errorDescription: String? {
        switch self {
        case .fileTooLarge:
            "The selected transcript is larger than 1 MB."
        case .unavailableFileSize:
            "TinyClips could not determine the selected transcript's size."
        }
    }

    var recoverySuggestion: String? {
        switch self {
        case .fileTooLarge:
            "Choose a plain-text transcript file that is 1 MB or smaller."
        case .unavailableFileSize:
            "Choose a different plain-text transcript file."
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
    @State private var previewOffset: CGFloat = 0
    @State private var lastTimelineDate: Date?

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

    private var currentScrollOffset: CGFloat {
        guard isPreviewing, maximumOffset > 0 else { return 0 }
        return previewOffset.truncatingRemainder(dividingBy: maximumOffset)
    }

    private func advancePreview(to date: Date) {
        guard isScrolling else {
            lastTimelineDate = nil
            return
        }
        defer { lastTimelineDate = date }

        guard let lastTimelineDate else { return }
        let elapsed = max(0, date.timeIntervalSince(lastTimelineDate))
        previewOffset = (previewOffset + CGFloat(elapsed * scrollSpeed))
            .truncatingRemainder(dividingBy: maximumOffset)
    }

    private func togglePreview() {
        if isPreviewing {
            isPreviewing = false
            previewOffset = 0
            lastTimelineDate = nil
        } else {
            guard canScroll else { return }
            previewOffset = 0
            lastTimelineDate = nil
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
                            : scrollSpeed <= 0
                                ? "Set a scroll speed above zero to start the preview."
                                : "The transcript is too short to scroll at the selected panel height. Add more text or choose a smaller panel height."
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
                        .offset(y: -currentScrollOffset)
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
                .onChange(of: context.date) { _, date in
                    advancePreview(to: date)
                }
            }
        }
        .onPreferenceChange(PreviewContentHeightKey.self) { contentHeight = $0 }
        .onChange(of: isScrolling) { _, isScrolling in
            if !isScrolling {
                lastTimelineDate = nil
            }
        }
    }
}

private struct PreviewContentHeightKey: PreferenceKey {
    static var defaultValue: CGFloat = 0

    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = nextValue()
    }
}
