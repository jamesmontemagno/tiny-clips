import Foundation

// MARK: - Resolved Frame

struct StudioResolvedShadow: Codable, Equatable, Sendable {
    var blur: Double
    var offsetY: Double
    var opacity: Double
}

struct StudioResolvedScreen: Codable, Equatable, Sendable {
    var rect: StudioRect
    var source: StudioRect
    var cornerRadius: Double
    var shadow: StudioResolvedShadow
}

struct StudioResolvedCamera: Codable, Equatable, Sendable {
    var rect: StudioRect
    var source: StudioRect
    var shape: StudioCameraShape
    var cornerRadius: Double
    var mirror: Bool
    var borderWidth: Double
    var shadow: StudioResolvedShadow
    var sourceTime: Double
    var visible: Bool
}

struct StudioResolvedFrame: Codable, Equatable, Sendable {
    var sceneIndex: Int
    var layout: StudioLayout
    var screen: StudioResolvedScreen?
    var camera: StudioResolvedCamera?
}

// MARK: - Layout Resolver

enum StudioLayoutResolver {
    static func resolve(
        project: StudioProject,
        time: Double,
        canvasWidth: Double,
        canvasHeight: Double
    ) -> StudioResolvedFrame {
        let normalizedScenes = normalizeScenes(project.scenes)
        let selected = activeScene(in: normalizedScenes, time: time)
        let scene = selected.scene
        let hasCamera = project.sources.camera != nil
        let layout = hasCamera ? scene.layout : .screen

        let m = min(canvasWidth, canvasHeight)
        let padding = StudioCanvasMath.clamped(project.canvas.padding, 0, 0.4) * m
        let content = StudioRect(
            x: padding,
            y: padding,
            width: canvasWidth - 2 * padding,
            height: canvasHeight - 2 * padding
        )
        let screenCrop = StudioCanvasMath.validCrop(project.screen.crop)
        let screenAspect = screenContentAspect(project: project, crop: screenCrop)
        let cardRadius = StudioCanvasMath.clamped(project.screen.cornerRadius, 0, 0.2) * m

        switch layout {
        case .screen:
            let rect = fit(aspect: screenAspect, in: content)
            return StudioResolvedFrame(
                sceneIndex: selected.index,
                layout: .screen,
                screen: resolvedScreen(project: project, rect: rect, source: screenCrop ?? unitRect(), radius: cardRadius, m: m),
                camera: nil
            )
        case .bubble:
            let screenRect = fit(aspect: screenAspect, in: content)
            let cameraRect = bubbleCameraRect(project: project, scene: scene, canvasWidth: canvasWidth, canvasHeight: canvasHeight, m: m)
            return StudioResolvedFrame(
                sceneIndex: selected.index,
                layout: .bubble,
                screen: resolvedScreen(project: project, rect: screenRect, source: screenCrop ?? unitRect(), radius: cardRadius, m: m),
                camera: resolvedCamera(project: project, scene: scene, layout: .bubble, rect: cameraRect, time: time, m: m, cardRadius: cardRadius)
            )
        case .sideBySide:
            let rects = sideBySideRects(project: project, scene: scene, content: content, canvasWidth: canvasWidth, canvasHeight: canvasHeight, m: m)
            return StudioResolvedFrame(
                sceneIndex: selected.index,
                layout: .sideBySide,
                screen: resolvedScreen(project: project, rect: rects.screen, source: screenCrop ?? unitRect(), radius: cardRadius, m: m),
                camera: resolvedCamera(project: project, scene: scene, layout: .sideBySide, rect: rects.camera, time: time, m: m, cardRadius: cardRadius)
            )
        case .camera:
            return StudioResolvedFrame(
                sceneIndex: selected.index,
                layout: .camera,
                screen: nil,
                camera: resolvedCamera(project: project, scene: scene, layout: .camera, rect: content, time: time, m: m, cardRadius: cardRadius)
            )
        }
    }

    /// The scene list of spec section 6.1: negative starts raised to 0, sorted by start (stable),
    /// the last scene kept among those sharing a start, and the first scene starting at 0.
    static func normalizeScenes(_ scenes: [StudioScene]) -> [StudioScene] {
        let source = scenes.isEmpty ? [StudioScene()] : scenes
        var adjusted: [(original: Int, scene: StudioScene)] = source.enumerated().map { index, scene in
            var copy = scene
            copy.start = max(0, copy.start)
            return (index, copy)
        }
        adjusted.sort {
            if $0.scene.start == $1.scene.start { return $0.original < $1.original }
            return $0.scene.start < $1.scene.start
        }

        var deduped: [StudioScene] = []
        for item in adjusted {
            if let last = deduped.last, last.start == item.scene.start {
                deduped[deduped.count - 1] = item.scene
            } else {
                deduped.append(item.scene)
            }
        }
        if !deduped.isEmpty {
            deduped[0].start = 0
        }
        return deduped
    }

