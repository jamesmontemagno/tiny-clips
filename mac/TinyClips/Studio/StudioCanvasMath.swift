import Foundation

// MARK: - Canvas Math

enum StudioCanvasMath {
    static func naturalCanvas(project: StudioProject) -> StudioRect {
        let screenCrop = validCrop(project.screen.crop)
        let sw = Double(project.sources.screen.width) * (screenCrop?.width ?? 1)
        let sh = Double(project.sources.screen.height) * (screenCrop?.height ?? 1)
        let aspect = targetAspect(project.canvas.aspect, screenWidth: sw, screenHeight: sh)

        let width: Double
        let height: Double
        if project.canvas.aspect == .auto {
            width = sw
            height = sh
        } else if sw / sh >= aspect {
            width = sw
            height = sw / aspect
        } else {
            width = sh * aspect
            height = sh
        }

        return StudioRect(x: 0, y: 0, width: even(width), height: even(height))
    }

    static func exportSize(project: StudioProject, longSideLimit: Double) -> StudioRect {
        let natural = naturalCanvas(project: project)
        return exportSize(naturalWidth: natural.width, naturalHeight: natural.height, longSideLimit: longSideLimit)
    }

    static func exportSize(naturalWidth: Double, naturalHeight: Double, longSideLimit: Double) -> StudioRect {
        let scale = longSideLimit <= 0 ? 1 : min(1, longSideLimit / max(naturalWidth, naturalHeight))
        return StudioRect(
            x: 0,
            y: 0,
            width: even(naturalWidth * scale),
            height: even(naturalHeight * scale)
        )
    }

    static func even(_ value: Double) -> Double {
        max(2, 2 * (value / 2).rounded())
    }

    static func validCrop(_ rect: StudioRect?) -> StudioRect? {
        guard let rect = rect else { return nil }
        guard rect.x >= 0,
              rect.y >= 0,
              rect.width >= 0.05,
              rect.height >= 0.05,
              rect.x + rect.width <= 1 + 1e-9,
              rect.y + rect.height <= 1 + 1e-9 else {
            return nil
        }
        return rect
    }

    static func clamped(_ value: Double, _ lower: Double, _ upper: Double) -> Double {
        min(max(value, lower), upper)
    }

    private static func targetAspect(_ aspect: StudioCanvasAspect, screenWidth: Double, screenHeight: Double) -> Double {
        switch aspect {
        case .auto:
            return screenWidth / screenHeight
        case .square:
            return 1
        case .landscape4x3:
            return 4.0 / 3.0
        case .landscape16x9:
            return 16.0 / 9.0
        case .portrait3x4:
            return 3.0 / 4.0
        case .portrait9x16:
            return 9.0 / 16.0
        }
    }
}
