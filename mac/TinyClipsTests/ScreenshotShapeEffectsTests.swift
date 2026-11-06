import AppKit
import SwiftUI
import XCTest
@testable import TinyClips

@MainActor
final class ScreenshotShapeEffectsTests: XCTestCase {
    private let red = Color(.sRGB, red: 1, green: 0, blue: 0, opacity: 1)
    private let blue = Color(.sRGB, red: 0, green: 0, blue: 1, opacity: 1)
    private let opaqueBlack = Color(.sRGB, red: 0, green: 0, blue: 0, opacity: 1)

    // MARK: - Agent fields

    func testParsesGradientAndShadowInImagePixels() throws {
        let bounds = ["x": 10, "y": 10, "width": 100, "height": 50]
        let annotations = try AutomationAnnotations.parse([
            [
                "type": "rectangle", "bounds": bounds, "fillColor": "#FF0000", "fillEndColor": "#0000FF",
                "fillDirection": 90, "shadowBlur": 20, "shadowColor": "#80112233",
            ],
            ["type": "ellipse", "bounds": bounds, "fillColor": "#FF0000", "fillEndColor": "#0000FF", "shadowBlur": 0],
            ["type": "rectangle", "bounds": bounds],
        ], imagePixelSize: CGSize(width: 1800, height: 900))

        let full = annotations[0].shapeEffects
        XCTAssertTrue(full.isGradientFill)
        XCTAssertEqual(full.gradientDirectionDegrees, 90)
        XCTAssertTrue(full.drawsShadow)
        // 1800 pixels wide doubles the stroke scale, so 20 image pixels is stored as 10 units.
        XCTAssertEqual(full.shadowBlur, 10, accuracy: 0.0001)

        XCTAssertEqual(annotations[1].shapeEffects.gradientDirectionDegrees, 45)
        XCTAssertFalse(annotations[1].shapeEffects.drawsShadow)
        XCTAssertFalse(annotations[2].shapeEffects.isGradientFill)
        XCTAssertFalse(annotations[2].shapeEffects.drawsShadow)
    }

    func testRejectsInvalidEffectFields() {
        let bounds = ["x": 10, "y": 10, "width": 100, "height": 50]
        let invalid: [[String: Any]] = [
            ["type": "arrow", "bounds": bounds, "shadowBlur": 4],
            ["type": "text", "bounds": bounds, "text": "x", "fillEndColor": "#0000FF"],
            ["type": "rectangle", "bounds": bounds, "fillEndColor": "#0000FF"],
            ["type": "rectangle", "bounds": bounds, "fillColor": "#00FF0000", "fillEndColor": "#0000FF"],
            ["type": "rectangle", "bounds": bounds, "fillColor": "#FF0000", "fillDirection": 45],
            ["type": "rectangle", "bounds": bounds, "fillColor": "#FF0000", "fillEndColor": "#0000FF", "fillDirection": 361],
            ["type": "rectangle", "bounds": bounds, "fillColor": "#FF0000", "fillEndColor": "nope"],
            ["type": "rectangle", "bounds": bounds, "shadowBlur": -1],
            ["type": "rectangle", "bounds": bounds, "shadowBlur": 501],
            ["type": "rectangle", "bounds": bounds, "shadowColor": "#000000"],
            ["type": "rectangle", "bounds": bounds, "shadowBlur": 4, "shadowColor": "black"],
        ]
        for (index, annotation) in invalid.enumerated() {
            XCTAssertThrowsError(
                try AutomationAnnotations.parse([annotation], imagePixelSize: CGSize(width: 400, height: 200)),
                "case \(index)"
            ) { error in
                XCTAssertEqual((error as? AutomationError)?.code, .invalidParams, "case \(index)")
            }
        }
    }

    // MARK: - Export pixels

