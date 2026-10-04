import Foundation
import SwiftUI

// MARK: - Speed Timeline

/// The speed changes as blocks along the recording, in source time, lined up with the other
/// timeline lanes. Pressing a block selects its speed change, dragging it moves it, and dragging
/// one of its ends changes where the video starts or stops playing at another speed.
struct StudioSpeedLane: View {
    @ObservedObject var viewModel: StudioViewModel
    @State private var drag: SpeedDrag?
    @State private var isTrackDragging = false

    private static let space = "studioSpeedLane"

    /// A press on a block, which becomes a drag once the pointer has moved.
    private struct SpeedDrag {
        enum Part { case body, start, end }

        /// Where the speed change is in the list. An edit says where it is afterwards.
        var index: Int
        var part: Part

        /// The speed change's start and end when the press began. The drag is measured from
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
                emptyText
                speedBlocks(width: width, height: proxy.size.height, usable: usable, duration: duration)
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
        if viewModel.speedChanges.isEmpty {
            Text(StudioEditorModel.noSpeedHint)
                .font(.caption)
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .allowsHitTesting(false)
        }
    }

    private func speedBlocks(width: CGFloat, height: CGFloat, usable: CGFloat, duration: Double) -> some View {
        ForEach(Array(viewModel.speedChanges.enumerated()), id: \.offset) { index, speed in
            let x0 = xPosition(for: speed.start, usable: usable, duration: duration)
            let x1 = xPosition(for: speed.end, usable: usable, duration: duration)
            let drawnWidth = max(10, x1 - x0)
            let x = blockOffset(center: (x0 + x1) / 2, width: drawnWidth, laneWidth: width)
            block(index: index, speed: speed, width: drawnWidth, height: max(0, height - 4))
                .offset(x: x, y: 2)
                .gesture(
                    blockGesture(index: index, speed: speed, x: x, width: drawnWidth, usable: usable, duration: duration)
                )
        }
    }

    private func block(index: Int, speed: StudioSpeedRange, width: CGFloat, height: CGFloat) -> some View {
        let isSelected = viewModel.selectedSpeedIndex == index
        let foreground = isSelected ? Color.black : Color.primary

        return RoundedRectangle(cornerRadius: 4)
            .fill(Color.orange.opacity(isSelected ? 0.9 : 0.3))
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
                        Image(systemName: speed.rate > 1 ? "hare.fill" : "tortoise.fill")
                    }
                    if width >= 56 {
                        Text(StudioEditorModel.speedRateText(speed.rate))
                            .font(.caption2.weight(.semibold))
                    }
                }
                .foregroundStyle(foreground)
                .allowsHitTesting(false)
            }
            .contentShape(Rectangle())
            .help("Drag to move this speed change. Drag an end to change where it starts or stops.")
            .accessibilityElement()
            .accessibilityLabel(StudioEditorModel.speedAccessibilityText(speed))
            .accessibilityValue(isSelected ? "Selected" : "Not selected")
            .accessibilityAddTraits(.isButton)
            .accessibilityAction {
                _ = viewModel.selectAndShowSpeed(index)
            }
    }

    private func blockGesture(
        index: Int,
        speed: StudioSpeedRange,
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
                    drag = SpeedDrag(index: index, part: part, start: speed.start, end: speed.end)
                    viewModel.selectSpeed(index)
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
                    newIndex = viewModel.moveSpeed(at: current.index, to: current.start + delta)
                case .start:
                    newIndex = viewModel.setSpeedStart(at: current.index, to: current.start + delta)
                case .end:
                    newIndex = viewModel.setSpeedEnd(at: current.index, to: current.end + delta)
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

    private func dragPart(localX: CGFloat, width: CGFloat) -> SpeedDrag.Part {
        guard width >= 24 else { return .body }
        if localX <= 6 { return .start }
        if localX >= width - 6 { return .end }
        return .body
    }
}

// MARK: - Speed Inspector

/// The Speed section of the inspector: stepping through speed changes, adding one at the
/// playhead, and editing the selected speed change's rate, start and end without a pointer.
struct StudioSpeedInspectorSection: View {
    @ObservedObject var viewModel: StudioViewModel

    var body: some View {
        StudioInspectorSection(title: "Speed") {
            navigationRow
            addButton
            selectedSpeedControls
            noSelectionText
        }
    }

