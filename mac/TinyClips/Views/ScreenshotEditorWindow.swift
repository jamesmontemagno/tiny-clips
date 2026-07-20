import AppKit
import SwiftUI
import UniformTypeIdentifiers

private let textSystemFontFamily = "System"

private struct ScreenshotEditorCommandActions {
    let save: () -> Void
    let saveAs: () -> Void
    let revealInFinder: () -> Void
    let undo: () -> Void
    let redo: () -> Void
    let copy: () -> Void
    let clearAnnotations: () -> Void
    let canUndo: Bool
    let canRedo: Bool
    let hasAnnotations: Bool
    let isEditingText: Bool
}

private struct ScreenshotEditorCommandActionsKey: FocusedValueKey {
    typealias Value = ScreenshotEditorCommandActions
}

private extension FocusedValues {
    var screenshotEditorCommandActions: ScreenshotEditorCommandActions? {
        get { self[ScreenshotEditorCommandActionsKey.self] }
        set { self[ScreenshotEditorCommandActionsKey.self] = newValue }
    }
}

private struct ScreenshotEditorMenuCommands: Commands {
    @FocusedValue(\.screenshotEditorCommandActions) private var editor

    var body: some Commands {
        CommandGroup(replacing: .saveItem) {
            Button("Save") {
                editor?.save()
            }
            .disabled(editor == nil)
            .keyboardShortcut("s", modifiers: .command)

            Button("Save As…") {
                editor?.saveAs()
            }
            .disabled(editor == nil)
            .keyboardShortcut("s", modifiers: [.command, .shift])

            Divider()

            Button("Reveal in Finder") {
                editor?.revealInFinder()
            }
            .disabled(editor == nil)
            .keyboardShortcut("r", modifiers: [.command, .shift])
        }

        CommandGroup(replacing: .undoRedo) {
            Button("Undo") {
                if editor?.isEditingText == true {
                    performTextAction("undo:")
                } else {
                    editor?.undo()
                }
            }
            .disabled(editor == nil || (editor?.isEditingText != true && editor?.canUndo != true))
            .keyboardShortcut("z", modifiers: .command)

            Button("Redo") {
                if editor?.isEditingText == true {
                    performTextAction("redo:")
                } else {
                    editor?.redo()
                }
            }
            .disabled(editor == nil || (editor?.isEditingText != true && editor?.canRedo != true))
            .keyboardShortcut("z", modifiers: [.command, .shift])
        }

        CommandGroup(replacing: .pasteboard) {
            Button("Cut") {
                performTextAction("cut:")
            }
            .disabled(editor?.isEditingText != true)
            .keyboardShortcut("x", modifiers: .command)

            Button("Copy") {
                if editor?.isEditingText == true {
                    performTextAction("copy:")
                } else {
                    editor?.copy()
                }
            }
            .disabled(editor == nil)
            .keyboardShortcut("c", modifiers: .command)

            Button("Paste") {
                performTextAction("paste:")
            }
            .disabled(editor?.isEditingText != true)
            .keyboardShortcut("v", modifiers: .command)

            Divider()

            Button("Clear Annotations…") {
                editor?.clearAnnotations()
            }
            .disabled(editor?.hasAnnotations != true)
        }
    }

    private func performTextAction(_ selectorName: String) {
        NSApp.sendAction(NSSelectorFromString(selectorName), to: nil, from: nil)
    }
}

// MARK: - Scene

struct ScreenshotEditorScene: Scene {
    var body: some Scene {
        WindowGroup("Edit Screenshot", id: ScreenshotEditorRegistry.windowID, for: UUID.self) { $sessionID in
            ScreenshotEditorSceneRoot(sessionID: sessionID)
        }
        .defaultSize(width: 1040, height: 720)
        .commands {
            AppWindowCommands()
            ScreenshotEditorMenuCommands()
        }
    }
}

private struct ScreenshotEditorSceneRoot: View {
    let sessionID: UUID?
    @Environment(\.dismissWindow) private var dismissWindow
    @State private var resolvedSession: ScreenshotEditorRegistry.Session?

    var body: some View {
        Group {
            if let session = resolvedSession, let sessionID {
                ScreenshotEditorView(
                    imageURL: session.imageURL,
                    initialSaveURL: session.initialSaveURL,
                    deleteSourceAfterSave: session.deleteSourceAfterSave
                ) { resultURL in
                    ScreenshotEditorRegistry.shared.finish(sessionID, result: resultURL)
                    dismissWindow(id: ScreenshotEditorRegistry.windowID, value: sessionID)
                }
            } else {
                Color(NSColor.windowBackgroundColor)
                    .frame(minWidth: 700, minHeight: 520)
            }
        }
        .onAppear {
            guard let sessionID else { return }
            if let session = ScreenshotEditorRegistry.shared.session(for: sessionID) {
                resolvedSession = session
            } else {
                dismissWindow(id: ScreenshotEditorRegistry.windowID, value: sessionID)
            }
        }
    }
}

