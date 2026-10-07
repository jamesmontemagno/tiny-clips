import AppKit
import Combine
import SwiftUI

/// What the Studio section of Settings lists: the projects, and which of them are open in an
/// editor. An object of its own and not state of the view, so that it hears of a change whether
/// or not a row of the section is on the screen. A form may let go of rows that are scrolled
/// out of it, and with a row goes whatever listens on it.
@MainActor
private final class StudioSettingsProjects: ObservableObject {
    @Published private(set) var projects: [StudioProjectSummary] = []
    @Published private(set) var unreadableProjects: [StudioUnreadableProject] = []

    /// The projects that are open in an editor, as of the last reload. Kept here because the
    /// registry that knows them tells nobody when they change.
    @Published private(set) var openProjectIDs: Set<String> = []

    private var changes: AnyCancellable?

    init() {
        // An editor opened or closed. While it was open it may have exported its project, or
        // deleted it, and nothing else tells this list.
        changes = NotificationCenter.default.publisher(for: .studioProjectsDidChange)
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in
                self?.reload()
            }
    }

    func reload() {
        openProjectIDs = StudioWindowRegistry.shared.openProjectIDs
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
}

/// Settings for Tiny Clips Studio: the switch that turns it on while it is in preview, how long
/// project sources are kept, and the recordings that have no exported video to stand for them.
struct StudioSettingsSection: View {
    @ObservedObject var settings: CaptureSettings

    @StateObject private var studioProjects = StudioSettingsProjects()
    @State private var isCleaningUp = false
    @State private var cleanUpResult: String?
    @State private var savingRecordingIDs: Set<String> = []
    @State private var savedRecordingNote: SavedRecordingNote?

    /// What the last save of a screen recording said, and the draft it said it about. It is
    /// shown while that draft is in the list, and not after the draft has gone.
    private struct SavedRecordingNote {
        let projectID: String
        let text: String
    }

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

