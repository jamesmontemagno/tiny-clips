import CoreGraphics
import SwiftUI

// MARK: - What the Panel Shows

/// A cue as the list in the Captions panel shows it, with its place in the project's list.
struct StudioCaptionRow: Identifiable, Equatable {
    let index: Int
    let cue: StudioCaptionCue

    var id: Int { index }
}

extension StudioCaptionRow: StudioCaptionTimed {
    var start: Double { cue.start }
    var end: Double { cue.end }
}

/// What the Captions panel says about the last thing done to the captions.
struct StudioCaptionNotice: Identifiable, Equatable {
    enum Kind: Equatable {
        /// Something went as asked. It goes after a while.
        case done
        /// Something was stopped as asked. It goes after a while.
        case info
        /// Generating heard nobody speak. It stays, with a way to try another language.
        case noSpeech
        /// Something went wrong. It stays until it is closed.
        case problem
    }

    /// How long one that goes by itself stays.
    static let shownFor: Duration = .seconds(6)

    let id = UUID()
    let kind: Kind
    let title: String?
    let message: String

    init(_ kind: Kind, _ message: String, title: String? = nil) {
        self.kind = kind
        self.message = message
        self.title = title
    }

    /// What VoiceOver says for it.
    var spokenText: String {
        title.map { "\($0). \(message)" } ?? message
    }
}

/// Finding words in captions, as the search box of the Captions panel does: ignoring case,
/// accents, and the width of East Asian letters.
enum StudioCaptionSearch {
    private static let options: String.CompareOptions = [.caseInsensitive, .diacriticInsensitive, .widthInsensitive]

    /// What is searched for: what was typed, without spaces around it.
    static func normalized(_ query: String) -> String {
        query.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func matches(_ text: String, query: String) -> Bool {
        !query.isEmpty && text.range(of: query, options: options, locale: .current) != nil
    }

    /// Where `query` is in `text`, from the start, without overlaps.
    static func ranges(of query: String, in text: String) -> [Range<String.Index>] {
        guard !query.isEmpty else { return [] }
        var found: [Range<String.Index>] = []
        var from = text.startIndex
        while from < text.endIndex,
              let range = text.range(of: query, options: options, range: from..<text.endIndex, locale: .current) {
            found.append(range)
            from = range.isEmpty ? text.index(after: range.lowerBound) : range.upperBound
        }
        return found
    }

    /// `text` with what was searched for marked.
    static func highlighted(_ text: String, query: String) -> AttributedString {
        var marked = AttributedString(text)
        for range in ranges(of: query, in: text) {
            guard let lower = AttributedString.Index(range.lowerBound, within: marked),
                  let upper = AttributedString.Index(range.upperBound, within: marked) else { continue }
            marked[lower..<upper].backgroundColor = Color.yellow.opacity(0.45)
            marked[lower..<upper].inlinePresentationIntent = .stronglyEmphasized
        }
        return marked
    }
}

/// What only the Mac's Captions panel says. What both editors say is in `StudioCaptionText`.
enum StudioCaptionPanelText {
    static let noSpeechTitle = "No Speech Found"
    static let tryAnotherLanguage = "Try Another Language\u{2026}"
    static let deletedMessage = "Caption deleted."
    static let splitMessage = "Caption split at the playhead."
    static let mergedMessage = "Captions merged."

    /// Said when generating heard nobody speak, in the language it listened for when it is known.
    static func noSpeechMessage(languageName: String?, hasCaptions: Bool) -> String {
        let heard = languageName.map { "No speech was found in this recording when listening for \($0)." }
            ?? "No speech was found in this recording."
        let message = "\(heard) If people speak another language in it, try that one."
        return hasCaptions ? "\(message) The captions are as they were." : message
    }

    /// The title of the notification sent when generating ends while Studio is behind.
    static func notificationTitle(for notice: StudioCaptionNotice) -> String {
        switch notice.kind {
        case .done: return "Captions Ready"
        case .info: return "Captions"
        case .noSpeech: return notice.title ?? noSpeechTitle
        case .problem: return "Captions Not Generated"
        }
    }

    static func notificationMessage(for notice: StudioCaptionNotice, clipName: String) -> String {
        clipName.isEmpty ? notice.message : "\(clipName): \(notice.message)"
    }

    /// What the checks found in one cue.
    static func problemText(_ kind: StudioCaptionFindingKind) -> String {
        switch kind {
        case .invalidTiming: return "Invalid time"
        case .overlap: return "Overlaps the caption before"
        case .empty: return "Empty"
        case .whitespace: return "Extra spaces"
        case .longLine: return "Line too long"
        case .tooManyLines: return "Too many lines"
        case .shortDuration: return "Too short to read"
        case .readingSpeed: return "Too fast to read"
        }
    }

