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
            let candidates = content.windows.filter { w in
                guard let bundleIdentifier = w.owningApplication?.bundleIdentifier else { return false }
                let isTinyClipsWindow = bundleIdentifier == Bundle.main.bundleIdentifier
                let isSystemChrome = Self.systemChromeBundleIdentifiers.contains(bundleIdentifier)
                return w.frame.width >= 50 && w.frame.height >= 50
                    && w.windowLayer == 0
                    && (includeTinyClips || !isTinyClipsWindow)
                    && !isSystemChrome
                    && w.isOnScreen
            }
            // ScreenCaptureKit does not document its window order, so hit-testing its list can
            // pick a window that is hidden behind another one. Window Server's list is
            // front-to-back; use it to order the candidates and drop fully transparent windows.
            windows = WindowPickerHitTesting.frontToBack(
                candidates,
                windowID: \.windowID,
                zOrder: WindowPickerHitTesting.currentZOrder()
            )
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

// MARK: - Hit Testing

/// Pure z-order and hit-testing helpers for the window picker, kept free of AppKit state so they
/// can be unit tested.
enum WindowPickerHitTesting {
    struct WindowListEntry: Equatable {
        let windowID: CGWindowID
        let layer: Int
        let alpha: Double
    }

    /// Windows at or below this opacity are invisible helpers that must not be highlighted.
    static let minimumVisibleAlpha = 0.01

    /// Parses `CGWindowListCopyWindowInfo` dictionaries, preserving their front-to-back order.
    static func entries(fromWindowInfo info: [[String: Any]]) -> [WindowListEntry] {
        info.compactMap { window in
            guard let number = window[kCGWindowNumber as String] as? NSNumber else { return nil }
            let layer = (window[kCGWindowLayer as String] as? NSNumber)?.intValue ?? 0
            let alpha = (window[kCGWindowAlpha as String] as? NSNumber)?.doubleValue ?? 1
            return WindowListEntry(windowID: CGWindowID(number.uint32Value), layer: layer, alpha: alpha)
        }
    }

    /// Maps each visible window to its front-to-back rank (0 is frontmost).
    static func zOrder(from entries: [WindowListEntry]) -> [CGWindowID: Int] {
        var ranks: [CGWindowID: Int] = [:]
        for (rank, entry) in entries.enumerated() where entry.alpha > minimumVisibleAlpha {
            if ranks[entry.windowID] == nil {
                ranks[entry.windowID] = rank
            }
        }
        return ranks
    }

    /// Reads the current front-to-back order from Window Server. Empty when unavailable.
    static func currentZOrder() -> [CGWindowID: Int] {
        guard let info = CGWindowListCopyWindowInfo(
            [.optionOnScreenOnly, .excludeDesktopElements],
            kCGNullWindowID
        ) as? [[String: Any]] else {
            return [:]
        }
        return zOrder(from: entries(fromWindowInfo: info))
    }

    /// Sorts items front-to-back using `zOrder`, dropping items Window Server does not list as
    /// visible. When no z-order is available the original order is kept as a fallback.
    static func frontToBack<Item>(
        _ items: [Item],
        windowID: (Item) -> CGWindowID,
        zOrder: [CGWindowID: Int]
    ) -> [Item] {
        guard !zOrder.isEmpty else { return items }
        return items.enumerated()
            .compactMap { offset, item -> (rank: Int, offset: Int, item: Item)? in
                guard let rank = zOrder[windowID(item)] else { return nil }
                return (rank, offset, item)
            }
            .sorted { ($0.rank, $0.offset) < ($1.rank, $1.offset) }
            .map { $0.item }
    }

    /// Index of the topmost frame containing `point`; `frames` must be front-to-back.
    static func topmostIndex(in frames: [CGRect], containing point: CGPoint) -> Int? {
        frames.firstIndex { $0.contains(point) }
    }

    /// Next (or previous) index for keyboard cycling, wrapping at the ends. With nothing
    /// highlighted, forward starts at the frontmost window and backward at the backmost.
    static func cycledIndex(current: Int?, count: Int, forward: Bool) -> Int? {
        guard count > 0 else { return nil }
        guard let current, current >= 0, current < count else {
            return forward ? 0 : count - 1
        }
        return (current + (forward ? 1 : -1) + count) % count
    }

