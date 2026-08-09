import AppKit
import SwiftUI

// MARK: - ViewModel

private struct EditorCanvasState {
    let annotations: [ScreenshotAnnotation]
    let cropRect: CGRect?
    let nextNumberLabel: Int
}

private enum AnnotationResizeHandle {
    case topLeft
    case topRight
    case bottomLeft
    case bottomRight
}

@MainActor
class ScreenshotEditorViewModel: ObservableObject {
    let sourceURL: URL
    @Published var originalImage: NSImage?
    @Published var selectedTool: EditTool = .move
    @Published var selectedColor: Color = .red
    @Published var selectedFillColor: Color = .clear
    @Published var lineWidth: CGFloat = 4
    @Published var annotations: [ScreenshotAnnotation] = []
    @Published var currentAnnotation: ScreenshotAnnotation?
    @Published var cropRect: CGRect?
    @Published private(set) var canUndo = false
    @Published private(set) var canRedo = false
    @Published var isEditingText = false
    @Published var textEditPosition: CGPoint? // normalized click position
    @Published var textEditValue: String = ""
    @Published var textFontSize: CGFloat = 16
    @Published var textFontFamily: String = textSystemFontFamily
    @Published var textIsBold = false
    @Published var textIsItalic = false
    @Published var textIsUnderlined = false
    @Published var selectedAnnotationIndex: Int?
    @Published var saveFormat: ImageFormat
    @Published var saveScale: Int
    @Published var saveJpegQuality: Double

    @Published var nextNumberLabel: Int = 1
    @Published var numberSizeMultiplier: CGFloat = 1.0
    @Published var numberTextColor: Color = .white
    @Published var redactionBlurPreset: RedactionBlurPreset = .medium
    @Published var selectedArrowStylePreset: ArrowStyle = .straight
    @Published var backgroundStyle: ExportBackgroundStyle = .transparent
    @Published var backgroundColor: Color = Color(red: 0.96, green: 0.96, blue: 0.98)
    @Published var backgroundSecondaryColor: Color = Color(red: 0.84, green: 0.90, blue: 0.99)
    @Published var selectedBackgroundPresetID: String = "transparent"
    @Published var wallpaperImage: NSImage?
    @Published var canvasPadding: CGFloat = 0
    @Published var canvasCornerRadius: CGFloat = 0
    @Published var canvasShadowRadius: CGFloat = 0
    @Published var exportFramePreset: ExportFramePreset = .original
    @Published var horizontalExportAlignment: ExportHorizontalAlignment = .center
    @Published var verticalExportAlignment: ExportVerticalAlignment = .center

    private var pencilPoints: [CGPoint] = []
    private var imagePixelSize: CGSize = .zero
    private var dragOffset: CGPoint = .zero
    private var dragOriginalRect: CGRect = .zero
    private var dragOriginalPoints: [CGPoint] = []
    private var dragOriginalFontSize: CGFloat = 0
    private var isDraggingAnnotation = false
    private var isDraggingEndpoint = false // true = dragging arrowhead/line end
    private var isDraggingStartpoint = false // true = dragging arrow tail/line start
    private var activeResizeHandle: AnnotationResizeHandle?
    private var undoStack: [EditorCanvasState] = []
    private var redoStack: [EditorCanvasState] = []
    private var pendingDragHistoryState: EditorCanvasState?
    private var didChangePendingDrag = false

    private var initialBackgroundStyle: ExportBackgroundStyle
    private var initialBackgroundColor: Color
    private var initialBackgroundSecondaryColor: Color
    private var initialSelectedBackgroundPresetID: String
    private var initialWallpaperPresent: Bool
    private var initialCanvasPadding: CGFloat
    private var initialCanvasCornerRadius: CGFloat
    private var initialCanvasShadowRadius: CGFloat
    private var initialExportFramePreset: ExportFramePreset
    private var initialHorizontalExportAlignment: ExportHorizontalAlignment
    private var initialVerticalExportAlignment: ExportVerticalAlignment
    private var hasPendingChanges = false

    init(url: URL) {
        self.sourceURL = url
        let settings = CaptureSettings.shared
        self.saveFormat = settings.imageFormat
        self.saveScale = settings.screenshotScale
        self.saveJpegQuality = settings.jpegQuality
        if let image = NSImage(contentsOf: url) {
            self.originalImage = image
            if let rep = image.representations.first {
                self.imagePixelSize = CGSize(width: rep.pixelsWide, height: rep.pixelsHigh)
            }
        }

        self.initialBackgroundStyle = .transparent
        self.initialBackgroundColor = Color(red: 0.96, green: 0.96, blue: 0.98)
        self.initialBackgroundSecondaryColor = Color(red: 0.84, green: 0.90, blue: 0.99)
        self.initialSelectedBackgroundPresetID = "transparent"
        self.initialWallpaperPresent = false
        self.initialCanvasPadding = 0
        self.initialCanvasCornerRadius = 0
        self.initialCanvasShadowRadius = 0
        self.initialExportFramePreset = .original
        self.initialHorizontalExportAlignment = .center
        self.initialVerticalExportAlignment = .center
    }

    // Convert point in overlay-local space to 0..1 normalized coordinate
    func normalizePoint(_ point: CGPoint, imageSize: CGSize) -> CGPoint {
        CGPoint(
            x: max(0, min(1, point.x / imageSize.width)),
            y: max(0, min(1, point.y / imageSize.height))
        )
    }

    // Convert normalized rect to screen rect
    func scaledRect(_ rect: CGRect, imageSize: CGSize, origin: CGPoint) -> CGRect {
        CGRect(
            x: origin.x + rect.origin.x * imageSize.width,
            y: origin.y + rect.origin.y * imageSize.height,
            width: rect.width * imageSize.width,
            height: rect.height * imageSize.height
        )
    }

    func scaledPoint(_ point: CGPoint, imageSize: CGSize, origin: CGPoint) -> CGPoint {
        CGPoint(
            x: origin.x + point.x * imageSize.width,
            y: origin.y + point.y * imageSize.height
        )
    }

    func linePoints(for annotation: ScreenshotAnnotation) -> LinePoints {
        if annotation.points.count >= 2 {
            return (annotation.points[0], annotation.points[1])
        }
        return (
            start: annotation.rect.origin,
            end: CGPoint(x: annotation.rect.origin.x + annotation.rect.width,
                         y: annotation.rect.origin.y + annotation.rect.height)
        )
    }

    func scaledLinePoints(for annotation: ScreenshotAnnotation, imageSize: CGSize, origin: CGPoint) -> LinePoints {
        let points = linePoints(for: annotation)
        return (
            start: scaledPoint(points.start, imageSize: imageSize, origin: origin),
            end: scaledPoint(points.end, imageSize: imageSize, origin: origin)
        )
    }

    func directedRect(from points: LinePoints) -> CGRect {
        CGRect(
            x: points.start.x,
            y: points.start.y,
            width: points.end.x - points.start.x,
            height: points.end.y - points.start.y
        )
    }

