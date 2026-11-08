import AppKit
import AVFoundation
import SwiftUI
import UniformTypeIdentifiers

// MARK: - Studio View Model

/// Drives one Studio editor window: the project being edited, the live preview, autosave, and export.
///
/// The preview always plays the whole source recording, so `playhead` is in source time. Trimming
/// and muting are applied by the transport here and by the exporter, never by rebuilding the player.
@MainActor
final class StudioViewModel: ObservableObject {
    enum LoadState: Equatable {
        case loading
        case ready
        case unavailable(String)
    }

    static let previewLongSide = 1600.0
    static let posterLongSide = 640.0

    let projectID: String

    @Published private(set) var state: LoadState = .loading
    @Published private(set) var editor: StudioEditorModel?
    @Published private(set) var player: AVPlayer?
    @Published private(set) var playhead: Double = 0 {
        didSet { followPlayheadIntoScene() }
    }
    @Published private(set) var isPlaying = false
    @Published private(set) var isExporting = false
    @Published private(set) var exportProgress: Double = 0

    /// Whether the Export popover is showing, asking for the resolution and quality.
    @Published private(set) var isChoosingExportOptions = false
    /// The zoom the inspector shows, as its place in the project's zooms, or nil. It stays on its
    /// zoom while edits, undo, and redo change the list around it.
    @Published private(set) var selectedZoomIndex: Int?

    /// The cut the inspector shows, as its place in the project's cuts, or nil. A zoom, a cut, or
    /// a speed change is selected, never two of them: selecting one lets go of the others.
    @Published private(set) var selectedCutIndex: Int?

    /// The speed change the inspector shows, as its place in the project's speed changes, or nil.
    @Published private(set) var selectedSpeedIndex: Int?

    /// The annotation the inspector shows, as its place in the project's annotations, or nil.
    @Published private(set) var selectedAnnotationIndex: Int?

    /// The inspector panel on show. Taking hold of a zoom, a cut, a speed change, a scene, or the
    /// camera shows the panel that edits it.
    @Published private(set) var inspectorPanel: StudioInspectorPanel = .background

    /// The cue whose text the Captions panel shows, as its place in `project.captions.cues`. Apart
    /// from the timeline's selection: captions have no lane.
    @Published private(set) var selectedCaptionIndex: Int?

    /// Whether captions are being made from the recording's sound. Editing goes on meanwhile,
    /// because what comes of it is in source time.
    @Published private(set) var isGeneratingCaptions = false

    /// Whether generating was asked to stop and has not yet. Speech recognition stops between
    /// stretches of work, so this can last for seconds.
    @Published private(set) var isStoppingCaptionGeneration = false

    @Published private(set) var captionGenerationProgress = StudioCaptionProgress(stage: .readingSound)

    /// What the Captions panel says about the last thing done to the captions, or nil.
    @Published private(set) var captionNotice: StudioCaptionNotice?

    /// Takes a notice away when it has been on show long enough. A notice that asks for something
    /// to be done stays until it is closed.
    private var captionNoticeTimer: Task<Void, Never>?

    /// The language captions were last asked for in, to name it when no speech was found.
    private var captionLanguageAsked: String?

    /// What is typed for the selected cue, until the box is left. So typing is one undo step.
    private var pendingCaptionText: String?

    /// The size the preview is drawn at. It follows the project, except during a drag, when a new
    /// size waits for the drag to end so the player is not rebuilt for every step of it.
    @Published private(set) var previewSize = CGSize.zero

    /// Whether a project that cannot be shown still has its screen recording, to be saved as a
    /// video of its own. False unless the state is `unavailable`.
    @Published private(set) var canSaveScreenRecording = false

    /// True while the screen recording is being copied.
    @Published private(set) var isSavingScreenRecording = false

    /// What came of saving the screen recording: the name it got, or why it was not saved.
    @Published private(set) var screenRecordingStatus: String?

    /// What the window is waiting for while the project is copied to a folder, or nil. Nothing
    /// is edited meanwhile.
    @Published private(set) var busyMessage: String?

    /// The other projects in the store, the one opened last first, for Open Recent.
    @Published private(set) var recentProjects: [StudioProjectSummary] = []

    static let recentProjectLimit = 8

    /// Set by the window. Called to close it once an export started from the close prompt is done.
    var requestClose: (() -> Void)?

    /// Set by the window. Whether it is in front of someone, so that what they would otherwise
    /// miss can be sent as a notification instead.
    var isWindowInFront: (() -> Bool)?

    private let store: StudioProjectStore
    private var events = StudioEvents()
    private var paths: StudioProjectPaths?
    private var playback: StudioPlayback?
    private var isInGesture = false
    private var needsPreviewRefresh = false
    private var isRefreshingPreview = false
    private var timeObserver: Any?
    private var rateObservation: NSKeyValueObservation?
    private var saveTask: Task<Void, Never>?
    private var previewTask: Task<Void, Never>?
    private var exportTask: Task<Void, Never>?
    private var hasUnsavedEdits = false
    private var closesAfterExport = false
    private var closesAfterChoosingExportOptions = false
    private var deletesOnClose = false
    private var isTornDown = false
    private var destinationScope: URL?
    private var isChoosingDestination = false
    /// What the project folder was last given, in this window: when it opened or was last saved.
    /// Closing asks about changes since then, and Don't Save puts it back.
    private var projectSaveBaseline: StudioEditableState?

    private func savedProjectFile() throws -> URL? {
        guard let record = try store.savedDestination(id: projectID) else { return nil }
        #if APPSTORE
        var destination = record
        guard let bookmark = destination.bookmark else {
            throw NSError(domain: "TinyClipsStudioSave", code: 2, userInfo: [
                NSLocalizedDescriptionKey: "Access to the project folder must be renewed. Use Save Project As to choose it again."
            ])
        }
        var stale = false
        let folder = try URL(resolvingBookmarkData: bookmark, options: [.withSecurityScope],
                             relativeTo: nil, bookmarkDataIsStale: &stale)
        if destinationScope != folder {
            guard folder.startAccessingSecurityScopedResource() else {
                throw NSError(domain: "TinyClipsStudioSave", code: 3, userInfo: [
                    NSLocalizedDescriptionKey: "The saved project folder cannot be accessed. Use Save Project As to choose it again."
                ])
            }
            destinationScope?.stopAccessingSecurityScopedResource()
            destinationScope = folder
        }
        destination.file = folder.appendingPathComponent(destination.file.lastPathComponent)
        if stale {
            try store.rememberDestination(id: projectID, file: destination.file,
                bookmark: folder.bookmarkData(options: [.withSecurityScope], includingResourceValuesForKeys: nil, relativeTo: nil))
        }
        return destination.file
        #else
        return record.file
        #endif
    }

    /// Where playback was last sent to get over a cut. Playback only goes forward, so it comes to
    /// the same cut again only after Play has been pressed, which forgets this.
    private var cutSkipTarget: Double?

    /// The rate the player was last asked to play at. It is compared with this and not with what
    /// the player says, which need not be the number it was given.
    private var requestedRate: Float = 1

    /// When the playhead was last moved by the playing player, on the system's clock.
    private var lastPlayheadMove: TimeInterval = 0

    private let captionGenerator: StudioCaptionGenerating
    private var captionGenerationTask: Task<Void, Never>?

    /// Which generation the progress that comes in belongs to. Some can arrive after the end.
    private var captionGenerationNumber = 0

    init(
        projectID: String,
        store: StudioProjectStore = .shared,
        captionGenerator: StudioCaptionGenerating = StudioAppleSpeechCaptionGenerator()
    ) {
        self.projectID = projectID
        self.store = store
        self.captionGenerator = captionGenerator
    }

    // MARK: - Derived State

    var project: StudioProject? { editor?.project }
    func automationProjectSnapshot() -> StudioProject? {
        guard isEditable else { return nil }
        return project
    }

    @discardableResult
    func applyAutomationProject(_ updated: StudioProject, expectedModifiedAt: Date) -> Bool {
        guard isEditable, !isInGesture else { return false }
        // What is being typed goes into the project first, so an agent's edit cannot drop it. When
        // that changes the project, the agent's edit was made without it and is refused as a
        // conflict: the agent reads the project again.
        let typed = editor?.editableState
        commitCaptionText()
        guard editor?.editableState == typed, project?.modifiedAt == expectedModifiedAt, saveNow() else { return false }
        selectedCaptionIndex = nil
        edit { $0.applyAutomationProject(updated) }
        guard saveNow() else {
            undo()
            return false
        }
        return true
    }

    func prepareAutomationExport() -> (StudioProject, StudioEvents)? {
        commitCaptionText()
        guard isEditable, saveNow(), let project else { return nil }
        return (project, events)
    }

    var isReady: Bool { state == .ready }
    var isEditable: Bool { state == .ready && !isExporting && busyMessage == nil }
    var isBusy: Bool { busyMessage != nil }

    /// Whether the project can be saved as a folder now.
    var canSaveProjectFolder: Bool { isEditable && !isGeneratingCaptions && editor?.project.sources.screen.external == false }
    var projectFolderHelp: String {
        editor?.project.sources.screen.external == true
            ? "Imported video edits are saved automatically in Studio. Saving a portable project folder is unavailable because the source video is stored externally. Keep the original video."
            : "Save includes the recordings and a .tinyclips file in a project folder."
    }

    /// Whether the project can be deleted now. One that cannot be shown can be: that is the
    /// project there is least reason to keep.
    var canDeleteProject: Bool { state != .loading && !isBusy }
    var canUndo: Bool { isEditable && (editor?.canUndo ?? false) }
    var canRedo: Bool { isEditable && (editor?.canRedo ?? false) }
    var canExport: Bool { isEditable && !isGeneratingCaptions && (editor?.outputDuration ?? 0) > 0 }
    var hasCamera: Bool { editor?.hasCamera ?? false }

    var clipName: String {
        let name = project?.name.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return name.isEmpty ? "Untitled Recording" : name
    }

    /// The canvas the preview shows, in pixels. This is the size the player was last built for, so
    /// what is laid out over the preview matches what is drawn in it.
    var previewRenderSize: CGSize {
        if previewSize.width > 0, previewSize.height > 0 {
            return previewSize
        }
        guard let project else { return CGSize(width: 1280, height: 720) }
        return Self.renderSize(for: project, longSide: Self.previewLongSide)
    }

    /// The size the next export comes to at `resolution`.
    func exportRenderSize(for resolution: StudioExportResolution) -> CGSize {
        guard let project else { return CGSize(width: 1920, height: 1080) }
        let size = StudioExportPresets.size(project: project, resolution: resolution)
        return CGSize(width: size.width, height: size.height)
    }

    var timeText: String {
        guard let editor else { return "0:00.0 / 0:00.0" }
        let position = StudioEditorModel.formattedTime(editor.outputTime(forSourceTime: playhead))
        return "\(position) / \(StudioEditorModel.formattedTime(editor.outputDuration))"
    }

    var zooms: [StudioZoom] { project?.zooms ?? [] }

    var selectedZoom: StudioZoom? {
        guard let index = selectedZoomIndex, zooms.indices.contains(index) else { return nil }
        return zooms[index]
    }

    /// Whether the recording has clicks to suggest zooms from. A recording of a window has none.
    var hasClicks: Bool { !events.clicks.isEmpty }

    /// Whether the recording has pointer positions for a zoom to follow. A recording of a window has none.
    var hasPointerPositions: Bool { !events.cursor.isEmpty }

    /// Whether adding a zoom at the playhead has a zoom to answer with: one fits there, or one is
    /// already there to select.
    var canAddZoomAtPlayhead: Bool { isEditable && (editor?.canAddZoom(at: playhead) ?? false) }

    var canSuggestZooms: Bool { isEditable && hasClicks }

    var hasSuggestedZooms: Bool { (editor?.suggestedZoomCount ?? 0) > 0 }

    /// The camera bubble on a canvas of `canvasSize`, when the bubble layout is showing.
    func bubbleRect(canvasSize: CGSize) -> StudioRect? {
        guard let editor, editor.effectiveLayout == .bubble else { return nil }
        return editor.bubbleRect(canvasSize: canvasSize)
    }

    // MARK: - Loading

    func load() async {
        guard state == .loading, editor == nil else { return }
        do {
            let opened = try store.markOpened(id: projectID)
            let openedPaths = try store.paths(for: opened)
            let model = StudioEditorModel(project: opened)
            paths = openedPaths
            editor = model
            projectSaveBaseline = model.editableState

            guard FileManager.default.fileExists(atPath: openedPaths.screenURL.path) else {
                becomeUnavailable(
                    "The original recording for this project is no longer on this Mac, so it cannot be previewed or exported here."
                )
                return
            }

            events = (try? store.loadEvents(id: projectID)) ?? StudioEvents()
            let size = Self.renderSize(for: model.project, longSide: Self.previewLongSide)
            let newPlayback = StudioPlayback(
                project: model.previewProject,
                events: events,
                paths: openedPaths,
                renderSize: size
            )
            previewSize = size
            playback = newPlayback
            try await newPlayback.makePlayerItem()
            guard !isTornDown else { return }

            newPlayback.player.isMuted = model.project.audio.muted
            newPlayback.player.actionAtItemEnd = .pause
            observe(newPlayback.player)
            player = newPlayback.player
            inspectorPanel = StudioInspectorPanel.initial(hasCamera: model.hasCamera)
            state = .ready
            refreshRecentProjects()
            seek(to: model.trimStart)
        } catch {
            becomeUnavailable(error.localizedDescription)
        }
    }

    /// The project cannot be shown. Whether its screen recording is still there is looked up
    /// now, so that the message can offer to save it.
    private func becomeUnavailable(_ message: String) {
        canSaveScreenRecording = store.screenRecordingURL(id: projectID) != nil
        state = .unavailable(message)
    }

    /// Saves the screen recording of a project that cannot be shown as an ordinary video, and
    /// says in `screenRecordingStatus` what came of it. The project is left as it is.
    func saveScreenRecording() {
        guard canSaveScreenRecording, !isSavingScreenRecording, !isTornDown else { return }
        isSavingScreenRecording = true
        screenRecordingStatus = nil
        let id = projectID
        Task { [weak self] in
            do {
                // A saved video is announced the way every saved video is, window or no window.
                let url = try await StudioMaintenance.saveScreenRecording(projectID: id)
                self?.screenRecordingStatus = "Saved as \(url.lastPathComponent)."
            } catch {
                let message = "The screen recording could not be saved: \(error.localizedDescription)"
                if let self, !self.isTornDown {
                    self.screenRecordingStatus = message
                    AccessibilityAnnouncementService.shared.announce(message, priority: .high)
                } else {
                    // The window closed while the recording was being copied, and the line
                    // that would have said this went with it.
                    SaveService.shared.showError(message)
                }
            }
            self?.isSavingScreenRecording = false
        }
    }

