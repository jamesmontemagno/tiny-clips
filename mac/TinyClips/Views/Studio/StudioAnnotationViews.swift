import Foundation
import SwiftUI

// MARK: - Annotation Timeline

/// The annotations as blocks along the recording, in source time, lined up with the other timeline
/// lanes. They may overlap: the selected block is on top, then the later ones, and the panel's list
/// reaches the ones under them. Pressing a block selects its annotation, dragging it moves it, and
/// dragging one of its ends changes when it starts or stops.
struct StudioAnnotationLane: View {
    @ObservedObject var viewModel: StudioViewModel
    @State private var drag: AnnotationDrag?
    @State private var isTrackDragging = false
    @State private var hoveredIndex: Int?

    private static let space = "studioAnnotationLane"

    private struct AnnotationDrag {
        var index: Int
        var part: StudioLaneBlockPart

        /// When the annotation started and ended as the press began. The drag is measured from
        /// these, so it does not add up rounding from one step to the next.
        var start: Double
        var end: Double
        var hasMoved = false
    }

    var body: some View {
        GeometryReader { proxy in
            let duration = max(viewModel.editor?.sourceDuration ?? 0, 0.0001)
            let width = proxy.size.width
            let usable = max(1, width - StudioTimelineMetrics.edgeInset * 2)

            ZStack(alignment: .topLeading) {
                track(usable: usable, duration: duration)
                blocks(width: width, height: proxy.size.height, usable: usable, duration: duration)
                playheadLine(usable: usable, duration: duration)
            }
            .coordinateSpace(.named(Self.space))
        }
    }

