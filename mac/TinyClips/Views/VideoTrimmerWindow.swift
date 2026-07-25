import AppKit
import SwiftUI
import AVFoundation
import AVKit
import ImageIO
import Carbon.HIToolbox

final class TrimmerMenuActions {
    var saveFrame: (() -> Void)?
    var copyFrame: (() -> Void)?
    var saveTrimmed: (() -> Void)?
    var saveWithoutTrimming: (() -> Void)?
    var saveAllFrames: (() -> Void)?
    var togglePlayback: (() -> Void)?
    var previousFrame: (() -> Void)?
    var nextFrame: (() -> Void)?

    func handles(_ action: Selector?) -> Bool {
        switch action {
        case NSSelectorFromString("trimmerSaveFrame:"),
            NSSelectorFromString("trimmerCopyFrame:"),
            NSSelectorFromString("trimmerSaveTrimmed:"),
            NSSelectorFromString("trimmerSaveWithoutTrimming:"),
            NSSelectorFromString("trimmerSaveAllFrames:"),
            NSSelectorFromString("trimmerTogglePlayback:"),
            NSSelectorFromString("trimmerPreviousFrame:"),
            NSSelectorFromString("trimmerNextFrame:"):
            return true
        default:
            return false
        }
    }

    func canPerform(_ action: Selector?) -> Bool {
        switch action {
        case NSSelectorFromString("trimmerSaveFrame:"):
            return saveFrame != nil
        case NSSelectorFromString("trimmerCopyFrame:"):
            return copyFrame != nil
        case NSSelectorFromString("trimmerSaveTrimmed:"):
            return saveTrimmed != nil
        case NSSelectorFromString("trimmerSaveWithoutTrimming:"):
            return saveWithoutTrimming != nil
        case NSSelectorFromString("trimmerSaveAllFrames:"):
            return saveAllFrames != nil
        case NSSelectorFromString("trimmerTogglePlayback:"):
            return togglePlayback != nil
        case NSSelectorFromString("trimmerPreviousFrame:"):
            return previousFrame != nil
        case NSSelectorFromString("trimmerNextFrame:"):
            return nextFrame != nil
        default:
            return false
        }
    }

    func clear() {
        saveFrame = nil
        copyFrame = nil
        saveTrimmed = nil
        saveWithoutTrimming = nil
        saveAllFrames = nil
        togglePlayback = nil
        previousFrame = nil
        nextFrame = nil
    }
}

enum TrimmerMenuCommands {
    private static var isInstalled = false

    static func installIfNeeded() {
        guard !isInstalled,
              let mainMenu = NSApp.mainMenu else {
            return
        }

        let trimMenu = NSMenu(title: "Trim")
        trimMenu.addItem(menuItem("Save Frame", action: "trimmerSaveFrame:"))
        trimMenu.addItem(menuItem("Copy Frame", action: "trimmerCopyFrame:"))
        trimMenu.addItem(.separator())
        trimMenu.addItem(menuItem("Save Trimmed", action: "trimmerSaveTrimmed:"))
        trimMenu.addItem(menuItem("Save Without Trimming", action: "trimmerSaveWithoutTrimming:"))
        trimMenu.addItem(menuItem("Save All Frames", action: "trimmerSaveAllFrames:"))

        let playbackMenu = NSMenu(title: "Playback")
        playbackMenu.addItem(menuItem("Play/Pause Preview", action: "trimmerTogglePlayback:"))
        playbackMenu.addItem(menuItem("Previous Frame", action: "trimmerPreviousFrame:"))
        playbackMenu.addItem(menuItem("Next Frame", action: "trimmerNextFrame:"))

        let playbackItem = NSMenuItem(title: "Playback", action: nil, keyEquivalent: "")
        playbackItem.submenu = playbackMenu
        trimMenu.addItem(.separator())
        trimMenu.addItem(playbackItem)

        let trimItem = NSMenuItem(title: "Trim", action: nil, keyEquivalent: "")
        trimItem.submenu = trimMenu
        mainMenu.addItem(trimItem)
        isInstalled = true
    }

    private static func menuItem(_ title: String, action: String) -> NSMenuItem {
        NSMenuItem(title: title, action: NSSelectorFromString(action), keyEquivalent: "")
    }
}

class VideoTrimmerWindow: NSWindow, NSWindowDelegate {
    private var onComplete: ((URL?) -> Void)?
    private var didComplete = false
    private let menuActions = TrimmerMenuActions()

