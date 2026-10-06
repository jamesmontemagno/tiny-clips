import SwiftUI

/// Settings for Tiny Clips Studio: the switch that turns it on while it is in preview, how long
/// project sources are kept, and the recordings that have no exported video to stand for them.
struct StudioSettingsSection: View {
    @ObservedObject var settings: CaptureSettings

    @State private var projects: [StudioProjectSummary] = []
    @State private var unreadableProjects: [StudioUnreadableProject] = []
    @State private var isCleaningUp = false
    @State private var cleanUpResult: String?
    @State private var draftPendingDelete: DraftRow?
    @State private var savingRecordingIDs: Set<String> = []

    /// One row of the drafts list: a recording that only its project holds.
    private struct DraftRow: Identifiable {
        let id: String
        let name: String

        /// When it was recorded and how much room it takes.
        let detail: String

        /// Why it is in the list although it is not a draft like the others, or nil.
        let note: String?
        let canOpen: Bool
        let canSaveRecording: Bool
    }

    private static let dateFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateStyle = .medium
        formatter.timeStyle = .short
        return formatter
    }()

    var body: some View {
        Section("Tiny Clips Studio") {
            // The task and the dialog hang off this one row so each exists once, whether
            // Studio is on or off.
            Toggle("Tiny Clips Studio (Preview)", isOn: $settings.studioPreviewEnabled)
                .help("Turns on Tiny Clips Studio, an editor for video recordings that is still being built.")
                .task {
                    reload()
                }
                .onChange(of: settings.studioPreviewEnabled) { _, _ in
                    reload()
                }
                .confirmationDialog(
                    "Delete this draft?",
                    isPresented: isConfirmingDelete,
                    presenting: draftPendingDelete
                ) { draft in
                    Button("Delete", role: .destructive) {
                        delete(draft)
                    }
                } message: { draft in
                    Text("\"\(draft.name)\" and its recordings will be removed. This cannot be undone.")
                }

            Text("Studio is an editor for video recordings. A recording made for Studio keeps the screen, the camera, and the clicks apart, so you can arrange them over a background, zoom, cut, and change the layout before you export. It is a preview: parts of it are unfinished.")
                .font(.caption)
                .foregroundStyle(.secondary)

            if settings.studioPreviewEnabled {
                Picker("Keep exported projects for:", selection: $settings.studioSourceRetentionDays) {
                    Text("7 days").tag(7)
                    Text("14 days").tag(14)
                    Text("30 days").tag(30)
                    Text("90 days").tag(90)
                    Text("Until I delete them").tag(0)
                }
                .help("How long a project stays editable after you last opened it.")

                Picker("Storage limit:", selection: $settings.studioStorageCapGigabytes) {
                    Text("5 GB").tag(5)
                    Text("10 GB").tag(10)
                    Text("20 GB").tag(20)
                    Text("50 GB").tag(50)
                    Text("No limit").tag(0)
                }
                .help("When exported projects use more than this, the ones opened longest ago are removed first. Drafts and projects you keep are not counted.")

                LabeledContent("Storage used:") {
                    Text(storageText)
                }

                HStack {
                    Button(isCleaningUp ? "Cleaning Up…" : "Clean Up Now") {
                        cleanUpNow()
                    }
                    .disabled(isCleaningUp)
                    .help("Remove the projects that are past these limits now.")

                    if let cleanUpResult {
                        Text(cleanUpResult)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }

                Text("Studio keeps the original screen and camera recordings so a video can be rearranged later. Removing a project does not remove the videos you exported from it. Drafts are never removed automatically, and neither is a project whose exported video is gone.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else if let keptText {
                Text(keptText)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }

        if settings.studioPreviewEnabled, !draftRows.isEmpty {
            Section("Studio Drafts") {
                ForEach(draftRows) { row in
                    draftRow(row)
                }
            }
        }
    }

    private func draftRow(_ row: DraftRow) -> some View {
        let isOpen = StudioWindowRegistry.shared.openProjectIDs.contains(row.id)
        let isSaving = savingRecordingIDs.contains(row.id)

        return HStack(spacing: 8) {
            VStack(alignment: .leading, spacing: 2) {
                Text(row.name)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Text(row.detail)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if let note = row.note {
                    Text(note)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            .accessibilityElement(children: .combine)

            Spacer()

            if row.canSaveRecording {
                Button {
                    saveRecording(row)
                } label: {
                    Image(systemName: "square.and.arrow.down")
                }
                .disabled(isSaving)
                .accessibilityLabel("Save the screen recording of \(row.name)")
                .help("Save this project's screen recording to your videos folder as an ordinary video. The project is kept as it is.")
            }

            if row.canOpen {
                Button("Open") {
                    StudioWindowRegistry.shared.open(projectID: row.id)
                }
                .accessibilityLabel("Open \(row.name) in Studio")
            }

            Button("Delete…", role: .destructive) {
                draftPendingDelete = row
            }
            .disabled(isOpen)
            .accessibilityLabel("Delete \(row.name)")
            .help(isOpen ? "Close this draft in Studio before deleting it." : "Delete this draft and its recordings.")
        }
    }

    // MARK: - Values

    /// Recordings that only their project holds, newest first: the ones that were kept without
    /// exporting, the ones whose exported video is gone, and after them the ones Studio cannot
    /// read.
    private var draftRows: [DraftRow] {
        let drafts = projects
            .filter { ($0.isDraft || $0.exportMissing) && !$0.isFlat }
            .sorted { $0.createdAt > $1.createdAt }
            .map { project in
                DraftRow(
                    id: project.id,
                    name: displayName(project),
                    detail: detailText(createdAt: project.createdAt, bytes: project.sizeOnDisk),
                    note: project.exportMissing ? "Its exported video is no longer where it was saved." : nil,
                    canOpen: true,
                    canSaveRecording: project.sourceExists
                )
            }
        let unreadable = unreadableProjects
            .sorted { $0.createdAt > $1.createdAt }
            .map { project in
                DraftRow(
                    id: project.id,
                    name: "Unreadable Project",
                    detail: detailText(createdAt: project.createdAt, bytes: project.sizeOnDisk),
                    note: "Studio cannot read this project. It may be damaged, or made by a newer version of Tiny Clips.",
                    canOpen: false,
                    canSaveRecording: project.hasScreenRecording
                )
            }
        return drafts + unreadable
    }

    private var projectCount: Int {
        projects.count + unreadableProjects.count
    }

    private var totalBytes: Int64 {
        projects.reduce(Int64(0)) { $0 + max(0, $1.sizeOnDisk) }
            + unreadableProjects.reduce(Int64(0)) { $0 + max(0, $1.sizeOnDisk) }
    }

    private var storageText: String {
        switch projectCount {
        case 0:
            return "No projects"
        case 1:
            return "1 project, \(sizeText(totalBytes))"
        default:
            return "\(projectCount) projects, \(sizeText(totalBytes))"
        }
    }

    /// What is still on disk while Studio is off, or nil when there is nothing.
    private var keptText: String? {
        switch projectCount {
        case 0:
            return nil
        case 1:
            return "1 Studio project is kept and uses \(sizeText(totalBytes)). It is not cleaned up while Studio is off. Turn Studio on to open or delete it."
        default:
            return "\(projectCount) Studio projects are kept and use \(sizeText(totalBytes)). They are not cleaned up while Studio is off. Turn Studio on to open or delete them."
        }
    }

    private var isConfirmingDelete: Binding<Bool> {
        Binding(
            get: { draftPendingDelete != nil },
            set: { isPresented in
                if !isPresented {
                    draftPendingDelete = nil
                }
            }
        )
    }

    private func displayName(_ project: StudioProjectSummary) -> String {
        let name = project.name.trimmingCharacters(in: .whitespacesAndNewlines)
        return name.isEmpty ? "Untitled Recording" : name
    }

    private func detailText(createdAt: Date, bytes: Int64) -> String {
        "\(Self.dateFormatter.string(from: createdAt)), \(sizeText(bytes))"
    }

    private func sizeText(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }

    // MARK: - Actions

    private func reload() {
        // On the App Store build this is what opens a save folder the user chose. The list
        // says whether each exported video is still where it was saved, and could not see into
        // that folder otherwise.
        _ = SaveService.shared.outputDirectoryURL(for: .video)
        Task {
            // Sizing every project reads the disk, so it stays off the main thread.
            let loaded = await Task.detached(priority: .utility) {
                (
                    (try? StudioProjectStore.shared.listSummaries()) ?? [],
                    (try? StudioProjectStore.shared.listUnreadableProjects()) ?? []
                )
            }.value
            projects = loaded.0
            unreadableProjects = loaded.1
        }
    }

    private func cleanUpNow() {
        isCleaningUp = true
        cleanUpResult = nil
        StudioMaintenance.cleanUp { removed in
            isCleaningUp = false
            switch removed {
            case .none:
                cleanUpResult = nil
            case .some(0):
                cleanUpResult = "Nothing to remove."
            case .some(1):
                cleanUpResult = "Removed 1 project."
            case .some(let count):
                cleanUpResult = "Removed \(count) projects."
            }
            reload()
        }
    }

    private func saveRecording(_ row: DraftRow) {
        guard !savingRecordingIDs.contains(row.id) else { return }
        savingRecordingIDs.insert(row.id)
        let id = row.id
        Task {
            do {
                // The saved video is announced the way every saved video is.
                _ = try await StudioMaintenance.saveScreenRecording(projectID: id)
            } catch {
                SaveService.shared.showError("The screen recording could not be saved: \(error.localizedDescription)")
            }
            savingRecordingIDs.remove(id)
        }
    }

    private func delete(_ draft: DraftRow) {
        draftPendingDelete = nil
        guard !StudioWindowRegistry.shared.openProjectIDs.contains(draft.id) else { return }
        do {
            try StudioProjectStore.shared.delete(id: draft.id)
        } catch {
            SaveService.shared.showError("The draft could not be deleted: \(error.localizedDescription)")
        }
        reload()
    }
}
