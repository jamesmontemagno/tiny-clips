import AppKit
import XCTest
@testable import TinyClips

final class ScreenshotRedactionTests: XCTestCase {
    private let barWidth = 20

    // MARK: - Levels

    func testLevelsGrowStrongerAndFollowDensityAndTextSize() {
        let line = CGSize(width: 300, height: 30)
        let cells = RedactionBlurPreset.allCases.map { $0.cellSize(forRegion: line, pixelDensity: 1) }
        XCTAssertEqual(cells, cells.sorted())
        XCTAssertEqual(Set(cells).count, 3)

        XCTAssertEqual(RedactionBlurPreset.medium.cellSize(forRegion: line, pixelDensity: 2), 10)
        XCTAssertGreaterThan(
            RedactionBlurPreset.medium.cellSize(forRegion: CGSize(width: 800, height: 100), pixelDensity: 2),
            RedactionBlurPreset.medium.cellSize(forRegion: line, pixelDensity: 2)
        )
        // A huge selection stays capped instead of collapsing into one flat color.
        XCTAssertEqual(RedactionBlurPreset.heavy.cellSize(forRegion: CGSize(width: 4000, height: 4000), pixelDensity: 2), 40)
    }

    func testStrengthRoundTripsThroughTheAnnotation() {
        var annotation = ScreenshotAnnotation(tool: .blur, rect: .zero, color: .black, lineWidth: 0, text: "", points: [])
        XCTAssertEqual(annotation.redactionStrength, .medium)
        annotation.redactionStrength = .heavy
        XCTAssertEqual(annotation.redactionBlurPreset, .heavy)
        XCTAssertFalse(annotation.isOpaqueRedaction)
        annotation.redactionStrength = .solid
        XCTAssertTrue(annotation.isOpaqueRedaction)
        XCTAssertEqual(annotation.redactionStrength, .solid)
        annotation.redactionStrength = .light
        XCTAssertFalse(annotation.isOpaqueRedaction)
        XCTAssertEqual(annotation.redactionBlurPreset, .light)
    }

    // MARK: - Renderer

    func testEachLevelRemovesMoreOfTheSourceDetail() throws {
        let source = try makeBars(width: 400, height: 200)
        let rect = CGRect(x: 0.25, y: 0.25, width: 0.5, height: 0.5)
        // Edge clamping preserves broad color changes; measure repeating detail inside the patch.
        let inset = 40
        let sourceSpread = try luminanceSpread(
            of: XCTUnwrap(source.cropping(to: CGRect(x: 100, y: 50, width: 200, height: 100))),
            inset: inset
        )

        let spreads = try RedactionBlurPreset.allCases.map { preset -> CGFloat in
            let patch = try XCTUnwrap(ScreenshotRedactionRenderer.patch(normalizedRect: rect, source: source, preset: preset))
            XCTAssertEqual(patch.image.width, 200)
            XCTAssertEqual(patch.image.height, 100)
            return try luminanceSpread(of: patch.image, inset: inset)
        }

        XCTAssertLessThan(spreads[0], sourceSpread * 0.95, "Light must visibly soften the source")
        XCTAssertLessThan(spreads[1], spreads[0] * 0.8)
        XCTAssertLessThan(spreads[2], spreads[1] * 0.8)
        XCTAssertLessThan(spreads[2], 0.05)
    }

    func testMediumRemovesDetailAtTextScale() throws {
        let source = try makeBars(width: 400, height: 200, barWidth: 3)
        let patch = try XCTUnwrap(ScreenshotRedactionRenderer.patch(
            normalizedRect: CGRect(x: 0, y: 0, width: 1, height: 1),
            source: source,
            preset: .medium
        ))
        XCTAssertLessThan(try luminanceSpread(of: patch.image, inset: 40), 0.02)
    }

    func testPatchCoversWholePixelsInsideTheImage() throws {
        let source = try makeBars(width: 400, height: 200)
        let patch = try XCTUnwrap(ScreenshotRedactionRenderer.patch(
            normalizedRect: CGRect(x: 0.9001, y: 0.4501, width: 0.5, height: 0.1),
            source: source,
            preset: .light
        ))
        XCTAssertEqual(patch.image.width, 40)
        XCTAssertEqual(patch.image.height, 21)
        XCTAssertEqual(patch.normalizedRect.minX, 0.9, accuracy: 0.0001)
        XCTAssertEqual(patch.normalizedRect.maxX, 1, accuracy: 0.0001)
        XCTAssertEqual(patch.normalizedRect.minY, 0.45, accuracy: 0.0001)

        XCTAssertNil(ScreenshotRedactionRenderer.patch(normalizedRect: CGRect(x: 2, y: 2, width: 0.1, height: 0.1), source: source, preset: .light))
    }

