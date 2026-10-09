import AppKit
import SwiftUI

// MARK: - Crop keyboard and VoiceOver target

/// The crop handles are only drawn into a canvas, so this invisible element over the selection is
/// what takes keyboard focus, arrow keys, and accessibility actions.
private struct CropSelectionKeyboardTarget: View {
    @ObservedObject var viewModel: ScreenshotEditorViewModel
    @FocusState private var isFocused: Bool

    var body: some View {
        Rectangle()
            .fill(Color.clear)
            .overlay {
                if isFocused {
                    Rectangle()
                        .stroke(Color.accentColor, lineWidth: 2)
                        .padding(-4)
                }
            }
            .accessibilityElement()
            .accessibilityLabel("Crop selection")
            .accessibilityValue(selectionDescription)
            .accessibilityHint(viewModel.hasCropSelection
                ? "Arrow keys move the selection one pixel, or ten with Shift. Hold Option to resize it. Press Return to apply the crop."
                : "Use the Select entire image action to start a selection.")
            .focusable()
            .focused($isFocused)
            .focusEffectDisabled()
            .onKeyPress(keys: [.leftArrow, .rightArrow, .upArrow, .downArrow], phases: [.down, .repeat]) { press in
                handleKeyPress(press)
            }
            .accessibilityAdjustableAction { direction in
                let step = viewModel.cropCoarseStepPixels
                switch direction {
                case .increment:
                    viewModel.adjustCropSelection(growingBy: CGSize(width: step, height: step))
                case .decrement:
                    viewModel.adjustCropSelection(growingBy: CGSize(width: -step, height: -step))
                @unknown default:
                    break
                }
            }
            .accessibilityActions {
                if viewModel.hasCropSelection {
                    Button("Move left") { move(x: -1, y: 0) }
                    Button("Move right") { move(x: 1, y: 0) }
                    Button("Move up") { move(x: 0, y: -1) }
                    Button("Move down") { move(x: 0, y: 1) }
                    Button("Make wider") { grow(width: 1, height: 0) }
                    Button("Make narrower") { grow(width: -1, height: 0) }
                    Button("Make taller") { grow(width: 0, height: 1) }
                    Button("Make shorter") { grow(width: 0, height: -1) }
                } else {
                    Button("Select entire image") { viewModel.selectEntireImageForCrop() }
                }
            }
            // Take focus once a selection settles so the arrow keys act on it straight away.
            .onChange(of: viewModel.hasCropSelection) { _, hasSelection in
                if hasSelection && !viewModel.isAdjustingCropSelection {
                    isFocused = true
                }
            }
            .onChange(of: viewModel.isAdjustingCropSelection) { _, isAdjusting in
                if !isAdjusting && viewModel.hasCropSelection {
                    isFocused = true
                }
            }
    }

    private var selectionDescription: String {
        guard let rect = viewModel.cropSelectionPixelRect else { return "No selection" }
        return "\(Int(rect.width)) by \(Int(rect.height)) pixels, \(Int(rect.minX)) pixels from the left, \(Int(rect.minY)) pixels from the top"
    }

    private func move(x: CGFloat, y: CGFloat) {
        let step = viewModel.cropCoarseStepPixels
        viewModel.adjustCropSelection(movingBy: CGSize(width: x * step, height: y * step))
    }

    private func grow(width: CGFloat, height: CGFloat) {
        let step = viewModel.cropCoarseStepPixels
        viewModel.adjustCropSelection(growingBy: CGSize(width: width * step, height: height * step))
    }

    private func handleKeyPress(_ press: KeyPress) -> KeyPress.Result {
        guard viewModel.hasCropSelection,
              !viewModel.isAdjustingCropSelection,
              press.modifiers.isDisjoint(with: [.command, .control]) else {
            return .ignored
        }

        let step: CGFloat = press.modifiers.contains(.shift) ? 10 : 1
        let delta: CGSize
        switch press.key {
        case .leftArrow: delta = CGSize(width: -step, height: 0)
        case .rightArrow: delta = CGSize(width: step, height: 0)
        case .upArrow: delta = CGSize(width: 0, height: -step)
        case .downArrow: delta = CGSize(width: 0, height: step)
        default: return .ignored
        }

        if press.modifiers.contains(.option) {
            viewModel.adjustCropSelection(growingBy: delta)
        } else {
            viewModel.adjustCropSelection(movingBy: delta)
        }
        return .handled
    }
}

