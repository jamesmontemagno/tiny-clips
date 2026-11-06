import SwiftUI

// MARK: - Captions Inspector

/// A cue as the list in the Captions panel shows it, with its place in the project's list.
struct StudioCaptionRow: Identifiable {
    let index: Int
    let cue: StudioCaptionCue

    var id: Int { index }
}

/// The Captions panel of the inspector: importing and exporting them, the list of cues in time
/// order, the text of the selected one, and what the checks find. Choosing a cue moves the
/// playhead to it. Its text goes into the project when the box is left, so typing is one undo step.
struct StudioCaptionsInspectorSection: View {
    @ObservedObject var viewModel: StudioViewModel
    @AppStorage("studioCaptionModel") private var modelID = ""
    @AppStorage("studioCaptionLanguage") private var languageID = ""
    @State private var isChoosingModel = false
    @State private var languages: [StudioCaptionLanguage] = []
    @State private var systemLanguage: String?
    @State private var hasLoadedLanguages = false

    private var model: StudioCaptionModel {
        StudioCaptionModels.resolve(
            id: modelID,
            appleSpeechIsAvailable: StudioAppleSpeech.isAvailable,
            canHearSystemLanguage: !hasLoadedLanguages || systemLanguage != nil
        )
    }

    private var language: StudioCaptionLanguage? {
        StudioCaptionLanguages.choose(stored: languageID, systemLanguage: systemLanguage, from: languages)
    }

    private var isModelReady: Bool {
        model.isAppleSpeech ? (language?.isInstalled ?? false) : viewModel.isCaptionModelInstalled(model)
    }

    private func loadLanguages() async {
        languages = await StudioAppleSpeech.languages()
        systemLanguage = await StudioAppleSpeech.language(equivalentTo: nil)
        hasLoadedLanguages = true
    }

