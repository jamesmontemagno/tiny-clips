import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct ScreenshotEditorViewportEventMonitor: NSViewRepresentable {
    let isEnabled: Bool
    let onZoom: (CGFloat, CGPoint) -> Void
    let onPan: (CGSize) -> Void

    func makeCoordinator() -> Coordinator {
        Coordinator(isEnabled: isEnabled, onZoom: onZoom, onPan: onPan)
    }

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        context.coordinator.install(for: view)
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        context.coordinator.isEnabled = isEnabled
        if !isEnabled {
            context.coordinator.resetSpaceState()
        }
        context.coordinator.onZoom = onZoom
        context.coordinator.onPan = onPan
    }

    static func dismantleNSView(_ nsView: NSView, coordinator: Coordinator) {
        coordinator.uninstall()
    }

    final class Coordinator {
        var isEnabled: Bool
        var onZoom: (CGFloat, CGPoint) -> Void
        var onPan: (CGSize) -> Void

        private weak var monitoredView: NSView?
        private var eventMonitor: Any?
        private var resignActiveObserver: NSObjectProtocol?
        private var isSpacePressed = false
        private var isSpaceDragging = false

        init(
            isEnabled: Bool,
            onZoom: @escaping (CGFloat, CGPoint) -> Void,
            onPan: @escaping (CGSize) -> Void
        ) {
            self.isEnabled = isEnabled
            self.onZoom = onZoom
            self.onPan = onPan
        }

        func install(for view: NSView) {
            monitoredView = view
            eventMonitor = NSEvent.addLocalMonitorForEvents(
                matching: [.scrollWheel, .magnify, .keyDown, .keyUp, .leftMouseDown, .leftMouseDragged, .leftMouseUp]
            ) { [weak self] event in
                self?.handle(event) ?? event
            }
            resignActiveObserver = NotificationCenter.default.addObserver(
                forName: NSApplication.didResignActiveNotification,
                object: nil,
                queue: .main
            ) { [weak self] _ in
                self?.resetSpaceState()
            }
        }

        func uninstall() {
            if let eventMonitor {
                NSEvent.removeMonitor(eventMonitor)
            }
            eventMonitor = nil
            if let resignActiveObserver {
                NotificationCenter.default.removeObserver(resignActiveObserver)
            }
            resignActiveObserver = nil
            resetSpaceState()
        }

        func resetSpaceState() {
            isSpacePressed = false
            isSpaceDragging = false
        }

        private func handle(_ event: NSEvent) -> NSEvent? {
            guard let view = monitoredView,
                  event.window === view.window,
                  isEnabled else {
                return event
            }

            switch event.type {
            case .keyDown where event.keyCode == 49:
                guard pointerIsInside(view),
                      !(view.window?.firstResponder is NSTextView) else {
                    return event
                }
                isSpacePressed = true
                return nil
            case .keyUp where event.keyCode == 49:
                guard isSpacePressed else { return event }
                isSpacePressed = false
                isSpaceDragging = false
                return nil
            case .leftMouseDown where isSpacePressed:
                guard contains(event, in: view) else { return event }
                isSpaceDragging = true
                return nil
            case .leftMouseDragged where isSpaceDragging:
                onPan(CGSize(width: event.deltaX, height: event.deltaY))
                return nil
            case .leftMouseUp where isSpaceDragging:
                isSpaceDragging = false
                return nil
            case .scrollWheel:
                guard contains(event, in: view) else { return event }
                if event.modifierFlags.contains(.command) {
                    onZoom(exp(event.scrollingDeltaY * 0.015), localPoint(for: event, in: view))
                } else {
                    onPan(CGSize(width: event.scrollingDeltaX, height: -event.scrollingDeltaY))
                }
                return nil
            case .magnify:
                guard contains(event, in: view) else { return event }
                onZoom(max(0.5, 1 + event.magnification), localPoint(for: event, in: view))
                return nil
            default:
                return event
            }
        }

        private func contains(_ event: NSEvent, in view: NSView) -> Bool {
            view.bounds.contains(view.convert(event.locationInWindow, from: nil))
        }

        private func localPoint(for event: NSEvent, in view: NSView) -> CGPoint {
            let point = view.convert(event.locationInWindow, from: nil)
            return CGPoint(x: point.x, y: view.bounds.height - point.y)
        }

        private func pointerIsInside(_ view: NSView) -> Bool {
            guard let window = view.window else { return false }
            let pointInWindow = window.convertPoint(fromScreen: NSEvent.mouseLocation)
            return view.bounds.contains(view.convert(pointInWindow, from: nil))
        }
    }
}

struct ScreenshotEditorScrollBars: View {
    let contentSize: CGSize
    let viewportSize: CGSize
    let panOffset: CGSize
    let onSetHorizontalPan: (CGFloat) -> Void
    let onSetVerticalPan: (CGFloat) -> Void

