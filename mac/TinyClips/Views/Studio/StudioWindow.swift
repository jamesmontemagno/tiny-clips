import AppKit
import Carbon.HIToolbox
import SwiftUI

// MARK: - Registry

/// Opens Studio editor windows, one per project, and knows which projects are open.
@MainActor
final class StudioWindowRegistry {
    static let shared = StudioWindowRegistry()

    private var windows: [String: StudioWindow] = [:]
    /// Run once when a project's window closes. Set for a window opened straight after a recording.
    private var closeHandlers: [String: @MainActor () -> Void] = [:]

    /// Projects open in an editor. Storage cleanup must leave these alone.
    var openProjectIDs: Set<String> {
        Set(windows.keys)
    }

    var hasOpenWindows: Bool {
        !windows.isEmpty
    }

    /// Writes the unsaved edits of every open editor. For the moment the app quits.
    func saveAllBeforeQuitting() {
        for window in windows.values {
            window.saveBeforeQuitting()
        }
    }

    /// Opens the project a video was exported from. Returns false when Studio is off, or the video
    /// did not come from a project that is still stored.
    @discardableResult
    func openProject(forExportedVideoAt url: URL) -> Bool {
        guard CaptureSettings.shared.studioPreviewEnabled,
              let projectID = try? StudioProjectStore.shared.findProjectID(exportedPath: url.path)
        else {
            return false
        }
        open(projectID: projectID)
        return true
    }

    /// Opens the editor for a project, or brings its window forward when it is already open.
    /// `onClose` runs once after that window has closed.
    func open(projectID: String, onClose: (@MainActor () -> Void)? = nil) {
        guard CaptureSettings.shared.studioPreviewEnabled else {
            SaveService.shared.showNotice("Recording saved as a Tiny Clips Studio project.")
            onClose?()
            return
        }

        let window: StudioWindow
        if let existing = windows[projectID] {
            window = existing
        } else {
            // Said before the window reads its project, so that a storage cleanup that is
            // under way cannot take the project's folder from under it.
            StudioProjectStore.shared.beginUse(id: projectID)
            window = StudioWindow(projectID: projectID) { [weak self] id in
                self?.windowDidClose(projectID: id)
            }
            windows[projectID] = window
            // An editor window gets a Dock icon and the menu bar, as the screenshot editor does.
            TinyClipsActivationPolicy.applyCurrent()
        }
        if let onClose {
            closeHandlers[projectID] = onClose
        }

        // Shown on the next run loop turn so it does not fight menu tracking or a closing panel.
        DispatchQueue.main.async {
            window.collectionBehavior.insert(.moveToActiveSpace)
            NSRunningApplication.current.activate(options: [.activateAllWindows])
            window.makeKeyAndOrderFront(nil)
            window.orderFrontRegardless()
        }
    }

    private func windowDidClose(projectID: String) {
        guard let closed = windows.removeValue(forKey: projectID) else { return }
        StudioProjectStore.shared.endUse(id: projectID)
        let closeHandler = closeHandlers.removeValue(forKey: projectID)
        // Finished on the next run loop turn, so the window is not deallocated while it is closing.
        DispatchQueue.main.async {
            _ = closed
            TinyClipsActivationPolicy.applyCurrent()
            StudioMaintenance.cleanUp()
            closeHandler?()
        }
    }
}

// MARK: - Menu

/// The Studio menu. Its items have no target, so they reach the key `StudioWindow` through the
/// responder chain and are disabled while another window is in front.
///
/// Only Export has a key equivalent here. The other shortcuts are single keys (Space, arrows, I, O,
/// S, X, R, Z, Delete, and 1 to 4), which as menu key equivalents would be taken from text fields in every
/// window, so `StudioWindow` handles them itself. That also keeps them working while Tiny Clips
/// runs without a Dock icon and its menu bar is not shown.
@MainActor
enum StudioMenuCommands {
    private static let menuTitle = "Studio"

