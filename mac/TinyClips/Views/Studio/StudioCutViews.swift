import Foundation
import SwiftUI

// MARK: - Cut Timeline

/// The cuts as blocks along the recording, in source time, lined up with the other timeline
/// lanes. Pressing a block selects its cut, dragging it moves the cut, and dragging one of its
/// ends changes where the video starts or stops leaving source time out.
struct StudioCutLane: View {
    @ObservedObject var viewModel: StudioViewModel
    @State private var drag: CutDrag?
    @State private var isTrackDragging = false

    private static let space = "studioCutLane"

    /// A press on a block, which becomes a drag once the pointer has moved.
    private struct CutDrag {
        enum Part { case body, start, end }

        /// Where the cut is in the list. An edit says where it is afterwards.
        var index: Int
        var part: Part

        /// The cut's start and end when the press began. The drag is measured from these, so it
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
                cutBlocks(width: width, height: proxy.size.height, usable: usable, duration: duration)
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
        if viewModel.cuts.isEmpty {
            Text("No cuts. Press X to cut a second out at the playhead.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .allowsHitTesting(false)
        }
    }

    private func cutBlocks(width: CGFloat, height: CGFloat, usable: CGFloat, duration: Double) -> some View {
        ForEach(Array(viewModel.cuts.enumerated()), id: \.offset) { index, cut in
            let x0 = xPosition(for: cut.start, usable: usable, duration: duration)
            let x1 = xPosition(for: cut.end, usable: usable, duration: duration)
            let drawnWidth = max(10, x1 - x0)
            let x = blockOffset(center: (x0 + x1) / 2, width: drawnWidth, laneWidth: width)
            block(index: index, cut: cut, width: drawnWidth, height: max(0, height - 4))
                .offset(x: x, y: 2)
                .gesture(
                    blockGesture(index: index, cut: cut, x: x, width: drawnWidth, usable: usable, duration: duration)
                )
        }
    }

    private func block(index: Int, cut: StudioTimeRange, width: CGFloat, height: CGFloat) -> some View {
        let isSelected = viewModel.selectedCutIndex == index

        // A selected block is filled with the text color, dark in a light window and light in a
        // dark one, so what is written on it takes the window's background color.
        let foreground = isSelected ? Color(nsColor: .windowBackgroundColor) : Color.primary

        return RoundedRectangle(cornerRadius: 4)
            .fill(Color.primary.opacity(isSelected ? 0.7 : 0.22))
            .overlay {
                if isSelected {
                    RoundedRectangle(cornerRadius: 4)
                        .strokeBorder(Color.primary, lineWidth: 1)
                }
            }
            .frame(width: width, height: height)
            .overlay {
                HStack(spacing: 4) {
                    if width >= 22 {
                        Image(systemName: "scissors")
                    }
                    if width >= 56 {
                        Text(String(format: "%.1f s", max(0, cut.end - cut.start)))
                            .font(.caption2.weight(.semibold))
                    }
                }
                .foregroundStyle(foreground)
                .allowsHitTesting(false)
            }
            .contentShape(Rectangle())
            .help("Drag to move this cut. Drag an end to change where it starts or stops.")
            .accessibilityElement()
            .accessibilityLabel(StudioEditorModel.cutAccessibilityText(cut))
            .accessibilityValue(isSelected ? "Selected" : "Not selected")
            .accessibilityAddTraits(.isButton)
            .accessibilityAction {
                _ = viewModel.selectAndShowCut(index)
            }
    }

