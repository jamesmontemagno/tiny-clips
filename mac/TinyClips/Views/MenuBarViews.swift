import SwiftUI
import os
import AppKit

// MARK: - Settings Window Manager

class SettingsWindowManager: ObservableObject {
    static let shared = SettingsWindowManager()
    @Published var selectedTab: SettingsTab? = nil
    @Published var showQuickBugReportForm = false
    
    private init() {}
}

// MARK: - Menu Bar Content

struct MenuBarContentView: View {
    @ObservedObject var captureManager: CaptureManager
    @ObservedObject var sparkleController: SparkleController
    @ObservedObject private var settings = CaptureSettings.shared
    @ObservedObject private var recentCaptures = RecentCaptureStore.shared
    @ObservedObject private var studioDrafts = StudioRecentDrafts.shared
    @ObservedObject private var automation = AutomationController.shared
    @ObservedObject private var screenshotEditors = ScreenshotEditorRegistry.shared
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        if !captureManager.isRecording {
            Button {
                captureManager.takeScreenshot()
            } label: {
                Label("Screenshot…", systemImage: "camera")
            }
            .keyboardShortcut(settings.hotKeyBinding(for: .screenshot).swiftUIShortcut)
            .accessibilityHint("Starts screenshot capture.")
            .disabled(captureManager.isCaptureActionInProgress)

            Button {
                captureManager.copyTextFromRegion()
            } label: {
                Label("Copy Text from Region…", systemImage: "text.viewfinder")
            }
            .keyboardShortcut(settings.hotKeyBinding(for: .copyTextFromRegion).swiftUIShortcut)
            .accessibilityHint("Selects a screen region and copies recognized text to the clipboard.")
            .disabled(captureManager.isCaptureActionInProgress)

            Button {
                captureManager.startVideoRecording()
            } label: {
                Label("Record Video...", systemImage: "video")
            }
            .keyboardShortcut(settings.hotKeyBinding(for: .recordVideo).swiftUIShortcut)
            .accessibilityHint("Starts video recording.")
            .disabled(captureManager.isCaptureActionInProgress)

            Button {
                captureManager.startVideoRecording(forStudio: true)
            } label: {
                Label("Studio Recording...", systemImage: "square.stack.3d.up")
            }
            .keyboardShortcut(settings.hotKeyBinding(for: .studioRecording).swiftUIShortcut)
            .accessibilityHint("Starts a video recording that opens in Tiny Clips Studio, with the screen and camera kept as separate layers.")
            .disabled(captureManager.isCaptureActionInProgress)

            Button {
                captureManager.startGifRecording()
            } label: {
                Label("Record GIF...", systemImage: "photo.on.rectangle")
            }
            .keyboardShortcut(settings.hotKeyBinding(for: .recordGif).swiftUIShortcut)
            .accessibilityHint("Starts GIF recording.")
            .disabled(captureManager.isCaptureActionInProgress)

            Divider()
        } else {
            Button {
                captureManager.stopRecording()
            } label: {
                Label("Stop Recording", systemImage: "stop.circle")
            }
            .keyboardShortcut(settings.hotKeyBinding(for: .stopRecording).swiftUIShortcut)
            .accessibilityHint("Stops the current recording.")

            Divider()
        }

        folderActions

        let recentEntries = RecentMenuEntry.merged(
            captures: Array(recentCaptures.items.prefix(RecentCaptureStore.menuDisplayLimit)),
            drafts: studioDrafts.drafts,
            limit: RecentCaptureStore.menuDisplayLimit
        )
        if !recentEntries.isEmpty {
            Button {
                DispatchQueue.main.async {
                    RecentCapturesPopoverController.shared.show(captureManager: captureManager)
                }
            } label: {
                Label("Recent Captures", systemImage: "clock.arrow.circlepath")
            }
            .accessibilityHint("Opens recent captures as a horizontally scrolling thumbnail popover.")
        }

