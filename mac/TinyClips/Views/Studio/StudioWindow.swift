import AppKit
import Carbon.HIToolbox
import SwiftUI

// MARK: - Registry

/// Opens Studio editor windows, one per project, and knows which projects are open.
@MainActor
final class StudioWindowRegistry {
    static let shared = StudioWindowRegistry()

    private var windows: [String: StudioWindow] = [:]

    /// Projects open in an editor. Storage cleanup must leave these alone.
    var openProjectIDs: Set<String> {
        Set(windows.keys)
    }

    func open(projectID: String) {
        guard CaptureSettings.shared.studioPreviewEnabled else {
            SaveService.shared.showNotice("Recording saved as a Tiny Clips Studio project.")
            return
        }

        let window: StudioWindow
        if let existing = windows[projectID] {
            window = existing
        } else {
            window = StudioWindow(projectID: projectID) { [weak self] id in
                self?.windowDidClose(projectID: id)
            }
            windows[projectID] = window
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
        // Released on the next run loop turn, so the window is not deallocated while it is closing.
        DispatchQueue.main.async {
            _ = closed
        }
    }
}

// MARK: - Menu

/// The Studio menu. Its items have no target, so they reach the key `StudioWindow` through the
/// responder chain and are disabled while another window is in front.
///
/// Only Export has a key equivalent here. The other shortcuts are single keys (Space, arrows, I, O,
/// 1 to 4), which as menu key equivalents would be taken from text fields in every window, so
/// `StudioWindow` handles them itself. That also keeps them working while Tiny Clips runs without a
/// Dock icon and its menu bar is not shown.
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
        minSize = NSSize(width: 980, height: 640)
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

    @objc func studioExport(_ sender: Any?) { viewModel.export() }
    @objc func studioUndo(_ sender: Any?) { viewModel.undo() }
    @objc func studioRedo(_ sender: Any?) { viewModel.redo() }
    @objc func studioTogglePlayback(_ sender: Any?) { viewModel.togglePlayback() }
    @objc func studioPreviousFrame(_ sender: Any?) { viewModel.stepFrame(by: -1) }
    @objc func studioNextFrame(_ sender: Any?) { viewModel.stepFrame(by: 1) }
    @objc func studioMarkIn(_ sender: Any?) { viewModel.setTrimStartAtPlayhead() }
    @objc func studioMarkOut(_ sender: Any?) { viewModel.setTrimEndAtPlayhead() }
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

    /// The key window is asked before the menu bar, so these work even when the menu is not shown.
    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        if handleCommandKey(event) {
            return true
        }
        return super.performKeyEquivalent(with: event)
    }

    private func handleCommandKey(_ event: NSEvent) -> Bool {
        // Text being edited keeps its own Undo and Redo.
        guard event.type == .keyDown, attachedSheet == nil, !(firstResponder is NSText) else {
            return false
        }
        let modifiers = event.modifierFlags.intersection([.command, .option, .control, .shift])
        let key = event.charactersIgnoringModifiers?.lowercased()

        if key == "z", modifiers == [.command] {
            viewModel.undo()
            return true
        }
        if key == "z", modifiers == [.command, .shift] {
            viewModel.redo()
            return true
        }
        if key == "e", modifiers == [.command] {
            viewModel.export()
            return true
        }
        return false
    }

    private func handleKey(_ event: NSEvent) -> Bool {
        let modifiers = event.modifierFlags.intersection([.command, .option, .control, .shift])
        guard modifiers.isEmpty, attachedSheet == nil else { return false }
        let keyCode = Int(event.keyCode)

        if viewModel.isExporting {
            guard keyCode == kVK_Escape else { return false }
            viewModel.cancelExport()
            return true
        }
        guard viewModel.isReady else { return false }

        // Holding an arrow key keeps stepping through frames. The other keys act once per press.
        let isFirstPress = !event.isARepeat
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
            if isFirstPress { viewModel.setLayout(.screen) }
            return true
        case kVK_ANSI_2:
            if isFirstPress { viewModel.setLayout(.bubble) }
            return true
        case kVK_ANSI_3:
            if isFirstPress { viewModel.setLayout(.sideBySide) }
            return true
        case kVK_ANSI_4:
            if isFirstPress { viewModel.setLayout(.camera) }
            return true
        default:
            break
        }

        // Letters are matched by what they type, so they follow the keyboard layout.
        switch event.charactersIgnoringModifiers?.lowercased() {
        case "i":
            if isFirstPress { viewModel.setTrimStartAtPlayhead() }
            return true
        case "o":
            if isFirstPress { viewModel.setTrimEndAtPlayhead() }
            return true
        default:
            return false
        }
    }
}