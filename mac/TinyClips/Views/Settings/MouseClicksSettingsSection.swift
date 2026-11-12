import SwiftUI

struct MouseClicksSettingsSection: View {
    @ObservedObject var settings: CaptureSettings
    private let clickLightURL = URL(string: "https://github.com/aurorascharff/ClickLight")!

    var body: some View {
        mouseClicksControls
    }

    @ViewBuilder
    private var mouseClicksControls: some View {
        SettingsFormSection("Mouse Click Effects", icon: "cursorarrow.rays") {
            Text("Tune the saved click pulse for recordings. Adding click effects will add more processing time at the end of recording.")
                .settingsDescription()
            Link("For the best live click animations, install ClickLight.", destination: clickLightURL)
                .help("Opens ClickLight on GitHub.")
        }

        SettingsFormSection("Video", icon: "video") {
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
            Toggle("Use Video click settings", isOn: $settings.gifMouseClicksUseVideoSettings)
                .help("When enabled, GIF click visuals mirror Video settings.")

            if settings.gifMouseClicksUseVideoSettings {
                Text("GIF click visuals mirror Video settings.")
                    .settingsDescription()
            } else {
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