    static func problemsText(_ kinds: [StudioCaptionFindingKind]) -> String {
        kinds.map(problemText).joined(separator: ", ")
    }

    /// A time as a clock shows it, to a tenth of a second: `1:05.3`.
    static func clockText(_ seconds: Double) -> String {
        // Capped like `formattedTime`: a hand-edited cue time past Int.max would trap.
        let clamped = seconds.isFinite ? min(max(0, seconds), 359_999) : 0
        let tenths = Int((clamped * 10).rounded())
        return String(format: "%d:%04.1f", tenths / 600, Double(tenths % 600) / 10)
    }

    /// When a cue starts and ends, as a clock shows it: `0:12.0 – 0:16.5`.
    static func clockRangeText(_ cue: StudioCaptionCue) -> String {
        "\(clockText(cue.start)) \u{2013} \(clockText(cue.end))"
    }

    /// How many cues a search found: `3 of 12`.
    static func searchCountText(found: Int, total: Int) -> String {
        "\(found) of \(total)"
    }

    /// Which cue the editor has open, in time order from 1: `Caption 3 of 12`.
    static func positionText(_ position: Int, of total: Int) -> String {
        "Caption \(position) of \(total)"
    }

    static func noMatchesText(_ query: String) -> String {
        "No captions contain \u{201C}\(query)\u{201D}."
    }

    static func lookName(_ appearance: StudioCaptionAppearance) -> String {
        switch appearance {
        case .classicOutline: return "Classic outline"
        case .contrastPanel: return "Contrast panel"
        case .boldImpact: return "Bold impact"
        case .cleanShadow: return "Clean shadow"
        }
    }
}

/// The small pictures of each look in the Captions panel, drawn by the painter that draws captions
/// into the video, so each shows what export makes.
@MainActor
enum StudioCaptionLookTile {
    static let size = CGSize(width: 140, height: 52)
    static let scale = 2.0
    static let sampleText = "Your captions"

    private static var images: [StudioCaptionAppearance: CGImage] = [:]

    static func image(for appearance: StudioCaptionAppearance) -> CGImage? {
        if let image = images[appearance] { return image }
        guard let image = draw(appearance) else { return nil }
        images[appearance] = image
        return image
    }

    private static func draw(_ appearance: StudioCaptionAppearance) -> CGImage? {
        let width = Int(size.width * scale)
        let height = Int(size.height * scale)
        // A canvas whose short side makes large captions about 13 points tall on the tile.
        let canvas = 312
        guard let picture = StudioCaptionPainter.picture(
            text: sampleText,
            overlay: StudioCaptionOverlay(enabled: true, size: .large, position: .bottom, appearance: appearance),
            language: nil,
            canvasWidth: canvas,
            canvasHeight: canvas
        ), let space = CGColorSpace(name: CGColorSpace.sRGB), let context = CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: space,
            bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue
        ) else {
            return nil
        }
        // A picture of light and dark, as a video has, so outlines and shadows show.
        let colors = [
            CGColor(srgbRed: 0.55, green: 0.63, blue: 0.74, alpha: 1),
            CGColor(srgbRed: 0.24, green: 0.29, blue: 0.38, alpha: 1),
            CGColor(srgbRed: 0.10, green: 0.12, blue: 0.17, alpha: 1),
        ] as CFArray
        if let gradient = CGGradient(colorsSpace: space, colors: colors, locations: [0, 0.55, 1]) {
            context.drawLinearGradient(
                gradient,
                start: CGPoint(x: 0, y: CGFloat(height)),
                end: CGPoint(x: CGFloat(width), y: 0),
                options: []
            )
        }
        let band = picture.image
        context.draw(band, in: CGRect(
            x: (CGFloat(width) - CGFloat(band.width)) / 2,
            y: (CGFloat(height) - CGFloat(band.height)) / 2,
            width: CGFloat(band.width),
            height: CGFloat(band.height)
        ))
        return context.makeImage()
    }
}

// MARK: - Captions Inspector

/// The Captions panel of the inspector. At the top, how many there are, what was last done, and
/// making, importing, and exporting them. Then how they are drawn into the video, every cue in
/// time order with a search, and what the checks find. The cue at the playhead is marked, and the
/// list follows it. Choosing a cue opens it in place to edit its text and timing; its text goes
/// into the project when the box is left, so typing is one undo step.
struct StudioCaptionsInspectorSection: View {
    @ObservedObject var viewModel: StudioViewModel
    @AppStorage(CaptureSettings.studioCaptionLanguageKey) private var languageID = ""
    @State private var isChoosingLanguage = false
    @State private var languages: [StudioCaptionLanguage] = []
    @State private var systemLanguage: String?
    @State private var query = ""
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private static let looks: [StudioCaptionAppearance] = [.classicOutline, .contrastPanel, .boldImpact, .cleanShadow]
    private static let lookColumns = [GridItem(.flexible(), spacing: 8), GridItem(.flexible(), spacing: 8)]