    func displayLayout(in containerSize: CGSize) -> ExportFrameLayout {
        guard let image = originalImage, image.size.width > 0, image.size.height > 0 else {
            return ExportFrameLayout.make(
                imageSize: .zero,
                padding: canvasPadding,
                preset: exportFramePreset,
                horizontalAlignment: horizontalExportAlignment,
                verticalAlignment: verticalExportAlignment
            )
        }
        let imageAspect = image.size.width / image.size.height
        let effectivePadding = max(0, canvasPadding)
        let screenScale = NSScreen.main?.backingScaleFactor ?? 2.0
        let nativeWidth = imagePixelSize.width / screenScale
        let nativeHeight = imagePixelSize.height / screenScale
        let frameAspect = exportFramePreset.aspectRatio ?? ((nativeWidth + effectivePadding * 2) / (nativeHeight + effectivePadding * 2))
        let maxFrameWidth = max(1, containerSize.width * 0.95)
        let maxFrameHeight = max(1, containerSize.height * 0.95)
        let frameWidth = min(maxFrameWidth, maxFrameHeight * frameAspect)
        let frameHeight = frameWidth / frameAspect
        let availableWidth = max(1, frameWidth - (effectivePadding * 2))
        let availableHeight = max(1, frameHeight - (effectivePadding * 2))

        // Cap at native pixel dimensions to prevent upscaling blur on Retina
        let maxWidth = min(availableWidth, nativeWidth)
        let maxHeight = min(availableHeight, nativeHeight)
        let imageSize: CGSize
        if maxWidth / maxHeight < imageAspect {
            imageSize = CGSize(width: maxWidth, height: maxWidth / imageAspect)
        } else {
            imageSize = CGSize(width: maxHeight * imageAspect, height: maxHeight)
        }

        return ExportFrameLayout.make(
            imageSize: imageSize,
            padding: effectivePadding,
            preset: exportFramePreset,
            horizontalAlignment: horizontalExportAlignment,
            verticalAlignment: verticalExportAlignment
        )
    }

    /// Returns the text size for an image rendered at `renderedImageWidth`.
    ///
    /// Pass points for the SwiftUI preview and pixels for bitmap export so the
    /// font scales with the image in the same coordinate space as its renderer.
    func textFontSize(_ fontSize: CGFloat, forRenderedImageWidth renderedImageWidth: CGFloat) -> CGFloat {
        fontSize * (renderedImageWidth / 800.0)
    }

    // Find which annotation is at a normalized point
    func annotationIndex(at point: CGPoint) -> Int? {
        // Search in reverse so topmost (last drawn) is picked first
        for i in annotations.indices.reversed() {
            let ann = annotations[i]
            if ann.tool == .pencil {
                if let bounds = pencilBounds(for: ann), bounds.insetBy(dx: -0.02, dy: -0.02).contains(point) {
                    return i
                }
            } else {
                let hitRect: CGRect
                if ann.tool == .arrow || ann.tool == .line {
                        let line = linePoints(for: ann)
                        if distanceFromPoint(point, toLineSegmentStart: line.start, end: line.end) <= 0.02 {
                            return i
                        }
                        continue
                } else if ann.tool == .text {
                    // Text annotations need a larger hit area since the visual text
                    // size doesn't scale with the normalized rect
                    hitRect = ann.rect.insetBy(dx: -0.03, dy: -0.03)
                } else {
                    hitRect = ann.rect.insetBy(dx: -0.01, dy: -0.01)
                }
                if hitRect.contains(point) {
                    return i
                }
            }
        }
        return nil
    }

    func pencilBounds(for annotation: ScreenshotAnnotation) -> CGRect? {
        guard !annotation.points.isEmpty else { return nil }
        var minX = annotation.points[0].x, maxX = minX
        var minY = annotation.points[0].y, maxY = minY
        for pt in annotation.points.dropFirst() {
            minX = min(minX, pt.x); maxX = max(maxX, pt.x)
            minY = min(minY, pt.y); maxY = max(maxY, pt.y)
        }
        return CGRect(x: minX, y: minY, width: maxX - minX, height: maxY - minY)
    }

    func selectedAnnotationRect(imageSize: CGSize, origin: CGPoint) -> CGRect? {
        guard let idx = selectedAnnotationIndex, idx < annotations.count else { return nil }
        let ann = annotations[idx]
        let normRect: CGRect
        if ann.tool == .pencil, let bounds = pencilBounds(for: ann) {
            normRect = bounds
        } else {
            normRect = ann.rect
        }
        return scaledRect(normRect, imageSize: imageSize, origin: origin)
    }

    var showsLineWidthControl: Bool {
        switch inspectorTool {
        case .rectangle, .circle, .arrow, .line, .pencil:
            return true
        default:
            return false
        }
    }

    var showsNumberSizeControl: Bool {
        inspectorTool == .number
    }

    var showsRedactionPresetControl: Bool {
        inspectorTool == .blur
    }

    var showsArrowStyleControl: Bool {
        inspectorTool == .arrow
    }

    var inspectorTool: EditTool {
        if selectedTool == .move,
           let index = selectedAnnotationIndex,
           annotations.indices.contains(index) {
            return annotations[index].tool
        }
        return selectedTool
    }

    func applyBackgroundPreset(_ preset: ExportBackgroundPreset) {
        selectedBackgroundPresetID = preset.id
        backgroundStyle = preset.style
        backgroundColor = preset.primary
        if let secondary = preset.secondary {
            backgroundSecondaryColor = secondary
        }
        markDirty()
    }

    func applyCustomSolidBackground() {
        selectedBackgroundPresetID = "custom-solid"
        backgroundStyle = .solid
        markDirty()
    }

    func applyCustomGradientBackground() {
        selectedBackgroundPresetID = "custom-gradient"
        backgroundStyle = .gradient
        markDirty()
    }

