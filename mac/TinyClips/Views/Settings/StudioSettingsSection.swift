import SwiftUI

/// Settings for Tiny Clips Studio: how long project sources are kept, and the drafts that have not
/// been exported yet. Shown only while the Studio preview is turned on.
struct StudioSettingsSection: View {
    @ObservedObject var settings: CaptureSettings

    @State private var projects: [StudioProjectSummary] = []
    @State private var isCleaningUp = false
    @State private var cleanUpResult: String?
    @State private var draftPendingDelete: StudioProjectSummary?

    private static let dateFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateStyle = .medium
        formatter.timeStyle = .short
        return formatter
    }()

    var body: some View {
        Section("Tiny Clips Studio (Preview)") {
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
            .help("When projects use more than this, the ones opened longest ago are removed first.")

            // The task and the dialog hang off this one row so each exists once.
            LabeledContent("Storage used:") {
                Text(storageText)
            }
            .task {
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
                Text("\"\(displayName(draft))\" and its recordings will be removed. This cannot be undone.")
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

            Text("Studio keeps the original screen and camera recordings so a video can be rearranged later. Removing a project does not remove the videos you exported from it. Drafts are never removed automatically.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }

        if !drafts.isEmpty {
            Section("Studio Drafts") {
                ForEach(drafts, id: \.id) { draft in
                    draftRow(draft)
                }
            }
        }
    }

    private func draftRow(_ draft: StudioProjectSummary) -> some View {
        let name = displayName(draft)
        let isOpen = StudioWindowRegistry.shared.openProjectIDs.contains(draft.id)

        return HStack(spacing: 8) {
            VStack(alignment: .leading, spacing: 2) {
                Text(name)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Text("\(Self.dateFormatter.string(from: draft.createdAt)), \(sizeText(draft.sizeOnDisk))")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .accessibilityElement(children: .combine)

            Spacer()

            Button("Open") {
                StudioWindowRegistry.shared.open(projectID: draft.id)
            }
            .accessibilityLabel("Open \(name) in Studio")

            Button("Delete…", role: .destructive) {
                draftPendingDelete = draft
            }
            .disabled(isOpen)
            .accessibilityLabel("Delete \(name)")
            .help(isOpen ? "Close this draft in Studio before deleting it." : "Delete this draft and its recordings.")
        }
    }

    // MARK: - Values

    /// Recordings that were kept without exporting, newest first.
    private var drafts: [StudioProjectSummary] {
        projects
            .filter { $0.isDraft && !$0.isFlat }
            .sorted { $0.createdAt > $1.createdAt }
    }

    private var storageText: String {
        let total = projects.reduce(Int64(0)) { $0 + max(0, $1.sizeOnDisk) }
        switch projects.count {
        case 0:
            return "No projects"
        case 1:
            return "1 project, \(sizeText(total))"
        default:
            return "\(projects.count) projects, \(sizeText(total))"
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

    private func sizeText(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }

    // MARK: - Actions

    private func reload() {
        Task {
            // Sizing every project reads the disk, so it stays off the main thread.
            let loaded = await Task.detached(priority: .utility) {
                (try? StudioProjectStore.shared.listSummaries()) ?? []
            }.value
            projects = loaded
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

    private func delete(_ draft: StudioProjectSummary) {
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