    private var language: StudioCaptionLanguage? {
        StudioCaptionLanguages.choose(stored: languageID, systemLanguage: systemLanguage, from: languages)
    }

    private var isLanguageReady: Bool {
        language?.isInstalled ?? true
    }

    private var motion: Animation? {
        reduceMotion ? nil : .easeOut(duration: 0.2)
    }

    private func loadLanguages() async {
        languages = await StudioAppleSpeech.languages()
        systemLanguage = await StudioAppleSpeech.language(equivalentTo: nil)
    }

    var body: some View {
        let rows = viewModel.captionRows
        VStack(alignment: .leading, spacing: 10) {
            header(count: rows.count)
            if let notice = viewModel.captionNotice {
                StudioCaptionNoticeBanner(
                    notice: notice,
                    tryAnotherLanguage: StudioAppleSpeech.isAvailable && viewModel.canGenerateCaptions
                        ? { isChoosingLanguage = true }
                        : nil,
                    dismiss: { viewModel.dismissCaptionNotice() }
                )
                .transition(.opacity.combined(with: .move(edge: .top)))
            }
            if viewModel.isGeneratingCaptions {
                generationProgress
                    .transition(.opacity)
            } else if StudioAppleSpeech.isAvailable {
                generateButton(isFirst: rows.isEmpty)
            }
            fileButtons
            footnote(StudioCaptionText.exportNote)
        }
        .animation(motion, value: viewModel.captionNotice)
        .animation(motion, value: viewModel.isGeneratingCaptions)
        if !rows.isEmpty {
            drawing
            checks
            StudioInspectorSection(title: "All Captions") {
                transcript(all: rows)
            }
        }
    }

    // MARK: Header

    private func header(count: Int) -> some View {
        HStack(alignment: .center, spacing: 10) {
            Image(systemName: StudioInspectorPanel.captions.symbolName)
                .font(.title3)
                .foregroundStyle(Color.accentColor)
                .frame(width: 34, height: 34)
                .background(Color.accentColor.opacity(0.14), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text(StudioCaptionText.countText(count))
                    .font(.headline)
                if count == 0 {
                    Text(StudioAppleSpeech.isAvailable ? StudioCaptionText.emptyHint : StudioCaptionText.needsNewerMac)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                } else if let name = captionLanguageName {
                    Label(name, systemImage: "globe")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .accessibilityLabel("Language: \(name)")
                }
            }
            Spacer(minLength: 0)
        }
        .accessibilityElement(children: .combine)
    }

    private var captionLanguageName: String? {
        viewModel.project?.captions.language.flatMap { Locale.current.localizedString(forIdentifier: $0) }
    }

    // MARK: Generating

    private func generateButton(isFirst: Bool) -> some View {
        Group {
            if isFirst {
                generateLabel(title: "Generate Captions\u{2026}")
                    .buttonStyle(.borderedProminent)
            } else {
                generateLabel(title: "Generate Again\u{2026}")
                    .buttonStyle(.bordered)
            }
        }
        .controlSize(.large)
        .disabled(!viewModel.canGenerateCaptions)
        .help("Make captions from the recording's sound, on this Mac")
        .popover(isPresented: $isChoosingLanguage, arrowEdge: .bottom) {
            languagePopover
        }
    }

    private func generateLabel(title: String) -> some View {
        Button {
            isChoosingLanguage = true
        } label: {
            Label(title, systemImage: StudioSymbol.generateCaptions)
                .frame(maxWidth: .infinity)
        }
    }

    private var languagePopover: some View {
        VStack(alignment: .leading, spacing: 12) {
            Label("Generate Captions", systemImage: StudioSymbol.generateCaptions)
                .font(.headline)
            Picker("Language", selection: Binding(get: { language?.id ?? "" }, set: { languageID = $0 })) {
                ForEach(languages) { language in
                    Text(language.name).tag(language.id)
                }
            }
            .disabled(languages.isEmpty)
            .help("Choose the language spoken in the recording. It is not detected.")
            VStack(alignment: .leading, spacing: 6) {
                footnote(StudioCaptionText.languageNote(isInstalled: isLanguageReady))
                footnote(StudioCaptionText.privacyNote)
                if !(viewModel.project?.captions.cues.isEmpty ?? true) {
                    footnote(StudioCaptionText.replaceNote)
                }
            }
            HStack {
                Spacer()
                Button("Cancel") {
                    isChoosingLanguage = false
                }
                .keyboardShortcut(.cancelAction)
                Button(StudioCaptionText.generateActionText(isInstalled: isLanguageReady)) {
                    isChoosingLanguage = false
                    viewModel.generateCaptions(language: language?.id)
                }
                .keyboardShortcut(.defaultAction)
                .disabled(language == nil || !viewModel.canGenerateCaptions)
            }
        }
        .padding()
        .frame(width: 300)
        .task {
            await loadLanguages()
        }
    }

