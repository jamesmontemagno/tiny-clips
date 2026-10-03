import Foundation

// MARK: - Enums

enum StudioCanvasAspect: String, StudioStringEnum {
    case auto
    case square
    case landscape4x3
    case landscape16x9
    case portrait3x4
    case portrait9x16

    static let defaultValue: StudioCanvasAspect = .auto
}

enum StudioBackgroundStyle: String, StudioStringEnum {
    case none
    case solid
    case gradient
    case image

    static let defaultValue: StudioBackgroundStyle = .gradient
}

enum StudioCameraShape: String, StudioStringEnum {
    case circle
    case roundedRectangle
    case squircle
    case rectangle

    static let defaultValue: StudioCameraShape = .circle
}

enum StudioCameraCutout: String, StudioStringEnum {
    case none
    case blur
    case remove

    static let defaultValue: StudioCameraCutout = .none
}

enum StudioLayout: String, StudioStringEnum {
    case screen
    case bubble
    case sideBySide
    case camera

    static let defaultValue: StudioLayout = .bubble
}

enum StudioAnchor: String, StudioStringEnum {
    case topLeft
    case topRight
    case bottomLeft
    case bottomRight

    static let defaultValue: StudioAnchor = .bottomRight
}

enum StudioCameraSide: String, StudioStringEnum {
    case leading
    case trailing

    static let defaultValue: StudioCameraSide = .trailing
}

enum StudioTransitionKind: String, StudioStringEnum {
    case cut
    case morph

    static let defaultValue: StudioTransitionKind = .cut
}

enum StudioZoomFocusMode: String, StudioStringEnum {
    case point
    case cursor

    static let defaultValue: StudioZoomFocusMode = .point
}

enum StudioZoomOrigin: String, StudioStringEnum {
    case manual
    case auto

    static let defaultValue: StudioZoomOrigin = .manual
}

// MARK: - Root

struct StudioProject: Codable, Equatable, Sendable {
    var schemaVersion: Int
    var id: String
    var name: String
    var createdAt: Date
    var modifiedAt: Date
    var lastOpenedAt: Date
    var app: StudioAppInfo
    var keepSources: Bool
    var sources: StudioSources
    var canvas: StudioCanvas
    var screen: StudioScreenStyle
    var camera: StudioCameraStyle
    var scenes: [StudioScene]
    var zooms: [StudioZoom]
    var edits: StudioEdits
    var audio: StudioAudio
    var overlays: StudioOverlays
    var exports: [StudioExport]
    var extra: [String: StudioJSONValue]

