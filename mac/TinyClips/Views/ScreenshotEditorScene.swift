import AppKit
import Combine
import SwiftUI

struct ScreenshotEditorCommandActions {
    let close: () -> Void
    let deleteSource: () -> Void
    let save: () -> Void
    let saveAs: () -> Void
    let revealInFinder: () -> Void
    let undo: () -> Void
    let redo: () -> Void
    let copy: () -> Void
    let deleteSelectedAnnotation: () -> Void
    let clearAnnotations: () -> Void
    let applyCrop: () -> Void
    let findSensitiveText: () -> Void
    let zoomIn: () -> Void
    let zoomOut: () -> Void
    let fitZoom: () -> Void
    let nativeSizeZoom: () -> Void
    let canUndo: Bool
    let canRedo: Bool
    let canDeleteSelectedAnnotation: Bool
    let hasAnnotations: Bool
    let canApplyCrop: Bool
    let canFindSensitiveText: Bool
    let isEditingText: Bool
    let canZoomIn: Bool
    let canZoomOut: Bool
}

private struct ScreenshotEditorCommandActionsKey: FocusedValueKey {
    typealias Value = ScreenshotEditorCommandActions
}

enum ScreenshotEditorPasteboardShortcutPolicy {
    enum Action {
        case cut
        case copy
        case paste
    }

    static func shouldInstall(
        _ action: Action,
        hasActiveEditor: Bool,
        isEditingText: Bool
    ) -> Bool {
        guard hasActiveEditor else { return false }
        return action == .copy || isEditingText
    }
}

extension FocusedValues {
    var screenshotEditorCommandActions: ScreenshotEditorCommandActions? {
        get { self[ScreenshotEditorCommandActionsKey.self] }
        set { self[ScreenshotEditorCommandActionsKey.self] = newValue }
    }
}

private struct ScreenshotEditorMenuCommands: Commands {
    @FocusedValue(\.screenshotEditorCommandActions) private var editor
    @ObservedObject private var registry = ScreenshotEditorRegistry.shared

    var body: some Commands {
        CommandGroup(replacing: .appTermination) {
            Button("Close Window") {
                if let editor {
                    editor.close()
                } else {
                    NSApp.keyWindow?.performClose(nil)
                }
            }
            .disabled(editor == nil)
            .keyboardShortcut("q", modifiers: .command)
        }

        CommandGroup(replacing: .saveItem) {
            // Replacing `.saveItem` removes SwiftUI's default Close item app-wide, and a separate
            // `before: .saveItem` group never reaches the menu, so Close has to live in this group.
            // The editor routes through its unsaved-changes prompt; other windows close normally.
            Button("Close") {
                if let editor {
                    editor.close()
                } else {
                    NSApp.keyWindow?.performClose(nil)
                }
            }
            .keyboardShortcut("w", modifiers: .command)

            Button("Close All Screenshot Editors") {
                registry.closeAllInteractively()
            }
            .disabled(!registry.hasOpenSessions || registry.isClosingAll)
            .keyboardShortcut("w", modifiers: [.command, .option])

            Divider()

            Button("Save") {
                editor?.save()
            }
            .disabled(editor == nil)
            .keyboardShortcut("s", modifiers: .command)

            Button("Save As…") {
                editor?.saveAs()
            }
            .disabled(editor == nil)
            .keyboardShortcut("s", modifiers: [.command, .shift])

            Divider()

            Button("Reveal in Finder") {
                editor?.revealInFinder()
            }
            .disabled(editor == nil)
            .keyboardShortcut("r", modifiers: [.command, .shift])
        }

        CommandGroup(after: .saveItem) {
            Button("Delete Screenshot…", role: .destructive) {
                editor?.deleteSource()
            }
            .disabled(editor == nil)
        }

        CommandGroup(replacing: .undoRedo) {
            Button("Undo") {
                if editor?.isEditingText == true {
                    performTextAction("undo:")
                } else {
                    editor?.undo()
                }
            }
            .disabled(editor == nil || (editor?.isEditingText != true && editor?.canUndo != true))
            .keyboardShortcut("z", modifiers: .command)

            Button("Redo") {
                if editor?.isEditingText == true {
                    performTextAction("redo:")
                } else {
                    editor?.redo()
                }
            }
            .disabled(editor == nil || (editor?.isEditingText != true && editor?.canRedo != true))
            .keyboardShortcut("z", modifiers: [.command, .shift])
        }

        CommandGroup(replacing: .pasteboard) {
            pasteboardButton(
                "Cut",
                action: .cut,
                shortcut: "x",
                disabled: editor?.isEditingText != true
            ) {
                performTextAction("cut:")
            }

            pasteboardButton(
                "Copy",
                action: .copy,
                shortcut: "c",
                disabled: editor == nil
            ) {
                if editor?.isEditingText == true {
                    performTextAction("copy:")
                } else {
                    editor?.copy()
                }
            }

            pasteboardButton(
                "Paste",
                action: .paste,
                shortcut: "v",
                disabled: editor?.isEditingText != true
            ) {
                performTextAction("paste:")
            }

            Divider()

            Button("Delete Annotation", role: .destructive) {
                editor?.deleteSelectedAnnotation()
            }
            .disabled(editor?.canDeleteSelectedAnnotation != true || editor?.isEditingText == true)

            Button("Clear Annotations…") {
                editor?.clearAnnotations()
            }
            .disabled(editor?.hasAnnotations != true)

            Divider()
            Button("Find sensitive text") { editor?.findSensitiveText() }
                .disabled(editor?.canFindSensitiveText != true)
        }

        CommandGroup(after: .toolbar) {
            Button("Apply Crop") {
                editor?.applyCrop()
            }
            .disabled(editor?.canApplyCrop != true)

            Divider()

            Button("Zoom In") {
                editor?.zoomIn()
            }
            .disabled(editor?.canZoomIn != true)
            .keyboardShortcut("+", modifiers: .command)

            Button("Zoom Out") {
                editor?.zoomOut()
            }
            .disabled(editor?.canZoomOut != true)
            .keyboardShortcut("-", modifiers: .command)

            Button("Fit to Window") {
                editor?.fitZoom()
            }
            .disabled(editor == nil)
            .keyboardShortcut("0", modifiers: .command)

            Button("Actual Size (1:1)") {
                editor?.nativeSizeZoom()
            }
            .disabled(editor == nil)
        }
    }