    // MARK: - Private

    /// The last scene starting at or before `time`, with its position in the normalized list.
    private static func activeScene(in scenes: [StudioScene], time: Double) -> (index: Int, scene: StudioScene) {
        guard let first = scenes.first else { return (0, StudioScene()) }
        var active: (index: Int, scene: StudioScene) = (0, first)
        for (position, scene) in scenes.enumerated() where scene.start <= time {
            active = (position, scene)
        }
        return active
    }

    private static func screenContentAspect(project: StudioProject, crop: StudioRect?) -> Double {
        let width = Double(project.sources.screen.width) * (crop?.width ?? 1)
        let height = Double(project.sources.screen.height) * (crop?.height ?? 1)
        return width / height
    }

    private static func cameraContentAspect(project: StudioProject, crop: StudioRect?) -> Double {
        guard let camera = project.sources.camera else { return 1 }
        let width = Double(camera.width) * (crop?.width ?? 1)
        let height = Double(camera.height) * (crop?.height ?? 1)
        return width / height
    }

    private static func fit(aspect: Double, in rect: StudioRect) -> StudioRect {
        if rect.width / rect.height > aspect {
            let height = rect.height
            let width = height * aspect
            return StudioRect(x: rect.x + (rect.width - width) / 2, y: rect.y, width: width, height: height)
        } else {
            let width = rect.width
            let height = width / aspect
            return StudioRect(x: rect.x, y: rect.y + (rect.height - height) / 2, width: width, height: height)
        }
    }

    private static func bubbleCameraRect(project: StudioProject, scene: StudioScene, canvasWidth: Double, canvasHeight: Double, m: Double) -> StudioRect {
        let cameraCrop = StudioCanvasMath.validCrop(project.camera.crop)
        let cameraAspect = cameraContentAspect(project: project, crop: cameraCrop)
        let diameter = StudioCanvasMath.clamped(scene.bubble.size, 0.08, 0.6) * m
        var width: Double
        var height: Double
        if project.camera.shape == .circle || project.camera.shape == .squircle {
            width = diameter
            height = diameter
        } else {
            height = diameter
            width = diameter * StudioCanvasMath.clamped(cameraAspect, 0.5, 2)
        }
        if width > 0.9 * canvasWidth {
            let scale = 0.9 * canvasWidth / width
            width *= scale
            height *= scale
        }

        let gap = 0.03 * m
        var x: Double
        var y: Double
        switch scene.bubble.anchor {
        case .topLeft, .bottomLeft:
            x = gap
        case .topRight, .bottomRight:
            x = canvasWidth - gap - width
        }
        switch scene.bubble.anchor {
        case .topLeft, .topRight:
            y = gap
        case .bottomLeft, .bottomRight:
            y = canvasHeight - gap - height
        }
        x += scene.bubble.offsetX * canvasWidth
        y += scene.bubble.offsetY * canvasHeight
        x = StudioCanvasMath.clamped(x, 0, canvasWidth - width)
        y = StudioCanvasMath.clamped(y, 0, canvasHeight - height)
        return StudioRect(x: x, y: y, width: width, height: height)
    }