    init(
        schemaVersion: Int = 1,
        id: String,
        name: String = "",
        createdAt: Date = Date(timeIntervalSince1970: 0),
        modifiedAt: Date = Date(timeIntervalSince1970: 0),
        lastOpenedAt: Date = Date(timeIntervalSince1970: 0),
        app: StudioAppInfo = StudioAppInfo(),
        keepSources: Bool = false,
        sources: StudioSources,
        canvas: StudioCanvas = StudioCanvas(),
        screen: StudioScreenStyle = StudioScreenStyle(),
        camera: StudioCameraStyle = StudioCameraStyle(),
        scenes: [StudioScene] = [StudioScene()],
        zooms: [StudioZoom] = [],
        edits: StudioEdits = StudioEdits(),
        audio: StudioAudio = StudioAudio(),
        overlays: StudioOverlays = StudioOverlays(),
        exports: [StudioExport] = [],
        extra: [String: StudioJSONValue] = [:]
    ) {
        self.schemaVersion = schemaVersion
        self.id = id
        self.name = name
        self.createdAt = createdAt
        self.modifiedAt = modifiedAt
        self.lastOpenedAt = lastOpenedAt
        self.app = app
        self.keepSources = keepSources
        self.sources = sources
        self.canvas = canvas
        self.screen = screen
        self.camera = camera
        self.scenes = scenes
        self.zooms = zooms
        self.edits = edits
        self.audio = audio
        self.overlays = overlays
        self.exports = exports
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        schemaVersion = try container.decodeInt("schemaVersion", default: 1)
        guard schemaVersion <= StudioJSON.supportedSchemaVersion else {
            throw StudioProjectError.unsupportedVersion(schemaVersion)
        }
        guard let decodedID = try container.decodeIfPresent(String.self, forKey: StudioJSONKey("id")) else {
            throw StudioProjectError.invalidProject("Missing required property id")
        }
        id = decodedID
        name = try container.decodeString("name", default: "")
        createdAt = try container.decodeDate("createdAt")
        modifiedAt = try container.decodeDate("modifiedAt")
        lastOpenedAt = try container.decodeDate("lastOpenedAt")
        app = try container.decodeIfPresent(StudioAppInfo.self, forKey: StudioJSONKey("app")) ?? StudioAppInfo()
        keepSources = try container.decodeBool("keepSources", default: false)
        guard let decodedSources = try container.decodeIfPresent(StudioSources.self, forKey: StudioJSONKey("sources")) else {
            throw StudioProjectError.invalidProject("Missing required property sources.screen")
        }
        sources = decodedSources
        canvas = try container.decodeIfPresent(StudioCanvas.self, forKey: StudioJSONKey("canvas")) ?? StudioCanvas()
        screen = try container.decodeIfPresent(StudioScreenStyle.self, forKey: StudioJSONKey("screen")) ?? StudioScreenStyle()
        camera = try container.decodeIfPresent(StudioCameraStyle.self, forKey: StudioJSONKey("camera")) ?? StudioCameraStyle()
        scenes = try container.decodeCompactArray("scenes", default: [StudioScene()])
        if scenes.isEmpty { scenes = [StudioScene()] }
        zooms = try container.decodeCompactArray("zooms", default: [])
        edits = try container.decodeIfPresent(StudioEdits.self, forKey: StudioJSONKey("edits")) ?? StudioEdits()
        audio = try container.decodeIfPresent(StudioAudio.self, forKey: StudioJSONKey("audio")) ?? StudioAudio()
        overlays = try container.decodeIfPresent(StudioOverlays.self, forKey: StudioJSONKey("overlays")) ?? StudioOverlays()
        exports = try container.decodeCompactArray("exports", default: [])
        extra = try StudioJSON.decodeExtra(from: container, excluding: Self.knownKeys)
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(schemaVersion, forKey: StudioJSONKey("schemaVersion"))
        try container.encode(id, forKey: StudioJSONKey("id"))
        try container.encode(name, forKey: StudioJSONKey("name"))
        try container.encodeDate(createdAt, forKey: "createdAt")
        try container.encodeDate(modifiedAt, forKey: "modifiedAt")
        try container.encodeDate(lastOpenedAt, forKey: "lastOpenedAt")
        try container.encode(app, forKey: StudioJSONKey("app"))
        try container.encode(keepSources, forKey: StudioJSONKey("keepSources"))
        try container.encode(sources, forKey: StudioJSONKey("sources"))
        try container.encode(canvas, forKey: StudioJSONKey("canvas"))
        try container.encode(screen, forKey: StudioJSONKey("screen"))
        try container.encode(camera, forKey: StudioJSONKey("camera"))
        try container.encode(scenes, forKey: StudioJSONKey("scenes"))
        try container.encode(zooms, forKey: StudioJSONKey("zooms"))
        try container.encode(edits, forKey: StudioJSONKey("edits"))
        try container.encode(audio, forKey: StudioJSONKey("audio"))
        try container.encode(overlays, forKey: StudioJSONKey("overlays"))
        try container.encode(exports, forKey: StudioJSONKey("exports"))
    }

    private static let knownKeys: Set<String> = [
        "schemaVersion", "id", "name", "createdAt", "modifiedAt", "lastOpenedAt", "app",
        "keepSources", "sources", "canvas", "screen", "camera", "scenes", "zooms",
        "edits", "audio", "overlays", "exports"
    ]
}

// MARK: - Sources

struct StudioAppInfo: Codable, Equatable, Sendable {
    var platform: String
    var version: String
    var extra: [String: StudioJSONValue]