        Divider()
#if !APPSTORE
        Button {
            SettingsWindowManager.shared.selectedTab = .about
            // Open Settings first so Sparkle has a parent window for its update dialog.
            // Without a key window (which doesn't exist after the menu bar menu closes),
            // Sparkle cannot present its UI and shows "Update failed" instead.
            openWindow(id: "settings-window")
            bringSettingsWindowToFront()
            checkForUpdatesAfterSettingsWindowAppears()
        } label: {
            Label("Check for Updates\u{2026}", systemImage: "arrow.trianglehead.clockwise")
        }
#endif
        Button {
            openWindow(id: "clips-manager")
            bringClipsManagerWindowToFront()
        } label: {
            Label("Clips Library…", systemImage: "film.stack")
        }

        Button {
            screenshotEditors.closeAllInteractively()
        } label: {
            Label("Close All Screenshot Editors", systemImage: "xmark.rectangle")
        }
        .disabled(!screenshotEditors.hasOpenSessions || screenshotEditors.isClosingAll)
        .accessibilityHint("Closes screenshot editors, asking before discarding unsaved work. Cancel stops closing the remaining editors.")

        Button {
            captureManager.showGuide()
        } label: {
            Label("Guide…", systemImage: "book")
        }

        Button {
            SettingsWindowManager.shared.selectedTab = .about
            SettingsWindowManager.shared.showQuickBugReportForm = true
            openWindow(id: "settings-window")
            bringSettingsWindowToFront()
        } label: {
            Label("Send Feedback…", systemImage: "bubble.left.and.bubble.right")
        }

#if APPSTORE
        Button {
            SettingsWindowManager.shared.selectedTab = .pro
            openWindow(id: "settings-window")
            bringSettingsWindowToFront()
        } label: {
            Label("Support…", systemImage: "heart")
        }
        .accessibilityHint("Opens support and tip options.")
#endif

        Button {
            PerformanceSignposts.beginSettingsOpen()
            openWindow(id: "settings-window")
            bringSettingsWindowToFront()
        } label: {
            Label("Settings…", systemImage: "gearshape")
        }
        .keyboardShortcut(",", modifiers: .command)
        .accessibilityHint("Opens TinyClips settings.")

        Divider()

        if automation.isEnabled {
            Label("Agent Control On", systemImage: "terminal")
                .disabled(true)
                .accessibilityLabel("Agent control is on")

            Button {
                automation.setEnabled(false)
            } label: {
                Label("Turn Off Agent Control", systemImage: "xmark.circle")
            }
            .accessibilityHint("Stops AI agents from controlling TinyClips.")
        } else {
            Button {
                if automation.setEnabled(true) {
                    SettingsWindowManager.shared.selectedTab = .agentControl
                    openWindow(id: "settings-window")
                    bringSettingsWindowToFront()
                }
            } label: {
                Label("Turn On Agent Control", systemImage: "terminal")
            }
            .accessibilityHint("Lets paired AI agents control TinyClips and opens settings the first time.")
        }

        Divider()