// MARK: - Canvas View

struct ScreenshotEditorCanvasView: View {
    @ObservedObject var viewModel: ScreenshotEditorViewModel
    let containerSize: CGSize
    let zoomScale: CGFloat
    let backingScale: CGFloat
    let panOffset: CGSize

    @State private var cropHover: CropDragMode?

    var body: some View {
        let exportLayout = viewModel.displayLayout(
            in: containerSize,
            zoomScale: zoomScale,
            backingScale: backingScale
        )
        let imageSize = exportLayout.imageRect.size
        let frameSize = exportLayout.frameSize
        let frameOrigin = CGPoint(
            x: (containerSize.width - frameSize.width) / 2 + panOffset.width,
            y: (containerSize.height - frameSize.height) / 2 + panOffset.height
        )
        let origin = CGPoint(
            x: frameOrigin.x + exportLayout.imageRect.minX,
            y: frameOrigin.y + exportLayout.imageRect.minY
        )
        let imageCornerRadius = min(
            viewModel.canvasCornerRadius,
            min(imageSize.width, imageSize.height) / 2
        )

        ZStack(alignment: .topLeading) {
            // Checkered background for transparency
            Color(nsColor: .controlBackgroundColor)

            if let image = viewModel.originalImage {
                if viewModel.backgroundStyle == .solid {
                    Rectangle()
                        .fill(viewModel.backgroundColor)
                        .frame(width: frameSize.width, height: frameSize.height)
                        .position(
                            x: containerSize.width / 2 + panOffset.width,
                            y: containerSize.height / 2 + panOffset.height
                        )
                } else if viewModel.backgroundStyle == .gradient {
                    Rectangle()
                        .fill(
                            LinearGradient(
                                colors: [viewModel.backgroundColor, viewModel.backgroundSecondaryColor],
                                startPoint: .topLeading,
                                endPoint: .bottomTrailing
                            )
                        )
                        .frame(width: frameSize.width, height: frameSize.height)
                        .position(
                            x: containerSize.width / 2 + panOffset.width,
                            y: containerSize.height / 2 + panOffset.height
                        )
                } else if viewModel.backgroundStyle == .wallpaper, let wallpaperImage = viewModel.wallpaperImage {
                    Image(nsImage: wallpaperImage)
                        .resizable()
                        .scaledToFill()
                        .frame(width: frameSize.width, height: frameSize.height)
                        .clipped()
                        .position(
                            x: containerSize.width / 2 + panOffset.width,
                            y: containerSize.height / 2 + panOffset.height
                        )
                }

                ZStack(alignment: .topLeading) {
                    Image(nsImage: image)
                        .resizable()
                        .aspectRatio(contentMode: .fit)
                        .frame(width: imageSize.width, height: imageSize.height)

                    // Image annotations layer
                    Canvas { context, _ in
                        for annotation in viewModel.annotations {
                            let scaledRect = viewModel.scaledRect(annotation.rect, imageSize: imageSize, origin: .zero)
                            drawAnnotation(annotation, in: context, scaledRect: scaledRect, imageSize: imageSize, origin: .zero, sourceImage: viewModel.originalImage, zoomScale: zoomScale)
                        }

                        // Draw in-progress annotation
                        if let current = viewModel.currentAnnotation {
                            let scaledRect = viewModel.scaledRect(current.rect, imageSize: imageSize, origin: .zero)
                            drawAnnotation(current, in: context, scaledRect: scaledRect, imageSize: imageSize, origin: .zero, sourceImage: viewModel.originalImage, zoomScale: zoomScale)
                        }
                    }

                    .allowsHitTesting(false)

                    // Text annotations
                    ForEach(viewModel.annotations.filter { $0.tool == .text }) { annotation in
                        let scaledRect = viewModel.scaledRect(annotation.rect, imageSize: imageSize, origin: .zero)
                        let boxStyle = annotation.textBoxStyle
                        let boxPadding = viewModel.textBoxMetric(boxStyle.padding, forRenderedImageWidth: imageSize.width)
                        let boxBorderWidth = viewModel.textBoxMetric(boxStyle.borderWidth, forRenderedImageWidth: imageSize.width)
                        let boxCornerRadius = viewModel.textBoxMetric(boxStyle.cornerRadius, forRenderedImageWidth: imageSize.width)
                        Text(annotation.text)
                            .font(textPreviewFont(
                                family: annotation.fontFamily,
                                size: viewModel.textFontSize(annotation.fontSize, forRenderedImageWidth: imageSize.width),
                                isBold: annotation.isBold
                            ))
                            .italic(annotation.isItalic)
                            .underline(annotation.isUnderlined)
                            .foregroundColor(annotation.color)
                            .frame(
                                width: max(1, scaledRect.width),
                                height: max(1, scaledRect.height),
                                alignment: .center
                            )
                            .padding(boxPadding + boxBorderWidth / 2)
                            .background {
                                RoundedRectangle(cornerRadius: boxCornerRadius)
                                    .fill(boxStyle.backgroundColor)
                            }
                            .overlay {
                                RoundedRectangle(cornerRadius: boxCornerRadius)
                                    .strokeBorder(boxStyle.borderColor, lineWidth: boxBorderWidth)
                            }
                            .rotationEffect(.radians(annotation.rotation))
                            .position(x: scaledRect.midX, y: scaledRect.midY)
                            .allowsHitTesting(false)
                    }

                }
                .frame(width: imageSize.width, height: imageSize.height, alignment: .topLeading)
                .clipShape(RoundedRectangle(cornerRadius: imageCornerRadius))
                .shadow(color: .black.opacity(0.25), radius: viewModel.canvasShadowRadius)
                .position(x: origin.x + imageSize.width / 2, y: origin.y + imageSize.height / 2)

                Canvas { context, size in
                    // Draw crop overlay
                    if viewModel.selectedTool == .crop, let cropRect = viewModel.cropRect {
                        let scaled = viewModel.scaledRect(cropRect, imageSize: imageSize, origin: origin)
                        // Dim outside crop
                        var dimPath = Path(CGRect(origin: .zero, size: size))
                        dimPath.addRect(scaled)
                        context.fill(dimPath, with: .color(.black.opacity(0.5)), style: FillStyle(eoFill: true))
                        // Crop border
                        context.stroke(Path(scaled), with: .color(.white), lineWidth: 2)
                        // Resize handles; edge handles are skipped when the side is too short to fit them.
                        let handleSize: CGFloat = 10
                        let minimumSideForEdgeHandle = handleSize * 4
                        for handle in CropHandle.allCases {
                            if !handle.isCorner {
                                let side = handle.movesLeftEdge || handle.movesRightEdge ? scaled.height : scaled.width
                                if side < minimumSideForEdgeHandle { continue }
                            }
                            let center = ScreenshotEditorCropMath.handlePoint(handle, in: scaled)
                            let handlePath = Path(CGRect(
                                x: center.x - handleSize / 2,
                                y: center.y - handleSize / 2,
                                width: handleSize,
                                height: handleSize
                            ))
                            context.fill(handlePath, with: .color(.white))
                            context.stroke(handlePath, with: .color(.accentColor), lineWidth: 1.5)
                        }
                    }
                }
                .allowsHitTesting(false)

                // Sits below the interaction overlay so pointer input still reaches the drag gestures.
                if viewModel.selectedTool == .crop {
                    let keyboardFrame = viewModel.cropRect.map {
                        viewModel.scaledRect($0, imageSize: imageSize, origin: origin)
                    } ?? CGRect(origin: origin, size: imageSize)
                    CropSelectionKeyboardTarget(viewModel: viewModel)
                        .frame(width: max(1, keyboardFrame.width), height: max(1, keyboardFrame.height))
                        .position(x: keyboardFrame.midX, y: keyboardFrame.midY)
                }

                // Inline text editing field
                if let textPos = viewModel.textEditPosition {
                    let screenPos = CGPoint(
                        x: origin.x + textPos.x * imageSize.width,
                        y: origin.y + textPos.y * imageSize.height
                    )
                    InlineTextEditor(
                        text: $viewModel.textEditValue,
                        fontSize: $viewModel.textFontSize,
                        fontFamily: viewModel.textFontFamily,
                        isBold: viewModel.textIsBold,
                        isItalic: viewModel.textIsItalic,
                        isUnderlined: viewModel.textIsUnderlined,
                        color: viewModel.selectedColor,
                        textBoxStyle: viewModel.textBoxStyle,
                        boxScale: imageSize.width / 800,
                        onCommit: {
                            viewModel.commitTextAnnotation()
                        }
                    )
                    .position(x: screenPos.x, y: screenPos.y)
                }

                // Selection highlight for move tool
                if viewModel.selectedTool == .move,
                   let idx = viewModel.selectedAnnotationIndex,
                   idx < viewModel.annotations.count {
                    let ann = viewModel.annotations[idx]

                    // Show endpoint handles for arrows and lines
                    if ann.tool == .arrow || ann.tool == .line {
                        let linePoints = viewModel.scaledLinePoints(for: ann, imageSize: imageSize, origin: origin)
                        let startPt = linePoints.start
                        let endPt = linePoints.end

                        // Tail handle (hollow circle)
                        Circle()
                            .stroke(Color.accentColor, lineWidth: 2)
                            .frame(width: 12, height: 12)
                            .position(startPt)
                            .allowsHitTesting(false)

                        // Head handle (filled circle)
                        Circle()
                            .fill(Color.accentColor)
                            .frame(width: 12, height: 12)
                            .position(endPt)
                            .allowsHitTesting(false)
                    } else if let frame = viewModel.rotationFrame(for: ann) {
                        let corners = RotatableAnnotationGeometry.corners(of: frame.rect, rotation: frame.rotation, in: imageSize)
                            .map { CGPoint(x: origin.x + $0.x, y: origin.y + $0.y) }
                        let scaledRect = viewModel.scaledRect(frame.rect, imageSize: imageSize, origin: origin)
                        let center = CGPoint(x: scaledRect.midX, y: scaledRect.midY)
                        let handleLocal = RotatableAnnotationGeometry.rotationHandle(for: frame.rect, rotation: frame.rotation, in: imageSize)
                        let handle = CGPoint(x: origin.x + handleLocal.x, y: origin.y + handleLocal.y)
                        let topCenter = RotatableAnnotationGeometry.rotate(
                            CGPoint(x: center.x, y: scaledRect.minY),
                            around: center,
                            by: frame.rotation
                        )

                        RoundedRectangle(cornerRadius: 2)
                            .stroke(Color.accentColor, style: StrokeStyle(lineWidth: 1.5, dash: [4, 3]))
                            .frame(width: scaledRect.width + 8, height: scaledRect.height + 8)
                            .rotationEffect(.radians(frame.rotation))
                            .position(center)
                            .allowsHitTesting(false)

                        Path { path in
                            path.move(to: topCenter)
                            path.addLine(to: handle)
                        }
                        .stroke(Color.accentColor, lineWidth: 1.5)
                        .allowsHitTesting(false)

                        ForEach(Array(corners.enumerated()), id: \.offset) { _, corner in
                            Rectangle()
                                .fill(Color(nsColor: .controlBackgroundColor))
                                .stroke(Color.accentColor, lineWidth: 2)
                                .frame(width: 10, height: 10)
                                .rotationEffect(.radians(frame.rotation))
                                .position(corner)
                                .allowsHitTesting(false)
                        }

                        ZStack {
                            Circle()
                                .fill(Color(nsColor: .controlBackgroundColor))
                                .stroke(Color.accentColor, lineWidth: 2)
                            Image(systemName: "arrow.clockwise")
                                .font(.system(size: 9, weight: .bold))
                                .foregroundStyle(Color.accentColor)
                        }
                        .frame(width: 16, height: 16)
                        .position(handle)
                        .allowsHitTesting(false)
                    } else if let selRect = viewModel.selectedAnnotationRect(imageSize: imageSize, origin: origin) {
                        RoundedRectangle(cornerRadius: 2)
                            .stroke(Color.accentColor, style: StrokeStyle(lineWidth: 1.5, dash: [4, 3]))
                            .frame(width: selRect.width + 8, height: selRect.height + 8)
                            .position(x: selRect.midX, y: selRect.midY)
                            .allowsHitTesting(false)

                        ForEach(Array(corners(of: selRect).enumerated()), id: \.offset) { _, corner in
                            Rectangle()
                                .fill(Color(nsColor: .controlBackgroundColor))
                                .stroke(Color.accentColor, lineWidth: 2)
                                .frame(width: 10, height: 10)
                                .position(corner)
                                .allowsHitTesting(false)
                        }
                    }
                }

                // Interaction overlay — gestures must be before .position()
                // so coordinates are in the overlay's local space (0..imageSize)
                Color.clear
                    .contentShape(Rectangle())
                    .frame(width: imageSize.width, height: imageSize.height)
                    .allowsHitTesting(viewModel.textEditPosition == nil)
                    .gesture(
                        DragGesture(minimumDistance: 1)
                            .onChanged { value in
                                let normalizedStart = viewModel.normalizePoint(value.startLocation, imageSize: imageSize)
                                let normalizedCurrent = viewModel.normalizePoint(value.location, imageSize: imageSize)
                                let isShiftPressed = NSEvent.modifierFlags.contains(.shift)
                                viewModel.handleDrag(start: normalizedStart, current: normalizedCurrent, isAspectLocked: isShiftPressed, displaySize: imageSize)
                            }
                            .onEnded { value in
                                let normalizedStart = viewModel.normalizePoint(value.startLocation, imageSize: imageSize)
                                let normalizedEnd = viewModel.normalizePoint(value.location, imageSize: imageSize)
                                let isShiftPressed = NSEvent.modifierFlags.contains(.shift)
                                viewModel.handleDragEnd(start: normalizedStart, end: normalizedEnd, isAspectLocked: isShiftPressed, displaySize: imageSize)
                            }
                    )
                    .simultaneousGesture(
                        SpatialTapGesture()
                            .onEnded { value in
                                let normalized = viewModel.normalizePoint(value.location, imageSize: imageSize)
                                if viewModel.selectedTool == .text && viewModel.textEditPosition == nil {
                                    viewModel.textEditPosition = normalized
                                    viewModel.textEditValue = ""
                                    viewModel.isEditingText = true
                                } else if viewModel.selectedTool == .number {
                                    viewModel.placeNumberAnnotation(at: normalized)
                                } else if viewModel.selectedTool == .emoji {
                                    viewModel.placeEmojiAnnotation(at: normalized)
                                } else if viewModel.selectedTool == .crop {
                                    viewModel.handleCropTap(at: normalized, displaySize: imageSize)
                                } else if viewModel.selectedTool == .move {
                                    // Tap to select/deselect annotations
                                    if let idx = viewModel.annotationIndex(at: normalized) {
                                        viewModel.selectedAnnotationIndex = idx
                                    } else {
                                        viewModel.selectedAnnotationIndex = nil
                                    }
                                }
                            }
                    )
                    .onContinuousHover { phase in
                        guard viewModel.selectedTool == .crop else {
                            if cropHover != nil { cropHover = nil }
                            return
                        }
                        switch phase {
                        case .active(let location):
                            let hover = viewModel.cropInteraction(
                                at: viewModel.normalizePoint(location, imageSize: imageSize),
                                displaySize: imageSize
                            )
                            if cropHover != hover { cropHover = hover }
                        case .ended:
                            if cropHover != nil { cropHover = nil }
                        }
                    }
                    .pointerStyle(cropPointerStyle)
                    .accessibilityElement(children: .ignore)
                    .accessibilityLabel("Screenshot annotation canvas")
                    .accessibilityHint(canvasAccessibilityHint)
                    .position(x: origin.x + imageSize.width / 2, y: origin.y + imageSize.height / 2)
            }
        }
    }

