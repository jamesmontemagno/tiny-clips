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
    /// 1, except while a scene is being entered with a morph and only one of the two scenes has
    /// a screen (section 6.9 of the project format).
    var opacity: Double = 1
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
    /// 1, except while a scene is being entered with a morph and only one of the two scenes has
    /// a camera (section 6.9 of the project format).
    var opacity: Double = 1
}

struct StudioResolvedFrame: Codable, Equatable, Sendable {
    var sceneIndex: Int
    var layout: StudioLayout
    var screen: StudioResolvedScreen?
    var camera: StudioResolvedCamera?
}

// MARK: - Layout Resolver

enum StudioLayoutResolver {
    private static let suggestionScale = 2.0
    private static let suggestionLead = 0.6
    private static let suggestionHold = 1.5
    private static let suggestionJoin = 4.0
    private static let suggestionInset = 0.15
    private static let suggestionShortest = 0.3
    private static let suggestionEase = 0.5

    static func resolve(
        project: StudioProject,
        time: Double,
        canvasWidth: Double,
        canvasHeight: Double,
        events: StudioEvents? = nil
    ) -> StudioResolvedFrame {
        let scenes = normalizeScenes(project.scenes)
        let selected = activeScene(in: scenes, time: time)
        var frame = sceneAtRest(
            project: project,
            scene: selected.scene,
            index: selected.index,
            time: time,
            canvasWidth: canvasWidth,
            canvasHeight: canvasHeight,
            events: events
        )

        // A scene entered with a morph: for a moment the layers are still on their way from where
        // the scene before had them (section 6.9). A scene with a length to its move is never the
        // first, and the active scene has started, so only the end needs asking.
        let length = transitionLength(scenes, index: selected.index)
        let start = selected.scene.start
        if length > 0, time < start + length {
            let k = ease((time - start) / length)
            let origin = sceneAtRest(
                project: project,
                scene: scenes[selected.index - 1],
                index: selected.index - 1,
                time: time,
                canvasWidth: canvasWidth,
                canvasHeight: canvasHeight,
                events: events
            )
            frame.screen = moved(from: origin.screen, to: frame.screen, k: k)
            frame.camera = moved(from: origin.camera, to: frame.camera, k: k, project: project)
        }
        return frame
    }

    /// How long the layers take to move into scene `index` of a normalized scene list: 0 for a
    /// cut and for the first scene, and never longer than the scene itself, so a move always
    /// starts from a scene at rest.
    static func transitionLength(_ scenes: [StudioScene], index: Int) -> Double {
        guard index >= 1, index < scenes.count, scenes[index].transition.kind == .morph else { return 0 }
        let length = StudioCanvasMath.clamped(scenes[index].transition.duration, 0, 2)
        guard index + 1 < scenes.count else { return length }
        return min(length, scenes[index + 1].start - scenes[index].start)
    }