    var body: some View {
        let rows = viewModel.captionRows
        VStack(alignment: .leading, spacing: 8) {
            Text(StudioCaptionText.countText(rows.count))
            if rows.isEmpty {
                Text(StudioCaptionModels.canGenerate ? StudioCaptionText.emptyHint : StudioCaptionText.needsAppleSilicon)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            if viewModel.isGeneratingCaptions {
                generationProgress
            } else if StudioCaptionModels.canGenerate {
                generateButton
            }
            HStack(spacing: 8) {
                importMenu
                exportMenu
            }
            Text(StudioCaptionText.exportNote)
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            if !viewModel.captionStatus.isEmpty {
                Text(viewModel.captionStatus)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        if viewModel.selectedCaption != nil {
            StudioSelectedCaptionEditor(viewModel: viewModel)
        }
        if !rows.isEmpty {
            drawing
            StudioInspectorSection(title: "All Captions") {
                list(rows)
            }
            checks
        }
    }

    private var generateButton: some View {
        Button("Generate\u{2026}") {
            isChoosingModel = true
        }
        .disabled(!viewModel.canGenerateCaptions)
        .help("Make captions from the recording's sound, on this Mac")
        .popover(isPresented: $isChoosingModel, arrowEdge: .bottom) {
            VStack(alignment: .leading, spacing: 12) {
                Text("Generate Captions")
                    .font(.headline)
                Picker("Model", selection: Binding(get: { model.id }, set: { modelID = $0 })) {
                    ForEach(StudioCaptionModels.options(appleSpeechIsAvailable: StudioAppleSpeech.isAvailable)) { model in
                        Text("\(model.name) (\(model.sizeText))").tag(model.id)
                    }
                }
                if model.isAppleSpeech {
                    Picker("Language", selection: Binding(get: { language?.id ?? "" }, set: { languageID = $0 })) {
                        ForEach(languages) { language in
                            Text(language.name).tag(language.id)
                        }
                    }
                    .disabled(languages.isEmpty)
                    .help("Apple Speech listens for one language, and does not detect it")
                }
                Text(StudioCaptionText.modelNote(model, isInstalled: isModelReady))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Text(StudioCaptionText.privacyNote)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if !(viewModel.project?.captions.cues.isEmpty ?? true) {
                    Text(StudioCaptionText.replaceNote)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                HStack {
                    Spacer()
                    Button(StudioCaptionText.generateActionText(model, isInstalled: isModelReady)) {
                        isChoosingModel = false
                        viewModel.generateCaptions(model: model, language: model.isAppleSpeech ? language?.id : nil)
                    }
                    .keyboardShortcut(.defaultAction)
                    .disabled(model.isAppleSpeech && language == nil)
                }
            }
            .padding()
            .frame(width: 280)
            .task {
                await loadLanguages()
            }
        }
    }

    private var generationProgress: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(viewModel.captionProgressText)
                .font(.caption)
                .foregroundStyle(.secondary)
            if viewModel.isStoppingCaptionGeneration || viewModel.captionGenerationProgress.stage == .preparingModel {
                ProgressView()
                    .progressViewStyle(.linear)
                    .accessibilityLabel("Generating captions")
            } else {
                ProgressView(value: min(1, max(0, viewModel.captionGenerationProgress.fraction)))
                    .accessibilityLabel("Generating captions")
            }
            Button("Cancel") {
                viewModel.cancelCaptionGeneration()
            }
            .disabled(!viewModel.canCancelCaptionGeneration)
            .help("Stop generating. The captions stay as they were.")
        }
    }

    private var importMenu: some View {
        Menu("Import") {
            Button("Timed to the Recording") {
                viewModel.importCaptions(timing: .recording)
            }
            Button("Timed to the Edited Video") {
                viewModel.importCaptions(timing: .editedVideo)
            }
        }
        .disabled(!viewModel.isEditable)
        .help("Replace the captions with an SRT or WebVTT file")
    }

    private var exportMenu: some View {
        Menu("Export") {
            Button("SubRip (.srt)") {
                viewModel.exportCaptions(.srt)
            }
            Button("WebVTT (.vtt)") {
                viewModel.exportCaptions(.vtt)
            }
        }
        .disabled(!viewModel.canExportCaptions)
        .help("Write the video's captions to a file")
    }

    private func list(_ rows: [StudioCaptionRow]) -> some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 2) {
                    ForEach(rows) { row in
                        StudioCaptionRowButton(
                            cue: row.cue,
                            isSelected: viewModel.selectedCaptionIndex == row.index
                        ) {
                            viewModel.selectCaption(at: row.index)
                        }
                        .id(row.index)
                    }
                }
            }
            .frame(maxHeight: 320)
            .onChange(of: viewModel.selectedCaptionIndex) { _, index in
                if let index { proxy.scrollTo(index) }
            }
        }
        .accessibilityLabel("Captions")
    }

    /// Whether, and how, the captions are drawn into the picture.
    private var drawing: some View {
        StudioInspectorSection(title: "In the Video") {
            Toggle("Draw captions on the video", isOn: Binding(
                get: { viewModel.project?.overlays.captions.enabled ?? false },
                set: { viewModel.setCaptionsDrawn($0) }
            ))
            .disabled(!viewModel.isEditable)
            .help("Shows them in the preview, and puts them into the picture of what you export")
            if viewModel.project?.overlays.captions.enabled == true {
                Picker("Look", selection: Binding(
                    get: { viewModel.project?.overlays.captions.appearance ?? .classicOutline },
                    set: { viewModel.setCaptionAppearance($0) }
                )) {
                    Text("Classic outline").tag(StudioCaptionAppearance.classicOutline)
                    Text("Contrast panel").tag(StudioCaptionAppearance.contrastPanel)
                    Text("Bold impact").tag(StudioCaptionAppearance.boldImpact)
                    Text("Clean shadow").tag(StudioCaptionAppearance.cleanShadow)
                }
                .disabled(!viewModel.isEditable)
                Picker("Size", selection: Binding(
                    get: { viewModel.project?.overlays.captions.size ?? .medium },
                    set: { viewModel.setCaptionSize($0) }
                )) {
                    Text("Small").tag(StudioCaptionSize.small)
                    Text("Medium").tag(StudioCaptionSize.medium)
                    Text("Large").tag(StudioCaptionSize.large)
                }
                .disabled(!viewModel.isEditable)
                Picker("Position", selection: Binding(
                    get: { viewModel.project?.overlays.captions.position ?? .bottom },
                    set: { viewModel.setCaptionPosition($0) }
                )) {
                    Text("Bottom").tag(StudioCaptionPosition.bottom)
                    Text("Top").tag(StudioCaptionPosition.top)
                }
                .disabled(!viewModel.isEditable)
            }
            Text(StudioCaptionText.drawnNote)
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var checks: some View {
        let findings = viewModel.captionFindings
        return StudioInspectorSection(title: "Checks") {
            Text(findings.isEmpty ? StudioCaptionText.noProblems : StudioCaptionQuality.summary(findings))
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Button("Auto Fix") {
                viewModel.fixCaptions()
            }
            .disabled(!viewModel.isEditable || !findings.contains(where: \.canFix))
            .help("Tidy text, wrap long lines, and give fast captions more time. Undo takes it back.")
        }
    }
}

/// A cue in the list of the Captions panel.
private struct StudioCaptionRowButton: View {
    let cue: StudioCaptionCue
    let isSelected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 2) {
                Text(StudioCaptionText.rangeText(cue))
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
                Text(StudioCaptionText.rowText(cue))
                    .lineLimit(2)
                    .multilineTextAlignment(.leading)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.vertical, 4)
            .padding(.horizontal, 6)
            .background(isSelected ? Color.accentColor.opacity(0.18) : Color.clear, in: RoundedRectangle(cornerRadius: 6))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(StudioCaptionText.rangeText(cue)): \(StudioCaptionText.rowText(cue))")
        .accessibilityAddTraits(isSelected ? [.isButton, .isSelected] : .isButton)
    }
}

/// The text of the selected cue. What is typed stays here and in the view model's pending text
/// until the box is left or another cue is chosen.
private struct StudioSelectedCaptionEditor: View {
    @ObservedObject var viewModel: StudioViewModel
    @State private var draft = ""
    @FocusState private var isEditing: Bool

    var body: some View {
        StudioInspectorSection(title: "Selected Caption") {
            if let cue = viewModel.selectedCaption {
                Text(StudioCaptionText.rangeText(cue))
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            TextEditor(text: Binding(
                get: { draft },
                set: { text in
                    draft = text
                    viewModel.setPendingCaptionText(text)
                }
            ))
            .focused($isEditing)
            .font(.body)
            .frame(minHeight: 60, maxHeight: 120)
            .overlay(RoundedRectangle(cornerRadius: 6).stroke(.separator))
            .accessibilityLabel("Caption text")
            Button("Delete Caption", systemImage: StudioSymbol.delete, role: .destructive) {
                viewModel.removeSelectedCaption()
            }
            .help("Take this caption out of the project")
        }
        .onAppear { draft = viewModel.selectedCaption?.text ?? "" }
        .onChange(of: viewModel.selectedCaptionIndex) { _, _ in
            draft = viewModel.selectedCaption?.text ?? ""
        }
        .onChange(of: viewModel.selectedCaption?.text) { _, text in
            draft = text ?? ""
        }
        .onChange(of: isEditing) { _, editing in
            if !editing { viewModel.commitCaptionText() }
        }
    }
}
