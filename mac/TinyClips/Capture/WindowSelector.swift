import AppKit
import ScreenCaptureKit

// MARK: - Window Selector

@MainActor
class WindowSelector {
    private static var activeSelector: WindowSelectorController?
    private static let systemChromeBundleIdentifiers: Set<String> = [
        "com.apple.WindowManager",
        "com.apple.controlcenter",
        "com.apple.dock",
        "com.apple.notificationcenterui",
        "com.apple.systemuiserver",
    ]

    static var isSelecting: Bool {
        activeSelector != nil
    }

    static func selectWindow() async -> SCWindow? {
        let windows: [SCWindow]
        do {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            let includeTinyClips = CaptureSettings.shared.includeTinyClipsInCapture
            windows = content.windows.filter { w in
                guard let bundleIdentifier = w.owningApplication?.bundleIdentifier else { return false }
                let isTinyClipsWindow = bundleIdentifier == Bundle.main.bundleIdentifier
                let isSystemChrome = Self.systemChromeBundleIdentifiers.contains(bundleIdentifier)
                return w.frame.width >= 50 && w.frame.height >= 50
                    && w.windowLayer == 0
                    && (includeTinyClips || !isTinyClipsWindow)
                    && !isSystemChrome
                    && w.isOnScreen
            }
        } catch {
            return nil
        }

        return await withCheckedContinuation { continuation in
            let controller = WindowSelectorController(windows: windows) { window in
                Self.activeSelector = nil
                continuation.resume(returning: window)
            }
            Self.activeSelector = controller
            controller.show()
        }
    }
}

// MARK: - Controller

@MainActor
private class WindowSelectorController {
    private var overlayWindows: [(window: NSWindow, screen: NSScreen)] = []
    private let scWindows: [SCWindow]
    private let completion: (SCWindow?) -> Void
    private var hoveredWindow: SCWindow?
    private let primaryScreenHeight: CGFloat
    private var localMonitor: Any?
    private var globalMonitor: Any?

    init(windows: [SCWindow], completion: @escaping (SCWindow?) -> Void) {
        self.scWindows = windows
        self.completion = completion
        self.primaryScreenHeight = CaptureCoordinateMath.primaryDisplayHeight(
            forDisplayFrames: NSScreen.screens.map(\.frame)
        )
    }

    func show() {
        for screen in NSScreen.screens {
            // The content rect is relative to the lower-left corner of `screen`, so passing the
            // global screen frame double-offsets overlays on secondary displays and leaves those
            // displays uncovered, making their windows impossible to hover or click.
            let window = WindowOverlayWindow(
                contentRect: NSRect(origin: .zero, size: screen.frame.size),
                styleMask: .borderless,
                backing: .buffered,
                defer: false,
                screen: screen
            )
            window.level = .screenSaver
            window.isOpaque = false
            window.backgroundColor = .clear
            window.hasShadow = false
            window.ignoresMouseEvents = false
            window.acceptsMouseMovedEvents = true
            window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]

            let view = WindowSelectionView(frame: NSRect(origin: .zero, size: screen.frame.size))
            view.onMouseMoved = { [weak self] in
                self?.updateHoveredWindow(at: NSEvent.mouseLocation)
            }
            view.onMouseDown = { [weak self] in
                self?.updateHoveredWindow(at: NSEvent.mouseLocation)
                self?.selectHoveredWindow()
            }
            view.onCancel = { [weak self] in
                self?.finish(with: nil)
            }
            window.contentView = view
            window.makeKeyAndOrderFront(nil)
            window.makeFirstResponder(view)
            overlayWindows.append((window: window, screen: screen))
        }

        // Show highlight for window under cursor at launch
        updateHoveredWindow(at: NSEvent.mouseLocation)

        localMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            if event.keyCode == 53 {
                self?.finish(with: nil)
                return nil
            }
            return event
        }
        globalMonitor = NSEvent.addGlobalMonitorForEvents(matching: .keyDown) { [weak self] event in
            if event.keyCode == 53 {
                self?.finish(with: nil)
            }
        }

        NSApp.activate()
        overlayWindows.first?.window.makeKey()

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) { [weak self] in
            NSApp.activate()
            self?.overlayWindows.first?.window.makeKeyAndOrderFront(nil)
        }
    }

    private func updateHoveredWindow(at globalPoint: NSPoint) {
        // Convert AppKit (Y-up) to SC coordinates (Y-down from top of primary screen)
        let scPoint = CGPoint(x: globalPoint.x, y: primaryScreenHeight - globalPoint.y)

        // Find topmost window (first in the array = frontmost) containing the point
        let newHovered = scWindows.first { w in w.frame.contains(scPoint) }

        if newHovered?.windowID != hoveredWindow?.windowID {
            hoveredWindow = newHovered
            updateHighlights()
        }
    }

    private func updateHighlights() {
        for (overlayWindow, screen) in overlayWindows {
            guard let view = overlayWindow.contentView as? WindowSelectionView else { continue }
            if let hovered = hoveredWindow {
                let appKitFrame = scFrameToAppKit(hovered.frame)
                let screenFrame = screen.frame
                let intersection = screenFrame.intersection(appKitFrame)
                if intersection.isNull || intersection.isEmpty {
                    view.highlightFrame = nil
                } else {
                    view.highlightFrame = CGRect(
                        x: intersection.origin.x - screenFrame.origin.x,
                        y: intersection.origin.y - screenFrame.origin.y,
                        width: intersection.width,
                        height: intersection.height
                    )
                }
            } else {
                view.highlightFrame = nil
            }
            view.needsDisplay = true
        }
    }

    private func selectHoveredWindow() {
        finish(with: hoveredWindow)
    }

    private func finish(with window: SCWindow?) {
        if let monitor = localMonitor {
            NSEvent.removeMonitor(monitor)
            localMonitor = nil
        }
        if let monitor = globalMonitor {
            NSEvent.removeMonitor(monitor)
            globalMonitor = nil
        }
        for (w, _) in overlayWindows {
            w.orderOut(nil)
        }
        overlayWindows.removeAll()
        completion(window)
    }

    /// Converts a rect from SC coordinates (origin top-left, Y-down) to AppKit coordinates (origin bottom-left, Y-up).
    private func scFrameToAppKit(_ scFrame: CGRect) -> CGRect {
        CGRect(
            x: scFrame.origin.x,
            y: primaryScreenHeight - scFrame.maxY,
            width: scFrame.width,
            height: scFrame.height
        )
    }
}

// MARK: - Selection View

private class WindowSelectionView: NSView {
    var onMouseMoved: (() -> Void)?
    var onMouseDown: (() -> Void)?
    var onCancel: (() -> Void)?

    var highlightFrame: NSRect? {
        didSet { needsDisplay = true }
    }

    override var acceptsFirstResponder: Bool { true }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: WindowPickerCursor.camera)
    }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.black.withAlphaComponent(0.3).setFill()
        bounds.fill()

        if let frame = highlightFrame {
            let highlightPath = NSBezierPath(
                roundedRect: frame.insetBy(dx: 2, dy: 2),
                xRadius: 16,
                yRadius: 16
            )

            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current?.compositingOperation = .copy
            NSColor.clear.setFill()
            highlightPath.fill()
            NSGraphicsContext.restoreGraphicsState()

            NSGraphicsContext.saveGraphicsState()
            let shadow = NSShadow()
            shadow.shadowColor = NSColor.white.withAlphaComponent(0.55)
            shadow.shadowBlurRadius = 14
            shadow.shadowOffset = .zero
            shadow.set()

            NSColor.white.setStroke()
            highlightPath.lineWidth = 4
            highlightPath.stroke()
            NSGraphicsContext.restoreGraphicsState()

            NSColor.white.withAlphaComponent(0.8).setStroke()
            highlightPath.lineWidth = 1
            highlightPath.stroke()
        }
    }

    override func mouseMoved(with event: NSEvent) {
        onMouseMoved?()
    }

    override func mouseDown(with event: NSEvent) {
        onMouseDown?()
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 { // Escape
            onCancel?()
        }
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach { removeTrackingArea($0) }
        addTrackingArea(NSTrackingArea(
            rect: bounds,
            options: [.mouseMoved, .activeAlways],
            owner: self,
            userInfo: nil
        ))
    }
}

// MARK: - Overlay Window

private class WindowOverlayWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

// MARK: - Picker Cursor

/// Builds a camera-styled cursor for the window picker overlay so the pointer
/// signals that a click captures the highlighted window.
enum WindowPickerCursor {
    static let camera: NSCursor = makeCameraCursor()

    private static func makeCameraCursor() -> NSCursor {
        let config = NSImage.SymbolConfiguration(pointSize: 22, weight: .regular)
        guard let symbol = NSImage(systemSymbolName: "camera.fill", accessibilityDescription: "Capture window")?
            .withSymbolConfiguration(config) else {
            return .crosshair
        }

        let tinted = tint(symbol, with: .white)
        let padding: CGFloat = 4
        let canvasSize = NSSize(
            width: tinted.size.width + padding * 2,
            height: tinted.size.height + padding * 2
        )

        let cursorImage = NSImage(size: canvasSize)
        cursorImage.lockFocus()

        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.75)
        shadow.shadowBlurRadius = 2
        shadow.shadowOffset = .zero
        shadow.set()

        tinted.draw(
            in: NSRect(x: padding, y: padding, width: tinted.size.width, height: tinted.size.height)
        )
        cursorImage.unlockFocus()

        let hotSpot = NSPoint(x: canvasSize.width / 2, y: canvasSize.height / 2)
        return NSCursor(image: cursorImage, hotSpot: hotSpot)
    }

    private static func tint(_ image: NSImage, with color: NSColor) -> NSImage {
        let result = NSImage(size: image.size)
        result.lockFocus()
        color.set()
        let rect = NSRect(origin: .zero, size: image.size)
        image.draw(in: rect)
        rect.fill(using: .sourceAtop)
        result.unlockFocus()
        return result
    }
}