    func chooseWallpaperBackground() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.image]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.canChooseFiles = true

        if panel.runModal() == .OK,
           let url = panel.url,
           let image = NSImage(contentsOf: url) {
            wallpaperImage = image
            selectedBackgroundPresetID = "wallpaper"
            backgroundStyle = .wallpaper
            markDirty()
        }
    }

    func clearWallpaperBackground() {
        wallpaperImage = nil
        selectedBackgroundPresetID = "transparent"
        backgroundStyle = .transparent
        markDirty()
    }

    var hasAnnotations: Bool {
        !annotations.isEmpty
    }

    var hasUnsavedChanges: Bool {
        if hasPendingChanges {
            return true
        }

        if backgroundStyle != initialBackgroundStyle {
            return true
        }

        if selectedBackgroundPresetID != initialSelectedBackgroundPresetID {
            return true
        }

        if (wallpaperImage != nil) != initialWallpaperPresent {
            return true
        }

        if !colorsEqual(backgroundColor, initialBackgroundColor) || !colorsEqual(backgroundSecondaryColor, initialBackgroundSecondaryColor) {
            return true
        }

        if abs(canvasPadding - initialCanvasPadding) > 0.0001 ||
            abs(canvasCornerRadius - initialCanvasCornerRadius) > 0.0001 ||
            abs(canvasShadowRadius - initialCanvasShadowRadius) > 0.0001 ||
            exportFramePreset != initialExportFramePreset ||
            horizontalExportAlignment != initialHorizontalExportAlignment ||
            verticalExportAlignment != initialVerticalExportAlignment {
            return true
        }

        return false
    }

    var showsAnyStyleControls: Bool {
        primaryStyleControlsVisible || showsLineWidthControl || showsArrowStyleControl || showsNumberSizeControl || showsRedactionPresetControl || showsTextStyleControls
    }

    var showsTextStyleControls: Bool {
        inspectorTool == .text
    }

    private var primaryStyleControlsVisible: Bool {
        switch inspectorTool {
        case .rectangle, .circle, .arrow, .line, .pencil, .text, .number:
            return true
        case .move, .crop, .blur:
            return false
        }
    }

    var availableTextFontFamilies: [String] {
        [textSystemFontFamily] + NSFontManager.shared.availableFontFamilies.sorted()
    }

    func selectedTextFontFamily() -> String? {
        guard selectedAnnotationIsText, let index = selectedAnnotationIndex else { return nil }
        return annotations[index].fontFamily
    }

    func selectedTextBold() -> Bool? {
        guard selectedAnnotationIsText, let index = selectedAnnotationIndex else { return nil }
        return annotations[index].isBold
    }

    func selectedTextItalic() -> Bool? {
        guard selectedAnnotationIsText, let index = selectedAnnotationIndex else { return nil }
        return annotations[index].isItalic
    }

    func selectedTextUnderline() -> Bool? {
        guard selectedAnnotationIsText, let index = selectedAnnotationIndex else { return nil }
        return annotations[index].isUnderlined
    }

    @discardableResult
    func updateSelectedTextFontFamily(_ family: String) -> Bool {
        guard selectedAnnotationIsText, let index = selectedAnnotationIndex else { return false }
        recordHistory()
        annotations[index].fontFamily = family
        markDirty()
        return true
    }

    @discardableResult
    func updateSelectedTextBold(_ isBold: Bool) -> Bool {
        guard selectedAnnotationIsText, let index = selectedAnnotationIndex else { return false }
        recordHistory()
        annotations[index].isBold = isBold
        markDirty()
        return true
    }

    @discardableResult
    func updateSelectedTextItalic(_ isItalic: Bool) -> Bool {
        guard selectedAnnotationIsText, let index = selectedAnnotationIndex else { return false }
        recordHistory()
        annotations[index].isItalic = isItalic
        markDirty()
        return true
    }

    @discardableResult
    func updateSelectedTextUnderline(_ isUnderlined: Bool) -> Bool {
        guard selectedAnnotationIsText, let index = selectedAnnotationIndex else { return false }
        recordHistory()
        annotations[index].isUnderlined = isUnderlined
        markDirty()
        return true
    }

    func selectedArrowStyle() -> ArrowStyle? {
        guard let index = selectedAnnotationIndex, annotations.indices.contains(index), annotations[index].tool == .arrow else {
            return nil
        }
        return annotations[index].arrowStyle
    }

    @discardableResult
    func updateSelectedArrowStyle(_ style: ArrowStyle) -> Bool {
        guard let index = selectedAnnotationIndex, annotations.indices.contains(index), annotations[index].tool == .arrow else {
            return false
        }
        recordHistory()
        annotations[index].arrowStyle = style
        markDirty()
        return true
    }

    func handleDrag(start: CGPoint, current: CGPoint, isAspectLocked: Bool = false) {
        switch selectedTool {
        case .move:
            if !isDraggingAnnotation && !isDraggingEndpoint && !isDraggingStartpoint && activeResizeHandle == nil {
                // First drag event — find what we hit
                let selectedIndex = selectedAnnotationIndex.flatMap { index in
                    annotations.indices.contains(index) && resizeHandle(at: start, for: annotations[index]) != nil ? index : nil
                }
                if let idx = selectedIndex ?? annotationIndex(at: start) {
                    beginDragHistory()
                    selectedAnnotationIndex = idx
                    dragOriginalRect = annotations[idx].tool == .pencil
                        ? pencilBounds(for: annotations[idx]) ?? annotations[idx].rect
                        : annotations[idx].rect
                    dragOriginalPoints = annotations[idx].points
                    dragOriginalFontSize = annotations[idx].fontSize

                    let ann = annotations[idx]
                    if ann.tool == .arrow || ann.tool == .line {
                        if dragOriginalPoints.count < 2 {
                            let line = linePoints(for: ann)
                            dragOriginalPoints = [line.start, line.end]
                        }
                        // Check if near the head (end) or tail (start) endpoint
                        let line = linePoints(for: ann)
                        let endPt = line.end
                        let startPt = line.start
                        let distToEnd = hypot(start.x - endPt.x, start.y - endPt.y)
                        let distToStart = hypot(start.x - startPt.x, start.y - startPt.y)
                        let threshold: CGFloat = 0.04

                        if distToEnd < threshold && distToEnd <= distToStart {
                            isDraggingEndpoint = true
                        } else if distToStart < threshold && distToStart < distToEnd {
                            isDraggingStartpoint = true
                        } else {
                            isDraggingAnnotation = true
                        }
                    } else if let handle = resizeHandle(at: start, for: ann) {
                        activeResizeHandle = handle
                    } else {
                        isDraggingAnnotation = true
                    }
                } else {
                    selectedAnnotationIndex = nil
                }
            }
            if let idx = selectedAnnotationIndex {
                let dx = current.x - start.x
                let dy = current.y - start.y
                if dx != 0 || dy != 0 {
                    didChangePendingDrag = true
                }
                if let resizeHandle = activeResizeHandle {
                    resizeAnnotation(at: idx, from: resizeHandle, to: current)
                } else if isDraggingEndpoint {
                    // Move just the endpoint (rotate the arrow/line)
                    var ann = annotations[idx]
                    let orig = originalLinePoints()
                    let updated: LinePoints = (
                        start: orig.start,
                        end: CGPoint(x: orig.end.x + dx, y: orig.end.y + dy)
                    )
                    ann.points = [updated.start, updated.end]
                    ann.rect = directedRect(from: updated)
                    annotations[idx] = ann
                } else if isDraggingStartpoint {
                    // Move the start point (reverse rotate)
                    var ann = annotations[idx]
                    let orig = originalLinePoints()
                    let updated: LinePoints = (
                        start: CGPoint(x: orig.start.x + dx, y: orig.start.y + dy),
                        end: orig.end
                    )
                    ann.points = [updated.start, updated.end]
                    ann.rect = directedRect(from: updated)
                    annotations[idx] = ann
                    markDirty()
                } else if isDraggingAnnotation {
                    moveAnnotation(at: idx, dx: dx, dy: dy)
                }
            }

        case .crop:
            let rect = makeRect(from: start, to: current)
            beginDragHistory()
            if cropRect != rect {
                didChangePendingDrag = true
            }
            cropRect = rect
            markDirty()

        case .pencil:
            pencilPoints.append(current)
            currentAnnotation = ScreenshotAnnotation(
                tool: .pencil,
                rect: .zero,
                color: selectedColor,
                lineWidth: lineWidth,
                text: "",
                points: pencilPoints
            )

        case .text, .number:
            break // text/number use click, not drag

        default:
            let rect = makeRect(from: start, to: current, isAspectLocked: isAspectLocked)
            currentAnnotation = ScreenshotAnnotation(
                tool: selectedTool,
                rect: rect,
                color: selectedColor,
                textColor: numberTextColor,
                fillColor: selectedFillColor,
                lineWidth: lineWidth,
                text: "",
                points: (selectedTool == .arrow || selectedTool == .line) ? [start, current] : [],
                redactionBlurPreset: redactionBlurPreset,
                arrowStyle: selectedArrowStylePreset
            )
        }
    }

    func handleDragEnd(start: CGPoint, end: CGPoint, isAspectLocked: Bool = false) {
        switch selectedTool {
        case .move:
            isDraggingAnnotation = false
            isDraggingEndpoint = false
            isDraggingStartpoint = false
            activeResizeHandle = nil
            commitDragHistory()

        case .crop:
            commitDragHistory()

        case .pencil:
            if pencilPoints.count > 1 {
                recordHistory()
                annotations.append(ScreenshotAnnotation(
                    tool: .pencil,
                    rect: .zero,
                    color: selectedColor,
                    lineWidth: lineWidth,
                    text: "",
                    points: pencilPoints
                ))
            }
            pencilPoints = []
            currentAnnotation = nil

        case .text, .number:
            break // handled by SpatialTapGesture

        default:
            let rect = makeRect(from: start, to: end, isAspectLocked: isAspectLocked)
            let w = abs(rect.width)
            let h = abs(rect.height)
            if w > 0.005 || h > 0.005 {
                recordHistory()
                annotations.append(ScreenshotAnnotation(
                    tool: selectedTool,
                    rect: rect,
                    color: selectedColor,
                    textColor: numberTextColor,
                    fillColor: selectedFillColor,
                    lineWidth: lineWidth,
                    text: "",
                    points: (selectedTool == .arrow || selectedTool == .line) ? [start, end] : [],
                    redactionBlurPreset: redactionBlurPreset,
                    arrowStyle: selectedArrowStylePreset
                ))
                markDirty()
            }
            currentAnnotation = nil
        }
    }

    private func moveAnnotation(at index: Int, dx: CGFloat, dy: CGFloat) {
        var ann = annotations[index]
        if ann.tool == .pencil {
            ann.points = dragOriginalPoints.map { CGPoint(x: $0.x + dx, y: $0.y + dy) }
        } else if ann.tool == .arrow || ann.tool == .line {
            let base = originalLinePoints()
            let moved: LinePoints = (
                start: CGPoint(x: base.start.x + dx, y: base.start.y + dy),
                end: CGPoint(x: base.end.x + dx, y: base.end.y + dy)
            )
            ann.points = [moved.start, moved.end]
            ann.rect = directedRect(from: moved)
        } else {
            ann.rect = CGRect(
                x: dragOriginalRect.origin.x + dx,
                y: dragOriginalRect.origin.y + dy,
                width: dragOriginalRect.width,
                height: dragOriginalRect.height
            )
        }
        annotations[index] = ann
        markDirty()
    }

    private func resizeHandle(at point: CGPoint, for annotation: ScreenshotAnnotation) -> AnnotationResizeHandle? {
        guard annotation.tool != .arrow, annotation.tool != .line else { return nil }
        let bounds = annotation.tool == .pencil ? pencilBounds(for: annotation) : annotation.rect
        guard let bounds else { return nil }
        let threshold: CGFloat = 0.025
        let handles: [(AnnotationResizeHandle, CGPoint)] = [
            (.topLeft, CGPoint(x: bounds.minX, y: bounds.minY)),
            (.topRight, CGPoint(x: bounds.maxX, y: bounds.minY)),
            (.bottomLeft, CGPoint(x: bounds.minX, y: bounds.maxY)),
            (.bottomRight, CGPoint(x: bounds.maxX, y: bounds.maxY)),
        ]
        return handles.min { hypot(point.x - $0.1.x, point.y - $0.1.y) < hypot(point.x - $1.1.x, point.y - $1.1.y) }
            .flatMap { hypot(point.x - $0.1.x, point.y - $0.1.y) <= threshold ? $0.0 : nil }
    }

    private func resizeAnnotation(at index: Int, from handle: AnnotationResizeHandle, to point: CGPoint) {
        var annotation = annotations[index]
        let originalBounds = dragOriginalRect
        let opposite = CGPoint(
            x: handle == .topLeft || handle == .bottomLeft ? originalBounds.maxX : originalBounds.minX,
            y: handle == .topLeft || handle == .topRight ? originalBounds.maxY : originalBounds.minY
        )
        var resizedBounds = CGRect(
            x: min(point.x, opposite.x),
            y: min(point.y, opposite.y),
            width: max(abs(point.x - opposite.x), 0.001),
            height: max(abs(point.y - opposite.y), 0.001)
        )
        if annotation.tool == .text || annotation.tool == .number {
            let pixelWidth = max(imagePixelSize.width, 1)
            let pixelHeight = max(imagePixelSize.height, 1)
            let minimumWidth = 1 / pixelWidth
            let minimumHeight = 1 / pixelHeight
            let clampedPoint = CGPoint(
                x: handle == .topLeft || handle == .bottomLeft
                    ? min(point.x, opposite.x - minimumWidth)
                    : max(point.x, opposite.x + minimumWidth),
                y: handle == .topLeft || handle == .topRight
                    ? min(point.y, opposite.y - minimumHeight)
                    : max(point.y, opposite.y + minimumHeight)
            )
            let originalDiagonal = hypot(originalBounds.width * pixelWidth, originalBounds.height * pixelHeight)
            let draggedDiagonal = hypot(
                (clampedPoint.x - opposite.x) * pixelWidth,
                (clampedPoint.y - opposite.y) * pixelHeight
            )
            let minimumScale: CGFloat
            let maximumScale: CGFloat
            if annotation.tool == .text {
                minimumScale = max(0.1, 8 / max(dragOriginalFontSize, 1))
                maximumScale = .greatestFiniteMagnitude
            } else {
                let originalMultiplier = originalBounds.width * pixelWidth / max(baseNumberSidePixels(), 1)
                minimumScale = 0.2 / max(originalMultiplier, 0.001)
                maximumScale = 2 / max(originalMultiplier, 0.001)
            }
            let scale = min(
                max(draggedDiagonal / max(originalDiagonal, 1), minimumScale),
                maximumScale
            )
            let width = originalBounds.width * scale
            let height = originalBounds.height * scale
            resizedBounds = CGRect(
                x: handle == .topLeft || handle == .bottomLeft ? opposite.x - width : opposite.x,
                y: handle == .topLeft || handle == .topRight ? opposite.y - height : opposite.y,
                width: width,
                height: height
            )
        }

        if annotation.tool == .pencil {
            let width = max(originalBounds.width, 0.001)
            let height = max(originalBounds.height, 0.001)
            annotation.points = dragOriginalPoints.map { original in
                CGPoint(
                    x: resizedBounds.minX + ((original.x - originalBounds.minX) / width) * resizedBounds.width,
                    y: resizedBounds.minY + ((original.y - originalBounds.minY) / height) * resizedBounds.height
                )
            }
        } else {
            annotation.rect = resizedBounds
            if annotation.tool == .text {
                annotation.fontSize = max(8, dragOriginalFontSize * resizedBounds.width / max(originalBounds.width, 0.001))
            }
        }
        annotations[index] = annotation
        markDirty()
    }

    func undo() {
        guard let previousState = undoStack.popLast() else { return }
        redoStack.append(canvasState())
        restoreCanvasState(previousState)
        updateHistoryAvailability()
        markDirty()
    }

    func redo() {
        guard let nextState = redoStack.popLast() else { return }
        undoStack.append(canvasState())
        restoreCanvasState(nextState)
        updateHistoryAvailability()
        markDirty()
    }

    func clearAnnotations() {
        guard !annotations.isEmpty else { return }

        recordHistory()
        annotations.removeAll()
        selectedAnnotationIndex = nil
        currentAnnotation = nil
        pencilPoints = []
        nextNumberLabel = 1
        markDirty()
    }

    func copyToClipboard() {
        guard let output = buildOutputImage() else { return }

        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.writeObjects([output.image])
    }

    func save(to destinationURL: URL? = nil) -> URL? {
        guard let output = buildOutputImage() else { return nil }

        let saveURL = destinationURL ?? SaveService.shared.generateURL(for: .screenshot, fileExtension: saveFormat.rawValue)
        do {
            let directoryURL = saveURL.deletingLastPathComponent()
            try FileManager.default.createDirectory(at: directoryURL, withIntermediateDirectories: true)
            try output.data.write(to: saveURL, options: [.atomic])
            return saveURL
        } catch {
            return nil
        }
    }

    func markDirty() {
        hasPendingChanges = true
    }

    func markSaved() {
        hasPendingChanges = false
        initialBackgroundStyle = backgroundStyle
        initialBackgroundColor = backgroundColor
        initialBackgroundSecondaryColor = backgroundSecondaryColor
        initialSelectedBackgroundPresetID = selectedBackgroundPresetID
        initialWallpaperPresent = wallpaperImage != nil
        initialCanvasPadding = canvasPadding
        initialCanvasCornerRadius = canvasCornerRadius
        initialCanvasShadowRadius = canvasShadowRadius
        initialExportFramePreset = exportFramePreset
        initialHorizontalExportAlignment = horizontalExportAlignment
        initialVerticalExportAlignment = verticalExportAlignment
    }

    private func canvasState() -> EditorCanvasState {
        EditorCanvasState(
            annotations: annotations,
            cropRect: cropRect,
            nextNumberLabel: nextNumberLabel
        )
    }

    private func restoreCanvasState(_ state: EditorCanvasState) {
        annotations = state.annotations
        cropRect = state.cropRect
        nextNumberLabel = state.nextNumberLabel
        selectedAnnotationIndex = nil
        currentAnnotation = nil
        pencilPoints = []
    }

    private func recordHistory() {
        undoStack.append(canvasState())
        redoStack.removeAll()
        updateHistoryAvailability()
    }

    private func beginDragHistory() {
        guard pendingDragHistoryState == nil else { return }
        pendingDragHistoryState = canvasState()
        didChangePendingDrag = false
    }

    private func commitDragHistory() {
        defer {
            pendingDragHistoryState = nil
            didChangePendingDrag = false
        }
        guard didChangePendingDrag, let state = pendingDragHistoryState else { return }
        undoStack.append(state)
        redoStack.removeAll()
        updateHistoryAvailability()
    }

    private func updateHistoryAvailability() {
        canUndo = !undoStack.isEmpty
        canRedo = !redoStack.isEmpty
    }

    var outputResolutionText: String? {
        let size = exportBasePixelSize()
        guard size.width > 0, size.height > 0 else { return nil }

        let scale = CGFloat(saveScale) / 100.0
        let outputWidth = max(1, Int((size.width * scale).rounded()))
        let outputHeight = max(1, Int((size.height * scale).rounded()))
        return "\(outputWidth) × \(outputHeight) px"
    }

    private func buildOutputImage() -> (image: NSImage, data: Data)? {
        guard let renderedBitmap = renderFinalImage() else { return nil }

        let outputBitmap = scaleBitmap(renderedBitmap, to: saveScale)

        let imageData: Data?
        switch saveFormat {
        case .png:
            imageData = outputBitmap.representation(using: .png, properties: [:])
        case .jpeg:
            imageData = outputBitmap.representation(using: .jpeg, properties: [.compressionFactor: saveJpegQuality])
        }

        guard let data = imageData else { return nil }

        let outputImage = NSImage(size: NSSize(width: outputBitmap.pixelsWide, height: outputBitmap.pixelsHigh))
        outputImage.addRepresentation(outputBitmap)

        return (outputImage, data)
    }

    private func scaleBitmap(_ bitmap: NSBitmapImageRep, to percent: Int) -> NSBitmapImageRep {
        guard percent < 100, percent > 0 else { return bitmap }
        let factor = CGFloat(percent) / 100.0
        let newW = Int(CGFloat(bitmap.pixelsWide) * factor)
        let newH = Int(CGFloat(bitmap.pixelsHigh) * factor)
        guard newW > 0, newH > 0,
              let scaled = NSBitmapImageRep(
                bitmapDataPlanes: nil,
                pixelsWide: newW,
                pixelsHigh: newH,
                bitsPerSample: 8,
                samplesPerPixel: 4,
                hasAlpha: true,
                isPlanar: false,
                colorSpaceName: .deviceRGB,
                bytesPerRow: 0,
                bitsPerPixel: 0
              ),
              let sourceCG = bitmap.cgImage,
              let cgContext = NSGraphicsContext(bitmapImageRep: scaled)?.cgContext else {
            return bitmap
        }

        cgContext.interpolationQuality = .high
        cgContext.draw(sourceCG, in: CGRect(x: 0, y: 0, width: newW, height: newH))
        scaled.size = NSSize(width: newW, height: newH)
        return scaled
    }

    // MARK: - Private

    private func exportBasePixelSize() -> CGSize {
        guard imagePixelSize.width > 0, imagePixelSize.height > 0 else { return .zero }

        let cropPixelRect: CGRect
        if let crop = cropRect {
            cropPixelRect = CGRect(
                x: crop.origin.x * imagePixelSize.width,
                y: crop.origin.y * imagePixelSize.height,
                width: crop.width * imagePixelSize.width,
                height: crop.height * imagePixelSize.height
            )
        } else {
            cropPixelRect = CGRect(origin: .zero, size: imagePixelSize)
        }

        return ExportFrameLayout.make(
            imageSize: cropPixelRect.size,
            padding: canvasPadding,
            preset: exportFramePreset,
            horizontalAlignment: horizontalExportAlignment,
            verticalAlignment: verticalExportAlignment
        ).frameSize
    }

    private func originalLinePoints() -> LinePoints {
        if dragOriginalPoints.count >= 2 {
            return (dragOriginalPoints[0], dragOriginalPoints[1])
        }
        return (
            start: dragOriginalRect.origin,
            end: CGPoint(x: dragOriginalRect.origin.x + dragOriginalRect.width,
                         y: dragOriginalRect.origin.y + dragOriginalRect.height)
        )
    }

    private func distanceFromPoint(_ point: CGPoint, toLineSegmentStart start: CGPoint, end: CGPoint) -> CGFloat {
        let dx = end.x - start.x
        let dy = end.y - start.y
        let len2 = dx * dx + dy * dy
        if len2 <= 0.000001 {
            return hypot(point.x - start.x, point.y - start.y)
        }
        let t = max(0, min(1, ((point.x - start.x) * dx + (point.y - start.y) * dy) / len2))
        let proj = CGPoint(x: start.x + t * dx, y: start.y + t * dy)
        return hypot(point.x - proj.x, point.y - proj.y)
    }

    private func makeRect(from start: CGPoint, to end: CGPoint, isAspectLocked: Bool = false) -> CGRect {
        // Arrow/line store start in origin and end in maxX/maxY (can be negative width/height)
        if selectedTool == .arrow || selectedTool == .line {
            return CGRect(x: start.x, y: start.y, width: end.x - start.x, height: end.y - start.y)
        }

        if isAspectLocked && (selectedTool == .rectangle || selectedTool == .circle) {
            let dx = end.x - start.x
            let dy = end.y - start.y
            let pixelWidth = max(1.0, imagePixelSize.width)
            let pixelHeight = max(1.0, imagePixelSize.height)
            let side = max(abs(dx * pixelWidth), abs(dy * pixelHeight))
            let constrainedEnd = CGPoint(
                x: start.x + ((dx >= 0 ? side : -side) / pixelWidth),
                y: start.y + ((dy >= 0 ? side : -side) / pixelHeight)
            )
            return CGRect(
                x: min(start.x, constrainedEnd.x),
                y: min(start.y, constrainedEnd.y),
                width: abs(constrainedEnd.x - start.x),
                height: abs(constrainedEnd.y - start.y)
            )
        }

        return CGRect(
            x: min(start.x, end.x),
            y: min(start.y, end.y),
            width: abs(end.x - start.x),
            height: abs(end.y - start.y)
        )
    }

    func commitTextAnnotation() {
        guard let pos = textEditPosition, !textEditValue.isEmpty else {
            cancelTextAnnotation()
            return
        }
        // Size the rect based on the chosen font size (normalized to image)
        let normFontHeight = textFontSize / 500.0 // approximate normalized height
        let textWidth = max(0.05, CGFloat(textEditValue.count) * normFontHeight * 0.6)
        let rect = CGRect(x: pos.x, y: pos.y - normFontHeight / 2, width: textWidth, height: normFontHeight)
        recordHistory()
        annotations.append(ScreenshotAnnotation(
            tool: .text,
            rect: rect,
            color: selectedColor,
            textColor: numberTextColor,
            lineWidth: lineWidth,
            text: textEditValue,
            points: [],
            fontSize: textFontSize,
            fontFamily: textFontFamily,
            isBold: textIsBold,
            isItalic: textIsItalic,
            isUnderlined: textIsUnderlined,
            redactionBlurPreset: redactionBlurPreset,
            arrowStyle: selectedArrowStylePreset
        ))
        markDirty()
        textEditPosition = nil
        textEditValue = ""
        isEditingText = false
    }

    func cancelTextAnnotation() {
        textEditPosition = nil
        textEditValue = ""
        isEditingText = false
    }

    func placeNumberAnnotation(at position: CGPoint) {
        guard imagePixelSize.width > 0, imagePixelSize.height > 0 else { return }
        let sidePixels = currentNumberSidePixels()
        let normW = sidePixels / imagePixelSize.width
        let normH = sidePixels / imagePixelSize.height
        let rect = CGRect(
            x: position.x - normW / 2,
            y: position.y - normH / 2,
            width: normW,
            height: normH
        )
        recordHistory()
        annotations.append(ScreenshotAnnotation(
            tool: .number,
            rect: rect,
            color: selectedColor,
            textColor: numberTextColor,
            lineWidth: lineWidth,
            text: "\(nextNumberLabel)",
            points: [],
            redactionBlurPreset: redactionBlurPreset,
            arrowStyle: selectedArrowStylePreset
        ))
        markDirty()
        nextNumberLabel += 1
    }

    func selectedRedactionBlurPreset() -> RedactionBlurPreset? {
        guard selectedAnnotationIsBlur, let index = selectedAnnotationIndex else { return nil }
        return annotations[index].redactionBlurPreset
    }

    func selectedNumberBadgeColor() -> Color? {
        guard selectedAnnotationIsNumber, let index = selectedAnnotationIndex else { return nil }
        return annotations[index].color
    }

    func selectedNumberTextColor() -> Color? {
        guard selectedAnnotationIsNumber, let index = selectedAnnotationIndex else { return nil }
        return annotations[index].textColor
    }

    func selectedNumberSizeMultiplier() -> CGFloat? {
        guard selectedAnnotationIsNumber else { return nil }
        let currentWidthPixels = annotations[selectedAnnotationIndex ?? 0].rect.width * imagePixelSize.width
        let baseWidthPixels = baseNumberSidePixels()
        guard baseWidthPixels > 0 else { return nil }
        return currentWidthPixels / baseWidthPixels
    }

    @discardableResult
    func updateSelectedNumberSizeMultiplier(_ multiplier: CGFloat) -> Bool {
        guard selectedAnnotationIsNumber,
              let index = selectedAnnotationIndex,
              imagePixelSize.width > 0,
              imagePixelSize.height > 0 else {
            return false
        }

        recordHistory()
        var annotation = annotations[index]
        let sidePixels = max(numberCircleMinimumDisplayPixels, baseNumberSidePixels() * multiplier)
        let normW = sidePixels / imagePixelSize.width
        let normH = sidePixels / imagePixelSize.height
        let center = CGPoint(x: annotation.rect.midX, y: annotation.rect.midY)
        annotation.rect = CGRect(
            x: center.x - normW / 2,
            y: center.y - normH / 2,
            width: normW,
            height: normH
        )
        annotations[index] = annotation
        markDirty()
        return true
    }

    @discardableResult
    func updateSelectedNumberBadgeColor(_ color: Color) -> Bool {
        guard selectedAnnotationIsNumber, let index = selectedAnnotationIndex else {
            return false
        }

        recordHistory()
        annotations[index].color = color
        markDirty()
        return true
    }

    @discardableResult
    func updateSelectedNumberTextColor(_ color: Color) -> Bool {
        guard selectedAnnotationIsNumber, let index = selectedAnnotationIndex else {
            return false
        }

        recordHistory()
        annotations[index].textColor = color
        markDirty()
        return true
    }

    @discardableResult
    func updateSelectedRedactionBlurPreset(_ preset: RedactionBlurPreset) -> Bool {
        guard selectedAnnotationIsBlur, let index = selectedAnnotationIndex else {
            return false
        }

        recordHistory()
        annotations[index].redactionBlurPreset = preset
        markDirty()
        return true
    }

    private func renderFinalImage() -> NSBitmapImageRep? {
        precondition(Thread.isMainThread, "Screenshot export rendering must run on the main thread.")

        guard let original = originalImage, imagePixelSize.width > 0 else { return nil }

        let pixelW = imagePixelSize.width
        let pixelH = imagePixelSize.height

        // Determine crop region in pixels
        let cropPixelRect: CGRect
        if let crop = cropRect {
            cropPixelRect = CGRect(
                x: crop.origin.x * pixelW,
                y: crop.origin.y * pixelH,
                width: crop.width * pixelW,
                height: crop.height * pixelH
            )
        } else {
            cropPixelRect = CGRect(origin: .zero, size: imagePixelSize)
        }

        let layout = ExportFrameLayout.make(
            imageSize: cropPixelRect.size,
            padding: canvasPadding,
            preset: exportFramePreset,
            horizontalAlignment: horizontalExportAlignment,
            verticalAlignment: verticalExportAlignment
        )
        let outputW = Int(layout.frameSize.width)
        let outputH = Int(layout.frameSize.height)
        guard outputW > 0 && outputH > 0 else { return nil }

        guard let result = NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: outputW,
            pixelsHigh: outputH,
            bitsPerSample: 8,
            samplesPerPixel: 4,
            hasAlpha: true,
            isPlanar: false,
            colorSpaceName: .deviceRGB,
            bytesPerRow: 0,
            bitsPerPixel: 0
        ),
        let nsContext = NSGraphicsContext(bitmapImageRep: result) else {
            return nil
        }
        result.size = NSSize(width: outputW, height: outputH)
        let context = nsContext.cgContext
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = nsContext
        defer { NSGraphicsContext.restoreGraphicsState() }

        if backgroundStyle != .transparent {
            let fullRect = CGRect(x: 0, y: 0, width: outputW, height: outputH)
            if backgroundStyle == .solid {
                context.setFillColor(NSColor(backgroundColor).cgColor)
                context.fill(fullRect)
            } else if backgroundStyle == .gradient {
                let colors = [NSColor(backgroundColor).cgColor, NSColor(backgroundSecondaryColor).cgColor] as CFArray
                let colorSpace = CGColorSpaceCreateDeviceRGB()
                if let gradient = CGGradient(colorsSpace: colorSpace, colors: colors, locations: [0, 1]) {
                    context.drawLinearGradient(gradient, start: CGPoint(x: 0, y: outputH), end: CGPoint(x: outputW, y: 0), options: [])
                }
            } else if backgroundStyle == .wallpaper,
                      let wallpaperImage,
                      let wallpaperCGImage = wallpaperImage.cgImage(forProposedRect: nil, context: nil, hints: nil) {
                drawWallpaper(wallpaperCGImage, in: fullRect, context: context)
            }
        }

        let imageRect = layout.imageRect
        let imageCornerRadius = max(0, min(canvasCornerRadius, min(imageRect.width, imageRect.height) / 2))
        let imageClipPath = CGPath(
            roundedRect: imageRect,
            cornerWidth: imageCornerRadius,
            cornerHeight: imageCornerRadius,
            transform: nil
        )
        let sourceCGImage = original.cgImage(forProposedRect: nil, context: nil, hints: nil)

        // Draw the rounded screenshot card (image + annotations) inside a transparency
        // layer so the drop shadow is cast from the card's real alpha. This keeps a
        // window capture's transparent rounded corners transparent instead of filling
        // them with a solid shadow color, matching the live preview.
        context.saveGState()
        if canvasShadowRadius > 0 {
            context.setShadow(
                offset: .zero,
                blur: canvasShadowRadius,
                color: NSColor.black.withAlphaComponent(0.25).cgColor
            )
        }
        context.beginTransparencyLayer(auxiliaryInfo: nil)

        context.saveGState()
        context.addPath(imageClipPath)
        context.clip()

        // Draw the original image (cropped)
        if let cgImage = sourceCGImage {
            let cropCGRect = CGRect(
                x: cropPixelRect.origin.x,
                y: pixelH - cropPixelRect.origin.y - cropPixelRect.height, // flip Y for CG
                width: cropPixelRect.width,
                height: cropPixelRect.height
            )
            if let croppedCG = cgImage.cropping(to: cropCGRect) {
                context.draw(croppedCG, in: imageRect)
            }
        }

        // Draw annotations
        for annotation in annotations {
            drawAnnotationCG(
                annotation,
                in: context,
                cropOrigin: cropPixelRect.origin,
                outputSize: CGSize(width: outputW, height: outputH),
                fullSize: imagePixelSize,
                contentOffset: imageRect.origin,
                imageHeight: imageRect.height,
                sourceCGImage: sourceCGImage
            )
        }

        // Remove the card clip before ending the layer so the shadow (applied on
        // composite) is not clipped to the image bounds.
        context.restoreGState()
        context.endTransparencyLayer()
        context.restoreGState()

        return result
    }

    private func drawAnnotationCG(_ annotation: ScreenshotAnnotation, in ctx: CGContext, cropOrigin: CGPoint, outputSize: CGSize, fullSize: CGSize, contentOffset: CGPoint, imageHeight: CGFloat, sourceCGImage: CGImage? = nil) {
        let imageOutputHeight = imageHeight
        // Convert normalized rect to pixel coords relative to crop
        let pixelRect = CGRect(
            x: (annotation.rect.origin.x * fullSize.width) - cropOrigin.x + contentOffset.x,
            y: imageOutputHeight - ((annotation.rect.origin.y * fullSize.height) - cropOrigin.y + annotation.rect.height * fullSize.height) + contentOffset.y, // flip Y
            width: annotation.rect.width * fullSize.width,
            height: annotation.rect.height * fullSize.height
        )

        let nsColor = NSColor(annotation.color)
        let cgColor = nsColor.cgColor
        ctx.setStrokeColor(cgColor)
        let strokeWidth = exportStrokeWidth(baseWidth: annotation.lineWidth, outputWidth: outputSize.width)
        ctx.setLineWidth(strokeWidth)

        switch annotation.tool {
        case .rectangle:
            if annotation.fillColor != .clear {
                let fillCGColor = NSColor(annotation.fillColor).cgColor
                ctx.setFillColor(fillCGColor)
                ctx.fill(pixelRect)
            }
            ctx.stroke(pixelRect)

        case .circle:
            if annotation.fillColor != .clear {
                let fillCGColor = NSColor(annotation.fillColor).cgColor
                ctx.setFillColor(fillCGColor)
                ctx.fillEllipse(in: pixelRect)
            }
            ctx.strokeEllipse(in: pixelRect)

        case .arrow:
            let line = linePoints(for: annotation)
            let startTopLeft = CGPoint(
                x: (line.start.x * fullSize.width) - cropOrigin.x + contentOffset.x,
                y: (line.start.y * fullSize.height) - cropOrigin.y + contentOffset.y
            )
            let endTopLeft = CGPoint(
                x: (line.end.x * fullSize.width) - cropOrigin.x + contentOffset.x,
                y: (line.end.y * fullSize.height) - cropOrigin.y + contentOffset.y
            )
            let controlTopLeft = arrowControlPoint(start: startTopLeft, end: endTopLeft, style: annotation.arrowStyle)
            let start = CGPoint(
                x: startTopLeft.x,
                y: outputSize.height - startTopLeft.y
            )
            let end = CGPoint(
                x: endTopLeft.x,
                y: outputSize.height - endTopLeft.y
            )
            let control = CGPoint(
                x: controlTopLeft.x,
                y: outputSize.height - controlTopLeft.y
            )
            let headLength = max(26, strokeWidth * 5.0)
            let headAngle: CGFloat = .pi / 6
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
            ctx.move(to: start)
            switch annotation.arrowStyle {
            case .straight:
                ctx.addLine(to: shaftEnd)
            case .curvedLeft, .curvedRight:
                ctx.addQuadCurve(to: shaftEnd, control: control)
            }
            ctx.strokePath()

            // Filled triangular arrowhead
            ctx.setFillColor(cgColor)
            ctx.move(to: end)
            ctx.addLine(to: wing1)
            ctx.addLine(to: wing2)
            ctx.closePath()
            ctx.fillPath()

        case .line:
            let line = linePoints(for: annotation)
            let start = CGPoint(
                x: (line.start.x * fullSize.width) - cropOrigin.x + contentOffset.x,
                y: imageOutputHeight - ((line.start.y * fullSize.height) - cropOrigin.y) + contentOffset.y
            )
            let end = CGPoint(
                x: (line.end.x * fullSize.width) - cropOrigin.x + contentOffset.x,
                y: imageOutputHeight - ((line.end.y * fullSize.height) - cropOrigin.y) + contentOffset.y
            )
            ctx.move(to: start)
            ctx.addLine(to: end)
            ctx.strokePath()

        case .pencil:
            if annotation.points.count > 1 {
                let scaledPoints = annotation.points.map { pt in
                    CGPoint(
                        x: (pt.x * fullSize.width) - cropOrigin.x + contentOffset.x,
                        y: imageOutputHeight - ((pt.y * fullSize.height) - cropOrigin.y) + contentOffset.y
                    )
                }
                ctx.move(to: scaledPoints[0])
                for pt in scaledPoints.dropFirst() {
                    ctx.addLine(to: pt)
                }
                ctx.strokePath()
            }

        case .blur:
            drawCheckerboardRedaction(in: ctx, rect: pixelRect, preset: annotation.redactionBlurPreset)

        case .number:
            // Draw filled circle
            ctx.setFillColor(cgColor)
            ctx.fillEllipse(in: pixelRect)

            // Draw number centered in circle
            let numberStr = annotation.text as NSString
            let numFontSize = pixelRect.width * numberCircleFontRatio
            let baseFont = NSFont.systemFont(ofSize: numFontSize, weight: .bold)
            let numFont: NSFont
            if let roundedDesc = baseFont.fontDescriptor.withDesign(.rounded) {
                numFont = NSFont(descriptor: roundedDesc, size: numFontSize) ?? baseFont
            } else {
                numFont = baseFont
            }
            let numAttrs: [NSAttributedString.Key: Any] = [
                .font: numFont,
                .foregroundColor: NSColor(annotation.textColor),
            ]
            let numTextSize = numberStr.size(withAttributes: numAttrs)
            let numTextRect = CGRect(
                x: pixelRect.midX - numTextSize.width / 2,
                y: pixelRect.midY - numTextSize.height / 2,
                width: numTextSize.width,
                height: numTextSize.height
            )
            numberStr.draw(in: numTextRect, withAttributes: numAttrs)

        case .text:
            let str = annotation.text as NSString
            let fontSize = textFontSize(annotation.fontSize, forRenderedImageWidth: fullSize.width)
            let font = exportTextFont(
                family: annotation.fontFamily,
                size: fontSize,
                isBold: annotation.isBold,
                isItalic: annotation.isItalic
            )
            let attrs: [NSAttributedString.Key: Any] = [
                .font: font,
                .foregroundColor: nsColor,
                .underlineStyle: annotation.isUnderlined ? NSUnderlineStyle.single.rawValue : 0,
            ]
            // NSGraphicsContext.current is set to the unflipped bitmap context,
            // so NSString.draw uses CG (bottom-left) coordinates directly.
            // pixelRect is already in CG space, so no manual flip is needed.
            let textSize = str.size(withAttributes: attrs)
            let drawPoint = CGPoint(
                x: pixelRect.midX - textSize.width / 2,
                y: pixelRect.midY - textSize.height / 2
            )
            str.draw(at: drawPoint, withAttributes: attrs)

        case .crop, .move:
            break
        }
    }

    private func exportStrokeWidth(baseWidth: CGFloat, outputWidth: CGFloat) -> CGFloat {
        let widthScale = max(1.0, outputWidth / 900.0)
        return baseWidth * widthScale
    }

    private func exportTextFont(family: String, size: CGFloat, isBold: Bool, isItalic: Bool) -> NSFont {
        if family == textSystemFontFamily {
            let weight: NSFont.Weight = isBold ? .bold : .regular
            let baseFont = NSFont.systemFont(ofSize: size, weight: weight)
            guard isItalic else { return baseFont }
            return NSFontManager.shared.convert(baseFont, toHaveTrait: .italicFontMask)
        }

        let traits: NSFontTraitMask = isItalic ? .italicFontMask : []
        let weight = isBold ? 9 : 5
        return NSFontManager.shared.font(withFamily: family, traits: traits, weight: weight, size: size)
            ?? NSFont(name: family, size: size)
            ?? NSFont.systemFont(ofSize: size, weight: isBold ? .bold : .regular)
    }

    private func drawWallpaper(_ image: CGImage, in rect: CGRect, context: CGContext) {
        let imageSize = CGSize(width: image.width, height: image.height)
        guard imageSize.width > 0, imageSize.height > 0, rect.width > 0, rect.height > 0 else { return }

        let scale = max(rect.width / imageSize.width, rect.height / imageSize.height)
        let drawSize = CGSize(width: imageSize.width * scale, height: imageSize.height * scale)
        let drawRect = CGRect(
            x: rect.midX - drawSize.width / 2,
            y: rect.midY - drawSize.height / 2,
            width: drawSize.width,
            height: drawSize.height
        )

        context.saveGState()
        context.clip(to: rect)
        context.draw(image, in: drawRect)
        context.restoreGState()
    }

    private var selectedAnnotationIsText: Bool {
        guard let index = selectedAnnotationIndex, annotations.indices.contains(index) else {
            return false
        }
        return annotations[index].tool == .text
    }

    private var selectedAnnotationIsNumber: Bool {
        guard let index = selectedAnnotationIndex, annotations.indices.contains(index) else {
            return false
        }
        return annotations[index].tool == .number
    }

    private var selectedAnnotationIsBlur: Bool {
        guard let index = selectedAnnotationIndex, annotations.indices.contains(index) else {
            return false
        }
        return annotations[index].tool == .blur
    }

    private func baseNumberSidePixels() -> CGFloat {
        max(numberCircleMinPixels, min(numberCircleMaxPixels, imagePixelSize.width * numberCircleSizeRatio))
    }

    private func currentNumberSidePixels() -> CGFloat {
        max(numberCircleMinimumDisplayPixels, baseNumberSidePixels() * numberSizeMultiplier)
    }

    private func colorsEqual(_ lhs: Color, _ rhs: Color) -> Bool {
        guard let left = NSColor(lhs).usingColorSpace(.deviceRGB),
              let right = NSColor(rhs).usingColorSpace(.deviceRGB) else {
            return false
        }

        var lr: CGFloat = 0
        var lg: CGFloat = 0
        var lb: CGFloat = 0
        var la: CGFloat = 0
        var rr: CGFloat = 0
        var rg: CGFloat = 0
        var rb: CGFloat = 0
        var ra: CGFloat = 0
        left.getRed(&lr, green: &lg, blue: &lb, alpha: &la)
        right.getRed(&rr, green: &rg, blue: &rb, alpha: &ra)

        return abs(lr - rr) < 0.0001 &&
            abs(lg - rg) < 0.0001 &&
            abs(lb - rb) < 0.0001 &&
            abs(la - ra) < 0.0001
    }
}