    init(platform: String = "macos", version: String = "", extra: [String: StudioJSONValue] = [:]) {
        self.platform = platform
        self.version = version
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        platform = try container.decodeString("platform", default: "macos")
        version = try container.decodeString("version", default: "")
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["platform", "version"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(platform, forKey: StudioJSONKey("platform"))
        try container.encode(version, forKey: StudioJSONKey("version"))
    }
}

struct StudioSources: Codable, Equatable, Sendable {
    var screen: StudioScreenSource
    var camera: StudioCameraSource?
    var events: String?
    var extra: [String: StudioJSONValue]

    init(
        screen: StudioScreenSource = StudioScreenSource(width: 0, height: 0, duration: 0),
        camera: StudioCameraSource? = nil,
        events: String? = nil,
        extra: [String: StudioJSONValue] = [:]
    ) {
        self.screen = screen
        self.camera = camera
        self.events = events
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        guard let decodedScreen = try container.decodeIfPresent(StudioScreenSource.self, forKey: StudioJSONKey("screen")) else {
            throw StudioProjectError.invalidProject("Missing required property sources.screen")
        }
        screen = decodedScreen
        camera = try container.decodeIfPresent(StudioCameraSource.self, forKey: StudioJSONKey("camera"))
        events = try container.decodeIfPresent(String.self, forKey: StudioJSONKey("events"))
        if let events {
            self.events = try StudioJSON.requirePlainFileName(events, "sources.events")
        }
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["screen", "camera", "events"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(screen, forKey: StudioJSONKey("screen"))
        if let camera = camera {
            try container.encode(camera, forKey: StudioJSONKey("camera"))
        } else {
            try container.encodeNil(forKey: "camera")
        }
        if let events = events {
            try container.encode(events, forKey: StudioJSONKey("events"))
        } else {
            try container.encodeNil(forKey: "events")
        }
    }
}

struct StudioScreenSource: Codable, Equatable, Sendable {
    var file: String
    var width: Int
    var height: Int
    var frameRate: Double
    var duration: Double
    var external: Bool
    var extra: [String: StudioJSONValue]

    init(
        file: String = "screen.mp4",
        width: Int,
        height: Int,
        frameRate: Double = 30,
        duration: Double,
        external: Bool = false,
        extra: [String: StudioJSONValue] = [:]
    ) {
        self.file = file
        self.width = width
        self.height = height
        self.frameRate = frameRate
        self.duration = duration
        self.external = external
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        file = try container.decodeString("file", default: "screen.mp4")
        width = try StudioJSON.requirePositive(container.decodeRequiredInt("width"), "sources.screen.width")
        height = try StudioJSON.requirePositive(container.decodeRequiredInt("height"), "sources.screen.height")
        frameRate = try container.decodeDouble("frameRate", default: 30)
        duration = try StudioJSON.requireNonNegative(container.decodeRequiredDouble("duration"), "sources.screen.duration")
        external = try container.decodeBool("external", default: false)
        if !external {
            file = try StudioJSON.requirePlainFileName(file, "sources.screen.file")
        }
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["file", "width", "height", "frameRate", "duration", "external"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(file, forKey: StudioJSONKey("file"))
        try container.encode(width, forKey: StudioJSONKey("width"))
        try container.encode(height, forKey: StudioJSONKey("height"))
        try container.encode(frameRate, forKey: StudioJSONKey("frameRate"))
        try container.encode(duration, forKey: StudioJSONKey("duration"))
        try container.encode(external, forKey: StudioJSONKey("external"))
    }
}

struct StudioCameraSource: Codable, Equatable, Sendable {
    var file: String
    var width: Int
    var height: Int
    var duration: Double
    var startOffset: Double
    var extra: [String: StudioJSONValue]