    private static func sideBySideRects(project: StudioProject, scene: StudioScene, content: StudioRect, canvasWidth: Double, canvasHeight: Double, m: Double) -> (screen: StudioRect, camera: StudioRect) {
        let gap = 0.02 * m
        let fraction = StudioCanvasMath.clamped(scene.split.cameraFraction, 0.15, 0.6)
        let screenAspect = screenContentAspect(project: project, crop: StudioCanvasMath.validCrop(project.screen.crop))

        if canvasWidth >= canvasHeight {
            let cameraWidth = fraction * (content.width - gap)
            let screen = fit(aspect: screenAspect, in: StudioRect(x: 0, y: 0, width: content.width - gap - cameraWidth, height: content.height))
            let x0 = content.x + (content.width - (screen.width + gap + cameraWidth)) / 2
            let y0 = content.y + (content.height - screen.height) / 2
            if scene.split.cameraSide == .trailing {
                return (
                    StudioRect(x: x0, y: y0, width: screen.width, height: screen.height),
                    StudioRect(x: x0 + screen.width + gap, y: y0, width: cameraWidth, height: screen.height)
                )
            }
            return (
                StudioRect(x: x0 + cameraWidth + gap, y: y0, width: screen.width, height: screen.height),
                StudioRect(x: x0, y: y0, width: cameraWidth, height: screen.height)
            )
        }

        let cameraHeight = fraction * (content.height - gap)
        let screen = fit(aspect: screenAspect, in: StudioRect(x: 0, y: 0, width: content.width, height: content.height - gap - cameraHeight))
        let x0 = content.x + (content.width - screen.width) / 2
        let y0 = content.y + (content.height - (screen.height + gap + cameraHeight)) / 2
        if scene.split.cameraSide == .trailing {
            return (
                StudioRect(x: x0, y: y0, width: screen.width, height: screen.height),
                StudioRect(x: x0, y: y0 + screen.height + gap, width: screen.width, height: cameraHeight)
            )
        }
        return (
            StudioRect(x: x0, y: y0 + cameraHeight + gap, width: screen.width, height: screen.height),
            StudioRect(x: x0, y: y0, width: screen.width, height: cameraHeight)
        )
    }

    private static func resolvedScreen(project: StudioProject, rect: StudioRect, source: StudioRect, radius: Double, m: Double) -> StudioResolvedScreen {
        StudioResolvedScreen(
            rect: rect,
            source: source,
            cornerRadius: min(radius, min(rect.width, rect.height) / 2),
            shadow: shadow(intensity: project.screen.shadow, m: m)
        )
    }

    private static func resolvedCamera(project: StudioProject, scene: StudioScene, layout: StudioLayout, rect: StudioRect, time: Double, m: Double, cardRadius: Double) -> StudioResolvedCamera? {
        guard let camera = project.sources.camera else { return nil }
        let crop = StudioCanvasMath.validCrop(project.camera.crop) ?? unitRect()
        let source = cameraSourceRect(project: project, crop: crop, destination: rect)
        let sourceTime = StudioCanvasMath.clamped(time - camera.startOffset, 0, camera.duration)
        let visible = (time - camera.startOffset) >= 0 && (time - camera.startOffset) <= camera.duration

        let shape: StudioCameraShape
        let cornerRadius: Double
        if layout == .bubble {
            shape = project.camera.shape
            switch project.camera.shape {
            case .circle, .squircle:
                cornerRadius = min(rect.width, rect.height) / 2
            case .roundedRectangle:
                cornerRadius = StudioCanvasMath.clamped(project.camera.cornerRadius, 0, 0.5) * min(rect.width, rect.height)
            case .rectangle:
                cornerRadius = 0
            }
        } else {
            cornerRadius = min(cardRadius, min(rect.width, rect.height) / 2)
            shape = cornerRadius > 0 ? .roundedRectangle : .rectangle
        }

        return StudioResolvedCamera(
            rect: rect,
            source: source,
            shape: shape,
            cornerRadius: cornerRadius,
            mirror: project.camera.mirror,
            borderWidth: StudioCanvasMath.clamped(project.camera.borderWidth, 0, 0.02) * m,
            shadow: shadow(intensity: project.camera.shadow, m: m),
            sourceTime: sourceTime,
            visible: visible
        )
    }

    private static func cameraSourceRect(project: StudioProject, crop: StudioRect, destination: StudioRect) -> StudioRect {
        let aspect = cameraContentAspect(project: project, crop: crop)
        let destinationAspect = destination.width / destination.height
        if aspect > destinationAspect {
            let scale = destinationAspect / aspect
            return StudioRect(x: crop.x + crop.width * (1 - scale) / 2, y: crop.y, width: crop.width * scale, height: crop.height)
        }
        let scale = aspect / destinationAspect
        return StudioRect(x: crop.x, y: crop.y + crop.height * (1 - scale) / 2, width: crop.width, height: crop.height * scale)
    }

    private static func shadow(intensity: Double, m: Double) -> StudioResolvedShadow {
        let value = StudioCanvasMath.clamped(intensity, 0, 1)
        return StudioResolvedShadow(
            blur: value * 0.04 * m,
            offsetY: value * 0.012 * m,
            opacity: value * 0.5
        )
    }

    private static func unitRect() -> StudioRect {
        StudioRect(x: 0, y: 0, width: 1, height: 1)
    }
}
