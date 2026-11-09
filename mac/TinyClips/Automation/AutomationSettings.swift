import Foundation

/// The settings agents may read and change through `settings.get` / `settings.set`. Anything not
/// listed here (save folders, agent control itself, credentials) is not reachable.
struct AutomationSetting {
    let key: String
    let writable: Bool
    let read: @MainActor () -> Any
    let write: @MainActor (Any) throws -> Void

    static func bool(_ key: String, _ keyPath: ReferenceWritableKeyPath<CaptureSettings, Bool>) -> AutomationSetting {
        AutomationSetting(
            key: key,
            writable: true,
            read: { CaptureSettings.shared[keyPath: keyPath] },
            write: { value in
                guard let bool = AutomationValue.bool(value) else {
                    throw AutomationError(.invalidParams, "\(key) must be true or false.")
                }
                CaptureSettings.shared[keyPath: keyPath] = bool
            }
        )
    }

    static func int(
        _ key: String,
        _ keyPath: ReferenceWritableKeyPath<CaptureSettings, Int>,
        _ range: ClosedRange<Int>,
        allowed: [Int]? = nil
    ) -> AutomationSetting {
        AutomationSetting(
            key: key,
            writable: true,
            read: { CaptureSettings.shared[keyPath: keyPath] },
            write: { value in
                guard let number = AutomationValue.int(value), range.contains(number) else {
                    throw AutomationError(.invalidParams, "\(key) must be a whole number from \(range.lowerBound) to \(range.upperBound).")
                }
                if let allowed, !allowed.contains(number) {
                    throw AutomationError(.invalidParams, "\(key) must be one of: \(allowed.map(String.init).joined(separator: ", ")).")
                }
                CaptureSettings.shared[keyPath: keyPath] = number
            }
        )
    }

    static func double(
        _ key: String,
        _ keyPath: ReferenceWritableKeyPath<CaptureSettings, Double>,
        _ range: ClosedRange<Double>
    ) -> AutomationSetting {
        AutomationSetting(
            key: key,
            writable: true,
            read: { CaptureSettings.shared[keyPath: keyPath] },
            write: { value in
                guard let number = AutomationValue.double(value), range.contains(number) else {
                    throw AutomationError(.invalidParams, "\(key) must be a number from \(range.lowerBound) to \(range.upperBound).")
                }
                CaptureSettings.shared[keyPath: keyPath] = number
            }
        )
    }

    static func string(
        _ key: String,
        _ keyPath: ReferenceWritableKeyPath<CaptureSettings, String>,
        allowed: [String]? = nil,
        maximumLength: Int = 200
    ) -> AutomationSetting {
        AutomationSetting(
            key: key,
            writable: true,
            read: { CaptureSettings.shared[keyPath: keyPath] },
            write: { value in
                guard let string = value as? String, string.count <= maximumLength else {
                    throw AutomationError(.invalidParams, "\(key) must be text of up to \(maximumLength) characters.")
                }
                if let allowed, !allowed.contains(string) {
                    throw AutomationError(.invalidParams, "\(key) must be one of: \(allowed.joined(separator: ", ")).")
                }
                CaptureSettings.shared[keyPath: keyPath] = string
            }
        )
    }

    static func readOnly(_ key: String, _ read: @escaping @MainActor () -> Any) -> AutomationSetting {
        AutomationSetting(
            key: key,
            writable: false,
            read: read,
            write: { _ in throw AutomationError(.invalidParams, "\(key) is read-only for agents.") }
        )
    }

    @MainActor
    static let all: [AutomationSetting] = [
        .readOnly("screenshotFolder") { SaveService.shared.outputDirectoryURL(for: .screenshot).path },
        .readOnly("videoFolder") { SaveService.shared.outputDirectoryURL(for: .video).path },
        .readOnly("gifFolder") { SaveService.shared.outputDirectoryURL(for: .gif).path },
        .string("fileNameTemplate", \.fileNameTemplate),
        .string("studioExportResolution", \.studioExportResolution, allowed: StudioExportResolution.allCases.map(\.rawValue)),
        .string("studioExportQuality", \.studioExportQuality, allowed: StudioExportQuality.allCases.map(\.rawValue)),
        .string("screenshotFormat", \.screenshotFormat, allowed: ImageFormat.allCases.map(\.rawValue)),
        .int("screenshotScale", \.screenshotScale, 25...100, allowed: [25, 50, 75, 100]),
        .double("jpegQuality", \.jpegQuality, 0.1...1),
        .bool("copyScreenshotToClipboard", \.copyScreenshotToClipboard),
        .bool("copyVideoToClipboard", \.copyVideoToClipboard),
        .bool("copyGifToClipboard", \.copyGifToClipboard),
        .bool("showInFinder", \.showInFinder),
        .bool("showSaveNotifications", \.showSaveNotifications),
        .bool("showScreenshotEditor", \.showScreenshotEditor),
        .bool("showTrimmer", \.showTrimmer),
        .bool("showGifTrimmer", \.showGifTrimmer),
        .bool("showScreenshotCapturePicker", \.showScreenshotCapturePicker),
        .bool("showVideoCapturePicker", \.showVideoCapturePicker),
        .bool("showGifCapturePicker", \.showGifCapturePicker),
        .bool("screenshotCountdownEnabled", \.screenshotCountdownEnabled),
        .int("screenshotCountdownDuration", \.screenshotCountdownDuration, 1...10),
        .bool("videoCountdownEnabled", \.videoCountdownEnabled),
        .int("videoCountdownDuration", \.videoCountdownDuration, 1...10),
        .bool("gifCountdownEnabled", \.gifCountdownEnabled),
        .int("gifCountdownDuration", \.gifCountdownDuration, 1...10),
        .int("videoFrameRate", \.videoFrameRate, 24...60, allowed: [24, 30, 60]),
        .int("videoRecordingTimeLimitMinutes", \.videoRecordingTimeLimitMinutes, 0...240),
        .double("gifFrameRate", \.gifFrameRate, 5...30),
        .int("gifMaxWidth", \.gifMaxWidth, 320...1920),
        .bool("recordAudio", \.recordAudio),
        .bool("recordMicrophone", \.recordMicrophone),
        .bool("webcamEnabled", \.webcamEnabled),
        .bool("showMouseClickVisualsInVideo", \.showMouseClickVisualsInVideo),
        .bool("showMouseClickVisualsInGif", \.showMouseClickVisualsInGif),
        .bool("showRegionIndicator", \.showRegionIndicator),
        .bool("includeTinyClipsInCapture", \.includeTinyClipsInCapture),
        .bool("studioPreviewEnabled", \.studioPreviewEnabled),
        .bool("teleprompterEnabled", \.teleprompterEnabled),
    ]

    @MainActor
    static func named(_ key: String) -> AutomationSetting? {
        all.first { $0.key == key }
    }
}

/// Reads loosely typed JSON values.
enum AutomationValue {
    static func bool(_ value: Any?) -> Bool? {
        guard let number = value as? NSNumber, CFGetTypeID(number) == CFBooleanGetTypeID() else { return nil }
        return number.boolValue
    }

    static func int(_ value: Any?) -> Int? {
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() else { return nil }
        let double = number.doubleValue
        guard double.rounded() == double, abs(double) < Double(Int32.max) else { return nil }
        return number.intValue
    }

    static func double(_ value: Any?) -> Double? {
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() else { return nil }
        return number.doubleValue
    }
}
