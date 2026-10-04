import AppKit
import AVFoundation
import SwiftUI

// MARK: - Root

struct StudioRootView: View {
    @ObservedObject var viewModel: StudioViewModel

    var body: some View {
        Group {
            switch viewModel.state {
            case .loading:
                ProgressView("Opening…")
            case .unavailable(let message):
                StudioUnavailableView(message: message)
            case .ready:
                StudioEditorView(viewModel: viewModel)
            }
        }
        .frame(minWidth: 980, maxWidth: .infinity, minHeight: 640, maxHeight: .infinity)
        .task {
            await viewModel.load()
        }
    }
}

private struct StudioUnavailableView: View {
    let message: String

    var body: some View {
        VStack(spacing: 10) {
            Image(systemName: "exclamationmark.triangle")
                .font(.system(size: 34))
                .foregroundStyle(.secondary)
                .accessibilityHidden(true)
            Text("This project can't be opened")
                .font(.headline)
            Text(message)
                .font(.callout)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 420)
        }
        .padding(32)
        .accessibilityElement(children: .combine)
    }
}

private struct StudioEditorView: View {
    @ObservedObject var viewModel: StudioViewModel

    var body: some View {
        VStack(spacing: 0) {
            StudioHeaderView(viewModel: viewModel)
            Divider()
            HStack(spacing: 0) {
                StudioPreviewView(viewModel: viewModel)
                    .frame(minWidth: 520, maxWidth: .infinity, maxHeight: .infinity)
                Divider()
                StudioInspectorView(viewModel: viewModel)
                    .frame(width: 320)
            }
            Divider()
            StudioTimelineView(viewModel: viewModel)
        }
        .disabled(viewModel.isExporting)
        .overlay {
            if viewModel.isExporting {
                StudioExportOverlay(progress: viewModel.exportProgress) {
                    viewModel.cancelExport()
                }
            }
        }
    }
}

// MARK: - Header

private struct StudioHeaderView: View {
    @ObservedObject var viewModel: StudioViewModel

    private static let aspects: [StudioCanvasAspect] = [
        .auto, .square, .landscape4x3, .landscape16x9, .portrait3x4, .portrait9x16
    ]

    var body: some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(viewModel.clipName)
                    .font(.headline)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Text(exportSizeText)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Spacer(minLength: 12)

            Button {
                viewModel.undo()
            } label: {
                Image(systemName: "arrow.uturn.backward")
            }
            .disabled(!viewModel.canUndo)
            .accessibilityLabel("Undo")
            .help("Undo (Command-Z)")

            Button {
                viewModel.redo()
            } label: {
                Image(systemName: "arrow.uturn.forward")
            }
            .disabled(!viewModel.canRedo)
            .accessibilityLabel("Redo")
            .help("Redo (Shift-Command-Z)")

            Picker("Canvas", selection: aspectBinding) {
                ForEach(Self.aspects, id: \.self) { aspect in
                    Text(StudioEditorModel.aspectName(aspect)).tag(aspect)
                }
            }
            .pickerStyle(.menu)
            .frame(width: 150)
            .help("The shape of the exported video")

            Button {
                viewModel.export()
            } label: {
                Label("Export", systemImage: "square.and.arrow.up")
            }
            .buttonStyle(.borderedProminent)
            .disabled(!viewModel.canExport)
            .help("Export the video (Command-E)")
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
    }

    private var exportSizeText: String {
        let size = viewModel.exportRenderSize
        return "Exports at \(Int(size.width)) × \(Int(size.height))"
    }

    private var aspectBinding: Binding<StudioCanvasAspect> {
        Binding(
            get: { viewModel.project?.canvas.aspect ?? .auto },
            set: { viewModel.setCanvasAspect($0) }
        )
    }
}

// MARK: - Preview

private struct StudioPreviewView: View {
    @ObservedObject var viewModel: StudioViewModel
    @State private var dragStartBubble: StudioRect?
    @State private var isHoveringBubble = false

