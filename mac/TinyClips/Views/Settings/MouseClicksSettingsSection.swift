import SwiftUI

struct MouseClicksSettingsSection: View {
    @ObservedObject var settings: CaptureSettings
    private let clickLightURL = URL(string: "https://github.com/aurorascharff/ClickLight")!

    var body: some View {
        mouseClicksControls
    }

    @ViewBuilder
    private var mouseClicksControls: some View {
        SettingsFormSection(
            "Video",
            icon: "video",
            footer: "Click effects are added to the saved recording, which adds processing time when a recording ends."
        ) {
            MouseClickOverlayControls(
                color: videoMouseClickColorBinding,
                size: Binding(
                    get: { settings.videoMouseClickSize },
                    set: { settings.videoMouseClickSize = $0 }
                ),
                strokeWidth: Binding(
                    get: { settings.videoMouseClickStrokeWidth },
                    set: { settings.videoMouseClickStrokeWidth = $0 }
                ),
                opacity: Binding(
                    get: { settings.videoMouseClickOpacity },
                    set: { settings.videoMouseClickOpacity = $0 }
                ),
                duration: Binding(
                    get: { settings.videoMouseClickDuration },
                    set: { settings.videoMouseClickDuration = $0 }
                )
            )
        }

        SettingsFormSection("GIF", icon: "photo.on.rectangle.angled") {
            SettingsToggleRow(
                title: "Use Video click settings",
                description: "GIF recordings use the same click effect as Video.",
                isOn: $settings.gifMouseClicksUseVideoSettings,
                help: "When enabled, GIF click visuals mirror Video settings."
            )

            if !settings.gifMouseClicksUseVideoSettings {
                MouseClickOverlayControls(
                    color: gifMouseClickColorBinding,
                    size: Binding(
                        get: { settings.gifMouseClickSize },
                        set: { settings.gifMouseClickSize = $0 }
                    ),
                    strokeWidth: Binding(
                        get: { settings.gifMouseClickStrokeWidth },
                        set: { settings.gifMouseClickStrokeWidth = $0 }
                    ),
                    opacity: Binding(
                        get: { settings.gifMouseClickOpacity },
                        set: { settings.gifMouseClickOpacity = $0 }
                    ),
                    duration: Binding(
                        get: { settings.gifMouseClickDuration },
                        set: { settings.gifMouseClickDuration = $0 }
                    )
                )
            }
        }

        SettingsFormSection("Live Click Animations", icon: "cursorarrow.rays") {
            SettingsActionRow(
                title: "ClickLight",
                description: "For the best live click animations while you record, install ClickLight."
            ) {
                Link("Get ClickLight", destination: clickLightURL)
                    .help("Opens ClickLight on GitHub.")
            }
        }
    }

    private var videoMouseClickColorBinding: Binding<NSColor> {
        Binding(
            get: { settings.videoMouseClickColor },
            set: { settings.videoMouseClickColor = $0 }
        )
    }

    private var gifMouseClickColorBinding: Binding<NSColor> {
        Binding(
            get: { settings.gifMouseClickColor },
            set: { settings.gifMouseClickColor = $0 }
        )
    }
}