// MARK: - Registry

@MainActor
final class ScreenshotEditorRegistry {
    static let shared = ScreenshotEditorRegistry()
    static let windowID = "screenshot-editor"

    struct Session {
        let imageURL: URL
        let initialSaveURL: URL
        let deleteSourceAfterSave: Bool
        let onComplete: (URL?) -> Void
    }

    private var sessions: [UUID: Session] = [:]
    private var pendingOpens: [UUID] = []
    private var opener: ((UUID) -> Void)?

    func installOpener(_ opener: @escaping (UUID) -> Void) {
        self.opener = opener
        let pending = pendingOpens
        pendingOpens.removeAll()
        for id in pending {
            opener(id)
        }
    }

    func present(
        imageURL: URL,
        initialSaveURL: URL? = nil,
        deleteSourceAfterSave: Bool = false,
        onComplete: @escaping (URL?) -> Void
    ) {
        let id = UUID()
        sessions[id] = Session(
            imageURL: imageURL,
            initialSaveURL: initialSaveURL ?? imageURL,
            deleteSourceAfterSave: deleteSourceAfterSave,
            onComplete: onComplete
        )
        if let opener {
            opener(id)
        } else {
            pendingOpens.append(id)
        }
    }

    func session(for id: UUID) -> Session? {
        sessions[id]
    }

    func finish(_ id: UUID, result: URL?) {
        guard let session = sessions.removeValue(forKey: id) else { return }
        session.onComplete(result)
    }
}

// MARK: - Tool Type

private enum EditTool: String, CaseIterable {
    case move = "arrow.up.and.down.and.arrow.left.and.right"
    case crop = "crop"
    case rectangle = "rectangle"
    case circle = "circle"
    case arrow = "arrowshape.right"
    case line = "line.diagonal"
    case pencil = "pencil.tip"
    case text = "textformat"
    case number = "number.circle.fill"
    case blur = "eye.slash"

    var label: String {
        switch self {
        case .move: return "Move"
        case .crop: return "Crop"
        case .rectangle: return "Rectangle"
        case .circle: return "Circle"
        case .arrow: return "Arrow"
        case .line: return "Line"
        case .pencil: return "Draw"
        case .text: return "Text"
        case .number: return "Number"
        case .blur: return "Redact"
        }
    }
}

// MARK: - Annotation

private struct Annotation: Identifiable {
    let id = UUID()
    let tool: EditTool
    var rect: CGRect
    var color: Color
    var textColor: Color = .white
    var fillColor: Color = .clear
    var lineWidth: CGFloat
    var text: String
    var points: [CGPoint] // for pencil
    var fontSize: CGFloat = 16 // for text annotations
    var fontFamily: String = textSystemFontFamily
    var isBold: Bool = false
    var isItalic: Bool = false
    var isUnderlined: Bool = false
    var redactionBlurPreset: RedactionBlurPreset = .medium
    var arrowStyle: ArrowStyle = .straight
}

private typealias LinePoints = (start: CGPoint, end: CGPoint)

private enum RedactionBlurPreset: String, CaseIterable, Identifiable {
    case light
    case medium
    case heavy

    var id: Self { self }

    var label: String {
        switch self {
        case .light: return "Light"
        case .medium: return "Medium"
        case .heavy: return "Heavy"
        }
    }

    var previewBlockSize: CGFloat {
        switch self {
        case .light: return 8
        case .medium: return 10
        case .heavy: return 14
        }
    }

    var exportBlockSize: CGFloat {
        switch self {
        case .light: return 10
        case .medium: return 12
        case .heavy: return 16
        }
    }

    var baseBrightness: Double {
        switch self {
        case .light: return 0.34
        case .medium: return 0.29
        case .heavy: return 0.24
        }
    }

    var contrastStep: Double {
        switch self {
        case .light: return 0.08
        case .medium: return 0.12
        case .heavy: return 0.16
        }
    }

    var cycleLength: Int {
        switch self {
        case .light: return 2
        case .medium: return 3
        case .heavy: return 4
        }
    }
}

private enum ArrowStyle: String, CaseIterable, Identifiable {
    case straight
    case curvedLeft
    case curvedRight

    var id: Self { self }

    var label: String {
        switch self {
        case .straight: return "Straight"
        case .curvedLeft: return "Curved Left"
        case .curvedRight: return "Curved Right"
        }
    }

    var curvatureSign: CGFloat {
        switch self {
        case .straight: return 0
        case .curvedLeft: return -1
        case .curvedRight: return 1
        }
    }
}

private enum ExportBackgroundStyle: String, CaseIterable, Identifiable {
    case transparent
    case solid
    case gradient
    case wallpaper

