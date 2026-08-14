import AppKit
import SwiftUI

struct ScreenshotEditorCommandActions {
    let save: () -> Void
    let saveAs: () -> Void
    let revealInFinder: () -> Void
    let undo: () -> Void
    let redo: () -> Void
    let copy: () -> Void
    let clearAnnotations: () -> Void
    let applyCrop: () -> Void
    let zoomIn: () -> Void
    let zoomOut: () -> Void
    let fitZoom: () -> Void
    let canUndo: Bool
    let canRedo: Bool
    let hasAnnotations: Bool
    let canApplyCrop: Bool
    let isEditingText: Bool
    let canZoomIn: Bool
    let canZoomOut: Bool
}

private struct ScreenshotEditorCommandActionsKey: FocusedValueKey {
    typealias Value = ScreenshotEditorCommandActions
}

extension FocusedValues {
    var screenshotEditorCommandActions: ScreenshotEditorCommandActions? {
        get { self[ScreenshotEditorCommandActionsKey.self] }
        set { self[ScreenshotEditorCommandActionsKey.self] = newValue }
    }
}

private struct ScreenshotEditorMenuCommands: Commands {
    @FocusedValue(\.screenshotEditorCommandActions) private var editor

    var body: some Commands {
        CommandGroup(replacing: .saveItem) {
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
            Button("Cut") {
                performTextAction("cut:")
            }
            .disabled(editor?.isEditingText != true)
            .keyboardShortcut("x", modifiers: .command)

            Button("Copy") {
                if editor?.isEditingText == true {
                    performTextAction("copy:")
                } else {
                    editor?.copy()
                }
            }
            .disabled(editor == nil)
            .keyboardShortcut("c", modifiers: .command)

            Button("Paste") {
                performTextAction("paste:")
            }
            .disabled(editor?.isEditingText != true)
            .keyboardShortcut("v", modifiers: .command)

            Divider()

            Button("Clear Annotations…") {
                editor?.clearAnnotations()
            }
            .disabled(editor?.hasAnnotations != true)
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

private struct ScreenshotEditorSceneRoot: View {
    let sessionID: UUID?
    @Environment(\.dismissWindow) private var dismissWindow
    @State private var resolvedSession: ScreenshotEditorRegistry.Session?
    @State private var completionResult: URL?

    var body: some View {
        Group {
            if let session = resolvedSession, let sessionID {
                ScreenshotEditorView(
                    imageURL: session.imageURL,
                    initialSaveURL: session.initialSaveURL,
                    deleteSourceAfterSave: session.deleteSourceAfterSave
                ) { resultURL in
                    completionResult = resultURL
                    dismissWindow(id: ScreenshotEditorRegistry.windowID, value: sessionID)
                }
            } else {
                Color(NSColor.windowBackgroundColor)
                    .frame(minWidth: 700, minHeight: 520)
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
        .onDisappear {
            guard let sessionID else { return }
            ScreenshotEditorRegistry.shared.finish(sessionID, result: completionResult)
        }
    }
}

@MainActor
final class ScreenshotEditorRegistry {
    static let shared = ScreenshotEditorRegistry()
    static let windowID = "screenshot-editor"

    struct Session {
        let imageURL: URL
        let initialSaveURL: URL
        let deleteSourceAfterSave: Bool
        let onComplete: (URL?) -> Void
    }

    private var sessions: [UUID: Session] = [:]
    private var pendingOpens: [UUID] = []
    private var opener: ((UUID) -> Void)?

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
        let id = UUID()
        sessions[id] = Session(
            imageURL: imageURL,
            initialSaveURL: initialSaveURL ?? imageURL,
            deleteSourceAfterSave: deleteSourceAfterSave,
            onComplete: onComplete
        )
        if let opener {
            opener(id)
        } else {
            pendingOpens.append(id)
        }
    }

    func session(for id: UUID) -> Session? {
        sessions[id]
    }

    func finish(_ id: UUID, result: URL?) {
        guard let session = sessions.removeValue(forKey: id) else { return }
        session.onComplete(result)
    }
}