    convenience init(videoURL: URL, onComplete: @escaping (URL?) -> Void) {
        self.init(
            contentRect: NSRect(x: 0, y: 0, width: 700, height: 560),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        self.onComplete = onComplete
        self.title = "Trim Video"
        self.isReleasedWhenClosed = false
        self.delegate = self
        self.minSize = NSSize(width: 680, height: 540)
        self.center()
        TrimmerMenuCommands.installIfNeeded()

        let trimmerView = VideoTrimmerView(
            videoURL: videoURL,
            menuActions: menuActions,
            onDone: { [weak self] resultURL in
                self?.completeWith(resultURL)
            }
        )
        self.contentView = NSHostingView(rootView: trimmerView)
    }

    private func completeWith(_ url: URL?) {
        guard !didComplete, let callback = onComplete else { return }
        didComplete = true
        onComplete = nil
        callback(url)
        orderOut(nil)
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        completeWith(nil)
        return true
    }

    @objc func trimmerSaveFrame(_ sender: Any?) { menuActions.saveFrame?() }
    @objc func trimmerCopyFrame(_ sender: Any?) { menuActions.copyFrame?() }
    @objc func trimmerSaveTrimmed(_ sender: Any?) { menuActions.saveTrimmed?() }
    @objc func trimmerSaveWithoutTrimming(_ sender: Any?) { menuActions.saveWithoutTrimming?() }
    @objc func trimmerTogglePlayback(_ sender: Any?) { menuActions.togglePlayback?() }
    @objc func trimmerPreviousFrame(_ sender: Any?) { menuActions.previousFrame?() }
    @objc func trimmerNextFrame(_ sender: Any?) { menuActions.nextFrame?() }

    @objc func copy(_ sender: Any?) {
        menuActions.copyFrame?()
    }

    override func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        if menuItem.action == NSSelectorFromString("copy:") {
            return menuActions.copyFrame != nil
        }
        guard menuActions.handles(menuItem.action) else {
            return super.validateMenuItem(menuItem)
        }
        return menuActions.canPerform(menuItem.action)
    }
}

// MARK: - Trimmer View

private struct VideoTrimmerView: View {
    let videoURL: URL
    let menuActions: TrimmerMenuActions
    let onDone: (URL?) -> Void

    @StateObject private var viewModel: TrimmerViewModel
    @State private var keyMonitor: Any?
    @State private var trimmerWindow: NSWindow?

    init(videoURL: URL, menuActions: TrimmerMenuActions, onDone: @escaping (URL?) -> Void) {
        self.videoURL = videoURL
        self.menuActions = menuActions
        self.onDone = onDone
        _viewModel = StateObject(wrappedValue: TrimmerViewModel(url: videoURL))
    }