    private static let space = "studioPreview"

    var body: some View {
        GeometryReader { proxy in
            let canvasSize = viewModel.previewRenderSize
            let geometry = StudioEditorModel.canvasGeometry(viewSize: proxy.size, canvasSize: canvasSize)
            let canvasRect = geometry.canvasRectInView

            ZStack(alignment: .topLeading) {
                Color.clear

                StudioPlayerView(player: viewModel.player)
                    .frame(width: CGFloat(canvasRect.width), height: CGFloat(canvasRect.height))
                    .accessibilityElement()
                    .accessibilityLabel("Preview")
                    .accessibilityValue(previewDescription)
                    .offset(x: CGFloat(canvasRect.x), y: CGFloat(canvasRect.y))

                if let bubble = viewModel.bubbleRect(canvasSize: canvasSize) {
                    bubbleHandle(bubble: bubble, geometry: geometry, canvasSize: canvasSize)
                }
            }
            .coordinateSpace(.named(Self.space))
        }
        .padding(16)
        .background(Color(nsColor: .underPageBackgroundColor))
    }

    private var previewDescription: String {
        guard let editor = viewModel.editor else { return "" }
        let layout = StudioEditorModel.layoutName(editor.effectiveLayout)
        guard editor.effectiveLayout == .bubble else { return layout }
        let corner = StudioEditorModel.anchorName(editor.currentScene.bubble.anchor).lowercased()
        return "\(layout), camera \(corner)"
    }

    /// A handle over the camera bubble. Dragging it moves the bubble; the Position and offset
    /// controls in the inspector make the same change without a pointer.
    private func bubbleHandle(
        bubble: StudioRect,
        geometry: StudioEditorCanvasGeometry,
        canvasSize: CGSize
    ) -> some View {
        let pixelsPerPoint = max(geometry.canvasPixelsPerViewPoint, 0.0001)
        let origin = geometry.viewPoint(fromCanvasPoint: CGPoint(x: bubble.x, y: bubble.y))
        let width = max(16, CGFloat(bubble.width / pixelsPerPoint))
        let height = max(16, CGFloat(bubble.height / pixelsPerPoint))
        let isActive = isHoveringBubble || dragStartBubble != nil

        return RoundedRectangle(cornerRadius: 8)
            .strokeBorder(
                isActive ? Color.accentColor : Color.clear,
                style: StrokeStyle(lineWidth: 2, dash: [6, 4])
            )
            .frame(width: width, height: height)
            .contentShape(Rectangle())
            .onHover { hovering in
                isHoveringBubble = hovering
            }
            .gesture(
                // Measured in the preview's space, which stays still while the handle moves.
                DragGesture(minimumDistance: 1, coordinateSpace: .named(Self.space))
                    .onChanged { value in
                        let start: StudioRect
                        if let dragStartBubble {
                            start = dragStartBubble
                        } else {
                            start = bubble
                            dragStartBubble = bubble
                            viewModel.beginGesture()
                        }
                        let topLeft = CGPoint(
                            x: start.x + Double(value.translation.width) * pixelsPerPoint,
                            y: start.y + Double(value.translation.height) * pixelsPerPoint
                        )
                        viewModel.moveBubble(topLeft: topLeft, canvasSize: canvasSize)
                    }
                    .onEnded { _ in
                        dragStartBubble = nil
                        viewModel.endGesture()
                    }
            )
            .help("Drag to move the camera")
            .accessibilityHidden(true)
            .offset(x: origin.x, y: origin.y)
    }
}

private struct StudioPlayerView: NSViewRepresentable {
    let player: AVPlayer?

    func makeNSView(context: Context) -> StudioPlayerHostView {
        let view = StudioPlayerHostView(frame: .zero)
        view.playerLayer?.player = player
        return view
    }

    func updateNSView(_ nsView: StudioPlayerHostView, context: Context) {
        if nsView.playerLayer?.player !== player {
            nsView.playerLayer?.player = player
        }
    }
}

