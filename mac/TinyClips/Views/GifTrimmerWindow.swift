import AppKit
import SwiftUI
import ImageIO

// MARK: - GIF Data passed from GifWriter

struct GifCaptureData {
    let frames: [CGImage]
    let frameDelay: Double
    let maxWidth: CGFloat

    init(frames: [CGImage], frameDelay: Double, maxWidth: CGFloat) {
        self.frames = frames
        self.frameDelay = frameDelay
        self.maxWidth = maxWidth
    }

    init(contentsOf url: URL) throws {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else {
            throw CocoaError(.fileReadCorruptFile)
        }

        let frameCount = CGImageSourceGetCount(source)
        var decodedFrames: [CGImage] = []
        decodedFrames.reserveCapacity(frameCount)
        var totalDelay = 0.0

        for index in 0..<frameCount {
            guard let frame = CGImageSourceCreateImageAtIndex(source, index, nil) else { continue }
            decodedFrames.append(frame)

            let properties = CGImageSourceCopyPropertiesAtIndex(source, index, nil) as? [CFString: Any]
            let gifProperties = properties?[kCGImagePropertyGIFDictionary] as? [CFString: Any]
            let frameDelay = (gifProperties?[kCGImagePropertyGIFUnclampedDelayTime] as? Double)
                ?? (gifProperties?[kCGImagePropertyGIFDelayTime] as? Double)
                ?? 0.1
            totalDelay += max(0.01, frameDelay)
        }

        frames = decodedFrames
        guard !frames.isEmpty else {
            throw CocoaError(.fileReadCorruptFile)
        }

        frameDelay = totalDelay / Double(frames.count)
        maxWidth = CGFloat(frames.map(\.width).max() ?? 1)
    }
}

// MARK: - Window

class GifTrimmerWindow: NSWindow, NSWindowDelegate {
    private var onComplete: ((URL?) -> Void)?
    private var didComplete = false
    private let menuActions = TrimmerMenuActions()