    var body: some View {
        VStack(spacing: 0) {
            // Video preview
            PlayerView(player: viewModel.player)
                .frame(minWidth: 400, minHeight: 260)
                .clipShape(RoundedRectangle(cornerRadius: 6))
                .padding([.top, .horizontal])
                .task { await viewModel.loadDuration() }

            HStack(spacing: 10) {
                Text(formatTime(viewModel.currentTime))
                    .monospacedDigit()
                    .frame(width: 64, alignment: .leading)

                Spacer(minLength: 8)

                Button(action: { viewModel.stepFrame(by: -1) }) {
                    Text("<")
                }
                .accessibilityLabel("Previous frame")
                .help("Move to the previous frame (Left Arrow).")

                Text("Frame \(viewModel.currentFrameNumber) of \(max(1, viewModel.totalFrameCount))")
                    .monospacedDigit()
                    .frame(minWidth: 140)
                    .multilineTextAlignment(.center)

                Button(action: { viewModel.stepFrame(by: 1) }) {
                    Text(">")
                }
                .accessibilityLabel("Next frame")
                .help("Move to the next frame (Right Arrow).")

                Spacer(minLength: 8)

                Text(formatTime(viewModel.duration))
                    .monospacedDigit()
                    .frame(width: 64, alignment: .trailing)
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            .padding(.horizontal, 20)
            .padding(.top, 6)
            .disabled(viewModel.duration <= 0)

            // Trim range control
            TrimRangeSlider(
                trimStart: $viewModel.trimStart,
                trimEnd: $viewModel.trimEnd,
                currentTime: viewModel.currentTime,
                duration: viewModel.duration,
                onSeek: { time in viewModel.seek(to: time) }
            )
            .frame(height: 44)
            .padding(.horizontal, 16)
            .padding(.top, 4)
            .accessibilityLabel("Trim range")
            .accessibilityHint("Adjust the start and end handles to choose the segment to save.")

            // Trim time labels
            HStack {
                Label(formatTime(viewModel.trimStart), systemImage: "scissors")
                    .font(.caption)
                    .foregroundStyle(.orange)
                Spacer()
                Text("Duration: \(formatTime(viewModel.trimmedOutputDuration))")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Spacer()
                Label(formatTime(viewModel.trimEnd), systemImage: "scissors")
                    .font(.caption)
                    .foregroundStyle(.orange)
            }
            .padding(.horizontal, 20)
            .padding(.top, 2)

            HStack(spacing: 12) {
                let maxStart = max(0, viewModel.trimEnd - 0.1)
                let minEnd = min(viewModel.duration, viewModel.trimStart + 0.1)

                Stepper(
                    value: Binding(
                        get: { viewModel.trimStart },
                        set: { newValue in
                            let clamped = min(max(0, newValue), maxStart)
                            viewModel.trimStart = clamped
                            viewModel.seek(to: max(viewModel.currentTime, clamped))
                        }
                    ),
                    in: 0...maxStart,
                    step: 0.1
                ) {
                    Text("Start: \(formatTime(viewModel.trimStart))")
                        .monospacedDigit()
                }

                Stepper(
                    value: Binding(
                        get: { viewModel.trimEnd },
                        set: { newValue in
                            let clamped = min(max(minEnd, newValue), viewModel.duration)
                            viewModel.trimEnd = clamped
                            viewModel.seek(to: min(viewModel.currentTime, clamped))
                        }
                    ),
                    in: minEnd...max(minEnd, viewModel.duration),
                    step: 0.1
                ) {
                    Text("End: \(formatTime(viewModel.trimEnd))")
                        .monospacedDigit()
                }

                Text("Speed")
                    .foregroundStyle(.secondary)
                Picker("", selection: $viewModel.speed) {
                    ForEach(TrimmerViewModel.speedOptions, id: \.self) { speed in
                        Text(TrimmerViewModel.speedLabel(for: speed)).tag(speed)
                    }
                }
                .labelsHidden()
                .pickerStyle(.menu)
                .frame(width: 120)
                .accessibilityLabel("Playback speed")
                .help("Choose the export playback speed.")

                Toggle("Remove audio", isOn: $viewModel.removeAudio)
                    .accessibilityLabel("Remove audio")
                    .help("Remove the audio track from the exported video. Preview audio is unchanged.")
                    .toggleStyle(.checkbox)

                if viewModel.removeAudio {
                    Text("Export will have no audio")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                Spacer()
            }
            .font(.caption)
            .padding(.horizontal, 20)
            .padding(.top, 6)

            Divider()
                .padding(.top, 10)

            // Playback & action buttons
            HStack {
                Button(action: { viewModel.previewTrimmed() }) {
                    Label("Preview", systemImage: viewModel.isPlaying ? "pause.fill" : "play.fill")
                }
                .help(viewModel.isPlaying ? "Pause the trimmed preview." : "Play the trimmed preview.")

                Spacer()

                Menu {
                    Button("Save Frame", systemImage: "square.and.arrow.down") {
                        viewModel.exportCurrentFrame()
                    }
                    .help("Save the current frame as an image.")

                    Button("Copy Frame", systemImage: "doc.on.doc") {
                        viewModel.copyCurrentFrame()
                    }
                    .help("Copy the current frame to the clipboard.")

                    Divider()

                    Button("Save Without Trimming", systemImage: "film") {
                        if viewModel.removeAudio {
                            viewModel.exportVideo(trimmed: false) { resultURL in
                                guard let resultURL else { return }
                                DispatchQueue.main.async { onDone(resultURL) }
                            }
                        } else {
                            onDone(videoURL)
                        }
                    }
                    .help(viewModel.removeAudio ? "Export the full video without audio." : "Save the original video without trimming.")

                    Button("Save Trimmed", systemImage: "scissors") {
                        viewModel.exportVideo(trimmed: true) { resultURL in
                            guard let resultURL else { return }
                            DispatchQueue.main.async { onDone(resultURL) }
                        }
                    }
                    .help("Export only the selected trimmed segment.")
                } label: {
                    Label("Save", systemImage: "square.and.arrow.down")
                }
                .help("Save the current frame or export the video.")
                .disabled(viewModel.duration <= 0 || viewModel.isExporting)

                Button("Done") {
                    viewModel.cleanup()
                    onDone(nil)
                }
                .keyboardShortcut(.cancelAction)
                .help("Close the trimmer.")
                .tint(.accentColor)
                .buttonStyle(.borderedProminent)
            }
            .padding()
        }
        .frame(minWidth: 660, minHeight: 540)
        .onAppear {
            configureMenuActions()
            installKeyMonitor()
        }
        .onDisappear {
            menuActions.clear()
            removeKeyMonitor()
        }
        .onChange(of: viewModel.speed) { _, _ in
            viewModel.applySpeedChange()
        }
        .disabled(viewModel.isExporting)
        .overlay {
            if viewModel.isExporting {
                ProgressOverlayView(title: "Saving…")
            }
        }
    }

    private func configureMenuActions() {
        menuActions.saveFrame = { [viewModel] in
            guard !viewModel.isExporting else { return }
            viewModel.exportCurrentFrame()
        }
        menuActions.copyFrame = { [viewModel] in
            guard !viewModel.isExporting else { return }
            viewModel.copyCurrentFrame()
        }
        menuActions.saveTrimmed = { [viewModel] in
            guard !viewModel.isExporting else { return }
            viewModel.exportVideo(trimmed: true) { resultURL in
                guard let resultURL else { return }
                DispatchQueue.main.async { onDone(resultURL) }
            }
        }
        menuActions.saveWithoutTrimming = { [viewModel] in
            guard !viewModel.isExporting else { return }
            if viewModel.removeAudio {
                viewModel.exportVideo(trimmed: false) { resultURL in
                    guard let resultURL else { return }
                    DispatchQueue.main.async { onDone(resultURL) }
                }
            } else {
                onDone(videoURL)
            }
        }
        menuActions.togglePlayback = { [viewModel] in viewModel.previewTrimmed() }
        menuActions.previousFrame = { [viewModel] in viewModel.stepFrame(by: -1) }
        menuActions.nextFrame = { [viewModel] in viewModel.stepFrame(by: 1) }
    }

    private func formatTime(_ seconds: Double) -> String {
        let mins = Int(seconds) / 60
        let secs = Int(seconds) % 60
        let frac = Int((seconds.truncatingRemainder(dividingBy: 1)) * 10)
        return String(format: "%d:%02d.%d", mins, secs, frac)
    }

    private func installKeyMonitor() {
        guard keyMonitor == nil else { return }
        trimmerWindow = NSApp.keyWindow
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            guard event.window === trimmerWindow else { return event }
            if let firstResponder = event.window?.firstResponder,
               firstResponder is NSText || firstResponder is NSTextView {
                return event
            }
            let relevantModifiers = event.modifierFlags.intersection([.command, .option, .control])
            guard relevantModifiers.isEmpty else { return event }

            switch Int(event.keyCode) {
            case kVK_LeftArrow:
                viewModel.stepFrame(by: -1)
                return nil
            case kVK_RightArrow:
                viewModel.stepFrame(by: 1)
                return nil
            default:
                return event
            }
        }
    }

    private func removeKeyMonitor() {
        trimmerWindow = nil
        if let keyMonitor {
            NSEvent.removeMonitor(keyMonitor)
            self.keyMonitor = nil
        }
    }
}

// MARK: - Trim Range Slider

private struct TrimRangeSlider: View {
    @Binding var trimStart: Double
    @Binding var trimEnd: Double
    let currentTime: Double
    let duration: Double
    let onSeek: (Double) -> Void

