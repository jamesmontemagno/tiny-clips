import AppKit
import ScreenCaptureKit

/// What an unattended `capture.screenshot` / `capture.ocr` request should capture.
enum AutomationCaptureTarget: Equatable {
    /// A whole display; `nil` means the main display.
    case screen(displayID: CGDirectDisplayID?)
    /// A display-local rectangle in physical pixels with a top-left origin.
    case region(displayID: CGDirectDisplayID, pixelRect: CGRect)
    case window(windowID: CGWindowID)

    var modeName: String {
        switch self {
        case .screen: return "screen"
        case .region: return "region"
        case .window: return "window"
        }
    }
}

/// Geometry of one display, in the units the agent contract uses.
struct AutomationDisplayGeometry: Equatable {
    var displayID: CGDirectDisplayID
    var name: String
    /// Display size in points (AppKit frame size).
    var pointSize: CGSize
    var scaleFactor: CGFloat
    var isMain: Bool

    var pixelWidth: Int { Int((pointSize.width * scaleFactor).rounded()) }
    var pixelHeight: Int { Int((pointSize.height * scaleFactor).rounded()) }
}

/// Pure parsing and validation for agent capture targets; no ScreenCaptureKit access.
enum AutomationCaptureTargets {
    static let displayPrefix = "display:"
    static let windowPrefix = "window:"

    static func displayID(for id: CGDirectDisplayID) -> String { "\(displayPrefix)\(id)" }
    static func windowID(for id: CGWindowID) -> String { "\(windowPrefix)\(id)" }

    static func parseDisplayID(_ value: String) -> CGDirectDisplayID? {
        guard value.hasPrefix(displayPrefix) else { return nil }
        return CGDirectDisplayID(value.dropFirst(displayPrefix.count))
    }

    static func parseWindowID(_ value: String) -> CGWindowID? {
        guard value.hasPrefix(windowPrefix) else { return nil }
        return CGWindowID(value.dropFirst(windowPrefix.count))
    }

    private static let allowedParams: Set<String> = ["mode", "displayId", "windowId", "region"]

    /// Parses `mode`, `displayId`, `windowId`, and `region`. Missing targets are errors; a picker
    /// is never shown for agent requests.
    static func parse(_ params: [String: Any]) throws -> AutomationCaptureTarget {
        if let unknown = params.keys.first(where: { !allowedParams.contains($0) }) {
            throw AutomationError(.invalidParams, "Unknown parameter \(unknown).")
        }
        let mode: String
        switch params["mode"] {
        case nil, is NSNull: mode = "screen"
        case let value as String: mode = value
        default: throw AutomationError(.invalidParams, "mode must be screen, region, or window.")
        }

        func optionalString(_ key: String) throws -> String? {
            switch params[key] {
            case nil, is NSNull: return nil
            case let value as String where !value.isEmpty: return value
            default: throw AutomationError(.invalidParams, "\(key) must be a non-empty string.")
            }
        }
        func display(_ value: String) throws -> CGDirectDisplayID {
            guard let id = parseDisplayID(value) else {
                throw AutomationError(.invalidParams, "displayId \(value) is not a display ID from capture.targets.")
            }
            return id
        }
        func reject(_ keys: [String]) throws {
            if let key = keys.first(where: { params[$0] != nil && !(params[$0] is NSNull) }) {
                throw AutomationError(.invalidParams, "\(key) is not used with mode \(mode).")
            }
        }

        switch mode {
        case "screen":
            try reject(["windowId", "region"])
            return .screen(displayID: try optionalString("displayId").map(display))
        case "region":
            try reject(["windowId"])
            guard let displayValue = try optionalString("displayId") else {
                throw AutomationError(.invalidParams, "mode region needs displayId from capture.targets.")
            }
            guard params["region"] != nil, !(params["region"] is NSNull) else {
                throw AutomationError(.invalidParams, "mode region needs region {x, y, width, height}.")
            }
            return .region(displayID: try display(displayValue), pixelRect: try parseRegion(params["region"]))
        case "window":
            try reject(["displayId", "region"])
            guard let windowValue = try optionalString("windowId") else {
                throw AutomationError(.invalidParams, "mode window needs windowId from capture.targets.")
            }
            guard let id = parseWindowID(windowValue) else {
                throw AutomationError(.invalidParams, "windowId \(windowValue) is not a window ID from capture.targets.")
            }
            return .window(windowID: id)
        default:
            throw AutomationError(.invalidParams, "mode must be screen, region, or window.")
        }
    }

    static func parseRegion(_ value: Any?) throws -> CGRect {
        guard let object = value as? [String: Any] else {
            throw AutomationError(.invalidParams, "region must be an object with x, y, width, and height.")
        }
        let keys: Set<String> = ["x", "y", "width", "height"]
        if let unknown = object.keys.first(where: { !keys.contains($0) }) {
            throw AutomationError(.invalidParams, "region has unknown field \(unknown).")
        }
        func integer(_ key: String, minimum: Int) throws -> Int {
            guard let number = AutomationValue.int(object[key]) else {
                throw AutomationError(.invalidParams, "region.\(key) must be an integer.")
            }
            guard number >= minimum else {
                throw AutomationError(.invalidParams, "region.\(key) must be at least \(minimum).")
            }
            return number
        }
        return CGRect(
            x: try integer("x", minimum: 0),
            y: try integer("y", minimum: 0),
            width: try integer("width", minimum: 1),
            height: try integer("height", minimum: 1)
        )
    }