    private var navigationRow: some View {
        HStack(spacing: 8) {
            Button {
                viewModel.showPreviousSpeed()
            } label: {
                Image(systemName: "chevron.left")
            }
            .disabled(!canSelectPrevious)
            .accessibilityLabel("Previous speed change")
            .help("Previous speed change")

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
                viewModel.showNextSpeed()
            } label: {
                Image(systemName: "chevron.right")
            }
            .disabled(!canSelectNext)
            .accessibilityLabel("Next speed change")
            .help("Next speed change")
        }
    }

    private var addButton: some View {
        Button("Change Speed at Playhead") {
            viewModel.addSpeedAtPlayhead()
        }
        .disabled(!viewModel.canAddSpeedAtPlayhead)
        .help("Play two seconds of the video twice as fast from the playhead (R)")
    }

    @ViewBuilder
    private var selectedSpeedControls: some View {
        if let speed = viewModel.selectedSpeed {
            Picker("Speed", selection: speedRateBinding(speed: speed)) {
                ForEach(rateChoices(for: speed.rate), id: \.self) { rate in
                    Text(StudioEditorModel.speedRateText(rate))
                        .tag(rate)
                        .accessibilityLabel(StudioEditorModel.speedRateName(rate))
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .accessibilityLabel("Speed")
            .help("How fast this stretch plays")

            timeRow(
                title: "Start",
                time: speed.start,
                accessibilityLabel: "Speed change start",
                nudgeForward: { viewModel.nudgeSelectedSpeedStart(by: 0.1) },
                nudgeBackward: { viewModel.nudgeSelectedSpeedStart(by: -0.1) },
                setAtPlayhead: { viewModel.setSelectedSpeedStartAtPlayhead() },
                buttonTitle: "Start speed change at playhead",
                help: "Start this speed change at the playhead"
            )
            timeRow(
                title: "End",
                time: speed.end,
                accessibilityLabel: "Speed change end",
                nudgeForward: { viewModel.nudgeSelectedSpeedEnd(by: 0.1) },
                nudgeBackward: { viewModel.nudgeSelectedSpeedEnd(by: -0.1) },
                setAtPlayhead: { viewModel.setSelectedSpeedEndAtPlayhead() },
                buttonTitle: "End speed change at playhead",
                help: "End this speed change at the playhead"
            )
            Text(StudioEditorModel.speedLengthText(speed))
                .font(.caption)
                .foregroundStyle(.secondary)
            Text(StudioEditorModel.speedSilentNote)
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Button("Delete Speed Change", role: .destructive) {
                viewModel.removeSelectedSpeed()
            }
            .help("Delete this speed change, so its stretch plays at the recording's own speed again (Delete)")
        }
    }

    @ViewBuilder
    private var noSelectionText: some View {
        if !viewModel.speedChanges.isEmpty && viewModel.selectedSpeed == nil {
            Text(StudioEditorModel.selectSpeedHint)
                .font(.caption)
                .foregroundStyle(.secondary)
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
        viewModel.isEditable && viewModel.editor?.speedIndex(before: viewModel.selectedSpeedIndex, playhead: viewModel.playhead) != nil
    }

    private var canSelectNext: Bool {
        viewModel.isEditable && viewModel.editor?.speedIndex(after: viewModel.selectedSpeedIndex, playhead: viewModel.playhead) != nil
    }

    private var selectionTitle: String {
        if let index = viewModel.selectedSpeedIndex, viewModel.selectedSpeed != nil {
            return StudioEditorModel.speedPositionText(index: index, count: viewModel.speedChanges.count)
        }
        if viewModel.speedChanges.isEmpty {
            return "No speed changes yet"
        }
        return viewModel.speedChanges.count == 1 ? "1 speed change" : "\(viewModel.speedChanges.count) speed changes"
    }

    private var selectionDetail: String? {
        guard let speed = viewModel.selectedSpeed else { return nil }
        return StudioEditorModel.speedRangeText(speed)
    }

    /// The rates the picker offers: the editor's own, and with them the rate of the selected
    /// speed change when a project file gave it another one. A picker whose selection is none of
    /// its choices is not defined to show anything.
    private func rateChoices(for rate: Double) -> [Double] {
        let rates = StudioEditorModel.speedRates
        return rates.contains(rate) ? rates : (rates + [rate]).sorted()
    }

    private func speedRateBinding(speed: StudioSpeedRange) -> Binding<Double> {
        Binding(
            get: { speed.rate },
            set: { viewModel.setSelectedSpeedRate($0) }
        )
    }
}