    func testGradientRunsFromFillToEndColorAlongItsDirection() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }

        editor.annotations = [gradientShape(degrees: 0)]
        var bitmap = try XCTUnwrap(editor.renderFinalImage(includesExportDecorations: false))
        assertColor(bitmap, x: 104, y: 100, red: 1, blue: 0)
        assertColor(bitmap, x: 296, y: 100, red: 0, blue: 1)

        editor.annotations = [gradientShape(degrees: 90)]
        bitmap = try XCTUnwrap(editor.renderFinalImage(includesExportDecorations: false))
        assertColor(bitmap, x: 200, y: 54, red: 1, blue: 0)
        assertColor(bitmap, x: 200, y: 146, red: 0, blue: 1)
    }

    func testGradientRotatesWithTheShape() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }

        var shape = gradientShape(degrees: 0)
        shape.rotation = .pi / 2
        editor.annotations = [shape]
        let bitmap = try XCTUnwrap(editor.renderFinalImage(includesExportDecorations: false))

        // Turned a quarter clockwise, the shape's left edge is now at the top.
        assertColor(bitmap, x: 200, y: 10, red: 1, blue: 0)
        assertColor(bitmap, x: 200, y: 190, red: 0, blue: 1)
    }

    func testShadowFallsDownEvenWhenTheShapeIsRotated() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }

        for rotation in [CGFloat(0), .pi / 2, .pi] {
            var shape = shadowShape(blur: 20)
            shape.rect = CGRect(x: 0.4, y: 0.3, width: 0.2, height: 0.2)
            shape.rotation = rotation
            let corners = RotatableAnnotationGeometry.corners(of: shape.rect, rotation: rotation, in: imageSize)
            let top = try XCTUnwrap(corners.map(\.y).min())
            let bottom = try XCTUnwrap(corners.map(\.y).max())
            editor.annotations = [shape]
            let bitmap = try XCTUnwrap(editor.renderFinalImage(includesExportDecorations: false))

            let above = try luminance(bitmap, x: 200, y: Int(top.rounded()) - 5)
            let below = try luminance(bitmap, x: 200, y: Int(bottom.rounded()) + 5)
            XCTAssertLessThan(below, above - 0.1, "rotation \(rotation)")
        }
    }

    func testShadowBlurIsTwiceTheStandardDeviation() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }

        editor.annotations = [shadowShape(blur: 20)]
        let bitmap = try XCTUnwrap(editor.renderFinalImage(includesExportDecorations: false))
        assertShadowProfile { try luminance(bitmap, x: 200, y: $0) }
    }

    func testShadowFollowsTheStrokeScaleOnLargeImages() throws {
        let (editor, cleanup) = try makeEditor(width: 1800, height: 1200)
        defer { cleanup() }

        // A stored blur of 20 is 40 pixels at twice the reference width: half the shadow's strength
        // sits 14 pixels below the shape, not 7.
        editor.annotations = [shadowShape(blur: 20)]
        let bitmap = try XCTUnwrap(editor.renderFinalImage(includesExportDecorations: false))
        let shapeBottom = Int(shadowShape(blur: 20).rect.maxY * 1200)
        let strength = try 1 - luminance(bitmap, x: 900, y: shapeBottom + 2 + 14)
        XCTAssertEqual(strength, 0.5, accuracy: 0.08)
    }

    func testShapeWithoutEffectsCastsNoShadow() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }

        var shape = shadowShape(blur: 20)
        shape.shapeEffects.hasShadow = false
        editor.annotations = [shape]
        let bitmap = try XCTUnwrap(editor.renderFinalImage(includesExportDecorations: false))
        XCTAssertEqual(try luminance(bitmap, x: 200, y: 160), 1, accuracy: 0.001)
    }

    // MARK: - Preview

    func testPreviewMatchesExport() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }

        var gradient = gradientShape(degrees: 30)
        gradient.shapeEffects.hasShadow = true
        gradient.shapeEffects.shadowBlur = 24
        gradient.shapeEffects.shadowColor = opaqueBlack
        var ellipse = ScreenshotAnnotation(
            tool: .circle, rect: CGRect(x: 0.1, y: 0.55, width: 0.3, height: 0.3), color: blue,
            fillColor: .clear, lineWidth: 6, text: "", points: []
        )
        ellipse.rotation = .pi / 5
        ellipse.shapeEffects.hasShadow = true
        ellipse.shapeEffects.shadowBlur = 16

        for shape in [gradient, ellipse] {
            editor.annotations = [shape]
            let exported = try XCTUnwrap(editor.renderFinalImage(includesExportDecorations: false))
            let preview = try renderPreview(of: shape)

            var total: CGFloat = 0
            var samples: CGFloat = 0
            for y in stride(from: 0, to: Int(imageSize.height), by: 2) {
                for x in stride(from: 0, to: Int(imageSize.width), by: 2) {
                    total += try abs(luminance(exported, x: x, y: y) - luminance(preview, x: x, y: y))
                    samples += 1
                }
            }
            XCTAssertLessThan(total / samples, 0.01, "tool \(shape.tool)")
        }

        let preview = try renderPreview(of: shadowShape(blur: 20))
        assertShadowProfile { try luminance(preview, x: 200, y: $0) }
    }

    // MARK: - Editing

    func testNewShapesTakeTheCurrentEffectsAndOtherToolsDoNot() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }

        editor.updateShapeEffects { $0.hasShadow = true }
        XCTAssertFalse(editor.hasUnsavedChanges)

        editor.selectedTool = .rectangle
        editor.handleDragEnd(start: CGPoint(x: 0.1, y: 0.1), end: CGPoint(x: 0.5, y: 0.5))
        editor.selectedTool = .arrow
        editor.handleDragEnd(start: CGPoint(x: 0.1, y: 0.1), end: CGPoint(x: 0.5, y: 0.5))

        XCTAssertTrue(editor.annotations[0].shapeEffects.hasShadow)
        XCTAssertFalse(editor.annotations[1].shapeEffects.hasShadow)
    }

    func testEditingASelectedShapeIsOneUndoStepAndLeavesDefaultsAlone() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }

        editor.annotations = [gradientShape(degrees: 0)]
        editor.selectedTool = .move
        editor.selectedAnnotationIndex = 0

        editor.updateShapeEffects { $0.hasShadow = true }
        XCTAssertTrue(editor.annotations[0].shapeEffects.hasShadow)
        XCTAssertFalse(editor.shapeEffects.hasShadow)
        XCTAssertTrue(editor.hasUnsavedChanges)

        editor.undo()
        XCTAssertFalse(editor.annotations[0].shapeEffects.hasShadow)
    }

    func testSliderDragIsOneUndoStep() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }

        var shape = shadowShape(blur: 10)
        shape.shapeEffects.gradientDirectionDegrees = 10
        editor.annotations = [shape]
        editor.selectedTool = .move
        editor.selectedAnnotationIndex = 0

        editor.beginSelectedShapeStyleEdit()
        for blur in [CGFloat(12), 14, 16] {
            editor.updateShapeEffects { $0.shadowBlur = blur }
        }
        editor.endSelectedShapeStyleEdit()
        XCTAssertEqual(editor.annotations[0].shapeEffects.shadowBlur, 16)

        editor.undo()
        XCTAssertEqual(editor.annotations[0].shapeEffects.shadowBlur, 10)
    }

    func testFillEditsTheSelectedShape() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }

        editor.annotations = [gradientShape(degrees: 0)]
        editor.selectedTool = .move
        editor.selectedAnnotationIndex = 0

        editor.updateShapeFillColor(.clear)
        XCTAssertEqual(editor.annotations[0].fillColor, .clear)
        XCTAssertEqual(editor.selectedFillColor, .clear)
        editor.undo()
        XCTAssertEqual(editor.annotations[0].fillColor, red)

        editor.selectedAnnotationIndex = nil
        editor.selectedTool = .rectangle
        editor.updateShapeFillColor(blue)
        XCTAssertEqual(editor.selectedFillColor, blue)
        XCTAssertEqual(editor.annotations[0].fillColor, red)
    }

    // MARK: - Helpers

    private let imageSize = CGSize(width: 400, height: 200)

    /// A 200 by 100 shape at (100, 50), in the image `makeEditor` makes by default.
    private func gradientShape(degrees: Double) -> ScreenshotAnnotation {
        var shape = ScreenshotAnnotation(
            tool: .rectangle, rect: CGRect(x: 0.25, y: 0.25, width: 0.5, height: 0.5), color: red,
            fillColor: red, lineWidth: 2, text: "", points: []
        )
        shape.shapeEffects.isGradientFill = true
        shape.shapeEffects.gradientEndColor = blue
        shape.shapeEffects.gradientDirectionDegrees = degrees
        return shape
    }

    /// An opaque black shape casting an opaque black shadow, whose bottom edge is at y = 100.
    private func shadowShape(blur: CGFloat) -> ScreenshotAnnotation {
        var shape = ScreenshotAnnotation(
            tool: .rectangle, rect: CGRect(x: 0.25, y: 0.1, width: 0.5, height: 0.4), color: opaqueBlack,
            fillColor: opaqueBlack, lineWidth: 2, text: "", points: []
        )
        shape.shapeEffects.hasShadow = true
        shape.shapeEffects.shadowBlur = blur
        shape.shapeEffects.shadowColor = opaqueBlack
        return shape
    }

    /// Checks `shadowShape(blur: 20)` on white: the shadow's edge is half strength, and one standard
    /// deviation (10 pixels) past it the strength follows the Gaussian's tail.
    private func assertShadowProfile(_ luminanceAtY: (Int) throws -> CGFloat, file: StaticString = #filePath, line: UInt = #line) {
        // Shape bottom 100, half the 2-pixel stroke, and a 7-pixel offset.
        let edge = 108
        XCTAssertEqual(1 - (try luminanceAtY(edge)), 0.5, accuracy: 0.08, file: file, line: line)
        XCTAssertEqual(1 - (try luminanceAtY(edge + 10)), 0.159, accuracy: 0.06, file: file, line: line)
    }

    private func renderPreview(of annotation: ScreenshotAnnotation) throws -> NSBitmapImageRep {
        let size = imageSize
        let rect = CGRect(
            x: annotation.rect.minX * size.width,
            y: annotation.rect.minY * size.height,
            width: annotation.rect.width * size.width,
            height: annotation.rect.height * size.height
        )
        let view = Canvas { context, _ in
            context.fill(Path(CGRect(origin: .zero, size: size)), with: .color(.white))
            ShapeEffectsRenderer.drawPreview(annotation, in: context, rect: rect, lineWidth: annotation.lineWidth, effectScale: 1)
        }
        .frame(width: size.width, height: size.height)

        let renderer = ImageRenderer(content: view)
        renderer.scale = 1
        return NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
    }

    private func makeEditor(width: Int = 400, height: Int = 200) throws -> (ScreenshotEditorViewModel, () -> Void) {
        let context = try XCTUnwrap(CGContext(
            data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
            space: XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB)),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.setFillColor(CGColor(gray: 1, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))

        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("ScreenshotShapeEffectsTests-\(UUID().uuidString).png")
        let bitmap = NSBitmapImageRep(cgImage: try XCTUnwrap(context.makeImage()))
        try XCTUnwrap(bitmap.representation(using: .png, properties: [:])).write(to: url)
        let editor = ScreenshotEditorViewModel(url: url)
        XCTAssertNotNil(editor.originalImage)
        return (editor, { try? FileManager.default.removeItem(at: url) })
    }

    private func luminance(_ bitmap: NSBitmapImageRep, x: Int, y: Int) throws -> CGFloat {
        let color = try XCTUnwrap(bitmap.colorAt(x: x, y: y)?.usingColorSpace(.sRGB))
        return 0.2126 * color.redComponent + 0.7152 * color.greenComponent + 0.0722 * color.blueComponent
    }

    private func assertColor(_ bitmap: NSBitmapImageRep, x: Int, y: Int, red: CGFloat, blue: CGFloat, file: StaticString = #filePath, line: UInt = #line) {
        guard let color = bitmap.colorAt(x: x, y: y)?.usingColorSpace(.sRGB) else {
            XCTFail("No pixel at \(x), \(y)", file: file, line: line)
            return
        }
        XCTAssertEqual(color.redComponent, red, accuracy: 0.1, file: file, line: line)
        XCTAssertEqual(color.blueComponent, blue, accuracy: 0.1, file: file, line: line)
    }
}