    private var generationProgress: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Image(systemName: StudioSymbol.generateCaptions)
                    .foregroundStyle(Color.accentColor)
                    .symbolEffect(.variableColor.iterative, options: .repeating, isActive: !reduceMotion)
                    .accessibilityHidden(true)
                Text(viewModel.captionProgressText)
                    .font(.callout.monospacedDigit())
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 4)
                Button("Cancel") {
                    viewModel.cancelCaptionGeneration()
                }
                .controlSize(.small)
                .disabled(!viewModel.canCancelCaptionGeneration)
                .help("Stop generating. The captions stay as they were.")
            }
            Group {
                if !viewModel.isStoppingCaptionGeneration, let fraction = viewModel.captionGenerationProgress.fraction {
                    ProgressView(value: fraction)
                } else {
                    ProgressView()
                }
            }
            .progressViewStyle(.linear)
            .accessibilityLabel("Generating captions")
        }
        .padding(10)
        .background(StudioCaptionCard.fill, in: StudioCaptionCard.shape)
        .overlay(StudioCaptionCard.shape.strokeBorder(StudioCaptionCard.stroke))
    }

    // MARK: Files

    private var fileButtons: some View {
        HStack(spacing: 8) {
            Menu {
                Button("Timed to the Recording") {
                    viewModel.importCaptions(timing: .recording)
                }
                Button("Timed to the Edited Video") {
                    viewModel.importCaptions(timing: .editedVideo)
                }
            } label: {
                Label("Import", systemImage: StudioSymbol.importCaptions)
            }
            .frame(maxWidth: .infinity)
            .disabled(!viewModel.isEditable)
            .help("Replace the captions with an SRT or WebVTT file")

            Menu {
                Button("SubRip (.srt)") {
                    viewModel.exportCaptions(.srt)
                }
                Button("WebVTT (.vtt)") {
                    viewModel.exportCaptions(.vtt)
                }
            } label: {
                Label("Export", systemImage: StudioSymbol.exportCaptions)
            }
            .frame(maxWidth: .infinity)
            .disabled(!viewModel.canExportCaptions)
            .help("Write the video's captions to a file")
        }
    }

    // MARK: In the Video

    /// Whether, and how, the captions are drawn into the picture.
    private var drawing: some View {
        let overlay = viewModel.project?.overlays.captions ?? StudioCaptionOverlay()
        return StudioInspectorSection(title: "In the Video") {
            Toggle("Draw captions on the video", isOn: Binding(
                get: { overlay.enabled },
                set: { viewModel.setCaptionsDrawn($0) }
            ))
            .toggleStyle(.checkbox)
            .disabled(!viewModel.isEditable)
            .help("Shows them in the preview, and puts them into the picture of what you export")
            if overlay.enabled {
                LazyVGrid(columns: Self.lookColumns, spacing: 8) {
                    ForEach(Self.looks, id: \.self) { look in
                        StudioCaptionLookButton(appearance: look, isSelected: overlay.appearance == look) {
                            viewModel.setCaptionAppearance(look)
                        }
                    }
                }
                .disabled(!viewModel.isEditable)
                .accessibilityElement(children: .contain)
                .accessibilityLabel("Look")
                Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 8) {
                    GridRow {
                        Text("Size")
                            .foregroundStyle(.secondary)
                        Picker("Size", selection: Binding(
                            get: { overlay.size },
                            set: { viewModel.setCaptionSize($0) }
                        )) {
                            Text("Small").tag(StudioCaptionSize.small)
                            Text("Medium").tag(StudioCaptionSize.medium)
                            Text("Large").tag(StudioCaptionSize.large)
                        }
                        .labelsHidden()
                        .pickerStyle(.segmented)
                    }
                    GridRow {
                        Text("Position")
                            .foregroundStyle(.secondary)
                        Picker("Position", selection: Binding(
                            get: { overlay.position },
                            set: { viewModel.setCaptionPosition($0) }
                        )) {
                            Text("Bottom").tag(StudioCaptionPosition.bottom)
                            Text("Top").tag(StudioCaptionPosition.top)
                        }
                        .labelsHidden()
                        .pickerStyle(.segmented)
                    }
                }
                .disabled(!viewModel.isEditable)
                .transition(.opacity)
            }
            footnote(StudioCaptionText.drawnNote)
        }
        .animation(motion, value: overlay.enabled)
    }

    // MARK: All Captions

    private func transcript(all: [StudioCaptionRow]) -> some View {
        let search = StudioCaptionSearch.normalized(query)
        let rows = viewModel.captionRows(matching: search)
        let playing = viewModel.captionIndexAtPlayhead
        let selected = viewModel.selectedCaptionIndex
        let position = viewModel.selectedCaptionRowPosition
        return VStack(alignment: .leading, spacing: 8) {
            // The open cue is drawn above the list, not in it. Its text box, steppers, and buttons
            // are AppKit controls, and in a lazy stack inside a second scroll view they stopped
            // taking clicks.
            if let position, all.indices.contains(position) {
                let row = all[position]
                StudioCaptionEditorCard(
                    viewModel: viewModel,
                    row: row,
                    position: position,
                    total: all.count,
                    problems: viewModel.captionProblems(at: row.index),
                    isAtPlayhead: playing == row.index
                )
                // A card of its own for each cue, so what was typed for one is not shown in the next.
                .id(row.index)
                .transition(.opacity)
            }
            searchField(found: rows.count, total: all.count, isSearching: !search.isEmpty)
            if rows.isEmpty {
                Text(StudioCaptionPanelText.noMatchesText(search))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 16)
            } else {
                ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(alignment: .leading, spacing: 2) {
                            ForEach(rows) { row in
                                StudioCaptionRowButton(
                                    row: row,
                                    query: search,
                                    problems: viewModel.captionProblems(at: row.index),
                                    isAtPlayhead: playing == row.index,
                                    isSelected: selected == row.index
                                ) {
                                    viewModel.selectCaption(at: row.index)
                                }
                                .equatable()
                                .id(row.index)
                            }
                        }
                        .padding(4)
                    }
                    // Shorter while a cue is open, so the card and the list fit together.
                    .frame(maxHeight: selected == nil ? 420 : 240)
                    .background(StudioCaptionCard.fill, in: StudioCaptionCard.shape)
                    .overlay(StudioCaptionCard.shape.strokeBorder(StudioCaptionCard.stroke))
                    .onChange(of: selected) { _, index in
                        guard let index else { return }
                        withAnimation(motion) { proxy.scrollTo(index) }
                    }
                    .onChange(of: playing) { _, index in
                        // The list follows the video, but not away from a caption being edited.
                        guard let index, selected == nil else { return }
                        withAnimation(motion) { proxy.scrollTo(index) }
                    }
                }
                .accessibilityElement(children: .contain)
                .accessibilityLabel("Captions")
            }
        }
        .animation(motion, value: selected)
    }

    private func searchField(found: Int, total: Int, isSearching: Bool) -> some View {
        HStack(spacing: 6) {
            Image(systemName: StudioSymbol.searchCaptions)
                .foregroundStyle(.secondary)
                .accessibilityHidden(true)
            TextField("Search captions", text: $query)
                .textFieldStyle(.plain)
                .onExitCommand { query = "" }
                .accessibilityLabel("Search captions")
            if isSearching {
                Text(StudioCaptionPanelText.searchCountText(found: found, total: total))
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
                    .accessibilityLabel("\(found) of \(total) captions found")
                Button {
                    query = ""
                } label: {
                    Image(systemName: "xmark.circle.fill")
                }
                .buttonStyle(.plain)
                .foregroundStyle(.tertiary)
                .help("Clear the search")
                .accessibilityLabel("Clear search")
            }
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 6)
        .background(StudioCaptionCard.fill, in: StudioCaptionCard.shape)
        .overlay(StudioCaptionCard.shape.strokeBorder(StudioCaptionCard.stroke))
    }

    // MARK: Checks

    private var checks: some View {
        let findings = viewModel.captionFindings
        let isClean = findings.isEmpty
        let fixable = findings.filter(\.canFix).count
        return StudioInspectorSection(title: "Checks") {
            VStack(alignment: .leading, spacing: 8) {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Image(systemName: isClean ? "checkmark.seal.fill" : StudioSymbol.captionProblem)
                        .foregroundStyle(isClean ? Color.green : Color.orange)
                        .accessibilityHidden(true)
                    Text(isClean ? StudioCaptionText.noProblems : StudioCaptionQuality.summary(findings))
                        .font(.callout)
                        .fixedSize(horizontal: false, vertical: true)
                    Spacer(minLength: 0)
                }
                if !isClean {
                    if let note = StudioCaptionText.manualFixNote(fixable: fixable, total: findings.count) {
                        footnote(note)
                    }
                    HStack(spacing: 8) {
                        if fixable > 0 {
                            Button {
                                viewModel.fixCaptions()
                            } label: {
                                Label("Auto Fix", systemImage: StudioSymbol.autoFixCaptions)
                            }
                            .help("Tidy text, wrap long lines, and give fast captions more time, from the gaps around them or by joining them to a neighbor. Undo takes it back.")
                        }
                        Button {
                            viewModel.selectNextCaptionProblem()
                        } label: {
                            Label("Next Problem", systemImage: StudioSymbol.nextCaptionProblem)
                        }
                        .help("Open the next caption with a problem, to edit it")
                        .accessibilityHint("Opens the next caption the checks found a problem in")
                    }
                    .disabled(!viewModel.isEditable)
                }
            }
            .padding(10)
            .background(StudioCaptionCard.fill, in: StudioCaptionCard.shape)
            .overlay(StudioCaptionCard.shape.strokeBorder(StudioCaptionCard.stroke))
        }
    }

    private func footnote(_ text: String) -> some View {
        Text(text)
            .font(.caption)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
    }
}