/// A view whose backing layer is the player layer, so AppKit keeps the layer sized to the view.
final class StudioPlayerHostView: NSView {
    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override func makeBackingLayer() -> CALayer {
        let layer = AVPlayerLayer()
        layer.videoGravity = .resizeAspect
        layer.backgroundColor = NSColor.black.cgColor
        return layer
    }

    var playerLayer: AVPlayerLayer? {
        layer as? AVPlayerLayer
    }
}

// MARK: - Inspector

private struct StudioInspectorView: View {
    @ObservedObject var viewModel: StudioViewModel

    private static let shapes: [StudioCameraShape] = [.circle, .roundedRectangle, .squircle, .rectangle]
    private static let anchors: [StudioAnchor] = [.topLeft, .topRight, .bottomLeft, .bottomRight]
    private static let swatchColumns = Array(repeating: GridItem(.fixed(22), spacing: 7), count: 9)

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                if viewModel.hasCamera {
                    StudioSceneInspectorSection(viewModel: viewModel)
                }
                layoutSection
                backgroundSection
                screenSection
                if viewModel.hasCamera {
                    cameraSection
                }
                StudioZoomInspectorSection(viewModel: viewModel)
                StudioCutInspectorSection(viewModel: viewModel)
                extrasSection
                Divider()
                Button("Save as Default Look") {
                    viewModel.saveDefaultLook()
                }
                .help("New Studio recordings start with this canvas, background, screen, and camera styling")
            }
            .padding(16)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(Color(nsColor: .controlBackgroundColor))
    }

    // MARK: Sections

    private var layoutSection: some View {
        StudioInspectorSection(title: "Layout") {
            if viewModel.hasCamera {
                Picker("Layout", selection: layoutBinding) {
                    Text("Screen").tag(StudioLayout.screen)
                    Text("Bubble").tag(StudioLayout.bubble)
                    Text("Side by Side").tag(StudioLayout.sideBySide)
                    Text("Camera").tag(StudioLayout.camera)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .accessibilityLabel("Layout")
                .help("Screen only, screen with a camera bubble, side by side, or camera only (1 to 4)")
            } else {
                Text("This recording has no camera, so it shows the screen only.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private var backgroundSection: some View {
        StudioInspectorSection(title: "Background") {
            Toggle("Show a background", isOn: backgroundEnabledBinding)
                .toggleStyle(.checkbox)
                .help("Off leaves the canvas black")
            if background.style != StudioBackgroundStyle.none {
                swatchGrid(title: "Solid", presets: solidBackgroundPresets.filter { $0.style == .solid })
                swatchGrid(title: "Gradient", presets: gradientBackgroundPresets.filter { $0.style == .gradient })
            }
            StudioSliderRow(
                title: "Padding",
                value: canvasPadding,
                range: 0...0.4,
                step: 0.01,
                valueText: percentText(canvasPadding),
                onChange: { viewModel.setCanvasPadding($0) },
                onEditingChanged: { gestureChanged($0) }
            )
        }
    }

    private var screenSection: some View {
        StudioInspectorSection(title: "Screen") {
            StudioSliderRow(
                title: "Corner radius",
                value: screenStyle.cornerRadius,
                range: 0...0.2,
                step: 0.005,
                valueText: percentText(screenStyle.cornerRadius * 5),
                onChange: { viewModel.setScreenCornerRadius($0) },
                onEditingChanged: { gestureChanged($0) }
            )
            StudioSliderRow(
                title: "Shadow",
                value: screenStyle.shadow,
                range: 0...1,
                step: 0.05,
                valueText: percentText(screenStyle.shadow),
                onChange: { viewModel.setScreenShadow($0) },
                onEditingChanged: { gestureChanged($0) }
            )
            Text("Crop")
                .font(.caption)
                .foregroundStyle(.secondary)
            cropControls(
                insets: viewModel.editor?.screenCropInsets ?? StudioCropInsets(),
                set: { edge, value in viewModel.setScreenCropInset(edge, to: value) },
                reset: { viewModel.clearScreenCrop() }
            )
        }
    }

    private var cameraSection: some View {
        StudioInspectorSection(title: "Camera") {
            switch layout {
            case .screen:
                Text("The camera is hidden in this layout.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            case .bubble:
                bubbleControls
                cameraStyleControls
            case .sideBySide:
                sideBySideControls
                cameraStyleControls
            case .camera:
                cameraStyleControls
            }
        }
    }

    @ViewBuilder
    private var bubbleControls: some View {
        Picker("Shape", selection: shapeBinding) {
            ForEach(Self.shapes, id: \.self) { shape in
                Text(StudioEditorModel.shapeName(shape)).tag(shape)
            }
        }
        if camera.shape == .roundedRectangle {
            StudioSliderRow(
                title: "Corner radius",
                value: camera.cornerRadius,
                range: 0...0.5,
                step: 0.01,
                valueText: percentText(camera.cornerRadius * 2),
                onChange: { viewModel.setCameraCornerRadius($0) },
                onEditingChanged: { gestureChanged($0) }
            )
        }
        StudioSliderRow(
            title: "Size",
            value: scene.bubble.size,
            range: 0.08...0.6,
            step: 0.01,
            valueText: percentText(scene.bubble.size),
            onChange: { viewModel.setCameraBubbleSize($0) },
            onEditingChanged: { gestureChanged($0) }
        )
        Picker("Position", selection: anchorBinding) {
            ForEach(Self.anchors, id: \.self) { anchor in
                Text(StudioEditorModel.anchorName(anchor)).tag(anchor)
            }
        }
        .help("Snaps the camera to a corner. Drag it in the preview, or use the offsets, to place it anywhere.")
        StudioSliderRow(
            title: "Horizontal offset",
            value: scene.bubble.offsetX,
            range: -1...1,
            step: 0.005,
            valueText: signedPercentText(scene.bubble.offsetX),
            onChange: { viewModel.setCameraBubbleOffset(x: $0, y: scene.bubble.offsetY) },
            onEditingChanged: { gestureChanged($0) }
        )
        StudioSliderRow(
            title: "Vertical offset",
            value: scene.bubble.offsetY,
            range: -1...1,
            step: 0.005,
            valueText: signedPercentText(scene.bubble.offsetY),
            onChange: { viewModel.setCameraBubbleOffset(x: scene.bubble.offsetX, y: $0) },
            onEditingChanged: { gestureChanged($0) }
        )
    }

    @ViewBuilder
    private var sideBySideControls: some View {
        Picker("Camera side", selection: sideBinding) {
            Text("Left or top").tag(StudioCameraSide.leading)
            Text("Right or bottom").tag(StudioCameraSide.trailing)
        }
        StudioSliderRow(
            title: "Camera share",
            value: scene.split.cameraFraction,
            range: 0.15...0.6,
            step: 0.01,
            valueText: percentText(scene.split.cameraFraction),
            onChange: { viewModel.setSideBySide(side: scene.split.cameraSide, fraction: $0) },
            onEditingChanged: { gestureChanged($0) }
        )
    }

    @ViewBuilder
    private var cameraStyleControls: some View {
        Toggle("Mirror", isOn: mirrorBinding)
            .toggleStyle(.checkbox)
        StudioSliderRow(
            title: "Border",
            value: camera.borderWidth,
            range: 0...0.02,
            step: 0.001,
            valueText: percentText(camera.borderWidth * 50),
            onChange: { viewModel.setCameraBorderWidth($0) },
            onEditingChanged: { gestureChanged($0) }
        )
        StudioSliderRow(
            title: "Shadow",
            value: camera.shadow,
            range: 0...1,
            step: 0.05,
            valueText: percentText(camera.shadow),
            onChange: { viewModel.setCameraShadow($0) },
            onEditingChanged: { gestureChanged($0) }
        )
        Text("Crop")
            .font(.caption)
            .foregroundStyle(.secondary)
        cropControls(
            insets: viewModel.editor?.cameraCropInsets ?? StudioCropInsets(),
            set: { edge, value in viewModel.setCameraCropInset(edge, to: value) },
            reset: { viewModel.clearCameraCrop() }
        )
    }

    private var extrasSection: some View {
        StudioInspectorSection(title: "Extras") {
            Toggle("Click rings", isOn: clickRingsBinding)
                .toggleStyle(.checkbox)
                .help("Draws a ring where each mouse click happened")
            Toggle("Tiny Clips badge", isOn: brandingBinding)
                .toggleStyle(.checkbox)
            Toggle("Mute audio", isOn: muteBinding)
                .toggleStyle(.checkbox)
        }
    }

    private func swatchGrid(title: String, presets: [ExportBackgroundPreset]) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title)
                .font(.caption)
                .foregroundStyle(.secondary)
            LazyVGrid(columns: Self.swatchColumns, alignment: .leading, spacing: 7) {
                ForEach(presets) { preset in
                    swatchButton(preset)
                }
            }
        }
    }

    private func swatchButton(_ preset: ExportBackgroundPreset) -> some View {
        let isSelected = background.preset == preset.id
        return Button {
            viewModel.applyBackgroundPreset(preset)
        } label: {
            BackgroundPresetSwatch(preset: preset, isSelected: isSelected)
        }
        .buttonStyle(.plain)
        .help(preset.label)
        .accessibilityLabel("\(preset.label) background")
        .accessibilityValue(isSelected ? "Selected" : "Not selected")
    }

    // MARK: Values

    private var scene: StudioScene { viewModel.editor?.currentScene ?? StudioScene() }
    private var camera: StudioCameraStyle { viewModel.project?.camera ?? StudioCameraStyle() }
    private var screenStyle: StudioScreenStyle { viewModel.project?.screen ?? StudioScreenStyle() }
    private var background: StudioBackground { viewModel.project?.canvas.background ?? StudioBackground() }
    private var canvasPadding: Double { viewModel.project?.canvas.padding ?? 0 }
    private var layout: StudioLayout { viewModel.editor?.effectiveLayout ?? .screen }

    private func percentText(_ fraction: Double) -> String {
        "\(Int((fraction * 100).rounded()))%"
    }

    private func signedPercentText(_ fraction: Double) -> String {
        let percent = Int((fraction * 100).rounded())
        return percent > 0 ? "+\(percent)%" : "\(percent)%"
    }

    @ViewBuilder
    private func cropControls(
        insets: StudioCropInsets,
        set: @escaping (StudioCropEdge, Double) -> Void,
        reset: @escaping () -> Void
    ) -> some View {
        cropSlider("Crop left", edge: .left, value: insets.left, set: set)
        cropSlider("Crop top", edge: .top, value: insets.top, set: set)
        cropSlider("Crop right", edge: .right, value: insets.right, set: set)
        cropSlider("Crop bottom", edge: .bottom, value: insets.bottom, set: set)
        Button("Reset Crop") {
            reset()
        }
        .disabled(insets.isEmpty)
    }

    private func cropSlider(
        _ title: String,
        edge: StudioCropEdge,
        value: Double,
        set: @escaping (StudioCropEdge, Double) -> Void
    ) -> some View {
        StudioSliderRow(
            title: title,
            value: value,
            range: 0...0.95,
            step: 0.01,
            valueText: percentText(value),
            onChange: { set(edge, $0) },
            onEditingChanged: { gestureChanged($0) }
        )
    }

    /// One slider drag is one undo step.
    private func gestureChanged(_ isEditing: Bool) {
        if isEditing {
            viewModel.beginGesture()
        } else {
            viewModel.endGesture()
        }
    }

    // MARK: Bindings

    private var layoutBinding: Binding<StudioLayout> {
        Binding(
            get: { layout },
            set: { viewModel.setLayout($0) }
        )
    }

    private var backgroundEnabledBinding: Binding<Bool> {
        Binding(
            get: { background.style != StudioBackgroundStyle.none },
            set: { isOn in
                if !isOn {
                    viewModel.removeBackground()
                } else if let preset = gradientBackgroundPresets.first(where: { $0.id == "ocean" }) {
                    viewModel.applyBackgroundPreset(preset)
                }
            }
        )
    }

    private var shapeBinding: Binding<StudioCameraShape> {
        Binding(
            get: { camera.shape },
            set: { viewModel.setCameraShape($0) }
        )
    }

    private var anchorBinding: Binding<StudioAnchor> {
        Binding(
            get: { scene.bubble.anchor },
            set: { viewModel.setCameraAnchor($0) }
        )
    }

    private var sideBinding: Binding<StudioCameraSide> {
        Binding(
            get: { scene.split.cameraSide },
            set: { viewModel.setSideBySide(side: $0, fraction: scene.split.cameraFraction) }
        )
    }

    private var mirrorBinding: Binding<Bool> {
        Binding(
            get: { camera.mirror },
            set: { viewModel.setCameraMirror($0) }
        )
    }

    private var clickRingsBinding: Binding<Bool> {
        Binding(
            get: { viewModel.project?.overlays.clicks.enabled ?? false },
            set: { viewModel.setClickRingsEnabled($0) }
        )
    }

    private var brandingBinding: Binding<Bool> {
        Binding(
            get: { viewModel.project?.overlays.branding ?? false },
            set: { viewModel.setBrandingEnabled($0) }
        )
    }

    private var muteBinding: Binding<Bool> {
        Binding(
            get: { viewModel.project?.audio.muted ?? false },
            set: { viewModel.setMuted($0) }
        )
    }
}

struct StudioInspectorSection<Content: View>: View {
    let title: String
    @ViewBuilder let content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title)
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
                .accessibilityAddTraits(.isHeader)
            content()
        }
    }
}