        Button {
            TinyClipsAppDelegate.requestFullTermination()
        } label: {
            Label("Quit", systemImage: "power")
        }
        .keyboardShortcut("q", modifiers: .command)
        .onAppear {
            recentCaptures.pruneMissing()
            studioDrafts.reload()
        }
        .onChange(of: settings.recentCapturesLimit) {
            recentCaptures.refreshForDisplayLimit()
            studioDrafts.refreshForDisplayLimit()
        }
    }

    @ViewBuilder
    private var folderActions: some View {
        let types: [CaptureType] = [.screenshot, .video, .gif]
        let directories = types.map { SaveService.shared.outputDirectoryURL(for: $0) }
        if Set(directories.map { $0.standardizedFileURL.path }).count == 1,
           let directory = directories.first {
            Button {
                openDirectory(directory)
            } label: {
                Label("Open Save Folder", systemImage: "folder")
            }
            .accessibilityHint("Opens the folder where captures are saved.")
        } else {
            Menu {
                ForEach(types, id: \.rawValue) { type in
                    Button("Open \(type.label) Folder") {
                        openDirectory(SaveService.shared.outputDirectoryURL(for: type))
                    }
                }
            } label: {
                Label("Open Capture Folder", systemImage: "folder")
            }
        }
    }

    private func openDirectory(_ directory: URL) {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        NSWorkspace.shared.open(directory)
    }

    private func checkForUpdatesAfterSettingsWindowAppears() {
        // Poll until the Settings window becomes key (or a 2 s timeout elapses) so
        // Sparkle has a valid parent window for its update dialog. A menu bar app has
        // no key window after the menu closes, which causes Sparkle to show "Update
        // failed" when checkForUpdates is called immediately.
        let start = Date()
        let timeout: TimeInterval = 2.0
        func tryCheck() {
            let settingsIsKey = NSApp.keyWindow.map { $0.identifier?.rawValue == "settings-window" || $0.title == "Tiny Clips Settings" } ?? false
            if settingsIsKey || Date().timeIntervalSince(start) >= timeout {
                sparkleController.checkForUpdates()
            } else {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { tryCheck() }
            }
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { tryCheck() }
    }

    private func bringSettingsWindowToFront() {
        DispatchQueue.main.async {
            NSRunningApplication.current.activate(options: [.activateAllWindows])
            if let settingsWindow = NSApp.windows.first(where: { $0.identifier?.rawValue == "settings-window" || $0.title == "Tiny Clips Settings" }) {
                settingsWindow.makeKeyAndOrderFront(nil)
                settingsWindow.orderFrontRegardless()
            }
        }

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
            NSRunningApplication.current.activate(options: [.activateAllWindows])
            if let settingsWindow = NSApp.windows.first(where: { $0.identifier?.rawValue == "settings-window" || $0.title == "Tiny Clips Settings" }) {
                settingsWindow.makeKeyAndOrderFront(nil)
                settingsWindow.orderFrontRegardless()
            }
        }
    }

    private func bringClipsManagerWindowToFront() {
        DispatchQueue.main.async {
            NSRunningApplication.current.activate(options: [.activateAllWindows])
            if let clipsWindow = NSApp.windows.first(where: { $0.identifier?.rawValue == "clips-manager" || $0.title == "Clips Library" }) {
                clipsWindow.makeKeyAndOrderFront(nil)
                clipsWindow.orderFrontRegardless()
            }
        }

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
            NSRunningApplication.current.activate(options: [.activateAllWindows])
            if let clipsWindow = NSApp.windows.first(where: { $0.identifier?.rawValue == "clips-manager" || $0.title == "Clips Library" }) {
                clipsWindow.makeKeyAndOrderFront(nil)
                clipsWindow.orderFrontRegardless()
            }
        }
    }

}

// MARK: - Recent Captures Popover

struct RecentCapturePresentation: Equatable {
    let id: String
    let title: String
    let kind: String
    let date: String
    let symbol: String
    let accessibilityText: String

    init(_ entry: RecentMenuEntry) {
        let shownDate = entry.date.formatted(date: .abbreviated, time: .shortened)
        id = entry.id
        date = shownDate
        switch entry {
        case .capture(let item):
            title = item.url.lastPathComponent
            kind = item.type.label
            symbol = switch item.type {
            case .screenshot: "photo"
            case .video: "video"
            case .gif: "photo.stack"
            }
        case .studioDraft(let project):
            let projectName = project.name.trimmingCharacters(in: .whitespacesAndNewlines)
            title = projectName.isEmpty ? "Untitled Recording" : projectName
            kind = "Studio project"
            symbol = "square.stack.3d.up"
        }
        accessibilityText = "\(title), \(kind), \(shownDate)"
    }
}

@MainActor
private final class RecentCapturesPopoverController: NSObject, NSWindowDelegate {
    static let shared = RecentCapturesPopoverController()

    private var panel: RecentCapturesPopoverPanel?
    private var globalMouseMonitor: Any?
    private var localKeyboardMonitor: Any?

    private override init() {
        super.init()
    }