    private var canvasAccessibilityHint: String {
        switch viewModel.selectedTool {
        case .move:
            return "Select an annotation, drag inside it to move, drag a corner handle to resize, or drag the rotation grip above it to rotate."
        case .emoji:
            return "Click to place the selected emoji."
        case .crop:
            return "Drag to select the area to keep, or move to the Crop selection element to position it with the arrow keys or VoiceOver actions. Press Return to apply the crop."
        default:
            return "Use the selected tool to edit the screenshot."
        }
    }

    /// Pointer feedback for the Crop tool: resize arrows on handles, a hand inside the selection.
    private var cropPointerStyle: PointerStyle? {
        guard viewModel.selectedTool == .crop else { return nil }
        guard let cropHover else { return PointerStyle.rectSelection }
        switch cropHover {
        case .create:
            return PointerStyle.rectSelection
        case .move:
            return viewModel.isAdjustingCropSelection ? PointerStyle.grabActive : PointerStyle.grabIdle
        case .resize(let handle):
            return PointerStyle.frameResize(position: frameResizePosition(for: handle))
        }
    }

    private func frameResizePosition(for handle: CropHandle) -> FrameResizePosition {
        switch handle {
        case .topLeft: return .topLeading
        case .top: return .top
        case .topRight: return .topTrailing
        case .left: return .leading
        case .right: return .trailing
        case .bottomLeft: return .bottomLeading
        case .bottom: return .bottom
        case .bottomRight: return .bottomTrailing
        }
    }