/// A labelled slider. Values snap to `step` without a tick mark for every step, and
/// `onEditingChanged` reports the start and end of a drag so it can be a single undo step.
struct StudioSliderRow: View {
    let title: String
    let value: Double
    let range: ClosedRange<Double>
    let step: Double
    let valueText: String
    let onChange: (Double) -> Void
    let onEditingChanged: (Bool) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(title)
                Spacer()
                Text(valueText)
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            .accessibilityHidden(true)
            Slider(value: binding, in: range, onEditingChanged: onEditingChanged)
                .controlSize(.small)
                .accessibilityLabel(title)
                .accessibilityValue(valueText)
        }
    }

    private var binding: Binding<Double> {
        Binding(
            get: { min(max(value, range.lowerBound), range.upperBound) },
            set: { newValue in
                let snapped = step > 0 ? (newValue / step).rounded() * step : newValue
                onChange(min(max(snapped, range.lowerBound), range.upperBound))
            }
        )
    }
}

// MARK: - Timeline

enum StudioTimelineMetrics {
    static let edgeInset: CGFloat = 12
}

private struct StudioTimelineView: View {
    @ObservedObject var viewModel: StudioViewModel

    var body: some View {
        VStack(spacing: 10) {
            HStack(spacing: 10) {
                Button {
                    viewModel.togglePlayback()
                } label: {
                    Image(systemName: viewModel.isPlaying ? "pause.fill" : "play.fill")
                        .frame(width: 16)
                }
                .accessibilityLabel(viewModel.isPlaying ? "Pause" : "Play")
                .help("Play or pause (Space)")

                Button {
                    viewModel.stepFrame(by: -1)
                } label: {
                    Image(systemName: "backward.frame.fill")
                }
                .accessibilityLabel("Previous frame")
                .help("Previous frame (Left Arrow)")

                Button {
                    viewModel.stepFrame(by: 1)
                } label: {
                    Image(systemName: "forward.frame.fill")
                }
                .accessibilityLabel("Next frame")
                .help("Next frame (Right Arrow)")

                Text(viewModel.timeText)
                    .font(.callout.monospacedDigit())
                    .foregroundStyle(.secondary)
                    .accessibilityLabel("Time")
                    .accessibilityValue(viewModel.timeText)

                Spacer()

                if viewModel.hasCamera {
                    Button("Split") {
                        viewModel.splitSceneAtPlayhead()
                    }
                    .disabled(!viewModel.canSplitSceneAtPlayhead)
                    .help(viewModel.splitSceneExplanation ?? "Start a new scene at the playhead (S)")
                    .accessibilityLabel("Split scene")
                }

                Button("Add Zoom") {
                    viewModel.addZoomAtPlayhead()
                }
                .disabled(!viewModel.canAddZoomAtPlayhead)
                .help("Add a zoom at the playhead (Z)")

                Button("Cut") {
                    viewModel.addCutAtPlayhead()
                }
                .disabled(!viewModel.canAddCutAtPlayhead)
                .help("Cut a second out of the video at the playhead (X)")
                .accessibilityLabel("Add cut")

                Button("Start Here") {
                    viewModel.setTrimStartAtPlayhead()
                }
                .help("Start the video at the playhead (I)")

                Button("End Here") {
                    viewModel.setTrimEndAtPlayhead()
                }
                .help("End the video at the playhead (O)")
            }

            if viewModel.hasCamera {
                StudioSceneLane(viewModel: viewModel)
                    .frame(height: 24)
            }

            StudioZoomLane(viewModel: viewModel)
                .frame(height: 26)

            StudioCutLane(viewModel: viewModel)
                .frame(height: 22)

            StudioTrimBar(viewModel: viewModel)
                .frame(height: 36)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
    }
}

/// The whole recording as a bar, in source time. The two handles are where the video starts and
/// ends, the tinted part between them is what gets exported, and the line is the playhead.
private struct StudioTrimBar: View {
    @ObservedObject var viewModel: StudioViewModel
    @State private var dragStartTime: Double?

