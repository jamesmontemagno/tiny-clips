import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct EditorThemesSettingsSection: View {
    @ObservedObject private var store = EditorThemeStore.shared
    @State private var selectedThemeID: String?
    @State private var nameDraft = ""
    @State private var errorMessage: String?
    @State private var themePendingDelete: EditorTheme?
    @State private var fontFamilies: [String] = []

    private var selectedTheme: EditorTheme? {
        selectedThemeID.flatMap(store.theme(id:))
    }

    var body: some View {
        themesSection
            .onAppear {
                // A modifier on a section can run for each of its rows.
                if fontFamilies.isEmpty {
                    fontFamilies = NSFontManager.shared.availableFontFamilies.sorted()
                }
                if selectedThemeID == nil {
                    select(store.activeThemeID ?? store.themes.first?.id)
                }
            }

        if let theme = selectedTheme {
            Section("Preview") {
                EditorThemePreview(theme: theme)
            }
            nameSection
            shapeSection("Rectangle", theme.rectangle, \.rectangle)
            shapeSection("Ellipse", theme.ellipse, \.ellipse)
            arrowSection
            strokeSection("Line", \.line)
            strokeSection("Pen", \.pen)
            textSection(theme)
            numberSection
            canvasSection(theme)
        }
    }

    // MARK: - Theme list

    private var themesSection: some View {
        Section {
            Picker("Active theme:", selection: activeThemeBinding) {
                Text("None").tag(String?.none)
                ForEach(store.themes) { theme in
                    Text(theme.name).tag(String?.some(theme.id))
                }
            }
            .help("New editor windows start with this theme. Open windows keep their own.")

            ForEach(store.themes) { theme in
                themeRow(theme)
            }

            HStack {
                Button("New", action: createTheme)
                Button("Duplicate", action: duplicateSelectedTheme)
                    .disabled(selectedTheme == nil)
                Button("Delete…", role: .destructive) {
                    themePendingDelete = selectedTheme
                }
                .disabled(selectedTheme == nil)
                .confirmationDialog(
                    "Delete \(themePendingDelete?.name ?? "theme")?",
                    isPresented: Binding(
                        get: { themePendingDelete != nil },
                        set: { if !$0 { themePendingDelete = nil } }
                    ),
                    titleVisibility: .visible
                ) {
                    Button("Delete Theme", role: .destructive, action: deletePendingTheme)
                    Button("Cancel", role: .cancel) {}
                } message: {
                    Text("Saved screenshots don't change.")
                }
                Spacer()
                Button("Import…", action: importTheme)
                Button("Export…", action: exportSelectedTheme)
                    .disabled(selectedTheme == nil)
            }

            if let errorMessage {
                Label(errorMessage, systemImage: "exclamationmark.triangle")
                    .foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityLabel("Error: \(errorMessage)")
            }
        } header: {
            Text("Editor Themes")
        } footer: {
            Text("A theme is a saved look for screenshot annotations and the export canvas. Choose one from the Theme menu in the screenshot editor. Redactions and emoji are never restyled.")
        }
    }

    private func themeRow(_ theme: EditorTheme) -> some View {
        let isSelected = theme.id == selectedThemeID
        let isActive = theme.id == store.activeThemeID

        return Button {
            select(theme.id)
        } label: {
            HStack {
                Image(systemName: isSelected ? "pencil.circle.fill" : "circle")
                    .foregroundStyle(isSelected ? Color.accentColor : .secondary)
                Text(theme.name)
                    .lineLimit(1)
                Spacer()
                if isActive {
                    Text("Active")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(theme.name)
        .accessibilityValue(isActive ? "Active" : "Not active")
        .accessibilityHint("Edits this theme.")
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }

    private var activeThemeBinding: Binding<String?> {
        Binding(
            get: { store.activeThemeID },
            set: { id in
                perform { try store.setActive(id: id) }
            }
        )
    }

    private func select(_ id: String?) {
        selectedThemeID = id
        nameDraft = id.flatMap(store.theme(id:))?.name ?? ""
        errorMessage = nil
    }

    private func perform(_ work: () throws -> Void) {
        do {
            try work()
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func createTheme() {
        perform {
            let theme = try store.save(EditorTheme(name: store.uniqueName(for: "New Theme")))
            select(theme.id)
        }
    }

    private func duplicateSelectedTheme() {
        guard var copy = selectedTheme else { return }
        copy.id = UUID().uuidString
        copy.name = store.uniqueName(for: "\(copy.name) Copy")
        perform {
            let theme = try store.save(copy)
            select(theme.id)
        }
    }

    private func deletePendingTheme() {
        guard let theme = themePendingDelete else { return }
        themePendingDelete = nil
        perform {
            try store.delete(id: theme.id)
            if selectedThemeID == theme.id {
                select(store.activeThemeID ?? store.themes.first?.id)
            }
        }
    }

    private func importTheme() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.json]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        perform {
            let theme = try store.importTheme(from: try Data(contentsOf: url))
            select(theme.id)
        }
    }

    private func exportSelectedTheme() {
        guard let theme = selectedTheme else { return }
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.json]
        panel.canCreateDirectories = true
        panel.nameFieldStringValue = "\(theme.name.replacingOccurrences(of: "/", with: "-")).json"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        perform {
            try store.exportData(id: theme.id).write(to: url, options: [.atomic])
        }
    }

    // MARK: - Bindings

    private func update(_ mutation: (inout EditorTheme) -> Void) {
        guard var theme = selectedTheme else { return }
        mutation(&theme)
        perform { try store.save(theme) }
    }

    private func value<Value>(_ keyPath: WritableKeyPath<EditorTheme, Value>, default defaultValue: Value) -> Binding<Value> {
        Binding(
            get: { selectedTheme?[keyPath: keyPath] ?? defaultValue },
            set: { newValue in update { $0[keyPath: keyPath] = newValue } }
        )
    }

    private func color(_ keyPath: WritableKeyPath<EditorTheme, String>) -> Binding<Color> {
        Binding(
            get: { EditorThemeColor.color(selectedTheme?[keyPath: keyPath] ?? EditorThemeColor.transparentHex) },
            set: { newValue in update { $0[keyPath: keyPath] = EditorThemeColor.hex(newValue) } }
        )
    }

    /// Sections repeat titles such as "Color:", so `name` is what VoiceOver reads.
    private func colorRow(_ title: String, _ keyPath: WritableKeyPath<EditorTheme, String>, name: String) -> some View {
        ColorPicker(title, selection: color(keyPath), supportsOpacity: true)
            .accessibilityLabel(name)
    }

    private func slider(
        _ title: String,
        _ keyPath: WritableKeyPath<EditorTheme, Double>,
        in range: ClosedRange<Double>,
        step: Double = 1,
        name: String
    ) -> some View {
        let current = selectedTheme?[keyPath: keyPath] ?? range.lowerBound
        let text = current.formatted(.number.precision(.fractionLength(0...2)))
        return HStack {
            Text(title)
            Slider(value: value(keyPath, default: range.lowerBound), in: range, step: step)
                .accessibilityLabel(name)
                .accessibilityValue(text)
            Text(text)
                .monospacedDigit()
                .frame(width: 44, alignment: .trailing)
                .accessibilityHidden(true)
        }
    }

    private func namedPicker(
        _ title: String,
        _ keyPath: WritableKeyPath<EditorTheme, String>,
        options: [(name: String, label: String)],
        name: String
    ) -> some View {
        Picker(title, selection: value(keyPath, default: options[0].name)) {
            ForEach(options, id: \.name) { option in
                Text(option.label).tag(option.name)
            }
        }
        .accessibilityLabel(name)
    }
}

// MARK: - Edit form

private extension EditorThemesSettingsSection {
    var nameSection: some View {
        Section("Name") {
            TextField("Name:", text: $nameDraft)
                .onChange(of: nameDraft) { _, newName in
                    guard newName != selectedTheme?.name else { return }
                    update { $0.name = newName }
                }
            Toggle("Apply background when the editor opens", isOn: value(\.canvas.applyOnOpen, default: false))
                .help("Otherwise this theme's background applies only when you choose the theme in the editor.")
        }
    }

    /// The rectangle and ellipse controls, which differ only in the section they edit.
    func shapeSection(
        _ title: String,
        _ section: EditorTheme.ShapeSection,
        _ keyPath: WritableKeyPath<EditorTheme, EditorTheme.ShapeSection>
    ) -> some View {
        Section(title) {
            colorRow("Stroke color:", keyPath.appending(path: \EditorTheme.ShapeSection.strokeColor), name: "\(title) stroke color")
            slider(
                "Stroke width:",
                keyPath.appending(path: \EditorTheme.ShapeSection.strokeWidth),
                in: EditorThemeRange.strokeWidth,
                step: 0.5,
                name: "\(title) stroke width"
            )
            ColorPicker("Fill color:", selection: shapeFillBinding(keyPath), supportsOpacity: true)
                .accessibilityLabel("\(title) fill color")
            Toggle("Gradient fill", isOn: gradientBinding(keyPath))
                .disabled(!EditorThemeColor.isVisible(section.fillColor))
                .help("A gradient starts from the fill color, so the fill can't be transparent.")
                .accessibilityLabel("\(title) gradient fill")
            if section.gradientEndColor != nil {
                ColorPicker("End color:", selection: gradientEndBinding(keyPath), supportsOpacity: true)
                    .accessibilityLabel("\(title) gradient end color")
                slider(
                    "Direction:",
                    keyPath.appending(path: \EditorTheme.ShapeSection.gradientDirection),
                    in: EditorThemeRange.direction,
                    name: "\(title) gradient direction"
                )
            }
            slider(
                "Shadow blur:",
                keyPath.appending(path: \EditorTheme.ShapeSection.shadowBlur),
                in: EditorThemeRange.shadowBlur,
                name: "\(title) shadow blur"
            )
            if section.shadowBlur > 0 {
                colorRow("Shadow color:", keyPath.appending(path: \EditorTheme.ShapeSection.shadowColor), name: "\(title) shadow color")
            }
        }
    }

    /// A transparent fill can't start a gradient, so losing the fill also ends the gradient.
    func shapeFillBinding(_ keyPath: WritableKeyPath<EditorTheme, EditorTheme.ShapeSection>) -> Binding<Color> {
        Binding(
            get: { EditorThemeColor.color(selectedTheme?[keyPath: keyPath].fillColor ?? EditorThemeColor.transparentHex) },
            set: { newValue in
                update { theme in
                    theme[keyPath: keyPath].fillColor = EditorThemeColor.hex(newValue)
                    if !EditorThemeColor.isVisible(theme[keyPath: keyPath].fillColor) {
                        theme[keyPath: keyPath].gradientEndColor = nil
                    }
                }
            }
        )
    }

    func gradientBinding(_ keyPath: WritableKeyPath<EditorTheme, EditorTheme.ShapeSection>) -> Binding<Bool> {
        Binding(
            get: { selectedTheme?[keyPath: keyPath].gradientEndColor != nil },
            set: { isOn in
                update { $0[keyPath: keyPath].gradientEndColor = isOn ? "#FF3385F5" : nil }
            }
        )
    }

    func gradientEndBinding(_ keyPath: WritableKeyPath<EditorTheme, EditorTheme.ShapeSection>) -> Binding<Color> {
        Binding(
            get: { EditorThemeColor.color(selectedTheme?[keyPath: keyPath].gradientEndColor ?? EditorThemeColor.transparentHex) },
            set: { newValue in update { $0[keyPath: keyPath].gradientEndColor = EditorThemeColor.hex(newValue) } }
        )
    }

    var arrowSection: some View {
        Section("Arrow") {
            colorRow("Color:", \.arrow.color, name: "Arrow color")
            slider("Width:", \.arrow.width, in: EditorThemeRange.strokeWidth, step: 0.5, name: "Arrow width")
            namedPicker(
                "Style:",
                \.arrow.style,
                options: ArrowStyle.allCases.map { (EditorThemeNames.name(for: $0), $0.label) },
                name: "Arrow style"
            )
        }
    }

    func strokeSection(_ title: String, _ keyPath: WritableKeyPath<EditorTheme, EditorTheme.StrokeSection>) -> some View {
        Section(title) {
            colorRow("Color:", keyPath.appending(path: \EditorTheme.StrokeSection.color), name: "\(title) color")
            slider(
                "Width:",
                keyPath.appending(path: \EditorTheme.StrokeSection.width),
                in: EditorThemeRange.strokeWidth,
                step: 0.5,
                name: "\(title) width"
            )
        }
    }

    func textSection(_ theme: EditorTheme) -> some View {
        Section("Text") {
            colorRow("Color:", \.text.color, name: "Text color")
            Picker("Font:", selection: value(\.text.fontFamily, default: nil)) {
                Text(textSystemFontFamily).tag(String?.none)
                ForEach(fontFamilyOptions(including: theme.text.fontFamily), id: \.self) { family in
                    Text(family).tag(String?.some(family))
                }
            }
            .accessibilityLabel("Text font")
            slider("Size:", \.text.fontSize, in: EditorThemeRange.fontSize, name: "Text size")
            Toggle("Bold", isOn: value(\.text.bold, default: false))
            Toggle("Italic", isOn: value(\.text.italic, default: false))
            Toggle("Underline", isOn: value(\.text.underline, default: false))
            colorRow("Box background:", \.text.box.backgroundColor, name: "Text box background")
            colorRow("Box border:", \.text.box.borderColor, name: "Text box border")
            slider("Border width:", \.text.box.borderWidth, in: EditorThemeRange.boxBorderWidth, name: "Text box border width")
            slider("Box padding:", \.text.box.padding, in: EditorThemeRange.boxPadding, name: "Text box padding")
            slider("Box corners:", \.text.box.cornerRadius, in: EditorThemeRange.boxCornerRadius, name: "Text box corners")
        }
    }

    /// A theme may name a font this Mac doesn't have, and the picker still needs a row for it.
    func fontFamilyOptions(including family: String?) -> [String] {
        guard let family, family != textSystemFontFamily, !fontFamilies.contains(family) else { return fontFamilies }
        return [family] + fontFamilies
    }

    var numberSection: some View {
        Section("Numbers") {
            colorRow("Badge color:", \.number.badgeColor, name: "Number badge color")
            colorRow("Text color:", \.number.textColor, name: "Number text color")
            slider("Size:", \.number.size, in: EditorThemeRange.numberSize, step: 0.05, name: "Number size")
        }
    }

    func canvasSection(_ theme: EditorTheme) -> some View {
        Section("Canvas") {
            namedPicker(
                "Background:",
                \.canvas.background,
                options: [("transparent", "Transparent"), ("solid", "Solid"), ("gradient", "Gradient")],
                name: "Canvas background"
            )
            if theme.canvas.background != "transparent" {
                colorRow("Color:", \.canvas.color, name: "Canvas color")
            }
            if theme.canvas.background == "gradient" {
                colorRow("Color 2:", \.canvas.secondaryColor, name: "Canvas second color")
                slider("Direction:", \.canvas.gradientDirection, in: EditorThemeRange.direction, name: "Canvas gradient direction")
            }
            slider("Padding:", \.canvas.padding, in: EditorThemeRange.canvasPadding, name: "Canvas padding")
            slider("Image corners:", \.canvas.cornerRadius, in: EditorThemeRange.canvasCornerRadius, name: "Canvas image corners")
            slider("Shadow:", \.canvas.shadow, in: EditorThemeRange.canvasShadow, name: "Canvas shadow")
            namedPicker(
                "Frame:",
                \.canvas.frame,
                options: ExportFramePreset.allCases.map { (EditorThemeNames.name(for: $0), $0.label) },
                name: "Canvas frame"
            )
            namedPicker(
                "Horizontal alignment:",
                \.canvas.horizontalAlignment,
                options: ExportHorizontalAlignment.allCases.map { (EditorThemeNames.name(for: $0), $0.label) },
                name: "Canvas horizontal alignment"
            )
            namedPicker(
                "Vertical alignment:",
                \.canvas.verticalAlignment,
                options: ExportVerticalAlignment.allCases.map { (EditorThemeNames.name(for: $0), $0.label) },
                name: "Canvas vertical alignment"
            )
        }
    }
}

// MARK: - Preview

/// A sample of the theme on an 800-pixel-wide screenshot, drawn with the editor's own styling and
/// shape renderer so the two can't disagree.
private struct EditorThemePreview: View {
    let theme: EditorTheme

    private let imageSize = CGSize(width: 800, height: 450)
    private let available = CGSize(width: 420, height: 240)

    private var layout: ExportFrameLayout {
        ExportFrameLayout.make(
            imageSize: imageSize,
            padding: CGFloat(theme.canvas.padding),
            preset: EditorThemeNames.frame(theme.canvas.frame),
            horizontalAlignment: EditorThemeNames.horizontalAlignment(theme.canvas.horizontalAlignment),
            verticalAlignment: EditorThemeNames.verticalAlignment(theme.canvas.verticalAlignment)
        )
    }

    var body: some View {
        let layout = self.layout
        let scale = min(available.width / layout.frameSize.width, available.height / layout.frameSize.height)
        let imageRect = CGRect(
            x: layout.imageRect.minX * scale,
            y: layout.imageRect.minY * scale,
            width: layout.imageRect.width * scale,
            height: layout.imageRect.height * scale
        )

        ZStack(alignment: .topLeading) {
            background
            samples(scale: scale, size: imageRect.size)
                .frame(width: imageRect.width, height: imageRect.height)
                .background(Color(nsColor: .textBackgroundColor))
                .clipShape(RoundedRectangle(cornerRadius: CGFloat(theme.canvas.cornerRadius) * scale))
                .shadow(color: .black.opacity(0.25), radius: CGFloat(theme.canvas.shadow) * scale)
                .offset(x: imageRect.minX, y: imageRect.minY)
        }
        .frame(width: layout.frameSize.width * scale, height: layout.frameSize.height * scale)
        .overlay {
            Rectangle().strokeBorder(.separator)
        }
        .frame(maxWidth: .infinity)
        .accessibilityElement()
        .accessibilityLabel("Preview of \(theme.name)")
    }

    @ViewBuilder
    private var background: some View {
        switch EditorThemeNames.background(theme.canvas.background) {
        case .solid:
            EditorThemeColor.color(theme.canvas.color)
        case .gradient:
            let endpoints = ScreenshotGradientMath.endpoints(
                degrees: theme.canvas.gradientDirection,
                width: layout.frameSize.width,
                height: layout.frameSize.height
            )
            LinearGradient(
                colors: [EditorThemeColor.color(theme.canvas.color), EditorThemeColor.color(theme.canvas.secondaryColor)],
                startPoint: endpoints.start,
                endPoint: endpoints.end
            )
        case .transparent, .wallpaper:
            Color.clear
        }
    }

    private func styled(_ tool: EditTool, rect: CGRect = .zero, text: String = "") -> ScreenshotAnnotation {
        var annotation = ScreenshotAnnotation(tool: tool, rect: rect, color: .clear, lineWidth: 0, text: text, points: [])
        EditorThemeStyling.apply(theme, to: &annotation, imagePixelSize: imageSize)
        return annotation
    }

    /// One sample of each annotation type: shapes and the number along the top, then the arrow,
    /// line, pen, and text along the bottom, in separate parts of the image so they don't overlap.
    private func samples(scale: CGFloat, size: CGSize) -> some View {
        let shapes = [
            styled(.rectangle, rect: CGRect(x: 0.05, y: 0.08, width: 0.26, height: 0.36)),
            styled(.circle, rect: CGRect(x: 0.37, y: 0.08, width: 0.26, height: 0.36)),
        ]
        let number = styled(.number, rect: CGRect(x: 0.795, y: 0.215, width: 0.05, height: 0.09), text: "1")
        let arrow = styled(.arrow)
        let line = styled(.line)
        let pen = styled(.pencil)

        return ZStack(alignment: .topLeading) {
            Canvas { context, _ in
                for shape in shapes {
                    ShapeEffectsRenderer.drawPreview(
                        shape,
                        in: context,
                        rect: RotatableAnnotationGeometry.scaledRect(shape.rect, in: size),
                        lineWidth: shape.lineWidth * scale,
                        effectScale: scale
                    )
                }
                drawArrow(
                    arrow,
                    from: CGPoint(x: size.width * 0.05, y: size.height * 0.9),
                    to: CGPoint(x: size.width * 0.27, y: size.height * 0.6),
                    scale: scale,
                    in: context
                )
                var linePath = Path()
                linePath.move(to: CGPoint(x: size.width * 0.34, y: size.height * 0.9))
                linePath.addLine(to: CGPoint(x: size.width * 0.5, y: size.height * 0.6))
                context.stroke(linePath, with: .color(line.color), lineWidth: line.lineWidth * scale)
                context.stroke(penPath(in: size), with: .color(pen.color), lineWidth: pen.lineWidth * scale)
            }

            sampleText(scale: scale)
                .position(x: size.width * 0.85, y: size.height * 0.75)

            Circle()
                .fill(number.color)
                .frame(width: number.rect.width * size.width, height: number.rect.width * size.width)
                .overlay {
                    Text(number.text)
                        .font(.system(size: number.rect.width * size.width * numberCircleFontRatio, weight: .bold))
                        .foregroundStyle(number.textColor)
                }
                .position(x: number.rect.midX * size.width, y: number.rect.midY * size.height)
        }
    }

    private func sampleText(scale: CGFloat) -> some View {
        let box = theme.text.box
        return Text("Text")
            .font(textPreviewFont(
                family: theme.text.fontFamily ?? textSystemFontFamily,
                size: CGFloat(theme.text.fontSize) * scale,
                isBold: theme.text.bold
            ))
            .italic(theme.text.italic)
            .underline(theme.text.underline)
            .foregroundStyle(EditorThemeColor.color(theme.text.color))
            .lineLimit(1)
            .fixedSize()
            .padding(CGFloat(box.padding + box.borderWidth / 2) * scale)
            .background {
                RoundedRectangle(cornerRadius: CGFloat(box.cornerRadius) * scale)
                    .fill(EditorThemeColor.color(box.backgroundColor))
            }
            .overlay {
                RoundedRectangle(cornerRadius: CGFloat(box.cornerRadius) * scale)
                    .strokeBorder(EditorThemeColor.color(box.borderColor), lineWidth: CGFloat(box.borderWidth) * scale)
            }
    }

    /// The editor canvas's arrow: a shaft ending at the base of a filled head.
    private func drawArrow(_ arrow: ScreenshotAnnotation, from start: CGPoint, to end: CGPoint, scale: CGFloat, in context: GraphicsContext) {
        let lineWidth = arrow.lineWidth * scale
        let headLength = max(18 * scale, lineWidth * 4)
        let headAngle = CGFloat.pi / 6
        let control = arrowControlPoint(start: start, end: end, style: arrow.arrowStyle)
        let tipAngle = atan2(end.y - control.y, end.x - control.x)
        let shaftEnd = CGPoint(
            x: end.x - headLength * cos(headAngle) * cos(tipAngle),
            y: end.y - headLength * cos(headAngle) * sin(tipAngle)
        )

        var shaft = Path()
        shaft.move(to: start)
        shaft.addQuadCurve(to: shaftEnd, control: control)
        context.stroke(shaft, with: .color(arrow.color), lineWidth: lineWidth)

        var head = Path()
        head.move(to: end)
        head.addLine(to: CGPoint(x: end.x - headLength * cos(tipAngle - headAngle), y: end.y - headLength * sin(tipAngle - headAngle)))
        head.addLine(to: CGPoint(x: end.x - headLength * cos(tipAngle + headAngle), y: end.y - headLength * sin(tipAngle + headAngle)))
        head.closeSubpath()
        context.fill(head, with: .color(arrow.color))
    }

    private func penPath(in size: CGSize) -> Path {
        var path = Path()
        for step in 0...32 {
            let progress = CGFloat(step) / 32
            let point = CGPoint(
                x: size.width * (0.56 + 0.14 * progress),
                y: size.height * (0.75 + 0.12 * sin(progress * 6 * .pi))
            )
            if step == 0 {
                path.move(to: point)
            } else {
                path.addLine(to: point)
            }
        }
        return path
    }
}