    private func track(usable: CGFloat, duration: Double) -> some View {
        RoundedRectangle(cornerRadius: 5)
            .fill(Color.primary.opacity(0.06))
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0, coordinateSpace: .named(Self.space))
                    .onChanged { value in
                        if !isTrackDragging {
                            isTrackDragging = true
                            viewModel.selectNothing()
                        }
                        viewModel.scrub(to: time(at: value.location.x, usable: usable, duration: duration))
                    }
                    .onEnded { _ in
                        isTrackDragging = false
                    }
            )
            .accessibilityHidden(true)
    }

    private func blocks(width: CGFloat, height: CGFloat, usable: CGFloat, duration: Double) -> some View {
        ForEach(Array(viewModel.annotations.enumerated()), id: \.offset) { index, annotation in
            let x0 = xPosition(for: annotation.start, usable: usable, duration: duration)
            let x1 = xPosition(for: annotation.end, usable: usable, duration: duration)
            let drawnWidth = max(10, x1 - x0)
            let x = min(max((x0 + x1) / 2 - drawnWidth / 2, 0), max(0, width - drawnWidth))
            let isSelected = viewModel.selectedAnnotationIndex == index
            let blockHeight = max(0, height - 4)
            // A narrow block that is selected has its handles outside its ends, and is that much
            // wider to press.
            let outset = CGFloat(StudioEditorModel.laneHandleOutset(blockWidth: Double(drawnWidth), isSelected: isSelected))
            ZStack {
                block(index: index, annotation: annotation, width: drawnWidth, height: blockHeight)
                StudioLaneBlockHandles(
                    blockWidth: drawnWidth,
                    height: blockHeight,
                    fill: Color.teal.opacity(0.9),
                    foreground: isSelected ? Color.black : Color.primary,
                    isSelected: isSelected,
                    isHovering: hoveredIndex == index
                )
            }
            .frame(width: drawnWidth + outset * 2, height: blockHeight)
            .onHover { hovering in
                if hovering {
                    hoveredIndex = index
                } else if hoveredIndex == index {
                    hoveredIndex = nil
                }
            }
            .offset(x: x - outset, y: 2)
            .zIndex(isSelected ? Double(viewModel.annotations.count) : Double(index))
            .gesture(blockGesture(index: index, annotation: annotation, x: x, width: drawnWidth, usable: usable, duration: duration))
        }
    }

    private func block(index: Int, annotation: StudioAnnotation, width: CGFloat, height: CGFloat) -> some View {
        let isSelected = viewModel.selectedAnnotationIndex == index
        let foreground = isSelected ? Color.black : Color.primary

        return RoundedRectangle(cornerRadius: 4)
            .fill(Color.teal.opacity(isSelected ? 0.9 : 0.35))
            .overlay {
                if isSelected {
                    RoundedRectangle(cornerRadius: 4)
                        .strokeBorder(Color.primary.opacity(0.8), lineWidth: 1)
                }
            }
            .frame(width: width, height: height)
            .overlay {
                HStack(spacing: 4) {
                    if width >= 22 {
                        Image(systemName: Self.symbol(for: annotation.kind))
                    }
                    if width >= 56 {
                        Text(StudioEditorModel.annotationLabel(annotation))
                            .font(.caption2.weight(.semibold))
                            .lineLimit(1)
                    }
                }
                .padding(.horizontal, 8)
                .foregroundStyle(foreground)
                .allowsHitTesting(false)
            }
            .contentShape(Rectangle())
            .help(
                StudioEditorModel.laneBlockHasInsideHandles(blockWidth: Double(width)) || isSelected
                    ? "Drag to move this annotation. Drag the handle at either end to change when it starts or stops."
                    : "Drag to move this annotation. Select it to show the handles that change when it starts or stops."
            )
            .accessibilityElement()
            .accessibilityLabel(StudioEditorModel.annotationDescription(annotation))
            .accessibilityValue(isSelected ? "Selected" : "Not selected")
            .accessibilityAddTraits(.isButton)
            .accessibilityAction {
                _ = viewModel.selectAndShowAnnotation(index)
            }
    }

    static func symbol(for kind: String) -> String {
        switch kind {
        case StudioAnnotationKind.arrow: return StudioSymbol.addArrow
        case StudioAnnotationKind.callout: return StudioSymbol.addCallout
        case StudioAnnotationKind.text: return StudioSymbol.addText
        default: return StudioInspectorPanel.annotate.symbolName
        }
    }

    private func blockGesture(
        index: Int,
        annotation: StudioAnnotation,
        x: CGFloat,
        width: CGFloat,
        usable: CGFloat,
        duration: Double
    ) -> some Gesture {
        DragGesture(minimumDistance: 0, coordinateSpace: .named(Self.space))
            .onChanged { value in
                if drag == nil {
                    // Read before the press selects the block: a narrow block has handles only
                    // once it is selected.
                    let part = StudioEditorModel.laneBlockPart(
                        x: Double(value.startLocation.x - x),
                        blockWidth: Double(width),
                        isSelected: viewModel.selectedAnnotationIndex == index
                    )
                    drag = AnnotationDrag(index: index, part: part, start: annotation.start, end: annotation.end)
                    viewModel.selectAnnotation(index)
                    viewModel.scrub(to: time(at: value.location.x, usable: usable, duration: duration))
                }

                guard var current = drag else { return }
                if !current.hasMoved, abs(value.translation.width) >= 3 {
                    current.hasMoved = true
                    viewModel.beginGesture()
                }
                guard current.hasMoved else {
                    drag = current
                    return
                }

                let delta = Double(value.translation.width / usable) * duration
                switch current.part {
                case .body:
                    viewModel.moveAnnotation(at: current.index, to: current.start + delta)
                case .start:
                    viewModel.setAnnotationStart(at: current.index, to: current.start + delta)
                case .end:
                    viewModel.setAnnotationEnd(at: current.index, to: current.end + delta)
                }
                drag = current
            }
            .onEnded { _ in
                if drag?.hasMoved == true {
                    viewModel.endGesture()
                }
                drag = nil
            }
    }

    private func playheadLine(usable: CGFloat, duration: Double) -> some View {
        Rectangle()
            .fill(Color.primary.opacity(0.5))
            .frame(width: 1)
            .offset(x: xPosition(for: viewModel.playhead, usable: usable, duration: duration))
            .allowsHitTesting(false)
            .accessibilityHidden(true)
    }

    private func xPosition(for time: Double, usable: CGFloat, duration: Double) -> CGFloat {
        let fraction = min(max(time / duration, 0), 1)
        return StudioTimelineMetrics.edgeInset + CGFloat(fraction) * usable
    }

    private func time(at x: CGFloat, usable: CGFloat, duration: Double) -> Double {
        let fraction = Double((x - StudioTimelineMetrics.edgeInset) / usable)
        return min(max(fraction, 0), 1) * duration
    }
}

