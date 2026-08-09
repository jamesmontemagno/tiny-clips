import AppKit
import SwiftUI

// MARK: - Canvas View

struct ScreenshotEditorCanvasView: View {
    @ObservedObject var viewModel: ScreenshotEditorViewModel
    let containerSize: CGSize

    var body: some View {
        let imageSize = viewModel.displaySize(in: containerSize)
        let origin = CGPoint(
            x: (containerSize.width - imageSize.width) / 2,
            y: (containerSize.height - imageSize.height) / 2
        )
        let imageCornerRadius = min(
            viewModel.canvasCornerRadius,
            min(imageSize.width, imageSize.height) / 2
        )

        ZStack(alignment: .topLeading) {
            // Checkered background for transparency
            Color(nsColor: .controlBackgroundColor)

            if let image = viewModel.originalImage {
                let backgroundWidth = imageSize.width + (viewModel.canvasPadding * 2)
                let backgroundHeight = imageSize.height + (viewModel.canvasPadding * 2)

                if viewModel.backgroundStyle == .solid {
                    Rectangle()
                        .fill(viewModel.backgroundColor)
                        .frame(width: backgroundWidth, height: backgroundHeight)
                        .position(x: containerSize.width / 2, y: containerSize.height / 2)
                } else if viewModel.backgroundStyle == .gradient {
                    Rectangle()
                        .fill(
                            LinearGradient(
                                colors: [viewModel.backgroundColor, viewModel.backgroundSecondaryColor],
                                startPoint: .topLeading,
                                endPoint: .bottomTrailing
                            )
                        )
                        .frame(width: backgroundWidth, height: backgroundHeight)
                        .position(x: containerSize.width / 2, y: containerSize.height / 2)
                } else if viewModel.backgroundStyle == .wallpaper, let wallpaperImage = viewModel.wallpaperImage {
                    Image(nsImage: wallpaperImage)
                        .resizable()
                        .scaledToFill()
                        .frame(width: backgroundWidth, height: backgroundHeight)
                        .position(x: containerSize.width / 2, y: containerSize.height / 2)
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
                            drawAnnotation(annotation, in: context, scaledRect: scaledRect, imageSize: imageSize, origin: .zero, sourceImage: viewModel.originalImage)
                        }

                        // Draw in-progress annotation
                        if let current = viewModel.currentAnnotation {
                            let scaledRect = viewModel.scaledRect(current.rect, imageSize: imageSize, origin: .zero)
                            drawAnnotation(current, in: context, scaledRect: scaledRect, imageSize: imageSize, origin: .zero, sourceImage: viewModel.originalImage)
                        }
                    }
                    .allowsHitTesting(false)

                    // Text annotations
                    ForEach(viewModel.annotations.filter { $0.tool == .text }) { annotation in
                        let scaledRect = viewModel.scaledRect(annotation.rect, imageSize: imageSize, origin: .zero)
                        Text(annotation.text)
                            .font(textPreviewFont(
                                family: annotation.fontFamily,
                                size: viewModel.textFontSize(annotation.fontSize, forRenderedImageWidth: imageSize.width),
                                isBold: annotation.isBold
                            ))
                            .italic(annotation.isItalic)
                            .underline(annotation.isUnderlined)
                            .foregroundColor(annotation.color)
                            .position(x: scaledRect.midX, y: scaledRect.midY)
                            .allowsHitTesting(false)
                    }
                }
                .frame(width: imageSize.width, height: imageSize.height, alignment: .topLeading)
                .clipShape(RoundedRectangle(cornerRadius: imageCornerRadius))
                .shadow(color: .black.opacity(0.25), radius: viewModel.canvasShadowRadius)
                .position(x: containerSize.width / 2, y: containerSize.height / 2)

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
                        // Corner handles
                        let handleSize: CGFloat = 8
                        for corner in corners(of: scaled) {
                            let handleRect = CGRect(x: corner.x - handleSize/2, y: corner.y - handleSize/2, width: handleSize, height: handleSize)
                            context.fill(Path(handleRect), with: .color(.white))
                        }
                    }
                }
                .allowsHitTesting(false)

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
                                viewModel.handleDrag(start: normalizedStart, current: normalizedCurrent, isAspectLocked: isShiftPressed)
                            }
                            .onEnded { value in
                                let normalizedStart = viewModel.normalizePoint(value.startLocation, imageSize: imageSize)
                                let normalizedEnd = viewModel.normalizePoint(value.location, imageSize: imageSize)
                                let isShiftPressed = NSEvent.modifierFlags.contains(.shift)
                                viewModel.handleDragEnd(start: normalizedStart, end: normalizedEnd, isAspectLocked: isShiftPressed)
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
                    .accessibilityElement(children: .ignore)
                    .accessibilityLabel("Screenshot annotation canvas")
                    .accessibilityHint(viewModel.selectedTool == .move
                        ? "Select an annotation, drag inside it to move, or drag a corner handle to resize."
                        : "Use the selected tool to edit the screenshot.")
                    .position(x: containerSize.width / 2, y: containerSize.height / 2)
            }
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

    private func drawAnnotation(_ annotation: ScreenshotAnnotation, in context: GraphicsContext, scaledRect: CGRect, imageSize: CGSize, origin: CGPoint, sourceImage: NSImage? = nil) {
        let color = annotation.color
        let lineWidth = annotation.lineWidth

        switch annotation.tool {
        case .rectangle:
            if annotation.fillColor != .clear {
                context.fill(Path(scaledRect), with: .color(annotation.fillColor))
            }
            context.stroke(Path(scaledRect), with: .color(color), lineWidth: lineWidth)

        case .circle:
            if annotation.fillColor != .clear {
                context.fill(Path(ellipseIn: scaledRect), with: .color(annotation.fillColor))
            }
            context.stroke(Path(ellipseIn: scaledRect), with: .color(color), lineWidth: lineWidth)

        case .arrow:
            let linePoints = viewModel.scaledLinePoints(for: annotation, imageSize: imageSize, origin: origin)
            let start = linePoints.start
            let end = linePoints.end
            let headLength = max(18, lineWidth * 4.0)
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

        case .text, .crop, .move:
            break
        }
    }
}