    var body: some View {
        ZStack {
            if contentSize.width > viewportSize.width {
                ZoomHorizontalScrollBar(
                    contentWidth: contentSize.width,
                    viewportWidth: viewportSize.width,
                    pan: panOffset.width,
                    onSetPan: onSetHorizontalPan
                )
                .padding(.horizontal, 12)
                .padding(.bottom, 6)
                .frame(maxHeight: .infinity, alignment: .bottom)
            }

            if contentSize.height > viewportSize.height {
                ZoomVerticalScrollBar(
                    contentHeight: contentSize.height,
                    viewportHeight: viewportSize.height,
                    pan: panOffset.height,
                    onSetPan: onSetVerticalPan
                )
                .padding(.trailing, 6)
                .padding(.vertical, 12)
                .frame(maxWidth: .infinity, alignment: .trailing)
            }
        }
    }
}

private struct ZoomHorizontalScrollBar: View {
    let contentWidth: CGFloat
    let viewportWidth: CGFloat
    let pan: CGFloat
    let onSetPan: (CGFloat) -> Void

    var body: some View {
        GeometryReader { geometry in
            let trackWidth = geometry.size.width
            let thumbWidth = max(28, trackWidth * min(1, viewportWidth / contentWidth))
            let travel = max(0, trackWidth - thumbWidth)
            let maxPan = max(0, (contentWidth - viewportWidth) / 2)
            let progress = maxPan > 0 ? (maxPan - pan) / (2 * maxPan) : 0

            ZStack(alignment: .leading) {
                Capsule().fill(.black.opacity(0.12))
                Capsule()
                    .fill(.secondary.opacity(0.55))
                    .frame(width: thumbWidth)
                    .offset(x: travel * progress)
            }
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { value in
                        let thumbPosition = min(travel, max(0, value.location.x - thumbWidth / 2))
                        let newProgress = travel > 0 ? thumbPosition / travel : 0
                        onSetPan(maxPan - newProgress * 2 * maxPan)
                    }
            )
            .accessibilityElement()
            .accessibilityLabel("Horizontal canvas scroll")
            .accessibilityValue("\(Int(progress * 100)) percent")
            .focusable()
            .onMoveCommand { direction in
                switch direction {
                case .left:
                    onSetPan(min(maxPan, pan + 40))
                case .right:
                    onSetPan(max(-maxPan, pan - 40))
                default:
                    break
                }
            }
            .accessibilityAdjustableAction { direction in
                switch direction {
                case .increment:
                    onSetPan(max(-maxPan, pan - 40))
                case .decrement:
                    onSetPan(min(maxPan, pan + 40))
                @unknown default:
                    break
                }
            }
        }
        .frame(height: 8)
    }
}

private struct ZoomVerticalScrollBar: View {
    let contentHeight: CGFloat
    let viewportHeight: CGFloat
    let pan: CGFloat
    let onSetPan: (CGFloat) -> Void

    var body: some View {
        GeometryReader { geometry in
            let trackHeight = geometry.size.height
            let thumbHeight = max(28, trackHeight * min(1, viewportHeight / contentHeight))
            let travel = max(0, trackHeight - thumbHeight)
            let maxPan = max(0, (contentHeight - viewportHeight) / 2)
            let progress = maxPan > 0 ? (maxPan - pan) / (2 * maxPan) : 0

            ZStack(alignment: .top) {
                Capsule().fill(.black.opacity(0.12))
                Capsule()
                    .fill(.secondary.opacity(0.55))
                    .frame(height: thumbHeight)
                    .offset(y: travel * progress)
            }
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { value in
                        let thumbPosition = min(travel, max(0, value.location.y - thumbHeight / 2))
                        let newProgress = travel > 0 ? thumbPosition / travel : 0
                        onSetPan(maxPan - newProgress * 2 * maxPan)
                    }
            )
            .accessibilityElement()
            .accessibilityLabel("Vertical canvas scroll")
            .accessibilityValue("\(Int(progress * 100)) percent")
            .focusable()
            .onMoveCommand { direction in
                switch direction {
                case .up:
                    onSetPan(min(maxPan, pan + 40))
                case .down:
                    onSetPan(max(-maxPan, pan - 40))
                default:
                    break
                }
            }
            .accessibilityAdjustableAction { direction in
                switch direction {
                case .increment:
                    onSetPan(max(-maxPan, pan - 40))
                case .decrement:
                    onSetPan(min(maxPan, pan + 40))
                @unknown default:
                    break
                }
            }
        }
        .frame(width: 8)
    }
}

private struct ExportAlignmentGrid: View {
    @Binding var horizontalAlignment: ExportHorizontalAlignment
    @Binding var verticalAlignment: ExportVerticalAlignment

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Image alignment")
                .font(.caption)
                .foregroundStyle(.secondary)