    /// The single screen that shows a window's label: the one containing its centre, otherwise
    /// the one with the largest overlap.
    static func labelScreenIndex(for frame: CGRect, screenFrames: [CGRect]) -> Int? {
        let center = CGPoint(x: frame.midX, y: frame.midY)
        if let index = screenFrames.firstIndex(where: { $0.contains(center) }) {
            return index
        }

        var best: (index: Int, area: CGFloat)?
        for (index, screen) in screenFrames.enumerated() {
            let overlap = screen.intersection(frame)
            guard !overlap.isNull, !overlap.isEmpty else { continue }
            let area = overlap.width * overlap.height
            if area > (best?.area ?? 0) {
                best = (index, area)
            }
        }
        return best?.index
    }

    /// Formats a size in pixels for the picker label, e.g. "1920 × 1080".
    static func pixelSizeText(for frame: CGRect, scale: CGFloat) -> String {
        "\(Int((frame.width * scale).rounded())) × \(Int((frame.height * scale).rounded()))"
    }
}

// MARK: - Controller

@MainActor
private class WindowSelectorController {
    private static let instructions = "Choose a window to capture. Press Tab to move between windows, Return to capture, or Escape to cancel."

    private var overlayWindows: [(window: NSWindow, screen: NSScreen)] = []
    private let scWindows: [SCWindow]
    private let completion: (SCWindow?) -> Void
    private var hoveredIndex: Int?
    private let primaryScreenHeight: CGFloat
    private var localMonitor: Any?
    private var globalMonitor: Any?
    private var iconCache: [pid_t: NSImage] = [:]
    private var scaleCache: [CGWindowID: CGFloat] = [:]
    private var isFinished = false

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
            window.setAccessibilityLabel("Choose a window to capture")

            let view = WindowSelectionView(frame: NSRect(origin: .zero, size: screen.frame.size))
            view.hintText = scWindows.isEmpty
                ? "No windows to capture · Esc to cancel"
                : "Click a window to capture · Tab to cycle · Esc to cancel"
            view.onMouseMoved = { [weak self] in
                self?.updateHoveredWindow(at: NSEvent.mouseLocation)
            }
            view.onMouseDown = { [weak self] in
                self?.selectWindow(at: NSEvent.mouseLocation)
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
            guard let self else { return event }
            return self.handleKeyDown(event) ? nil : event
        }
        globalMonitor = NSEvent.addGlobalMonitorForEvents(matching: .keyDown) { [weak self] event in
            if event.keyCode == KeyCode.escape {
                self?.finish(with: nil)
            }
        }

