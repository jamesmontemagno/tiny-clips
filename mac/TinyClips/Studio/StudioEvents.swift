import Foundation

// MARK: - Enums

enum StudioMouseButton: String, StudioStringEnum {
    case left
    case right
    case middle
    case other

    static let defaultValue: StudioMouseButton = .left
}

enum StudioCaptureKind: String, StudioStringEnum {
    case display
    case region
    case window

    static let defaultValue: StudioCaptureKind = .display
}

// MARK: - Events

struct StudioEvents: Codable, Equatable, Sendable {
    var schemaVersion: Int
    var capture: StudioCaptureInfo
    var clicks: [StudioClickEvent]
    var cursor: [StudioCursorSample]
    var cameraCorners: [StudioCameraCornerEvent]
    var markers: [StudioJSONValue]
    var extra: [String: StudioJSONValue]

    init(
        schemaVersion: Int = 1,
        capture: StudioCaptureInfo = StudioCaptureInfo(),
        clicks: [StudioClickEvent] = [],
        cursor: [StudioCursorSample] = [],
        cameraCorners: [StudioCameraCornerEvent] = [],
        markers: [StudioJSONValue] = [],
        extra: [String: StudioJSONValue] = [:]
    ) {
        self.schemaVersion = schemaVersion
        self.capture = capture
        self.clicks = clicks
        self.cursor = cursor
        self.cameraCorners = cameraCorners
        self.markers = markers
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        schemaVersion = try container.decodeInt("schemaVersion", default: 1)
        guard schemaVersion <= StudioJSON.supportedSchemaVersion else {
            throw StudioProjectError.unsupportedVersion(schemaVersion)
        }
        capture = try container.decodeIfPresent(StudioCaptureInfo.self, forKey: StudioJSONKey("capture")) ?? StudioCaptureInfo()
        clicks = try container.decodeCompactArray("clicks", default: [])
        cursor = try container.decodeCompactArray("cursor", default: [])
        cameraCorners = try container.decodeCompactArray("cameraCorners", default: [])
        markers = try container.decodeCompactArray("markers", default: [])
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["schemaVersion", "capture", "clicks", "cursor", "cameraCorners", "markers"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(schemaVersion, forKey: StudioJSONKey("schemaVersion"))
        try container.encode(capture, forKey: StudioJSONKey("capture"))
        try container.encode(clicks, forKey: StudioJSONKey("clicks"))
        try container.encode(cursor, forKey: StudioJSONKey("cursor"))
        try container.encode(cameraCorners, forKey: StudioJSONKey("cameraCorners"))
        try container.encode(markers, forKey: StudioJSONKey("markers"))
    }
}

struct StudioCaptureInfo: Codable, Equatable, Sendable {
    var width: Int
    var height: Int
    var scale: Double
    var kind: StudioCaptureKind
    var extra: [String: StudioJSONValue]

    init(width: Int = 0, height: Int = 0, scale: Double = 1, kind: StudioCaptureKind = .display, extra: [String: StudioJSONValue] = [:]) {
        self.width = width
        self.height = height
        self.scale = scale
        self.kind = kind
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        width = try container.decodeInt("width", default: 0)
        height = try container.decodeInt("height", default: 0)
        scale = try container.decodeDouble("scale", default: 1)
        kind = try container.decodeEnum("kind", default: .display)
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["width", "height", "scale", "kind"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(width, forKey: StudioJSONKey("width"))
        try container.encode(height, forKey: StudioJSONKey("height"))
        try container.encode(scale, forKey: StudioJSONKey("scale"))
        try container.encode(kind, forKey: StudioJSONKey("kind"))
    }
}

struct StudioClickEvent: Codable, Equatable, Sendable {
    var t: Double
    var x: Double
    var y: Double
    var button: StudioMouseButton
    var extra: [String: StudioJSONValue]

    init(t: Double = 0, x: Double = 0, y: Double = 0, button: StudioMouseButton = .left, extra: [String: StudioJSONValue] = [:]) {
        self.t = t
        self.x = x
        self.y = y
        self.button = button
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        t = try container.decodeDouble("t", default: 0)
        x = try container.decodeDouble("x", default: 0)
        y = try container.decodeDouble("y", default: 0)
        button = try container.decodeEnum("button", default: .left)
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["t", "x", "y", "button"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(t, forKey: StudioJSONKey("t"))
        try container.encode(x, forKey: StudioJSONKey("x"))
        try container.encode(y, forKey: StudioJSONKey("y"))
        try container.encode(button, forKey: StudioJSONKey("button"))
    }
}

struct StudioCursorSample: Codable, Equatable, Sendable {
    var t: Double
    var x: Double
    var y: Double
    var extra: [String: StudioJSONValue]

    init(t: Double = 0, x: Double = 0, y: Double = 0, extra: [String: StudioJSONValue] = [:]) {
        self.t = t
        self.x = x
        self.y = y
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        t = try container.decodeDouble("t", default: 0)
        x = try container.decodeDouble("x", default: 0)
        y = try container.decodeDouble("y", default: 0)
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["t", "x", "y"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(t, forKey: StudioJSONKey("t"))
        try container.encode(x, forKey: StudioJSONKey("x"))
        try container.encode(y, forKey: StudioJSONKey("y"))
    }
}

struct StudioCameraCornerEvent: Codable, Equatable, Sendable {
    var t: Double
    var corner: StudioAnchor
    var extra: [String: StudioJSONValue]

    init(t: Double = 0, corner: StudioAnchor = .bottomRight, extra: [String: StudioJSONValue] = [:]) {
        self.t = t
        self.corner = corner
        self.extra = extra
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: StudioJSONKey.self)
        t = try container.decodeDouble("t", default: 0)
        corner = try container.decodeEnum("corner", default: .bottomRight)
        extra = try StudioJSON.decodeExtra(from: container, excluding: ["t", "corner"])
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: StudioJSONKey.self)
        try StudioJSON.encodeExtra(extra, to: &container)
        try container.encode(t, forKey: StudioJSONKey("t"))
        try container.encode(corner, forKey: StudioJSONKey("corner"))
    }
}