    func show(captureManager: CaptureManager) {
        let panel = panel ?? {
            let created = RecentCapturesPopoverPanel()
            created.delegate = self
            self.panel = created
            return created
        }()
        panel.setContent(
            RecentCapturesPopoverView(
                captureManager: captureManager,
                dismiss: { [weak self] in self?.dismiss() }
            )
        )

        position(panel, near: NSEvent.mouseLocation)
        NSApp.activate(ignoringOtherApps: true)
        panel.makeKeyAndOrderFront(nil)
        panel.orderFrontRegardless()
        installDismissMonitors()
    }

    func dismiss() {
        if let globalMouseMonitor {
            NSEvent.removeMonitor(globalMouseMonitor)
            self.globalMouseMonitor = nil
        }
        if let localKeyboardMonitor {
            NSEvent.removeMonitor(localKeyboardMonitor)
            self.localKeyboardMonitor = nil
        }
        panel?.orderOut(nil)
    }

    func windowWillClose(_ notification: Notification) {
        dismiss()
    }

    func windowDidResignKey(_ notification: Notification) {
        guard NSApp.modalWindow == nil else { return }
        dismiss()
    }

    private func installDismissMonitors() {
        if globalMouseMonitor == nil {
            globalMouseMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] _ in
                Task { @MainActor in self?.dismiss() }
            }
        }
        if localKeyboardMonitor == nil {
            localKeyboardMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
                if event.keyCode == 53 {
                    self?.dismiss()
                    return nil
                }
                return event
            }
        }
    }

    private func position(_ panel: NSPanel, near point: NSPoint) {
        let screen = NSScreen.screens.first(where: { NSMouseInRect(point, $0.frame, false) }) ?? NSScreen.main
        guard let screen else { return }
        let bounds = screen.visibleFrame
        let margin: CGFloat = 12
        let size = panel.frame.size
        let x = min(max(point.x - size.width / 2, bounds.minX + margin), bounds.maxX - size.width - margin)
        let y = min(bounds.maxY - size.height - margin, max(bounds.minY + margin, point.y - size.height - 18))
        panel.setFrameOrigin(NSPoint(x: x, y: y))
    }
}

@MainActor
private final class RecentCapturesPopoverPanel: NSPanel {
    private let width: CGFloat = 568
    private let height: CGFloat = 218

    override var canBecomeKey: Bool { true }

    init() {
        super.init(
            contentRect: NSRect(x: 0, y: 0, width: width, height: height),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        isReleasedWhenClosed = false
        level = .popUpMenu
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
    }

    func setContent<Content: View>(_ content: Content) {
        let hostingView = NSHostingView(rootView: content.frame(width: width, height: height))
        hostingView.wantsLayer = true
        contentView = hostingView
    }

    override func cancelOperation(_ sender: Any?) {
        RecentCapturesPopoverController.shared.dismiss()
    }
}

@MainActor
private struct RecentCapturesPopoverView: View {
    @ObservedObject private var recentCaptures = RecentCaptureStore.shared
    @ObservedObject private var studioDrafts = StudioRecentDrafts.shared
    @ObservedObject private var settings = CaptureSettings.shared
    let captureManager: CaptureManager
    let dismiss: () -> Void

    @State private var copiedEntryID: String?
    @State private var pendingDeleteEntry: RecentMenuEntry?
    @State private var hoveredEntryID: String?
    @FocusState private var focusedEntryID: String?

    private var entries: [RecentMenuEntry] {
        RecentMenuEntry.merged(
            captures: recentCaptures.items,
            drafts: studioDrafts.drafts,
            limit: settings.recentCapturesLimit
        )
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Label("Recent Captures", systemImage: "clock.arrow.circlepath")
                    .font(.headline)
                Spacer()
                Text("\(entries.count)")
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
                    .accessibilityLabel("\(entries.count) recent items")
                Button(action: dismiss) {
                    Label("Close recent captures", systemImage: "xmark")
                        .labelStyle(.iconOnly)
                        .frame(width: 24, height: 24)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.borderless)
                .help("Close recent captures (Esc)")
            }
            .padding(.horizontal, 2)