    convenience init(gifData: GifCaptureData, outputURL: URL, onComplete: @escaping (URL?) -> Void) {
        self.init(
            contentRect: NSRect(x: 0, y: 0, width: 580, height: 460),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        self.onComplete = onComplete
        self.title = "Trim GIF"
        self.isReleasedWhenClosed = false
        self.delegate = self
        self.minSize = NSSize(width: 560, height: 420)
        self.center()
        TrimmerMenuCommands.installIfNeeded()

        let trimmerView = GifTrimmerView(
            gifData: gifData,
            outputURL: outputURL,
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
    @objc func trimmerSaveAllFrames(_ sender: Any?) { menuActions.saveAllFrames?() }
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

private struct GifTrimmerView: View {
    let gifData: GifCaptureData
    let outputURL: URL
    let menuActions: TrimmerMenuActions
    let onDone: (URL?) -> Void

    @StateObject private var viewModel: GifTrimmerViewModel
    @State private var isSaving = false

    init(gifData: GifCaptureData, outputURL: URL, menuActions: TrimmerMenuActions, onDone: @escaping (URL?) -> Void) {
        self.gifData = gifData
        self.outputURL = outputURL
        self.menuActions = menuActions
        self.onDone = onDone
        _viewModel = StateObject(wrappedValue: GifTrimmerViewModel(gifData: gifData))
    }

    var body: some View {
        VStack(spacing: 0) {
            // Frame preview
            if let currentImage = viewModel.currentFrameImage {
                Image(nsImage: currentImage)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .frame(minWidth: 400, minHeight: 260)
                    .clipShape(RoundedRectangle(cornerRadius: 6))
                    .padding([.top, .horizontal])
            } else {
                Color.clear
                    .frame(minWidth: 400, minHeight: 260)
                    .padding([.top, .horizontal])
            }

            // Frame counter and selection
            HStack(spacing: 10) {
                Text("\(formatDuration(viewModel.currentDurationSeconds))")
                    .monospacedDigit()
                    .frame(width: 64, alignment: .leading)

                Spacer(minLength: 8)

                Button(action: { viewModel.stepFrame(by: -1) }) {
                    Text("<")
                }
                .accessibilityLabel("Previous frame")
                .help("Move to the previous frame.")

                Text("Frame \(viewModel.currentFrameIndex + 1) of \(max(1, viewModel.totalFrames))")
                    .monospacedDigit()
                    .frame(minWidth: 140)
                    .multilineTextAlignment(.center)

                Button(action: { viewModel.stepFrame(by: 1) }) {
                    Text(">")
                }
                .accessibilityLabel("Next frame")
                .help("Move to the next frame.")

                Spacer(minLength: 8)

                Text("\(formatDuration(viewModel.totalDurationSeconds)) total")
                    .monospacedDigit()
                    .frame(width: 80, alignment: .trailing)
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            .padding(.horizontal, 20)
            .padding(.top, 6)
            .disabled(viewModel.totalFrames <= 0)

            // Trim range slider
            GifTrimSlider(
                trimStart: $viewModel.trimStartFrame,
                trimEnd: $viewModel.trimEndFrame,
                currentFrame: viewModel.currentFrameIndex,
                totalFrames: viewModel.totalFrames,
                onSeek: { frame in viewModel.seekTo(frame: frame) }
            )
            .frame(height: 44)
            .padding(.horizontal, 16)
            .padding(.top, 4)
            .accessibilityLabel("Trim frame range")
            .accessibilityHint("Adjust the start and end handles to choose which frames to keep.")

            // Trim info
            HStack {
                Label("Frame \(viewModel.trimStartFrame + 1)", systemImage: "scissors")
                    .font(.caption)
                    .foregroundStyle(.orange)
                Spacer()
                Text("\(viewModel.trimmedFrameCount) frames (\(String(format: "%.1f", viewModel.trimmedDurationSeconds))s)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Spacer()
                Label("Frame \(viewModel.trimEndFrame + 1)", systemImage: "scissors")
                    .font(.caption)
                    .foregroundStyle(.orange)
            }
            .padding(.horizontal, 20)
            .padding(.top, 2)

            HStack(spacing: 12) {
                let maxStart = max(0, viewModel.trimEndFrame - 1)
                let minEnd = min(max(0, viewModel.totalFrames - 1), viewModel.trimStartFrame + 1)
                let maxEnd = max(minEnd, viewModel.totalFrames - 1)

                Stepper(
                    value: Binding(
                        get: { viewModel.trimStartFrame },
                        set: { newValue in
                            let clamped = min(max(0, newValue), maxStart)
                            viewModel.trimStartFrame = clamped
                            viewModel.seekTo(frame: clamped)
                        }
                    ),
                    in: 0...maxStart
                ) {
                    Text("Start: \(viewModel.trimStartFrame + 1)")
                        .monospacedDigit()
                }

                Stepper(
                    value: Binding(
                        get: { viewModel.trimEndFrame },
                        set: { newValue in
                            let clamped = min(max(minEnd, newValue), maxEnd)
                            viewModel.trimEndFrame = clamped
                            viewModel.seekTo(frame: min(viewModel.currentFrameIndex, clamped))
                        }
                    ),
                    in: minEnd...maxEnd
                ) {
                    Text("End: \(viewModel.trimEndFrame + 1)")
                        .monospacedDigit()
                }

                Text("Speed")
                    .foregroundStyle(.secondary)
                Picker("", selection: $viewModel.speed) {
                    ForEach(GifTrimmerViewModel.speedOptions, id: \.self) { speed in
                        Text(GifTrimmerViewModel.speedLabel(for: speed)).tag(speed)
                    }
                }
                .labelsHidden()
                .pickerStyle(.menu)
                .frame(width: 120)
                .accessibilityLabel("Playback speed")
                .help("Choose the GIF playback speed.")
                Spacer()
            }
            .font(.caption)
            .padding(.horizontal, 20)
            .padding(.top, 6)

            Divider()
                .padding(.top, 10)

            // Action buttons
            HStack {
                Button(action: { viewModel.togglePlayback() }) {
                    Label("Preview", systemImage: viewModel.isPlaying ? "pause.fill" : "play.fill")
                }
                .help(viewModel.isPlaying ? "Pause the preview." : "Play the selected frames.")

                Spacer()

                Menu {
                    Button("Save Frame", systemImage: "square.and.arrow.down") {
                        saveCurrentFrame()
                    }
                    .help("Save the current frame as an image.")

                    Button("Copy Frame", systemImage: "doc.on.doc") {
                        copyCurrentFrame()
                    }
                    .help("Copy the current frame to the clipboard.")

                    Divider()

                    Button("Save All Frames", systemImage: "photo.stack") {
                        saveAllFrames()
                    }
                    .help("Save every frame as separate images.")

                    Button("Save Trimmed", systemImage: "scissors") {
                        saveTrimmedGif()
                    }
                    .help("Export a GIF using the selected frame range.")
                    .keyboardShortcut(.defaultAction)
                } label: {
                    Label("Save", systemImage: "square.and.arrow.down")
                }
                .help("Save the current frame or export the GIF.")

                Button("Done") {
                    onDone(nil)
                }
                .keyboardShortcut(.cancelAction)
                .help("Close the trimmer.")
                .tint(.accentColor)
                .buttonStyle(.borderedProminent)
            }
            .padding()
        }
        .frame(minWidth: 560, minHeight: 420)
        .onAppear(perform: configureMenuActions)
        .onDisappear(perform: menuActions.clear)
        .onChange(of: viewModel.speed) { _, _ in
            viewModel.restartPlaybackTimerIfNeeded()
        }
        .disabled(isSaving)
        .overlay {
            if isSaving {
                ProgressOverlayView(title: "Saving…")
            }
        }
    }

    private func configureMenuActions() {
        menuActions.saveFrame = saveCurrentFrame
        menuActions.copyFrame = copyCurrentFrame
        menuActions.saveTrimmed = saveTrimmedGif
        menuActions.saveAllFrames = saveAllFrames
        menuActions.togglePlayback = viewModel.togglePlayback
        menuActions.previousFrame = { viewModel.stepFrame(by: -1) }
        menuActions.nextFrame = { viewModel.stepFrame(by: 1) }
    }

    private func saveTrimmedGif() {
        guard !isSaving else { return }
        isSaving = true

        let destinationURL = outputURL

        DispatchQueue.main.async {
            if let url = viewModel.exportGif(to: destinationURL, trimmed: true) {
                isSaving = false
                onDone(url)
            } else {
                isSaving = false
            }
        }
    }

    private func saveAllFrames() {
        guard !isSaving else { return }
        isSaving = true

        let destinationDirectory = destinationDirectoryForFrames()

        do {
            try FileManager.default.createDirectory(
                at: destinationDirectory,
                withIntermediateDirectories: true
            )
        } catch {
            isSaving = false
            SaveService.shared.showError("Could not create folder for frames: \(error.localizedDescription)")
            return
        }

        DispatchQueue.main.async {
            do {
                let savedDirectory = try viewModel.exportFrames(to: destinationDirectory, sourceURL: outputURL)
                isSaving = false
                SaveService.shared.handleSavedFile(url: savedDirectory, type: .gif)
            } catch {
                isSaving = false
                SaveService.shared.showError("Could not save the GIF frames: \(error.localizedDescription)")
            }
        }
    }

    private func saveCurrentFrame() {
        guard let frame = viewModel.currentFrameCGImage else {
            SaveService.shared.showError("Could not access the current frame.")
            return
        }

        let outputURL = SaveService.shared.generateURL(for: .screenshot, stemSuffix: "Frame")
        do {
            _ = try ScreenshotCapture.saveImage(frame, to: outputURL)
            SaveService.shared.handleSavedFile(url: outputURL, type: .screenshot)
        } catch {
            SaveService.shared.showError("Could not save the current frame: \(error.localizedDescription)")
        }
    }

    private func copyCurrentFrame() {
        guard let frame = viewModel.currentFrameCGImage else {
            SaveService.shared.showError("Could not access the current frame.")
            return
        }

        let image = NSImage(cgImage: frame, size: .zero)
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        if !pasteboard.writeObjects([image]) {
            SaveService.shared.showError("Could not copy the current frame to the clipboard.")
        }
    }

    private func destinationDirectoryForFrames() -> URL {
        let stem = outputURL.deletingPathExtension().lastPathComponent
        return outputURL.deletingLastPathComponent()
            .appendingPathComponent("\(stem) Frames", isDirectory: true)
    }

    private func formatDuration(_ seconds: Double) -> String {
        String(format: "%.1fs", seconds)
    }
}

// MARK: - GIF Trim Slider

private struct GifTrimSlider: View {
    @Binding var trimStart: Int
    @Binding var trimEnd: Int
    let currentFrame: Int
    let totalFrames: Int
    let onSeek: (Int) -> Void

    @State private var dragStartValue: Int = 0
    @State private var dragEndValue: Int = 0
    @State private var draggingStart = false
    @State private var draggingEnd = false

    private let handleWidth: CGFloat = 12
    private let trackHeight: CGFloat = 32

    var body: some View {
        GeometryReader { geo in
            let usable = max(1, geo.size.width - handleWidth * 2)
            let startX = totalFrames > 1 ? CGFloat(trimStart) / CGFloat(totalFrames - 1) * usable : 0
            let endX = totalFrames > 1 ? CGFloat(trimEnd) / CGFloat(totalFrames - 1) * usable : usable
            let playheadX = totalFrames > 1 ? handleWidth + CGFloat(currentFrame) / CGFloat(totalFrames - 1) * usable : handleWidth

            ZStack(alignment: .leading) {
                // Background track with frame ticks
                RoundedRectangle(cornerRadius: 4)
                    .fill(.primary.opacity(0.1))
                    .frame(height: trackHeight)

                // Active region
                RoundedRectangle(cornerRadius: 2)
                    .fill(.orange.opacity(0.25))
                    .frame(width: max(0, endX - startX + handleWidth * 2), height: trackHeight)
                    .offset(x: startX)

                // Frame tick marks (sparse for readability)
                let tickInterval = max(1, totalFrames / 30)
                ForEach(Array(stride(from: 0, to: totalFrames, by: tickInterval)), id: \.self) { i in
                    let tickX = totalFrames > 1 ? handleWidth + CGFloat(i) / CGFloat(totalFrames - 1) * usable : handleWidth
                    Rectangle()
                        .fill(.primary.opacity(0.15))
                        .frame(width: 1, height: trackHeight * 0.4)
                        .offset(x: tickX)
                }

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
                                let delta = value.translation.width / usable * CGFloat(max(1, totalFrames - 1))
                                let newStart = max(0, min(Int(CGFloat(dragStartValue) + delta), trimEnd))
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
                                let delta = value.translation.width / usable * CGFloat(max(1, totalFrames - 1))
                                let newEnd = max(trimStart, min(Int(CGFloat(dragEndValue) + delta), totalFrames - 1))
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
        guard totalFrames > 0, !draggingStart, !draggingEnd else { return }
        if totalFrames == 1 {
            onSeek(0)
            return
        }
        let normalized = min(max(0, x - handleWidth), usable) / usable
        let frame = Int((normalized * CGFloat(totalFrames - 1)).rounded())
        onSeek(frame)
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
private class GifTrimmerViewModel: ObservableObject {
    static let speedOptions: [Double] = [0.5, 0.75, 1.0, 1.1, 1.25, 1.5, 2.0]

    static func speedLabel(for value: Double) -> String {
        if value == value.rounded() {
            return "\(Int(value))x"
        }
        return String(format: "%.1fx", value)
    }

    let gifData: GifCaptureData

    @Published var currentFrameIndex: Int = 0
    @Published var trimStartFrame: Int = 0
    @Published var trimEndFrame: Int = 0
    @Published var isPlaying = false
    @Published var speed: Double = 1.0

    var totalFrames: Int { gifData.frames.count }
    var trimmedFrameCount: Int { max(0, trimEndFrame - trimStartFrame + 1) }
    var effectiveFrameDelay: Double { max(0.01, gifData.frameDelay / speed) }
    var totalDurationSeconds: Double { Double(totalFrames) * effectiveFrameDelay }
    var trimmedDurationSeconds: Double { Double(trimmedFrameCount) * effectiveFrameDelay }

    var currentFrameImage: NSImage? {
        guard currentFrameIndex >= 0, currentFrameIndex < gifData.frames.count else { return nil }
        let cg = gifData.frames[currentFrameIndex]
        return NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
    }

    var currentFrameCGImage: CGImage? {
        guard currentFrameIndex >= 0, currentFrameIndex < gifData.frames.count else { return nil }
        return gifData.frames[currentFrameIndex]
    }

    private var playbackTimer: Timer?

    init(gifData: GifCaptureData) {
        self.gifData = gifData
        self.trimEndFrame = max(0, gifData.frames.count - 1)
    }

    func seekTo(frame: Int) {
        currentFrameIndex = max(0, min(frame, totalFrames - 1))
    }

    func stepFrame(by offset: Int) {
        guard totalFrames > 0 else { return }
        stopPlayback()
        seekTo(frame: currentFrameIndex + offset)
    }

    var currentDurationSeconds: Double {
        Double(currentFrameIndex) * effectiveFrameDelay
    }

    func togglePlayback() {
        if isPlaying {
            stopPlayback()
        } else {
            startPlayback()
        }
    }

    private func startPlayback() {
        isPlaying = true
        currentFrameIndex = trimStartFrame
        playbackTimer = Timer.scheduledTimer(withTimeInterval: effectiveFrameDelay, repeats: true) { [weak self] _ in
            guard let self else { return }
            DispatchQueue.main.async {
                if self.currentFrameIndex >= self.trimEndFrame {
                    self.currentFrameIndex = self.trimStartFrame
                } else {
                    self.currentFrameIndex += 1
                }
            }
        }
    }

    private func stopPlayback() {
        isPlaying = false
        playbackTimer?.invalidate()
        playbackTimer = nil
    }

    func restartPlaybackTimerIfNeeded() {
        guard isPlaying else { return }
        playbackTimer?.invalidate()
        playbackTimer = Timer.scheduledTimer(withTimeInterval: effectiveFrameDelay, repeats: true) { [weak self] _ in
            guard let self else { return }
            DispatchQueue.main.async {
                if self.currentFrameIndex >= self.trimEndFrame {
                    self.currentFrameIndex = self.trimStartFrame
                } else {
                    self.currentFrameIndex += 1
                }
            }
        }
    }

    func exportGif(to url: URL, trimmed: Bool) -> URL? {
        stopPlayback()

        let frames: [CGImage]
        if trimmed {
            let start = max(0, trimStartFrame)
            let end = min(gifData.frames.count - 1, trimEndFrame)
            frames = Array(gifData.frames[start...end])
        } else {
            frames = gifData.frames
        }

        guard !frames.isEmpty else { return nil }

        // Downscale if needed
        let processedFrames: [CGImage]
        if CGFloat(frames[0].width) > gifData.maxWidth {
            let scale = gifData.maxWidth / CGFloat(frames[0].width)
            let newWidth = Int(gifData.maxWidth)
            let newHeight = Int(CGFloat(frames[0].height) * scale)
            let size = CGSize(width: newWidth, height: newHeight)
            processedFrames = frames.compactMap { downscale($0, to: size) }
        } else {
            processedFrames = frames
        }

        guard let destination = CGImageDestinationCreateWithURL(
            url as CFURL,
            "com.compuserve.gif" as CFString,
            processedFrames.count,
            nil
        ) else { return nil }

        let gifProperties: [String: Any] = [
            kCGImagePropertyGIFDictionary as String: [
                kCGImagePropertyGIFLoopCount: 0,
            ],
        ]
        CGImageDestinationSetProperties(destination, gifProperties as CFDictionary)

        for frame in processedFrames {
            let frameProperties: [String: Any] = [
                kCGImagePropertyGIFDictionary as String: [
                    kCGImagePropertyGIFDelayTime: effectiveFrameDelay,
                ],
            ]
            CGImageDestinationAddImage(destination, frame, frameProperties as CFDictionary)
        }

        guard CGImageDestinationFinalize(destination) else { return nil }
        return url
    }

    func exportFrames(to directoryURL: URL, sourceURL: URL) throws -> URL {
        stopPlayback()

        let frameDigits = max(3, String(max(1, totalFrames)).count)
        let stem = sourceURL.deletingPathExtension().lastPathComponent

        for (index, frame) in gifData.frames.enumerated() {
            let fileName = String(format: "%@ Frame %0*d.png", stem, frameDigits, index + 1)
            let frameURL = directoryURL.appendingPathComponent(fileName)
            _ = try ScreenshotCapture.saveImage(frame, to: frameURL)
        }

        return directoryURL
    }

    private func downscale(_ image: CGImage, to size: CGSize) -> CGImage? {
        guard let context = CGContext(
            data: nil,
            width: Int(size.width),
            height: Int(size.height),
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue
        ) else { return nil }
        context.interpolationQuality = .high
        context.draw(image, in: CGRect(origin: .zero, size: size))
        return context.makeImage()
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