/// The rounded box the Captions panel puts its groups in.
private enum StudioCaptionCard {
    static let shape = RoundedRectangle(cornerRadius: 8, style: .continuous)
    static let fill = Color.primary.opacity(0.04)
    static let stroke = Color.primary.opacity(0.08)
}

// MARK: - Pieces

/// What was last done to the captions, with a way to close it.
private struct StudioCaptionNoticeBanner: View {
    let notice: StudioCaptionNotice
    /// Opens the choice of language again, after no speech was found.
    let tryAnotherLanguage: (() -> Void)?
    let dismiss: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: symbol)
                .foregroundStyle(tint)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 4) {
                if let title = notice.title {
                    Text(title)
                        .font(.callout.weight(.semibold))
                }
                Text(notice.message)
                    .font(notice.title == nil ? .callout : .caption)
                    .foregroundStyle(notice.title == nil ? .primary : .secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if notice.kind == .noSpeech, let tryAnotherLanguage {
                    Button(StudioCaptionPanelText.tryAnotherLanguage, action: tryAnotherLanguage)
                        .controlSize(.small)
                        .padding(.top, 2)
                }
            }
            Spacer(minLength: 0)
            Button(action: dismiss) {
                Image(systemName: "xmark")
                    .font(.caption.weight(.semibold))
            }
            .buttonStyle(.plain)
            .foregroundStyle(.secondary)
            .help("Dismiss")
            .accessibilityLabel("Dismiss message")
        }
        .padding(10)
        .background(tint.opacity(0.12), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).strokeBorder(tint.opacity(0.3)))
        .accessibilityElement(children: .contain)
    }

    private var symbol: String {
        switch notice.kind {
        case .done: return "checkmark.circle.fill"
        case .info: return "info.circle.fill"
        case .noSpeech: return StudioSymbol.noSpeech
        case .problem: return StudioSymbol.captionProblem
        }
    }

    private var tint: Color {
        switch notice.kind {
        case .done: return .green
        case .info: return .accentColor
        case .noSpeech: return .orange
        case .problem: return .red
        }
    }
}

