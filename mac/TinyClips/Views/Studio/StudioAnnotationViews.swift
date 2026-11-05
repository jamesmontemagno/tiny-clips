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

/// The Annotate panel of the inspector: stepping through the annotations, the list they are drawn
/// in, and everything about the selected one without a pointer.
struct StudioAnnotationInspectorSection: View {
    @ObservedObject var viewModel: StudioViewModel
    @FocusState private var isTextFocused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            navigationRow
            hint
            layerList
        }
        selectedControls
    }

    private var navigationRow: some View {
        HStack(spacing: 8) {
            Button {
                viewModel.showPreviousAnnotation()
            } label: {
                Image(systemName: "chevron.left")
            }
            .disabled(!viewModel.canSelectPreviousAnnotation)
            .accessibilityLabel("Previous annotation")
            .help("Previous annotation")

            VStack(alignment: .leading, spacing: 2) {
                Text(selectionTitle)
                if let annotation = viewModel.selectedAnnotation {
                    Text(StudioEditorModel.annotationDescription(annotation))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            Button {
                viewModel.showNextAnnotation()
            } label: {
                Image(systemName: "chevron.right")
            }
            .disabled(!viewModel.canSelectNextAnnotation)
            .accessibilityLabel("Next annotation")
            .help("Next annotation")
        }
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
            }
            .frame(maxHeight: 120)
            .fixedSize(horizontal: false, vertical: viewModel.annotations.count <= 4)
            .accessibilityLabel("Annotations, top first")
        }
    }

    private func layerRow(index: Int, annotation: StudioAnnotation) -> some View {
        let isSelected = viewModel.selectedAnnotationIndex == index
        return Button {
            viewModel.selectAnnotationInView(index)
        } label: {
            Label(StudioEditorModel.annotationLabel(annotation), systemImage: StudioAnnotationLane.symbol(for: annotation.kind))
                .lineLimit(1)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 6)
                .padding(.vertical, 3)
                .background(isSelected ? Color.accentColor.opacity(0.25) : Color.clear, in: RoundedRectangle(cornerRadius: 4))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(StudioEditorModel.annotationDescription(annotation))
        .accessibilityValue(isSelected ? "Selected" : "Not selected")
    }

    @ViewBuilder
    private var selectedControls: some View {
        if let index = viewModel.selectedAnnotationIndex, let annotation = viewModel.selectedAnnotation {
            let isKnown = [StudioAnnotationKind.arrow, StudioAnnotationKind.callout, StudioAnnotationKind.text].contains(annotation.kind)
            if !isKnown {
                note(StudioEditorModel.annotationNotDrawnNote)
            }
            if annotation.kind == StudioAnnotationKind.callout || annotation.kind == StudioAnnotationKind.text {
                textField
            }
            if isKnown {
                appearance(annotation)
                position(annotation)
            }
            timing(annotation)
            HStack(spacing: 8) {
                Button("Bring Forward", systemImage: StudioSymbol.bringForward) {
                    viewModel.bringSelectedAnnotationForward()
                }
                .disabled(index + 1 >= viewModel.annotations.count)
                .help("Draw this annotation over the one above it in the list")
                Button("Send Back", systemImage: StudioSymbol.sendBack) {
                    viewModel.sendSelectedAnnotationBack()
                }
                .disabled(index == 0)
                .help("Draw this annotation under the one below it in the list")
            }
            Button("Delete Annotation", systemImage: StudioSymbol.delete, role: .destructive) {
                viewModel.removeSelectedAnnotation()
            }
            .help("Delete this annotation (Delete)")
        }
    }

    private var textField: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Text")
            // A field of its own for each annotation: what one held can't be written into the next.
            TextField("", text: textBinding, axis: .vertical)
                .id(viewModel.selectedAnnotationIndex)
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
        VStack(alignment: .leading, spacing: 8) {
            Picker("Style", selection: Binding(get: { annotation.style }, set: { viewModel.setSelectedAnnotationStyle($0) })) {
                Text("Red").tag(StudioAnnotationStyle.red)
                Text("Blue").tag(StudioAnnotationStyle.blue)
                Text("Yellow").tag(StudioAnnotationStyle.yellow)
                Text("White").tag(StudioAnnotationStyle.white)
                Text("Dark").tag(StudioAnnotationStyle.dark)
            }
            .help("The color of this annotation")
            Picker("Size", selection: Binding(get: { annotation.size }, set: { viewModel.setSelectedAnnotationSize($0) })) {
                Text("Small").tag(StudioAnnotationSize.small)
                Text("Medium").tag(StudioAnnotationSize.medium)
                Text("Large").tag(StudioAnnotationSize.large)
            }
            .pickerStyle(.segmented)
            Picker("Attach to", selection: Binding(get: { annotation.attach }, set: { viewModel.setSelectedAnnotationAttach($0) })) {
                Text("Screen").tag(StudioAnnotationAttach.screen)
                Text("Frame").tag(StudioAnnotationAttach.frame)
            }
            .pickerStyle(.segmented)
            .help("Screen follows zooms and crops. Frame stays in one place in the video.")
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

    private func slider(_ title: String, _ value: Double, _ range: ClosedRange<Double>, _ onChange: @escaping (Double) -> Void) -> some View {
        StudioSliderRow(
            title: title,
            value: value,
            range: range,
            step: 0.01,
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
}