    init(
        file: String = "camera.mp4",
        width: Int,
        height: Int,
        duration: Double,
        startOffset: Double = 0,
        extra: [String: StudioJSONValue] = [:]
    ) {
        self.file = file
        self.width = width
        self.height = height
        self.duration = duration
        self.startOffset = startOffset
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        file = try container.decodeString("file", default: "camera.mp4")
        file = try StudioJSON.requirePlainFileName(file, "sources.camera.file")
        width = try StudioJSON.requirePositive(container.decodeRequiredInt("width"), "sources.camera.width")
        height = try StudioJSON.requirePositive(container.decodeRequiredInt("height"), "sources.camera.height")
        duration = try StudioJSON.requireNonNegative(container.decodeRequiredDouble("duration"), "sources.camera.duration")
        startOffset = try container.decodeDouble("startOffset", default: 0)
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["file", "width", "height", "duration", "startOffset"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(file, forKey: StudioJSONKey("file"))
        try container.encode(width, forKey: StudioJSONKey("width"))
        try container.encode(height, forKey: StudioJSONKey("height"))
        try container.encode(duration, forKey: StudioJSONKey("duration"))
        try container.encode(startOffset, forKey: StudioJSONKey("startOffset"))
    }
}

// MARK: - Styling

struct StudioCanvas: Codable, Equatable, Sendable {
    var aspect: StudioCanvasAspect
    var padding: Double
    var background: StudioBackground
    var extra: [String: StudioJSONValue]

    init(
        aspect: StudioCanvasAspect = .auto,
        padding: Double = 0.06,
        background: StudioBackground = StudioBackground(),
        extra: [String: StudioJSONValue] = [:]
    ) {
        self.aspect = aspect
        self.padding = padding
        self.background = background
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        aspect = try container.decodeEnum("aspect", default: .auto)
        padding = try container.decodeDouble("padding", default: 0.06)
        background = try container.decodeIfPresent(StudioBackground.self, forKey: StudioJSONKey("background")) ?? StudioBackground()
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["aspect", "padding", "background"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(aspect, forKey: StudioJSONKey("aspect"))
        try container.encode(padding, forKey: StudioJSONKey("padding"))
        try container.encode(background, forKey: StudioJSONKey("background"))
    }
}

struct StudioBackground: Codable, Equatable, Sendable {
    var style: StudioBackgroundStyle
    var preset: String?
    var primary: String
    var secondary: String?
    var image: String?
    var extra: [String: StudioJSONValue]

    init(
        style: StudioBackgroundStyle = .gradient,
        preset: String? = "ocean",
        primary: String = "#2687E8",
        secondary: String? = "#2EE0BF",
        image: String? = nil,
        extra: [String: StudioJSONValue] = [:]
    ) {
        self.style = style
        self.preset = preset
        self.primary = primary
        self.secondary = secondary
        self.image = image
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        style = try container.decodeEnum("style", default: .gradient)
        preset = try container.decodeNullableString("preset", default: "ocean")
        primary = try container.decodeString("primary", default: "#2687E8")
        secondary = try container.decodeNullableString("secondary", default: "#2EE0BF")
        image = try container.decodeNullableString("image", default: nil)
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["style", "preset", "primary", "secondary", "image"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(style, forKey: StudioJSONKey("style"))
        if let preset = preset { try container.encode(preset, forKey: StudioJSONKey("preset")) } else { try container.encodeNil(forKey: "preset") }
        try container.encode(primary, forKey: StudioJSONKey("primary"))
        if let secondary = secondary { try container.encode(secondary, forKey: StudioJSONKey("secondary")) } else { try container.encodeNil(forKey: "secondary") }
        if let image = image { try container.encode(image, forKey: StudioJSONKey("image")) } else { try container.encodeNil(forKey: "image") }
    }
}

struct StudioScreenStyle: Codable, Equatable, Sendable {
    var cornerRadius: Double
    var shadow: Double
    var crop: StudioRect?
    var extra: [String: StudioJSONValue]

    init(cornerRadius: Double = 0.02, shadow: Double = 0.5, crop: StudioRect? = nil, extra: [String: StudioJSONValue] = [:]) {
        self.cornerRadius = cornerRadius
        self.shadow = shadow
        self.crop = crop
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        cornerRadius = try container.decodeDouble("cornerRadius", default: 0.02)
        shadow = try container.decodeDouble("shadow", default: 0.5)
        crop = try container.decodeIfPresent(StudioRect.self, forKey: StudioJSONKey("crop"))
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["cornerRadius", "shadow", "crop"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(cornerRadius, forKey: StudioJSONKey("cornerRadius"))
        try container.encode(shadow, forKey: StudioJSONKey("shadow"))
        if let crop = crop { try container.encode(crop, forKey: StudioJSONKey("crop")) } else { try container.encodeNil(forKey: "crop") }
    }
}

struct StudioCameraStyle: Codable, Equatable, Sendable {
    var shape: StudioCameraShape
    var cornerRadius: Double
    var mirror: Bool
    var borderWidth: Double
    var borderColor: String
    var shadow: Double
    var crop: StudioRect?
    var cutout: StudioCameraCutout
    var extra: [String: StudioJSONValue]