// MARK: - Annotation Inspector

/// The Annotate panel of the inspector: the tools that add an annotation, the list they are drawn
/// in, and the selected one's look. What is rarely changed is under Advanced.
struct StudioAnnotationInspectorSection: View {
    @ObservedObject var viewModel: StudioViewModel
    @FocusState private var isTextFocused: Bool
    @AppStorage("studioAnnotationAdvancedExpanded") private var isAdvancedExpanded = false

    var body: some View {
        toolGrid
        VStack(alignment: .leading, spacing: 8) {
            listHeader
            hint
            layerList
        }
        selectedControls
    }

    // MARK: Tools

    private var toolGrid: some View {
        HStack(spacing: 6) {
            StudioAnnotationToolTile(title: "Arrow", name: "Add arrow", help: "Add an arrow at the playhead (A)", symbol: StudioSymbol.addArrow) {
                viewModel.addAnnotationAtPlayhead(kind: StudioAnnotationKind.arrow)
            }
            StudioAnnotationToolTile(title: "Callout", name: "Add callout", help: "Add a callout at the playhead (C)", symbol: StudioSymbol.addCallout) {
                viewModel.addAnnotationAtPlayhead(kind: StudioAnnotationKind.callout)
            }
            StudioAnnotationToolTile(title: "Text", name: "Add text", help: "Add text at the playhead (T)", symbol: StudioSymbol.addText) {
                viewModel.addAnnotationAtPlayhead(kind: StudioAnnotationKind.text)
            }
        }
        .disabled(!viewModel.canAddAnnotation)
    }

    // MARK: List

    private var listHeader: some View {
        HStack(spacing: 2) {
            Text(selectionTitle)
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
                .accessibilityAddTraits(.isHeader)
            Spacer(minLength: 8)
            if let index = viewModel.selectedAnnotationIndex, viewModel.selectedAnnotation != nil {
                headerButton("Bring forward", symbol: StudioSymbol.bringForward, help: "Draw this annotation over the one above it in the list", isEnabled: index + 1 < viewModel.annotations.count) {
                    viewModel.bringSelectedAnnotationForward()
                }
                headerButton("Send back", symbol: StudioSymbol.sendBack, help: "Draw this annotation under the one below it in the list", isEnabled: index > 0) {
                    viewModel.sendSelectedAnnotationBack()
                }
                Divider().frame(height: 14).padding(.horizontal, 2)
            }
            headerButton("Previous annotation", symbol: "chevron.left", help: "Previous annotation", isEnabled: viewModel.canSelectPreviousAnnotation) {
                viewModel.showPreviousAnnotation()
            }
            headerButton("Next annotation", symbol: "chevron.right", help: "Next annotation", isEnabled: viewModel.canSelectNextAnnotation) {
                viewModel.showNextAnnotation()
            }
        }
    }