    static func installIfNeeded() {
        guard let mainMenu = NSApp.mainMenu, mainMenu.item(withTitle: menuTitle) == nil else { return }

        let menu = NSMenu(title: menuTitle)
        let exportItem = menuItem("Export", action: "studioExport:")
        exportItem.keyEquivalent = "e"
        exportItem.keyEquivalentModifierMask = [.command]
        menu.addItem(exportItem)
        menu.addItem(.separator())
        menu.addItem(menuItem("Undo", action: "studioUndo:"))
        menu.addItem(menuItem("Redo", action: "studioRedo:"))
        menu.addItem(.separator())
        menu.addItem(menuItem("Play/Pause", action: "studioTogglePlayback:"))
        menu.addItem(menuItem("Previous Frame", action: "studioPreviousFrame:"))
        menu.addItem(menuItem("Next Frame", action: "studioNextFrame:"))
        menu.addItem(.separator())
        menu.addItem(menuItem("Start Here", action: "studioMarkIn:"))
        menu.addItem(menuItem("End Here", action: "studioMarkOut:"))
        menu.addItem(.separator())
        menu.addItem(menuItem("Add Zoom", action: "studioAddZoom:"))
        menu.addItem(menuItem("Delete Zoom", action: "studioDeleteZoom:"))
        menu.addItem(menuItem("Previous Zoom", action: "studioPreviousZoom:"))
        menu.addItem(menuItem("Next Zoom", action: "studioNextZoom:"))
        menu.addItem(menuItem("Suggest Zooms", action: "studioSuggestZooms:"))
        menu.addItem(.separator())
        menu.addItem(menuItem("Split Scene", action: "studioSplitScene:"))
        menu.addItem(menuItem("Delete Scene", action: "studioDeleteScene:"))
        menu.addItem(menuItem("Previous Scene", action: "studioPreviousScene:"))
        menu.addItem(menuItem("Next Scene", action: "studioNextScene:"))
        menu.addItem(.separator())
        menu.addItem(menuItem("Add Cut", action: "studioAddCut:"))
        menu.addItem(menuItem("Delete Cut", action: "studioDeleteCut:"))
        menu.addItem(menuItem("Previous Cut", action: "studioPreviousCut:"))
        menu.addItem(menuItem("Next Cut", action: "studioNextCut:"))
        menu.addItem(.separator())
        menu.addItem(menuItem("Add Speed Change", action: "studioAddSpeed:"))
        menu.addItem(menuItem("Delete Speed Change", action: "studioDeleteSpeed:"))
        menu.addItem(menuItem("Previous Speed Change", action: "studioPreviousSpeed:"))
        menu.addItem(menuItem("Next Speed Change", action: "studioNextSpeed:"))
        menu.addItem(.separator())
        menu.addItem(menuItem("Screen Only", action: "studioLayoutScreen:"))
        menu.addItem(menuItem("Screen with Camera Bubble", action: "studioLayoutBubble:"))
        menu.addItem(menuItem("Side by Side", action: "studioLayoutSideBySide:"))
        menu.addItem(menuItem("Camera Only", action: "studioLayoutCamera:"))

        let item = NSMenuItem(title: menuTitle, action: nil, keyEquivalent: "")
        item.submenu = menu
        mainMenu.addItem(item)
    }

    private static func menuItem(_ title: String, action: String) -> NSMenuItem {
        NSMenuItem(title: title, action: NSSelectorFromString(action), keyEquivalent: "")
    }
}

// MARK: - Window

final class StudioWindow: NSWindow, NSWindowDelegate {
    private let projectID: String
    private let viewModel: StudioViewModel
    private var onClose: ((String) -> Void)?

    init(projectID: String, onClose: @escaping (String) -> Void) {
        self.projectID = projectID
        self.onClose = onClose
        self.viewModel = StudioViewModel(projectID: projectID)

        super.init(
            contentRect: NSRect(x: 0, y: 0, width: 1180, height: 760),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )

        title = "Tiny Clips Studio"
        isReleasedWhenClosed = false
        minSize = NSSize(width: 980, height: 672)
        delegate = self
        center()
        StudioMenuCommands.installIfNeeded()

        viewModel.requestClose = { [weak self] in
            self?.close()
        }
        contentView = NSHostingView(rootView: StudioRootView(viewModel: viewModel))
    }