    init(
        shape: StudioCameraShape = .circle,
        cornerRadius: Double = 0.12,
        mirror: Bool = true,
        borderWidth: Double = 0,
        borderColor: String = "#FFFFFF",
        shadow: Double = 0.35,
        crop: StudioRect? = nil,
        cutout: StudioCameraCutout = .none,
        extra: [String: StudioJSONValue] = [:]
    ) {
        self.shape = shape
        self.cornerRadius = cornerRadius
        self.mirror = mirror
        self.borderWidth = borderWidth
        self.borderColor = borderColor
        self.shadow = shadow
        self.crop = crop
        self.cutout = cutout
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        shape = try container.decodeEnum("shape", default: .circle)
        cornerRadius = try container.decodeDouble("cornerRadius", default: 0.12)
        mirror = try container.decodeBool("mirror", default: true)
        borderWidth = try container.decodeDouble("borderWidth", default: 0)
        borderColor = try container.decodeString("borderColor", default: "#FFFFFF")
        shadow = try container.decodeDouble("shadow", default: 0.35)
        crop = try container.decodeIfPresent(StudioRect.self, forKey: StudioJSONKey("crop"))
        cutout = try container.decodeEnum("cutout", default: .none)
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["shape", "cornerRadius", "mirror", "borderWidth", "borderColor", "shadow", "crop", "cutout"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(shape, forKey: StudioJSONKey("shape"))
        try container.encode(cornerRadius, forKey: StudioJSONKey("cornerRadius"))
        try container.encode(mirror, forKey: StudioJSONKey("mirror"))
        try container.encode(borderWidth, forKey: StudioJSONKey("borderWidth"))
        try container.encode(borderColor, forKey: StudioJSONKey("borderColor"))
        try container.encode(shadow, forKey: StudioJSONKey("shadow"))
        if let crop = crop { try container.encode(crop, forKey: StudioJSONKey("crop")) } else { try container.encodeNil(forKey: "crop") }
        try container.encode(cutout, forKey: StudioJSONKey("cutout"))
    }
}

// MARK: - Timeline

struct StudioScene: Codable, Equatable, Sendable {
    var start: Double
    var layout: StudioLayout
    var bubble: StudioBubble
    var split: StudioSplit
    var transition: StudioTransition
    var extra: [String: StudioJSONValue]

    init(start: Double = 0, layout: StudioLayout = .bubble, bubble: StudioBubble = StudioBubble(), split: StudioSplit = StudioSplit(), transition: StudioTransition = StudioTransition(), extra: [String: StudioJSONValue] = [:]) {
        self.start = start
        self.layout = layout
        self.bubble = bubble
        self.split = split
        self.transition = transition
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        start = try container.decodeDouble("start", default: 0)
        layout = try container.decodeEnum("layout", default: .bubble)
        bubble = try container.decodeIfPresent(StudioBubble.self, forKey: StudioJSONKey("bubble")) ?? StudioBubble()
        split = try container.decodeIfPresent(StudioSplit.self, forKey: StudioJSONKey("split")) ?? StudioSplit()
        transition = try container.decodeIfPresent(StudioTransition.self, forKey: StudioJSONKey("transition")) ?? StudioTransition()
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["start", "layout", "bubble", "split", "transition"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(start, forKey: StudioJSONKey("start"))
        try container.encode(layout, forKey: StudioJSONKey("layout"))
        try container.encode(bubble, forKey: StudioJSONKey("bubble"))
        try container.encode(split, forKey: StudioJSONKey("split"))
        try container.encode(transition, forKey: StudioJSONKey("transition"))
    }
}

struct StudioBubble: Codable, Equatable, Sendable {
    var anchor: StudioAnchor
    var size: Double
    var offsetX: Double
    var offsetY: Double
    var extra: [String: StudioJSONValue]