    var id: Self { self }

    var label: String {
        switch self {
        case .transparent: return "Transparent"
        case .solid: return "Solid"
        case .gradient: return "Gradient"
        case .wallpaper: return "Wallpaper"
        }
    }
}

private struct ExportBackgroundPreset: Identifiable {
    let id: String
    let label: String
    let style: ExportBackgroundStyle
    let primary: Color
    let secondary: Color?
}

private let solidBackgroundPresets: [ExportBackgroundPreset] = [
    ExportBackgroundPreset(id: "transparent", label: "Transparent", style: .transparent, primary: .clear, secondary: nil),
    ExportBackgroundPreset(id: "white", label: "White", style: .solid, primary: .white, secondary: nil),
    ExportBackgroundPreset(id: "ink", label: "Ink", style: .solid, primary: Color(red: 0.08, green: 0.09, blue: 0.10), secondary: nil),
    ExportBackgroundPreset(id: "coral", label: "Coral", style: .solid, primary: Color(red: 1.00, green: 0.48, blue: 0.42), secondary: nil),
    ExportBackgroundPreset(id: "lemon", label: "Lemon", style: .solid, primary: Color(red: 1.00, green: 0.88, blue: 0.25), secondary: nil),
    ExportBackgroundPreset(id: "mint", label: "Mint", style: .solid, primary: Color(red: 0.41, green: 0.86, blue: 0.62), secondary: nil),
    ExportBackgroundPreset(id: "sky", label: "Sky", style: .solid, primary: Color(red: 0.34, green: 0.67, blue: 0.96), secondary: nil),
    ExportBackgroundPreset(id: "lilac", label: "Lilac", style: .solid, primary: Color(red: 0.70, green: 0.58, blue: 0.94), secondary: nil),
    ExportBackgroundPreset(id: "bubblegum", label: "Bubblegum", style: .solid, primary: Color(red: 1.00, green: 0.42, blue: 0.76), secondary: nil),
    ExportBackgroundPreset(id: "tangerine", label: "Tangerine", style: .solid, primary: Color(red: 1.00, green: 0.56, blue: 0.16), secondary: nil),
    ExportBackgroundPreset(id: "lagoon", label: "Lagoon", style: .solid, primary: Color(red: 0.00, green: 0.72, blue: 0.78), secondary: nil),
    ExportBackgroundPreset(id: "plum", label: "Plum", style: .solid, primary: Color(red: 0.39, green: 0.18, blue: 0.58), secondary: nil),
]

private let gradientBackgroundPresets: [ExportBackgroundPreset] = [
    ExportBackgroundPreset(id: "sunset", label: "Sunset", style: .gradient, primary: Color(red: 1.00, green: 0.48, blue: 0.37), secondary: Color(red: 1.00, green: 0.86, blue: 0.31)),
    ExportBackgroundPreset(id: "ocean", label: "Ocean", style: .gradient, primary: Color(red: 0.15, green: 0.53, blue: 0.91), secondary: Color(red: 0.18, green: 0.88, blue: 0.75)),
    ExportBackgroundPreset(id: "candy", label: "Candy", style: .gradient, primary: Color(red: 1.00, green: 0.42, blue: 0.68), secondary: Color(red: 0.55, green: 0.78, blue: 1.00)),
    ExportBackgroundPreset(id: "forest", label: "Forest", style: .gradient, primary: Color(red: 0.16, green: 0.56, blue: 0.35), secondary: Color(red: 0.72, green: 0.88, blue: 0.42)),
    ExportBackgroundPreset(id: "ember", label: "Ember", style: .gradient, primary: Color(red: 0.22, green: 0.08, blue: 0.05), secondary: Color(red: 1.00, green: 0.45, blue: 0.16)),
    ExportBackgroundPreset(id: "aurora", label: "Aurora", style: .gradient, primary: Color(red: 0.28, green: 0.94, blue: 0.72), secondary: Color(red: 0.52, green: 0.42, blue: 1.00)),
    ExportBackgroundPreset(id: "peach", label: "Peach", style: .gradient, primary: Color(red: 1.00, green: 0.72, blue: 0.52), secondary: Color(red: 0.98, green: 0.42, blue: 0.54)),
    ExportBackgroundPreset(id: "glacier", label: "Glacier", style: .gradient, primary: Color(red: 0.73, green: 0.94, blue: 1.00), secondary: Color(red: 0.42, green: 0.58, blue: 0.96)),
    ExportBackgroundPreset(id: "neon", label: "Neon", style: .gradient, primary: Color(red: 0.05, green: 1.00, blue: 0.54), secondary: Color(red: 1.00, green: 0.08, blue: 0.70)),
    ExportBackgroundPreset(id: "mango", label: "Mango", style: .gradient, primary: Color(red: 1.00, green: 0.78, blue: 0.20), secondary: Color(red: 1.00, green: 0.26, blue: 0.18)),
    ExportBackgroundPreset(id: "midnight", label: "Midnight", style: .gradient, primary: Color(red: 0.05, green: 0.07, blue: 0.18), secondary: Color(red: 0.00, green: 0.58, blue: 0.82)),
    ExportBackgroundPreset(id: "prism", label: "Prism", style: .gradient, primary: Color(red: 0.98, green: 0.16, blue: 0.38), secondary: Color(red: 0.18, green: 0.86, blue: 0.93)),
]

