import Foundation

// MARK: - Dynamic JSON

enum StudioJSONValue: Codable, Equatable, Sendable {
    case null
    case bool(Bool)
    case number(Double)
    case string(String)
    case array([StudioJSONValue])
    case object([String: StudioJSONValue])

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() {
            self = .null
        } else if let value = try? container.decode(Bool.self) {
            self = .bool(value)
        } else if let value = try? container.decode(Double.self) {
            self = .number(value)
        } else if let value = try? container.decode(String.self) {
            self = .string(value)
        } else if let value = try? container.decode([StudioJSONValue].self) {
            self = .array(value)
        } else {
            self = .object(try container.decode([String: StudioJSONValue].self))
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .null:
            try container.encodeNil()
        case .bool(let value):
            try container.encode(value)
        case .number(let value):
            try container.encode(value)
        case .string(let value):
            try container.encode(value)
        case .array(let value):
            try container.encode(value)
        case .object(let value):
            try container.encode(value)
        }
    }
}

struct StudioJSONKey: CodingKey, Hashable {
    let stringValue: String
    let intValue: Int?

    init(_ stringValue: String) {
        self.stringValue = stringValue
        self.intValue = nil
    }

    init?(stringValue: String) {
        self.init(stringValue)
    }

    init?(intValue: Int) {
        self.stringValue = "\(intValue)"
        self.intValue = intValue
    }
}

// MARK: - Errors

enum StudioProjectError: Error, Equatable, LocalizedError {
    case invalidProject(String)
    case unsupportedVersion(Int)

    var errorDescription: String? {
        switch self {
        case .invalidProject(let message):
            return "Invalid Studio project: \(message)"
        case .unsupportedVersion(let version):
            return "Unsupported Studio schema version \(version)"
        }
    }
}

// MARK: - Helpers

enum StudioJSON {
    static let supportedSchemaVersion = 1

    static func makeDecoder() -> JSONDecoder {
        JSONDecoder()
    }

    static func makeEncoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return encoder
    }

    static func timestamp(from string: String?) -> Date {
        guard let string = string else { return Date(timeIntervalSince1970: 0) }
        if let date = wholeSecondFormatter.date(from: string) {
            return date
        }
        if let date = fractionalFormatter.date(from: string) {
            return date
        }
        return Date(timeIntervalSince1970: 0)
    }

    static func timestampString(from date: Date) -> String {
        wholeSecondFormatter.string(from: date)
    }

    static func decodeExtra(
        from container: KeyedDecodingContainer<StudioJSONKey>,
        excluding knownKeys: Set<String>
    ) throws -> [String: StudioJSONValue] {
        var extra: [String: StudioJSONValue] = [:]
        for key in container.allKeys where !knownKeys.contains(key.stringValue) {
            extra[key.stringValue] = try container.decode(StudioJSONValue.self, forKey: key)
        }
        return extra
    }

    static func encodeExtra(
        _ extra: [String: StudioJSONValue],
        to container: inout KeyedEncodingContainer<StudioJSONKey>
    ) throws {
        for key in extra.keys.sorted() {
            try container.encode(extra[key], forKey: StudioJSONKey(key))
        }
    }

    static func requireFinite(_ value: Double, _ name: String) throws -> Double {
        guard value.isFinite else {
            throw StudioProjectError.invalidProject("\(name) must be finite")
        }
        return value
    }

    private static let wholeSecondFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'"
        return formatter
    }()

    private static let fractionalFormatter: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        return formatter
    }()
}

protocol StudioStringEnum: RawRepresentable, Codable, Equatable, Sendable where RawValue == String {
    static var defaultValue: Self { get }
}

extension StudioStringEnum {
    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        let value = try container.decode(String.self)
        self = Self(rawValue: value) ?? Self.defaultValue
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }
}

extension KeyedDecodingContainer where Key == StudioJSONKey {
    func decodeString(_ key: String, default defaultValue: String) throws -> String {
        try decodeIfPresent(String.self, forKey: StudioJSONKey(key)) ?? defaultValue
    }

    func decodeBool(_ key: String, default defaultValue: Bool) throws -> Bool {
        try decodeIfPresent(Bool.self, forKey: StudioJSONKey(key)) ?? defaultValue
    }

    func decodeInt(_ key: String, default defaultValue: Int) throws -> Int {
        try decodeIfPresent(Int.self, forKey: StudioJSONKey(key)) ?? defaultValue
    }

    func decodeDouble(_ key: String, default defaultValue: Double) throws -> Double {
        try decodeIfPresent(Double.self, forKey: StudioJSONKey(key)) ?? defaultValue
    }

    func decodeRequiredInt(_ key: String) throws -> Int {
        guard let value = try decodeIfPresent(Int.self, forKey: StudioJSONKey(key)) else {
            throw StudioProjectError.invalidProject("Missing required property \(key)")
        }
        return value
    }

    func decodeRequiredDouble(_ key: String) throws -> Double {
        guard let value = try decodeIfPresent(Double.self, forKey: StudioJSONKey(key)) else {
            throw StudioProjectError.invalidProject("Missing required property \(key)")
        }
        return try StudioJSON.requireFinite(value, key)
    }

    func decodeDate(_ key: String) throws -> Date {
        StudioJSON.timestamp(from: try decodeIfPresent(String.self, forKey: StudioJSONKey(key)))
    }

    func decodeNullableString(_ key: String, default defaultValue: String?) throws -> String? {
        let codingKey = StudioJSONKey(key)
        guard contains(codingKey) else { return defaultValue }
        if try decodeNil(forKey: codingKey) { return nil }
        return try decode(String.self, forKey: codingKey)
    }

    func decodeEnum<T: StudioStringEnum>(_ key: String, default defaultValue: T) throws -> T {
        guard let rawValue = try decodeIfPresent(String.self, forKey: StudioJSONKey(key)) else {
            return defaultValue
        }
        return T(rawValue: rawValue) ?? defaultValue
    }
}

extension KeyedEncodingContainer where Key == StudioJSONKey {
    mutating func encodeDate(_ value: Date, forKey key: String) throws {
        try encode(StudioJSON.timestampString(from: value), forKey: StudioJSONKey(key))
    }

    mutating func encodeNil(forKey key: String) throws {
        try encodeNil(forKey: StudioJSONKey(key))
    }
}