            LazyVGrid(
                columns: Array(repeating: GridItem(.fixed(30), spacing: 4), count: 3),
                spacing: 4
            ) {
                ForEach(ExportVerticalAlignment.allCases) { vertical in
                    ForEach(ExportHorizontalAlignment.allCases) { horizontal in
                        alignmentButton(horizontal: horizontal, vertical: vertical)
                    }
                }
            }
            .padding(4)
            .background(.secondary.opacity(0.08), in: RoundedRectangle(cornerRadius: 8))
        }
        .accessibilityElement(children: .contain)
    }

    private func alignmentButton(
        horizontal: ExportHorizontalAlignment,
        vertical: ExportVerticalAlignment
    ) -> some View {
        let isSelected = horizontalAlignment == horizontal && verticalAlignment == vertical

        return Button {
            horizontalAlignment = horizontal
            verticalAlignment = vertical
        } label: {
            RoundedRectangle(cornerRadius: 5)
            .fill(isSelected ? Color.accentColor : .secondary.opacity(0.14))
            .frame(width: 30, height: 30)
            .overlay {
                RoundedRectangle(cornerRadius: 2)
                    .fill(isSelected ? .white : .secondary)
                    .frame(width: 10, height: 6)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: previewAlignment(horizontal: horizontal, vertical: vertical))
                    .padding(5)
            }
        }
        .buttonStyle(.plain)
        .help("\(vertical.label) \(horizontal.label)")
        .accessibilityLabel("\(vertical.label) \(horizontal.label) image alignment")
        .accessibilityValue(isSelected ? "Selected" : "Not selected")
    }

    private func previewAlignment(
        horizontal: ExportHorizontalAlignment,
        vertical: ExportVerticalAlignment
    ) -> Alignment {
        switch (horizontal, vertical) {
        case (.leading, .top): return .topLeading
        case (.center, .top): return .top
        case (.trailing, .top): return .topTrailing
        case (.leading, .center): return .leading
        case (.center, .center): return .center
        case (.trailing, .center): return .trailing
        case (.leading, .bottom): return .bottomLeading
        case (.center, .bottom): return .bottom
        case (.trailing, .bottom): return .bottomTrailing
        }
    }
}

// MARK: - Editor View

struct ScreenshotEditorView: View {
    let imageURL: URL
    let initialSaveURL: URL
    let deleteSourceAfterSave: Bool
    let onDone: (URL?) -> Void

    @StateObject private var viewModel: ScreenshotEditorViewModel
    @State private var isSaving = false
    @State private var splitVisibility: NavigationSplitViewVisibility = .automatic
    @State private var activePopover: EditorPopover?
    @State private var isBackgroundSectionExpanded = true
    @State private var showExitConfirmation = false
    @State private var showClearAnnotationsConfirmation = false
    @State private var currentSaveURL: URL
    @State private var lastSavedURL: URL?
    @State private var zoomScale: CGFloat = 1
    @State private var panOffset: CGSize = .zero
    @State private var viewportSize: CGSize = .zero

    init(
        imageURL: URL,
        initialSaveURL: URL,
        deleteSourceAfterSave: Bool,
        onDone: @escaping (URL?) -> Void
    ) {
        self.imageURL = imageURL
        self.initialSaveURL = initialSaveURL
        self.deleteSourceAfterSave = deleteSourceAfterSave
        self.onDone = onDone
        _viewModel = StateObject(wrappedValue: ScreenshotEditorViewModel(url: imageURL))
        _currentSaveURL = State(initialValue: initialSaveURL)
        _lastSavedURL = State(initialValue: nil)
    }

    private var inspectorTool: EditTool {
        viewModel.inspectorTool
    }

    private var primaryColorLabel: String? {
        switch inspectorTool {
        case .rectangle, .circle:
            return "Border"
        case .arrow, .line, .pencil, .text:
            return "Color"
        case .number:
            return "Badge"
        default:
            return nil
        }
    }

    private var showsFillColorPicker: Bool {
        inspectorTool == .rectangle || inspectorTool == .circle
    }

    private var showsNumberTextColorPicker: Bool {
        inspectorTool == .number
    }

    private var primaryColorBinding: Binding<Color> {
        Binding(
            get: {
                viewModel.selectedNumberBadgeColor() ?? viewModel.selectedColor
            },
            set: { newValue in
                if !viewModel.updateSelectedNumberBadgeColor(newValue) {
                    viewModel.selectedColor = newValue
                }
            }
        )
    }

    private var numberTextColorBinding: Binding<Color> {
        Binding(
            get: {
                viewModel.selectedNumberTextColor() ?? viewModel.numberTextColor
            },
            set: { newValue in
                if !viewModel.updateSelectedNumberTextColor(newValue) {
                    viewModel.numberTextColor = newValue
                }
            }
        )
    }

    private var numberSizeBinding: Binding<CGFloat> {
        Binding(
            get: {
                viewModel.selectedNumberSizeMultiplier() ?? viewModel.numberSizeMultiplier
            },
            set: { newValue in
                if !viewModel.updateSelectedNumberSizeMultiplier(newValue) {
                    viewModel.numberSizeMultiplier = newValue
                }
            }
        )
    }