private enum EditorPopover: String, Identifiable {
    case saveOptions

    var id: Self { self }
}

private struct AnnotationColorPreset: Identifiable {
    let id: String
    let name: String
    let color: Color
}

/// Common preset colors offered before the native "Custom…" picker across the
/// editor's annotation color surfaces (stroke, fill, text, badge, background).
private let annotationColorPresets: [AnnotationColorPreset] = [
    AnnotationColorPreset(id: "black", name: "Black", color: .black),
    AnnotationColorPreset(id: "white", name: "White", color: .white),
    AnnotationColorPreset(id: "gray", name: "Gray", color: Color(red: 0.55, green: 0.55, blue: 0.57)),
    AnnotationColorPreset(id: "red", name: "Red", color: Color(red: 0.90, green: 0.20, blue: 0.18)),
    AnnotationColorPreset(id: "orange", name: "Orange", color: Color(red: 1.00, green: 0.58, blue: 0.16)),
    AnnotationColorPreset(id: "yellow", name: "Yellow", color: Color(red: 1.00, green: 0.84, blue: 0.20)),
    AnnotationColorPreset(id: "green", name: "Green", color: Color(red: 0.24, green: 0.70, blue: 0.34)),
    AnnotationColorPreset(id: "teal", name: "Teal", color: Color(red: 0.00, green: 0.72, blue: 0.72)),
    AnnotationColorPreset(id: "blue", name: "Blue", color: Color(red: 0.20, green: 0.52, blue: 0.96)),
    AnnotationColorPreset(id: "purple", name: "Purple", color: Color(red: 0.55, green: 0.35, blue: 0.90)),
    AnnotationColorPreset(id: "pink", name: "Pink", color: Color(red: 1.00, green: 0.36, blue: 0.66)),
    AnnotationColorPreset(id: "brown", name: "Brown", color: Color(red: 0.60, green: 0.40, blue: 0.24)),
]

private func annotationColorsEqual(_ lhs: Color, _ rhs: Color) -> Bool {
    guard let left = NSColor(lhs).usingColorSpace(.deviceRGB),
          let right = NSColor(rhs).usingColorSpace(.deviceRGB) else {
        return false
    }

    var lr: CGFloat = 0, lg: CGFloat = 0, lb: CGFloat = 0, la: CGFloat = 0
    var rr: CGFloat = 0, rg: CGFloat = 0, rb: CGFloat = 0, ra: CGFloat = 0
    left.getRed(&lr, green: &lg, blue: &lb, alpha: &la)
    right.getRed(&rr, green: &rg, blue: &rb, alpha: &ra)

    return abs(lr - rr) < 0.0001 &&
        abs(lg - rg) < 0.0001 &&
        abs(lb - rb) < 0.0001 &&
        abs(la - ra) < 0.0001
}

private func redactionBrightness(for row: Int, column: Int, preset: RedactionBlurPreset) -> Double {
    let phase = Double((row + column) % preset.cycleLength)
    return min(0.82, preset.baseBrightness + phase * preset.contrastStep)
}

private func drawCheckerboardRedaction(in context: GraphicsContext, rect: CGRect, preset: RedactionBlurPreset) {
    let blockSize = preset.previewBlockSize
    let cols = max(1, Int(ceil(rect.width / blockSize)))
    let rows = max(1, Int(ceil(rect.height / blockSize)))

    for row in 0..<rows {
        for col in 0..<cols {
            let blockRect = CGRect(
                x: rect.minX + CGFloat(col) * blockSize,
                y: rect.minY + CGFloat(row) * blockSize,
                width: min(blockSize, rect.maxX - (rect.minX + CGFloat(col) * blockSize)),
                height: min(blockSize, rect.maxY - (rect.minY + CGFloat(row) * blockSize))
            )
            guard blockRect.width > 0, blockRect.height > 0 else { continue }
            let brightness = redactionBrightness(for: row, column: col, preset: preset)
            context.fill(Path(blockRect), with: .color(Color(white: brightness, opacity: 1.0)))
        }
    }
}