/// A look for drawn captions, shown as the painter draws it.
private struct StudioCaptionLookButton: View {
    let appearance: StudioCaptionAppearance
    let isSelected: Bool
    let action: () -> Void
    @State private var isHovered = false

    private var name: String { StudioCaptionPanelText.lookName(appearance) }

    var body: some View {
        Button(action: action) {
            VStack(spacing: 5) {
                Group {
                    if let image = StudioCaptionLookTile.image(for: appearance) {
                        Image(decorative: image, scale: StudioCaptionLookTile.scale)
                            .resizable()
                            .interpolation(.high)
                            .aspectRatio(contentMode: .fill)
                    } else {
                        Color.secondary.opacity(0.3)
                    }
                }
                .frame(height: StudioCaptionLookTile.size.height)
                .frame(maxWidth: .infinity)
                .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                Text(name)
                    .font(.caption)
                    .foregroundStyle(isSelected ? .primary : .secondary)
                    .lineLimit(1)
            }
            .padding(5)
            .background(
                RoundedRectangle(cornerRadius: 9, style: .continuous)
                    .fill(isSelected ? Color.accentColor.opacity(0.16) : isHovered ? Color.primary.opacity(0.06) : Color.clear)
            )
            .overlay(
                RoundedRectangle(cornerRadius: 9, style: .continuous)
                    .strokeBorder(isSelected ? Color.accentColor : Color.clear, lineWidth: 2)
            )
            .contentShape(RoundedRectangle(cornerRadius: 9, style: .continuous))
        }
        .buttonStyle(.plain)
        .onHover { isHovered = $0 }
        .help("Draw captions with the \(name.lowercased()) look")
        .accessibilityLabel(name)
        .accessibilityValue(isSelected ? "Selected" : "Not selected")
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }
}