    func testPatchIsOpaqueOverTransparentPixels() throws {
        let source = try makeBars(width: 200, height: 100, alpha: { x, _ in x < 100 ? 0 : 1 })
        let patch = try XCTUnwrap(ScreenshotRedactionRenderer.patch(
            normalizedRect: CGRect(x: 0, y: 0, width: 1, height: 1),
            source: source,
            preset: .medium
        ))
        let bitmap = NSBitmapImageRep(cgImage: patch.image)
        for x in stride(from: 0, to: 200, by: 25) {
            let color = try XCTUnwrap(bitmap.colorAt(x: x, y: 50))
            XCTAssertEqual(color.alphaComponent, 1, accuracy: 0.001, "x=\(x)")
        }
    }

    func testPixelatedPatchIsMadeOfFlatBlocks() throws {
        let source = try makeBars(width: 400, height: 200)
        let patch = try XCTUnwrap(ScreenshotRedactionRenderer.patch(
            normalizedRect: CGRect(x: 0, y: 0, width: 1, height: 1),
            source: source,
            preset: .heavy,
            isPixelated: true
        ))
        // 20 px cells across 400 px: every pixel in a block matches, so steps only occur between blocks.
        let bitmap = NSBitmapImageRep(cgImage: patch.image)
        var steps = 0
        for x in 1..<bitmap.pixelsWide {
            let current = try luminance(bitmap, x: x, y: 20)
            let previous = try luminance(bitmap, x: x - 1, y: 20)
            if abs(current - previous) > 0.01 { steps += 1 }
        }
        XCTAssertGreaterThan(steps, 0)
        XCTAssertLessThanOrEqual(steps, 19)
    }

    // MARK: - Editor and export

    @MainActor
    func testExportBlursOnlyTheRedactedAreaAndMatchesThePreviewPixels() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        var annotation = ScreenshotAnnotation(
            tool: .blur,
            rect: CGRect(x: 0.25, y: 0.25, width: 0.5, height: 0.5),
            color: .black, lineWidth: 0, text: "", points: []
        )
        annotation.redactionBlurPreset = .heavy
        editor.annotations = [annotation]

        let output = try XCTUnwrap(editor.renderFinalImage(includesExportDecorations: false))
        XCTAssertEqual(output.pixelsWide, 400)
        XCTAssertEqual(output.pixelsHigh, 200)

        var inside: [CGFloat] = []
        var outside: [CGFloat] = []
        for x in 140..<260 {
            inside.append(try luminance(output, x: x, y: 100))
            outside.append(try luminance(output, x: x, y: 20))
        }
        XCTAssertLessThan(spread(inside), 0.05)
        XCTAssertGreaterThan(spread(outside), 0.9, "Pixels outside the redaction stay sharp")