    init(anchor: StudioAnchor = .bottomRight, size: Double = 0.24, offsetX: Double = 0, offsetY: Double = 0, extra: [String: StudioJSONValue] = [:]) {
        self.anchor = anchor
        self.size = size
        self.offsetX = offsetX
        self.offsetY = offsetY
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        anchor = try container.decodeEnum("anchor", default: .bottomRight)
        size = try container.decodeDouble("size", default: 0.24)
        offsetX = try container.decodeDouble("offsetX", default: 0)
        offsetY = try container.decodeDouble("offsetY", default: 0)
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["anchor", "size", "offsetX", "offsetY"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(anchor, forKey: StudioJSONKey("anchor"))
        try container.encode(size, forKey: StudioJSONKey("size"))
        try container.encode(offsetX, forKey: StudioJSONKey("offsetX"))
        try container.encode(offsetY, forKey: StudioJSONKey("offsetY"))
    }
}

struct StudioSplit: Codable, Equatable, Sendable {
    var cameraSide: StudioCameraSide
    var cameraFraction: Double
    var extra: [String: StudioJSONValue]

    init(cameraSide: StudioCameraSide = .trailing, cameraFraction: Double = 0.3, extra: [String: StudioJSONValue] = [:]) {
        self.cameraSide = cameraSide
        self.cameraFraction = cameraFraction
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        cameraSide = try container.decodeEnum("cameraSide", default: .trailing)
        cameraFraction = try container.decodeDouble("cameraFraction", default: 0.3)
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["cameraSide", "cameraFraction"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(cameraSide, forKey: StudioJSONKey("cameraSide"))
        try container.encode(cameraFraction, forKey: StudioJSONKey("cameraFraction"))
    }
}

struct StudioTransition: Codable, Equatable, Sendable {
    var kind: StudioTransitionKind
    var duration: Double
    var extra: [String: StudioJSONValue]

    init(kind: StudioTransitionKind = .cut, duration: Double = 0.35, extra: [String: StudioJSONValue] = [:]) {
        self.kind = kind
        self.duration = duration
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        kind = try container.decodeEnum("kind", default: .cut)
        duration = try container.decodeDouble("duration", default: 0.35)
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["kind", "duration"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(kind, forKey: StudioJSONKey("kind"))
        try container.encode(duration, forKey: StudioJSONKey("duration"))
    }
}

struct StudioZoom: Codable, Equatable, Sendable {
    var start: Double
    var end: Double
    var scale: Double
    var focus: StudioZoomFocus
    var easeIn: Double
    var easeOut: Double
    var origin: StudioZoomOrigin
    var extra: [String: StudioJSONValue]

    init(start: Double = 0, end: Double = 0, scale: Double = 1, focus: StudioZoomFocus = StudioZoomFocus(), easeIn: Double = 0, easeOut: Double = 0, origin: StudioZoomOrigin = .manual, extra: [String: StudioJSONValue] = [:]) {
        self.start = start
        self.end = end
        self.scale = scale
        self.focus = focus
        self.easeIn = easeIn
        self.easeOut = easeOut
        self.origin = origin
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        start = try container.decodeDouble("start", default: 0)
        end = try container.decodeDouble("end", default: 0)
        scale = try container.decodeDouble("scale", default: 1)
        focus = try container.decodeIfPresent(StudioZoomFocus.self, forKey: StudioJSONKey("focus")) ?? StudioZoomFocus()
        easeIn = try container.decodeDouble("easeIn", default: 0)
        easeOut = try container.decodeDouble("easeOut", default: 0)
        origin = try container.decodeEnum("origin", default: .manual)
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["start", "end", "scale", "focus", "easeIn", "easeOut", "origin"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(start, forKey: StudioJSONKey("start"))
        try container.encode(end, forKey: StudioJSONKey("end"))
        try container.encode(scale, forKey: StudioJSONKey("scale"))
        try container.encode(focus, forKey: StudioJSONKey("focus"))
        try container.encode(easeIn, forKey: StudioJSONKey("easeIn"))
        try container.encode(easeOut, forKey: StudioJSONKey("easeOut"))
        try container.encode(origin, forKey: StudioJSONKey("origin"))
    }
}

struct StudioZoomFocus: Codable, Equatable, Sendable {
    var mode: StudioZoomFocusMode
    var x: Double
    var y: Double
    var extra: [String: StudioJSONValue]