            ScrollView(.horizontal, showsIndicators: false) {
                LazyHStack(spacing: 12) {
                    ForEach(entries) { entry in
                        recentCard(entry)
                    }
                }
                .padding(.horizontal, 2)
                .padding(.vertical, 1)
            }
        }
        .padding(16)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: 16, style: .continuous)
                .stroke(.white.opacity(0.16))
        }
        .alert(deleteTitle, isPresented: deleteAlertBinding) {
            Button("Cancel", role: .cancel) {
                pendingDeleteEntry = nil
            }
            Button(deleteActionTitle, role: .destructive) {
                if let pendingDeleteEntry {
                    performDelete(pendingDeleteEntry)
                }
                pendingDeleteEntry = nil
            }
        } message: {
            Text(deleteMessage)
        }
        .onChange(of: settings.recentCapturesLimit) {
            recentCaptures.refreshForDisplayLimit()
            studioDrafts.refreshForDisplayLimit()
        }
    }

    @ViewBuilder
    private func recentCard(_ entry: RecentMenuEntry) -> some View {
        let presentation = RecentCapturePresentation(entry)
        VStack(spacing: 7) {
            ZStack(alignment: .top) {
                Button {
                    dismiss()
                    open(entry)
                } label: {
                    VStack(alignment: .leading, spacing: 5) {
                        thumbnail(entry, symbol: presentation.symbol)
                        Text(presentation.title)
                            .lineLimit(1)
                            .font(.subheadline.weight(.medium))
                            .truncationMode(.middle)
                        HStack(spacing: 5) {
                            Text(presentation.date)
                        }
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                    }
                    .frame(width: 160, alignment: .leading)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .focused($focusedEntryID, equals: entry.id)
                .help(presentation.accessibilityText)
                .accessibilityLabel(presentation.accessibilityText)
                .accessibilityHint(openHint(entry))

                HStack(spacing: 6) {
                    thumbnailActions(entry)
                }
                .padding(.top, 32)
                .frame(width: 160, alignment: .center)
                .opacity(hoveredEntryID == entry.id || focusedEntryID == entry.id ? 1 : 0)
                .allowsHitTesting(hoveredEntryID == entry.id || focusedEntryID == entry.id)
                .accessibilityHidden(hoveredEntryID != entry.id && focusedEntryID != entry.id)
                .animation(.easeOut(duration: 0.12), value: hoveredEntryID)
                .animation(.easeOut(duration: 0.12), value: focusedEntryID)
            }
            .onHover { hovering in
                hoveredEntryID = hovering ? entry.id : nil
            }
            .onAppear {
                switch entry {
                case .capture(let item):
                    recentCaptures.requestThumbnail(for: item)
                case .studioDraft(let project):
                    studioDrafts.requestThumbnail(for: project.id)
                }
            }
        }
        .padding(9)
        .frame(width: 176)
        .background(Color(nsColor: .controlBackgroundColor).opacity(0.78), in: RoundedRectangle(cornerRadius: 12))
        .overlay {
            RoundedRectangle(cornerRadius: 12)
                .stroke(Color.primary.opacity(0.08))
        }
    }

    @ViewBuilder
    private func thumbnail(_ entry: RecentMenuEntry, symbol: String) -> some View {
        let image: NSImage? = switch entry {
        case .capture(let item): recentCaptures.thumbnails[item.id]
        case .studioDraft(let project): studioDrafts.thumbnails[project.id]
        }
        ZStack(alignment: .bottomLeading) {
            if let image {
                Image(nsImage: image)
                    .resizable()
                    .scaledToFill()
            } else {
                ZStack {
                    Color.primary.opacity(0.04)
                    Image(systemName: symbol)
                        .font(.title2)
                        .foregroundStyle(.secondary)
                }
            }
            Image(systemName: symbol)
                .font(.caption.weight(.semibold))
                .padding(5)
                .foregroundStyle(.white)
                .background(.black.opacity(0.58), in: RoundedRectangle(cornerRadius: 5))
                .padding(6)

        }
        .frame(width: 160, height: 90)
        .clipShape(RoundedRectangle(cornerRadius: 8))
    }

    @ViewBuilder
    private func thumbnailActions(_ entry: RecentMenuEntry) -> some View {
        let presentation = RecentCapturePresentation(entry)
        switch entry {
        case .capture(let item):
            quickAction(copyFeedback(entry) ? "checkmark" : "doc.on.doc", "Copy \(item.url.lastPathComponent)", copyFeedback(entry) ? "Copied" : "Copy", entryID: entry.id) {
                copy(item, entryID: entry.id)
            }
            quickAction("pencil", "Edit \(item.url.lastPathComponent)", "Edit", entryID: entry.id) {
                dismiss()
                open(entry)
            }
            quickAction("folder", "Show \(item.url.lastPathComponent) in Finder", "Reveal", entryID: entry.id) {
                dismiss()
                NSWorkspace.shared.activateFileViewerSelecting([item.url])
            }
            quickAction("trash", "Delete \(item.url.lastPathComponent)", "Delete", entryID: entry.id) {
                requestDelete(entry)
            }
            .tint(.red)
        case .studioDraft(let project):
            quickAction("pencil", "Open \(presentation.title) in Studio", "Edit", entryID: entry.id) {
                dismiss()
                open(entry)
            }
            quickAction("folder", "Show \(presentation.title) in Finder", "Reveal", entryID: entry.id) {
                dismiss()
                reveal(project)
            }
            quickAction("trash", "Delete Studio project \(presentation.title)", "Delete", entryID: entry.id) {
                requestDelete(entry)
            }
            .tint(.red)
        }
    }

    @ViewBuilder
    private func quickAction(
        _ symbol: String,
        _ accessibilityLabel: String,
        _ help: String,
        entryID: String,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.caption)
                .frame(width: 26, height: 26)
                .background(.regularMaterial, in: Circle())
                .overlay {
                    Circle().stroke(.white.opacity(0.18))
                }
        }
        .buttonStyle(.plain)
        .focused($focusedEntryID, equals: entryID)
        .help(help)
        .accessibilityLabel(accessibilityLabel)
    }

    private func openHint(_ entry: RecentMenuEntry) -> String {
        switch entry {
        case .capture(let item): "Opens this \(item.type.label.lowercased()) in its editor."
        case .studioDraft: "Opens this project in Tiny Clips Studio."
        }
    }

    private func open(_ entry: RecentMenuEntry) {
        switch entry {
        case .capture(let item):
            captureManager.openRecentCapture(item)
        case .studioDraft(let project):
            StudioWindowRegistry.shared.open(projectID: project.id)
        }
    }

    private func copy(_ item: RecentCaptureItem, entryID: String) {
        let copied: Bool
        switch item.type {
        case .screenshot:
            guard let image = ImagePixelDensity.loadImage(contentsOf: item.url) else {
                SaveService.shared.showError("Could not prepare the screenshot for the clipboard.")
                return
            }
            copied = SaveService.shared.copyImageToClipboard(image)
        case .video, .gif:
            let pasteboard = NSPasteboard.general
            pasteboard.clearContents()
            copied = pasteboard.writeObjects([item.url as NSURL])
        }
        guard copied else {
            SaveService.shared.showError("Could not copy the capture to the clipboard.")
            return
        }
        copiedEntryID = entryID
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { [entryID] in
            if copiedEntryID == entryID {
                copiedEntryID = nil
            }
        }
        AccessibilityAnnouncementService.shared.announce("\(item.url.lastPathComponent) copied.", priority: .medium)
    }

    private func copyFeedback(_ entry: RecentMenuEntry) -> Bool {
        copiedEntryID == entry.id
    }

    private func reveal(_ project: StudioProjectSummary) {
        do {
            let directory = try StudioProjectStore.shared.paths(forID: project.id).projectDirectory
            NSWorkspace.shared.activateFileViewerSelecting([directory])
        } catch {
            SaveService.shared.showError("Could not show the Studio project: \(error.localizedDescription)")
        }
    }

    private func requestDelete(_ entry: RecentMenuEntry) {
        if CaptureSettings.shared.clipsManagerConfirmDelete {
            pendingDeleteEntry = entry
        } else {
            performDelete(entry)
        }
    }

    private func performDelete(_ entry: RecentMenuEntry) {
        switch entry {
        case .capture(let item):
            do {
                var trashedURL: NSURL?
                try FileManager.default.trashItem(at: item.url, resultingItemURL: &trashedURL)
                recentCaptures.remove(item)
            } catch {
                SaveService.shared.showError("Could not move the capture to Trash: \(error.localizedDescription)")
                return
            }
        case .studioDraft(let project):
            if StudioWindowRegistry.shared.openProjectIDs.contains(project.id) {
                SaveService.shared.showError("\"\(RecentCapturePresentation(entry).title)\" is open in Studio. Close it before deleting the project.")
                return
            }
            do {
                try StudioProjectStore.shared.delete(id: project.id)
                studioDrafts.reload()
            } catch {
                SaveService.shared.showError("Could not delete the Studio project: \(error.localizedDescription)")
                return
            }
        }
        dismiss()
    }

    private var deleteAlertBinding: Binding<Bool> {
        Binding(
            get: { pendingDeleteEntry != nil },
            set: { if !$0 { pendingDeleteEntry = nil } }
        )
    }

    private var deleteTitle: String {
        guard let pendingDeleteEntry else { return "Delete item?" }
        return switch pendingDeleteEntry {
        case .capture: "Move capture to Trash?"
        case .studioDraft: "Delete Studio project?"
        }
    }

    private var deleteActionTitle: String {
        guard let pendingDeleteEntry else { return "Delete" }
        return switch pendingDeleteEntry {
        case .capture: "Move to Trash"
        case .studioDraft: "Delete Project"
        }
    }

    private var deleteMessage: String {
        guard let pendingDeleteEntry else { return "" }
        let title = RecentCapturePresentation(pendingDeleteEntry).title
        return switch pendingDeleteEntry {
        case .capture: "\"\(title)\" will be moved to Trash."
        case .studioDraft: "\"\(title)\" and its managed recordings and edits will be removed. Exported videos and external originals are kept."
        }
    }
}