        /// What VoiceOver calls the row in the names of its buttons. Two recordings can have one
        /// name, and every project that cannot be read has the same one, so the date is in it.
        var spokenName: String { "\(name), \(detail)" }
    }

    /// Why a project whose video was exported is listed with the drafts again. A file of another
    /// size under the video's name counts as gone too. On the App Store build the app sees only
    /// the folders it was given, so a video in a folder that is no longer the chosen one looks
    /// the same as one that is gone.
    private static var exportMissingNote: String {
        #if APPSTORE
        return "Its exported video is not where it was saved, has been changed since, or is in a folder Tiny Clips can no longer open."
        #else
        return "Its exported video is no longer where it was saved, or has been changed since."
        #endif
    }

    private static let dateFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateStyle = .medium
        formatter.timeStyle = .short
        return formatter
    }()

    var body: some View {
        Section("Tiny Clips Studio") {
            // The task hangs off this one row so it exists once, whether Studio is on or off.
            // What an editor changes is heard by the list itself, not by a row.
            Toggle("Tiny Clips Studio (Preview)", isOn: $settings.studioPreviewEnabled)
                .help("Turns on Tiny Clips Studio, an editor for video recordings that is still being built.")
                .task {
                    studioProjects.reload()
                }
                .onChange(of: settings.studioPreviewEnabled) { _, _ in
                    studioProjects.reload()
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
                .help("When exported projects use more than this, the ones opened longest ago are removed first, and the one you opened last is always kept. Drafts and projects you keep are not counted.")

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

                if let note = savedRecordingNote, draftRows.contains(where: { $0.id == note.projectID }) {
                    Text(note.text)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
        }
    }

    private func draftRow(_ row: DraftRow) -> some View {
        let isOpen = studioProjects.openProjectIDs.contains(row.id)
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
                .accessibilityLabel("Save the screen recording of \(row.spokenName)")
                .help("Save this project's screen recording to your videos folder as an ordinary video. The project is kept as it is.")
            }

            if row.canOpen {
                Button("Open") {
                    StudioWindowRegistry.shared.open(projectID: row.id)
                }
                .accessibilityLabel("Open \(row.spokenName) in Studio")
            }

            Button("Delete…", role: .destructive) {
                confirmDelete(row)
            }
            .disabled(isOpen)
            .accessibilityLabel("Delete \(row.spokenName)")
            .help(isOpen ? "Close this draft in Studio before deleting it." : "Delete this draft and its recordings.")
        }
    }

    // MARK: - Values

    /// Recordings that only their project holds, newest first: the ones that were kept without
    /// exporting, the ones whose exported video is gone, and after them the ones Studio cannot
    /// read.
    private var draftRows: [DraftRow] {
        let drafts = studioProjects.projects
            .filter { $0.isDraftOrLostItsExport }
            .sorted { $0.createdAt > $1.createdAt }
            .map { project in
                DraftRow(
                    id: project.id,
                    name: displayName(project),
                    detail: detailText(createdAt: project.createdAt, bytes: project.sizeOnDisk),
                    note: project.exportMissing ? Self.exportMissingNote : nil,
                    canOpen: true,
                    canSaveRecording: project.sourceExists
                )
            }
        let unreadable = studioProjects.unreadableProjects
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
        studioProjects.projects.count + studioProjects.unreadableProjects.count
    }

    private var totalBytes: Int64 {
        studioProjects.projects.reduce(Int64(0)) { $0 + max(0, $1.sizeOnDisk) }
            + studioProjects.unreadableProjects.reduce(Int64(0)) { $0 + max(0, $1.sizeOnDisk) }
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

    private func cleanUpNow() {
        isCleaningUp = true
        cleanUpResult = nil
        StudioMaintenance.cleanUp { removed in
            isCleaningUp = false
            let result: String?
            switch removed {
            case .none:
                result = nil
            case .some(0):
                result = "Nothing to remove."
            case .some(1):
                result = "Removed 1 project."
            case .some(let count):
                result = "Removed \(count) projects."
            }
            cleanUpResult = result
            if let result {
                // The line appears beside a button that already has the focus, where VoiceOver
                // would not come by it.
                AccessibilityAnnouncementService.shared.announce(result, priority: .medium)
            }
            studioProjects.reload()
        }
    }

    private func saveRecording(_ row: DraftRow) {
        guard !savingRecordingIDs.contains(row.id) else { return }
        savingRecordingIDs.insert(row.id)
        let id = row.id
        Task {
            do {
                // The saved video is announced the way every saved video is. With the settings
                // as they come that shows nothing on the screen, so it is said here as well.
                let url = try await StudioMaintenance.saveScreenRecording(projectID: id)
                savedRecordingNote = SavedRecordingNote(projectID: id, text: "Saved as \(url.lastPathComponent).")
            } catch {
                savedRecordingNote = nil
                SaveService.shared.showError("The screen recording could not be saved: \(error.localizedDescription)")
            }
            savingRecordingIDs.remove(id)
        }
    }

    /// Asks before a draft is deleted. An alert of the app's own and not a dialog hung on a row
    /// of this form, so that it does not depend on which rows are on the screen.
    private func confirmDelete(_ row: DraftRow) {
        guard !isOpenInStudio(row) else { return }

        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Delete this draft?"
        alert.informativeText = "\"\(row.name)\" and its recordings will be removed. This cannot be undone."
        let deleteButton = alert.addButton(withTitle: "Delete")
        deleteButton.hasDestructiveAction = true
        // An alert's first button answers Return. This one must not: it cannot be undone.
        deleteButton.keyEquivalent = ""
        // Esc, by its title.
        let cancelButton = alert.addButton(withTitle: "Cancel")
        // With Keyboard Navigation switched on, Space presses the button that has the focus.
        // That is to be Cancel. The alert is laid out here, so that what is set after it is
        // what the alert opens with.
        alert.layout()
        alert.window.initialFirstResponder = cancelButton
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        delete(row)
    }

    /// Says so when a draft is open in an editor, where it cannot be deleted from here.
    private func isOpenInStudio(_ row: DraftRow) -> Bool {
        guard StudioWindowRegistry.shared.openProjectIDs.contains(row.id) else { return false }
        SaveService.shared.showError("\"\(row.name)\" is open in Studio. Close it there before deleting it.")
        return true
    }

    private func delete(_ draft: DraftRow) {
        guard !isOpenInStudio(draft) else { return }
        do {
            try StudioProjectStore.shared.delete(id: draft.id)
        } catch {
            SaveService.shared.showError("The draft could not be deleted: \(error.localizedDescription)")
        }
        studioProjects.reload()
    }
}