    @State private var dragStartValue: Double = 0
    @State private var dragEndValue: Double = 0
    @State private var draggingStart = false
    @State private var draggingEnd = false

    private let handleWidth: CGFloat = 12
    private let trackHeight: CGFloat = 32

    var body: some View {
        GeometryReader { geo in
            let usable = max(1, geo.size.width - handleWidth * 2)
            let startX = duration > 0 ? (trimStart / duration) * usable : 0
            let endX = duration > 0 ? (trimEnd / duration) * usable : usable
            let playheadX = duration > 0 ? handleWidth + (currentTime / duration) * usable : handleWidth

            ZStack(alignment: .leading) {
                // Dimmed regions (trimmed out)
                RoundedRectangle(cornerRadius: 4)
                    .fill(.primary.opacity(0.1))
                    .frame(height: trackHeight)

                // Active region
                RoundedRectangle(cornerRadius: 2)
                    .fill(.orange.opacity(0.25))
                    .frame(width: max(0, endX - startX + handleWidth * 2), height: trackHeight)
                    .offset(x: startX)

                // Start handle
                trimHandle(color: .orange)
                    .offset(x: startX)
                    .gesture(
                        DragGesture()
                            .onChanged { value in
                                if !draggingStart {
                                    draggingStart = true
                                    dragStartValue = trimStart
                                }
                                let delta = value.translation.width / usable * duration
                                let newStart = max(0, min(dragStartValue + delta, trimEnd - 0.1))
                                trimStart = newStart
                                onSeek(newStart)
                            }
                            .onEnded { _ in draggingStart = false }
                    )

                // End handle
                trimHandle(color: .orange)
                    .offset(x: endX + handleWidth)
                    .gesture(
                        DragGesture()
                            .onChanged { value in
                                if !draggingEnd {
                                    draggingEnd = true
                                    dragEndValue = trimEnd
                                }
                                let delta = value.translation.width / usable * duration
                                let newEnd = max(trimStart + 0.1, min(dragEndValue + delta, duration))
                                trimEnd = newEnd
                                onSeek(newEnd)
                            }
                            .onEnded { _ in draggingEnd = false }
                    )

                // Playhead
                Rectangle()
                    .fill(.white)
                    .frame(width: 2, height: trackHeight + 8)
                    .offset(x: playheadX - 1)
                    .allowsHitTesting(false)
            }
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { value in
                        seekPlayhead(to: value.location.x, usable: usable)
                    }
                    .onEnded { value in
                        seekPlayhead(to: value.location.x, usable: usable)
                    }
            )
        }
    }