private func drawCheckerboardRedaction(in context: CGContext, rect: CGRect, preset: RedactionBlurPreset) {
    let blockSize = preset.exportBlockSize
    let cols = max(1, Int(ceil(rect.width / blockSize)))
    let rows = max(1, Int(ceil(rect.height / blockSize)))

    for row in 0..<rows {
        for col in 0..<cols {
            let blockRect = CGRect(
                x: rect.minX + CGFloat(col) * blockSize,
                y: rect.minY + CGFloat(row) * blockSize,
                width: min(blockSize, rect.maxX - (rect.minX + CGFloat(col) * blockSize)),
                height: min(blockSize, rect.maxY - (rect.minY + CGFloat(row) * blockSize))
            )
            guard blockRect.width > 0, blockRect.height > 0 else { continue }
            let brightness = redactionBrightness(for: row, column: col, preset: preset)
            context.setFillColor(CGColor(gray: brightness, alpha: 1.0))
            context.fill(blockRect)
        }
    }
}

private func arrowControlPoint(start: CGPoint, end: CGPoint, style: ArrowStyle) -> CGPoint {
    let mid = CGPoint(x: (start.x + end.x) * 0.5, y: (start.y + end.y) * 0.5)
    guard style != .straight else { return mid }
    let dx = end.x - start.x
    let dy = end.y - start.y
    let length = max(1, hypot(dx, dy))
    let normal = CGPoint(x: -dy / length, y: dx / length)
    let magnitude = max(20, length * 0.25) * style.curvatureSign
    return CGPoint(x: mid.x + normal.x * magnitude, y: mid.y + normal.y * magnitude)
}

// MARK: - Editor View

private struct ScreenshotEditorView: View {
    let imageURL: URL
    let initialSaveURL: URL
    let deleteSourceAfterSave: Bool
    let onDone: (URL?) -> Void

    @StateObject private var viewModel: EditorViewModel
    @State private var isSaving = false
    @State private var splitVisibility: NavigationSplitViewVisibility = .automatic
    @State private var activePopover: EditorPopover?
    @State private var isBackgroundSectionExpanded = true
    @State private var showExitConfirmation = false
    @State private var showClearAnnotationsConfirmation = false
    @State private var currentSaveURL: URL
    @State private var lastSavedURL: URL?

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
        _viewModel = StateObject(wrappedValue: EditorViewModel(url: imageURL))
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
                    CanvasView(viewModel: viewModel, containerSize: geo.size)
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
                canUndo: viewModel.canUndo,
                canRedo: viewModel.canRedo,
                hasAnnotations: viewModel.hasAnnotations,
                isEditingText: viewModel.isEditingText
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
        .disabled(isSaving)
        .overlay {
            if isSaving {
                ProgressOverlayView(title: "Saving…")
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
private struct TextStyleToggleButton: View {
    let title: String
    let systemImage: String
    @Binding var isOn: Bool

    var body: some View {
        Button {
            isOn.toggle()
        } label: {
            Image(systemName: systemImage)
                .font(.system(size: 13, weight: .semibold))
                .frame(width: 30, height: 28)
                .background(isOn ? Color.accentColor.opacity(0.2) : Color.clear)
                .clipShape(RoundedRectangle(cornerRadius: 6))
                .contentShape(RoundedRectangle(cornerRadius: 6))
        }
        .buttonStyle(.plain)
        .accessibilityLabel(title)
        .accessibilityValue(isOn ? "On" : "Off")
        .help(title)
    }
}

private struct ArrowStyleButton: View {
    let style: ArrowStyle
    let isSelected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: iconName)
                .font(.system(size: 15, weight: .semibold))
                .frame(width: 34, height: 30)
                .background(isSelected ? Color.accentColor.opacity(0.2) : Color.clear)
                .clipShape(RoundedRectangle(cornerRadius: 7))
                .contentShape(RoundedRectangle(cornerRadius: 7))
        }
        .buttonStyle(.plain)
        .accessibilityLabel(style.label)
        .accessibilityValue(isSelected ? "Selected" : "Not selected")
        .help(style.label)
    }

    private var iconName: String {
        switch style {
        case .straight: return "arrowshape.right"
        case .curvedLeft: return "arrowshape.turn.up.right"
        case .curvedRight: return "arrowshape.turn.up.left"
        }
    }
}

private struct BackgroundPresetSwatch: View {
    let preset: ExportBackgroundPreset
    let isSelected: Bool

    var body: some View {
        ZStack {
            Circle()
                .fill(fillStyle)
                .overlay {
                    if preset.style == .transparent {
                        Image(systemName: "slash")
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundStyle(.secondary)
                    }
                }
                .overlay(Circle().stroke(.separator, lineWidth: 1))
                .frame(width: 15, height: 15)

            if isSelected {
                Circle()
                    .stroke(Color.accentColor, lineWidth: 2)
                    .frame(width: 19, height: 19)
            }
        }
        .frame(width: 19, height: 19)
    }