    // MARK: - Keeping

    /// Whether the project is pinned against automatic cleanup.
    var keepsSources: Bool { project?.keepSources ?? false }

    /// Pins the project against automatic cleanup, or lets go of it. It is written into the
    /// project on disk at once. It is not an edit: Undo leaves it alone, and it does not have
    /// to wait for an export to end.
    func setKeepsSources(_ keepSources: Bool) {
        guard !isTornDown, editor != nil, keepsSources != keepSources else { return }
        do {
            let saved = try store.setKeepSources(id: projectID, keepSources: keepSources)
            editor?.refreshBookkeeping(from: saved)
        } catch {
            // Nothing changed, so nothing would redraw the checkbox, which has already turned.
            objectWillChange.send()
            SaveService.shared.showError("Studio could not change whether this project is kept: \(error.localizedDescription)")
        }
    }

    // MARK: - Edits

    /// Starts a gesture such as a drag, so everything until `endGesture()` is one undo step.
    func beginGesture() {
        guard isEditable else { return }
        isInGesture = true
        editor?.beginEditingGroup()
    }

    func endGesture() {
        editor?.commitEditingGroup()
        guard isInGesture else { return }
        isInGesture = false
        // A canvas size that waited for the drag to end is applied now.
        if let project, Self.renderSize(for: project, longSide: Self.previewLongSide) != previewSize {
            refreshPreview()
        }
    }

    func undo() {
        selectedCaptionIndex = nil
        pendingCaptionText = nil
        edit { $0.undo() }
    }

    func redo() {
        selectedCaptionIndex = nil
        pendingCaptionText = nil
        edit { $0.redo() }
    }

    // MARK: - Captions

    /// What the Captions panel shows of the cues. The panel is drawn again each time the playhead
    /// moves, and checking hundreds of cues takes longer than a frame, so this is worked out again
    /// only when the cues, the edits, or the recording's length change.
    private struct CaptionOverview {
        var cues: [StudioCaptionCue]
        var edits: StudioEdits
        var sourceDuration: Double
        var rows: [StudioCaptionRow]
        var findings: [StudioCaptionFinding]
        /// What the checks found in each cue, by its place in `project.captions.cues`.
        var problems: [Int: [StudioCaptionFindingKind]]
        var canExport: Bool
    }

    private var cachedCaptionOverview: CaptionOverview?

    private var captionOverview: CaptionOverview? {
        guard let project else { return nil }
        let cues = project.captions.cues
        let duration = project.sources.screen.duration
        if let cached = cachedCaptionOverview,
           cached.cues == cues, cached.edits == project.edits, cached.sourceDuration == duration {
            return cached
        }
        let rows = cues.indexesInTimeOrder().map { StudioCaptionRow(index: $0, cue: cues[$0]) }
        let findings = StudioCaptionQuality.check(cues, sourceDuration: duration)
        var problems: [Int: [StudioCaptionFindingKind]] = [:]
        // A finding counts the cue's place in time order, which is its row.
        for finding in findings where rows.indices.contains(finding.cue) {
            let index = rows[finding.cue].index
            if !(problems[index]?.contains(finding.kind) ?? false) {
                problems[index, default: []].append(finding.kind)
            }
        }
        let overview = CaptionOverview(
            cues: cues,
            edits: project.edits,
            sourceDuration: duration,
            rows: rows,
            findings: findings,
            problems: problems,
            canExport: !StudioCaptionResolver.resolve(project: project).isEmpty
        )
        cachedCaptionOverview = overview
        return overview
    }

    /// The cues, in time order, with their places in `project.captions.cues`.
    var captionRows: [StudioCaptionRow] { captionOverview?.rows ?? [] }

    private var cachedCaptionSearch: (query: String, cues: [StudioCaptionCue], rows: [StudioCaptionRow])?

    /// The rows whose text has `query` in it, ignoring case and accents. All of them for an empty
    /// query. The panel asks each time the playhead moves, so the last answer is kept.
    func captionRows(matching query: String) -> [StudioCaptionRow] {
        let rows = captionRows
        let query = StudioCaptionSearch.normalized(query)
        guard !query.isEmpty, let cues = captionOverview?.cues else { return rows }
        if let cached = cachedCaptionSearch, cached.query == query, cached.cues == cues {
            return cached.rows
        }
        let found = rows.filter { StudioCaptionSearch.matches(StudioCaptionText.rowText($0.cue), query: query) }
        cachedCaptionSearch = (query, cues, found)
        return found
    }

    /// What the checks found in a cue, by its place in `project.captions.cues`.
    func captionProblems(at index: Int) -> [StudioCaptionFindingKind] {
        captionOverview?.problems[index] ?? []
    }

    /// The cue the video shows at the playhead, as its place in `project.captions.cues`.
    var captionIndexAtPlayhead: Int? {
        let rows = captionRows
        return StudioCaptionEditing.position(at: playhead, in: rows).map { rows[$0].index }
    }

    var selectedCaption: StudioCaptionCue? {
        guard let index = selectedCaptionIndex, let cues = project?.captions.cues, cues.indices.contains(index) else { return nil }
        return cues[index]
    }

    var captionFindings: [StudioCaptionFinding] { captionOverview?.findings ?? [] }

    var canExportCaptions: Bool { captionOverview?.canExport ?? false }

    // MARK: - Generating Captions

    var canGenerateCaptions: Bool { isEditable && !isGeneratingCaptions }

    var canCancelCaptionGeneration: Bool { isGeneratingCaptions && !isStoppingCaptionGeneration }

    var captionProgressText: String {
        isStoppingCaptionGeneration
            ? StudioCaptionText.stoppingMessage
            : StudioCaptionText.progressText(captionGenerationProgress)
    }

    /// Makes captions from the recording's sound. They replace the ones there are when they are
    /// ready, as one edit, so Undo brings those back.
    func generateCaptions(language: String?) {
        guard canGenerateCaptions, let project, let paths else { return }
        commitCaptionText()
        dismissCaptionNotice()
        captionLanguageAsked = language
        captionGenerationNumber += 1
        let generation = captionGenerationNumber
        isGeneratingCaptions = true
        isStoppingCaptionGeneration = false
        captionGenerationProgress = StudioCaptionProgress(stage: .readingSound)
        NSAccessibility.post(element: NSApp as Any, notification: .announcementRequested, userInfo: [
            .announcement: StudioCaptionText.generatingMessage,
        ])

        let generator = captionGenerator
        let report: @Sendable (StudioCaptionProgress) -> Void = { [weak self] progress in
            guard let self else { return }
            Task { @MainActor in
                self.reportCaptionProgress(progress, generation: generation)
            }
        }
        captionGenerationTask = Task { [weak self] in
            let outcome: Result<StudioGeneratedCaptions, Error>
            do {
                outcome = .success(try await generator.generate(project: project, paths: paths, language: language, progress: report))
            } catch {
                outcome = .failure(error)
            }
            self?.finishGeneratingCaptions(outcome)
        }
    }

    private func reportCaptionProgress(_ progress: StudioCaptionProgress, generation: Int) {
        guard captionGenerationNumber == generation, isGeneratingCaptions else { return }
        let next = captionGenerationProgress.advanced(to: progress)
        if next != captionGenerationProgress {
            captionGenerationProgress = next
        }
    }

    func cancelCaptionGeneration() {
        guard canCancelCaptionGeneration else { return }
        isStoppingCaptionGeneration = true
        captionGenerationTask?.cancel()
        NSAccessibility.post(element: NSApp as Any, notification: .announcementRequested, userInfo: [
            .announcement: StudioCaptionText.stoppingMessage,
        ])
    }

    private func finishGeneratingCaptions(_ outcome: Result<StudioGeneratedCaptions, Error>) {
        let wasStopped = isStoppingCaptionGeneration
        isGeneratingCaptions = false
        isStoppingCaptionGeneration = false
        captionGenerationTask = nil
        guard state == .ready else { return }

        let notice: StudioCaptionNotice
        switch outcome {
        case .success where wasStopped:
            notice = StudioCaptionNotice(.info, StudioCaptionText.cancelledMessage)
        case .success(let heard) where heard.cues.isEmpty:
            notice = noSpeechNotice()
        case .success(let heard):
            guard isEditable else {
                notice = StudioCaptionNotice(.problem, "The captions could not be put in the project. Try again.")
                break
            }
            // What was being typed goes in first, so Undo brings it back with the old captions.
            commitCaptionText()
            selectedCaptionIndex = nil
            edit { $0.setGeneratedCaptions(language: heard.language, cues: heard.cues) }
            notice = StudioCaptionNotice(.done, StudioCaptionText.generatedMessage(heard.cues.count))
        case .failure(let error) where error is CancellationError || wasStopped:
            notice = StudioCaptionNotice(.info, StudioCaptionText.cancelledMessage)
        case .failure(let error as StudioCaptionGenerationError) where error == .noSpeech:
            notice = noSpeechNotice()
        case .failure(let error):
            notice = StudioCaptionNotice(.problem, error.localizedDescription)
        }
        showCaptionNotice(notice)

        // Generating can take minutes, and whoever asked may have gone to another window. They
        // are told how it went unless they stopped it themselves.
        if notice.kind != .info, isWindowInFront?() == false {
            SaveService.shared.showNotice(
                StudioCaptionPanelText.notificationMessage(for: notice, clipName: clipName),
                title: StudioCaptionPanelText.notificationTitle(for: notice),
                studioProjectID: projectID,
                allowsInAppFallback: false
            )
        } else {
            announce(notice.spokenText)
        }
    }

    private func noSpeechNotice() -> StudioCaptionNotice {
        StudioCaptionNotice(
            .noSpeech,
            StudioCaptionPanelText.noSpeechMessage(
                languageName: captionLanguageAsked.flatMap { Locale.current.localizedString(forIdentifier: $0) },
                hasCaptions: !(project?.captions.cues.isEmpty ?? true)
            ),
            title: StudioCaptionPanelText.noSpeechTitle
        )
    }

    /// Shows `notice` in the Captions panel. One that says something went well goes after a while.
    private func showCaptionNotice(_ notice: StudioCaptionNotice) {
        captionNoticeTimer?.cancel()
        captionNotice = notice
        guard notice.kind == .done || notice.kind == .info else { return }
        let id = notice.id
        captionNoticeTimer = Task { [weak self] in
            try? await Task.sleep(for: StudioCaptionNotice.shownFor)
            guard !Task.isCancelled, let self, self.captionNotice?.id == id else { return }
            self.captionNotice = nil
        }
    }

    func dismissCaptionNotice() {
        captionNoticeTimer?.cancel()
        captionNoticeTimer = nil
        captionNotice = nil
    }

    /// Selects a cue and moves the playhead to where it starts, or lets go with nil. A selected
    /// zoom, cut, or speed change is let go, so that Delete does not take away what is not on show.
    func selectCaption(at index: Int?) {
        commitCaptionText()
        guard let index, let cues = project?.captions.cues, cues.indices.contains(index) else {
            selectedCaptionIndex = nil
            return
        }
        selectedCaptionIndex = index
        selectNothing()
        seek(to: cues[index].start)
    }

    func setPendingCaptionText(_ text: String) {
        pendingCaptionText = text
    }

    /// Puts what was typed for the selected cue into the project.
    func commitCaptionText() {
        guard let text = pendingCaptionText else { return }
        pendingCaptionText = nil
        guard let index = selectedCaptionIndex else { return }
        edit { $0.setCaptionText(at: index, to: text) }
    }

    func removeSelectedCaption() {
        pendingCaptionText = nil
        guard let index = selectedCaptionIndex else { return }
        selectedCaptionIndex = nil
        edit { $0.removeCaption(at: index) }
        announce(StudioCaptionPanelText.deletedMessage)
    }

    /// Whether the selected cue can be split: at the playhead, or in its middle when the playhead
    /// is not inside it.
    var canSplitSelectedCaption: Bool {
        guard isEditable, let cue = selectedCaption else { return false }
        return StudioCaptionEditing.splitTime(of: cue, playhead: playhead) != nil
    }

    /// Whether the selected cue has one after it to be put together with.
    var canMergeSelectedCaption: Bool {
        guard isEditable, let index = selectedCaptionIndex, let cues = project?.captions.cues else { return false }
        return StudioCaptionEditing.canMergeWithNext(cues, at: index)
    }

    /// Splits the selected cue at the playhead, or in its middle when the playhead is not inside
    /// it, and selects the second part.
    func splitSelectedCaptionAtPlayhead() {
        commitCaptionText()
        guard isEditable, let index = selectedCaptionIndex, let cue = selectedCaption,
              let time = StudioCaptionEditing.splitTime(of: cue, playhead: playhead) else { return }
        var second: Int?
        edit { second = $0.splitCaption(at: index, sourceTime: time) }
        guard let second else { return }
        selectedCaptionIndex = second
        announce(StudioCaptionPanelText.splitMessage)
    }

    /// Puts the selected cue together with the one after it, and selects what they make.
    func mergeSelectedCaptionWithNext() {
        commitCaptionText()
        guard isEditable, let index = selectedCaptionIndex else { return }
        var merged: Int?
        edit { merged = $0.mergeCaptionWithNext(at: index) }
        guard let merged else { return }
        selectedCaptionIndex = merged
        announce(StudioCaptionPanelText.mergedMessage)
    }

    /// Moves one end of the selected cue by `seconds`, for the panel's step buttons.
    func nudgeSelectedCaption(_ edge: StudioCaptionEdge, by seconds: Double) {
        guard let cue = selectedCaption else { return }
        retimeSelectedCaption(edge, to: (edge == .start ? cue.start : cue.end) + seconds)
    }

    /// Puts one end of the selected cue at the playhead. The playhead stays where it is.
    func setSelectedCaptionAtPlayhead(_ edge: StudioCaptionEdge) {
        retimeSelectedCaption(edge, to: playhead)
    }

    private func retimeSelectedCaption(_ edge: StudioCaptionEdge, to time: Double) {
        commitCaptionText()
        guard isEditable, let index = selectedCaptionIndex else { return }
        var moved: Int?
        edit { moved = $0.retimeCaption(at: index, edge: edge, to: time) }
        guard let moved else { return }
        selectedCaptionIndex = moved
        if let cue = selectedCaption {
            announce(StudioCaptionText.rangeText(cue))
        }
    }