/// The mark on a cue the checks found problems in.
private struct StudioCaptionProblemBadge: View {
    let problems: [StudioCaptionFindingKind]

    var body: some View {
        HStack(spacing: 2) {
            Image(systemName: StudioSymbol.captionProblem)
            if problems.count > 1 {
                Text("\(problems.count)")
                    .monospacedDigit()
            }
        }
        .font(.caption)
        .foregroundStyle(.orange)
        .help(StudioCaptionPanelText.problemsText(problems))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Problems: \(StudioCaptionPanelText.problemsText(problems))")
    }
}

/// A cue in the list of the Captions panel. It is drawn again only when what it shows changes,
/// because the panel is drawn again each time the playhead moves.
private struct StudioCaptionRowButton: View, Equatable {
    let row: StudioCaptionRow
    let query: String
    let problems: [StudioCaptionFindingKind]
    let isAtPlayhead: Bool
    /// Whether the cue is the one open in the card above the list.
    let isSelected: Bool
    let action: () -> Void
    @State private var isHovered = false

    static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.row == rhs.row && lhs.query == rhs.query && lhs.problems == rhs.problems
            && lhs.isAtPlayhead == rhs.isAtPlayhead && lhs.isSelected == rhs.isSelected
    }

    private var fill: Color {
        if isSelected { return Color.accentColor.opacity(0.16) }
        if isAtPlayhead { return Color.accentColor.opacity(0.08) }
        return isHovered ? Color.primary.opacity(0.06) : Color.clear
    }

    var body: some View {
        let text = StudioCaptionText.rowText(row.cue)
        Button(action: action) {
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 6) {
                    Text(StudioCaptionPanelText.clockRangeText(row.cue))
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(isAtPlayhead ? Color.accentColor : Color.secondary)
                    Spacer(minLength: 0)
                    if !problems.isEmpty {
                        StudioCaptionProblemBadge(problems: problems)
                    }
                }
                Text(StudioCaptionSearch.highlighted(text, query: query))
                    .lineLimit(2)
                    .multilineTextAlignment(.leading)
                    .foregroundStyle(.primary)
            }
            .padding(.vertical, 6)
            .padding(.leading, 10)
            .padding(.trailing, 8)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(
                RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .fill(fill)
            )
            .overlay(
                RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .strokeBorder(isSelected ? Color.accentColor.opacity(0.55) : Color.clear)
            )
            .overlay(alignment: .leading) {
                if isAtPlayhead {
                    Capsule()
                        .fill(Color.accentColor)
                        .frame(width: 3)
                        .padding(.vertical, 6)
                        .padding(.leading, 2)
                }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { isHovered = $0 }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(StudioCaptionText.rangeText(row.cue)): \(text)")
        .accessibilityValue(accessibilityValue)
        .accessibilityAddTraits(isSelected ? [.isButton, .isSelected] : .isButton)
        .accessibilityHint("Edits this caption and moves the playhead to it")
    }

    private var accessibilityValue: String {
        [
            isSelected ? "Open for editing" : nil,
            isAtPlayhead ? "At the playhead" : nil,
            problems.isEmpty ? nil : "Problems: \(StudioCaptionPanelText.problemsText(problems))",
        ]
        .compactMap { $0 }
        .joined(separator: ". ")
    }
}

/// The cue open for editing, in a card above the list of the Captions panel: its text, its timing,
/// and splitting, merging, and deleting it, with a way to the cues either side. What is typed
/// stays here and in the view model's pending text until the box is left or another cue is chosen.
private struct StudioCaptionEditorCard: View {
    @ObservedObject var viewModel: StudioViewModel
    let row: StudioCaptionRow
    /// Where the cue is in time order, from 0.
    let position: Int
    let total: Int
    let problems: [StudioCaptionFindingKind]
    let isAtPlayhead: Bool
    @State private var draft = ""
    @FocusState private var isEditing: Bool