    @ViewBuilder
    private func pasteboardButton(
        _ title: String,
        action: ScreenshotEditorPasteboardShortcutPolicy.Action,
        shortcut: KeyEquivalent,
        disabled: Bool,
        perform: @escaping () -> Void
    ) -> some View {
        if ScreenshotEditorPasteboardShortcutPolicy.shouldInstall(
            action,
            hasActiveEditor: editor != nil,
            isEditingText: editor?.isEditingText == true
        ) {
            Button(title, action: perform)
                .disabled(disabled)
                .keyboardShortcut(shortcut, modifiers: .command)
        } else {
            Button(title, action: perform)
                .disabled(disabled)
        }
    }

    private func performTextAction(_ selectorName: String) {
        NSApp.sendAction(NSSelectorFromString(selectorName), to: nil, from: nil)
    }
}

struct ScreenshotEditorScene: Scene {
    var body: some Scene {
        WindowGroup("Edit Screenshot", id: ScreenshotEditorRegistry.windowID, for: UUID.self) { $sessionID in
            ScreenshotEditorSceneRoot(sessionID: sessionID)
        }
        .defaultSize(width: 1040, height: 720)
        .commands {
            AppWindowCommands()
            ScreenshotEditorMenuCommands()
        }
    }
}

private final class ScreenshotEditorWindowObserverView: NSView {
    var onAttach: ((NSWindow) -> Void)?
    var onClose: (() -> Void)?
    private var closeObserver: AnyCancellable?

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        closeObserver = nil
        guard let window else { return }
        closeObserver = NotificationCenter.default.publisher(for: NSWindow.willCloseNotification, object: window)
            .sink { [weak self] _ in
                MainActor.assumeIsolated {
                    self?.onClose?()
                }
            }
        DispatchQueue.main.async { [weak self, weak window] in
            guard let self, let window, self.window === window else { return }
            self.onAttach?(window)
        }
    }
}

private struct ScreenshotEditorWindowObserver: NSViewRepresentable {
    let sessionID: UUID

    func makeNSView(context: Context) -> ScreenshotEditorWindowObserverView {
        let view = ScreenshotEditorWindowObserverView()
        updateNSView(view, context: context)
        return view
    }

    func updateNSView(_ view: ScreenshotEditorWindowObserverView, context: Context) {
        view.onAttach = { window in
            ScreenshotEditorRegistry.shared.windowDidAttach(window, for: sessionID)
        }
        view.onClose = {
            ScreenshotEditorRegistry.shared.windowWillClose(for: sessionID)
        }
    }
}

private struct ScreenshotEditorSceneRoot: View {
    let sessionID: UUID?
    @Environment(\.dismissWindow) private var dismissWindow
    @State private var resolvedSession: ScreenshotEditorRegistry.Session?