    /// Sections 6.2 to 6.5: the layout of one scene at rest.
    private static func sceneAtRest(
        project: StudioProject,
        scene: StudioScene,
        index: Int,
        time: Double,
        canvasWidth: Double,
        canvasHeight: Double,
        events: StudioEvents?
    ) -> StudioResolvedFrame {
        let hasCamera = project.sources.camera != nil
        let layout = hasCamera ? scene.layout : .screen
        let screenSource = zoomWindow(project: project, events: events, time: time)

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
                sceneIndex: index,
                layout: .screen,
                screen: resolvedScreen(project: project, rect: rect, source: screenSource, radius: cardRadius, m: m),
                camera: nil
            )
        case .bubble:
            let screenRect = fit(aspect: screenAspect, in: content)
            let cameraRect = bubbleCameraRect(project: project, scene: scene, canvasWidth: canvasWidth, canvasHeight: canvasHeight, m: m)
            return StudioResolvedFrame(
                sceneIndex: index,
                layout: .bubble,
                screen: resolvedScreen(project: project, rect: screenRect, source: screenSource, radius: cardRadius, m: m),
                camera: resolvedCamera(project: project, scene: scene, layout: .bubble, rect: cameraRect, time: time, m: m, cardRadius: cardRadius)
            )
        case .sideBySide:
            let rects = sideBySideRects(project: project, scene: scene, content: content, canvasWidth: canvasWidth, canvasHeight: canvasHeight, m: m)
            return StudioResolvedFrame(
                sceneIndex: index,
                layout: .sideBySide,
                screen: resolvedScreen(project: project, rect: rects.screen, source: screenSource, radius: cardRadius, m: m),
                camera: resolvedCamera(project: project, scene: scene, layout: .sideBySide, rect: rects.camera, time: time, m: m, cardRadius: cardRadius)
            )
        case .camera:
            return StudioResolvedFrame(
                sceneIndex: index,
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

    static func normalizeZooms(_ zooms: [StudioZoom]) -> [StudioZoom] {
        var adjusted: [(original: Int, zoom: StudioZoom)] = []
        for (index, zoom) in zooms.enumerated() {
            var copy = zoom
            copy.start = max(0, copy.start)
            guard copy.end > copy.start else { continue }
            adjusted.append((index, copy))
        }
        adjusted.sort {
            if $0.zoom.start == $1.zoom.start { return $0.original < $1.original }
            return $0.zoom.start < $1.zoom.start
        }

        var deduped: [StudioZoom] = []
        for item in adjusted {
            if let last = deduped.last, last.start == item.zoom.start {
                deduped[deduped.count - 1] = item.zoom
            } else {
                deduped.append(item.zoom)
            }
        }
        guard deduped.count > 1 else { return deduped }
        for index in 0..<(deduped.count - 1) where deduped[index + 1].start < deduped[index].end {
            deduped[index].end = deduped[index + 1].start
        }
        return deduped
    }

    static func suggestZooms(project: StudioProject, events: StudioEvents?) -> [StudioZoom] {
        let base = screenBaseRect(project: project)
        let duration = max(0, project.sources.screen.duration)
        let orderedClicks = (events?.clicks ?? []).enumerated().sorted { left, right in
            if left.element.t == right.element.t { return left.offset < right.offset }
            return left.element.t < right.element.t
        }
        var clicks: [StudioClickEvent] = []
        for item in orderedClicks {
            let click = item.element
            let insideTime = click.t >= 0 && click.t <= duration
            let insideX = click.x >= base.x && click.x <= base.x + base.width
            let insideY = click.y >= base.y && click.y <= base.y + base.height
            if insideTime && insideX && insideY {
                clicks.append(click)
            }
        }

        var groups: [SuggestionGroup] = []
        for click in clicks {
            if let last = groups.indices.last,
               click.t - groups[last].last <= suggestionJoin {
                let window = heldWindow(base: base, scale: suggestionScale, focusX: groups[last].x, focusY: groups[last].y)
                let inner = StudioRect(
                    x: window.x + suggestionInset * window.width,
                    y: window.y + suggestionInset * window.height,
                    width: (1 - 2 * suggestionInset) * window.width,
                    height: (1 - 2 * suggestionInset) * window.height
                )
                if click.x >= inner.x,
                   click.x <= inner.x + inner.width,
                   click.y >= inner.y,
                   click.y <= inner.y + inner.height {
                    groups[last].last = click.t
                } else {
                    let end = max(click.t - suggestionLead, (groups[last].last + click.t) / 2)
                    groups[last].end = end
                    groups.append(SuggestionGroup(start: end, x: click.x, y: click.y, last: click.t))
                }
            } else {
                groups.append(SuggestionGroup(start: max(0, click.t - suggestionLead), x: click.x, y: click.y, last: click.t))
            }
        }

        let suggestions = groups.compactMap { group -> StudioZoom? in
            let end = group.end ?? min(duration, group.last + suggestionHold)
            guard end - group.start >= suggestionShortest else { return nil }
            return StudioZoom(
                start: group.start,
                end: end,
                scale: suggestionScale,
                focus: StudioZoomFocus(mode: .point, x: group.x, y: group.y),
                easeIn: suggestionEase,
                easeOut: suggestionEase,
                origin: .auto
            )
        }
        let manual = normalizeZooms(project.zooms.filter { $0.origin != .auto })
        return suggestions.filter { suggestion in
            !manual.contains { suggestion.start < $0.end && $0.start < suggestion.end }
        }
    }

    // MARK: - Private

    /// A screen on its way from the scene before to this one. A screen only one of the two
    /// scenes has stays where that scene has it and fades.
    private static func moved(from origin: StudioResolvedScreen?, to target: StudioResolvedScreen?, k: Double) -> StudioResolvedScreen? {
        guard let origin else {
            guard var appearing = target else { return nil }
            appearing.opacity = k
            return appearing
        }
        guard let target else {
            var leaving = origin
            leaving.opacity = 1 - k
            return leaving
        }
        var moving = target
        moving.rect = lerp(origin.rect, target.rect, k)
        moving.cornerRadius = origin.cornerRadius + (target.cornerRadius - origin.cornerRadius) * k
        return moving
    }

    private static func moved(from origin: StudioResolvedCamera?, to target: StudioResolvedCamera?, k: Double, project: StudioProject) -> StudioResolvedCamera? {
        guard let origin else {
            guard var appearing = target else { return nil }
            appearing.opacity = k
            return appearing
        }
        guard let target else {
            var leaving = origin
            leaving.opacity = 1 - k
            return leaving
        }
        // The source is worked out again for the card the camera has now, so its picture is
        // cropped to the card all the way and never stretched. Between two shapes it moves as a
        // rounded rectangle.
        var moving = target
        moving.rect = lerp(origin.rect, target.rect, k)
        moving.source = cameraSourceRect(
            project: project,
            crop: StudioCanvasMath.validCrop(project.camera.crop) ?? unitRect(),
            destination: moving.rect
        )
        let sameShape = origin.shape == target.shape
        let fromRadius = sameShape ? origin.cornerRadius : radiusBetweenShapes(origin)
        let toRadius = sameShape ? target.cornerRadius : radiusBetweenShapes(target)
        moving.shape = sameShape ? target.shape : .roundedRectangle
        moving.cornerRadius = fromRadius + (toRadius - fromRadius) * k
        return moving
    }

    /// The corner radius a camera has as one end of a move between two shapes. A squircle is
    /// drawn without one: the rounded rectangle that reaches as far into the corners of its box
    /// has a radius of 0.22 of its short side.
    private static func radiusBetweenShapes(_ camera: StudioResolvedCamera) -> Double {
        camera.shape == .squircle ? 0.22 * min(camera.rect.width, camera.rect.height) : camera.cornerRadius
    }

    private struct SuggestionGroup {
        var start: Double
        var x: Double
        var y: Double
        var last: Double
        var end: Double?
    }

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

    /// The part of the screen a zoom works inside: the valid crop, or the whole frame (section 6.8).
    static func screenBaseRect(project: StudioProject) -> StudioRect {
        StudioCanvasMath.validCrop(project.screen.crop) ?? unitRect()
    }

    private static func zoomWindow(project: StudioProject, events: StudioEvents?, time: Double) -> StudioRect {
        let base = screenBaseRect(project: project)
        let zooms = normalizeZooms(project.zooms)
        guard let active = zooms.firstIndex(where: { time >= $0.start && time < $0.end }) else {
            return base
        }

        let zoom = zooms[active]
        let samples = events?.preparedCursorSamples
        let chainedToPrevious = active > 0 && zooms[active - 1].end == zoom.start
        let nextIsChained = active + 1 < zooms.count && zooms[active + 1].start == zoom.end
        var easeIn = StudioCanvasMath.clamped(zoom.easeIn, 0, 3)
        var easeOut = nextIsChained ? 0 : StudioCanvasMath.clamped(zoom.easeOut, 0, 3)
        let duration = zoom.end - zoom.start
        if easeIn + easeOut > duration {
            let factor = duration / (easeIn + easeOut)
            easeIn *= factor
            easeOut *= factor
        }

        let held = heldWindow(base: base, zoom: zoom, samples: samples, time: time)
        if time < zoom.start + easeIn {
            let origin: StudioRect
            if chainedToPrevious {
                origin = heldWindow(base: base, zoom: zooms[active - 1], samples: samples, time: time)
            } else {
                origin = base
            }
            return lerp(origin, held, ease((time - zoom.start) / easeIn))
        }
        if time > zoom.end - easeOut {
            return lerp(base, held, ease((zoom.end - time) / easeOut))
        }
        return held
    }

    private static func heldWindow(base: StudioRect, zoom: StudioZoom, samples: StudioPreparedCursorSamples?, time: Double) -> StudioRect {
        let focus = zoomFocus(zoom, samples: samples, time: time)
        return heldWindow(base: base, scale: zoom.scale, focusX: focus.x, focusY: focus.y)
    }

    /// The part of the screen a zoom shows while it is held (section 6.8): `base` made smaller by
    /// the scale, centered on the focus, and pushed back inside where it would stick out.
    static func heldWindow(base: StudioRect, scale: Double, focusX: Double, focusY: Double) -> StudioRect {
        let scale = StudioCanvasMath.clamped(scale, 1, 5)
        let width = base.width / scale
        let height = base.height / scale
        let x = max(base.x, min(focusX - width / 2, base.x + base.width - width))
        let y = max(base.y, min(focusY - height / 2, base.y + base.height - height))
        return StudioRect(x: x, y: y, width: width, height: height)
    }

    private static func zoomFocus(_ zoom: StudioZoom, samples: StudioPreparedCursorSamples?, time: Double) -> (x: Double, y: Double) {
        if zoom.focus.mode == .cursor, let focus = samples?.focus(at: time) {
            return focus
        }
        return (
            StudioCanvasMath.clamped(zoom.focus.x, 0, 1),
            StudioCanvasMath.clamped(zoom.focus.y, 0, 1)
        )
    }

    private static func lerp(_ a: StudioRect, _ b: StudioRect, _ k: Double) -> StudioRect {
        StudioRect(
            x: a.x + (b.x - a.x) * k,
            y: a.y + (b.y - a.y) * k,
            width: a.width + (b.width - a.width) * k,
            height: a.height + (b.height - a.height) * k
        )
    }

    private static func ease(_ value: Double) -> Double {
        value * value * (3 - 2 * value)
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