    private func headerButton(_ name: String, symbol: String, help: String, isEnabled: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 11, weight: .medium))
                .frame(width: 22, height: 22)
                .contentShape(Rectangle())
        }
        .buttonStyle(.borderless)
        .disabled(!isEnabled)
        .accessibilityLabel(name)
        .help(help)
    }

    @ViewBuilder
    private var hint: some View {
        if viewModel.annotations.isEmpty {
            note(StudioEditorModel.annotationEmptyHint)
        } else if viewModel.selectedAnnotation == nil {
            note(StudioEditorModel.selectAnnotationHint)
        }
    }

    private func note(_ text: String) -> some View {
        Text(text)
            .font(.caption)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
    }

    /// The top of the picture first, as the order they are drawn in reads from the front.
    @ViewBuilder
    private var layerList: some View {
        if !viewModel.annotations.isEmpty {
            ScrollView {
                VStack(spacing: 2) {
                    ForEach(Array(viewModel.annotations.enumerated().reversed()), id: \.offset) { index, annotation in
                        layerRow(index: index, annotation: annotation)
                    }
                }
                .padding(3)
            }
            .frame(maxHeight: 132)
            .fixedSize(horizontal: false, vertical: viewModel.annotations.count <= 4)
            .background(Color.primary.opacity(0.04), in: RoundedRectangle(cornerRadius: 8))
            .accessibilityLabel("Annotations, top first")
        }
    }

    private func layerRow(index: Int, annotation: StudioAnnotation) -> some View {
        let isSelected = viewModel.selectedAnnotationIndex == index
        let colors = StudioAnnotationGeometry.effectiveColors(annotation)
        return Button {
            viewModel.selectAnnotationInView(index)
        } label: {
            HStack(spacing: 8) {
                Circle()
                    .fill(Self.color(colors.fill))
                    .overlay(Circle().strokeBorder(Color.primary.opacity(0.25), lineWidth: 0.5))
                    .frame(width: 10, height: 10)
                Image(systemName: StudioAnnotationLane.symbol(for: annotation.kind))
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .frame(width: 14)
                Text(StudioEditorModel.annotationLabel(annotation))
                    .lineLimit(1)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 7)
            .padding(.vertical, 5)
            .background(isSelected ? Color.accentColor.opacity(0.22) : Color.clear, in: RoundedRectangle(cornerRadius: 6))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(StudioEditorModel.annotationDescription(annotation))
        .accessibilityValue(isSelected ? "Selected" : "Not selected")
    }

    // MARK: Selected annotation

    @ViewBuilder
    private var selectedControls: some View {
        if viewModel.selectedAnnotationIndex != nil, let annotation = viewModel.selectedAnnotation {
            let isKnown = [StudioAnnotationKind.arrow, StudioAnnotationKind.callout, StudioAnnotationKind.text].contains(annotation.kind)
            if !isKnown {
                note(StudioEditorModel.annotationNotDrawnNote)
            }
            if annotation.kind == StudioAnnotationKind.callout || annotation.kind == StudioAnnotationKind.text {
                textField
            }
            if isKnown {
                appearance(annotation)
            }
            advanced(annotation, isKnown: isKnown)
            Button("Delete Annotation", systemImage: StudioSymbol.delete, role: .destructive) {
                viewModel.removeSelectedAnnotation()
            }
            .help("Delete this annotation (Delete)")
        }
    }

    private var textField: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Text")
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
            // A field of its own for each annotation: what one held can't be written into the next.
            TextField("", text: textBinding, axis: .vertical)
                .id(viewModel.selectedAnnotationIndex)
                .textFieldStyle(.roundedBorder)
                .lineLimit(1...4)
                .focused($isTextFocused)
                .accessibilityLabel("Annotation text")
                .onChange(of: isTextFocused) { _, isFocused in
                    if !isFocused { viewModel.endAnnotationTyping() }
                }
        }
    }

    private var textBinding: Binding<String> {
        let index = viewModel.selectedAnnotationIndex
        return Binding(
            get: { viewModel.selectedAnnotation?.text ?? "" },
            set: { text in
                if viewModel.selectedAnnotationIndex == index {
                    viewModel.setSelectedAnnotationText(text)
                }
            }
        )
    }

    private func appearance(_ annotation: StudioAnnotation) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            styleChips(annotation)
            let colors = StudioAnnotationGeometry.effectiveColors(annotation)
            SwatchColorPicker(
                label: annotation.kind == StudioAnnotationKind.arrow ? "Color" : "Fill",
                color: Binding(get: { Self.color(colors.fill) }, set: { viewModel.setSelectedAnnotationFill($0) }),
                supportsOpacity: false,
                colorName: Self.styleName(colors.fill),
                onClosed: { viewModel.endAnnotationColorChoice() }
            )
            if annotation.kind != StudioAnnotationKind.arrow {
                SwatchColorPicker(
                    label: "Text color",
                    color: Binding(get: { Self.color(colors.text) }, set: { viewModel.setSelectedAnnotationTextColor($0) }),
                    supportsOpacity: false,
                    colorName: Self.styleName(colors.text),
                    onClosed: { viewModel.endAnnotationColorChoice() }
                )
            }
            slider("Size", annotation.scale, StudioAnnotationGeometry.minimumScale...StudioAnnotationGeometry.maximumScale, step: 0.05) {
                viewModel.setSelectedAnnotationScale($0)
            }
        }
    }

    /// The looks to start from, each showing what it gives. Choosing one takes back chosen colors.
    private func styleChips(_ annotation: StudioAnnotation) -> some View {
        let isCustom = StudioAnnotationGeometry.customColor(annotation.fill) != nil
            || StudioAnnotationGeometry.customColor(annotation.textColor) != nil
        return VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text("Style")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.secondary)
                Spacer()
                if isCustom {
                    Text("Custom")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            HStack(spacing: 6) {
                ForEach(Self.styles, id: \.style) { entry in
                    styleChip(entry.style, name: entry.name, kind: annotation.kind, isSelected: !isCustom && annotation.style == entry.style)
                }
            }
        }
    }

    private static let styles: [(style: StudioAnnotationStyle, name: String)] = [
        (.red, "Red"), (.blue, "Blue"), (.yellow, "Yellow"), (.white, "White"), (.dark, "Dark"),
    ]

    private func styleChip(_ style: StudioAnnotationStyle, name: String, kind: String, isSelected: Bool) -> some View {
        let colors = StudioAnnotationGeometry.colors(style)
        return Button {
            viewModel.setSelectedAnnotationStyle(style)
        } label: {
            ZStack {
                RoundedRectangle(cornerRadius: 6).fill(Self.color(colors.fill))
                if kind == StudioAnnotationKind.arrow {
                    Image(systemName: "arrow.up.right")
                        .font(.system(size: 11, weight: .bold))
                        .foregroundStyle(Self.color(colors.text))
                } else {
                    Text("Aa")
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(Self.color(colors.text))
                }
            }
            .frame(maxWidth: .infinity, minHeight: 28, maxHeight: 28)
            .overlay {
                RoundedRectangle(cornerRadius: 6)
                    .strokeBorder(isSelected ? Color.accentColor : Color.primary.opacity(0.18), lineWidth: isSelected ? 2 : 1)
            }
            .contentShape(RoundedRectangle(cornerRadius: 6))
        }
        .buttonStyle(.plain)
        .help("\(name) style")
        .accessibilityLabel("\(name) style")
        .accessibilityValue(isSelected ? "Selected" : "Not selected")
    }

    /// What most annotations never need: dragging on the preview does the same as Position, and the
    /// lane the same as Timing. These are also how the keyboard does both.
    private func advanced(_ annotation: StudioAnnotation, isKnown: Bool) -> some View {
        DisclosureGroup("Advanced", isExpanded: $isAdvancedExpanded) {
            VStack(alignment: .leading, spacing: 12) {
                if isKnown {
                    Picker("Attach to", selection: Binding(get: { annotation.attach }, set: { viewModel.setSelectedAnnotationAttach($0) })) {
                        Text("Screen").tag(StudioAnnotationAttach.screen)
                        Text("Frame").tag(StudioAnnotationAttach.frame)
                    }
                    .pickerStyle(.segmented)
                    .help("Screen follows zooms and crops. Frame stays in one place in the video.")
                    position(annotation)
                }
                timing(annotation)
            }
            .padding(.top, 8)
        }
    }

    private func position(_ annotation: StudioAnnotation) -> some View {
        StudioInspectorSection(title: "Position") {
            slider("Horizontal", annotation.x, 0...1) { viewModel.setSelectedAnnotationPoint(x: $0, y: annotation.y) }
            slider("Vertical", annotation.y, 0...1) { viewModel.setSelectedAnnotationPoint(x: annotation.x, y: $0) }
            if annotation.kind != StudioAnnotationKind.text {
                let name = annotation.kind == StudioAnnotationKind.callout ? "Box" : "Tail"
                slider("\(name) horizontal", annotation.offsetX, -1...1) { viewModel.setSelectedAnnotationOffset(x: $0, y: annotation.offsetY) }
                slider("\(name) vertical", annotation.offsetY, -1...1) { viewModel.setSelectedAnnotationOffset(x: annotation.offsetX, y: $0) }
            }
        }
    }

    private func slider(_ title: String, _ value: Double, _ range: ClosedRange<Double>, step: Double = 0.01, _ onChange: @escaping (Double) -> Void) -> some View {
        StudioSliderRow(
            title: title,
            value: value,
            range: range,
            step: step,
            valueText: "\(Int((value * 100).rounded()))%",
            onChange: onChange,
            onEditingChanged: { isEditing in
                if isEditing {
                    viewModel.beginGesture()
                } else {
                    viewModel.endGesture()
                }
            }
        )
    }

    private func timing(_ annotation: StudioAnnotation) -> some View {
        StudioInspectorSection(title: "Timing") {
            timeRow(
                title: "Start",
                time: annotation.start,
                nudge: { viewModel.nudgeSelectedAnnotationStart(by: $0) },
                setAtPlayhead: { viewModel.setSelectedAnnotationStartAtPlayhead() }
            )
            timeRow(
                title: "End",
                time: annotation.end,
                nudge: { viewModel.nudgeSelectedAnnotationEnd(by: $0) },
                setAtPlayhead: { viewModel.setSelectedAnnotationEndAtPlayhead() }
            )
        }
    }

    private func timeRow(title: String, time: Double, nudge: @escaping (Double) -> Void, setAtPlayhead: @escaping () -> Void) -> some View {
        HStack(spacing: 8) {
            Text(title)
            Spacer()
            Text(StudioEditorModel.secondsText(time))
                .font(.caption.monospacedDigit())
                .foregroundStyle(.secondary)
            Stepper("", onIncrement: { nudge(0.1) }, onDecrement: { nudge(-0.1) })
                .labelsHidden()
                .accessibilityLabel("Annotation \(title.lowercased())")
                .accessibilityValue(StudioEditorModel.secondsText(time))
            Button("At Playhead", action: setAtPlayhead)
                .controlSize(.small)
                .help("\(title) this annotation at the playhead")
                .accessibilityLabel("\(title) annotation at playhead")
        }
    }

    private var selectionTitle: String {
        if let index = viewModel.selectedAnnotationIndex, viewModel.selectedAnnotation != nil {
            return StudioEditorModel.annotationPositionText(index: index, count: viewModel.annotations.count)
        }
        switch viewModel.annotations.count {
        case 0: return "No annotations yet"
        case 1: return "1 annotation"
        default: return "\(viewModel.annotations.count) annotations"
        }
    }

    /// The style a color comes from, as the pickers call it. Nil for one chosen by hand.
    private static func styleName(_ hex: String) -> String? {
        styles.first { StudioAnnotationGeometry.colors($0.style).fill == hex }?.name
    }

    /// `#RRGGBB` as sRGB, which is what the format says it is, so a picker shows what is drawn.
    static func color(_ hex: String) -> Color {
        guard let value = Int(hex.dropFirst(), radix: 16) else { return .clear }
        return Color(
            .sRGB,
            red: Double((value >> 16) & 0xFF) / 255,
            green: Double((value >> 8) & 0xFF) / 255,
            blue: Double(value & 0xFF) / 255,
            opacity: 1
        )
    }
}