    var body: some View {
        ZStack {
            if let session = resolvedSession, let sessionID {
                ScreenshotEditorView(
                    imageURL: session.imageURL,
                    initialSaveURL: session.initialSaveURL,
                    deleteSourceAfterSave: session.deleteSourceAfterSave,
                    lifecycle: session.lifecycle
                ) { resultURL in
                    guard !session.lifecycle.isFinishing else { return }
                    session.lifecycle.isFinishing = true
                    session.lifecycle.completionResult = resultURL
                    // `dismissWindow` is ignored while a sheet is attached, and a confirmation dialog
                    // runs its button action before detaching, so close on the next main-loop turn.
                    DispatchQueue.main.async {
                        dismissWindow(id: ScreenshotEditorRegistry.windowID, value: sessionID)
                    }
                }
            } else {
                Color(NSColor.windowBackgroundColor)
                    .frame(minWidth: 700, minHeight: 520)
            }
        }
        .background {
            if let sessionID {
                ScreenshotEditorWindowObserver(sessionID: sessionID)
                    .allowsHitTesting(false)
            }
        }
        .onAppear {
            guard let sessionID else { return }
            if let session = ScreenshotEditorRegistry.shared.session(for: sessionID) {
                resolvedSession = session
            } else {
                dismissWindow(id: ScreenshotEditorRegistry.windowID, value: sessionID)
            }
        }
    }
}

@MainActor
final class ScreenshotEditorLifecycle: ObservableObject {
    enum CloseMode: Equatable {
        case interactive
        case safeOnly
    }

    enum BlockReason: String {
        case unsavedChanges, unsavedCapture, saving, notReady, busy

        /// The user can answer a prompt to close anyway.
        var needsConfirmation: Bool {
            self == .unsavedChanges || self == .unsavedCapture
        }
    }

    enum CloseOutcome: Equatable {
        case closed
        case cancelled
        case blocked(BlockReason)
    }

    struct CloseRequest: Equatable {
        let id = UUID()
        let mode: CloseMode
    }

    @Published var isSaving = false
    @Published var lastSavedURL: URL?
    @Published var isFinishing = false
    @Published private(set) var closeRequest: CloseRequest?
    var completionResult: URL?
    weak var window: (any WindowPresentationTarget)?
    private weak var viewModel: ScreenshotEditorViewModel?
    private let deleteSourceAfterSave: Bool
    private var completion: CheckedContinuation<CloseOutcome, Never>?

    init(deleteSourceAfterSave: Bool) {
        self.deleteSourceAfterSave = deleteSourceAfterSave
    }

    func attach(_ viewModel: ScreenshotEditorViewModel) {
        self.viewModel = viewModel
    }

    var blockReason: BlockReason? {
        guard let viewModel, window != nil else { return .notReady }
        if isSaving { return .saving }
        // A shape or pen stroke still being drawn is not saved and not yet counted as a change.
        if isFinishing || window?.hasAttachedSheet == true || viewModel.currentAnnotation != nil { return .busy }
        if deleteSourceAfterSave && lastSavedURL == nil { return .unsavedCapture }
        if viewModel.hasUnsavedChanges || viewModel.isEditingText { return .unsavedChanges }
        return nil
    }

    /// Why the editor cannot close in `mode`, or nil when it can close or ask the user first.
    func blockReason(for mode: CloseMode) -> BlockReason? {
        guard let reason = blockReason else { return nil }
        return mode == .interactive && reason.needsConfirmation ? nil : reason
    }

    func requestClose(mode: CloseMode) async -> CloseOutcome {
        guard completion == nil else { return .blocked(.busy) }
        if let reason = blockReason(for: mode) {
            return .blocked(reason)
        }
        return await withCheckedContinuation { continuation in
            completion = continuation
            closeRequest = CloseRequest(mode: mode)
        }
    }

    func resolve(_ outcome: CloseOutcome) {
        let pending = completion
        completion = nil
        closeRequest = nil
        pending?.resume(returning: outcome)
    }

    func finish() {
        resolve(.closed)
        viewModel = nil
        window = nil
    }
}

@MainActor
final class ScreenshotEditorRegistry: ObservableObject {
    static let shared = ScreenshotEditorRegistry()
    static let windowID = "screenshot-editor"

    struct Session {
        let imageURL: URL
        /// `imageURL` with symlinks resolved, so one file reached by two paths is one session.
        let identity: URL
        let initialSaveURL: URL
        let deleteSourceAfterSave: Bool
        let onComplete: (URL?) -> Void
        let lifecycle: ScreenshotEditorLifecycle
    }

    @Published private var sessions: [UUID: Session] = [:]
    @Published private(set) var isClosingAll = false
    private var sessionOrder: [UUID] = []
    private var pendingOpens: [UUID] = []
    private var pendingWindowPresentations: Set<UUID> = []
    private var opener: ((UUID) -> Void)?
    private let presentWindow: @MainActor (any WindowPresentationTarget) -> Void
    private let applyActivationPolicy: @MainActor () -> Void

