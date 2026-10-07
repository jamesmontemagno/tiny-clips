import Foundation
import SwiftUI

// MARK: - Zoom Timeline

/// The zooms as blocks along the recording, in source time, lined up with the trim bar under it.
/// Pressing a block selects its zoom, dragging it moves the zoom, and dragging one of its ends
/// changes when the zoom starts or stops. The Zoom section of the inspector does the same without
/// a pointer.
struct StudioZoomLane: View {
    @ObservedObject var viewModel: StudioViewModel
    @State private var drag: ZoomDrag?
    @State private var isTrackDragging = false
    @State private var hoveredIndex: Int?

    private static let space = "studioZoomLane"

    /// A press on a block, which becomes a drag once the pointer has moved.
    private struct ZoomDrag {
        /// Where the zoom is in the list. An edit says where it is afterwards.
        var index: Int
        var part: StudioLaneBlockPart

        /// The zoom's start and end when the press began. The drag is measured from these, so it
        /// does not add up rounding from one step to the next.
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
                emptyText
                zoomBlocks(width: width, height: proxy.size.height, usable: usable, duration: duration)
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

    @ViewBuilder
    private var emptyText: some View {
        if viewModel.zooms.isEmpty {
            Text("No zooms. Press Z to add one at the playhead.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .allowsHitTesting(false)
        }
    }

    private func zoomBlocks(width: CGFloat, height: CGFloat, usable: CGFloat, duration: Double) -> some View {
        ForEach(Array(viewModel.zooms.enumerated()), id: \.offset) { index, zoom in
            let x0 = xPosition(for: zoom.start, usable: usable, duration: duration)
            let x1 = xPosition(for: zoom.end, usable: usable, duration: duration)
            let drawnWidth = max(10, x1 - x0)
            let x = blockOffset(center: (x0 + x1) / 2, width: drawnWidth, laneWidth: width)
            let isSelected = viewModel.selectedZoomIndex == index
            let blockHeight = max(0, height - 4)
            // A narrow block that is selected has its handles outside its ends, and is that
            // much wider to press.
            let outset = CGFloat(StudioEditorModel.laneHandleOutset(blockWidth: Double(drawnWidth), isSelected: isSelected))
            ZStack {
                block(index: index, zoom: zoom, width: drawnWidth, height: blockHeight)
                StudioLaneBlockHandles(
                    blockWidth: drawnWidth,
                    height: blockHeight,
                    fill: Color.accentColor.opacity(0.85),
                    foreground: isSelected ? Color.white : Color.primary,
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
            .zIndex(isSelected ? 1 : 0)
            .gesture(
                blockGesture(index: index, zoom: zoom, x: x, width: drawnWidth, usable: usable, duration: duration)
            )
        }
    }

    private func block(index: Int, zoom: StudioZoom, width: CGFloat, height: CGFloat) -> some View {
        let isSelected = viewModel.selectedZoomIndex == index
        let foreground = isSelected ? Color.white : Color.primary

        return RoundedRectangle(cornerRadius: 4)
            .fill(Color.accentColor.opacity(isSelected ? 0.85 : 0.35))
            .overlay {
                if isSelected {
                    RoundedRectangle(cornerRadius: 4)
                        .strokeBorder(Color.primary.opacity(0.6), lineWidth: 1)
                }
            }
            .frame(width: width, height: height)
            .overlay {
                HStack(spacing: 4) {
                    if width >= 30 {
                        Text(StudioEditorModel.zoomScaleText(zoom.scale))
                            .font(.caption2.weight(.semibold))
                    }
                    if width >= 56, zoom.focus.mode == .cursor {
                        Image(systemName: "cursorarrow")
                    }
                    if width >= 56, zoom.origin == .auto {
                        Image(systemName: "sparkles")
                    }
                }
                .foregroundStyle(foreground)
                .allowsHitTesting(false)
            }
            .contentShape(Rectangle())
            .help(
                StudioEditorModel.laneBlockHasInsideHandles(blockWidth: Double(width)) || isSelected
                    ? "Drag to move this zoom. Drag the handle at either end to change when it starts or stops."
                    : "Drag to move this zoom. Select it to show the handles that change when it starts or stops."
            )
            .accessibilityElement()
            .accessibilityLabel(StudioEditorModel.zoomAccessibilityText(zoom))
            .accessibilityValue(isSelected ? "Selected" : "Not selected")
            .accessibilityAddTraits(.isButton)
            .accessibilityAction {
                _ = viewModel.selectAndShowZoom(index)
            }
    }

    private func blockGesture(
        index: Int,
        zoom: StudioZoom,
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
                        isSelected: viewModel.selectedZoomIndex == index
                    )
                    drag = ZoomDrag(index: index, part: part, start: zoom.start, end: zoom.end)
                    viewModel.selectZoom(index)
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
                let newIndex: Int?
                switch current.part {
                case .body:
                    newIndex = viewModel.moveZoom(at: current.index, to: current.start + delta)
                case .start:
                    newIndex = viewModel.setZoomStart(at: current.index, to: current.start + delta)
                case .end:
                    newIndex = viewModel.setZoomEnd(at: current.index, to: current.end + delta)
                }
                if let newIndex {
                    current.index = newIndex
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

    private func blockOffset(center: CGFloat, width: CGFloat, laneWidth: CGFloat) -> CGFloat {
        min(max(center - width / 2, 0), max(0, laneWidth - width))
    }
}

// MARK: - Zoom Inspector

/// The Zoom panel of the inspector: stepping through the zooms, adding and suggesting them, and
/// every value of the selected one.
struct StudioZoomInspectorSection: View {
    @ObservedObject var viewModel: StudioViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            navigationRow
            actionRow
            clickExplanation
            removeSuggestionsButton
            noSelectionText
        }
        selectedZoomControls
    }

    private var navigationRow: some View {
        HStack(spacing: 8) {
            Button {
                viewModel.showPreviousZoom()
            } label: {
                Image(systemName: "chevron.left")
            }
            .disabled(!canSelectPrevious)
            .accessibilityLabel("Previous zoom")
            .help("Previous zoom")

            VStack(alignment: .leading, spacing: 2) {
                Text(selectionTitle)
                if let detail = selectionDetail {
                    Text(detail)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            Button {
                viewModel.showNextZoom()
            } label: {
                Image(systemName: "chevron.right")
            }
            .disabled(!canSelectNext)
            .accessibilityLabel("Next zoom")
            .help("Next zoom")
        }
    }

    private var actionRow: some View {
        HStack(spacing: 8) {
            Button("Add Zoom") {
                viewModel.addZoomAtPlayhead()
            }
            .disabled(!viewModel.canAddZoomAtPlayhead)
            .help("Add a zoom at the playhead (Z)")

            Button("Suggest Zooms") {
                viewModel.suggestZooms()
            }
            .disabled(!viewModel.canSuggestZooms)
            .help("Add zooms where you clicked")
        }
    }

    @ViewBuilder
    private var clickExplanation: some View {
        if !viewModel.hasClicks {
            Text(StudioEditorModel.noClicksExplanation)
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    @ViewBuilder
    private var removeSuggestionsButton: some View {
        if viewModel.hasSuggestedZooms {
            Button("Remove Suggestions") {
                viewModel.removeZoomSuggestions()
            }
        }
    }

    @ViewBuilder
    private var selectedZoomControls: some View {
        if let index = viewModel.selectedZoomIndex, let zoom = viewModel.selectedZoom {
            StudioSliderRow(
                title: "Zoom level",
                value: min(max(zoom.scale, 1), 5),
                range: 1...5,
                step: 0.1,
                valueText: StudioEditorModel.zoomScaleText(zoom.scale),
                onChange: { viewModel.setSelectedZoomScale($0) },
                onEditingChanged: { gestureChanged($0) }
            )
            StudioInspectorSection(title: "Focus") {
                Picker("Focus", selection: focusModeBinding(zoom: zoom)) {
                    Text("Fixed Point").tag(StudioZoomFocusMode.point)
                    Text("Follow Pointer").tag(StudioZoomFocusMode.cursor)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .accessibilityLabel("Focus")
                .help("Zoom in on one point, or keep the pointer in view as it moves")
                .disabled(!viewModel.hasPointerPositions && zoom.focus.mode == .point)
                pointerExplanation
                focusControls(index: index)
            }
            timingSection(zoom: zoom)
            Button("Delete Zoom", role: .destructive) {
                viewModel.removeSelectedZoom()
            }
            .help("Delete this zoom (Delete)")
        }
    }

    private func timingSection(zoom: StudioZoom) -> some View {
        StudioInspectorSection(title: "Timing") {
            timeRow(
                title: "Start",
                time: zoom.start,
                accessibilityLabel: "Start",
                nudgeForward: { viewModel.nudgeSelectedZoomStart(by: 0.1) },
                nudgeBackward: { viewModel.nudgeSelectedZoomStart(by: -0.1) },
                setAtPlayhead: { viewModel.setSelectedZoomStartAtPlayhead() },
                buttonTitle: "Start at playhead",
                help: "Start this zoom at the playhead"
            )
            timeRow(
                title: "End",
                time: zoom.end,
                accessibilityLabel: "End",
                nudgeForward: { viewModel.nudgeSelectedZoomEnd(by: 0.1) },
                nudgeBackward: { viewModel.nudgeSelectedZoomEnd(by: -0.1) },
                setAtPlayhead: { viewModel.setSelectedZoomEndAtPlayhead() },
                buttonTitle: "End at playhead",
                help: "End this zoom at the playhead"
            )
            StudioSliderRow(
                title: "Zoom-in time",
                value: zoom.easeIn,
                range: 0...3,
                step: 0.1,
                valueText: secondsValueText(zoom.easeIn),
                onChange: { viewModel.setSelectedZoomEaseIn($0) },
                onEditingChanged: { gestureChanged($0) }
            )
            StudioSliderRow(
                title: "Zoom-out time",
                value: zoom.easeOut,
                range: 0...3,
                step: 0.1,
                valueText: secondsValueText(zoom.easeOut),
                onChange: { viewModel.setSelectedZoomEaseOut($0) },
                onEditingChanged: { gestureChanged($0) }
            )
        }
    }

    @ViewBuilder
    private var pointerExplanation: some View {
        if !viewModel.hasPointerPositions {
            Text(StudioEditorModel.noPointerExplanation)
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    @ViewBuilder
    private func focusControls(index: Int) -> some View {
        if let zoom = viewModel.selectedZoom,
           zoom.focus.mode == .point,
           let pad = viewModel.editor?.zoomPad(at: index) {
            StudioFocusPad(
                pad: pad,
                onBegan: { viewModel.beginGesture() },
                onChanged: { x, y in viewModel.setSelectedZoomFocusOnPad(x: x, y: y) },
                onEnded: { viewModel.endGesture() }
            )
            StudioSliderRow(
                title: "Horizontal",
                value: pad.focusX,
                range: 0...1,
                step: 0.01,
                valueText: percentText(pad.focusX),
                onChange: { viewModel.setSelectedZoomFocusOnPad(x: $0, y: pad.focusY) },
                onEditingChanged: { gestureChanged($0) }
            )
            StudioSliderRow(
                title: "Vertical",
                value: pad.focusY,
                range: 0...1,
                step: 0.01,
                valueText: percentText(pad.focusY),
                onChange: { viewModel.setSelectedZoomFocusOnPad(x: pad.focusX, y: $0) },
                onEditingChanged: { gestureChanged($0) }
            )
        }
    }

    private func timeRow(
        title: String,
        time: Double,
        accessibilityLabel: String,
        nudgeForward: @escaping () -> Void,
        nudgeBackward: @escaping () -> Void,
        setAtPlayhead: @escaping () -> Void,
        buttonTitle: String,
        help: String
    ) -> some View {
        HStack(spacing: 8) {
            Text(title)
            Spacer()
            Text(StudioEditorModel.secondsText(time))
                .font(.caption.monospacedDigit())
                .foregroundStyle(.secondary)
            Stepper("", onIncrement: nudgeForward, onDecrement: nudgeBackward)
                .labelsHidden()
                .accessibilityLabel(accessibilityLabel)
                .accessibilityValue(StudioEditorModel.secondsText(time))
            Button("At Playhead") {
                setAtPlayhead()
            }
            .controlSize(.small)
            .help(help)
            .accessibilityLabel(buttonTitle)
        }
    }

    @ViewBuilder
    private var noSelectionText: some View {
        if viewModel.zooms.isEmpty {
            Text("Press Z or Add Zoom to zoom in at the playhead.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        } else if viewModel.selectedZoom == nil {
            Text("Select a zoom on the timeline, or step to one with the arrows above.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var canSelectPrevious: Bool {
        viewModel.isEditable && viewModel.editor?.zoomIndex(before: viewModel.selectedZoomIndex, playhead: viewModel.playhead) != nil
    }

    private var canSelectNext: Bool {
        viewModel.isEditable && viewModel.editor?.zoomIndex(after: viewModel.selectedZoomIndex, playhead: viewModel.playhead) != nil
    }

    private var selectionTitle: String {
        if let index = viewModel.selectedZoomIndex, viewModel.selectedZoom != nil {
            return StudioEditorModel.zoomPositionText(index: index, count: viewModel.zooms.count)
        }
        if viewModel.zooms.isEmpty {
            return "No zooms yet"
        }
        return viewModel.zooms.count == 1 ? "1 zoom" : "\(viewModel.zooms.count) zooms"
    }

    private var selectionDetail: String? {
        guard let zoom = viewModel.selectedZoom else { return nil }
        let suffix = zoom.origin == .auto ? ", suggested" : ""
        return StudioEditorModel.zoomRangeText(zoom) + suffix
    }

    private func focusModeBinding(zoom: StudioZoom) -> Binding<StudioZoomFocusMode> {
        Binding(
            get: { zoom.focus.mode },
            set: { viewModel.setSelectedZoomFocusMode($0) }
        )
    }

    private func gestureChanged(_ isEditing: Bool) {
        if isEditing {
            viewModel.beginGesture()
        } else {
            viewModel.endGesture()
        }
    }

    private func percentText(_ fraction: Double) -> String {
        StudioEditorModel.percentText(fraction)
    }

    private func secondsValueText(_ value: Double) -> String {
        String(format: "%.1f s", value.isFinite ? value : 0)
    }
}

// MARK: - Focus Pad

/// A small picture of the screen, or of its crop, with the part a zoom holds drawn as a rectangle
/// and the point it looks at as a dot. Dragging in it moves the point. The Horizontal and Vertical
/// sliders under it do the same, so it is hidden from VoiceOver.
struct StudioFocusPad: View {
    let pad: StudioZoomPad
    let onBegan: () -> Void
    let onChanged: (Double, Double) -> Void
    let onEnded: () -> Void

    @State private var isDragging = false

    /// The room the inspector has for content: its 320 points less 16 of padding on each side.
    private static let availableWidth: CGFloat = 288
    private static let maximumHeight: CGFloat = 160

    var body: some View {
        let size = padSize

        ZStack(alignment: .topLeading) {
            RoundedRectangle(cornerRadius: 4)
                .fill(Color.primary.opacity(0.08))
                .overlay {
                    RoundedRectangle(cornerRadius: 4)
                        .strokeBorder(Color.primary.opacity(0.25), lineWidth: 1)
                }
            windowRect(size: size)
            focusDot(size: size)
        }
        .frame(width: size.width, height: size.height, alignment: .topLeading)
        .contentShape(Rectangle())
        .gesture(dragGesture(size: size))
        .frame(maxWidth: .infinity)
        .help("Drag to choose where this zoom looks")
        .accessibilityHidden(true)
    }

    /// As wide as the inspector allows and at most `maximumHeight` high, in the pad's shape. The
    /// size is worked out here because the inspector scrolls, and in a scroll view a shape that
    /// only fits its width has no limit on its height.
    private var padSize: CGSize {
        let ratio = CGFloat(safeAspectRatio)
        let height = min(Self.maximumHeight, Self.availableWidth / ratio)
        return CGSize(width: height * ratio, height: height)
    }

    private func windowRect(size: CGSize) -> some View {
        Rectangle()
            .fill(Color.accentColor.opacity(0.2))
            .overlay {
                Rectangle()
                    .strokeBorder(Color.accentColor, lineWidth: 1.5)
            }
            .frame(
                width: max(0, CGFloat(pad.window.width) * size.width),
                height: max(0, CGFloat(pad.window.height) * size.height)
            )
            .offset(
                x: CGFloat(min(max(pad.window.x, 0), 1)) * size.width,
                y: CGFloat(min(max(pad.window.y, 0), 1)) * size.height
            )
    }

    private func focusDot(size: CGSize) -> some View {
        Circle()
            .fill(Color.accentColor)
            .overlay {
                Circle()
                    .strokeBorder(Color.white, lineWidth: 1)
            }
            .frame(width: 8, height: 8)
            .offset(
                x: CGFloat(min(max(pad.focusX, 0), 1)) * size.width - 4,
                y: CGFloat(min(max(pad.focusY, 0), 1)) * size.height - 4
            )
    }

    private func dragGesture(size: CGSize) -> some Gesture {
        DragGesture(minimumDistance: 0)
            .onChanged { value in
                if !isDragging {
                    isDragging = true
                    onBegan()
                }
                let x = size.width > 0 ? Double(value.location.x / size.width) : 0
                let y = size.height > 0 ? Double(value.location.y / size.height) : 0
                onChanged(min(max(x, 0), 1), min(max(y, 0), 1))
            }
            .onEnded { _ in
                if isDragging {
                    onEnded()
                }
                isDragging = false
            }
    }

    private var safeAspectRatio: Double {
        pad.aspectRatio.isFinite && pad.aspectRatio > 0 ? pad.aspectRatio : 16.0 / 9.0
    }
}