        let patch = try XCTUnwrap(editor.redactionPatch(for: annotation))
        let preview = NSBitmapImageRep(cgImage: patch.image)
        XCTAssertEqual(
            try luminance(preview, x: 100, y: 50),
            try luminance(output, x: 200, y: 100),
            accuracy: 0.05
        )
    }

    @MainActor
    func testOffCenterRedactionUsesTheMatchingSourceRows() throws {
        let context = try XCTUnwrap(CGContext(
            data: nil, width: 400, height: 200, bitsPerComponent: 8, bytesPerRow: 0,
            space: XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB)),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.setFillColor(NSColor.blue.cgColor)
        context.fill(CGRect(x: 0, y: 0, width: 400, height: 100))
        context.setFillColor(NSColor.red.cgColor)
        context.fill(CGRect(x: 0, y: 100, width: 400, height: 100))
        let (editor, cleanup) = try makeEditor(source: XCTUnwrap(context.makeImage()))
        defer { cleanup() }
        let annotation = ScreenshotAnnotation(
            tool: .blur,
            rect: CGRect(x: 0.25, y: 0.1, width: 0.5, height: 0.3),
            color: .black, lineWidth: 0, text: "", points: []
        )
        editor.annotations = [annotation]

        let patch = try XCTUnwrap(editor.redactionPatch(for: annotation))
        let preview = NSBitmapImageRep(cgImage: patch.image)
        let output = try XCTUnwrap(editor.renderFinalImage(includesExportDecorations: false))
        for (bitmap, x, y) in [(preview, 100, 30), (output, 200, 50)] {
            let color = try XCTUnwrap(bitmap.colorAt(x: x, y: y)?.usingColorSpace(.sRGB))
            XCTAssertGreaterThan(color.redComponent, 0.95)
            XCTAssertLessThan(color.blueComponent, 0.05)
        }
        let untouched = try XCTUnwrap(output.colorAt(x: 200, y: 150)?.usingColorSpace(.sRGB))
        XCTAssertGreaterThan(untouched.blueComponent, 0.95)
        XCTAssertLessThan(untouched.redComponent, 0.05)
    }

    @MainActor
    func testOverlappingRedactionsKeepTheStrongerPixelsInEitherOrder() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let light = ScreenshotAnnotation(
            tool: .blur,
            rect: CGRect(x: 0.375, y: 0.25, width: 0.5, height: 0.5),
            color: .black, lineWidth: 0, text: "", points: [],
            redactionBlurPreset: .light
        )
        editor.annotations = [light]
        let lightOnly = try XCTUnwrap(editor.renderFinalImage(includesExportDecorations: false))

        for preset in [RedactionBlurPreset.medium, .heavy] {
            for isPixelated in [false, true] {
                let stronger = ScreenshotAnnotation(
                    tool: .blur,
                    rect: CGRect(x: 0.125, y: 0.25, width: 0.5, height: 0.5),
                    color: .black, lineWidth: 0, text: "", points: [],
                    redactionBlurPreset: preset,
                    isPixelatedRedaction: isPixelated
                )
                editor.annotations = [stronger]
                let strongerOnly = try XCTUnwrap(editor.renderFinalImage(includesExportDecorations: false))

                for annotations in [[stronger, light], [light, stronger]] {
                    editor.annotations = annotations
                    XCTAssertEqual(editor.previewAnnotationsInDrawOrder.last?.id, stronger.id)
                    let output = try XCTUnwrap(editor.renderFinalImage(includesExportDecorations: false))
                    for x in stride(from: 155, through: 245, by: 10) {
                        XCTAssertEqual(
                            try luminance(output, x: x, y: 100),
                            try luminance(strongerOnly, x: x, y: 100),
                            accuracy: 0.001,
                            "Overlap must retain \(preset.rawValue) pixels in either annotation order"
                        )
                    }
                    XCTAssertEqual(
                        try luminance(output, x: 300, y: 100),
                        try luminance(lightOnly, x: 300, y: 100),
                        accuracy: 0.001,
                        "Light-only areas must stay unchanged"
                    )
                    let selected = try XCTUnwrap(editor.annotationIndex(at: CGPoint(x: 0.5, y: 0.5)))
                    XCTAssertEqual(editor.annotations[selected].id, stronger.id)
                }
            }
        }
    }

    @MainActor
    func testInProgressLightRedactionCannotCoverAStrongerPreview() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        var stronger = ScreenshotAnnotation(
            tool: .blur,
            rect: CGRect(x: 0.25, y: 0.25, width: 0.5, height: 0.5),
            color: .black, lineWidth: 0, text: "", points: [],
            redactionBlurPreset: .heavy
        )
        let light = ScreenshotAnnotation(
            tool: .blur, rect: stronger.rect,
            color: .black, lineWidth: 0, text: "", points: [],
            redactionBlurPreset: .light
        )
        for strength in [RedactionStrength.medium, .heavy, .solid] {
            stronger.redactionStrength = strength
            for (committed, current) in [(stronger, light), (light, stronger)] {
                editor.annotations = [committed]
                editor.currentAnnotation = current
                XCTAssertEqual(editor.previewAnnotationsInDrawOrder.map(\.id), [light.id, stronger.id])
            }
        }
    }

    @MainActor
    func testSolidLevelCoversInBlackAndCanBeUndone() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        editor.selectedTool = .blur
        editor.redactionStrength = .solid
        editor.handleDragEnd(start: CGPoint(x: 0.25, y: 0.25), end: CGPoint(x: 0.75, y: 0.75))
        XCTAssertEqual(editor.annotations.count, 1)
        XCTAssertTrue(editor.annotations[0].isOpaqueRedaction)

        var output = try XCTUnwrap(editor.renderFinalImage(includesExportDecorations: false))
        XCTAssertEqual(try luminance(output, x: 200, y: 100), 0, accuracy: 0.001)

        editor.selectedTool = .move
        editor.selectedAnnotationIndex = 0
        XCTAssertEqual(editor.selectedRedactionStrength(), .solid)
        XCTAssertTrue(editor.updateSelectedRedactionStrength(.heavy))
        XCTAssertFalse(editor.annotations[0].isOpaqueRedaction)
        output = try XCTUnwrap(editor.renderFinalImage(includesExportDecorations: false))
        XCTAssertGreaterThan(try luminance(output, x: 200, y: 100), 0.1)

        editor.undo()
        XCTAssertTrue(editor.annotations[0].isOpaqueRedaction)
    }

    @MainActor
    func testSolidDefaultOnlyAppliesToTheRedactTool() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        editor.redactionStrength = .solid
        editor.selectedTool = .rectangle
        editor.handleDragEnd(start: CGPoint(x: 0.1, y: 0.1), end: CGPoint(x: 0.4, y: 0.4))
        XCTAssertEqual(editor.annotations.count, 1)
        XCTAssertFalse(editor.annotations[0].isOpaqueRedaction)
    }

    @MainActor
    func testChangingTheImageDropsCachedPixels() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        var annotation = ScreenshotAnnotation(
            tool: .blur,
            rect: CGRect(x: 0, y: 0, width: 0.5, height: 1),
            color: .black, lineWidth: 0, text: "", points: []
        )
        annotation.redactionBlurPreset = .heavy
        editor.annotations = [annotation]
        let before = try XCTUnwrap(editor.redactionPatch(for: annotation))

        editor.originalImage = try NSImage(cgImage: makeBars(width: 400, height: 200, barWidth: 200), size: NSSize(width: 400, height: 200))
        let after = try XCTUnwrap(editor.redactionPatch(for: annotation))
        XCTAssertFalse(before.image === after.image)
    }

    func testAgentRedactionStylesMapToRenderingModes() throws {
        let bounds: [String: Any] = ["x": 0, "y": 0, "width": 100, "height": 100]
        let annotations = try AutomationAnnotations.parse([
            ["type": "redact", "bounds": bounds],
            ["type": "redact", "bounds": bounds, "redactionStyle": "blur", "redactionLevel": "light"],
            ["type": "redact", "bounds": bounds, "redactionStyle": "pixelate"],
            ["type": "redact", "bounds": bounds, "redactionStyle": "solid"],
        ] as [Any], imagePixelSize: CGSize(width: 400, height: 200))
        XCTAssertEqual(annotations.map(\.redactionStrength), [.medium, .light, .medium, .solid])
        XCTAssertEqual(annotations.map(\.isPixelatedRedaction), [false, false, true, false])
    }

    // MARK: - Helpers

    @MainActor
    private func makeEditor(source: CGImage? = nil) throws -> (ScreenshotEditorViewModel, () -> Void) {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("ScreenshotRedactionTests-\(UUID().uuidString).png")
        let bitmap = try NSBitmapImageRep(cgImage: source ?? makeBars(width: 400, height: 200))
        try XCTUnwrap(bitmap.representation(using: .png, properties: [:])).write(to: url)
        let editor = ScreenshotEditorViewModel(url: url)
        XCTAssertNotNil(editor.originalImage)
        return (editor, { try? FileManager.default.removeItem(at: url) })
    }

    private func makeBars(
        width: Int,
        height: Int,
        barWidth: Int? = nil,
        alpha: (Int, Int) -> CGFloat = { _, _ in 1 }
    ) throws -> CGImage {
        let bar = barWidth ?? self.barWidth
        let context = try XCTUnwrap(CGContext(
            data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
            space: XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB)),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        for x in 0..<width {
            let brightness: CGFloat = (x / bar) % 2 == 0 ? 0 : 1
            context.setFillColor(CGColor(gray: brightness, alpha: alpha(x, 0)))
            context.setBlendMode(.copy)
            context.fill(CGRect(x: x, y: 0, width: 1, height: height))
        }
        return try XCTUnwrap(context.makeImage())
    }

    private func luminance(_ bitmap: NSBitmapImageRep, x: Int, y: Int) throws -> CGFloat {
        let color = try XCTUnwrap(bitmap.colorAt(x: x, y: y)?.usingColorSpace(.sRGB))
        return 0.2126 * color.redComponent + 0.7152 * color.greenComponent + 0.0722 * color.blueComponent
    }

    private func luminanceSpread(of image: CGImage, inset: Int = 0) throws -> CGFloat {
        let bitmap = NSBitmapImageRep(cgImage: image)
        var values: [CGFloat] = []
        for x in inset..<(bitmap.pixelsWide - inset) {
            values.append(try luminance(bitmap, x: x, y: bitmap.pixelsHigh / 2))
        }
        return spread(values)
    }

    private func spread(_ values: [CGFloat]) -> CGFloat {
        (values.max() ?? 0) - (values.min() ?? 0)
    }
}