/// One of the tools at the top of the Annotate panel, in the look of the screenshot editor's.
private struct StudioAnnotationToolTile: View {
    let title: String
    let name: String
    let help: String
    let symbol: String
    let action: () -> Void

    @State private var isHovering = false
    @Environment(\.isEnabled) private var isEnabled

    var body: some View {
        Button(action: action) {
            VStack(spacing: 4) {
                Image(systemName: symbol)
                    .font(.system(size: 15, weight: .medium))
                Text(title)
                    .font(.caption)
                    .lineLimit(1)
            }
            .frame(maxWidth: .infinity, minHeight: 50)
            .background(isHovering && isEnabled ? Color.accentColor.opacity(0.2) : Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 8))
            .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(Color.primary.opacity(0.08)))
            .contentShape(RoundedRectangle(cornerRadius: 8))
        }
        .buttonStyle(.plain)
        .onHover { isHovering = $0 }
        .accessibilityLabel(name)
        .help(help)
    }
}

// MARK: - Annotations on the preview

/// One annotation on the picture, in canvas pixels, for a pointer to take hold of.
struct StudioAnnotationHandle: Equatable, Identifiable {
    var index: Int
    var kind: String
    /// What an arrow or a callout points at, or the center of a text box.
    var tip: StudioAnnotationPoint
    var tail: StudioAnnotationPoint?
    var stroke: Double = 0
    var box: StudioRect?
    var radius: Double = 0