    /// Makes the safe fixes, and says what came of them and how many problems are left.
    func fixCaptions() {
        commitCaptionText()
        selectedCaptionIndex = nil
        var applied = 0
        edit { applied = $0.fixCaptions() }
        reportCaptions(StudioCaptionText.fixedMessage(applied, left: captionFindings.count))
    }

    /// Where the selected cue is among the rows, in time order.
    var selectedCaptionRowPosition: Int? {
        guard let index = selectedCaptionIndex else { return nil }
        return captionRows.firstIndex { $0.index == index }
    }

    /// Whether there is a cue `step` rows from the selected one, in time order.
    func canSelectCaption(by step: Int) -> Bool {
        guard let position = selectedCaptionRowPosition else { return false }
        return captionRows.indices.contains(position + step)
    }

    /// Selects the cue `step` rows from the selected one, in time order: -1 for the one before.
    func selectCaption(by step: Int) {
        guard let position = selectedCaptionRowPosition else { return }
        let rows = captionRows
        guard rows.indices.contains(position + step) else { return }
        selectCaption(at: rows[position + step].index)
    }

    /// Whether the checks found a problem that Next Problem can go to.
    var hasCaptionProblems: Bool { !(captionOverview?.problems.isEmpty ?? true) }

    /// Selects the next cue, in time order, that the checks found a problem in: after the selected
    /// one, or the first when none is selected, going round after the last.
    func selectNextCaptionProblem() {
        guard let overview = captionOverview else { return }
        let rows = overview.rows
        let next = StudioCaptionEditing.nextProblem(count: rows.count, after: selectedCaptionRowPosition) {
            overview.problems[rows[$0].index] != nil
        }
        guard let next else { return }
        selectCaption(at: rows[next].index)
    }

    /// Asks for a caption file and puts its cues in place of the captions.
    func importCaptions(timing: StudioCaptionTiming) {
        guard isEditable, let editor else { return }
        commitCaptionText()
        pause()
        let panel = NSOpenPanel()
        panel.title = "Import Captions"
        panel.prompt = "Import"
        panel.allowedContentTypes = ["srt", "vtt"].compactMap { UTType(filenameExtension: $0) }
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }

        let hasAccess = url.startAccessingSecurityScopedResource()
        defer { if hasAccess { url.stopAccessingSecurityScopedResource() } }
        do {
            let contents = StudioCaptionFiles.read(
                try StudioCaptionFiles.readFile(url),
                timing: timing,
                timeMap: editor.timeMap
            )
            guard !contents.cues.isEmpty else {
                reportCaptionProblem(StudioCaptionText.nothingToImport)
                return
            }
            selectedCaptionIndex = nil
            edit { $0.replaceCaptions(contents.cues) }
            reportCaptions(StudioCaptionText.importedMessage(count: contents.cues.count, skipped: contents.skippedBlocks))
        } catch {
            reportCaptionProblem("The captions could not be read: \(error.localizedDescription)")
        }
    }

    /// Asks where to write the captions the video has, and writes them there.
    func exportCaptions(_ format: StudioCaptionFormat) {
        commitCaptionText()
        guard let project else { return }
        let captions = StudioCaptionResolver.resolve(project: project)
        guard !captions.isEmpty else { return }
        pause()

        let savedFolder = (try? savedProjectFile())?.deletingLastPathComponent()
        let panel = NSSavePanel()
        panel.title = "Export Captions"
        panel.prompt = "Export"
        panel.allowedContentTypes = [UTType(filenameExtension: format.fileExtension)].compactMap { $0 }
        panel.canCreateDirectories = true
        panel.directoryURL = savedFolder.flatMap { FileManager.default.fileExists(atPath: $0.path) ? $0 : nil }
            ?? SaveService.shared.outputDirectoryURL(for: .video)
        panel.nameFieldStringValue = StudioProjectStore.folderName(for: clipName) + "." + format.fileExtension
        guard panel.runModal() == .OK, let url = panel.url else { return }

        let hasAccess = url.startAccessingSecurityScopedResource()
        defer { if hasAccess { url.stopAccessingSecurityScopedResource() } }
        do {
            let chosen = StudioCaptionFormat(path: url.path) ?? format
            try Data(StudioCaptionFiles.write(chosen, captions).utf8).write(to: url, options: .atomic)
            reportCaptions(StudioCaptionText.exportedMessage)
        } catch {
            reportCaptionProblem("The captions could not be written: \(error.localizedDescription)")
        }
    }

    private func reportCaptions(_ message: String) {
        showCaptionNotice(StudioCaptionNotice(.done, message))
        announce(message)
    }

    private func reportCaptionProblem(_ message: String) {
        showCaptionNotice(StudioCaptionNotice(.problem, message))
        AccessibilityAnnouncementService.shared.announce(message, priority: .high)
    }

    func setLayout(_ layout: StudioLayout) {
        editCurrentScene { $0.setLayout(layout) }
    }

    func setCanvasAspect(_ aspect: StudioCanvasAspect) {
        edit { $0.setCanvasAspect(aspect) }
    }

    func setCanvasPadding(_ value: Double) {
        edit { $0.setCanvasPadding(value) }
    }

    func applyBackgroundPreset(_ preset: ExportBackgroundPreset) {
        let primary = Self.hexString(for: preset.primary)
        let secondary = preset.secondary.map { Self.hexString(for: $0) }
        switch preset.style {
        case .solid:
            edit { $0.setBackground(style: .solid, preset: preset.id, primary: primary, secondary: nil) }
        case .gradient:
            edit { $0.setBackground(style: .gradient, preset: preset.id, primary: primary, secondary: secondary) }
        case .transparent, .wallpaper:
            break
        }
    }

    func setBackgroundStyle(_ style: StudioBackgroundStyle) {
        guard style == .solid || style == .gradient,
              let current = project?.canvas.background else { return }
        let secondary = style == .gradient ? current.secondary ?? "#2EE0BF" : current.secondary
        edit {
            $0.setBackground(
                style: style,
                preset: nil,
                primary: current.primary,
                secondary: secondary,
                gradientDirectionDegrees: current.gradientDirectionDegrees
            )
        }
    }

    func setBackgroundPrimaryColor(_ color: Color) {
        guard let current = project?.canvas.background else { return }
        let style = current.style == .gradient ? StudioBackgroundStyle.gradient : .solid
        edit {
            $0.setBackground(
                style: style,
                preset: nil,
                primary: Self.hexString(for: color),
                secondary: current.secondary,
                gradientDirectionDegrees: current.gradientDirectionDegrees
            )
        }
    }

    func setBackgroundSecondaryColor(_ color: Color) {
        guard let current = project?.canvas.background else { return }
        edit {
            $0.setBackground(
                style: .gradient,
                preset: nil,
                primary: current.primary,
                secondary: Self.hexString(for: color),
                gradientDirectionDegrees: current.gradientDirectionDegrees
            )
        }
    }

    func setGradientDirectionDegrees(_ degrees: Double) {
        guard let current = project?.canvas.background, degrees.isFinite else { return }
        edit {
            $0.setBackground(
                style: .gradient,
                preset: nil,
                primary: current.primary,
                secondary: current.secondary,
                gradientDirectionDegrees: degrees
            )
        }
    }

    /// No background: the canvas is black, as in a recording made without Studio.
    func removeBackground() {
        edit {
            let current = $0.project.canvas.background
            $0.setBackground(style: .none, preset: nil, primary: current.primary, secondary: current.secondary)
        }
    }

    func setScreenCornerRadius(_ value: Double) {
        edit { $0.setScreenCornerRadius(value) }
    }

    func setScreenShadow(_ value: Double) {
        edit { $0.setScreenShadow(value) }
    }

    func setCameraShape(_ shape: StudioCameraShape) {
        edit { $0.setCameraShape(shape) }
    }

    func setCameraCornerRadius(_ value: Double) {
        edit { $0.setCameraCornerRadius(value) }
    }

    func setCameraBubbleSize(_ value: Double) {
        editCurrentScene { $0.setCameraBubbleSize(value) }
    }

    func setCameraAnchor(_ anchor: StudioAnchor) {
        editCurrentScene { $0.setCameraAnchor(anchor) }
    }

    /// Moves the bubble sideways from its corner. The other offset stays as the scene has it.
    func setCameraBubbleOffsetX(_ x: Double) {
        editCurrentScene { model in
            let y = model.currentScene.bubble.offsetY
            model.setCameraBubbleOffsets(x: x, y: y)
        }
    }

    /// Moves the bubble up or down from its corner. The other offset stays as the scene has it.
    func setCameraBubbleOffsetY(_ y: Double) {
        editCurrentScene { model in
            let x = model.currentScene.bubble.offsetX
            model.setCameraBubbleOffsets(x: x, y: y)
        }
    }

    func moveBubble(topLeft: CGPoint, canvasSize: CGSize) {
        editCurrentScene { $0.moveBubble(topLeft: topLeft, canvasSize: canvasSize) }
    }

    func setCameraMirror(_ value: Bool) {
        edit { $0.setCameraMirror(value) }
    }

    func setCameraCutout(_ value: StudioCameraCutout) {
        edit { $0.setCameraCutout(value) }
    }

    func setCameraBorderWidth(_ value: Double) {
        edit { $0.setCameraBorderWidth(value) }
    }

    func setCameraShadow(_ value: Double) {
        edit { $0.setCameraShadow(value) }
    }

    /// Puts the camera on the other side. Its share stays as the scene has it.
    func setCameraSide(_ side: StudioCameraSide) {
        editCurrentScene { model in
            let fraction = model.currentScene.split.cameraFraction
            model.setSideBySide(cameraSide: side, fraction: fraction)
        }
    }

    /// Sets how much of the canvas the camera takes side by side. Its side stays as the scene has it.
    func setCameraShare(_ fraction: Double) {
        editCurrentScene { model in
            let side = model.currentScene.split.cameraSide
            model.setSideBySide(cameraSide: side, fraction: fraction)
        }
    }

    func setMuted(_ value: Bool) {
        edit { $0.setMuted(value) }
    }

    func setVolume(_ value: Double) {
        edit { $0.setVolume(value) }
    }

    func setSystemVolume(_ value: Double) {
        edit { $0.setSystemVolume(value) }
    }

    func setMicrophoneVolume(_ value: Double) {
        edit { $0.setMicrophoneVolume(value) }
    }

    func setCaptionsDrawn(_ value: Bool) {
        edit { $0.setCaptionsDrawn(value) }
    }

    func setCaptionSize(_ value: StudioCaptionSize) {
        edit { $0.setCaptionSize(value) }
    }

    func setCaptionPosition(_ value: StudioCaptionPosition) {
        edit { $0.setCaptionPosition(value) }
    }

    func setCaptionAppearance(_ value: StudioCaptionAppearance) {
        edit { $0.setCaptionAppearance(value) }
    }

    func setClickRingsEnabled(_ value: Bool) {
        edit { $0.setClickRingsEnabled(value) }
    }

    func setBrandingEnabled(_ value: Bool) {
        edit { $0.setBrandingEnabled(value) }
    }

    // MARK: - Crops

    func setScreenCropInset(_ edge: StudioCropEdge, to value: Double) {
        edit { $0.setScreenCropInset(edge, to: value) }
    }

    func clearScreenCrop() {
        edit { $0.clearScreenCrop() }
    }

    func setCameraCropInset(_ edge: StudioCropEdge, to value: Double) {
        edit { $0.setCameraCropInset(edge, to: value) }
    }

    func clearCameraCrop() {
        edit { $0.clearCameraCrop() }
    }

    // MARK: - Zooms
    //
    // An index is a zoom's place in `project.zooms`, which is kept in time order. One zoom can be
    // selected. An edit to the selected zoom takes the selection with it, and so does adding a
    // zoom. Through every other edit, and undo and redo, the selection follows its zoom as
    // `StudioEditorModel.zoomIndex(following:from:to:)` finds it, or lets go when the zoom is gone.

    /// Selects a zoom, or none with nil or a place that has no zoom. The playhead stays. A
    /// selected cut or speed change is let go when a zoom is selected.
    func selectZoom(_ index: Int?) {
        setSelectedZoomIndex(index)
        if selectedZoomIndex != nil {
            setSelectedCutIndex(nil)
            setSelectedSpeedIndex(nil)
            setSelectedAnnotationIndex(nil)
            showInspectorPanel(.zoom)
        }
    }

    /// Shows an inspector panel, or the nearest one this recording has. The rail's buttons do
    /// this, and so does taking hold of something a panel edits. Not an edit: Undo leaves it.
    func showInspectorPanel(_ panel: StudioInspectorPanel) {
        let resolved = StudioInspectorPanel.resolved(panel, hasCamera: hasCamera)
        if inspectorPanel != resolved {
            inspectorPanel = resolved
        }
    }

    /// Lets go of whatever is selected: a zoom, a cut, a speed change, or an annotation. The playhead stays. A
    /// press on an empty part of a lane does this: selecting no zoom alone would leave a selected
    /// cut as it is.
    func selectNothing() {
        setSelectedZoomIndex(nil)
        setSelectedCutIndex(nil)
        setSelectedSpeedIndex(nil)
        setSelectedAnnotationIndex(nil)
    }

    /// Selects a zoom and moves the playhead to where it has moved in.
    @discardableResult
    func selectAndShowZoom(_ index: Int?) -> Bool {
        guard isEditable, let index, let time = editor?.zoomLookTime(at: index) else { return false }
        selectZoom(index)
        scrub(to: time)
        return true
    }

    /// Selects the zoom after the selected one and shows it. With nothing selected, the zoom at the
    /// playhead or the first one after it. False when there is none.
    @discardableResult
    func selectNextZoom() -> Bool {
        selectAndShowZoom(editor?.zoomIndex(after: selectedZoomIndex, playhead: playhead))
    }

    /// Selects the zoom before the selected one and shows it. With nothing selected, the zoom at
    /// the playhead or the last one before it. False when there is none.
    @discardableResult
    func selectPreviousZoom() -> Bool {
        selectAndShowZoom(editor?.zoomIndex(before: selectedZoomIndex, playhead: playhead))
    }

    /// The Previous zoom button and menu item. The zoom it lands on is read out: neither says
    /// anything of where it lands by itself.
    func showPreviousZoom() {
        if selectPreviousZoom() {
            announceSelectedZoom()
        }
    }

    /// The Next zoom button and menu item. The zoom it lands on is read out.
    func showNextZoom() {
        if selectNextZoom() {
            announceSelectedZoom()
        }
    }

    private func announceSelectedZoom() {
        guard let index = selectedZoomIndex, let zoom = selectedZoom else { return }
        announce(StudioEditorModel.zoomStepText(index: index, count: zooms.count, zoom: zoom))
    }

    /// Adds a zoom at the playhead and selects it. Where a zoom already is, that one is selected
    /// instead. A zoom starts unzoomed, so a paused playhead then moves to where the new zoom has
    /// moved in, which shows what it looks at.
    func addZoomAtPlayhead() {
        guard isEditable else { return }
        let time = playhead
        let recordedEvents = events
        var result = StudioZoomEditResult(changed: false, index: nil)
        editAndSelect { model in
            result = model.addZoom(at: time, events: recordedEvents)
            if let index = result.index {
                return .zoom(index)
            }
            return .follow
        }

        if result.changed {
            announce(StudioEditorModel.zoomAddedMessage)
            if !isPlaying, let index = result.index, let lookTime = editor?.zoomLookTime(at: index) {
                scrub(to: lookTime)
            }
        } else if result.index != nil {
            announce(StudioEditorModel.zoomAlreadyThereMessage)
        } else {
            announce(StudioEditorModel.noRoomForZoomMessage)
        }
    }

    func removeSelectedZoom() {
        guard let index = selectedZoomIndex else { return }
        let result = editZoom(at: index) { $0.removeZoom(at: index) }
        if result.changed {
            announce(StudioEditorModel.zoomDeletedMessage)
        }
    }

    /// Moves a zoom's start, from the lane or the inspector, and shows the frame it now starts on.
    /// Returns where the zoom is afterwards.
    @discardableResult
    func setZoomStart(at index: Int, to sourceTime: Double) -> Int? {
        let result = editZoom(at: index) { $0.setZoomStart(at: index, to: sourceTime) }
        if isEditable, let place = result.index, zooms.indices.contains(place) {
            scrub(to: zooms[place].start)
        }
        return result.index
    }

    /// Moves a zoom's end and shows the frame it now ends on. Returns where the zoom is afterwards.
    @discardableResult
    func setZoomEnd(at index: Int, to sourceTime: Double) -> Int? {
        let result = editZoom(at: index) { $0.setZoomEnd(at: index, to: sourceTime) }
        if isEditable, let place = result.index, zooms.indices.contains(place) {
            scrub(to: zooms[place].end)
        }
        return result.index
    }

    /// Moves a whole zoom so it starts at `sourceTime`, keeping its length. The playhead stays.
    /// Returns where the zoom is afterwards.
    @discardableResult
    func moveZoom(at index: Int, to sourceTime: Double) -> Int? {
        editZoom(at: index) { $0.moveZoom(at: index, to: sourceTime) }.index
    }

    func setSelectedZoomStartAtPlayhead() {
        guard let index = selectedZoomIndex else { return }
        setZoomStart(at: index, to: playhead)
    }

    func setSelectedZoomEndAtPlayhead() {
        guard let index = selectedZoomIndex else { return }
        setZoomEnd(at: index, to: playhead)
    }

    /// Moves the selected zoom's start by `seconds`, for the inspector's step buttons.
    func nudgeSelectedZoomStart(by seconds: Double) {
        guard let index = selectedZoomIndex, let zoom = selectedZoom else { return }
        setZoomStart(at: index, to: zoom.start + seconds)
    }

    /// Moves the selected zoom's end by `seconds`, for the inspector's step buttons.
    func nudgeSelectedZoomEnd(by seconds: Double) {
        guard let index = selectedZoomIndex, let zoom = selectedZoom else { return }
        setZoomEnd(at: index, to: zoom.end + seconds)
    }

    func setSelectedZoomScale(_ value: Double) {
        guard let index = selectedZoomIndex else { return }
        editZoom(at: index) { $0.setZoomScale(at: index, to: value) }
    }

    func setSelectedZoomFocusMode(_ mode: StudioZoomFocusMode) {
        guard let index = selectedZoomIndex else { return }
        editZoom(at: index) { $0.setZoomFocusMode(at: index, to: mode) }
    }

    /// Points the selected zoom at a place on its focus pad, from 0 to 1 across and down the pad.
    func setSelectedZoomFocusOnPad(x: Double, y: Double) {
        guard let index = selectedZoomIndex else { return }
        editZoom(at: index) { $0.setZoomFocusOnPad(at: index, x: x, y: y) }
    }

    func setSelectedZoomEaseIn(_ seconds: Double) {
        guard let index = selectedZoomIndex else { return }
        editZoom(at: index) { $0.setZoomEaseIn(at: index, to: seconds) }
    }

    func setSelectedZoomEaseOut(_ seconds: Double) {
        guard let index = selectedZoomIndex else { return }
        editZoom(at: index) { $0.setZoomEaseOut(at: index, to: seconds) }
    }

    /// Replaces the suggested zooms with new ones worked out from the recording's clicks, as one
    /// undo step, and says how many there are. Zooms the user made or changed stay.
    func suggestZooms() {
        guard canSuggestZooms else { return }
        showInspectorPanel(.zoom)
        let recordedEvents = events
        edit { model in
            let suggestions = StudioLayoutResolver.suggestZooms(project: model.project, events: recordedEvents)
            model.applyZoomSuggestions(suggestions)
        }
        announce(StudioEditorModel.zoomSuggestionsText(count: editor?.suggestedZoomCount ?? 0))
    }

    /// Removes the suggested zooms, as one undo step.
    func removeZoomSuggestions() {
        guard isEditable, hasSuggestedZooms else { return }
        edit { $0.applyZoomSuggestions([]) }
        announce(StudioEditorModel.zoomSuggestionsRemovedMessage)
    }

    // MARK: - Cuts
    //
    // An index is a cut's place in `project.edits.cuts`, which is kept in time order. One cut can
    // be selected, in place of a zoom. An edit to the selected cut takes the selection with it,
    // and so does adding a cut. Through every other edit, and undo and redo, the selection follows
    // its cut as `StudioEditorModel.cutIndex(following:from:to:)` finds it, or lets go when the
    // cut is gone.

    var cuts: [StudioTimeRange] { project?.edits.cuts ?? [] }

    var selectedCut: StudioTimeRange? {
        guard let index = selectedCutIndex, cuts.indices.contains(index) else { return nil }
        return cuts[index]
    }

    /// Whether adding a cut at the playhead has a cut to answer with: one fits there, or one is
    /// already there to select.
    var canAddCutAtPlayhead: Bool { isEditable && (editor?.canAddCut(at: playhead) ?? false) }

    /// Selects a cut, or none with nil or a place that has no cut. The playhead stays. A selected
    /// zoom or speed change is let go when a cut is selected.
    func selectCut(_ index: Int?) {
        setSelectedCutIndex(index)
        if selectedCutIndex != nil {
            setSelectedZoomIndex(nil)
            setSelectedSpeedIndex(nil)
            setSelectedAnnotationIndex(nil)
            showInspectorPanel(.cut)
        }
    }

    /// Selects a cut and moves the playhead to where it starts, on the first picture the video
    /// leaves out.
    @discardableResult
    func selectAndShowCut(_ index: Int?) -> Bool {
        guard isEditable, let index, cuts.indices.contains(index) else { return false }
        selectCut(index)
        scrub(to: cuts[index].start)
        return true
    }

    /// Selects the cut after the selected one and shows it. With nothing selected, the cut at the
    /// playhead or the first one after it. False when there is none.
    @discardableResult
    func selectNextCut() -> Bool {
        selectAndShowCut(editor?.cutIndex(after: selectedCutIndex, playhead: playhead))
    }

    /// Selects the cut before the selected one and shows it. With nothing selected, the cut at the
    /// playhead or the last one before it. False when there is none.
    @discardableResult
    func selectPreviousCut() -> Bool {
        selectAndShowCut(editor?.cutIndex(before: selectedCutIndex, playhead: playhead))
    }

    /// The Previous cut button and menu item. The cut it lands on is read out: neither says
    /// anything of where it lands by itself.
    func showPreviousCut() {
        if selectPreviousCut() {
            announceSelectedCut()
        }
    }

    /// The Next cut button and menu item. The cut it lands on is read out.
    func showNextCut() {
        if selectNextCut() {
            announceSelectedCut()
        }
    }

    private func announceSelectedCut() {
        guard let index = selectedCutIndex, let cut = selectedCut else { return }
        announce(StudioEditorModel.cutStepText(index: index, count: cuts.count, cut: cut))
    }

    /// Adds a cut at the playhead and selects it. Where a cut already is, that one is selected
    /// instead. The playhead stays where the cut starts.
    func addCutAtPlayhead() {
        guard isEditable else { return }
        let time = playhead
        var result = StudioCutEditResult(changed: false, index: nil)
        editAndSelect { model in
            result = model.addCut(at: time)
            if let index = result.index {
                return .cut(index)
            }
            return .follow
        }

        if result.changed {
            announce(StudioEditorModel.cutAddedMessage)
        } else if result.index != nil {
            announce(StudioEditorModel.cutAlreadyThereMessage)
        } else {
            announce(StudioEditorModel.noRoomForCutMessage)
        }
    }

    /// Deletes the selected cut, which puts its stretch back into the video.
    func removeSelectedCut() {
        guard let index = selectedCutIndex else { return }
        let result = editCut(at: index) { $0.removeCut(at: index) }
        if result.changed {
            announce(StudioEditorModel.cutDeletedMessage)
        }
    }

    /// Moves a cut's start, from the lane or the inspector, and shows the picture there, which is
    /// the first one the video leaves out. Returns where the cut is afterwards.
    @discardableResult
    func setCutStart(at index: Int, to sourceTime: Double) -> Int? {
        let result = editCut(at: index) { $0.setCutStart(at: index, to: sourceTime) }
        if isEditable, let place = result.index, cuts.indices.contains(place) {
            scrub(to: cuts[place].start)
        }
        return result.index
    }

    /// Moves a cut's end and shows the picture there, which is the one the video picks up again
    /// with. Returns where the cut is afterwards.
    @discardableResult
    func setCutEnd(at index: Int, to sourceTime: Double) -> Int? {
        let result = editCut(at: index) { $0.setCutEnd(at: index, to: sourceTime) }
        if isEditable, let place = result.index, cuts.indices.contains(place) {
            scrub(to: cuts[place].end)
        }
        return result.index
    }

    /// Moves a whole cut so it starts at `sourceTime`, keeping its length. The playhead stays.
    /// Returns where the cut is afterwards.
    @discardableResult
    func moveCut(at index: Int, to sourceTime: Double) -> Int? {
        editCut(at: index) { $0.moveCut(at: index, to: sourceTime) }.index
    }

    /// Starts the selected cut at the playhead. The playhead stays where it is.
    func setSelectedCutStartAtPlayhead() {
        guard let index = selectedCutIndex else { return }
        let time = playhead
        editCut(at: index) { $0.setCutStart(at: index, to: time) }
    }

    /// Ends the selected cut at the playhead. The playhead stays where it is.
    func setSelectedCutEndAtPlayhead() {
        guard let index = selectedCutIndex else { return }
        let time = playhead
        editCut(at: index) { $0.setCutEnd(at: index, to: time) }
    }

    /// Moves the selected cut's start by `seconds`, for the inspector's step buttons.
    func nudgeSelectedCutStart(by seconds: Double) {
        guard let index = selectedCutIndex, let cut = selectedCut else { return }
        setCutStart(at: index, to: cut.start + seconds)
    }

    /// Moves the selected cut's end by `seconds`, for the inspector's step buttons.
    func nudgeSelectedCutEnd(by seconds: Double) {
        guard let index = selectedCutIndex, let cut = selectedCut else { return }
        setCutEnd(at: index, to: cut.end + seconds)
    }

    // MARK: - Speed
    //
    // An index is a speed change's place in `project.edits.speed`, which is kept in time order.
    // One speed change can be selected, in place of a zoom or a cut. An edit to the selected one
    // takes the selection with it, and so does adding one. Through every other edit, and undo and
    // redo, the selection follows its speed change as
    // `StudioEditorModel.speedIndex(following:from:to:)` finds it, or lets go when it is gone.
    //
    // While playing, the preview goes through a speed change at its rate and without sound, as
    // the export does. See `applySpeedAndSound(at:)`.

    var speedChanges: [StudioSpeedRange] { project?.edits.speed ?? [] }

    var selectedSpeed: StudioSpeedRange? {
        guard let index = selectedSpeedIndex, speedChanges.indices.contains(index) else { return nil }
        return speedChanges[index]
    }

    /// Whether adding a speed change at the playhead has one to answer with: one fits there, or
    /// one is already there to select.
    var canAddSpeedAtPlayhead: Bool { isEditable && (editor?.canAddSpeed(at: playhead) ?? false) }

    /// Selects a speed change, or none with nil or a place that has none. The playhead stays. A
    /// selected zoom or cut is let go when a speed change is selected.
    func selectSpeed(_ index: Int?) {
        setSelectedSpeedIndex(index)
        if selectedSpeedIndex != nil {
            setSelectedAnnotationIndex(nil)
            setSelectedZoomIndex(nil)
            setSelectedCutIndex(nil)
            showInspectorPanel(.speed)
        }
    }

    /// Selects a speed change and moves the playhead to where it starts, on the first picture
    /// that plays at the other speed.
    @discardableResult
    func selectAndShowSpeed(_ index: Int?) -> Bool {
        guard isEditable, let index, speedChanges.indices.contains(index) else { return false }
        selectSpeed(index)
        scrub(to: speedChanges[index].start)
        return true
    }

    /// Selects the speed change after the selected one and shows it. With nothing selected, the
    /// one at the playhead or the first one after it. False when there is none.
    @discardableResult
    func selectNextSpeed() -> Bool {
        selectAndShowSpeed(editor?.speedIndex(after: selectedSpeedIndex, playhead: playhead))
    }

    /// Selects the speed change before the selected one and shows it. With nothing selected, the
    /// one at the playhead or the last one before it. False when there is none.
    @discardableResult
    func selectPreviousSpeed() -> Bool {
        selectAndShowSpeed(editor?.speedIndex(before: selectedSpeedIndex, playhead: playhead))
    }

    /// The Previous speed change button and menu item. The one it lands on is read out: neither
    /// says anything of where it lands by itself.
    func showPreviousSpeed() {
        if selectPreviousSpeed() {
            announceSelectedSpeed()
        }
    }

    /// The Next speed change button and menu item. The one it lands on is read out.
    func showNextSpeed() {
        if selectNextSpeed() {
            announceSelectedSpeed()
        }
    }

    private func announceSelectedSpeed() {
        guard let index = selectedSpeedIndex, let speed = selectedSpeed else { return }
        announce(StudioEditorModel.speedStepText(index: index, count: speedChanges.count, speed: speed))
    }

    /// Adds a speed change at the playhead and selects it. Where one already is, that one is
    /// selected instead. The playhead stays where it starts.
    func addSpeedAtPlayhead() {
        guard isEditable else { return }
        let time = playhead
        var result = StudioSpeedEditResult(changed: false, index: nil)
        editAndSelect { model in
            result = model.addSpeed(at: time)
            if let index = result.index {
                return .speed(index)
            }
            return .follow
        }

        if result.changed {
            announce(StudioEditorModel.speedAddedMessage)
        } else if result.index != nil {
            announce(StudioEditorModel.speedAlreadyThereMessage)
        } else {
            announce(StudioEditorModel.noRoomForSpeedMessage)
        }
    }

    /// Deletes the selected speed change, so its stretch plays at the recording's own speed again.
    func removeSelectedSpeed() {
        guard let index = selectedSpeedIndex else { return }
        let result = editSpeed(at: index) { $0.removeSpeed(at: index) }
        if result.changed {
            announce(StudioEditorModel.speedDeletedMessage)
        }
    }

    /// Moves a speed change's start, from the lane or the inspector, and shows the picture there,
    /// which is the first one that plays at the other speed. Returns where it is afterwards.
    @discardableResult
    func setSpeedStart(at index: Int, to sourceTime: Double) -> Int? {
        let result = editSpeed(at: index) { $0.setSpeedStart(at: index, to: sourceTime) }
        if isEditable, let place = result.index, speedChanges.indices.contains(place) {
            scrub(to: speedChanges[place].start)
        }
        return result.index
    }

    /// Moves a speed change's end and shows the picture there, which is the first one at the
    /// recording's own speed again. Returns where it is afterwards.
    @discardableResult
    func setSpeedEnd(at index: Int, to sourceTime: Double) -> Int? {
        let result = editSpeed(at: index) { $0.setSpeedEnd(at: index, to: sourceTime) }
        if isEditable, let place = result.index, speedChanges.indices.contains(place) {
            scrub(to: speedChanges[place].end)
        }
        return result.index
    }

    /// Moves a whole speed change so it starts at `sourceTime`, keeping its length and its rate.
    /// The playhead stays. Returns where it is afterwards.
    @discardableResult
    func moveSpeed(at index: Int, to sourceTime: Double) -> Int? {
        editSpeed(at: index) { $0.moveSpeed(at: index, to: sourceTime) }.index
    }

    /// Sets how fast the selected speed change plays. The playhead stays.
    func setSelectedSpeedRate(_ rate: Double) {
        guard let index = selectedSpeedIndex else { return }
        editSpeed(at: index) { $0.setSpeedRate(at: index, to: rate) }
    }

    /// Starts the selected speed change at the playhead. The playhead stays where it is.
    func setSelectedSpeedStartAtPlayhead() {
        guard let index = selectedSpeedIndex else { return }
        let time = playhead
        editSpeed(at: index) { $0.setSpeedStart(at: index, to: time) }
    }

    /// Ends the selected speed change at the playhead. The playhead stays where it is.
    func setSelectedSpeedEndAtPlayhead() {
        guard let index = selectedSpeedIndex else { return }
        let time = playhead
        editSpeed(at: index) { $0.setSpeedEnd(at: index, to: time) }
    }

    /// Moves the selected speed change's start by `seconds`, for the inspector's step buttons.
    func nudgeSelectedSpeedStart(by seconds: Double) {
        guard let index = selectedSpeedIndex, let speed = selectedSpeed else { return }
        setSpeedStart(at: index, to: speed.start + seconds)
    }

    /// Moves the selected speed change's end by `seconds`, for the inspector's step buttons.
    func nudgeSelectedSpeedEnd(by seconds: Double) {
        guard let index = selectedSpeedIndex, let speed = selectedSpeed else { return }
        setSpeedEnd(at: index, to: speed.end + seconds)
    }

    // MARK: - Annotations
    //
    // An index is an annotation's place in `project.overlays.annotations`, the order they are drawn
    // in. One can be selected, in place of a zoom, a cut, or a speed change. Through an edit that
    // does not say, and undo and redo, the selection follows its annotation by its id.

    var annotations: [StudioAnnotation] { project?.overlays.annotations ?? [] }

    var selectedAnnotation: StudioAnnotation? {
        guard let index = selectedAnnotationIndex, annotations.indices.contains(index) else { return nil }
        return annotations[index]
    }

    var canAddAnnotation: Bool { isEditable && (editor?.canAddAnnotation ?? false) }

    var canSelectPreviousAnnotation: Bool {
        isEditable && editor?.annotationIndex(before: selectedAnnotationIndex, sourceTime: playhead) != nil
    }

    var canSelectNextAnnotation: Bool {
        isEditable && editor?.annotationIndex(after: selectedAnnotationIndex, sourceTime: playhead) != nil
    }

    /// Selects an annotation, or none with nil. The playhead stays. What else was selected is let
    /// go, and the inspector shows the Annotate panel.
    func selectAnnotation(_ index: Int?) {
        if index != selectedAnnotationIndex { endAnnotationColorChoice() }
        setSelectedAnnotationIndex(index)
        if selectedAnnotationIndex != nil {
            setSelectedZoomIndex(nil)
            setSelectedCutIndex(nil)
            setSelectedSpeedIndex(nil)
            showInspectorPanel(.annotate)
        }
    }

    /// Selects an annotation and moves the playhead to where it starts.
    @discardableResult
    func selectAndShowAnnotation(_ index: Int?) -> Bool {
        guard isEditable, let index, annotations.indices.contains(index) else { return false }
        selectAnnotation(index)
        scrub(to: annotations[index].start)
        return true
    }

    /// Selects an annotation from the panel's list, and moves the playhead to where it starts when
    /// it would not be on the picture otherwise.
    func selectAnnotationInView(_ index: Int) {
        guard annotations.indices.contains(index) else { return }
        let annotation = annotations[index]
        if annotation.start <= playhead, playhead < annotation.end {
            selectAnnotation(index)
        } else {
            selectAndShowAnnotation(index)
        }
    }

    func showPreviousAnnotation() {
        if selectAndShowAnnotation(editor?.annotationIndex(before: selectedAnnotationIndex, sourceTime: playhead)) {
            announceSelectedAnnotation()
        }
    }

    func showNextAnnotation() {
        if selectAndShowAnnotation(editor?.annotationIndex(after: selectedAnnotationIndex, sourceTime: playhead)) {
            announceSelectedAnnotation()
        }
    }

    private func announceSelectedAnnotation() {
        guard let index = selectedAnnotationIndex, let annotation = selectedAnnotation else { return }
        announce(StudioEditorModel.annotationStepText(index: index, count: annotations.count, annotation: annotation))
    }

    /// Adds an annotation at the playhead, on top of the others, and selects it. The playhead stays.
    func addAnnotationAtPlayhead(kind: String) {
        guard isEditable else { return }
        let time = playhead
        let recordedEvents = events
        var result = StudioAnnotationEditResult(changed: false, index: nil)
        editAndSelect { model in
            result = model.addAnnotation(kind: kind, at: time, events: recordedEvents)
            if let index = result.index {
                return .annotation(index)
            }
            return .follow
        }
        announce(result.changed ? StudioEditorModel.annotationAddedMessage(kind) : StudioEditorModel.noRoomForAnnotationMessage)
    }

    func removeSelectedAnnotation() {
        guard let index = selectedAnnotationIndex else { return }
        if editAnnotation(at: index, { $0.removeAnnotation(at: index) }).changed {
            announce(StudioEditorModel.annotationDeletedMessage)
        }
    }

    /// Moves when an annotation starts, and shows the first picture that has it.
    @discardableResult
    func setAnnotationStart(at index: Int, to sourceTime: Double) -> Int? {
        let result = editAnnotation(at: index) { $0.setAnnotationStart(at: index, to: sourceTime) }
        if isEditable, let place = result.index, annotations.indices.contains(place) {
            scrub(to: annotations[place].start)
        }
        return result.index
    }

    /// Moves when an annotation ends, and shows the last picture that has it.
    @discardableResult
    func setAnnotationEnd(at index: Int, to sourceTime: Double) -> Int? {
        let result = editAnnotation(at: index) { $0.setAnnotationEnd(at: index, to: sourceTime) }
        if isEditable, let place = result.index, annotations.indices.contains(place), let editor {
            scrub(to: max(annotations[place].start, annotations[place].end - editor.frameDuration))
        }
        return result.index
    }

    /// Moves a whole annotation so it starts at `sourceTime`. The playhead stays.
    @discardableResult
    func moveAnnotation(at index: Int, to sourceTime: Double) -> Int? {
        editAnnotation(at: index) { $0.moveAnnotation(at: index, to: sourceTime) }.index
    }

    func nudgeSelectedAnnotationStart(by seconds: Double) {
        guard let index = selectedAnnotationIndex, let annotation = selectedAnnotation else { return }
        setAnnotationStart(at: index, to: annotation.start + seconds)
    }

    func nudgeSelectedAnnotationEnd(by seconds: Double) {
        guard let index = selectedAnnotationIndex, let annotation = selectedAnnotation else { return }
        setAnnotationEnd(at: index, to: annotation.end + seconds)
    }

    func setSelectedAnnotationStartAtPlayhead() {
        guard let index = selectedAnnotationIndex else { return }
        let time = playhead
        editAnnotation(at: index) { $0.setAnnotationStart(at: index, to: time) }
    }

    func setSelectedAnnotationEndAtPlayhead() {
        guard let index = selectedAnnotationIndex else { return }
        let time = playhead
        editAnnotation(at: index) { $0.setAnnotationEnd(at: index, to: time) }
    }

    func setSelectedAnnotationText(_ text: String) {
        guard let index = selectedAnnotationIndex else { return }
        editAnnotation(at: index) { $0.setAnnotationText(at: index, to: text) }
    }

    /// The text field has lost the focus: what is typed from now on is another undo step.
    func endAnnotationTyping() {
        editor?.endAnnotationTyping()
    }

    func setSelectedAnnotationStyle(_ style: StudioAnnotationStyle) {
        guard let index = selectedAnnotationIndex else { return }
        editAnnotation(at: index) { $0.setAnnotationStyle(at: index, to: style) }
    }

    /// A color has been chosen: the next one is another undo step.
    func endAnnotationColorChoice() {
        editor?.endAnnotationColorChoice()
    }

    func setSelectedAnnotationFill(_ color: Color) {
        guard let index = selectedAnnotationIndex else { return }
        let hex = Self.hexString(for: color)
        editAnnotation(at: index) { $0.setAnnotationFill(at: index, to: hex) }
    }

    func setSelectedAnnotationTextColor(_ color: Color) {
        guard let index = selectedAnnotationIndex else { return }
        let hex = Self.hexString(for: color)
        editAnnotation(at: index) { $0.setAnnotationTextColor(at: index, to: hex) }
    }

    func setSelectedAnnotationScale(_ scale: Double) {
        guard let index = selectedAnnotationIndex else { return }
        editAnnotation(at: index) { $0.setAnnotationScale(at: index, to: scale) }
    }

    /// The width and height of the selected rectangle, ellipse, or redaction, normalized.
    func setSelectedAnnotationSize(width: Double, height: Double) {
        guard let index = selectedAnnotationIndex else { return }
        editAnnotation(at: index) { $0.setAnnotationSize(at: index, width: width, height: height) }
    }

    func setSelectedAnnotationCornerRadius(_ radius: Double) {
        guard let index = selectedAnnotationIndex else { return }
        editAnnotation(at: index) { $0.setAnnotationCornerRadius(at: index, to: radius) }
    }

    /// Fills the inside of the selected rectangle or ellipse with `color`, or empties it with nil.
    func setSelectedAnnotationShapeFill(_ color: Color?) {
        guard let index = selectedAnnotationIndex else { return }
        let hex = color.map { Self.hexString(for: $0) } ?? ""
        editAnnotation(at: index) { $0.setAnnotationShapeFill(at: index, to: hex) }
    }

    func setSelectedAnnotationShapeFillOpacity(_ opacity: Double) {
        guard let index = selectedAnnotationIndex else { return }
        editAnnotation(at: index) { $0.setAnnotationShapeFillOpacity(at: index, to: opacity) }
    }

    func setSelectedAnnotationRedactStyle(_ style: StudioRedactStyle) {
        guard let index = selectedAnnotationIndex else { return }
        editAnnotation(at: index) { $0.setAnnotationRedactStyle(at: index, to: style) }
    }

    func setSelectedAnnotationRedactLevel(_ level: StudioRedactLevel) {
        guard let index = selectedAnnotationIndex else { return }
        editAnnotation(at: index) { $0.setAnnotationRedactLevel(at: index, to: level) }
    }

    /// Small, medium, or large, before the scale.
    func setSelectedAnnotationSize(_ size: StudioAnnotationSize) {
        guard let index = selectedAnnotationIndex else { return }
        editAnnotation(at: index) { $0.setAnnotationSize(at: index, to: size) }
    }

    /// Picks the selected emoji's emoji from the grid, as one undo step.
    func setSelectedAnnotationEmoji(_ emoji: String) {
        guard let index = selectedAnnotationIndex else { return }
        editAnnotation(at: index) { $0.setAnnotationEmoji(at: index, to: emoji) }
    }

    func setSelectedAnnotationShadow(_ shadow: Bool) {
        guard let index = selectedAnnotationIndex else { return }
        editAnnotation(at: index) { $0.setAnnotationShadow(at: index, to: shadow) }
    }

    var canDuplicateSelectedAnnotation: Bool { isEditable && selectedAnnotation != nil }

    /// Puts a copy of the selected annotation directly above it, nudged so both show, and selects it.
    func duplicateSelectedAnnotation() {
        guard isEditable, let index = selectedAnnotationIndex, let annotation = selectedAnnotation else { return }
        var result = StudioAnnotationEditResult(changed: false, index: nil)
        editAndSelect { model in
            result = model.duplicateAnnotation(at: index)
            if let copy = result.index {
                return .annotation(copy)
            }
            return .follow
        }
        if result.changed {
            announce("\(StudioEditorModel.annotationKindName(annotation.kind)) duplicated.")
        }
    }

    /// What is drawn at the playhead on a canvas of `canvasSize`, bottom first, for the preview to
    /// let a pointer take hold of. Nothing while the video plays, when there is nothing to hold still.
    func annotationHandles(canvasSize: CGSize) -> [StudioAnnotationHandle] {
        guard isEditable, !isPlaying, let project, canvasSize.width > 0, canvasSize.height > 0 else { return [] }
        let width = Double(canvasSize.width)
        let height = Double(canvasSize.height)
        let frame = StudioLayoutResolver.resolve(project: project, time: playhead, canvasWidth: width, canvasHeight: height, events: events)
        return StudioAnnotationGeometry.resolveAll(project: project, frame: frame, time: playhead, canvasWidth: width, canvasHeight: height).map { resolved in
            if resolved.isArrow {
                return StudioAnnotationHandle(index: resolved.index, kind: resolved.kind, tip: resolved.anchor, tail: resolved.tail, stroke: resolved.stroke)
            }
            if resolved.isArea {
                return StudioAnnotationHandle(index: resolved.index, kind: resolved.kind, tip: resolved.anchor, radius: resolved.radius, area: resolved.rect)
            }
            if resolved.kind == StudioAnnotationKind.emoji {
                let placed = StudioCompositor.annotationEmojiLayout(resolved, canvasWidth: width, canvasHeight: height)
                return StudioAnnotationHandle(index: resolved.index, kind: resolved.kind, tip: resolved.anchor, box: placed, radius: 0)
            }
            let layout = StudioCompositor.annotationBoxLayout(resolved, canvasWidth: width, canvasHeight: height)
            return StudioAnnotationHandle(index: resolved.index, kind: resolved.kind, tip: resolved.anchor, box: layout.box, radius: resolved.radius)
        }
    }

    /// Where a drag on the preview leaves an annotation that was `start` when it began. What the
    /// move did is handed back so that the preview can show a center line it settled on.
    @discardableResult
    func moveAnnotation(
        at index: Int,
        from start: StudioAnnotation,
        part: StudioAnnotationPart,
        byX: Double,
        byY: Double,
        drawnBoxCenter: StudioAnnotationPoint?,
        canvasSize: CGSize
    ) -> StudioAnnotationMove? {
        guard let project else { return nil }
        let width = Double(canvasSize.width)
        let height = Double(canvasSize.height)
        let frame = StudioLayoutResolver.resolve(project: project, time: playhead, canvasWidth: width, canvasHeight: height, events: events)
        guard let move = StudioAnnotationGeometry.moved(start, part: part, byX: byX, byY: byY, drawnBoxCenter: drawnBoxCenter, frame: frame, canvasWidth: width, canvasHeight: height) else {
            return nil
        }
        editAnnotation(at: index) { $0.moveAnnotation(at: index, to: move) }
        return move
    }

    /// What the selected annotation is fixed to. It stays where it is in the picture where it can.
    func setSelectedAnnotationAttach(_ attach: StudioAnnotationAttach) {
        guard let index = selectedAnnotationIndex else { return }
        let time = playhead
        let recordedEvents = events
        editAnnotation(at: index) { $0.setAnnotationAttach(at: index, to: attach, time: time, events: recordedEvents) }
    }

    func setSelectedAnnotationPoint(x: Double, y: Double) {
        guard let index = selectedAnnotationIndex else { return }
        editAnnotation(at: index) { $0.setAnnotationPoint(at: index, x: x, y: y) }
    }

    func setSelectedAnnotationOffset(x: Double, y: Double) {
        guard let index = selectedAnnotationIndex else { return }
        editAnnotation(at: index) { $0.setAnnotationOffset(at: index, x: x, y: y) }
    }

    func bringSelectedAnnotationForward() {
        guard let index = selectedAnnotationIndex else { return }
        editAnnotation(at: index) { $0.moveAnnotationLayer(at: index, by: 1) }
    }

    func sendSelectedAnnotationBack() {
        guard let index = selectedAnnotationIndex else { return }
        editAnnotation(at: index) { $0.moveAnnotationLayer(at: index, by: -1) }
    }

    /// An edit to one annotation. The selection goes with it when it is the selected one. An edit
    /// that changes nothing shows no panel.
    @discardableResult
    private func editAnnotation(
        at index: Int,
        _ change: (inout StudioEditorModel) -> StudioAnnotationEditResult
    ) -> StudioAnnotationEditResult {
        var result = StudioAnnotationEditResult(changed: false, index: index)
        let isSelected = selectedAnnotationIndex == index
        editAndSelect { model in
            result = change(&model)
            return isSelected && result.changed ? .annotation(result.index) : .follow
        }
        return result
    }

    // MARK: - Scenes
    //
    // Every moment of the recording is in one scene, so a scene is not selected the way a zoom is:
    // the current scene is the one the playhead is in. The layout controls, the bubble in the
    // preview, and the keys 1 to 4 change that scene.

    var scenes: [StudioScene] { project?.scenes ?? [] }

    /// The scene the playhead is in, as its place in the project's scenes.
    var currentSceneIndex: Int { editor?.currentSceneIndex ?? 0 }

    /// Whether Split would start a new scene at the playhead.
    var canSplitSceneAtPlayhead: Bool { isEditable && (editor?.canSplitScene(at: playhead) ?? false) }

    /// Why the scene cannot be split at the playhead, in words, or nil when it can be.
    var splitSceneExplanation: String? { editor?.splitSceneExplanation(at: playhead) }

    /// Whether the current scene can be deleted: any scene but the only one.
    var canRemoveCurrentScene: Bool { isEditable && (editor?.canRemoveScene(at: currentSceneIndex) ?? false) }

    /// Starts a new scene at the playhead, a copy of the current one that is entered by moving.
    /// At its first instant a scene still looks like the one before, so a paused playhead then
    /// moves to where the new scene has been entered, which shows what is changed in it next.
    func splitSceneAtPlayhead() {
        guard isEditable else { return }
        // Shown for a split that is refused as well: the panel says why.
        showInspectorPanel(.scene)
        let time = playhead
        var result = StudioSceneEditResult(changed: false, index: currentSceneIndex)
        edit { model in
            result = model.splitScene(at: time)
        }

        guard result.changed else {
            if let explanation = splitSceneExplanation {
                announce(explanation)
            }
            return
        }
        announce(StudioEditorModel.sceneSplitMessage)
        if !isPlaying, let lookTime = editor?.sceneLookTime(at: result.index) {
            scrub(to: lookTime)
        }
    }

    /// Deletes the scene the playhead is in. The scene before it then lasts until the next one,
    /// and deleting the first scene hands its time to the second.
    func removeCurrentScene() {
        guard isEditable else { return }
        let index = currentSceneIndex
        var result = StudioSceneEditResult(changed: false, index: index)
        edit { model in
            result = model.removeScene(at: index)
        }
        announce(result.changed ? StudioEditorModel.sceneDeletedMessage : StudioEditorModel.onlySceneExplanation)
    }

    /// Moves where a scene starts, from the lane or the inspector, and shows the picture there:
    /// the playhead follows the start as it does a trim handle. The first scene starts with the
    /// recording and is left alone.
    func setSceneStart(at index: Int, to sourceTime: Double) {
        edit { model in
            model.setSceneStart(at: index, to: sourceTime)
        }
        if isEditable, index >= 1, let range = editor?.sceneRange(at: index) {
            scrub(to: range.start)
        }
    }

    /// Makes the current scene start at the playhead. The playhead stays where it is.
    func setCurrentSceneStartAtPlayhead() {
        let index = currentSceneIndex
        let time = playhead
        edit { model in
            model.setSceneStart(at: index, to: time)
        }
    }

    /// Moves the current scene's start by `seconds`, for the inspector's step buttons.
    func nudgeCurrentSceneStart(by seconds: Double) {
        let index = currentSceneIndex
        guard let range = editor?.sceneRange(at: index) else { return }
        setSceneStart(at: index, to: range.start + seconds)
    }

    /// Sets whether the current scene is cut to or entered by moving. Not for the first scene.
    func setCurrentSceneTransitionKind(_ kind: StudioTransitionKind) {
        editCurrentScene { model in
            let index = model.currentSceneIndex
            model.setSceneTransitionKind(at: index, to: kind)
        }
    }

    /// Sets how long the move into the current scene takes. Not for the first scene.
    func setCurrentSceneTransitionDuration(_ seconds: Double) {
        editCurrentScene { model in
            let index = model.currentSceneIndex
            model.setSceneTransitionDuration(at: index, to: seconds)
        }
    }

    /// Moves the playhead to where a scene has been entered. False when there is no such scene.
    @discardableResult
    func showScene(_ index: Int) -> Bool {
        guard isEditable, let time = editor?.sceneLookTime(at: index) else { return false }
        scrub(to: time)
        return true
    }

    /// Moves the playhead into the scene after the current one. False in the last scene.
    @discardableResult
    func showNextScene() -> Bool {
        showScene(currentSceneIndex + 1)
    }

    /// Moves the playhead into the scene before the current one. False in the first scene.
    @discardableResult
    func showPreviousScene() -> Bool {
        showScene(currentSceneIndex - 1)
    }

    /// The Previous scene button and menu item. The scene it lands on is read out: neither says
    /// anything of where it lands by itself.
    func stepToPreviousScene() {
        if showPreviousScene() {
            showInspectorPanel(.scene)
            announceCurrentScene()
        }
    }

    /// The Next scene button and menu item. The scene it lands on is read out.
    func stepToNextScene() {
        if showNextScene() {
            showInspectorPanel(.scene)
            announceCurrentScene()
        }
    }

    private func announceCurrentScene() {
        guard let editor else { return }
        announce(editor.sceneAccessibilityText(at: editor.currentSceneIndex))
    }

    // MARK: - Trim

    /// Moves the trim start and shows the frame it now starts on.
    func setTrimStart(_ sourceTime: Double) {
        edit { $0.setTrimStart(sourceTime) }
        if let editor, isEditable {
            scrub(to: editor.trimStart)
        }
    }

    /// Moves the trim end and shows the frame it now ends on.
    func setTrimEnd(_ sourceTime: Double) {
        edit { $0.setTrimEnd(sourceTime) }
        if let editor, isEditable {
            scrub(to: editor.trimEnd)
        }
    }

    func setTrimStartAtPlayhead() {
        let time = playhead
        edit { $0.setTrimStart(time) }
    }

    func setTrimEndAtPlayhead() {
        let time = playhead
        edit { $0.setTrimEnd(time) }
    }

    func saveDefaultLook() {
        guard let editor else { return }
        CaptureSettings.shared.studioDefaultLook = editor.currentLook
        SaveService.shared.showNotice("Saved. New Studio recordings will start with this look.")
    }

    // MARK: - Transport

    func togglePlayback() {
        guard isReady, !isExporting, let player, let editor else { return }
        if isPlaying {
            pause()
            return
        }
        let start = editor.playbackStart(from: playhead)
        if abs(start - playhead) > editor.frameDuration / 2 {
            seek(to: start)
        }
        cutSkipTarget = nil
        isPlaying = true
        play(player, from: start)
    }

    func pause() {
        player?.pause()
        isPlaying = false
        applySpeedAndSound(at: playhead)
    }

    func stepFrame(by count: Int) {
        guard isReady, !isExporting, let editor else { return }
        pause()
        seek(to: playhead + Double(count) * editor.frameDuration)
    }

    /// Pauses and moves the playhead whole seconds back (negative) or forward, within the video.
    func stepSeconds(by count: Int) {
        guard isReady, !isExporting, let editor else { return }
        pause()
        seek(to: editor.sourceTime(steppingSeconds: count, from: playhead))
    }

    /// Pauses and moves the playhead to the first picture of the trimmed video.
    func goToTrimStart() {
        guard isReady, !isExporting, let editor else { return }
        pause()
        seek(to: editor.playbackStart)
    }

    /// Pauses and moves the playhead to the last picture of the trimmed video.
    func goToTrimEnd() {
        guard isReady, !isExporting, let editor else { return }
        pause()
        seek(to: editor.lastPlaybackFrame)
    }

    /// Pauses and moves the playhead, for dragging along the timeline.
    func scrub(to sourceTime: Double) {
        guard isReady, !isExporting else { return }
        pause()
        seek(to: sourceTime)
    }

    // MARK: - Export

    func export() {
        requestExportOptions(closingAfterward: false)
    }

    /// The Export popover's content, starting from the choice last made there.
    func makeExportOptions() -> StudioExportOptionsModel? {
        guard let project else { return nil }
        let settings = CaptureSettings.shared
        return StudioExportOptionsModel(
            project: project,
            isHEVC: settings.videoCodec == .hevc,
            saved: settings.studioExportChoice
        )
    }

    /// The popover answered: the choices are kept, and the save panel comes up once the popover
    /// has gone. A modal panel opened from its button would run with the popover still on screen.
    func confirmExportOptions(_ options: StudioExportOptionsModel) {
        CaptureSettings.shared.studioExportChoice = options.choiceToRemember
        let closingAfterward = closesAfterChoosingExportOptions
        closesAfterChoosingExportOptions = false
        isChoosingExportOptions = false
        DispatchQueue.main.async { [weak self] in
            self?.startExport(closingAfterward: closingAfterward, choice: options.choice)
        }
    }

    /// The popover went away without an answer. A window waiting on it stays open.
    func cancelExportOptions() {
        closesAfterChoosingExportOptions = false
        isChoosingExportOptions = false
    }

    private func requestExportOptions(closingAfterward: Bool) {
        guard canExport, !isChoosingDestination, !isChoosingExportOptions else { return }
        pause()
        closesAfterChoosingExportOptions = closingAfterward
        isChoosingExportOptions = true
    }

    func cancelExport() {
        exportTask?.cancel()
    }

    private func startExport(closingAfterward: Bool, choice: StudioExportChoice) {
        commitCaptionText()
        guard canExport, !isChoosingDestination, let paths else { return }
        pause()
        let outputURL: URL
        let target: StudioExportDestination
        isChoosingDestination = true
        do {
            let savedFile = try savedProjectFile()
            var folder = savedFile?.deletingLastPathComponent() ?? SaveService.shared.outputDirectoryURL(for: .video)
            if savedFile != nil && !FileManager.default.fileExists(atPath: folder.path) {
                SaveService.shared.showNotice("The project folder is unavailable. Choose another folder for this export.")
                folder = SaveService.shared.outputDirectoryURL(for: .video)
            }
            let panel = NSSavePanel()
            panel.title = "Export Video"
            panel.prompt = "Export"
            panel.allowedContentTypes = [.mpeg4Movie]
            panel.canCreateDirectories = true
            panel.directoryURL = folder
            panel.nameFieldStringValue = StudioProjectStore.folderName(for: clipName) + ".mp4"
            guard panel.runModal() == .OK, let chosen = panel.url else {
                isChoosingDestination = false
                return
            }
            outputURL = chosen
            target = try StudioExportDestination(url: chosen)
            var protected = [paths.projectDirectory]
            if let project {
                protected.append(paths.screenURL)
                if project.sources.screen.external { protected.append(URL(fileURLWithPath: project.sources.screen.file)) }
                if let savedFile {
                    let savedFolder = savedFile.deletingLastPathComponent()
                    protected.append(savedFile)
                    if FileManager.default.fileExists(atPath: savedFile.path) {
                        protected.append(contentsOf: try store.savedProjectAssets(at: savedFile))
                    }
                    protected.append(savedFolder.appendingPathComponent(project.sources.screen.file))
                    if let camera = project.sources.camera { protected.append(savedFolder.appendingPathComponent(camera.file)) }
                }
            }
            let path = chosen.standardizedFileURL.resolvingSymlinksInPath().path
            guard !protected.contains(where: {
                let protectedPath = $0.standardizedFileURL.resolvingSymlinksInPath().path
                return path.caseInsensitiveCompare(protectedPath) == .orderedSame
                    || path.lowercased().hasPrefix(protectedPath.lowercased() + "/")
            }) else {
                throw NSError(domain: "TinyClipsStudioExport", code: 1, userInfo: [
                    NSLocalizedDescriptionKey: "Choose another filename: exporting here would overwrite project data or a source recording."
                ])
            }
        } catch {
            isChoosingDestination = false
            SaveService.shared.showError("The export destination could not be chosen: \(error.localizedDescription)")
            return
        }
        isChoosingDestination = false
        // The export link is written into the saved project, so the edits have to be on disk first.
        guard saveNow(), let editor else { return }

        isExporting = true
        exportProgress = 0
        closesAfterExport = closingAfterward

        let project = editor.project
        let rendered = editor.editableState
        let projectEvents = events
        let projectStore = store
        let exportedSize = StudioExportPresets.size(project: project, resolution: choice.resolution)
        let renderSize = CGSize(width: exportedSize.width, height: exportedSize.height)
        let quality = choice.quality
        let posterSize = Self.renderSize(for: project, longSide: Self.posterLongSide)
        let codec = CaptureSettings.shared.videoCodec
        let reportProgress: @Sendable (Double) -> Void = { [weak self] progress in
            Task { @MainActor in
                self?.exportProgress = progress
            }
        }

        exportTask = Task { [weak self] in
            let hasAccess = outputURL.startAccessingSecurityScopedResource()
            defer { if hasAccess { outputURL.stopAccessingSecurityScopedResource() } }
            do {
                let url = try await StudioExporter.export(
                    project: project,
                    events: projectEvents,
                    paths: paths,
                    outputURL: outputURL,
                    renderSize: renderSize,
                    codec: codec,
                    quality: quality,
                    onProgress: reportProgress,
                    destination: target
                ).url
                try? await StudioExporter.writePoster(
                    project: project,
                    events: projectEvents,
                    paths: paths,
                    posterURL: paths.posterURL,
                    renderSize: posterSize,
                    time: 0
                )
                let saved = try projectStore.recordExport(id: project.id, path: url.path)
                // The project has a video now and is no draft any more, wherever it is listed.
                NotificationCenter.default.post(name: .studioProjectsDidChange, object: nil)
                guard let self else { return }
                self.editor?.refreshBookkeeping(from: saved)
                self.editor?.markExported(rendered)
                self.isExporting = false
                self.exportProgress = 1
                SaveService.shared.handleSavedFile(url: url, type: .video)
                if self.closesAfterExport {
                    self.closesAfterExport = false
                    self.requestClose?()
                }
            } catch is CancellationError {
                self?.exportDidNotFinish(message: nil)
            } catch {
                self?.exportDidNotFinish(message: error.localizedDescription)
            }
        }
    }

    private func exportDidNotFinish(message: String?) {
        isExporting = false
        exportProgress = 0
        closesAfterExport = false
        if let message, !isTornDown {
            SaveService.shared.showError("Studio export failed: \(message)")
        }
    }

    // MARK: - Closing

    /// Asked when Esc is pressed. Returns false to keep the window open.
    ///
    /// A project saved to its folder with nothing new has nothing to lose; the question only
    /// guards against a stray Esc, and follows the setting shared by the other editors. Any other
    /// project is not asked here, because closing it asks what to do with it anyway.
    func confirmEscapeClose() -> Bool {
        guard CaptureSettings.shared.confirmEditorEscape,
              isReady, editor != nil, closePrompt == .none
        else {
            return true
        }
        pause()

        let alert = NSAlert()
        alert.messageText = "Close Studio?"
        alert.informativeText = "Your edits are saved with the project, and you can reopen it from the Clips Library. You can turn off this confirmation in General settings."
        alert.addButton(withTitle: "Close Studio")
        alert.addButton(withTitle: "Cancel")
        return alert.runModal() == .alertFirstButtonReturn
    }

    // MARK: - The Project as a Whole

    /// Reads which other projects there are, for Open Recent. Reading every project takes a
    /// moment, so it is done off the main thread.
    func refreshRecentProjects() {
        let store = store
        let currentID = projectID
        Task { [weak self] in
            let summaries = await Task.detached(priority: .utility) {
                (try? store.listSummaries()) ?? []
            }.value
            self?.recentProjects = StudioProjectSummary.recent(
                from: summaries,
                excluding: currentID,
                limit: Self.recentProjectLimit
            )
        }
    }

    /// Updates the linked project folder, or chooses one for the first save and Save As.
    /// Editing and autosave continue to use Studio's internal working copy. Saved from the close
    /// prompt, the window closes once the folder has the project, and the folder is not shown.
    func saveProjectFolder(saveAs: Bool = false, closingAfterward: Bool = false) {
        commitCaptionText()
        guard canSaveProjectFolder, !isChoosingDestination else { return }
        pause()
        // The folder gets the project as it is saved, so the edits have to be on disk first.
        guard saveNow() else { return }

        let linkedFile: URL?
        let folder: URL
        isChoosingDestination = true
        defer { isChoosingDestination = false }
        do {
            let savedFile = try (saveAs ? nil : savedProjectFile())
            linkedFile = savedFile
            if let savedFile {
                folder = savedFile.deletingLastPathComponent()
            } else {
                let panel = NSSavePanel()
                panel.title = saveAs ? "Save Project As" : "Save Project"
                panel.message = "Saves the recordings and a .tinyclips file in a folder of this name."
                panel.prompt = "Save"
                panel.nameFieldLabel = "Folder name:"
                panel.nameFieldStringValue = StudioProjectStore.folderName(for: clipName)
                panel.canCreateDirectories = true
                panel.isExtensionHidden = false
                guard panel.runModal() == .OK, let chosen = panel.url else { return }
                folder = chosen
            }
        } catch {
            SaveService.shared.showError("Studio could not locate the saved project: \(error.localizedDescription)")
            return
        }

        let store = store
        let id = projectID
        // Editing waits while the folder is written, so this is what the folder gets.
        let saving = editor?.editableState
        busyMessage = "Saving project…"
        Task { [weak self] in
            let failure: String? = await Task.detached(priority: .userInitiated) {
                do {
                    // The panel has asked before it hands back a name that is taken.
                    if let linkedFile {
                        try store.updateSavedProject(id: id, projectFile: linkedFile)
                    } else {
                        try store.exportProjectFolder(id: id, to: folder, replacingSavedProject: true)
                    }
                    let file = linkedFile ?? folder.appendingPathComponent(folder.lastPathComponent)
                        .appendingPathExtension(StudioProjectStore.projectFileExtension)
                    #if APPSTORE
                    let bookmark = try folder.bookmarkData(options: [.withSecurityScope], includingResourceValuesForKeys: nil, relativeTo: nil)
                    try store.rememberDestination(id: id, file: file, bookmark: bookmark)
                    #else
                    try store.rememberDestination(id: id, file: file)
                    #endif
                    return nil
                } catch {
                    return error.localizedDescription
                }
            }.value
            guard let self else { return }
            self.busyMessage = nil
            if let failure {
                SaveService.shared.showError("Studio could not save this project: \(failure)")
            } else {
                self.projectSaveBaseline = saving
                SaveService.shared.showNotice("Project saved to the folder \(folder.lastPathComponent).")
                if closingAfterward {
                    self.requestClose?()
                    return
                }
                let projectFile = linkedFile ?? folder
                    .appendingPathComponent(folder.lastPathComponent)
                    .appendingPathExtension(StudioProjectStore.projectFileExtension)
                NSWorkspace.shared.activateFileViewerSelecting([projectFile])
            }
        }
    }

    /// Deletes the whole project, after asking: its recordings and every edit. Videos exported
    /// from it and folders it was saved to are files of the user's own and stay. The window
    /// closes.
    func deleteProject() {
        guard canDeleteProject else { return }
        pause()

        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Delete “\(clipName)”?"
        alert.informativeText = "The recording and every edit are removed from Tiny Clips Studio. Videos you exported and folders you saved this project to are not deleted. This cannot be undone."
        let deleteButton = alert.addButton(withTitle: "Delete Project")
        deleteButton.hasDestructiveAction = true
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }

        closesAfterExport = false
        exportTask?.cancel()
        deletesOnClose = true
        requestClose?()
    }

    /// Asked when the user closes the window. Returns false to keep it open.
    func shouldClose() -> Bool {
        commitCaptionText()
        if isChoosingDestination { return false }
        if isBusy {
            // The copy runs to its end either way; the window stays to say how it went.
            NSSound.beep()
            return false
        }

        if isExporting {
            let alert = NSAlert()
            alert.messageText = "An export is still running."
            alert.informativeText = "Closing the window stops the export. Your edits are kept."
            alert.addButton(withTitle: "Keep Exporting")
            alert.addButton(withTitle: "Stop and Close")
            guard alert.runModal() == .alertSecondButtonReturn else { return false }
            closesAfterExport = false
            exportTask?.cancel()
            return true
        }

        switch closePrompt {
        case .none:
            return true
        case .draft:
            return askAboutDraft()
        case .unsavedChanges:
            return askAboutUnsavedChanges()
        }
    }

    /// What closing asks, from whether the project has a folder and changes the folder does not.
    var closePrompt: StudioClosePrompt {
        guard isReady, let editor else { return .none }
        let hasFolder = hasSavedProjectFolder
        return StudioClosePrompt.resolve(
            isReady: true,
            hasSavedProjectFolder: hasFolder,
            hasChangesSinceSave: hasFolder && editor.editableState != projectSaveBaseline
        )
    }

    /// Whether the project is linked to a folder whose project file is still there.
    private var hasSavedProjectFolder: Bool {
        guard let file = try? savedProjectFile() else { return false }
        return FileManager.default.fileExists(atPath: file.path)
    }

    private func askAboutDraft() -> Bool {
        pause()
        let alert = NSAlert()
        alert.messageText = "Keep this recording?"
        alert.informativeText = "It has not been saved as a project. Keep it as a draft to finish later from the Clips Library, or delete it."
        alert.addButton(withTitle: "Keep as Draft")
        let deleteButton = alert.addButton(withTitle: "Delete")
        deleteButton.hasDestructiveAction = true
        alert.addButton(withTitle: "Cancel")

        switch alert.runModal() {
        case .alertFirstButtonReturn:
            return true
        case .alertSecondButtonReturn:
            deletesOnClose = true
            return true
        default:
            return false
        }
    }

    private func askAboutUnsavedChanges() -> Bool {
        pause()
        let alert = NSAlert()
        alert.messageText = "Save changes to “\(clipName)”?"
        alert.informativeText = "This project has changes that are not in its project folder. If you don’t save, the changes you made since it was last saved are lost."
        alert.addButton(withTitle: "Save")
        alert.addButton(withTitle: "Don’t Save")
        alert.addButton(withTitle: "Cancel")

        switch alert.runModal() {
        case .alertFirstButtonReturn:
            saveProjectFolder(closingAfterward: true)
            return false
        case .alertSecondButtonReturn:
            if let projectSaveBaseline {
                editor?.restore(projectSaveBaseline)
                // Written when the window closes.
                hasUnsavedEdits = true
            }
            return true
        default:
            return false
        }
    }

    /// Called once, when the window is closing for good.
    func tearDown() {
        guard !isTornDown else { return }
        isTornDown = true
        destinationScope?.stopAccessingSecurityScopedResource()
        destinationScope = nil
        saveTask?.cancel()
        previewTask?.cancel()
        exportTask?.cancel()
        captionGenerationTask?.cancel()
        rateObservation?.invalidate()
        rateObservation = nil
        if let timeObserver, let player {
            player.removeTimeObserver(timeObserver)
        }
        timeObserver = nil
        player?.pause()
        player?.replaceCurrentItem(with: nil)
        isPlaying = false

        if deletesOnClose {
            try? store.delete(id: projectID)
            return
        }

        saveNow()
        markLastUsed()
        if isReady, let project = editor?.project, let paths {
            let projectEvents = events
            let posterSize = Self.renderSize(for: project, longSide: Self.posterLongSide)
            // The Clips Library thumbnail. It outlives the window on purpose.
            Task {
                try? await StudioExporter.writePoster(
                    project: project,
                    events: projectEvents,
                    paths: paths,
                    posterURL: paths.posterURL,
                    renderSize: posterSize,
                    time: 0
                )
            }
        }
    }

    // MARK: - Private: Edits

    /// Where the selection goes after an edit.
    private enum SelectionAfterEdit {
        /// The edit did not say. The selection follows the zoom, the cut, or the speed change it
        /// was on.
        case follow

        /// The edit knows where its zoom went, or that it is gone.
        case zoom(Int?)

        /// The edit knows where its cut went, or that it is gone.
        case cut(Int?)

        /// The edit knows where its speed change went, or that it is gone.
        case speed(Int?)

        /// The edit knows where its annotation went, or that it is gone.
        case annotation(Int?)
    }

    private func edit(_ change: (inout StudioEditorModel) -> Void) {
        editAndSelect { model in
            change(&model)
            return .follow
        }
    }

    /// An edit to the scene the playhead is in: its layout, its bubble, its split, or how it is
    /// entered.
    private func editCurrentScene(_ change: (inout StudioEditorModel) -> Void) {
        holdSceneForDrag()
        edit(change)
    }

    /// Stops playback before a drag changes the scene the playhead is in. A drag is many changes,
    /// and while the recording plays the playhead may come into the next scene before the drag is
    /// over: the rest of the drag would then change that scene as well. A change that stands
    /// alone, such as a key or a choice from a menu, is over at once and leaves playback alone.
    /// So does a drag in a recording with one scene, which has no other scene to come into.
    private func holdSceneForDrag() {
        guard isInGesture, isPlaying, scenes.count > 1 else { return }
        pause()
    }

    /// An edit to one zoom. The selection goes with the zoom when it is the selected one. An edit
    /// that is refused, because the project cannot be edited just now, leaves the zoom where it was.
    @discardableResult
    private func editZoom(
        at index: Int,
        _ change: (inout StudioEditorModel) -> StudioZoomEditResult
    ) -> StudioZoomEditResult {
        var result = StudioZoomEditResult(changed: false, index: index)
        let isSelected = selectedZoomIndex == index
        editAndSelect { model in
            result = change(&model)
            return isSelected ? .zoom(result.index) : .follow
        }
        return result
    }

    /// An edit to one cut. The selection goes with the cut when it is the selected one. An edit
    /// that is refused, because the project cannot be edited just now, leaves the cut where it was.
    @discardableResult
    private func editCut(
        at index: Int,
        _ change: (inout StudioEditorModel) -> StudioCutEditResult
    ) -> StudioCutEditResult {
        var result = StudioCutEditResult(changed: false, index: index)
        let isSelected = selectedCutIndex == index
        editAndSelect { model in
            result = change(&model)
            return isSelected ? .cut(result.index) : .follow
        }
        return result
    }

    /// An edit to one speed change. The selection goes with it when it is the selected one. An
    /// edit that is refused, because the project cannot be edited just now, leaves it where it was.
    @discardableResult
    private func editSpeed(
        at index: Int,
        _ change: (inout StudioEditorModel) -> StudioSpeedEditResult
    ) -> StudioSpeedEditResult {
        var result = StudioSpeedEditResult(changed: false, index: index)
        let isSelected = selectedSpeedIndex == index
        editAndSelect { model in
            result = change(&model)
            return isSelected ? .speed(result.index) : .follow
        }
        return result
    }

    private func editAndSelect(_ change: (inout StudioEditorModel) -> SelectionAfterEdit) {
        guard isEditable, var model = editor else { return }
        // An edit to the layout changes the scene the playhead is in.
        model.sceneTime = playhead
        let before = model.editableState
        let zoomsBefore = model.project.zooms
        let cutsBefore = model.project.edits.cuts
        let speedBefore = model.project.edits.speed
        let annotationsBefore = model.project.overlays.annotations
        let selection = change(&model)
        editor = model
        let isChanged = model.editableState != before

        // Through an edit that did not say, the selection follows the zoom, the cut, or the speed
        // change it was on.
        var zoomIndex = selectedZoomIndex
        var cutIndex = selectedCutIndex
        var speedIndex = selectedSpeedIndex
        var annotationIndex = selectedAnnotationIndex
        if isChanged, let selected = zoomIndex {
            zoomIndex = StudioEditorModel.zoomIndex(following: selected, from: zoomsBefore, to: model.project.zooms)
        }
        if isChanged, let selected = cutIndex {
            cutIndex = StudioEditorModel.cutIndex(following: selected, from: cutsBefore, to: model.project.edits.cuts)
        }
        if isChanged, let selected = speedIndex {
            speedIndex = StudioEditorModel.speedIndex(following: selected, from: speedBefore, to: model.project.edits.speed)
        }

        if isChanged, let selected = annotationIndex {
            annotationIndex = StudioEditorModel.annotationIndex(following: selected, from: annotationsBefore, to: model.project.overlays.annotations)
        }

        // One selection: an edit that says where a zoom is lets go of a cut and a speed change,
        // and so on. An edit to the selected zoom finds nothing else selected, so only adding one
        // does.
        switch selection {
        case .follow:
            break
        case .zoom(let index):
            zoomIndex = index
            cutIndex = nil
            speedIndex = nil
            annotationIndex = nil
        case .cut(let index):
            cutIndex = index
            zoomIndex = nil
            speedIndex = nil
            annotationIndex = nil
        case .speed(let index):
            speedIndex = index
            zoomIndex = nil
            cutIndex = nil
            annotationIndex = nil
        case .annotation(let index):
            annotationIndex = index
            zoomIndex = nil
            cutIndex = nil
            speedIndex = nil
        }
        setSelectedZoomIndex(zoomIndex)
        setSelectedCutIndex(cutIndex)
        setSelectedSpeedIndex(speedIndex)
        setSelectedAnnotationIndex(annotationIndex)

        // An edit that says which zoom, cut, or speed change it left selected shows its panel.
        // Adding one does, from the timeline, the menu, or the keyboard.
        switch selection {
        case .follow:
            break
        case .zoom:
            if selectedZoomIndex != nil { showInspectorPanel(.zoom) }
        case .cut:
            if selectedCutIndex != nil { showInspectorPanel(.cut) }
        case .speed:
            if selectedSpeedIndex != nil { showInspectorPanel(.speed) }
        case .annotation:
            if selectedAnnotationIndex != nil { showInspectorPanel(.annotate) }
        }

        guard isChanged else { return }
        hasUnsavedEdits = true
        scheduleSave()
        if isPlaying, model.isAtPlaybackEnd(playhead) {
            pause()
        }
        // The edit may have changed whether the project is muted, or how fast it plays here.
        applySpeedAndSound(at: playhead)
        refreshPreview()
    }

    /// Sets the selection to a zoom that exists, or to none. It is published only when it changes.
    private func setSelectedZoomIndex(_ index: Int?) {
        var valid: Int?
        if let index, zooms.indices.contains(index) {
            valid = index
        }
        if selectedZoomIndex != valid {
            selectedZoomIndex = valid
        }
    }

    /// Sets the selection to a cut that exists, or to none. It is published only when it changes.
    private func setSelectedCutIndex(_ index: Int?) {
        var valid: Int?
        if let index, cuts.indices.contains(index) {
            valid = index
        }
        if selectedCutIndex != valid {
            selectedCutIndex = valid
        }
    }

    /// Sets the selection to a speed change that exists, or to none. It is published only when it
    /// changes.
    private func setSelectedSpeedIndex(_ index: Int?) {
        var valid: Int?
        if let index, speedChanges.indices.contains(index) {
            valid = index
        }
        if selectedSpeedIndex != valid {
            selectedSpeedIndex = valid
        }
    }

    /// Sets the selection to an annotation that exists, or to none. It is published only when it
    /// changes.
    private func setSelectedAnnotationIndex(_ index: Int?) {
        var valid: Int?
        if let index, annotations.indices.contains(index) {
            valid = index
        }
        if selectedAnnotationIndex != valid {
            selectedAnnotationIndex = valid
        }
    }

    /// Tells the model where the playhead is once it has moved into another scene. The model is
    /// published, so it is told then and not for every frame that plays. Every edit tells it again.
    private func followPlayheadIntoScene() {
        guard let editor, editor.sceneIndex(at: playhead) != editor.currentSceneIndex else { return }
        self.editor?.sceneTime = playhead
    }

    /// Says through VoiceOver what the screen alone shows.
    private func announce(_ message: String) {
        AccessibilityAnnouncementService.shared.announce(message, priority: .medium)
    }

    /// Redraws the preview for the current project. Only a new canvas size rebuilds the player
    /// item, and one rebuild runs at a time: an edit that arrives during one is applied when it
    /// has ended, so the preview always ends on the latest edit.
    private func refreshPreview() {
        guard editor != nil, let playback else { return }
        needsPreviewRefresh = true
        guard !isRefreshingPreview else { return }
        isRefreshingPreview = true

        previewTask = Task { [weak self] in
            while true {
                guard let self, !self.isTornDown, self.needsPreviewRefresh else { break }
                self.needsPreviewRefresh = false
                await self.applyPreview(to: playback)
            }
            self?.isRefreshingPreview = false
        }
    }

    private func applyPreview(to playback: StudioPlayback) async {
        guard let model = editor else { return }
        let size = Self.renderSize(for: model.project, longSide: Self.previewLongSide)
        // During a drag the canvas keeps its size. A crop changes the size with every step, and
        // every new size rebuilds the player item. The size is applied when the drag ends.
        let newSize: CGSize? = (size == previewSize || isInGesture) ? nil : size
        if let newSize {
            previewSize = newSize
        }

        do {
            let previousItem = playback.player.currentItem
            let item = try await playback.apply(project: model.previewProject, renderSize: newSize)
            guard !isTornDown else { return }
            if item !== previousItem {
                // A new player item starts at zero, so put the playhead back. Whether to go on
                // playing is asked now and not before the wait above: a pause, a scrub or a
                // frame step that came in while the item was being built has to hold.
                seek(to: playhead)
                if isPlaying {
                    play(playback.player, from: playhead)
                }
            }
        } catch {
            guard !isTornDown else { return }
            SaveService.shared.showError("Studio preview failed: \(error.localizedDescription)")
        }
    }

    private func scheduleSave() {
        saveTask?.cancel()
        saveTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 600_000_000)
            guard !Task.isCancelled else { return }
            self?.saveNow()
        }
    }

    /// Writes an edit that is still waiting for its save, when the app is about to quit. Quitting
    /// closes no window, so without this the last change made before a quit would be lost.
    func saveBeforeQuitting() {
        guard !isTornDown else { return }
        saveNow()
        markLastUsed()
    }

    /// Writes down that the project was in use until now, when its window closes or the app
    /// quits. Cleanup removes the recordings of an exported project some days after it was last
    /// open, and counts from this time. Counted from when the editor opened, a project whose
    /// editor stayed open for longer than that would be removed the moment the editor closed,
    /// by the cleanup that follows every close. A project that was never read has nothing to
    /// write down, and a failure here is not reported: the edits are saved separately.
    private func markLastUsed() {
        guard editor != nil, let saved = try? store.markOpened(id: projectID) else { return }
        editor?.refreshBookkeeping(from: saved)
    }

    /// Writes the edits into the project on disk. Only the edited parts are replaced, so export
    /// links or names changed elsewhere (the Clips Library, for example) are not overwritten.
    @discardableResult
    private func saveNow() -> Bool {
        saveTask?.cancel()
        saveTask = nil
        guard hasUnsavedEdits, let model = editor else { return true }
        do {
            let onDisk = try store.load(id: projectID)
            let saved = try store.save(model.editableState.applied(to: onDisk))
            editor?.refreshBookkeeping(from: saved)
            hasUnsavedEdits = false
            return true
        } catch {
            SaveService.shared.showError("Studio could not save this project: \(error.localizedDescription)")
            return false
        }
    }

    // MARK: - Private: Playback

    private func seek(to sourceTime: Double) {
        guard let editor else { return }
        let time = editor.clampedSourceTime(sourceTime)
        playhead = time
        player?.seek(
            to: CMTime(seconds: time, preferredTimescale: 600),
            toleranceBefore: .zero,
            toleranceAfter: .zero
        )
    }

    private func observe(_ player: AVPlayer) {
        timeObserver = player.addPeriodicTimeObserver(
            forInterval: CMTime(value: 1, timescale: 30),
            queue: .main
        ) { [weak self] time in
            let seconds = time.seconds
            MainActor.assumeIsolated {
                guard let self else { return }
                self.playerDidReach(seconds)
            }
        }
        rateObservation = player.observe(\.rate, options: [.new]) { [weak self] _, _ in
            Task { @MainActor in
                guard let self else { return }
                self.playerRateDidChange()
            }
        }
    }

    private func playerDidReach(_ seconds: Double) {
        guard isPlaying, seconds.isFinite, let editor else { return }
        if !editor.isAtPlaybackEnd(seconds), let target = editor.cutSkipTarget(at: seconds) {
            // Playback has reached a cut and goes on from its end. The player reports times inside
            // the cut until its seek has landed. Those do not send it there again, and they leave
            // the playhead where it was sent.
            if target != cutSkipTarget {
                cutSkipTarget = target
                seek(to: target)
                applySpeedAndSound(at: target)
            }
            return
        }

        // The player reports once for every thirtieth of a second of the recording, which at
        // eight times the speed is eight times as often by the clock. The playhead, and all that
        // is drawn from it, moves no more often than it does at the recording's own speed.
        let isAtEnd = editor.isAtPlaybackEnd(seconds)
        let now = ProcessInfo.processInfo.systemUptime
        if isAtEnd || now - lastPlayheadMove >= 1.0 / 60 {
            lastPlayheadMove = now
            playhead = editor.clampedSourceTime(seconds)
        }
        if isAtEnd {
            pause()
            seek(to: editor.playbackEnd)
        } else {
            applySpeedAndSound(at: seconds)
        }
    }

    private func playerRateDidChange() {
        guard isPlaying, let player, player.rate == 0 else { return }
        // The player stopped by itself: the end of the recording, or an interruption.
        isPlaying = false
        let seconds = player.currentTime().seconds
        if seconds.isFinite, let editor {
            playhead = editor.clampedSourceTime(seconds)
        }
        applySpeedAndSound(at: playhead)
    }

    /// Starts the player at the speed the video has at a source time.
    private func play(_ player: AVPlayer, from sourceTime: Double) {
        requestedRate = Float(editor?.playbackRate(at: sourceTime) ?? 1)
        player.rate = requestedRate
        applySpeedAndSound(at: sourceTime)
    }

    /// Sets how fast the player plays and whether it is heard, for where playback is. A stretch
    /// at another speed plays at its rate and without sound, as in the export (section 7 of the
    /// project format). A player that is paused, or that has stopped by itself, is not started
    /// by this.
    private func applySpeedAndSound(at sourceTime: Double) {
        guard let player, let editor else { return }
        let rate = isPlaying ? Float(editor.playbackRate(at: sourceTime)) : 1
        if isPlaying, player.rate != 0, requestedRate != rate {
            requestedRate = rate
            player.rate = rate
        }
        let isMuted = editor.project.audio.muted || rate != 1
        if player.isMuted != isMuted {
            player.isMuted = isMuted
        }
    }

    // MARK: - Private: Helpers

    private static func renderSize(for project: StudioProject, longSide: Double) -> CGSize {
        let size = StudioCanvasMath.exportSize(project: project, longSideLimit: longSide)
        return CGSize(width: max(2, size.width), height: max(2, size.height))
    }

    static func hexString(for color: Color) -> String {
        let nsColor = NSColor(color)
        guard let resolved = nsColor.usingColorSpace(.sRGB) ?? nsColor.usingColorSpace(.deviceRGB) else {
            return "#000000"
        }
        return String(
            format: "#%02X%02X%02X",
            Int((min(max(resolved.redComponent, 0), 1) * 255).rounded()),
            Int((min(max(resolved.greenComponent, 0), 1) * 255).rounded()),
            Int((min(max(resolved.blueComponent, 0), 1) * 255).rounded())
        )
    }
}