    private func seekPlayhead(to x: CGFloat, usable: CGFloat) {
        guard duration > 0, !draggingStart, !draggingEnd else { return }
        let normalized = min(max(0, x - handleWidth), usable) / usable
        onSeek(Double(normalized) * duration)
    }

    private func trimHandle(color: Color) -> some View {
        RoundedRectangle(cornerRadius: 3)
            .fill(color)
            .frame(width: handleWidth, height: trackHeight)
            .overlay {
                RoundedRectangle(cornerRadius: 2)
                    .fill(.white.opacity(0.4))
                    .frame(width: 3, height: 14)
            }
            .cursor(.resizeLeftRight)
    }
}

// MARK: - ViewModel

@MainActor
private class TrimmerViewModel: ObservableObject {
    static let speedOptions: [Double] = [0.5, 0.75, 1.0, 1.1, 1.25, 1.5, 2.0]

    static func speedLabel(for value: Double) -> String {
        if value == value.rounded() {
            return "\(Int(value))x"
        }
        return String(format: "%.1fx", value)
    }

    let player: AVPlayer
    let asset: AVAsset
    let sourceURL: URL

    @Published var duration: Double = 0
    @Published var currentTime: Double = 0
    @Published var trimStart: Double = 0
    @Published var trimEnd: Double = 0
    @Published var isPlaying = false
    @Published var isExporting = false
    @Published var speed: Double = 1.0
    @Published var removeAudio = false
    @Published private(set) var frameStepDuration: Double = 1.0 / 30.0

    func applySpeedChange() {
        player.isMuted = speed != 1.0
        if isPlaying {
            player.rate = Float(speed)
        }
    }

    var trimmedOutputDuration: Double {
        max(0, (trimEnd - trimStart) / speed)
    }

    var totalFrameCount: Int {
        guard duration > 0, frameStepDuration > 0 else { return 0 }
        return max(1, Int((duration / frameStepDuration).rounded(.up)))
    }

    var currentFrameNumber: Int {
        guard totalFrameCount > 0 else { return 1 }
        let currentIndex = Int((max(0, currentTime) / frameStepDuration).rounded())
        return min(totalFrameCount, max(1, currentIndex + 1))
    }

    private var timeObserver: Any?

