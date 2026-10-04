import Foundation
import SwiftUI

// MARK: - Scene Timeline

/// The scenes as blocks along the recording, in source time, lined up with the zoom lane and the
/// trim bar under it. Pressing a block moves the playhead there, which makes that scene the
/// current one, and dragging the line between two blocks changes when the later scene begins.
/// The Scene section of the inspector does the same without a pointer.
struct StudioSceneLane: View {
    @ObservedObject var viewModel: StudioViewModel
    @State private var drag: SceneDrag?

    private static let space = "studioSceneLane"

    /// A press on a block. It moves the playhead, or becomes a drag of a scene's start once the
    /// pointer has moved.
    private struct SceneDrag {
        /// The scene whose start is dragged, or nil when the press only moves the playhead.
        var startIndex: Int?

        /// That scene's start when the press began. The drag is measured from it, so it does not
        /// add up rounding from one step to the next.
        var start: Double
        var hasMoved = false
    }

    var body: some View {
        GeometryReader { proxy in
            let duration = max(viewModel.editor?.sourceDuration ?? 0, 0.0001)
            let width = proxy.size.width
            let usable = max(1, width - StudioTimelineMetrics.edgeInset * 2)

            ZStack(alignment: .topLeading) {
                sceneBlocks(width: width, height: proxy.size.height, usable: usable, duration: duration)
                playheadLine(usable: usable, duration: duration)
            }
            .coordinateSpace(.named(Self.space))
        }
    }

    private func sceneBlocks(width: CGFloat, height: CGFloat, usable: CGFloat, duration: Double) -> some View {
        ForEach(Array(viewModel.scenes.enumerated()), id: \.offset) { index, scene in
            sceneBlock(index: index, scene: scene, laneWidth: width, height: height, usable: usable, duration: duration)
        }
    }

    /// One scene, from a point after its start to a point before its end, so neighbors have a gap
    /// between them.
    @ViewBuilder
    private func sceneBlock(
        index: Int,
        scene: StudioScene,
        laneWidth: CGFloat,
        height: CGFloat,
        usable: CGFloat,
        duration: Double
    ) -> some View {
        if let range = viewModel.editor?.sceneRange(at: index) {
            let x0 = xPosition(for: range.start, usable: usable, duration: duration)
            let x1 = xPosition(for: range.end, usable: usable, duration: duration)
            let x = min(max(x0 + 1, 0), max(0, laneWidth - 4))
            let drawnWidth = min(max(4, x1 - x0 - 2), max(4, laneWidth - x))
            block(index: index, scene: scene, width: drawnWidth, height: height)
                .offset(x: x)
                .gesture(
                    blockGesture(index: index, range: range, x: x, width: drawnWidth, usable: usable, duration: duration)
                )
        }
    }

    private func block(index: Int, scene: StudioScene, width: CGFloat, height: CGFloat) -> some View {
        let isCurrent = index == viewModel.currentSceneIndex
        let shownLayout: StudioLayout = viewModel.hasCamera ? scene.layout : .screen

        return RoundedRectangle(cornerRadius: 4)
            .fill(isCurrent ? Color.accentColor.opacity(0.30) : Color.primary.opacity(0.10))
            .overlay {
                if isCurrent {
                    RoundedRectangle(cornerRadius: 4)
                        .strokeBorder(Color.accentColor, lineWidth: 1)
                }
            }
            .frame(width: width, height: height)
            .overlay(alignment: .leading) {
                grip(index: index, width: width, height: height)
            }
            .overlay {
                if width >= 52 {
                    Text(StudioEditorModel.layoutName(shownLayout))
                        .font(.caption2.weight(.semibold))
                        .lineLimit(1)
                        .padding(.horizontal, 8)
                        .allowsHitTesting(false)
                }
            }
            .contentShape(Rectangle())
            .help("Press to move the playhead here. Drag the start of a scene to change when it begins.")
            .accessibilityElement()
            .accessibilityLabel(viewModel.editor?.sceneAccessibilityText(at: index) ?? "")
            .accessibilityValue(isCurrent ? "Current scene" : "")
            .accessibilityAddTraits(.isButton)
            .accessibilityAction {
                _ = viewModel.showScene(index)
            }
    }