    init(
        presentWindow: @escaping @MainActor (any WindowPresentationTarget) -> Void = { WindowPresenter.shared.present($0) },
        applyActivationPolicy: @escaping @MainActor () -> Void = { TinyClipsActivationPolicy.applyCurrent() }
    ) {
        self.presentWindow = presentWindow
        self.applyActivationPolicy = applyActivationPolicy
    }

    var hasOpenSessions: Bool {
        !sessions.isEmpty
    }

    func installOpener(_ opener: @escaping (UUID) -> Void) {
        self.opener = opener
        let pending = pendingOpens
        pendingOpens.removeAll()
        for id in pending {
            opener(id)
        }
    }

    func present(
        imageURL: URL,
        initialSaveURL: URL? = nil,
        deleteSourceAfterSave: Bool = false,
        onComplete: @escaping (URL?) -> Void
    ) {
        let identity = imageURL.standardizedFileURL.resolvingSymlinksInPath()
        if let session = sessions.values.first(where: { $0.identity == identity && !$0.lifecycle.isFinishing }) {
            if let window = session.lifecycle.window {
                presentWindow(window)
            }
            return
        }
        let id = UUID()
        sessions[id] = Session(
            imageURL: imageURL,
            identity: identity,
            initialSaveURL: initialSaveURL ?? imageURL,
            deleteSourceAfterSave: deleteSourceAfterSave,
            onComplete: onComplete,
            lifecycle: ScreenshotEditorLifecycle(deleteSourceAfterSave: deleteSourceAfterSave)
        )
        sessionOrder.append(id)
        pendingWindowPresentations.insert(id)
        applyActivationPolicy()
        if let opener {
            opener(id)
        } else {
            pendingOpens.append(id)
        }
    }

    func session(for id: UUID) -> Session? {
        sessions[id]
    }

    func windowDidAttach(_ window: any WindowPresentationTarget, for id: UUID) {
        guard sessions[id] != nil, pendingWindowPresentations.remove(id) != nil else { return }
        sessions[id]?.lifecycle.window = window
        presentWindow(window)
    }

    func windowWillClose(for id: UUID) {
        guard let session = sessions[id] else { return }
        finish(id, result: session.lifecycle.completionResult)
    }

    func finish(_ id: UUID, result: URL?) {
        guard let session = sessions.removeValue(forKey: id) else { return }
        sessionOrder.removeAll { $0 == id }
        pendingOpens.removeAll { $0 == id }
        pendingWindowPresentations.remove(id)
        if let window = session.lifecycle.window as? NSWindow {
            TinyClipsActivationPolicy.endPresentation(of: window)
        }
        session.lifecycle.finish()
        session.onComplete(result)
        applyActivationPolicy()
    }

    func listEditors() -> [[String: Any]] {
        sessionOrder.compactMap { id in
            guard let session = sessions[id] else { return nil }
            let reason = session.lifecycle.blockReason
            var result: [String: Any] = [
                "id": id.uuidString,
                "path": session.imageURL.path,
                "canCloseSafely": reason == nil
            ]
            if let reason { result["reason"] = reason.rawValue }
            return result
        }
    }

    func closeAll(
        mode: ScreenshotEditorLifecycle.CloseMode,
        validateAccess: () throws -> Void = { try Task.checkCancellation() }
    ) async throws -> (closed: [String], cancelled: Bool, blocked: Bool) {
        guard !isClosingAll else {
            throw AutomationError(.busy, "Screenshot editors are already being closed.")
        }
        isClosingAll = true
        defer { isClosingAll = false }
        let targets = sessionOrder
        var closed: [String] = []
        var blocked = false
        for id in targets {
            try validateAccess()
            guard let session = sessions[id] else { continue }
            if mode == .interactive, let window = session.lifecycle.window,
               session.lifecycle.blockReason?.needsConfirmation == true {
                // The prompt is attached to this window, which may be minimized or behind others.
                presentWindow(window)
            }
            switch await session.lifecycle.requestClose(mode: mode) {
            case .closed:
                closed.append(id.uuidString)
            case .cancelled:
                return (closed, true, blocked)
            case .blocked:
                blocked = true
            }
        }
        try validateAccess()
        return (closed, false, blocked)
    }

    func closeAllInteractively() {
        guard !isClosingAll else { return }
        Task {
            guard let result = try? await closeAll(mode: .interactive) else { return }
            if !result.cancelled, result.blocked {
                SaveService.shared.showError("Some screenshot editors are busy or still opening. Finish their current action, then try Close All Screenshot Editors again.")
            }
        }
    }
}