    init(url: URL) {
        self.sourceURL = url
        let asset = AVURLAsset(url: url)
        self.asset = asset
        let item = AVPlayerItem(asset: asset)
        self.player = AVPlayer(playerItem: item)

        timeObserver = player.addPeriodicTimeObserver(
            forInterval: CMTime(value: 1, timescale: 30),
            queue: .main
        ) { [weak self] time in
            Task { @MainActor [weak self] in
                guard let self else { return }
                self.currentTime = time.seconds
                if self.isPlaying && time.seconds >= self.trimEnd {
                    self.player.pause()
                    self.isPlaying = false
                }
            }
        }
    }

    deinit {
        if let obs = timeObserver {
            player.removeTimeObserver(obs)
        }
    }

    @MainActor
    func loadDuration() async {
        guard duration == 0 else { return }
        if let dur = try? await asset.load(.duration) {
            self.duration = dur.seconds
            self.trimEnd = dur.seconds
        }
        if let track = try? await asset.loadTracks(withMediaType: .video).first {
            if let minFrameDuration = try? await track.load(.minFrameDuration),
               minFrameDuration.isValid,
               minFrameDuration.seconds > 0 {
                frameStepDuration = minFrameDuration.seconds
            } else if let nominalFrameRate = try? await track.load(.nominalFrameRate),
                      nominalFrameRate > 0 {
                frameStepDuration = 1.0 / Double(nominalFrameRate)
            }
        }
    }

    func cleanup() {
        player.pause()
        if let obs = timeObserver {
            player.removeTimeObserver(obs)
            timeObserver = nil
        }
    }

    func seek(to time: Double) {
        let clamped = min(max(0, time), duration)
        currentTime = clamped
        player.seek(to: CMTime(seconds: clamped, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
    }

    func stepFrame(by offset: Int) {
        guard duration > 0, frameStepDuration > 0 else { return }
        player.pause()
        isPlaying = false

        let currentIndex = Int((max(0, currentTime) / frameStepDuration).rounded())
        let targetIndex = max(0, min(currentIndex + offset, max(0, totalFrameCount - 1)))
        seek(to: Double(targetIndex) * frameStepDuration)
    }

    func previewTrimmed() {
        if isPlaying {
            player.pause()
            isPlaying = false
        } else {
            seek(to: trimStart)
            player.isMuted = speed != 1.0
            player.playImmediately(atRate: Float(speed))
            isPlaying = true
        }
    }

    func exportCurrentFrame() {
        guard duration > 0 else { return }
        isExporting = true
        player.pause()
        isPlaying = false

        let outputURL = SaveService.shared.generateURL(for: .screenshot, stemSuffix: "Frame")
        let requestedTime = CMTime(seconds: currentTime, preferredTimescale: 600)

        Task {
            do {
                let frameCapture = try await captureCurrentFrame(at: requestedTime)
                _ = try ScreenshotCapture.saveImage(frameCapture.image, to: outputURL)

                await MainActor.run {
                    self.seek(to: frameCapture.actualTime.seconds)
                    self.isExporting = false
                    SaveService.shared.handleSavedFile(url: outputURL, type: .screenshot)
                }
            } catch {
                await MainActor.run {
                    self.isExporting = false
                    SaveService.shared.showError("Could not save the current frame: \(error.localizedDescription)")
                }
            }
        }
    }

    func copyCurrentFrame() {
        guard duration > 0 else { return }
        isExporting = true
        player.pause()
        isPlaying = false

        let requestedTime = CMTime(seconds: currentTime, preferredTimescale: 600)

        Task {
            do {
                let frameCapture = try await captureCurrentFrame(at: requestedTime)

                let didCopy = await MainActor.run { () -> Bool in
                    self.seek(to: frameCapture.actualTime.seconds)
                    let image = NSImage(cgImage: frameCapture.image, size: .zero)
                    let pasteboard = NSPasteboard.general
                    pasteboard.clearContents()
                    return pasteboard.writeObjects([image])
                }

                await MainActor.run {
                    self.isExporting = false
                    if !didCopy {
                        SaveService.shared.showError("Could not copy the current frame to the clipboard.")
                    }
                }
            } catch {
                await MainActor.run {
                    self.isExporting = false
                    SaveService.shared.showError("Could not copy the current frame: \(error.localizedDescription)")
                }
            }
        }
    }

    private func captureCurrentFrame(at requestedTime: CMTime) async throws -> (image: CGImage, actualTime: CMTime) {
        let generator = AVAssetImageGenerator(asset: asset)
        generator.appliesPreferredTrackTransform = true
        generator.requestedTimeToleranceBefore = .zero
        generator.requestedTimeToleranceAfter = .zero

        return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<(image: CGImage, actualTime: CMTime), Error>) in
            generator.generateCGImageAsynchronously(for: requestedTime) { image, actualTime, error in
                if let image {
                    continuation.resume(returning: (image, actualTime))
                } else {
                    continuation.resume(throwing: error ?? CaptureError.saveFailed)
                }
            }
        }
    }