    // MARK: NSWindowDelegate

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        viewModel.shouldClose()
    }

    func windowDidBecomeKey(_ notification: Notification) {
        // The main menu is rebuilt when scenes change, which can drop the Studio menu.
        StudioMenuCommands.installIfNeeded()
    }

    func windowWillClose(_ notification: Notification) {
        viewModel.requestClose = nil
        viewModel.tearDown()
        // Cleared before it is called, so a second close cannot report twice.
        let callback = onClose
        onClose = nil
        callback?(projectID)
    }

    // MARK: Menu actions

    func saveBeforeQuitting() { viewModel.saveBeforeQuitting() }

    @objc func studioExport(_ sender: Any?) { viewModel.export() }
    @objc func studioUndo(_ sender: Any?) { viewModel.undo() }
    @objc func studioRedo(_ sender: Any?) { viewModel.redo() }
    @objc func studioTogglePlayback(_ sender: Any?) { viewModel.togglePlayback() }
    @objc func studioPreviousFrame(_ sender: Any?) { viewModel.stepFrame(by: -1) }
    @objc func studioNextFrame(_ sender: Any?) { viewModel.stepFrame(by: 1) }
    @objc func studioMarkIn(_ sender: Any?) { viewModel.setTrimStartAtPlayhead() }
    @objc func studioMarkOut(_ sender: Any?) { viewModel.setTrimEndAtPlayhead() }
    @objc func studioAddZoom(_ sender: Any?) { viewModel.addZoomAtPlayhead() }
    @objc func studioDeleteZoom(_ sender: Any?) { viewModel.removeSelectedZoom() }
    @objc func studioPreviousZoom(_ sender: Any?) { viewModel.showPreviousZoom() }
    @objc func studioNextZoom(_ sender: Any?) { viewModel.showNextZoom() }
    @objc func studioSuggestZooms(_ sender: Any?) { viewModel.suggestZooms() }
    @objc func studioSplitScene(_ sender: Any?) { viewModel.splitSceneAtPlayhead() }
    @objc func studioDeleteScene(_ sender: Any?) { viewModel.removeCurrentScene() }
    @objc func studioPreviousScene(_ sender: Any?) { viewModel.stepToPreviousScene() }
    @objc func studioNextScene(_ sender: Any?) { viewModel.stepToNextScene() }
    @objc func studioAddCut(_ sender: Any?) { viewModel.addCutAtPlayhead() }
    @objc func studioDeleteCut(_ sender: Any?) { viewModel.removeSelectedCut() }
    @objc func studioPreviousCut(_ sender: Any?) { viewModel.showPreviousCut() }
    @objc func studioNextCut(_ sender: Any?) { viewModel.showNextCut() }
    @objc func studioAddSpeed(_ sender: Any?) { viewModel.addSpeedAtPlayhead() }
    @objc func studioDeleteSpeed(_ sender: Any?) { viewModel.removeSelectedSpeed() }
    @objc func studioPreviousSpeed(_ sender: Any?) { viewModel.showPreviousSpeed() }
    @objc func studioNextSpeed(_ sender: Any?) { viewModel.showNextSpeed() }
    @objc func studioLayoutScreen(_ sender: Any?) { viewModel.setLayout(.screen) }
    @objc func studioLayoutBubble(_ sender: Any?) { viewModel.setLayout(.bubble) }
    @objc func studioLayoutSideBySide(_ sender: Any?) { viewModel.setLayout(.sideBySide) }
    @objc func studioLayoutCamera(_ sender: Any?) { viewModel.setLayout(.camera) }

    override func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        guard let action = menuItem.action else {
            return super.validateMenuItem(menuItem)
        }

        if let layout = layout(forMenuAction: action) {
            menuItem.state = viewModel.isReady && viewModel.editor?.effectiveLayout == layout ? .on : .off
            return viewModel.isEditable && (layout == .screen || viewModel.hasCamera)
        }

        switch action {
        case #selector(studioExport(_:)):
            return viewModel.canExport
        case #selector(studioUndo(_:)):
            return viewModel.canUndo
        case #selector(studioRedo(_:)):
            return viewModel.canRedo
        case #selector(studioTogglePlayback(_:)),
            #selector(studioPreviousFrame(_:)),
            #selector(studioNextFrame(_:)),
            #selector(studioMarkIn(_:)),
            #selector(studioMarkOut(_:)):
            return viewModel.isEditable
        case #selector(studioAddZoom(_:)):
            return viewModel.canAddZoomAtPlayhead
        case #selector(studioDeleteZoom(_:)):
            return viewModel.isEditable && viewModel.selectedZoomIndex != nil
        case #selector(studioPreviousZoom(_:)),
            #selector(studioNextZoom(_:)):
            return viewModel.isEditable && !viewModel.zooms.isEmpty
        case #selector(studioSuggestZooms(_:)):
            return viewModel.canSuggestZooms
        case #selector(studioSplitScene(_:)):
            return viewModel.canSplitSceneAtPlayhead
        case #selector(studioDeleteScene(_:)):
            return viewModel.canRemoveCurrentScene
        case #selector(studioPreviousScene(_:)):
            return viewModel.isEditable && viewModel.currentSceneIndex > 0
        case #selector(studioNextScene(_:)):
            return viewModel.isEditable && viewModel.currentSceneIndex + 1 < viewModel.scenes.count
        case #selector(studioAddCut(_:)):
            return viewModel.canAddCutAtPlayhead
        case #selector(studioDeleteCut(_:)):
            return viewModel.isEditable && viewModel.selectedCutIndex != nil
        case #selector(studioPreviousCut(_:)),
            #selector(studioNextCut(_:)):
            return viewModel.isEditable && !viewModel.cuts.isEmpty
        case #selector(studioAddSpeed(_:)):
            return viewModel.canAddSpeedAtPlayhead
        case #selector(studioDeleteSpeed(_:)):
            return viewModel.isEditable && viewModel.selectedSpeedIndex != nil
        case #selector(studioPreviousSpeed(_:)),
            #selector(studioNextSpeed(_:)):
            return viewModel.isEditable && !viewModel.speedChanges.isEmpty
        default:
            return super.validateMenuItem(menuItem)
        }
    }

    private func layout(forMenuAction action: Selector) -> StudioLayout? {
        switch action {
        case #selector(studioLayoutScreen(_:)):
            return .screen
        case #selector(studioLayoutBubble(_:)):
            return .bubble
        case #selector(studioLayoutSideBySide(_:)):
            return .sideBySide
        case #selector(studioLayoutCamera(_:)):
            return .camera
        default:
            return nil
        }
    }

    // MARK: Keys

    /// Key presses arrive here only after the focused control or text field has passed on them, so
    /// a focused slider keeps its arrow keys and a focused button keeps Space.
    override func keyDown(with event: NSEvent) {
        if handleKey(event) {
            return
        }
        super.keyDown(with: event)
    }

    /// Esc, or Command-Period. It reaches the window because no control in Studio uses it as a key
    /// equivalent. It stops a running export; otherwise it closes the editor like the other Tiny
    /// Clips editors, asking first when that setting is on. A key that is being held does
    /// nothing: the press it belongs to has done what there was to do, perhaps in another window.
    /// Held a little too long, the Esc that closed a window in front would otherwise stop an
    /// export here, and the Esc that answered "Close Studio?" with Cancel would ask it again.
    override func cancelOperation(_ sender: Any?) {
        guard attachedSheet == nil else { return }
        if let event = NSApp.currentEvent, event.type == .keyDown, event.isARepeat {
            return
        }
        if viewModel.isExporting {
            viewModel.cancelExport()
            return
        }
        guard viewModel.confirmEscapeClose() else { return }
        performClose(nil)
    }

    /// The key window is asked before the menu bar, so these work even when the menu is not shown.
    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        if handleCommandKey(event) {
            return true
        }
        return super.performKeyEquivalent(with: event)
    }

    /// True while a mouse button is held down, which is how every drag in the editor is made. A
    /// key that changes the project waits until it is let go: a layout key in the middle of a drag
    /// of the camera would take the handle away from under the pointer, and Delete would take
    /// away the zoom, cut, or speed change the pointer is holding.
    private var isMouseButtonHeld: Bool { NSEvent.pressedMouseButtons != 0 }

    private func handleCommandKey(_ event: NSEvent) -> Bool {
        // Text being edited keeps its own Undo and Redo.
        guard event.type == .keyDown, attachedSheet == nil, !(firstResponder is NSText) else {
            return false
        }
        let modifiers = event.modifierFlags.intersection([.command, .option, .control, .shift])
        let key = event.charactersIgnoringModifiers?.lowercased()

        // During a drag these keys are taken and do nothing, so that the menu does not act on
        // them either.
        if key == "z", modifiers == [.command] {
            if !isMouseButtonHeld { viewModel.undo() }
            return true
        }
        if key == "z", modifiers == [.command, .shift] {
            if !isMouseButtonHeld { viewModel.redo() }
            return true
        }
        if key == "e", modifiers == [.command] {
            if !isMouseButtonHeld { viewModel.export() }
            return true
        }
        return false
    }

    private func handleKey(_ event: NSEvent) -> Bool {
        let modifiers = event.modifierFlags.intersection([.command, .option, .control, .shift])
        guard modifiers.isEmpty, attachedSheet == nil else { return false }
        guard viewModel.isReady, !viewModel.isExporting else { return false }
        let keyCode = Int(event.keyCode)

        // Holding an arrow key keeps stepping through frames. The other keys act once per press.
        // Those that change the project are taken and do nothing while a drag is being made;
        // Space and the arrows only move the playhead, and still do.
        let isFirstPress = !event.isARepeat
        let changesProject = isFirstPress && !isMouseButtonHeld
        if keyCode == kVK_Delete || keyCode == kVK_ForwardDelete {
            if viewModel.selectedCutIndex != nil {
                if changesProject { viewModel.removeSelectedCut() }
                return true
            }
            if viewModel.selectedZoomIndex != nil {
                if changesProject { viewModel.removeSelectedZoom() }
                return true
            }
            if viewModel.selectedSpeedIndex != nil {
                if changesProject { viewModel.removeSelectedSpeed() }
                return true
            }
        }

        switch keyCode {
        case kVK_LeftArrow:
            viewModel.stepFrame(by: -1)
            return true
        case kVK_RightArrow:
            viewModel.stepFrame(by: 1)
            return true
        case kVK_Space:
            if isFirstPress { viewModel.togglePlayback() }
            return true
        // The number row is matched by position, because some layouts need Shift to type a digit.
        case kVK_ANSI_1:
            if changesProject { viewModel.setLayout(.screen) }
            return true
        case kVK_ANSI_2:
            if changesProject { viewModel.setLayout(.bubble) }
            return true
        case kVK_ANSI_3:
            if changesProject { viewModel.setLayout(.sideBySide) }
            return true
        case kVK_ANSI_4:
            if changesProject { viewModel.setLayout(.camera) }
            return true
        default:
            break
        }

        // Letters are matched by what they type, so they follow the keyboard layout.
        switch event.charactersIgnoringModifiers?.lowercased() {
        case "i":
            if changesProject { viewModel.setTrimStartAtPlayhead() }
            return true
        case "o":
            if changesProject { viewModel.setTrimEndAtPlayhead() }
            return true
        case "s":
            if changesProject { viewModel.splitSceneAtPlayhead() }
            return true
        case "r":
            if changesProject { viewModel.addSpeedAtPlayhead() }
            return true
        case "x":
            if changesProject { viewModel.addCutAtPlayhead() }
            return true
        case "z":
            if changesProject { viewModel.addZoomAtPlayhead() }
            return true
        default:
            return false
        }
    }
}