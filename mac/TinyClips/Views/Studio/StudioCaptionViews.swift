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

    var body: some View {
        let rows = viewModel.captionRows
        VStack(alignment: .leading, spacing: 8) {
            Text(StudioCaptionText.countText(rows.count))
            if rows.isEmpty {
                Text(StudioCaptionText.emptyHint)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
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
            StudioInspectorSection(title: "All Captions") {
                list(rows)
            }
            checks
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