    func exportVideo(trimmed: Bool, completion: @escaping (URL?) -> Void) {
        isExporting = true

        let outputSuffix = trimmed ? (removeAudio ? " (trimmed, no audio)" : " (trimmed)") : " (no audio)"
        let outputURL = sourceURL.deletingLastPathComponent()
            .appendingPathComponent(sourceURL.deletingPathExtension().lastPathComponent + "\(outputSuffix).mp4")

        // Clean up any existing file at the destination
        try? FileManager.default.removeItem(at: outputURL)

        let startTime = CMTime(seconds: trimmed ? trimStart : 0, preferredTimescale: 600)
        let endTime = CMTime(seconds: trimmed ? trimEnd : duration, preferredTimescale: 600)
        let timeRange = CMTimeRange(start: startTime, end: endTime)

        Task {
            do {
                let composition = AVMutableComposition()
                guard let track = try await asset.loadTracks(withMediaType: .video).first,
                      let compositionTrack = composition.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid) else {
                    self.isExporting = false
                    completion(nil)
                    return
                }
                try compositionTrack.insertTimeRange(timeRange, of: track, at: .zero)
                let preferredTransform = try await track.load(.preferredTransform)
                compositionTrack.preferredTransform = preferredTransform

                let targetDuration = CMTimeMultiplyByFloat64(timeRange.duration, multiplier: 1.0 / speed)
                compositionTrack.scaleTimeRange(
                    CMTimeRange(start: .zero, duration: timeRange.duration),
                    toDuration: targetDuration
                )

                if !removeAudio,
                   let audioTrack = try? await asset.loadTracks(withMediaType: .audio).first,
                   let compositionAudio = composition.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid) {
                    try? compositionAudio.insertTimeRange(timeRange, of: audioTrack, at: .zero)
                    compositionAudio.scaleTimeRange(
                        CMTimeRange(start: .zero, duration: timeRange.duration),
                        toDuration: targetDuration
                    )
                }

                guard let session = AVAssetExportSession(asset: composition, presetName: AVAssetExportPresetHighestQuality) else {
                    self.isExporting = false
                    completion(nil)
                    return
                }
                session.outputURL = outputURL
                session.outputFileType = .mp4

                try await session.export(to: outputURL, as: .mp4)
                try? FileManager.default.removeItem(at: self.sourceURL)
                self.isExporting = false
                completion(outputURL)
            } catch {
                self.isExporting = false
                completion(nil)
            }
        }
    }
}

// MARK: - Player View (AVPlayerView wrapper)

private struct PlayerView: NSViewRepresentable {
    let player: AVPlayer

    func makeNSView(context: Context) -> AVPlayerView {
        let view = AVPlayerView()
        view.player = player
        view.controlsStyle = .none
        view.showsFullScreenToggleButton = false
        return view
    }

    func updateNSView(_ nsView: AVPlayerView, context: Context) {
        nsView.player = player
    }
}

// MARK: - Cursor modifier

private struct CursorModifier: ViewModifier {
    let cursor: NSCursor
    func body(content: Content) -> some View {
        content.onHover { inside in
            if inside { cursor.push() } else { NSCursor.pop() }
        }
    }
}

private extension View {
    func cursor(_ cursor: NSCursor) -> some View {
        modifier(CursorModifier(cursor: cursor))
    }
}

private struct ProgressOverlayView: View {
    let title: String

    var body: some View {
        ZStack {
            Color.black.opacity(0.2)
                .ignoresSafeArea()

            VStack(spacing: 10) {
                ProgressView()
                    .controlSize(.regular)
                Text(title)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .padding(.horizontal, 18)
            .padding(.vertical, 14)
            .background(.ultraThinMaterial)
            .clipShape(RoundedRectangle(cornerRadius: 10))
        }
    }
}