    private var fillStyle: AnyShapeStyle {
        switch preset.style {
        case .transparent:
            return AnyShapeStyle(.regularMaterial)
        case .solid:
            return AnyShapeStyle(preset.primary)
        case .gradient:
            return AnyShapeStyle(
                LinearGradient(
                    colors: [preset.primary, preset.secondary ?? preset.primary],
                    startPoint: .topLeading,
                    endPoint: .bottomTrailing
                )
            )
        case .wallpaper:
            return AnyShapeStyle(.regularMaterial)
        }
    }
}

private struct WallpaperPresetSwatch: View {
    let image: NSImage?
    let isSelected: Bool

    var body: some View {
        ZStack {
            Circle()
                .fill(.regularMaterial)
                .overlay {
                    if let image {
                        Image(nsImage: image)
                            .resizable()
                            .scaledToFill()
                    } else {
                        Image(systemName: "photo")
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundStyle(.secondary)
                    }
                }
                .clipShape(Circle())
                .overlay(Circle().stroke(.separator, lineWidth: 1))
                .frame(width: 20, height: 20)

            if isSelected {
                Circle()
                    .stroke(Color.accentColor, lineWidth: 3)
                    .frame(width: 26, height: 26)
            }
        }
        .frame(width: 28, height: 28)
    }
}

private func isEffectivelyClear(_ color: Color) -> Bool {
    guard let resolved = NSColor(color).usingColorSpace(.deviceRGB) else {
        return false
    }
    return resolved.alphaComponent < 0.01
}

private struct AnnotationColorSwatch: View {
    let color: Color
    let isSelected: Bool
    var isTransparent: Bool = false

    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 5)
                .fill(isTransparent ? AnyShapeStyle(.regularMaterial) : AnyShapeStyle(color))
                .overlay {
                    if isTransparent {
                        Image(systemName: "slash.circle")
                            .font(.system(size: 12, weight: .semibold))
                            .foregroundStyle(.secondary)
                    }
                }
                .overlay(RoundedRectangle(cornerRadius: 5).stroke(.separator, lineWidth: 1))
                .frame(width: 18, height: 18)

            if isSelected {
                RoundedRectangle(cornerRadius: 7)
                    .stroke(Color.accentColor, lineWidth: 2)
                    .frame(width: 22, height: 22)
            }
        }
        .frame(width: 22, height: 22)
    }
}

/// Compact preview of the current color shown on the closed dropdown control.
private struct ColorPreviewSwatch: View {
    let color: Color
    var isTransparent: Bool = false

    var body: some View {
        RoundedRectangle(cornerRadius: 4)
            .fill(isTransparent ? AnyShapeStyle(.regularMaterial) : AnyShapeStyle(color))
            .overlay {
                if isTransparent {
                    Image(systemName: "slash")
                        .font(.system(size: 10, weight: .semibold))
                        .foregroundStyle(.secondary)
                }
            }
            .overlay(RoundedRectangle(cornerRadius: 4).stroke(.separator, lineWidth: 1))
            .frame(width: 22, height: 16)
    }
}

/// Reusable color control: a dropdown that previews the current color and opens
/// a popover of common preset swatches plus a native "Custom…" color well for
/// full precision/opacity. When `allowsTransparent` is set it also offers a
/// "None" (transparent) option. Used across the editor's annotation and
/// background color surfaces.
private struct SwatchColorPicker: View {
    let label: String
    @Binding var color: Color
    var supportsOpacity: Bool = true
    var allowsTransparent: Bool = false

    @State private var isPresented = false

    private let columns = Array(repeating: GridItem(.fixed(22), spacing: 6), count: 6)

    private var isClear: Bool {
        allowsTransparent && isEffectivelyClear(color)
    }

    private var currentColorName: String {
        if isClear {
            return "None"
        }
        if let match = annotationColorPresets.first(where: { annotationColorsEqual($0.color, color) }) {
            return match.name
        }
        return "Custom"
    }

    var body: some View {
        HStack {
            Text(label)
                .font(.caption)
                .foregroundStyle(.secondary)

            Spacer()

            Button {
                isPresented.toggle()
            } label: {
                HStack(spacing: 6) {
                    ColorPreviewSwatch(color: color, isTransparent: isClear)
                    Text(currentColorName)
                        .font(.caption)
                        .lineLimit(1)
                    Image(systemName: "chevron.down")
                        .font(.system(size: 9, weight: .semibold))
                        .foregroundStyle(.secondary)
                }
            }
            .buttonStyle(.bordered)
            .controlSize(.small)
            .accessibilityLabel("\(label) color")
            .accessibilityValue(currentColorName)
            .accessibilityHint("Opens color options")
            .popover(isPresented: $isPresented, arrowEdge: .bottom) {
                popoverContent
            }
        }
    }