    private func corners(of rect: CGRect) -> [CGPoint] {
        [
            CGPoint(x: rect.minX, y: rect.minY),
            CGPoint(x: rect.maxX, y: rect.minY),
            CGPoint(x: rect.minX, y: rect.maxY),
            CGPoint(x: rect.maxX, y: rect.maxY),
        ]
    }

    /// Returns a context rotated about `scaledRect`'s center plus the rect re-centered on the
    /// origin when the annotation is rotated; otherwise the untouched context and rect.
    private func rotatedDrawing(_ context: GraphicsContext, annotation: ScreenshotAnnotation, scaledRect: CGRect) -> (GraphicsContext, CGRect) {
        guard annotation.isRotated else { return (context, scaledRect) }
        var rotated = context
        rotated.translateBy(x: scaledRect.midX, y: scaledRect.midY)
        rotated.rotate(by: .radians(annotation.rotation))
        return (rotated, CGRect(x: -scaledRect.width / 2, y: -scaledRect.height / 2, width: scaledRect.width, height: scaledRect.height))
    }

    private func drawAnnotation(_ annotation: ScreenshotAnnotation, in context: GraphicsContext, scaledRect: CGRect, imageSize: CGSize, origin: CGPoint, sourceImage: NSImage? = nil, zoomScale: CGFloat = 1) {
        let color = annotation.color
        // `imageSize` already grows with zoomScale, but stroke widths, arrowheads, and
        // number-badge fonts are stored in fixed screen-space units. Scale them by
        // zoomScale so the preview matches the exported annotation proportions at
        // any zoom level instead of appearing thinner as the canvas is enlarged.
        let lineWidth = annotation.lineWidth * zoomScale

        switch annotation.tool {
        case .rectangle:
            let (ctx, rect) = rotatedDrawing(context, annotation: annotation, scaledRect: scaledRect)
            if annotation.fillColor != .clear {
                ctx.fill(Path(rect), with: .color(annotation.fillColor))
            }
            ctx.stroke(Path(rect), with: .color(color), lineWidth: lineWidth)

        case .circle:
            let (ctx, rect) = rotatedDrawing(context, annotation: annotation, scaledRect: scaledRect)
            if annotation.fillColor != .clear {
                ctx.fill(Path(ellipseIn: rect), with: .color(annotation.fillColor))
            }
            ctx.stroke(Path(ellipseIn: rect), with: .color(color), lineWidth: lineWidth)

        case .arrow:
            let linePoints = viewModel.scaledLinePoints(for: annotation, imageSize: imageSize, origin: origin)
            let start = linePoints.start
            let end = linePoints.end
            let headLength = max(18 * zoomScale, lineWidth * 4.0)
            let headAngle: CGFloat = .pi / 6
            let control = arrowControlPoint(start: start, end: end, style: annotation.arrowStyle)
            let tipAngle: CGFloat
            switch annotation.arrowStyle {
            case .straight:
                tipAngle = atan2(end.y - start.y, end.x - start.x)
            case .curvedLeft, .curvedRight:
                tipAngle = atan2(end.y - control.y, end.x - control.x)
            }

            let wing1 = CGPoint(
                x: end.x - headLength * cos(tipAngle - headAngle),
                y: end.y - headLength * sin(tipAngle - headAngle)
            )
            let wing2 = CGPoint(
                x: end.x - headLength * cos(tipAngle + headAngle),
                y: end.y - headLength * sin(tipAngle + headAngle)
            )
            // Shaft ends at the base of the filled arrowhead
            let shaftEnd = CGPoint(
                x: end.x - headLength * cos(headAngle) * cos(tipAngle),
                y: end.y - headLength * cos(headAngle) * sin(tipAngle)
            )
            var linePath = Path()
            linePath.move(to: start)
            switch annotation.arrowStyle {
            case .straight:
                linePath.addLine(to: shaftEnd)
            case .curvedLeft, .curvedRight:
                linePath.addQuadCurve(to: shaftEnd, control: control)
            }
            context.stroke(linePath, with: .color(color), lineWidth: lineWidth)

            // Filled triangular arrowhead
            var arrowHead = Path()
            arrowHead.move(to: end)
            arrowHead.addLine(to: wing1)
            arrowHead.addLine(to: wing2)
            arrowHead.closeSubpath()
            context.fill(arrowHead, with: .color(color))

        case .line:
            var path = Path()
            let linePoints = viewModel.scaledLinePoints(for: annotation, imageSize: imageSize, origin: origin)
            path.move(to: linePoints.start)
            path.addLine(to: linePoints.end)
            context.stroke(path, with: .color(color), lineWidth: lineWidth)

        case .pencil:
            if annotation.points.count > 1 {
                var path = Path()
                let scaledPoints = annotation.points.map { pt in
                    CGPoint(
                        x: origin.x + pt.x * imageSize.width,
                        y: origin.y + pt.y * imageSize.height
                    )
                }
                path.move(to: scaledPoints[0])
                for pt in scaledPoints.dropFirst() {
                    path.addLine(to: pt)
                }
                context.stroke(path, with: .color(color), lineWidth: lineWidth)
            }

        case .blur:
            drawCheckerboardRedaction(in: context, rect: scaledRect, preset: annotation.redactionBlurPreset)

        case .number:
            // Draw filled circle
            context.fill(Path(ellipseIn: scaledRect), with: .color(color))
            // Draw number centered in circle
            let fontSize = scaledRect.width * numberCircleFontRatio
            let numberText = Text(annotation.text)
                .font(.system(size: fontSize, weight: .bold, design: .rounded))
                .foregroundColor(annotation.textColor)
            context.draw(numberText, at: CGPoint(x: scaledRect.midX, y: scaledRect.midY), anchor: .center)

        case .emoji:
            let center = CGPoint(x: scaledRect.midX, y: scaledRect.midY)
            var rotated = context
            rotated.translateBy(x: center.x, y: center.y)
            rotated.rotate(by: .radians(annotation.rotation))
            let glyph = Text(annotation.text)
                .font(.system(size: EmojiAnnotationMath.glyphFontSize(forSide: scaledRect.height)))
            rotated.draw(glyph, at: .zero, anchor: .center)

        case .text, .crop, .move:
            break
        }
    }
}