    /// A mark at the start of every scene but the first, which shows what can be dragged.
    @ViewBuilder
    private func grip(index: Int, width: CGFloat, height: CGFloat) -> some View {
        if index >= 1, width >= 16 {
            Capsule()
                .fill(Color.primary.opacity(0.45))
                .frame(width: 2, height: max(0, height / 2))
                .padding(.leading, 4)
                .allowsHitTesting(false)
        }
    }

    private func blockGesture(
        index: Int,
        range: (start: Double, end: Double),
        x: CGFloat,
        width: CGFloat,
        usable: CGFloat,
        duration: Double
    ) -> some Gesture {
        DragGesture(minimumDistance: 0, coordinateSpace: .named(Self.space))
            .onChanged { value in
                if drag == nil {
                    drag = pressedPart(index: index, range: range, localX: value.startLocation.x - x, width: width)
                }

                guard var current = drag else { return }
                guard let startIndex = current.startIndex else {
                    viewModel.scrub(to: time(at: value.location.x, usable: usable, duration: duration))
                    return
                }

                if !current.hasMoved, abs(value.translation.width) >= 3 {
                    current.hasMoved = true
                    viewModel.beginGesture()
                }
                drag = current
                guard current.hasMoved else { return }

                // A scene keeps its place in the list while its start is dragged, because the
                // model stops it short of its neighbors.
                let delta = Double(value.translation.width / usable) * duration
                viewModel.setSceneStart(at: startIndex, to: current.start + delta)
            }
            .onEnded { value in
                if let current = drag, current.startIndex != nil {
                    if current.hasMoved {
                        viewModel.endGesture()
                    } else {
                        // A press near a start that did not become a drag is a press on the lane.
                        viewModel.scrub(to: time(at: value.location.x, usable: usable, duration: duration))
                    }
                }
                drag = nil
            }
    }