    private var popoverContent: some View {
        VStack(alignment: .leading, spacing: 10) {
            LazyVGrid(columns: columns, alignment: .leading, spacing: 6) {
                if allowsTransparent {
                    Button {
                        color = .clear
                        isPresented = false
                    } label: {
                        AnnotationColorSwatch(color: .clear, isSelected: isClear, isTransparent: true)
                    }
                    .buttonStyle(.plain)
                    .help("None (transparent)")
                    .accessibilityLabel("No \(label.lowercased())")
                    .accessibilityValue(isClear ? "Selected" : "Not selected")
                }

                ForEach(annotationColorPresets) { preset in
                    let isSelected = !isClear && annotationColorsEqual(preset.color, color)
                    Button {
                        color = preset.color
                        isPresented = false
                    } label: {
                        AnnotationColorSwatch(color: preset.color, isSelected: isSelected)
                    }
                    .buttonStyle(.plain)
                    .help(preset.name)
                    .accessibilityLabel("\(preset.name) \(label.lowercased())")
                    .accessibilityValue(isSelected ? "Selected" : "Not selected")
                }
            }

            Divider()

            HStack {
                Text("Custom")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Spacer()
                ColorPicker("", selection: $color, supportsOpacity: supportsOpacity)
                    .labelsHidden()
                    .accessibilityLabel("Custom \(label.lowercased()) color")
                    .accessibilityHint("Opens the full color picker")
            }
        }
        .padding(12)
        .frame(width: 208)
    }
}

private struct ProgressOverlayView: View {
    let title: String

    var body: some View {
        ZStack {
            Color.black.opacity(0.2)
                .ignoresSafeArea()

            VStack(spacing: 10) {
                ProgressView()
                    .controlSize(.regular)
                Text(title)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .padding(.horizontal, 18)
            .padding(.vertical, 14)
            .background(.ultraThinMaterial)
            .clipShape(RoundedRectangle(cornerRadius: 10))
        }
    }
}

// MARK: - Canvas View

private struct CanvasView: View {
    @ObservedObject var viewModel: EditorViewModel
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
                            .font(textPreviewFont(family: annotation.fontFamily, size: annotation.fontSize, isBold: annotation.isBold))
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