        NSApp.activate()
        overlayWindows.first?.window.makeKey()

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) { [weak self] in
            guard let self, !self.isFinished else { return }
            NSApp.activate()
            self.overlayWindows.first?.window.makeKeyAndOrderFront(nil)
            AccessibilityAnnouncementService.shared.announce(WindowSelectorController.instructions, priority: .high)
        }
    }

    private enum KeyCode {
        static let tab: UInt16 = 48
        static let returnKey: UInt16 = 36
        static let keypadEnter: UInt16 = 76
        static let escape: UInt16 = 53
    }

    /// Returns true when the key was handled by the picker.
    private func handleKeyDown(_ event: NSEvent) -> Bool {
        switch event.keyCode {
        case KeyCode.escape:
            finish(with: nil)
        case KeyCode.tab:
            let forward = !event.modifierFlags.contains(.shift)
            guard let index = WindowPickerHitTesting.cycledIndex(
                current: hoveredIndex,
                count: scWindows.count,
                forward: forward
            ) else { return true }
            setHoveredIndex(index)
            AccessibilityAnnouncementService.shared.announce(description(for: scWindows[index]), priority: .medium)
        case KeyCode.returnKey, KeyCode.keypadEnter:
            if let hoveredIndex {
                finish(with: scWindows[hoveredIndex])
            }
        default:
            return false
        }
        return true
    }

    private func screenCapturePoint(for globalPoint: NSPoint) -> CGPoint {
        // Convert AppKit (Y-up) to SC coordinates (Y-down from top of primary screen)
        CGPoint(x: globalPoint.x, y: primaryScreenHeight - globalPoint.y)
    }

    private func windowIndex(at globalPoint: NSPoint) -> Int? {
        WindowPickerHitTesting.topmostIndex(
            in: scWindows.map(\.frame),
            containing: screenCapturePoint(for: globalPoint)
        )
    }

    private func updateHoveredWindow(at globalPoint: NSPoint) {
        setHoveredIndex(windowIndex(at: globalPoint))
    }

    /// Clicking empty space is ignored so a near-miss does not throw away the capture; Escape,
    /// right-click or the menu cancel instead.
    private func selectWindow(at globalPoint: NSPoint) {
        guard let index = windowIndex(at: globalPoint) else { return }
        finish(with: scWindows[index])
    }

    private func setHoveredIndex(_ index: Int?) {
        guard index != hoveredIndex else { return }
        hoveredIndex = index
        updateHighlights()
    }

    private func updateHighlights() {
        let hovered = hoveredIndex.map { scWindows[$0] }
        let appKitFrame = hovered.map { scFrameToAppKit($0.frame) }
        let labelScreen = appKitFrame.flatMap {
            WindowPickerHitTesting.labelScreenIndex(for: $0, screenFrames: overlayWindows.map { $0.screen.frame })
        }

        for (offset, overlay) in overlayWindows.enumerated() {
            guard let view = overlay.window.contentView as? WindowSelectionView else { continue }
            let screenFrame = overlay.screen.frame
            guard let hovered, let appKitFrame else {
                view.highlightFrame = nil
                view.label = nil
                continue
            }

            let intersection = screenFrame.intersection(appKitFrame)
            if intersection.isNull || intersection.isEmpty {
                view.highlightFrame = nil
                view.label = nil
            } else {
                view.highlightFrame = CGRect(
                    x: intersection.origin.x - screenFrame.origin.x,
                    y: intersection.origin.y - screenFrame.origin.y,
                    width: intersection.width,
                    height: intersection.height
                )
                view.label = offset == labelScreen
                    ? label(for: hovered)
                    : nil
            }
        }
    }

    private func label(for window: SCWindow) -> WindowPickerLabel {
        let appName = window.owningApplication?.applicationName ?? ""
        let title = window.title.flatMap { $0.isEmpty ? nil : $0 } ?? appName
        let size = WindowPickerHitTesting.pixelSizeText(for: window.frame, scale: pixelScale(for: window))
        let detail = appName.isEmpty || appName == title ? size : "\(appName) · \(size)"
        return WindowPickerLabel(icon: icon(for: window), title: title, detail: detail)
    }

    /// The scale the window will actually be captured at, so the label matches the saved image
    /// even when the label sits on a different display than most of the window.
    private func pixelScale(for window: SCWindow) -> CGFloat {
        if let cached = scaleCache[window.windowID] {
            return cached
        }
        let scale = ScreenshotCapture.pixelScale(for: window)
        scaleCache[window.windowID] = scale
        return scale
    }

    private func description(for window: SCWindow) -> String {
        let appName = window.owningApplication?.applicationName ?? ""
        let title = window.title.flatMap { $0.isEmpty ? nil : $0 } ?? appName
        return appName.isEmpty || appName == title ? title : "\(title), \(appName)"
    }

    private func icon(for window: SCWindow) -> NSImage? {
        guard let processID = window.owningApplication?.processID else { return nil }
        if let cached = iconCache[processID] {
            return cached
        }
        let icon = NSRunningApplication(processIdentifier: processID)?.icon
        iconCache[processID] = icon
        return icon
    }

    private func finish(with window: SCWindow?) {
        guard !isFinished else { return }
        isFinished = true
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

private struct WindowPickerLabel {
    let icon: NSImage?
    let title: String
    let detail: String
}

private class WindowSelectionView: NSView {
    private static let edgeMargin: CGFloat = 12

    var onMouseMoved: (() -> Void)?
    var onMouseDown: (() -> Void)?
    var onCancel: (() -> Void)?

    var highlightFrame: NSRect? {
        didSet { needsDisplay = true }
    }

    var label: WindowPickerLabel? {
        didSet { needsDisplay = true }
    }

    var hintText = "" {
        didSet { needsDisplay = true }
    }

    override var acceptsFirstResponder: Bool { true }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: WindowPickerCursor.camera)
    }

    override func isAccessibilityElement() -> Bool { true }

    override func accessibilityRole() -> NSAccessibility.Role? { .group }

    override func accessibilityLabel() -> String? {
        if let label {
            return "\(label.title), \(label.detail)"
        }
        return "Choose a window to capture"
    }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.black.withAlphaComponent(0.35).setFill()
        bounds.fill()

        if let frame = highlightFrame {
            let highlightPath = NSBezierPath(
                roundedRect: frame.insetBy(dx: 1.5, dy: 1.5),
                xRadius: 12,
                yRadius: 12
            )

            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current?.compositingOperation = .copy
            NSColor.clear.setFill()
            highlightPath.fill()
            NSGraphicsContext.restoreGraphicsState()

            let accent = NSColor.controlAccentColor
            accent.withAlphaComponent(0.12).setFill()
            highlightPath.fill()

            NSGraphicsContext.saveGraphicsState()
            let glow = NSShadow()
            glow.shadowColor = accent.withAlphaComponent(0.7)
            glow.shadowBlurRadius = 16
            glow.shadowOffset = .zero
            glow.set()
            accent.setStroke()
            highlightPath.lineWidth = 3
            highlightPath.stroke()
            NSGraphicsContext.restoreGraphicsState()

            if let label {
                drawLabel(label, centeredIn: frame)
            }
        }

        drawHint()
    }

    private func drawLabel(_ label: WindowPickerLabel, centeredIn frame: NSRect) {
        let paragraph = NSMutableParagraphStyle()
        paragraph.lineBreakMode = .byTruncatingTail
        let title = NSAttributedString(string: label.title, attributes: [
            .font: NSFont.systemFont(ofSize: 14, weight: .semibold),
            .foregroundColor: NSColor.white,
            .paragraphStyle: paragraph,
        ])
        let detail = NSAttributedString(string: label.detail, attributes: [
            .font: NSFont.systemFont(ofSize: 12),
            .foregroundColor: NSColor.white.withAlphaComponent(0.75),
            .paragraphStyle: paragraph,
        ])

        let iconSize: CGFloat = label.icon == nil ? 0 : 32
        let iconSpacing: CGFloat = label.icon == nil ? 0 : 10
        let horizontalPadding: CGFloat = 14
        let verticalPadding: CGFloat = 10
        let lineSpacing: CGFloat = 2
        let titleSize = title.size()
        let detailSize = detail.size()
        let textWidth = min(360, max(titleSize.width, detailSize.width)).rounded(.up)
        let textHeight = titleSize.height + lineSpacing + detailSize.height
        let size = NSSize(
            width: horizontalPadding * 2 + iconSize + iconSpacing + textWidth,
            height: verticalPadding * 2 + max(iconSize, textHeight)
        )

        let margin = Self.edgeMargin
        let origin = NSPoint(
            x: max(bounds.minX + margin, min(frame.midX - size.width / 2, bounds.maxX - size.width - margin)),
            y: max(bounds.minY + margin, min(frame.midY - size.height / 2, bounds.maxY - size.height - margin))
        )
        let rect = NSRect(origin: origin, size: size)

        let background = NSBezierPath(roundedRect: rect, xRadius: 10, yRadius: 10)
        NSColor(white: 0.1, alpha: 0.85).setFill()
        background.fill()
        NSColor.white.withAlphaComponent(0.15).setStroke()
        background.lineWidth = 1
        background.stroke()

        var textX = rect.minX + horizontalPadding
        if let icon = label.icon {
            icon.draw(in: NSRect(x: textX, y: rect.midY - iconSize / 2, width: iconSize, height: iconSize))
            textX += iconSize + iconSpacing
        }

        let textTop = rect.midY + textHeight / 2
        title.draw(
            with: NSRect(x: textX, y: textTop - titleSize.height, width: textWidth, height: titleSize.height),
            options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine]
        )
        detail.draw(
            with: NSRect(
                x: textX,
                y: textTop - titleSize.height - lineSpacing - detailSize.height,
                width: textWidth,
                height: detailSize.height
            ),
            options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine]
        )
    }

    private func drawHint() {
        guard !hintText.isEmpty else { return }
        let text = NSAttributedString(string: hintText, attributes: [
            .font: NSFont.systemFont(ofSize: 13, weight: .medium),
            .foregroundColor: NSColor.white,
        ])
        let textSize = text.size()
        let size = NSSize(width: textSize.width + 32, height: textSize.height + 16)
        let rect = NSRect(x: bounds.midX - size.width / 2, y: bounds.minY + 48, width: size.width, height: size.height)
        let pill = NSBezierPath(roundedRect: rect, xRadius: size.height / 2, yRadius: size.height / 2)
        NSColor(white: 0.1, alpha: 0.8).setFill()
        pill.fill()
        text.draw(at: NSPoint(x: rect.minX + 16, y: rect.minY + 8))
    }

    override func mouseMoved(with event: NSEvent) {
        onMouseMoved?()
    }

    override func mouseDown(with event: NSEvent) {
        onMouseDown?()
    }

    override func rightMouseDown(with event: NSEvent) {
        onCancel?()
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