    private func blockGesture(
        index: Int,
        cut: StudioTimeRange,
        x: CGFloat,
        width: CGFloat,
        usable: CGFloat,
        duration: Double
    ) -> some Gesture {
        DragGesture(minimumDistance: 0, coordinateSpace: .named(Self.space))
            .onChanged { value in
                if drag == nil {
                    let localX = value.startLocation.x - x
                    let part = dragPart(localX: localX, width: width)
                    drag = CutDrag(index: index, part: part, start: cut.start, end: cut.end)
                    viewModel.selectCut(index)
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
                    newIndex = viewModel.moveCut(at: current.index, to: current.start + delta)
                case .start:
                    newIndex = viewModel.setCutStart(at: current.index, to: current.start + delta)
                case .end:
                    newIndex = viewModel.setCutEnd(at: current.index, to: current.end + delta)
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

    private func dragPart(localX: CGFloat, width: CGFloat) -> CutDrag.Part {
        guard width >= 24 else { return .body }
        if localX <= 6 { return .start }
        if localX >= width - 6 { return .end }
        return .body
    }
}

// MARK: - Cut Inspector

/// The Cut panel of the inspector: stepping through cuts, adding one at the playhead, and
/// editing the selected cut's start and end without a pointer.
struct StudioCutInspectorSection: View {
    @ObservedObject var viewModel: StudioViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            navigationRow
            addButton
            noSelectionText
        }
        selectedCutControls
    }

    private var navigationRow: some View {
        HStack(spacing: 8) {
            Button {
                viewModel.showPreviousCut()
            } label: {
                Image(systemName: "chevron.left")
            }
            .disabled(!canSelectPrevious)
            .accessibilityLabel("Previous cut")
            .help("Previous cut")

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
                viewModel.showNextCut()
            } label: {
                Image(systemName: "chevron.right")
            }
            .disabled(!canSelectNext)
            .accessibilityLabel("Next cut")
            .help("Next cut")
        }
    }

    private var addButton: some View {
        Button("Add Cut") {
            viewModel.addCutAtPlayhead()
        }
        .disabled(!viewModel.canAddCutAtPlayhead)
        .help("Cut a second out of the video at the playhead (X)")
    }

    @ViewBuilder
    private var selectedCutControls: some View {
        if let cut = viewModel.selectedCut {
            StudioInspectorSection(title: "Timing") {
                timeRow(
                    title: "Start",
                    time: cut.start,
                    accessibilityLabel: "Cut start",
                    nudgeForward: { viewModel.nudgeSelectedCutStart(by: 0.1) },
                    nudgeBackward: { viewModel.nudgeSelectedCutStart(by: -0.1) },
                    setAtPlayhead: { viewModel.setSelectedCutStartAtPlayhead() },
                    buttonTitle: "Start cut at playhead",
                    help: "Start this cut at the playhead"
                )
                timeRow(
                    title: "End",
                    time: cut.end,
                    accessibilityLabel: "Cut end",
                    nudgeForward: { viewModel.nudgeSelectedCutEnd(by: 0.1) },
                    nudgeBackward: { viewModel.nudgeSelectedCutEnd(by: -0.1) },
                    setAtPlayhead: { viewModel.setSelectedCutEndAtPlayhead() },
                    buttonTitle: "End cut at playhead",
                    help: "End this cut at the playhead"
                )
                Text(StudioEditorModel.cutLengthText(cut))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Button("Delete Cut", role: .destructive) {
                viewModel.removeSelectedCut()
            }
            .help("Delete this cut and put the part it removed back in the video (Delete)")
        }
    }

    @ViewBuilder
    private var noSelectionText: some View {
        if viewModel.cuts.isEmpty {
            Text("Press X or Add Cut to cut a second out of the video at the playhead.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        } else if viewModel.selectedCut == nil {
            Text("Select a cut on the timeline, or step to one with the arrows above.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
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

    private var canSelectPrevious: Bool {
        viewModel.isEditable && viewModel.editor?.cutIndex(before: viewModel.selectedCutIndex, playhead: viewModel.playhead) != nil
    }

    private var canSelectNext: Bool {
        viewModel.isEditable && viewModel.editor?.cutIndex(after: viewModel.selectedCutIndex, playhead: viewModel.playhead) != nil
    }

    private var selectionTitle: String {
        if let index = viewModel.selectedCutIndex, viewModel.selectedCut != nil {
            return StudioEditorModel.cutPositionText(index: index, count: viewModel.cuts.count)
        }
        if viewModel.cuts.isEmpty {
            return "No cuts yet"
        }
        return viewModel.cuts.count == 1 ? "1 cut" : "\(viewModel.cuts.count) cuts"
    }

    private var selectionDetail: String? {
        guard let cut = viewModel.selectedCut else { return nil }
        return StudioEditorModel.cutRangeText(cut)
    }
}
