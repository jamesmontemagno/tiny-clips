import AppKit
import SwiftUI

final class TeleprompterPanel: NSPanel {
    private static let positionXKey = "teleprompterPanelX"
    private static let positionYKey = "teleprompterPanelY"

    private var didPersistPosition = false
    private let scrollState: TeleprompterScrollState
    private let panelSize: NSSize

    init(
        transcript: String,
        scrollSpeed: Double,
        fontSize: TeleprompterDisplaySize,
        panelHeight: TeleprompterDisplaySize
    ) {
        let panelSize = NSSize(width: 600, height: panelHeight.panelHeight)
        let scrollState = TeleprompterScrollState(scrollSpeed: scrollSpeed)
        self.panelSize = panelSize
        self.scrollState = scrollState
        super.init(
            contentRect: NSRect(origin: .zero, size: panelSize),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        isReleasedWhenClosed = false
        level = .floating
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        sharingType = .none
        isMovableByWindowBackground = true

        let hostingView = NSHostingView(
            rootView: TeleprompterView(
                state: scrollState,
                transcript: transcript,
                panelSize: panelSize,
                viewportHeight: panelHeight.viewportHeight,
                fontSize: fontSize.fontSize
            )
        )
        hostingView.setAccessibilityElement(true)
        hostingView.setAccessibilityRole(.staticText)
        hostingView.setAccessibilityLabel("Teleprompter")
        hostingView.setAccessibilityValue(transcript)
        contentView = hostingView
    }

    func prepareHidden(relativeTo region: CaptureRegion?) {
        restorePosition(for: region)
        alphaValue = 0
        ignoresMouseEvents = true
        orderFront(nil)
    }

    func reveal() {
        alphaValue = 1
        ignoresMouseEvents = false
    }

    func pause() {
        scrollState.pause()
    }

    func resume() {
        scrollState.resume()
    }

    override func close() {
        scrollState.stop()
        persistPosition()
        super.close()
    }

    private func restorePosition(for region: CaptureRegion?) {
        let defaults = UserDefaults.standard
        if let x = defaults.object(forKey: Self.positionXKey) as? Double,
           let y = defaults.object(forKey: Self.positionYKey) as? Double {
            let origin = NSPoint(x: x, y: y)
            if Self.isUsableOrigin(origin, panelSize: frame.size) {
                setFrameOrigin(origin)
                return
            }
        }

        guard let screen = Self.screen(for: region) ?? NSScreen.main else { return }
        let frame = screen.frame
        setFrameOrigin(NSPoint(
            x: frame.midX - panelSize.width / 2,
            y: frame.maxY - panelSize.height - 120
        ))
    }

    private func persistPosition() {
        guard !didPersistPosition else { return }
        didPersistPosition = true
        let defaults = UserDefaults.standard
        defaults.set(Double(frame.origin.x), forKey: Self.positionXKey)
        defaults.set(Double(frame.origin.y), forKey: Self.positionYKey)
    }

    private static func isUsableOrigin(_ origin: NSPoint, panelSize: NSSize) -> Bool {
        let panelRect = NSRect(origin: origin, size: panelSize)
        return NSScreen.screens.contains { screen in
            let visibleIntersection = screen.visibleFrame.intersection(panelRect)
            return visibleIntersection.width >= 80 && visibleIntersection.height >= 40
        }
    }

    private static func screen(for region: CaptureRegion?) -> NSScreen? {
        guard let region else { return nil }
        return NSScreen.screens.first(where: {
            ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID) == region.displayID
        })
    }
}

private final class TeleprompterScrollState: ObservableObject {
    @Published var scrollOffset: CGFloat = 0
    @Published var contentHeight: CGFloat = 0 {
        didSet {
            if !isPaused {
                startTimer()
            }
        }
    }

    let scrollSpeed: Double
    private var timer: Timer?
    private var viewportHeight: CGFloat?
    private var isPaused = true

    init(scrollSpeed: Double) {
        self.scrollSpeed = min(max(scrollSpeed, 0), 100)
    }

    func start(viewportHeight: CGFloat) {
        self.viewportHeight = viewportHeight
        if !isPaused {
            startTimer()
        }
    }

    func pause() {
        isPaused = true
        stopTimer()
    }

    func resume() {
        isPaused = false
        startTimer()
    }

    func stop() {
        isPaused = true
        viewportHeight = nil
        stopTimer()
    }

    private func startTimer() {
        guard timer == nil, scrollSpeed > 0, let viewportHeight else { return }
        let maxOffset = max(0, contentHeight - viewportHeight)
        guard scrollOffset < maxOffset else { return }
        let interval: TimeInterval = 1.0 / 60.0
        timer = Timer.scheduledTimer(withTimeInterval: interval, repeats: true) { [weak self] _ in
            guard let self else { return }
            let maxOffset = max(0, self.contentHeight - viewportHeight)
            guard maxOffset > 0 else { return }
            let next = self.scrollOffset + self.scrollSpeed * interval
            if next >= maxOffset {
                self.scrollOffset = maxOffset
                self.stopTimer()
            } else {
                self.scrollOffset = next
            }
        }
        if let timer {
            RunLoop.main.add(timer, forMode: .common)
        }
    }

    private func stopTimer() {
        timer?.invalidate()
        timer = nil
    }
}

private struct TeleprompterView: View {
    @ObservedObject var state: TeleprompterScrollState
    let transcript: String
    let panelSize: NSSize
    let viewportHeight: CGFloat
    let fontSize: CGFloat

    var body: some View {
        VStack(spacing: 0) {
            Text(transcript)
                .font(.system(size: fontSize, weight: .medium))
                .foregroundStyle(.white)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.horizontal, 20)
                .padding(.vertical, 12)
                .background {
                    GeometryReader { proxy in
                        Color.clear.onAppear { state.contentHeight = proxy.size.height }
                    }
                }
        }
        .frame(width: panelSize.width, alignment: .top)
        .fixedSize(horizontal: false, vertical: true)
        .offset(y: -state.scrollOffset)
        .frame(width: panelSize.width, height: viewportHeight, alignment: .top)
        .clipped()
        .background {
            RoundedRectangle(cornerRadius: 12)
                .fill(Color.black.opacity(0.7))
        }
        .onAppear { state.start(viewportHeight: viewportHeight) }
        .onDisappear { state.stop() }
    }
}
