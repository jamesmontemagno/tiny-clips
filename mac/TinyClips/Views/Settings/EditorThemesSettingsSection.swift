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
            shapeSection(theme)
            lineSection
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

    private func colorRow(_ title: String, _ keyPath: WritableKeyPath<EditorTheme, String>) -> some View {
        ColorPicker(title, selection: color(keyPath), supportsOpacity: true)
    }

    private func slider(
        _ title: String,
        _ keyPath: WritableKeyPath<EditorTheme, Double>,
        in range: ClosedRange<Double>,
        step: Double = 1
    ) -> some View {
        let current = selectedTheme?[keyPath: keyPath] ?? range.lowerBound
        let text = current.formatted(.number.precision(.fractionLength(0...2)))
        return HStack {
            Text(title)
            Slider(value: value(keyPath, default: range.lowerBound), in: range, step: step)
                .accessibilityLabel(title)
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
        options: [(name: String, label: String)]
    ) -> some View {
        Picker(title, selection: value(keyPath, default: options[0].name)) {
            ForEach(options, id: \.name) { option in
                Text(option.label).tag(option.name)
            }
        }
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

    func shapeSection(_ theme: EditorTheme) -> some View {
        Section("Shapes") {
            colorRow("Stroke color:", \.shape.strokeColor)
            slider("Stroke width:", \.shape.strokeWidth, in: EditorThemeRange.strokeWidth, step: 0.5)
            ColorPicker("Fill color:", selection: shapeFillBinding, supportsOpacity: true)
            Toggle("Gradient fill", isOn: gradientBinding)
                .disabled(!EditorThemeColor.isVisible(theme.shape.fillColor))
                .help("A gradient starts from the fill color, so the fill can't be transparent.")
            if theme.shape.gradientEndColor != nil {
                ColorPicker("End color:", selection: gradientEndBinding, supportsOpacity: true)
                slider("Direction:", \.shape.gradientDirection, in: EditorThemeRange.direction)
            }
            slider("Shadow blur:", \.shape.shadowBlur, in: EditorThemeRange.shadowBlur)
            if theme.shape.shadowBlur > 0 {
                colorRow("Shadow color:", \.shape.shadowColor)
            }
        }
    }

    /// A transparent fill can't start a gradient, so losing the fill also ends the gradient.
    var shapeFillBinding: Binding<Color> {
        Binding(
            get: { EditorThemeColor.color(selectedTheme?.shape.fillColor ?? EditorThemeColor.transparentHex) },
            set: { newValue in
                update { theme in
                    theme.shape.fillColor = EditorThemeColor.hex(newValue)
                    if !EditorThemeColor.isVisible(theme.shape.fillColor) {
                        theme.shape.gradientEndColor = nil
                    }
                }
            }
        )
    }

    var gradientBinding: Binding<Bool> {
        Binding(
            get: { selectedTheme?.shape.gradientEndColor != nil },
            set: { isOn in
                update { $0.shape.gradientEndColor = isOn ? "#FF3385F5" : nil }
            }
        )
    }

    var gradientEndBinding: Binding<Color> {
        Binding(
            get: { EditorThemeColor.color(selectedTheme?.shape.gradientEndColor ?? EditorThemeColor.transparentHex) },
            set: { newValue in update { $0.shape.gradientEndColor = EditorThemeColor.hex(newValue) } }
        )
    }

    var lineSection: some View {
        Section("Arrows, Lines, and Pen") {
            colorRow("Color:", \.line.color)
            slider("Width:", \.line.width, in: EditorThemeRange.strokeWidth, step: 0.5)
            namedPicker(
                "Arrow style:",
                \.line.arrowStyle,
                options: ArrowStyle.allCases.map { (EditorThemeNames.name(for: $0), $0.label) }
            )
        }
    }

    func textSection(_ theme: EditorTheme) -> some View {
        Section("Text") {
            colorRow("Color:", \.text.color)
            Picker("Font:", selection: value(\.text.fontFamily, default: nil)) {
                Text(textSystemFontFamily).tag(String?.none)
                ForEach(fontFamilyOptions(including: theme.text.fontFamily), id: \.self) { family in
                    Text(family).tag(String?.some(family))
                }
            }
            slider("Size:", \.text.fontSize, in: EditorThemeRange.fontSize)
            Toggle("Bold", isOn: value(\.text.bold, default: false))
            Toggle("Italic", isOn: value(\.text.italic, default: false))
            Toggle("Underline", isOn: value(\.text.underline, default: false))
            colorRow("Box background:", \.text.box.backgroundColor)
            colorRow("Box border:", \.text.box.borderColor)
            slider("Border width:", \.text.box.borderWidth, in: EditorThemeRange.boxBorderWidth)
            slider("Box padding:", \.text.box.padding, in: EditorThemeRange.boxPadding)
            slider("Box corners:", \.text.box.cornerRadius, in: EditorThemeRange.boxCornerRadius)
        }
    }

    /// A theme may name a font this Mac doesn't have, and the picker still needs a row for it.
    func fontFamilyOptions(including family: String?) -> [String] {
        guard let family, family != textSystemFontFamily, !fontFamilies.contains(family) else { return fontFamilies }
        return [family] + fontFamilies
    }

    var numberSection: some View {
        Section("Numbers") {
            colorRow("Badge color:", \.number.badgeColor)
            colorRow("Text color:", \.number.textColor)
            slider("Size:", \.number.size, in: EditorThemeRange.numberSize, step: 0.05)
        }
    }

    func canvasSection(_ theme: EditorTheme) -> some View {
        Section("Canvas") {
            namedPicker(
                "Background:",
                \.canvas.background,
                options: [("transparent", "Transparent"), ("solid", "Solid"), ("gradient", "Gradient")]
            )
            if theme.canvas.background != "transparent" {
                colorRow("Color:", \.canvas.color)
            }
            if theme.canvas.background == "gradient" {
                colorRow("Color 2:", \.canvas.secondaryColor)
                slider("Direction:", \.canvas.gradientDirection, in: EditorThemeRange.direction)
            }
            slider("Padding:", \.canvas.padding, in: EditorThemeRange.canvasPadding)
            slider("Image corners:", \.canvas.cornerRadius, in: EditorThemeRange.canvasCornerRadius)
            slider("Shadow:", \.canvas.shadow, in: EditorThemeRange.canvasShadow)
            namedPicker(
                "Frame:",
                \.canvas.frame,
                options: ExportFramePreset.allCases.map { (EditorThemeNames.name(for: $0), $0.label) }
            )
            namedPicker(
                "Horizontal alignment:",
                \.canvas.horizontalAlignment,
                options: ExportHorizontalAlignment.allCases.map { (EditorThemeNames.name(for: $0), $0.label) }
            )
            namedPicker(
                "Vertical alignment:",
                \.canvas.verticalAlignment,
                options: ExportVerticalAlignment.allCases.map { (EditorThemeNames.name(for: $0), $0.label) }
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
            sample(scale: scale, size: imageRect.size)
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

    private func styled(_ annotation: ScreenshotAnnotation) -> ScreenshotAnnotation {
        var annotation = annotation
        EditorThemeStyling.apply(theme, to: &annotation, imagePixelSize: imageSize)
        return annotation
    }

    private func sample(scale: CGFloat, size: CGSize) -> some View {
        let shape = styled(ScreenshotAnnotation(
            tool: .rectangle,
            rect: CGRect(x: 0.08, y: 0.14, width: 0.34, height: 0.42),
            color: .clear,
            lineWidth: 0,
            text: "",
            points: []
        ))
        let number = styled(ScreenshotAnnotation(
            tool: .number,
            rect: CGRect(x: 0.84, y: 0.14, width: 0.05, height: 0.09),
            color: .clear,
            lineWidth: 0,
            text: "1",
            points: []
        ))
        let lineWidth = CGFloat(theme.line.width) * scale
        let start = CGPoint(x: size.width * 0.5, y: size.height * 0.72)
        let end = CGPoint(x: size.width * 0.8, y: size.height * 0.3)
        let lineColor = EditorThemeColor.color(theme.line.color)

        return ZStack(alignment: .topLeading) {
            Canvas { context, _ in
                ShapeEffectsRenderer.drawPreview(
                    shape,
                    in: context,
                    rect: CGRect(
                        x: shape.rect.minX * size.width,
                        y: shape.rect.minY * size.height,
                        width: shape.rect.width * size.width,
                        height: shape.rect.height * size.height
                    ),
                    lineWidth: CGFloat(theme.shape.strokeWidth) * scale,
                    effectScale: scale
                )
                context.stroke(
                    arrowPath(from: start, to: end, headLength: max(6, lineWidth * 3)),
                    with: .color(lineColor),
                    style: StrokeStyle(lineWidth: lineWidth, lineCap: .round, lineJoin: .round)
                )
            }

            sampleText(scale: scale)
                .position(x: size.width * 0.28, y: size.height * 0.8)

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
        return Text("Sample text")
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

    private func arrowPath(from start: CGPoint, to end: CGPoint, headLength: CGFloat) -> Path {
        let style = EditorThemeNames.arrowStyle(theme.line.arrowStyle)
        let bend = hypot(end.x - start.x, end.y - start.y) * 0.25 * style.curvatureSign
        let control = CGPoint(
            x: (start.x + end.x) / 2 - (end.y - start.y) / hypot(end.x - start.x, end.y - start.y) * bend,
            y: (start.y + end.y) / 2 + (end.x - start.x) / hypot(end.x - start.x, end.y - start.y) * bend
        )
        let angle = atan2(end.y - control.y, end.x - control.x)
        var path = Path()
        path.move(to: start)
        path.addQuadCurve(to: end, control: control)
        for offset in [CGFloat.pi * 0.85, -CGFloat.pi * 0.85] {
            path.move(to: end)
            path.addLine(to: CGPoint(
                x: end.x + cos(angle + offset) * headLength,
                y: end.y + sin(angle + offset) * headLength
            ))
        }
        return path
    }
}