    /// Converts a display-local physical-pixel rect to the point-based, top-left `sourceRect`
    /// that `CaptureRegion` uses, after checking it fits on the display.
    static func sourceRect(forPixelRect pixelRect: CGRect, on display: AutomationDisplayGeometry) throws -> CGRect {
        guard display.scaleFactor > 0 else {
            throw AutomationError(.internal, "Display has no scale factor.")
        }
        guard pixelRect.maxX <= CGFloat(display.pixelWidth), pixelRect.maxY <= CGFloat(display.pixelHeight) else {
            throw AutomationError(
                .invalidParams,
                "region must fit inside the display (\(display.pixelWidth)×\(display.pixelHeight) pixels)."
            )
        }
        return CGRect(
            x: pixelRect.minX / display.scaleFactor,
            y: pixelRect.minY / display.scaleFactor,
            width: pixelRect.width / display.scaleFactor,
            height: pixelRect.height / display.scaleFactor
        )
    }

    static func displayJSON(_ display: AutomationDisplayGeometry) -> [String: Any] {
        [
            "id": displayID(for: display.displayID),
            "name": display.name,
            "width": display.pixelWidth,
            "height": display.pixelHeight,
            "scaleFactor": Double(display.scaleFactor),
            "isMain": display.isMain,
        ]
    }
}

/// Live display and window discovery for agent captures.
@MainActor
enum AutomationCaptureEnvironment {
    static func displays() -> [AutomationDisplayGeometry] {
        let mainID = CGMainDisplayID()
        return NSScreen.screens.compactMap { screen in
            guard let id = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID else {
                return nil
            }
            return AutomationDisplayGeometry(
                displayID: id,
                name: screen.localizedName,
                pointSize: screen.frame.size,
                scaleFactor: screen.backingScaleFactor,
                isMain: id == mainID
            )
        }
    }

    static func display(_ id: CGDirectDisplayID?) throws -> AutomationDisplayGeometry {
        let all = displays()
        let match: AutomationDisplayGeometry?
        if let id {
            match = all.first { $0.displayID == id }
        } else {
            match = all.first(where: \.isMain) ?? all.first
        }
        guard let match else {
            throw AutomationError(.notFound, "No display matches that displayId. Call capture.targets again.")
        }
        return match
    }

    /// Visible, capturable app windows front-to-back, filtered like the window picker.
    static func windows() async throws -> [SCWindow] {
        let content: SCShareableContent
        do {
            content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        } catch {
            throw AutomationError(.permissionRequired, "Could not list windows: \(error.localizedDescription)")
        }
        let includeTinyClips = CaptureSettings.shared.includeTinyClipsInCapture
        let candidates = content.windows.filter { window in
            guard let bundleIdentifier = window.owningApplication?.bundleIdentifier else { return false }
            let isTinyClipsWindow = bundleIdentifier == Bundle.main.bundleIdentifier
            return window.frame.width >= 50 && window.frame.height >= 50
                && window.windowLayer == 0
                && window.isOnScreen
                && (includeTinyClips || !isTinyClipsWindow)
                && !WindowSelector.systemChromeBundleIdentifiers.contains(bundleIdentifier)
        }
        return WindowPickerHitTesting.frontToBack(
            candidates,
            windowID: \.windowID,
            zOrder: WindowPickerHitTesting.currentZOrder()
        )
    }

    static func window(_ id: CGWindowID) async throws -> SCWindow {
        guard let window = try await windows().first(where: { $0.windowID == id }) else {
            throw AutomationError(.notFound, "No visible window matches that windowId. Call capture.targets again.")
        }
        return window
    }

    static func windowJSON(_ window: SCWindow) -> [String: Any] {
        let scale = ScreenshotCapture.pixelScale(for: window)
        var json: [String: Any] = [
            "id": AutomationCaptureTargets.windowID(for: window.windowID),
            "title": window.title ?? "",
            "app": window.owningApplication?.applicationName ?? "",
            "width": Int((window.frame.width * scale).rounded()),
            "height": Int((window.frame.height * scale).rounded()),
        ]
        if let bundleID = window.owningApplication?.bundleIdentifier {
            json["bundleId"] = bundleID
        }
        // SCWindow frames and CGDisplayBounds share the global top-left coordinate space.
        let best = displays().max { lhs, rhs in
            overlapArea(window.frame, CGDisplayBounds(lhs.displayID)) < overlapArea(window.frame, CGDisplayBounds(rhs.displayID))
        }
        if let best, overlapArea(window.frame, CGDisplayBounds(best.displayID)) > 0 {
            json["displayId"] = AutomationCaptureTargets.displayID(for: best.displayID)
        }
        return json
    }

    private static func overlapArea(_ a: CGRect, _ b: CGRect) -> CGFloat {
        let intersection = a.intersection(b)
        return intersection.isNull ? 0 : intersection.width * intersection.height
    }

    /// Captures the target as an image without showing any picker or overlay.
    static func captureImage(_ target: AutomationCaptureTarget) async throws -> (image: CGImage, scaleFactor: CGFloat) {
        switch target {
        case .screen(let displayID):
            let display = try display(displayID)
            let region = CaptureRegion(
                sourceRect: CGRect(origin: .zero, size: display.pointSize),
                displayID: display.displayID,
                scaleFactor: display.scaleFactor
            )
            return (try await ScreenshotCapture.captureImage(region: region), display.scaleFactor)
        case .region(let displayID, let pixelRect):
            let display = try display(displayID)
            let sourceRect = try AutomationCaptureTargets.sourceRect(forPixelRect: pixelRect, on: display)
            let region = CaptureRegion(sourceRect: sourceRect, displayID: display.displayID, scaleFactor: display.scaleFactor)
            return (try await ScreenshotCapture.captureImage(region: region), display.scaleFactor)
        case .window(let windowID):
            let window = try await window(windowID)
            return try await ScreenshotCapture.captureWindowImage(window)
        }
    }
}