// MARK: - Menu Bar Label

struct MenuBarLabelView: View {
    @ObservedObject var captureManager: CaptureManager
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Image(systemName: captureManager.isRecording ? "record.circle.fill" : "camera.viewfinder")
            .foregroundStyle(captureManager.isRecording ? .red : .primary)
            .onAppear {
                ScreenshotEditorRegistry.shared.installOpener { sessionID in
                    openWindow(id: ScreenshotEditorRegistry.windowID, value: sessionID)
                }
                AutomationWindowOpener.shared.install { windowID in
                    openWindow(id: windowID)
                }
            }
    }
}

// MARK: - Performance Signposts

enum PerformanceSignposts {
    private static let signposter = OSSignposter(subsystem: Bundle.main.bundleIdentifier ?? "com.tinyclips.app", category: "Performance")
    private static let stateQueue = DispatchQueue(label: "com.tinyclips.performance-signposts")
    private static var pendingSettingsOpenState: OSSignpostIntervalState?

    static func beginSettingsOpen() {
        let state = signposter.beginInterval("SettingsOpen")
        stateQueue.sync {
            pendingSettingsOpenState = state
        }
    }

    static func endSettingsOpenIfNeeded() {
        let state: OSSignpostIntervalState? = stateQueue.sync {
            defer { pendingSettingsOpenState = nil }
            return pendingSettingsOpenState
        }

        guard let state else { return }
        signposter.endInterval("SettingsOpen", state)
    }

    static func markVideoTabOpened() {
        signposter.emitEvent("VideoSettingsTabOpened")
    }

    static func measureMicrophoneEnumeration<T>(_ operation: () -> T) -> T {
        let state = signposter.beginInterval("MicrophoneEnumeration")
        defer { signposter.endInterval("MicrophoneEnumeration", state) }
        return operation()
    }
}