    private static let space = "studioTrimBar"
    private let handleWidth = StudioTimelineMetrics.edgeInset

    var body: some View {
        GeometryReader { proxy in
            let duration = max(viewModel.editor?.sourceDuration ?? 0, 0.0001)
            let trimStart = viewModel.editor?.trimStart ?? 0
            let trimEnd = viewModel.editor?.trimEnd ?? duration
            let usable = max(1, proxy.size.width - handleWidth * 2)
            let startX = CGFloat(trimStart / duration) * usable
            let endX = CGFloat(trimEnd / duration) * usable
            let playheadFraction = min(max(viewModel.playhead / duration, 0), 1)
            let playheadX = handleWidth + CGFloat(playheadFraction) * usable

            ZStack(alignment: .topLeading) {
                RoundedRectangle(cornerRadius: 5)
                    .fill(Color.primary.opacity(0.1))
                    .contentShape(Rectangle())
                    .gesture(
                        DragGesture(minimumDistance: 0, coordinateSpace: .named(Self.space))
                            .onChanged { value in
                                let fraction = Double((value.location.x - handleWidth) / usable)
                                viewModel.scrub(to: min(max(fraction, 0), 1) * duration)
                            }
                    )
                    .accessibilityHidden(true)

                RoundedRectangle(cornerRadius: 3)
                    .fill(Color.accentColor.opacity(0.3))
                    .frame(width: max(0, endX - startX))
                    .offset(x: startX + handleWidth)
                    .allowsHitTesting(false)
                    .accessibilityHidden(true)

                cutGaps(trimStart: trimStart, trimEnd: trimEnd, usable: usable, duration: duration)

                handle(isStart: true, time: trimStart, usable: usable, duration: duration)
                    .offset(x: startX)
                handle(isStart: false, time: trimEnd, usable: usable, duration: duration)
                    .offset(x: endX + handleWidth)

                Rectangle()
                    .fill(Color.primary)
                    .frame(width: 2)
                    .allowsHitTesting(false)
                    .accessibilityElement()
                    .accessibilityLabel("Playhead")
                    .accessibilityValue(viewModel.editor?.playheadText(sourceTime: viewModel.playhead) ?? "")
                    .accessibilityAdjustableAction { direction in
                        switch direction {
                        case .increment:
                            viewModel.stepFrame(by: 1)
                        case .decrement:
                            viewModel.stepFrame(by: -1)
                        @unknown default:
                            break
                        }
                    }
                    .offset(x: playheadX - 1)
            }
            .coordinateSpace(.named(Self.space))
        }
    }