    var id: Int { index }
}

/// Over the preview: pressing an annotation selects it and dragging moves it. The selected one
/// shows its handles. The sliders under Advanced do the same without a pointer, so VoiceOver is not
/// shown any of this, as it isn't shown the handle over the camera.
struct StudioAnnotationPreviewLayer: View {
    @ObservedObject var viewModel: StudioViewModel
    let geometry: StudioEditorCanvasGeometry
    let canvasSize: CGSize
    let space: String

    @State private var drag: Drag?
    @State private var guides = Guides()

    private struct Drag {
        var index: Int
        var start: StudioAnnotation
        var drawnBoxCenter: StudioAnnotationPoint?
        var isMoving = false
    }

    private struct Guides {
        var vertical = false
        var horizontal = false
    }

    private static let handleSize: CGFloat = 12
    private static let shortestHitWidth: CGFloat = 14

    var body: some View {
        let handles = viewModel.annotationHandles(canvasSize: canvasSize)
        ZStack(alignment: .topLeading) {
            ForEach(handles) { handle in
                hitArea(handle)
            }
            if let selected = handles.first(where: { $0.index == viewModel.selectedAnnotationIndex }) {
                selection(selected)
            }
            guideLines
        }
        .frame(width: geometry.viewSize.width, height: geometry.viewSize.height, alignment: .topLeading)
        .accessibilityHidden(true)
        // A handle can go during a drag: Space and the arrow keys still move the playhead. A gesture
        // whose view has gone never ends, so what it opened is closed here.
        .onChange(of: handles.map(\.index)) { _, indices in
            if let drag, !indices.contains(drag.index) { endDrag() }
        }
        .onDisappear { endDrag() }
    }

