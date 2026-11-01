import AppKit
import XCTest
@testable import TinyClips

final class AutomationCaptureTargetsTests: XCTestCase {
    private static var repoRoot: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
    }

    private let retina = AutomationDisplayGeometry(
        displayID: 7,
        name: "Built-in",
        pointSize: CGSize(width: 1512, height: 982),
        scaleFactor: 2,
        isMain: true
    )

    private func assertInvalid(_ block: () throws -> Void, file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertThrowsError(try block(), file: file, line: line) { error in
            XCTAssertEqual((error as? AutomationError)?.code, .invalidParams, file: file, line: line)
        }
    }

    // MARK: - Contract

    func testContractDecodesNestedSchemaAndSupportsAutonomousCommands() throws {
        let contract = try AutomationContract.load(from: Self.repoRoot.appendingPathComponent("shared/automation/commands.json"))
        for name in [
            "capture.targets", "capture.screenshot", "capture.ocr",
            "capture.scrolling.start", "capture.scrolling.stop", "capture.scrolling.status", "capture.scrolling.cancel",
            "recording.startVideo", "recording.startGif", "editor.annotate",
        ] {
            XCTAssertNotNil(contract.command(named: name), name)
            XCTAssertTrue(AutomationCommandRouter.supportedCommands.contains(name), name)
        }
        XCTAssertEqual(contract.command(named: "capture.scrolling.cancel")?.scope, "discard")
        for name in ["recording.startVideo", "recording.startGif", "capture.scrolling.start"] {
            XCTAssertNotNil(contract.command(named: name)?.params["mode"])
        }
        XCTAssertEqual(contract.command(named: "editor.annotate")?.params["path"]?.required, true)
        XCTAssertNotEqual(contract.command(named: "capture.screenshot")?.params["region"]?.required, true)
        let names = Set(contract.commands.map(\.name))
        XCTAssertTrue(AutomationCommandRouter.supportedCommands.isSubset(of: names))
    }

    // MARK: - Target parsing

    func testParseDefaultsToMainScreen() throws {
        XCTAssertEqual(try AutomationCaptureTargets.parse([:]), .screen(displayID: nil))
        XCTAssertEqual(try AutomationCaptureTargets.parse(["mode": "screen", "displayId": "display:3"]), .screen(displayID: 3))
    }

    func testParseRegionAndWindow() throws {
        let region = try AutomationCaptureTargets.parse([
            "mode": "region",
            "displayId": "display:7",
            "region": ["x": 10, "y": 20, "width": 300, "height": 200],
        ])
        XCTAssertEqual(region, .region(displayID: 7, pixelRect: CGRect(x: 10, y: 20, width: 300, height: 200)))
        XCTAssertEqual(try AutomationCaptureTargets.parse(["mode": "window", "windowId": "window:42"]), .window(windowID: 42))
    }

    func testParseRejectsMissingTargetsInsteadOfOpeningPicker() {
        assertInvalid { _ = try AutomationCaptureTargets.parse(["mode": "window"]) }
        assertInvalid { _ = try AutomationCaptureTargets.parse(["mode": "region", "displayId": "display:7"]) }
        assertInvalid { _ = try AutomationCaptureTargets.parse(["mode": "region", "region": ["x": 0, "y": 0, "width": 1, "height": 1]]) }
    }

    func testParseRejectsMalformedInput() {
        assertInvalid { _ = try AutomationCaptureTargets.parse(["mode": "scrolling"]) }
        assertInvalid { _ = try AutomationCaptureTargets.parse(["mode": "window", "windowId": "42"]) }
        assertInvalid { _ = try AutomationCaptureTargets.parse(["mode": "window", "windowId": "display:1"]) }
        assertInvalid { _ = try AutomationCaptureTargets.parse(["mode": "screen", "windowId": "window:1"]) }
        assertInvalid { _ = try AutomationCaptureTargets.parse(["mode": "screen", "extra": true]) }
        let base: [String: Any] = ["mode": "region", "displayId": "display:7"]
        for region: [String: Any] in [
            ["x": -1, "y": 0, "width": 10, "height": 10],
            ["x": 0, "y": 0, "width": 0, "height": 10],
            ["x": 0.5, "y": 0, "width": 10, "height": 10],
            ["x": 0, "y": 0, "width": 10],
            ["x": 0, "y": 0, "width": 10, "height": 10, "depth": 1],
            ["x": true, "y": 0, "width": 10, "height": 10],
        ] {
            var params = base
            params["region"] = region
            assertInvalid { _ = try AutomationCaptureTargets.parse(params) }
        }
    }

    func testRegionPixelsConvertToDisplayPoints() throws {
        let rect = try AutomationCaptureTargets.sourceRect(
            forPixelRect: CGRect(x: 100, y: 50, width: 400, height: 300),
            on: retina
        )
        XCTAssertEqual(rect, CGRect(x: 50, y: 25, width: 200, height: 150))
        let full = try AutomationCaptureTargets.sourceRect(forPixelRect: CGRect(x: 0, y: 0, width: 3024, height: 1964), on: retina)
        XCTAssertEqual(full.size, retina.pointSize)
    }

    func testRegionOutsideDisplayIsRejected() {
        assertInvalid {
            _ = try AutomationCaptureTargets.sourceRect(forPixelRect: CGRect(x: 3000, y: 0, width: 25, height: 10), on: retina)
        }
        assertInvalid {
            _ = try AutomationCaptureTargets.sourceRect(forPixelRect: CGRect(x: 0, y: 1900, width: 10, height: 65), on: retina)
        }
    }

    func testDisplayJSONUsesPhysicalPixels() {
        let json = AutomationCaptureTargets.displayJSON(retina)
        XCTAssertEqual(json["id"] as? String, "display:7")
        XCTAssertEqual(json["width"] as? Int, 3024)
        XCTAssertEqual(json["height"] as? Int, 1964)
        XCTAssertEqual(json["scaleFactor"] as? Double, 2)
        XCTAssertEqual(AutomationCaptureTargets.parseDisplayID(json["id"] as! String), 7)
    }

    // MARK: - Annotations

    private let imageSize = CGSize(width: 400, height: 200)

    func testParsesFullToolboxIntoNormalizedEditorAnnotations() throws {
        let annotations = try AutomationAnnotations.parse([
            ["type": "rectangle", "bounds": ["x": 40, "y": 20, "width": 200, "height": 100], "color": "#00FF00", "fillColor": "#8000FF00"],
            ["type": "ellipse", "bounds": ["x": 0, "y": 0, "width": 10, "height": 10]],
            ["type": "arrow", "bounds": ["x": 0, "y": 0, "width": 0, "height": 0], "points": [["x": 10, "y": 10], ["x": 100, "y": 50]]],
            ["type": "line", "bounds": ["x": 0, "y": 0, "width": 0, "height": 100]],
            ["type": "pen", "bounds": ["x": 0, "y": 0, "width": 50, "height": 50], "points": [["x": 0, "y": 0], ["x": 25, "y": 25], ["x": 50, "y": 50]]],
            ["type": "text", "bounds": ["x": 10, "y": 10, "width": 100, "height": 30], "text": "Hi", "fontSize": 20],
            ["type": "number", "bounds": ["x": 10, "y": 10, "width": 30, "height": 30]],
            ["type": "number", "bounds": ["x": 50, "y": 10, "width": 30, "height": 30]],
            ["type": "emoji", "bounds": ["x": 10, "y": 10, "width": 40, "height": 40], "emoji": "🎉"],
            ["type": "redact", "bounds": ["x": 300, "y": 100, "width": 100, "height": 100], "redactionLevel": "heavy", "redactionStyle": "solid"],
        ] as [Any], imagePixelSize: imageSize)

        XCTAssertEqual(annotations.map(\.tool), [.rectangle, .circle, .arrow, .line, .pencil, .text, .number, .number, .emoji, .blur])
        XCTAssertEqual(annotations[0].rect, CGRect(x: 0.1, y: 0.1, width: 0.5, height: 0.5))
        XCTAssertEqual(annotations[2].points, [CGPoint(x: 0.025, y: 0.05), CGPoint(x: 0.25, y: 0.25)])
        XCTAssertEqual(annotations[3].points, [CGPoint(x: 0, y: 0), CGPoint(x: 0, y: 0.5)])
        XCTAssertEqual(annotations[5].text, "Hi")
        XCTAssertEqual(annotations[5].fontSize, 20 * 800 / 400, accuracy: 0.001)
        XCTAssertEqual(annotations[6].text, "1")
        XCTAssertEqual(annotations[7].text, "2")
        XCTAssertEqual(annotations[8].text, "🎉")
        XCTAssertTrue(annotations[9].isOpaqueRedaction)
        XCTAssertEqual(annotations[9].redactionBlurPreset, .heavy)
    }

    func testRejectsMalformedAnnotations() {
        let bounds: [String: Any] = ["x": 0, "y": 0, "width": 10, "height": 10]
        let invalid: [Any] = [
            [],
            [["type": "star", "bounds": bounds]],
            [["type": "rectangle"]],
            [["type": "rectangle", "bounds": ["x": 395, "y": 0, "width": 10, "height": 10]]],
            [["type": "rectangle", "bounds": ["x": -1, "y": 0, "width": 10, "height": 10]]],
            [["type": "rectangle", "bounds": ["x": 0, "y": 0, "width": 0, "height": 10]]],
            [["type": "rectangle", "bounds": bounds, "color": "red"]],
            [["type": "rectangle", "bounds": bounds, "color": "#12345"]],
            [["type": "rectangle", "bounds": bounds, "lineWidth": -1]],
            [["type": "rectangle", "bounds": bounds, "rotation": 1]],
            [["type": "rectangle", "bounds": bounds, "text": "x"]],
            [["type": "pen", "bounds": bounds, "points": [["x": 1, "y": 1]]]],
            [["type": "pen", "bounds": bounds, "points": [["x": 1, "y": 1], ["x": 500, "y": 1]]]],
            [["type": "arrow", "bounds": ["x": 0, "y": 0, "width": 0, "height": 0]]],
            [["type": "text", "bounds": bounds]],
            [["type": "text", "bounds": bounds, "text": "x", "fontSize": 0]],
            [["type": "emoji", "bounds": bounds, "emoji": "ab"]],
            [["type": "redact", "bounds": bounds, "redactionStyle": "smudge"]],
            [["type": "rectangle", "bounds": bounds, "redactionLevel": "light"]],
        ]
        for (index, annotations) in invalid.enumerated() {
            XCTAssertThrowsError(try AutomationAnnotations.parse(annotations, imagePixelSize: imageSize), "case \(index)") { error in
                XCTAssertEqual((error as? AutomationError)?.code, .invalidParams, "case \(index)")
            }
        }
    }

    func testHexColors() throws {
        let rgb = try XCTUnwrap(AutomationAnnotations.hexComponents("#FF8000"))
        XCTAssertEqual(rgb.red, 1)
        XCTAssertEqual(rgb.green, 128.0 / 255, accuracy: 0.0001)
        XCTAssertEqual(rgb.alpha, 1)
        XCTAssertEqual(try XCTUnwrap(AutomationAnnotations.hexComponents("#80FF0000")).alpha, 128.0 / 255, accuracy: 0.0001)
        XCTAssertNil(AutomationAnnotations.hexComponents("FF0000"))
        XCTAssertNil(AutomationAnnotations.hexComponents("#GG0000"))
    }

    @MainActor
    func testRenderWritesNewImageAndLeavesSourceUntouched() throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("AutomationAnnotateTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }

        let sourceURL = directory.appendingPathComponent("source.png")
        let bitmap = try XCTUnwrap(NSBitmapImageRep(
            bitmapDataPlanes: nil, pixelsWide: 400, pixelsHigh: 200, bitsPerSample: 8, samplesPerPixel: 4,
            hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
        ))
        let context = try XCTUnwrap(NSGraphicsContext(bitmapImageRep: bitmap)).cgContext
        context.setFillColor(NSColor.white.cgColor)
        context.fill(CGRect(x: 0, y: 0, width: 400, height: 200))
        try XCTUnwrap(bitmap.representation(using: .png, properties: [:])).write(to: sourceURL)
        let sourceData = try Data(contentsOf: sourceURL)

        let rendered = try AutomationAnnotations.render(
            sourceURL: sourceURL,
            annotationsJSON: [
                ["type": "redact", "bounds": ["x": 0, "y": 0, "width": 100, "height": 100], "redactionStyle": "solid"],
            ] as [Any]
        ) { fileExtension in
            directory.appendingPathComponent("annotated.\(fileExtension)")
        }

        XCTAssertEqual(rendered.count, 1)
        XCTAssertNotEqual(rendered.url, sourceURL)
        XCTAssertEqual(try Data(contentsOf: sourceURL), sourceData)
        let output = try XCTUnwrap(NSBitmapImageRep(data: Data(contentsOf: rendered.url)))
        XCTAssertGreaterThan(output.pixelsWide, 0)
        // The solid redaction covers the top-left corner of the image.
        let corner = try XCTUnwrap(output.colorAt(x: 2, y: 2)?.usingColorSpace(.deviceRGB))
        XCTAssertLessThan(corner.brightnessComponent, 0.1)
        let far = try XCTUnwrap(output.colorAt(x: output.pixelsWide - 3, y: output.pixelsHigh - 3)?.usingColorSpace(.deviceRGB))
        XCTAssertGreaterThan(far.brightnessComponent, 0.9)
    }
}