    private func drawAnnotation(_ annotation: Annotation, in context: GraphicsContext, scaledRect: CGRect, imageSize: CGSize, origin: CGPoint, sourceImage: NSImage? = nil) {
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
private func textPreviewFont(family: String, size: CGFloat, isBold: Bool) -> Font {
    if family == textSystemFontFamily {
        return .system(size: size, weight: isBold ? .bold : .regular)
    }
    return .custom(family, size: size).weight(isBold ? .bold : .regular)
}

// MARK: - ViewModel

// Number tool rendering constants
private let numberCircleMinPixels: CGFloat = 20
private let numberCircleMaxPixels: CGFloat = 80
private let numberCircleSizeRatio: CGFloat = 0.05
private let numberCircleFontRatio: CGFloat = 0.55
private let numberCircleMinimumDisplayPixels: CGFloat = 16

private struct EditorCanvasState {
    let annotations: [Annotation]
    let cropRect: CGRect?
    let nextNumberLabel: Int
}

@MainActor
private class EditorViewModel: ObservableObject {
    let sourceURL: URL
    @Published var originalImage: NSImage?
    @Published var selectedTool: EditTool = .move
    @Published var selectedColor: Color = .red
    @Published var selectedFillColor: Color = .clear
    @Published var lineWidth: CGFloat = 4
    @Published var annotations: [Annotation] = []
    @Published var currentAnnotation: Annotation?
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

    private var pencilPoints: [CGPoint] = []
    private var imagePixelSize: CGSize = .zero
    private var dragOffset: CGPoint = .zero
    private var dragOriginalRect: CGRect = .zero
    private var dragOriginalPoints: [CGPoint] = []
    private var isDraggingAnnotation = false
    private var isDraggingEndpoint = false // true = dragging arrowhead/line end
    private var isDraggingStartpoint = false // true = dragging arrow tail/line start
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

    func linePoints(for annotation: Annotation) -> LinePoints {
        if annotation.points.count >= 2 {
            return (annotation.points[0], annotation.points[1])
        }
        return (
            start: annotation.rect.origin,
            end: CGPoint(x: annotation.rect.origin.x + annotation.rect.width,
                         y: annotation.rect.origin.y + annotation.rect.height)
        )
    }

    func scaledLinePoints(for annotation: Annotation, imageSize: CGSize, origin: CGPoint) -> LinePoints {
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

    // Calculate display size maintaining aspect ratio, capped at native pixel size
    func displaySize(in containerSize: CGSize) -> CGSize {
        guard let image = originalImage, image.size.width > 0, image.size.height > 0 else {
            return .zero
        }
        let imageAspect = image.size.width / image.size.height
        let effectivePadding = max(0, canvasPadding)
        let availableWidth = max(1, containerSize.width - (effectivePadding * 2))
        let availableHeight = max(1, containerSize.height - (effectivePadding * 2))

        // Cap at native pixel dimensions to prevent upscaling blur on Retina
        let screenScale = NSScreen.main?.backingScaleFactor ?? 2.0
        let maxWidth = min(availableWidth * 0.95, imagePixelSize.width / screenScale)
        let maxHeight = min(availableHeight * 0.95, imagePixelSize.height / screenScale)

        if maxWidth / maxHeight < imageAspect {
            return CGSize(width: maxWidth, height: maxWidth / imageAspect)
        } else {
            return CGSize(width: maxHeight * imageAspect, height: maxHeight)
        }
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

    func pencilBounds(for annotation: Annotation) -> CGRect? {
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
            abs(canvasShadowRadius - initialCanvasShadowRadius) > 0.0001 {
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
            if !isDraggingAnnotation && !isDraggingEndpoint && !isDraggingStartpoint {
                // First drag event — find what we hit
                if let idx = annotationIndex(at: start) {
                    beginDragHistory()
                    selectedAnnotationIndex = idx
                    dragOriginalRect = annotations[idx].rect
                    dragOriginalPoints = annotations[idx].points

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
                if isDraggingEndpoint {
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
            currentAnnotation = Annotation(
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
            currentAnnotation = Annotation(
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
            commitDragHistory()

        case .crop:
            commitDragHistory()

        case .pencil:
            if pencilPoints.count > 1 {
                recordHistory()
                annotations.append(Annotation(
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
                annotations.append(Annotation(
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

        let exportPadding = max(0, Int(canvasPadding))
        return CGSize(
            width: Int(cropPixelRect.width) + (exportPadding * 2),
            height: Int(cropPixelRect.height) + (exportPadding * 2)
        )
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
        annotations.append(Annotation(
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
        annotations.append(Annotation(
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

        let exportPadding = max(0, Int(canvasPadding))
        let outputW = Int(cropPixelRect.width) + (exportPadding * 2)
        let outputH = Int(cropPixelRect.height) + (exportPadding * 2)
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

        let imageRect = CGRect(
            x: exportPadding,
            y: exportPadding,
            width: Int(cropPixelRect.width),
            height: Int(cropPixelRect.height)
        )
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
                contentOffset: CGPoint(x: exportPadding, y: exportPadding),
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

    private func drawAnnotationCG(_ annotation: Annotation, in ctx: CGContext, cropOrigin: CGPoint, outputSize: CGSize, fullSize: CGSize, contentOffset: CGPoint, sourceCGImage: CGImage? = nil) {
        let imageOutputHeight = outputSize.height - (contentOffset.y * 2)
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
            let fontSize = annotation.fontSize * (fullSize.width / 800.0) // scale to pixel density
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

// MARK: - Inline Text Editor

private struct InlineTextEditor: View {
    @Binding var text: String
    @Binding var fontSize: CGFloat
    let fontFamily: String
    let isBold: Bool
    let isItalic: Bool
    let isUnderlined: Bool
    let color: Color
    let onCommit: () -> Void

    @FocusState private var isFocused: Bool

    var body: some View {
        VStack(spacing: 4) {
            TextField("Type text…", text: $text)
                .textFieldStyle(.plain)
                .font(textPreviewFont(family: fontFamily, size: fontSize, isBold: isBold))
                .italic(isItalic)
                .underline(isUnderlined)
                .foregroundColor(color)
                .padding(.horizontal, 8)
                .padding(.vertical, 4)
                .frame(width: 180)
                .focused($isFocused)
                .onAppear { isFocused = true }
                .onSubmit { onCommit() }

            // Font size control
            HStack(spacing: 6) {
                Button {
                    fontSize = max(10, fontSize - 2)
                } label: {
                    Image(systemName: "minus")
                        .font(.system(size: 10, weight: .bold))
                        .frame(width: 20, height: 20)
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Decrease text size")
                .accessibilityHint("Reduces text size by two points.")
                .help("Decrease text size.")

                Text("\(Int(fontSize))pt")
                    .font(.system(size: 11))
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
                    .frame(width: 32)

                Button {
                    fontSize = min(72, fontSize + 2)
                } label: {
                    Image(systemName: "plus")
                        .font(.system(size: 10, weight: .bold))
                        .frame(width: 20, height: 20)
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Increase text size")
                .accessibilityHint("Increases text size by two points.")
                .help("Increase text size.")
            }
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 6)
        .background {
            RoundedRectangle(cornerRadius: 4)
                .fill(.background)
                .shadow(color: .black.opacity(0.3), radius: 3, y: 1)
                .overlay {
                    RoundedRectangle(cornerRadius: 4)
                        .strokeBorder(color.opacity(0.5), lineWidth: 1.5)
                }
        }
    }
}