    private func endDrag() {
        if drag?.isMoving == true {
            viewModel.endGesture()
        }
        drag = nil
        guides = Guides()
    }

    private func view(_ point: StudioAnnotationPoint) -> CGPoint {
        geometry.viewPoint(fromCanvasPoint: CGPoint(x: point.x, y: point.y))
    }

    private func viewRect(_ rect: StudioRect) -> CGRect {
        let origin = geometry.viewPoint(fromCanvasPoint: CGPoint(x: rect.x, y: rect.y))
        let scale = max(geometry.canvasPixelsPerViewPoint, 0.0001)
        return CGRect(x: origin.x, y: origin.y, width: CGFloat(rect.width / scale), height: CGFloat(rect.height / scale))
    }

    /// Everything but the handles of the selected one: the shaft of an arrow or a whole box.
    @ViewBuilder
    private func hitArea(_ handle: StudioAnnotationHandle) -> some View {
        if let tail = handle.tail {
            let scale = max(geometry.canvasPixelsPerViewPoint, 0.0001)
            let width = max(Self.shortestHitWidth, CGFloat(handle.stroke * 3 / scale))
            let line = Path { path in
                path.move(to: view(tail))
                path.addLine(to: view(handle.tip))
            }.strokedPath(StrokeStyle(lineWidth: width, lineCap: .round))
            Color.clear
                .contentShape(line)
                .pointerStyle(.grabIdle)
                .gesture(dragGesture(handle, part: .body))
        } else if let box = handle.box {
            let rect = viewRect(box)
            Color.clear
                .contentShape(Rectangle())
                .frame(width: rect.width, height: rect.height)
                .offset(x: rect.minX, y: rect.minY)
                .pointerStyle(.grabIdle)
                .gesture(dragGesture(handle, part: .box))
        }
    }