    /// What a press on a block takes hold of: the start of the block's own scene near its left
    /// edge, the start of the next scene near its right edge, and otherwise the playhead. So the
    /// line between two blocks can be taken from either side.
    private func pressedPart(
        index: Int,
        range: (start: Double, end: Double),
        localX: CGFloat,
        width: CGFloat
    ) -> SceneDrag {
        guard width >= 16 else {
            return SceneDrag(startIndex: nil, start: range.start)
        }
        if index >= 1, localX <= 8 {
            return SceneDrag(startIndex: index, start: range.start)
        }
        if index + 1 < viewModel.scenes.count, localX >= width - 4 {
            return SceneDrag(startIndex: index + 1, start: range.end)
        }
        return SceneDrag(startIndex: nil, start: range.start)
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

// MARK: - Scene Inspector

/// The Scene section of the inspector: stepping from scene to scene, splitting the current one,
/// and what a scene has that the layout controls under it do not cover, which is when it starts,
/// how it is entered, and deleting it. The current scene is the one the playhead is in.
struct StudioSceneInspectorSection: View {
    @ObservedObject var viewModel: StudioViewModel

    var body: some View {
        StudioInspectorSection(title: "Scene") {
            navigationRow
            splitButton
            splitExplanation
            firstSceneExplanation
            currentSceneControls
            deleteButton
        }
    }

    private var navigationRow: some View {
        HStack(spacing: 8) {
            Button {
                _ = viewModel.showPreviousScene()
            } label: {
                Image(systemName: "chevron.left")
            }
            .disabled(!canShowPrevious)
            .accessibilityLabel("Previous scene")
            .help("Previous scene")

            VStack(alignment: .leading, spacing: 2) {
                Text(positionTitle)
                if let detail = positionDetail {
                    Text(detail)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            Button {
                _ = viewModel.showNextScene()
            } label: {
                Image(systemName: "chevron.right")
            }
            .disabled(!canShowNext)
            .accessibilityLabel("Next scene")
            .help("Next scene")
        }
    }

    private var splitButton: some View {
        Button("Split at Playhead") {
            viewModel.splitSceneAtPlayhead()
        }
        .disabled(!viewModel.canSplitSceneAtPlayhead)
        .help("Start a new scene at the playhead (S)")
    }

    @ViewBuilder
    private var splitExplanation: some View {
        if let explanation = viewModel.splitSceneExplanation {
            explanationText(explanation)
        }
    }

    @ViewBuilder
    private var firstSceneExplanation: some View {
        if viewModel.scenes.count == 1 {
            explanationText("Split the recording into scenes to change the layout partway through.")
        } else if viewModel.currentSceneIndex == 0 {
            explanationText(StudioEditorModel.firstSceneExplanation)
        }
    }

    /// The start and the entry of the current scene. The first scene starts with the recording
    /// and has nothing to move from, so it has neither.
    @ViewBuilder
    private var currentSceneControls: some View {
        if viewModel.currentSceneIndex >= 1, let scene = currentScene {
            timeRow(
                title: "Start",
                time: scene.start,
                accessibilityLabel: "Scene start",
                nudgeForward: { viewModel.nudgeCurrentSceneStart(by: 0.1) },
                nudgeBackward: { viewModel.nudgeCurrentSceneStart(by: -0.1) },
                setAtPlayhead: { viewModel.setCurrentSceneStartAtPlayhead() },
                buttonTitle: "Start scene at playhead",
                help: "Start this scene at the playhead"
            )
            Picker("Entered by", selection: transitionKindBinding(scene: scene)) {
                Text("A Cut").tag(StudioTransitionKind.cut)
                Text("Moving").tag(StudioTransitionKind.morph)
            }
            .pickerStyle(.segmented)
            .help("Cut to this scene, or have the screen and camera move into place")
            moveDurationControls(index: viewModel.currentSceneIndex, scene: scene)
        }
    }

    @ViewBuilder
    private func moveDurationControls(index: Int, scene: StudioScene) -> some View {
        if scene.transition.kind == .morph {
            let range = StudioEditorModel.sceneTransitionDurationRange
            let value = min(max(scene.transition.duration, range.lowerBound), range.upperBound)
            StudioSliderRow(
                title: "Move takes",
                value: value,
                range: range,
                step: 0.05,
                valueText: String(format: "%.2f s", value),
                onChange: { viewModel.setCurrentSceneTransitionDuration($0) },
                onEditingChanged: { gestureChanged($0) }
            )
            if let text = viewModel.editor?.sceneMoveLimitedText(at: index) {
                explanationText(text)
            }
        }
    }

    @ViewBuilder
    private var deleteButton: some View {
        if viewModel.scenes.count > 1 {
            Button("Delete Scene", role: .destructive) {
                viewModel.removeCurrentScene()
            }
            .disabled(!viewModel.canRemoveCurrentScene)
            .help("Delete this scene. The scene before it then lasts until the next one.")
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

    private func explanationText(_ text: String) -> some View {
        Text(text)
            .font(.caption)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
    }

    private var canShowPrevious: Bool {
        viewModel.isEditable && viewModel.currentSceneIndex > 0
    }

    private var canShowNext: Bool {
        viewModel.isEditable && viewModel.currentSceneIndex + 1 < viewModel.scenes.count
    }

    private var positionTitle: String {
        StudioEditorModel.scenePositionText(index: viewModel.currentSceneIndex, count: viewModel.scenes.count)
    }

    private var positionDetail: String? {
        viewModel.editor?.sceneRangeText(at: viewModel.currentSceneIndex)
    }

    private var currentScene: StudioScene? {
        let index = viewModel.currentSceneIndex
        guard viewModel.scenes.indices.contains(index) else { return nil }
        return viewModel.scenes[index]
    }

    private func transitionKindBinding(scene: StudioScene) -> Binding<StudioTransitionKind> {
        Binding(
            get: { scene.transition.kind },
            set: { viewModel.setCurrentSceneTransitionKind($0) }
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
}