    init(mode: StudioZoomFocusMode = .point, x: Double = 0.5, y: Double = 0.5, extra: [String: StudioJSONValue] = [:]) {
        self.mode = mode
        self.x = x
        self.y = y
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        mode = try container.decodeEnum("mode", default: .point)
        x = try container.decodeDouble("x", default: 0.5)
        y = try container.decodeDouble("y", default: 0.5)
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["mode", "x", "y"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(mode, forKey: StudioJSONKey("mode"))
        try container.encode(x, forKey: StudioJSONKey("x"))
        try container.encode(y, forKey: StudioJSONKey("y"))
    }
}

struct StudioEdits: Codable, Equatable, Sendable {
    var trimStart: Double
    var trimEnd: Double?
    var cuts: [StudioTimeRange]
    var speed: [StudioSpeedRange]
    var extra: [String: StudioJSONValue]

    init(trimStart: Double = 0, trimEnd: Double? = nil, cuts: [StudioTimeRange] = [], speed: [StudioSpeedRange] = [], extra: [String: StudioJSONValue] = [:]) {
        self.trimStart = trimStart
        self.trimEnd = trimEnd
        self.cuts = cuts
        self.speed = speed
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        trimStart = try container.decodeDouble("trimStart", default: 0)
        trimEnd = try container.decodeIfPresent(Double.self, forKey: StudioJSONKey("trimEnd"))
        cuts = try container.decodeCompactArray("cuts", default: [])
        speed = try container.decodeCompactArray("speed", default: [])
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["trimStart", "trimEnd", "cuts", "speed"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(trimStart, forKey: StudioJSONKey("trimStart"))
        if let trimEnd = trimEnd { try container.encode(trimEnd, forKey: StudioJSONKey("trimEnd")) } else { try container.encodeNil(forKey: "trimEnd") }
        try container.encode(cuts, forKey: StudioJSONKey("cuts"))
        try container.encode(speed, forKey: StudioJSONKey("speed"))
    }
}

struct StudioTimeRange: Codable, Equatable, Sendable {
    var start: Double
    var end: Double
    var extra: [String: StudioJSONValue]

    init(start: Double = 0, end: Double = 0, extra: [String: StudioJSONValue] = [:]) {
        self.start = start
        self.end = end
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        start = try container.decodeDouble("start", default: 0)
        end = try container.decodeDouble("end", default: 0)
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["start", "end"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(start, forKey: StudioJSONKey("start"))
        try container.encode(end, forKey: StudioJSONKey("end"))
    }
}

struct StudioSpeedRange: Codable, Equatable, Sendable {
    var start: Double
    var end: Double
    var rate: Double
    var extra: [String: StudioJSONValue]

    init(start: Double = 0, end: Double = 0, rate: Double = 1, extra: [String: StudioJSONValue] = [:]) {
        self.start = start
        self.end = end
        self.rate = rate
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        start = try container.decodeDouble("start", default: 0)
        end = try container.decodeDouble("end", default: 0)
        rate = try container.decodeDouble("rate", default: 1)
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["start", "end", "rate"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(start, forKey: StudioJSONKey("start"))
        try container.encode(end, forKey: StudioJSONKey("end"))
        try container.encode(rate, forKey: StudioJSONKey("rate"))
    }
}

// MARK: - Output Options

struct StudioAudio: Codable, Equatable, Sendable {
    var muted: Bool
    var systemVolume: Double
    var microphoneVolume: Double
    var extra: [String: StudioJSONValue]

    init(muted: Bool = false, systemVolume: Double = 1, microphoneVolume: Double = 1, extra: [String: StudioJSONValue] = [:]) {
        self.muted = muted
        self.systemVolume = systemVolume
        self.microphoneVolume = microphoneVolume
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        muted = try container.decodeBool("muted", default: false)
        systemVolume = try container.decodeDouble("systemVolume", default: 1)
        microphoneVolume = try container.decodeDouble("microphoneVolume", default: 1)
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["muted", "systemVolume", "microphoneVolume"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(muted, forKey: StudioJSONKey("muted"))
        try container.encode(systemVolume, forKey: StudioJSONKey("systemVolume"))
        try container.encode(microphoneVolume, forKey: StudioJSONKey("microphoneVolume"))
    }
}

struct StudioOverlays: Codable, Equatable, Sendable {
    var clicks: StudioClickOverlay
    var branding: Bool
    var extra: [String: StudioJSONValue]