    @ViewBuilder
    private func selection(_ handle: StudioAnnotationHandle) -> some View {
        if let box = handle.box {
            let rect = viewRect(box)
            RoundedRectangle(cornerRadius: max(2, CGFloat(handle.radius / max(geometry.canvasPixelsPerViewPoint, 0.0001))) + 2)
                .strokeBorder(Color.accentColor, lineWidth: 1.5)
                .frame(width: rect.width + 4, height: rect.height + 4)
                .offset(x: rect.minX - 2, y: rect.minY - 2)
                .allowsHitTesting(false)
        }
        if handle.kind != StudioAnnotationKind.text {
            dot(handle, part: .tip, at: handle.tip)
        }
        if let tail = handle.tail {
            dot(handle, part: .tail, at: tail)
        }
    }

    private func dot(_ handle: StudioAnnotationHandle, part: StudioAnnotationPart, at point: StudioAnnotationPoint) -> some View {
        let center = view(point)
        let target = Self.handleSize + 10
        return Circle()
            .fill(Color.white)
            .overlay(Circle().strokeBorder(Color.accentColor, lineWidth: 2))
            .frame(width: Self.handleSize, height: Self.handleSize)
            .shadow(color: .black.opacity(0.35), radius: 2, y: 1)
            .frame(width: target, height: target)
            .contentShape(Circle())
            .offset(x: center.x - target / 2, y: center.y - target / 2)
            .pointerStyle(.grabIdle)
            .gesture(dragGesture(handle, part: part))
    }

    /// The center lines of the canvas, while what is dragged has settled on one.
    @ViewBuilder
    private var guideLines: some View {
        let canvas = geometry.canvasRectInView
        if guides.vertical {
            Rectangle()
                .fill(Color.accentColor)
                .frame(width: 1, height: CGFloat(canvas.height))
                .offset(x: CGFloat(canvas.x + canvas.width / 2), y: CGFloat(canvas.y))
                .allowsHitTesting(false)
        }
        if guides.horizontal {
            Rectangle()
                .fill(Color.accentColor)
                .frame(width: CGFloat(canvas.width), height: 1)
                .offset(x: CGFloat(canvas.x), y: CGFloat(canvas.y + canvas.height / 2))
                .allowsHitTesting(false)
        }
    }

    /// A press selects. Moving a little further starts a drag, which is one undo step.
    private func dragGesture(_ handle: StudioAnnotationHandle, part: StudioAnnotationPart) -> some Gesture {
        DragGesture(minimumDistance: 0, coordinateSpace: .named(space))
            .onChanged { value in
                if drag == nil, viewModel.annotations.indices.contains(handle.index) {
                    viewModel.selectAnnotation(handle.index)
                    drag = Drag(
                        index: handle.index,
                        start: viewModel.annotations[handle.index],
                        drawnBoxCenter: handle.box.map { StudioAnnotationPoint(x: $0.x + $0.width / 2, y: $0.y + $0.height / 2) }
                    )
                }
                guard var current = drag else { return }
                if !current.isMoving {
                    guard hypot(value.translation.width, value.translation.height) >= 2 else { return }
                    current.isMoving = true
                    drag = current
                    viewModel.beginGesture()
                }
                let scale = geometry.canvasPixelsPerViewPoint
                if let move = viewModel.moveAnnotation(
                    at: current.index,
                    from: current.start,
                    part: part,
                    byX: Double(value.translation.width) * scale,
                    byY: Double(value.translation.height) * scale,
                    drawnBoxCenter: current.drawnBoxCenter,
                    canvasSize: canvasSize
                ) {
                    guides = Guides(vertical: move.isOnVerticalCenter, horizontal: move.isOnHorizontalCenter)
                }
            }
            .onEnded { _ in endDrag() }
    }
}