    private var blurPresetBinding: Binding<RedactionBlurPreset> {
        Binding(
            get: {
                viewModel.selectedRedactionBlurPreset() ?? viewModel.redactionBlurPreset
            },
            set: { newValue in
                if !viewModel.updateSelectedRedactionBlurPreset(newValue) {
                    viewModel.redactionBlurPreset = newValue
                }
            }
        )
    }

    private var arrowStyleBinding: Binding<ArrowStyle> {
        Binding(
            get: {
                viewModel.selectedArrowStyle() ?? viewModel.selectedArrowStylePreset
            },
            set: { newValue in
                if !viewModel.updateSelectedArrowStyle(newValue) {
                    viewModel.selectedArrowStylePreset = newValue
                }
            }
        )
    }

    private var textFontFamilyBinding: Binding<String> {
        Binding(
            get: {
                viewModel.selectedTextFontFamily() ?? viewModel.textFontFamily
            },
            set: { newValue in
                if !viewModel.updateSelectedTextFontFamily(newValue) {
                    viewModel.textFontFamily = newValue
                }
            }
        )
    }

    private var textBoldBinding: Binding<Bool> {
        Binding(
            get: {
                viewModel.selectedTextBold() ?? viewModel.textIsBold
            },
            set: { newValue in
                if !viewModel.updateSelectedTextBold(newValue) {
                    viewModel.textIsBold = newValue
                }
            }
        )
    }

    private var textItalicBinding: Binding<Bool> {
        Binding(
            get: {
                viewModel.selectedTextItalic() ?? viewModel.textIsItalic
            },
            set: { newValue in
                if !viewModel.updateSelectedTextItalic(newValue) {
                    viewModel.textIsItalic = newValue
                }
            }
        )
    }

    private var textUnderlineBinding: Binding<Bool> {
        Binding(
            get: {
                viewModel.selectedTextUnderline() ?? viewModel.textIsUnderlined
            },
            set: { newValue in
                if !viewModel.updateSelectedTextUnderline(newValue) {
                    viewModel.textIsUnderlined = newValue
                }
            }
        )
    }

    var body: some View {
        NavigationSplitView(columnVisibility: $splitVisibility) {
            sidebar
                .navigationSplitViewColumnWidth(min: 160, ideal: 220, max: 320)
        } detail: {
            VStack(spacing: 0) {
                GeometryReader { geo in
                    ZStack {
                        ScreenshotEditorCanvasView(
                            viewModel: viewModel,
                            containerSize: geo.size,
                            zoomScale: zoomScale,
                            panOffset: panOffset
                        )

                        ScreenshotEditorViewportEventMonitor(
                            isEnabled: !viewModel.isEditingText,
                            onZoom: { multiplier, focalPoint in
                                setZoom(zoomScale * multiplier, focalPoint: focalPoint)
                            },
                            onPan: panCanvas
                        )
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .allowsHitTesting(false)

                        ScreenshotEditorScrollBars(
                            contentSize: zoomContentSize(in: geo.size),
                            viewportSize: geo.size,
                            panOffset: panOffset,
                            onSetHorizontalPan: { panOffset.width = $0 },
                            onSetVerticalPan: { panOffset.height = $0 }
                        )
                    }
                    .onAppear {
                        updateViewportSize(geo.size)
                    }
                    .onChange(of: geo.size) { _, newSize in
                        updateViewportSize(newSize)
                    }
                }
                .background(Color(nsColor: .windowBackgroundColor).opacity(0.5))
                .clipped()

                Divider()

                exportControls
                    .padding(14)
                    .background(.regularMaterial)
            }
        }
        .navigationSplitViewStyle(.balanced)
        .focusedSceneValue(
            \.screenshotEditorCommandActions,
            ScreenshotEditorCommandActions(
                save: saveCurrentImage,
                saveAs: beginSaveAs,
                revealInFinder: openSaveFolder,
                undo: viewModel.undo,
                redo: viewModel.redo,
                copy: viewModel.copyToClipboard,
                clearAnnotations: { showClearAnnotationsConfirmation = true },
                zoomIn: zoomIn,
                zoomOut: zoomOut,
                fitZoom: fitZoom,
                canUndo: viewModel.canUndo,
                canRedo: viewModel.canRedo,
                hasAnnotations: viewModel.hasAnnotations,
                isEditingText: viewModel.isEditingText,
                canZoomIn: !viewModel.isEditingText && zoomScale < ScreenshotEditorZoomMath.maximumScale,
                canZoomOut: !viewModel.isEditingText && zoomScale > ScreenshotEditorZoomMath.minimumScale
            )
        )
        .onExitCommand {
            handleEscape()
        }
        .confirmationDialog("Discard changes?", isPresented: $showExitConfirmation, titleVisibility: .visible) {
            Button("Discard Changes", role: .destructive) {
                onDone(lastSavedURL)
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("You have unsaved changes. Are you sure you want to exit?")
        }
        .confirmationDialog("Clear all annotations?", isPresented: $showClearAnnotationsConfirmation, titleVisibility: .visible) {
            Button("Clear Annotations", role: .destructive) {
                viewModel.clearAnnotations()
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This removes all annotations from the screenshot.")
        }
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                Button {
                    viewModel.undo()
                } label: {
                    Label("Undo", systemImage: "arrow.uturn.backward")
                }
                .disabled(!viewModel.canUndo)
                .keyboardShortcut("z", modifiers: .command)
                .help("Undo the last edit.")

                Button {
                    viewModel.redo()
                } label: {
                    Label("Redo", systemImage: "arrow.uturn.forward")
                }
                .disabled(!viewModel.canRedo)
                .keyboardShortcut("Z", modifiers: [.command, .shift])
                .help("Redo the last undone edit.")

                Button {
                    showClearAnnotationsConfirmation = true
                } label: {
                    Label("Clear Annotations", systemImage: "eraser")
                }
                .disabled(!viewModel.hasAnnotations)
                .help("Clear all annotations.")

                Button {
                    viewModel.copyToClipboard()
                } label: {
                    Label("Copy", systemImage: "doc.on.doc")
                }
                .help("Copy the edited image to the clipboard.")

                Button {
                    saveCurrentImage()
                } label: {
                    Label("Save", systemImage: "internaldrive")
                        .labelStyle(.titleAndIcon)
                }
                .keyboardShortcut("s", modifiers: .command)
                .help("Save the edited image to the current file.")

                Button {
                    activePopover = .saveOptions
                } label: {
                    Label("Save As", systemImage: "square.and.arrow.down.on.square")
                        .labelStyle(.titleAndIcon)
                }
                .help("Choose export options and save the edited image to a new file.")
                .popover(item: $activePopover) { item in
                    switch item {
                    case .saveOptions:
                        saveOptionsPopover
                    }
                }
            }
        }
        .onChange(of: viewModel.canvasPadding) { _, _ in constrainPan() }
        .onChange(of: viewModel.exportFramePreset) { _, _ in constrainPan() }
        .onChange(of: viewModel.horizontalExportAlignment) { _, _ in constrainPan() }
        .onChange(of: viewModel.verticalExportAlignment) { _, _ in constrainPan() }
        .disabled(isSaving)
        .overlay {
            if isSaving {
                ScreenshotEditorProgressOverlayView(title: "Saving…")
            }
        }
    }