    private func cutGaps(trimStart: Double, trimEnd: Double, usable: CGFloat, duration: Double) -> some View {
        ForEach(Array(viewModel.cuts.enumerated()), id: \.offset) { _, cut in
            let from = max(cut.start, trimStart)
            let to = min(cut.end, trimEnd)
            if to > from {
                Rectangle()
                    .fill(Color(nsColor: .windowBackgroundColor).opacity(0.85))
                    .frame(width: CGFloat((to - from) / duration) * usable)
                    .offset(x: handleWidth + CGFloat(from / duration) * usable)
                    .allowsHitTesting(false)
                    .accessibilityHidden(true)
            }
        }
    }

    private func handle(isStart: Bool, time: Double, usable: CGFloat, duration: Double) -> some View {
        RoundedRectangle(cornerRadius: 3)
            .fill(Color.accentColor)
            .frame(width: handleWidth)
            .overlay {
                Capsule()
                    .fill(Color.white.opacity(0.9))
                    .frame(width: 2, height: 14)
            }
            .contentShape(Rectangle())
            .gesture(
                // Measured in the bar's space, which stays still while the handle moves.
                DragGesture(minimumDistance: 0, coordinateSpace: .named(Self.space))
                    .onChanged { value in
                        let start: Double
                        if let dragStartTime {
                            start = dragStartTime
                        } else {
                            start = time
                            dragStartTime = time
                            viewModel.beginGesture()
                        }
                        setTrim(isStart: isStart, to: start + Double(value.translation.width / usable) * duration)
                    }
                    .onEnded { _ in
                        dragStartTime = nil
                        viewModel.endGesture()
                    }
            )
            .help(isStart ? "Drag to change where the video starts" : "Drag to change where the video ends")
            .accessibilityElement()
            .accessibilityLabel(isStart ? "Start" : "End")
            .accessibilityValue(StudioEditorModel.secondsText(time))
            .accessibilityAdjustableAction { direction in
                // About a hundred steps across the recording, and never finer than one frame.
                let frame = viewModel.editor?.frameDuration ?? 1.0 / 30.0
                let step = max(frame, min(1, duration / 100))
                switch direction {
                case .increment:
                    setTrim(isStart: isStart, to: time + step)
                case .decrement:
                    setTrim(isStart: isStart, to: time - step)
                @unknown default:
                    break
                }
            }
    }

    private func setTrim(isStart: Bool, to sourceTime: Double) {
        if isStart {
            viewModel.setTrimStart(sourceTime)
        } else {
            viewModel.setTrimEnd(sourceTime)
        }
    }
}

// MARK: - Export Overlay

private struct StudioExportOverlay: View {
    let progress: Double
    let onCancel: () -> Void

    var body: some View {
        let fraction = min(max(progress, 0), 1)

        ZStack {
            Color.black.opacity(0.25)
            VStack(spacing: 12) {
                Text("Exporting…")
                    .font(.headline)
                ProgressView(value: fraction)
                    .frame(width: 240)
                    .accessibilityLabel("Export progress")
                Text("\(Int((fraction * 100).rounded()))%")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
                    .accessibilityHidden(true)
                // Esc also cancels: the window handles it, so the button takes no key equivalent.
                Button("Cancel") {
                    onCancel()
                }
            }
            .padding(24)
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
        }
    }
}