    init(clicks: StudioClickOverlay = StudioClickOverlay(), branding: Bool = false, extra: [String: StudioJSONValue] = [:]) {
        self.clicks = clicks
        self.branding = branding
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        clicks = try container.decodeIfPresent(StudioClickOverlay.self, forKey: StudioJSONKey("clicks")) ?? StudioClickOverlay()
        branding = try container.decodeBool("branding", default: false)
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["clicks", "branding"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(clicks, forKey: StudioJSONKey("clicks"))
        try container.encode(branding, forKey: StudioJSONKey("branding"))
    }
}

struct StudioClickOverlay: Codable, Equatable, Sendable {
    var enabled: Bool
    var color: String
    var size: Double
    var strokeWidth: Double
    var opacity: Double
    var duration: Double
    var extra: [String: StudioJSONValue]

    init(enabled: Bool = true, color: String = "#0A84FF", size: Double = 40, strokeWidth: Double = 3, opacity: Double = 0.85, duration: Double = 0.45, extra: [String: StudioJSONValue] = [:]) {
        self.enabled = enabled
        self.color = color
        self.size = size
        self.strokeWidth = strokeWidth
        self.opacity = opacity
        self.duration = duration
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        enabled = try container.decodeBool("enabled", default: true)
        color = try container.decodeString("color", default: "#0A84FF")
        size = try container.decodeDouble("size", default: 40)
        strokeWidth = try container.decodeDouble("strokeWidth", default: 3)
        opacity = try container.decodeDouble("opacity", default: 0.85)
        duration = try container.decodeDouble("duration", default: 0.45)
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["enabled", "color", "size", "strokeWidth", "opacity", "duration"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(enabled, forKey: StudioJSONKey("enabled"))
        try container.encode(color, forKey: StudioJSONKey("color"))
        try container.encode(size, forKey: StudioJSONKey("size"))
        try container.encode(strokeWidth, forKey: StudioJSONKey("strokeWidth"))
        try container.encode(opacity, forKey: StudioJSONKey("opacity"))
        try container.encode(duration, forKey: StudioJSONKey("duration"))
    }
}

struct StudioExport: Codable, Equatable, Sendable {
    var path: String
    var exportedAt: Date
    var extra: [String: StudioJSONValue]

    init(path: String, exportedAt: Date = Date(timeIntervalSince1970: 0), extra: [String: StudioJSONValue] = [:]) {
        self.path = path
        self.exportedAt = exportedAt
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        path = try container.decodeString("path", default: "")
        exportedAt = try container.decodeDate("exportedAt")
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["path", "exportedAt"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(path, forKey: StudioJSONKey("path"))
        try container.encodeDate(exportedAt, forKey: "exportedAt")
    }
}

// MARK: - Geometry

struct StudioRect: Codable, Equatable, Sendable {
    var x: Double
    var y: Double
    var width: Double
    var height: Double
    var extra: [String: StudioJSONValue]

    init(x: Double = 0, y: Double = 0, width: Double = 0, height: Double = 0, extra: [String: StudioJSONValue] = [:]) {
        self.x = x
        self.y = y
        self.width = width
        self.height = height
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        x = try container.decodeDouble("x", default: 0)
        y = try container.decodeDouble("y", default: 0)
        width = try container.decodeDouble("width", default: 0)
        height = try container.decodeDouble("height", default: 0)
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["x", "y", "width", "height"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(x, forKey: StudioJSONKey("x"))
        try container.encode(y, forKey: StudioJSONKey("y"))
        try container.encode(width, forKey: StudioJSONKey("width"))
        try container.encode(height, forKey: StudioJSONKey("height"))
    }

    var maxX: Double { x + width }
    var maxY: Double { y + height }
}
