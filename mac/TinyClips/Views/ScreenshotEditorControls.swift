import AppKit
import SwiftUI

struct TextStyleToggleButton: View {
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

struct ArrowStyleButton: View {
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

struct EmojiPickerView: View {
    let selectedEmoji: String
    let recentEmoji: [String]
    let onSelect: (String) -> Void

    @State private var customEntry = ""
    @FocusState private var isEntryFocused: Bool

    private let columns = Array(repeating: GridItem(.flexible(), spacing: 2), count: 6)

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Text(selectedEmoji)
                    .font(.system(size: 28))
                    .frame(width: 40, height: 40)
                    .background(Color.accentColor.opacity(0.15))
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                    .accessibilityLabel("Selected emoji \(selectedEmoji)")

                TextField("Type or paste", text: $customEntry)
                    .textFieldStyle(.roundedBorder)
                    .focused($isEntryFocused)
                    .onChange(of: customEntry) { _, newValue in
                        guard let emoji = EmojiAnnotationMath.emoji(from: newValue) else { return }
                        onSelect(emoji)
                        customEntry = ""
                    }
                    .accessibilityLabel("Custom emoji")
                    .help("Type or paste any emoji, or open the emoji picker")

                Button {
                    openSystemEmojiPicker()
                } label: {
                    Image(systemName: "face.smiling")
                        .font(.system(size: 15))
                        .frame(width: 30, height: 28)
                }
                .buttonStyle(.bordered)
                .accessibilityLabel("Open emoji picker")
                .help("Open the macOS emoji picker (Control-Command-Space)")
            }

            if !recentEmoji.isEmpty {
                emojiSection("Recent", emoji: recentEmoji)
            }

            emojiSection("Common", emoji: EmojiPalette.common)
        }
    }

    /// The Character Viewer inserts into the first responder, so focus the entry field first
    /// and let `onChange` adopt whatever glyph the user picks.
    private func openSystemEmojiPicker() {
        isEntryFocused = true
        DispatchQueue.main.async {
            NSApp.orderFrontCharacterPalette(nil)
        }
    }

    private func emojiSection(_ title: String, emoji: [String]) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title)
                .font(.caption)
                .foregroundStyle(.secondary)
            LazyVGrid(columns: columns, spacing: 2) {
                ForEach(emoji, id: \.self) { item in
                    Button {
                        onSelect(item)
                    } label: {
                        Text(item)
                            .font(.system(size: 18))
                            .frame(maxWidth: .infinity, minHeight: 28)
                            .background(item == selectedEmoji ? Color.accentColor.opacity(0.2) : Color.clear)
                            .clipShape(RoundedRectangle(cornerRadius: 6))
                            .contentShape(RoundedRectangle(cornerRadius: 6))
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("Emoji \(item)")
                    .accessibilityValue(item == selectedEmoji ? "Selected" : "Not selected")
                }
            }
        }
    }
}

struct BackgroundPresetSwatch: View {
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

struct WallpaperPresetSwatch: View {
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

struct SwatchColorPicker: View {
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

struct ScreenshotEditorProgressOverlayView: View {
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

func textPreviewFont(family: String, size: CGFloat, isBold: Bool) -> Font {
    if family == textSystemFontFamily {
        return .system(size: size, weight: isBold ? .bold : .regular)
    }
    return .custom(family, size: size).weight(isBold ? .bold : .regular)
}

struct InlineTextEditor: View {
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