    private var title: String {
        StudioCaptionPanelText.positionText(position + 1, of: total)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 6) {
                Button {
                    viewModel.selectCaption(by: -1)
                } label: {
                    Image(systemName: "chevron.left")
                }
                .disabled(!viewModel.canSelectCaption(by: -1))
                .help("Previous caption")
                .accessibilityLabel("Previous caption")

                VStack(alignment: .leading, spacing: 1) {
                    Text(title)
                        .font(.callout.weight(.semibold))
                    Text(StudioCaptionPanelText.clockRangeText(row.cue))
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(isAtPlayhead ? Color.accentColor : Color.secondary)
                        .accessibilityLabel(StudioCaptionText.rangeText(row.cue))
                }
                .frame(maxWidth: .infinity, alignment: .leading)

                Button {
                    viewModel.selectCaption(by: 1)
                } label: {
                    Image(systemName: "chevron.right")
                }
                .disabled(!viewModel.canSelectCaption(by: 1))
                .help("Next caption")
                .accessibilityLabel("Next caption")

                Button("Done") {
                    viewModel.selectCaption(at: nil)
                }
                .buttonStyle(.borderedProminent)
                .help("Close this caption (Esc)")
            }
            .controlSize(.small)

            TextEditor(text: Binding(
                get: { draft },
                set: { text in
                    draft = text
                    viewModel.setPendingCaptionText(text)
                }
            ))
            .focused($isEditing)
            .font(.body)
            .scrollContentBackground(.hidden)
            .padding(4)
            // A fixed height: the panel scrolls, so a box that could grow would be given no limit.
            .frame(height: 64)
            .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 6, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: 6, style: .continuous)
                    .strokeBorder(isEditing ? Color.accentColor : Color(nsColor: .separatorColor), lineWidth: isEditing ? 2 : 1)
            )
            .accessibilityLabel("Caption text")

            if !problems.isEmpty {
                Label(StudioCaptionPanelText.problemsText(problems), systemImage: StudioSymbol.captionProblem)
                    .font(.caption)
                    .foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }

            VStack(spacing: 6) {
                timeRow(.start)
                timeRow(.end)
            }

            HStack(spacing: 6) {
                Button {
                    viewModel.splitSelectedCaptionAtPlayhead()
                } label: {
                    Label("Split", systemImage: StudioSymbol.splitCaption)
                }
                .disabled(!viewModel.canSplitSelectedCaption)
                .help("Split this caption in two at the playhead, or in the middle when the playhead isn't inside it")
                .accessibilityLabel("Split caption")
                Button {
                    viewModel.mergeSelectedCaptionWithNext()
                } label: {
                    Label("Merge", systemImage: StudioSymbol.mergeCaption)
                }
                .disabled(!viewModel.canMergeSelectedCaption)
                .help("Put this caption together with the one after it")
                .accessibilityLabel("Merge with next caption")
                Spacer(minLength: 0)
                Button(role: .destructive) {
                    viewModel.removeSelectedCaption()
                } label: {
                    Label("Delete", systemImage: StudioSymbol.delete)
                        .labelStyle(.iconOnly)
                }
                .help("Delete this caption")
                .accessibilityLabel("Delete caption")
            }
            .controlSize(.small)
            .disabled(!viewModel.isEditable)
        }
        .padding(10)
        .background(
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .fill(Color.accentColor.opacity(0.10))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .strokeBorder(Color.accentColor.opacity(0.55), lineWidth: 1)
        )
        .onAppear { draft = row.cue.text }
        .onChange(of: row.cue.text) { _, text in
            draft = text
        }
        .onChange(of: isEditing) { _, editing in
            if !editing { viewModel.commitCaptionText() }
        }
        .onExitCommand {
            viewModel.selectCaption(at: nil)
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Editing \(title)")
    }

    private func timeRow(_ edge: StudioCaptionEdge) -> some View {
        let time = edge == .start ? row.cue.start : row.cue.end
        let name = edge == .start ? "Start" : "End"
        return HStack(spacing: 6) {
            Text(name)
                .font(.caption)
                .foregroundStyle(.secondary)
                .frame(width: 34, alignment: .leading)
            Text(StudioCaptionPanelText.clockText(time))
                .font(.callout.monospacedDigit())
                .accessibilityHidden(true)
            Spacer(minLength: 4)
            Stepper(
                "",
                onIncrement: { viewModel.nudgeSelectedCaption(edge, by: 0.1) },
                onDecrement: { viewModel.nudgeSelectedCaption(edge, by: -0.1) }
            )
            .labelsHidden()
            .help("Move the \(name.lowercased()) by a tenth of a second")
            .accessibilityLabel("Caption \(name.lowercased())")
            .accessibilityValue(StudioEditorModel.secondsText(time))
            Button("At Playhead") {
                viewModel.setSelectedCaptionAtPlayhead(edge)
            }
            .controlSize(.small)
            .help(edge == .start ? "Start this caption at the playhead" : "End this caption at the playhead")
            .accessibilityLabel(edge == .start ? "Start caption at playhead" : "End caption at playhead")
        }
        .disabled(!viewModel.isEditable)
    }
}