    private var sidebar: some View {
        List {
            Section("Tools") {
                toolGrid
                    .listRowInsets(EdgeInsets(top: 2, leading: 2, bottom: 4, trailing: 2))
            }

            if viewModel.showsAnyStyleControls {
                Section("Style") {
                    styleControls
                        .listRowInsets(EdgeInsets(top: 2, leading: 4, bottom: 4, trailing: 4))
                }
            }

            Section {
                DisclosureGroup(isExpanded: $isBackgroundSectionExpanded) {
                    backgroundControls
                        .padding(.top, 8)
                } label: {
                    Label("Background", systemImage: "photo.on.rectangle")
                }
                .listRowInsets(EdgeInsets(top: 2, leading: 4, bottom: 4, trailing: 4))
            }
        }
        .listStyle(.sidebar)
    }

    private var toolGrid: some View {
        LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 4), count: 3), spacing: 4) {
            ForEach(EditTool.allCases, id: \.self) { tool in
                Button {
                    viewModel.selectedTool = tool
                } label: {
                    VStack(spacing: 1) {
                        Image(systemName: tool.rawValue)
                            .font(.system(size: 12))
                        Text(tool.label)
                            .font(.system(size: 8))
                            .lineLimit(1)
                            .minimumScaleFactor(0.8)
                    }
                    .frame(maxWidth: .infinity, minHeight: 34)
                    .background(viewModel.selectedTool == tool ? Color.accentColor.opacity(0.2) : Color.clear)
                    .clipShape(RoundedRectangle(cornerRadius: 6))
                    .contentShape(RoundedRectangle(cornerRadius: 6))
                }
                .buttonStyle(.plain)
                .accessibilityLabel("\(tool.label) tool")
                .accessibilityValue(viewModel.selectedTool == tool ? "Selected" : "Not selected")
            }
        }
    }

    private var styleControls: some View {
        VStack(alignment: .leading, spacing: 10) {
            if let primaryColorLabel {
                SwatchColorPicker(label: primaryColorLabel, color: primaryColorBinding, supportsOpacity: true)
            }

            if showsFillColorPicker {
                SwatchColorPicker(label: "Fill", color: $viewModel.selectedFillColor, supportsOpacity: true, allowsTransparent: true)
            }

            if showsNumberTextColorPicker {
                SwatchColorPicker(label: "Text", color: numberTextColorBinding, supportsOpacity: true)
            }

            if viewModel.showsTextStyleControls {
                Picker("Font", selection: textFontFamilyBinding) {
                    ForEach(viewModel.availableTextFontFamilies, id: \.self) { family in
                        Text(family).tag(family)
                    }
                }

                HStack(spacing: 8) {
                    TextStyleToggleButton(title: "Bold", systemImage: "bold", isOn: textBoldBinding)
                    TextStyleToggleButton(title: "Italic", systemImage: "italic", isOn: textItalicBinding)
                    TextStyleToggleButton(title: "Underline", systemImage: "underline", isOn: textUnderlineBinding)
                }
            }

            if viewModel.showsLineWidthControl {
                Picker("Stroke", selection: $viewModel.lineWidth) {
                    Text("1 px").tag(CGFloat(1))
                    Text("2 px").tag(CGFloat(2))
                    Text("4 px").tag(CGFloat(4))
                    Text("6 px").tag(CGFloat(6))
                    Text("8 px").tag(CGFloat(8))
                    Text("10 px").tag(CGFloat(10))
                }
            }

            if viewModel.showsArrowStyleControl {
                HStack(spacing: 8) {
                    ForEach(ArrowStyle.allCases) { style in
                        ArrowStyleButton(style: style, isSelected: arrowStyleBinding.wrappedValue == style) {
                            arrowStyleBinding.wrappedValue = style
                        }
                    }
                }
            }

            if viewModel.showsNumberSizeControl {
                Picker("Number size", selection: numberSizeBinding) {
                    Text("20%").tag(CGFloat(0.2))
                    Text("30%").tag(CGFloat(0.3))
                    Text("40%").tag(CGFloat(0.4))
                    Text("50%").tag(CGFloat(0.5))
                    Text("60%").tag(CGFloat(0.6))
                    Text("70%").tag(CGFloat(0.7))
                    Text("80%").tag(CGFloat(0.8))
                    Text("90%").tag(CGFloat(0.9))
                    Text("100%").tag(CGFloat(1.0))
                    Text("110%").tag(CGFloat(1.1))
                    Text("125%").tag(CGFloat(1.25))
                    Text("150%").tag(CGFloat(1.5))
                    Text("175%").tag(CGFloat(1.75))
                    Text("200%").tag(CGFloat(2.0))
                }
            }

            if viewModel.showsRedactionPresetControl {
                Picker("Redaction", selection: blurPresetBinding) {
                    ForEach(RedactionBlurPreset.allCases) { preset in
                        Text(preset.label).tag(preset)
                    }
                }
            }
        }
    }
    private var backgroundControls: some View {
        VStack(alignment: .leading, spacing: 10) {
            backgroundPresetSection("Solid", presets: solidBackgroundPresets)
            backgroundPresetSection("Gradient", presets: gradientBackgroundPresets)

            VStack(alignment: .leading, spacing: 8) {
                Text("Custom")
                    .font(.caption)
                    .foregroundStyle(.secondary)

                SwatchColorPicker(label: "Color", color: $viewModel.backgroundColor, supportsOpacity: true)

                if viewModel.backgroundStyle == .gradient {
                    SwatchColorPicker(label: "Color 2", color: $viewModel.backgroundSecondaryColor, supportsOpacity: true)
                }

                HStack(spacing: 8) {
                    Button("Solid") {
                        viewModel.applyCustomSolidBackground()
                    }
                    Button("Gradient") {
                        viewModel.applyCustomGradientBackground()
                    }
                }
            }

            VStack(alignment: .leading, spacing: 8) {
                Text("Wallpaper")
                    .font(.caption)
                    .foregroundStyle(.secondary)

                HStack(spacing: 8) {
                    Button {
                        viewModel.chooseWallpaperBackground()
                    } label: {
                        WallpaperPresetSwatch(image: viewModel.wallpaperImage, isSelected: viewModel.backgroundStyle == .wallpaper)
                    }
                    .buttonStyle(.plain)
                    .help("Choose wallpaper")
                    .accessibilityLabel("Wallpaper background")
                    .accessibilityValue(viewModel.backgroundStyle == .wallpaper ? "Selected" : "Not selected")

                    if viewModel.backgroundStyle == .wallpaper {
                        Button("Remove") {
                            viewModel.clearWallpaperBackground()
                        }
                        .buttonStyle(.link)
                    }
                }
            }

            VStack(alignment: .leading, spacing: 4) {
                Text("Padding: \(Int(viewModel.canvasPadding)) px")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Slider(value: $viewModel.canvasPadding, in: 0...160, step: 2)
            }

            Picker("Frame", selection: $viewModel.exportFramePreset) {
                ForEach(ExportFramePreset.allCases) { preset in
                    Text(preset.label).tag(preset)
                }
            }
            .accessibilityHint("Sets the export frame without stretching the screenshot.")

            ExportAlignmentGrid(
                horizontalAlignment: $viewModel.horizontalExportAlignment,
                verticalAlignment: $viewModel.verticalExportAlignment
            )

            VStack(alignment: .leading, spacing: 4) {
                Text("Image corners: \(Int(viewModel.canvasCornerRadius)) px")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Slider(value: $viewModel.canvasCornerRadius, in: 0...60, step: 1)
            }

            VStack(alignment: .leading, spacing: 4) {
                Text("Shadow: \(Int(viewModel.canvasShadowRadius))")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Slider(value: $viewModel.canvasShadowRadius, in: 0...40, step: 1)
            }
        }
    }

    private func backgroundPresetSection(_ title: String, presets: [ExportBackgroundPreset]) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title)
                .font(.caption)
                .foregroundStyle(.secondary)

            LazyVGrid(columns: Array(repeating: GridItem(.fixed(19), spacing: 5), count: 6), spacing: 5) {
                ForEach(presets) { preset in
                    Button {
                        viewModel.applyBackgroundPreset(preset)
                    } label: {
                        BackgroundPresetSwatch(preset: preset, isSelected: viewModel.selectedBackgroundPresetID == preset.id)
                    }
                    .buttonStyle(.plain)
                    .help(preset.label)
                    .accessibilityLabel("\(preset.label) background")
                    .accessibilityValue(viewModel.selectedBackgroundPresetID == preset.id ? "Selected" : "Not selected")
                }
            }
        }
    }

    private var exportControls: some View {
        HStack(spacing: 12) {
            if let img = viewModel.originalImage {
                let rep = img.representations.first
                Text("\(Int(rep?.pixelsWide ?? 0)) × \(Int(rep?.pixelsHigh ?? 0))")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Divider()
                .frame(height: 18)

            Button(action: zoomOut) {
                Image(systemName: "minus.magnifyingglass")
            }
            .buttonStyle(.borderless)
            .disabled(zoomScale <= ScreenshotEditorZoomMath.minimumScale)
            .help("Zoom Out (Command-minus)")
            .accessibilityLabel("Zoom out")

            Menu {
                ForEach(ScreenshotEditorZoomMath.presets, id: \.self) { preset in
                    Button {
                        setZoom(preset)
                    } label: {
                        if abs(zoomScale - preset) < 0.001 {
                            Label("\(Int(preset * 100))%", systemImage: "checkmark")
                        } else {
                            Text("\(Int(preset * 100))%")
                        }
                    }
                }
            } label: {
                Text("\(Int((zoomScale * 100).rounded()))%")
                    .monospacedDigit()
                    .frame(minWidth: 42)
            }
            .menuStyle(.borderlessButton)
            .accessibilityLabel("Canvas zoom")
            .accessibilityValue("\(Int((zoomScale * 100).rounded())) percent")

            Button(action: zoomIn) {
                Image(systemName: "plus.magnifyingglass")
            }
            .buttonStyle(.borderless)
            .disabled(zoomScale >= ScreenshotEditorZoomMath.maximumScale)
            .help("Zoom In (Command-plus)")
            .accessibilityLabel("Zoom in")

            Button(action: fitZoom) {
                Label("Fit", systemImage: "arrow.down.right.and.arrow.up.left")
            }
            .controlSize(.small)
            .help("Fit to Window (Command-0)")
            .accessibilityLabel("Fit canvas to window")

            Spacer()

            Button {
                openSaveFolder()
            } label: {
                Label("Open Folder", systemImage: "folder")
            }
            .help("Open the folder for the current save location.")
        }
    }

    private var saveOptionsPopover: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Export")
                .font(.headline)

            Picker("Format", selection: $viewModel.saveFormat) {
                ForEach(ImageFormat.allCases, id: \.self) { format in
                    Text(format.label).tag(format)
                }
            }

            Picker("Scale", selection: $viewModel.saveScale) {
                Text("100%").tag(100)
                Text("90%").tag(90)
                Text("80%").tag(80)
                Text("70%").tag(70)
                Text("60%").tag(60)
                Text("50%").tag(50)
                Text("40%").tag(40)
                Text("30%").tag(30)
                Text("25%").tag(25)
            }

            if let outputResolution = viewModel.outputResolutionText {
                Text("Output: \(outputResolution)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            }

            if viewModel.saveFormat == .jpeg {
                VStack(alignment: .leading, spacing: 4) {
                    Text("JPEG quality: \(Int(viewModel.saveJpegQuality * 100))%")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Slider(value: $viewModel.saveJpegQuality, in: 0.1...1.0, step: 0.05)
                }
            }

            HStack {
                Spacer()
                Button("Cancel") { activePopover = nil }
                Button("Continue…") {
                    activePopover = nil
                    DispatchQueue.main.async {
                        saveAsImage()
                    }
                }
                .keyboardShortcut(.defaultAction)
            }
        }
        .frame(width: 260)
        .padding(14)
    }

    private func zoomIn() {
        guard !viewModel.isEditingText else { return }
        setZoom(ScreenshotEditorZoomMath.steppedScale(from: zoomScale, direction: 1))
    }

    private func zoomOut() {
        guard !viewModel.isEditingText else { return }
        setZoom(ScreenshotEditorZoomMath.steppedScale(from: zoomScale, direction: -1))
    }

    private func fitZoom() {
        zoomScale = 1
        panOffset = .zero
    }

    private func setZoom(_ requestedScale: CGFloat, focalPoint: CGPoint? = nil) {
        let newScale = ScreenshotEditorZoomMath.clamp(requestedScale)
        guard abs(newScale - zoomScale) > 0.0001 else { return }
        let focalPoint = focalPoint ?? CGPoint(x: viewportSize.width / 2, y: viewportSize.height / 2)
        let adjustedPan = ScreenshotEditorZoomMath.focalAdjustedPan(
            panOffset,
            oldScale: zoomScale,
            newScale: newScale,
            focalPoint: focalPoint,
            viewportSize: viewportSize
        )
        zoomScale = newScale
        panOffset = ScreenshotEditorZoomMath.clampedPan(
            adjustedPan,
            contentSize: zoomContentSize(in: viewportSize, scale: newScale),
            viewportSize: viewportSize
        )
    }

    private func panCanvas(by delta: CGSize) {
        let proposed = CGSize(
            width: panOffset.width + delta.width,
            height: panOffset.height + delta.height
        )
        panOffset = ScreenshotEditorZoomMath.clampedPan(
            proposed,
            contentSize: zoomContentSize(in: viewportSize),
            viewportSize: viewportSize
        )
    }

    private func updateViewportSize(_ size: CGSize) {
        viewportSize = size
        constrainPan()
    }

    private func constrainPan() {
        panOffset = ScreenshotEditorZoomMath.clampedPan(
            panOffset,
            contentSize: zoomContentSize(in: viewportSize),
            viewportSize: viewportSize
        )
    }

    private func zoomContentSize(in size: CGSize, scale: CGFloat? = nil) -> CGSize {
        viewModel.displayLayout(in: size, zoomScale: scale ?? zoomScale).frameSize
    }

    private func saveCurrentImage() {
        guard !isSaving else { return }
        isSaving = true

        DispatchQueue.main.async {
            if let url = viewModel.save(to: currentSaveURL) {
                currentSaveURL = url
                lastSavedURL = url
                viewModel.markSaved()
                removeTemporarySourceAfterSave(to: url)
            } else {
                SaveService.shared.showError("Could not save the edited image.")
            }
            isSaving = false
        }
    }

    private func beginSaveAs() {
        guard !isSaving else { return }
        activePopover = .saveOptions
    }

    private func saveAsImage() {
        guard !isSaving else { return }

        guard let targetURL = chooseSaveLocation() else { return }

        isSaving = true
        DispatchQueue.main.async {
            if let url = viewModel.save(to: targetURL) {
                currentSaveURL = url
                lastSavedURL = url
                viewModel.markSaved()
                removeTemporarySourceAfterSave(to: url)
                activePopover = nil
            } else {
                SaveService.shared.showError("Could not save the edited image.")
            }
            isSaving = false
        }
    }

    private func chooseSaveLocation() -> URL? {
        let panel = NSSavePanel()
        panel.title = "Save edited screenshot"
        panel.message = "Choose where to save the edited screenshot."
        panel.nameFieldLabel = "Name"
        panel.canCreateDirectories = true
        panel.allowedContentTypes = [viewModel.saveFormat.utType]
        panel.nameFieldStringValue = suggestedSaveName
        panel.directoryURL = SaveService.shared.outputDirectoryURL(for: .screenshot)

        guard panel.runModal() == .OK, let selectedURL = panel.url else {
            return nil
        }

        if selectedURL.pathExtension.isEmpty {
            return selectedURL.appendingPathExtension(viewModel.saveFormat.rawValue)
        }

        return selectedURL
    }

    private var suggestedSaveName: String {
        let baseName = currentSaveURL.deletingPathExtension().lastPathComponent
        let stem = baseName.isEmpty ? imageURL.deletingPathExtension().lastPathComponent : baseName
        return "\(stem).\(viewModel.saveFormat.rawValue)"
    }

    private func openSaveFolder() {
        let directoryURL = SaveService.shared.outputDirectoryURL(for: .screenshot)
        NSWorkspace.shared.open(directoryURL)
    }

    private func removeTemporarySourceAfterSave(to savedURL: URL) {
        guard deleteSourceAfterSave,
              imageURL.standardizedFileURL != savedURL.standardizedFileURL else {
            return
        }
        try? FileManager.default.removeItem(at: imageURL)
    }

    private func requestClose() {
        if viewModel.hasUnsavedChanges {
            showExitConfirmation = true
        } else {
            onDone(lastSavedURL)
        }
    }

    private func handleEscape() {
        if viewModel.textEditPosition != nil {
            viewModel.cancelTextAnnotation()
            return
        }

        requestClose()
    }
}
