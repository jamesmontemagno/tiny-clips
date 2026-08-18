import AppKit

@MainActor
class RegionSelector {
    private static var activeSelector: RegionSelectorController?

    static var isSelecting: Bool {
        activeSelector != nil
    }

    static func selectRegion() async -> CaptureRegion? {
        return await selectRegion(on: nil)
    }

    static func selectRegion(on screen: NSScreen?) async -> CaptureRegion? {
        activeSelector?.cancel()

        return await withCheckedContinuation { continuation in
            let selector = RegionSelectorController(screen: screen, completion: { region in
                Self.activeSelector = nil
                continuation.resume(returning: region)
            })
            Self.activeSelector = selector
            selector.show()
        }
    }
}

@MainActor
private class RegionSelectorController {
    private var windows: [NSWindow] = []
    private let completion: (CaptureRegion?) -> Void
    private var localMonitor: Any?
    private var globalMonitor: Any?
    private let targetScreen: NSScreen?
    private var didFinish = false

    init(screen: NSScreen? = nil, completion: @escaping (CaptureRegion?) -> Void) {
        self.targetScreen = screen
        self.completion = completion
    }

    func show() {
        let screens = targetScreen.map { [$0] } ?? NSScreen.screens
        for screen in screens {
            let window = RegionOverlayWindow(
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

            let view = RegionSelectionView(frame: NSRect(origin: .zero, size: screen.frame.size))
            view.onComplete = { [weak self] region in
                self?.finish(with: region)
            }
            view.onCancel = { [weak self] in
                self?.finish(with: nil)
            }
            window.contentView = view
            window.makeKeyAndOrderFront(nil)
            window.makeFirstResponder(view)
            windows.append(window)
        }

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
        windows.first?.makeKey()

        // Secondary screens may need a delayed activation to properly receive focus
        if targetScreen != nil {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) { [weak self] in
                NSApp.activate()
                self?.windows.first?.makeKeyAndOrderFront(nil)
            }
        }
    }

    func cancel() {
        finish(with: nil)
    }

    private func finish(with region: CaptureRegion?) {
        guard !didFinish else { return }
        didFinish = true

        if let monitor = localMonitor {
            NSEvent.removeMonitor(monitor)
            localMonitor = nil
        }
        if let monitor = globalMonitor {
            NSEvent.removeMonitor(monitor)
            globalMonitor = nil
        }
        for window in windows {
            window.orderOut(nil)
        }
        windows.removeAll()
        completion(region)
    }
}

private class RegionSelectionView: NSView {
    var onComplete: ((CaptureRegion) -> Void)?
    var onCancel: (() -> Void)?

    private var startPoint: NSPoint?
    private var currentPoint: NSPoint?

    override var acceptsFirstResponder: Bool { true }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: .crosshair)
    }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.black.withAlphaComponent(0.3).setFill()
        bounds.fill()

        if let start = startPoint, let current = currentPoint {
            let selectionRect = makeRect(from: start, to: current)

            // Clear the selection area
            NSColor.clear.setFill()
            selectionRect.fill(using: .copy)

            // Draw border
            NSColor.white.setStroke()
            let path = NSBezierPath(rect: selectionRect)
            path.lineWidth = 1.5
            path.stroke()

            // Draw dashed inner border
            NSColor.white.withAlphaComponent(0.5).setStroke()
            let dashedPath = NSBezierPath(rect: selectionRect.insetBy(dx: 1, dy: 1))
            dashedPath.lineWidth = 0.5
            dashedPath.setLineDash([4, 4], count: 2, phase: 0)
            dashedPath.stroke()

            // Draw dimensions label
            drawDimensionsLabel(for: selectionRect)
        }
    }

    private func drawDimensionsLabel(for rect: NSRect) {
        guard rect.width >= 1, rect.height >= 1 else { return }

        let scale = window?.screen?.backingScaleFactor ?? 1
        let aligned = CaptureCoordinateMath.pixelAlignedRect(rect, scaleFactor: scale)
        let pixelWidth = max(1, Int((aligned.width * scale).rounded()))
        let pixelHeight = max(1, Int((aligned.height * scale).rounded()))
        let width = Int(aligned.width.rounded())
        let height = Int(aligned.height.rounded())
        let text: String
        if pixelWidth != width || pixelHeight != height {
            text = "\(width) × \(height) pt · \(pixelWidth) × \(pixelHeight) px"
        } else {
            text = "\(width) × \(height)"
        }
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedSystemFont(ofSize: 12, weight: .medium),
            .foregroundColor: NSColor.white,
        ]
        let size = (text as NSString).size(withAttributes: attrs)
        let labelRect = NSRect(
            x: rect.midX - size.width / 2 - 6,
            y: rect.maxY + 8,
            width: size.width + 12,
            height: size.height + 6
        )

        NSColor.black.withAlphaComponent(0.7).setFill()
        let pill = NSBezierPath(roundedRect: labelRect, xRadius: 4, yRadius: 4)
        pill.fill()

        let textPoint = NSPoint(x: labelRect.minX + 6, y: labelRect.minY + 3)
        (text as NSString).draw(at: textPoint, withAttributes: attrs)
    }

    override func mouseDown(with event: NSEvent) {
        startPoint = clampedPoint(convert(event.locationInWindow, from: nil))
        currentPoint = startPoint
        needsDisplay = true
    }

    override func mouseDragged(with event: NSEvent) {
        currentPoint = clampedPoint(convert(event.locationInWindow, from: nil))
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        guard let start = startPoint else { return }
        let end = clampedPoint(convert(event.locationInWindow, from: nil))
        let selectionRect = makeRect(from: start, to: end)

        guard selectionRect.width >= 10, selectionRect.height >= 10 else {
            startPoint = nil
            currentPoint = nil
            needsDisplay = true
            return
        }

        guard let window = self.window, let screen = window.screen else { return }

        // Convert view coordinates → window coordinates → screen coordinates
        let windowRect = convert(selectionRect, to: nil)
        let screenRect = window.convertToScreen(windowRect)

        // Convert to display-local coordinates (origin at top-left, Y-down)
        let displayID = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as! CGDirectDisplayID
        let localX = screenRect.minX - screen.frame.minX
        let localY = screen.frame.maxY - screenRect.maxY
        let sourceRect = CGRect(x: localX, y: localY, width: screenRect.width, height: screenRect.height)

        let region = CaptureRegion(
            sourceRect: sourceRect,
            displayID: displayID,
            scaleFactor: screen.backingScaleFactor
        ).pixelAligned()
        onComplete?(region)
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 {
            onCancel?()
        }
    }

    private func makeRect(from p1: NSPoint, to p2: NSPoint) -> NSRect {
        NSRect(
            x: min(p1.x, p2.x),
            y: min(p1.y, p2.y),
            width: abs(p2.x - p1.x),
            height: abs(p2.y - p1.y)
        )
    }

    private func clampedPoint(_ point: NSPoint) -> NSPoint {
        NSPoint(
            x: min(max(point.x, bounds.minX), bounds.maxX),
            y: min(max(point.y, bounds.minY), bounds.maxY)
        )
    }
}

// MARK: - Overlay Window

/// Borderless windows return false for canBecomeKey by default,
/// which prevents mouse event delivery on secondary screens.
private class RegionOverlayWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}
