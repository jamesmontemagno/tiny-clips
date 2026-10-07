import Foundation

/// The panels of the Studio inspector. One is on show at a time. The rail down the inspector's
/// edge chooses which, and so does taking hold of something on the timeline or in the preview.
enum StudioInspectorPanel: String, CaseIterable, Identifiable {
    case scene
    case background
    case screen
    case camera
    case zoom
    case cut
    case speed
    case audio
    case project

    var id: String { rawValue }

    var title: String {
        switch self {
        case .scene: return "Scene"
        case .background: return "Background"
        case .screen: return "Screen"
        case .camera: return "Camera"
        case .zoom: return "Zoom"
        case .cut: return "Cut"
        case .speed: return "Speed"
        case .audio: return "Audio"
        case .project: return "Project"
        }
    }

    /// The SF Symbol on the panel's rail button.
    var symbolName: String {
        switch self {
        case .scene: return "rectangle.inset.bottomright.filled"
        case .background: return "photo"
        case .screen: return "macwindow"
        case .camera: return "video"
        case .zoom: return "plus.magnifyingglass"
        case .cut: return "scissors"
        case .speed: return "gauge.with.needle"
        case .audio: return "speaker.wave.2"
        case .project: return "folder"
        }
    }

    /// What the panel holds, for the rail button's help.
    var summary: String {
        switch self {
        case .scene: return "Scene: layout, splits, and transitions"
        case .background: return "Background and padding"
        case .screen: return "Screen: corners, shadow, clicks, and crop"
        case .camera: return "Camera: placement, appearance, and crop"
        case .zoom: return "Zooms"
        case .cut: return "Cuts"
        case .speed: return "Speed changes"
        case .audio: return "Audio: mute and volumes"
        case .project: return "Project: save, storage, and delete"
        }
    }

    /// The rail's panels in order, in the groups it draws a line between: how the picture looks,
    /// what is edited along the timeline, and the rest. A recording without a camera has one
    /// layout and nothing to place, so it has neither a Scene nor a Camera panel.
    static func groups(hasCamera: Bool) -> [[StudioInspectorPanel]] {
        let look: [StudioInspectorPanel] = hasCamera
            ? [.scene, .background, .screen, .camera]
            : [.background, .screen]
        return [look, [.zoom, .cut, .speed], [.audio, .project]]
    }

    static func available(hasCamera: Bool) -> [StudioInspectorPanel] {
        groups(hasCamera: hasCamera).flatMap { $0 }
    }

    /// The panel a project opens on.
    static func initial(hasCamera: Bool) -> StudioInspectorPanel {
        hasCamera ? .scene : .background
    }

    /// The panel to show when `panel` is asked for: itself, or its nearest neighbor when the
    /// recording has no camera.
    static func resolved(_ panel: StudioInspectorPanel, hasCamera: Bool) -> StudioInspectorPanel {
        guard !hasCamera else { return panel }
        switch panel {
        case .scene: return .background
        case .camera: return .screen
        default: return panel
        }
    }
}
