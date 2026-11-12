import SwiftUI

/// The click-effect settings as form rows, for a Settings section.
struct MouseClickOverlayControls: View {
    let color: Binding<NSColor>
    let size: Binding<Double>
    let strokeWidth: Binding<Double>
    let opacity: Binding<Double>
    let duration: Binding<Double>

    var body: some View {
        HStack {
            Text("Color")
            Spacer()
            MouseClickColorWell(color: color)
                .frame(width: 44, height: 22)
                .accessibilityLabel("Mouse click color")
        }

        SettingsSliderRow(
            title: "Size",
            value: size,
            range: 12...100,
            step: 1,
            valueText: "\(Int(size.wrappedValue)) px"
        )

        SettingsSliderRow(
            title: "Stroke width",
            value: strokeWidth,
            range: 1...10,
            step: 1,
            valueText: "\(Int(strokeWidth.wrappedValue)) px"
        )

        SettingsSliderRow(
            title: "Opacity",
            value: opacity,
            range: 0.1...1.0,
            step: 0.05,
            valueText: "\(Int((opacity.wrappedValue * 100).rounded()))%"
        )

        SettingsSliderRow(
            title: "Duration",
            value: duration,
            range: 0.15...1.0,
            step: 0.05,
            valueText: String(format: "%.2f s", duration.wrappedValue)
        )
    }
}

struct MouseClickColorWell: NSViewRepresentable {
    @Binding var color: NSColor

    func makeCoordinator() -> Coordinator {
        Coordinator(color: $color)
    }

    func makeNSView(context: Context) -> NSColorWell {
        let well = NSColorWell()
        well.target = context.coordinator
        well.action = #selector(Coordinator.colorDidChange(_:))
        well.isBordered = true
        well.color = color
        return well
    }

    func updateNSView(_ nsView: NSColorWell, context: Context) {
        if !nsView.color.isEqual(color) {
            nsView.color = color
        }
    }

    final class Coordinator: NSObject {
        private let color: Binding<NSColor>

        init(color: Binding<NSColor>) {
            self.color = color
        }

        @objc func colorDidChange(_ sender: NSColorWell) {
            color.wrappedValue = sender.color
        }
    }
}
