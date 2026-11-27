import AppKit
import SwiftUI
import XCTest
@testable import TinyClips

final class ScreenshotEditorViewModelTests: XCTestCase {
    @MainActor
    func testSwitchingToolsClearsTheSelection() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        editor.selectedTool = .rectangle
        editor.handleDragEnd(start: CGPoint(x: 0.1, y: 0.1), end: CGPoint(x: 0.4, y: 0.4))
        editor.selectedTool = .move
        editor.selectedAnnotationIndex = 0
        XCTAssertNotNil(editor.selectedStrokeColor())

        editor.selectedTool = .pencil

        XCTAssertNil(editor.selectedAnnotationIndex)
        XCTAssertNil(editor.selectedStrokeColor())
        XCTAssertFalse(editor.updateSelectedStrokeColor(.blue))
        XCTAssertEqual(editor.annotations[0].color, .red)
    }

    @MainActor
    func testPenDrawsInItsOwnColorAfterTextWasSelected() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        editor.annotations = [ScreenshotAnnotation(
            tool: .text,
            rect: CGRect(x: 0.1, y: 0.1, width: 0.2, height: 0.05),
            color: .red, lineWidth: 4, text: "Hi", points: []
        )]
        editor.selectedTool = .move
        editor.selectedAnnotationIndex = 0
        editor.selectedTool = .pencil
        XCTAssertNil(editor.selectedTextColor())
        XCTAssertFalse(editor.hasUnsavedChanges)

        editor.selectedColor = .blue
        let start = CGPoint(x: 0.5, y: 0.5)
        editor.handleDrag(start: start, current: CGPoint(x: 0.6, y: 0.55))
        editor.handleDrag(start: start, current: CGPoint(x: 0.7, y: 0.6))
        editor.handleDragEnd(start: start, end: CGPoint(x: 0.7, y: 0.6))

        XCTAssertEqual(editor.annotations.count, 2)
        XCTAssertEqual(editor.annotations[0].color, .red)
        XCTAssertEqual(editor.annotations[1].tool, .pencil)
        XCTAssertEqual(editor.annotations[1].color, .blue)
        XCTAssertTrue(editor.hasUnsavedChanges)
    }

    @MainActor
    func testPlacingAnEmojiLeavesNothingSelected() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        // Placing records a recent emoji in the app's defaults; keep the developer's list.
        let recentKey = "screenshotEditor.recentEmoji"
        let savedRecent = UserDefaults.standard.object(forKey: recentKey)
        defer { UserDefaults.standard.set(savedRecent, forKey: recentKey) }

        editor.selectedTool = .emoji
        editor.placeEmojiAnnotation(at: CGPoint(x: 0.5, y: 0.5))
        XCTAssertEqual(editor.annotations.count, 1)
        XCTAssertNil(editor.selectedAnnotationIndex)
    }

    @MainActor
    func testPointerPlacementKeepsHotspotAtTheImageEdgeAndUndoRestoresAppearance() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        editor.selectedTool = .pointer
        editor.pointerDefault.style = .hand
        editor.placePointerAnnotation(at: CGPoint(x: 0, y: 0))
        let placed = try XCTUnwrap(editor.annotations.first)
        let hotspot = PointerGeometry.hotspot(for: .hand)
        XCTAssertEqual((placed.rect.minX + placed.rect.width * hotspot.x) * 400, 0, accuracy: 1e-8)
        XCTAssertEqual((placed.rect.minY + placed.rect.height * hotspot.y) * 200, 0, accuracy: 1e-8)
        XCTAssertNil(editor.selectedAnnotationIndex)

        editor.selectedTool = .move
        editor.selectedAnnotationIndex = 0
        editor.setAdjustingPointer(true)
        var appearance = editor.pointerForInspector()
        appearance.opacity = 0.6
        editor.updatePointer(appearance)
        appearance.opacity = 0.3
        editor.updatePointer(appearance)
        editor.setAdjustingPointer(false)
        editor.undo()
        XCTAssertEqual(editor.annotations[0].pointer, placed.pointer)
        editor.redo()
        XCTAssertEqual(editor.annotations[0].pointer.opacity, 0.3, accuracy: 1e-8)
        XCTAssertEqual(editor.pointerDefault.opacity, 1)
    }

    @MainActor
    func testPointerExportCompositesOpacityAndShadow() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        var appearance = PointerAppearance()
        appearance.fill = Color(red: 1, green: 0, blue: 0)
        appearance.strokeWidth = 0
        appearance.shadow.enabled = false
        appearance.opacity = 0.5
        editor.annotations = [ScreenshotAnnotation(
            tool: .pointer,
            rect: CGRect(x: 0.25, y: 0.25, width: 0.2, height: 0.4),
            color: .white, lineWidth: 0, text: "", points: [], pointer: appearance
        )]
        let withoutShadow = try XCTUnwrap(editor.renderFinalImage(includesExportDecorations: false))
        let fill = try XCTUnwrap(withoutShadow.colorAt(x: 120, y: 75)?.usingColorSpace(.sRGB))
        XCTAssertGreaterThan(fill.redComponent, 0.95)
        XCTAssertEqual(fill.greenComponent, 0.5, accuracy: 0.08)

        appearance.shadow = PointerShadow(enabled: true, opacity: 1, blur: 0, offsetX: 15, offsetY: 15)
        editor.annotations[0].pointer = appearance
        let withShadow = try XCTUnwrap(editor.renderFinalImage(includesExportDecorations: false))
        var changedPixels = 0
        for y in 50..<155 {
            for x in 100..<200 {
                let before = try XCTUnwrap(withoutShadow.colorAt(x: x, y: y)?.usingColorSpace(.sRGB))
                let after = try XCTUnwrap(withShadow.colorAt(x: x, y: y)?.usingColorSpace(.sRGB))
                if before.redComponent - after.redComponent > 0.2 {
                    changedPixels += 1
                }
            }
        }
        XCTAssertGreaterThan(changedPixels, 100)
    }

    @MainActor
    func testPlacedTextCanBeResizedFromTheInspector() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        editor.selectedTool = .text
        editor.textFontSize = 20
        editor.textEditPosition = CGPoint(x: 0.5, y: 0.5)
        editor.textEditValue = "Hello"
        editor.commitTextAnnotation()
        XCTAssertFalse(editor.updateSelectedTextFontSize(30))
        let placed = editor.annotations[0]

        editor.selectedTool = .move
        editor.selectedAnnotationIndex = 0
        XCTAssertEqual(editor.selectedTextFontSize(), 20)
        XCTAssertTrue(editor.updateSelectedTextFontSize(40))

        let resized = editor.annotations[0]
        XCTAssertEqual(resized.fontSize, 40)
        XCTAssertEqual(resized.rect.width, placed.rect.width * 2, accuracy: 1e-9)
        XCTAssertEqual(resized.rect.height, placed.rect.height * 2, accuracy: 1e-9)
        XCTAssertEqual(resized.rect.midX, placed.rect.midX, accuracy: 1e-9)
        XCTAssertEqual(resized.rect.midY, placed.rect.midY, accuracy: 1e-9)
        XCTAssertEqual(editor.textFontSize, 20)

        editor.undo()
        XCTAssertEqual(editor.annotations[0].fontSize, 20)
        XCTAssertEqual(editor.annotations[0].rect, placed.rect)

        // A slider drag is one undo step.
        editor.selectedAnnotationIndex = 0
        editor.beginSelectedTextStyleEdit()
        editor.updateSelectedTextFontSize(30)
        editor.updateSelectedTextFontSize(36)
        editor.endSelectedTextStyleEdit()
        XCTAssertEqual(editor.annotations[0].fontSize, 36)
        editor.undo()
        XCTAssertEqual(editor.annotations[0].fontSize, 20)
    }

    @MainActor
    func testFillOpacitySliderPreservesColorAndRecordsOneUndoStep() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let original = Color(red: 0.2, green: 0.4, blue: 0.8, opacity: 1)
        editor.annotations = [
            ScreenshotAnnotation(
                tool: .rectangle,
                rect: CGRect(x: 0.1, y: 0.1, width: 0.4, height: 0.4),
                color: .red,
                fillColor: original,
                lineWidth: 4,
                text: "",
                points: []
            )
        ]
        editor.selectedTool = .move
        editor.selectedAnnotationIndex = 0

        editor.setAdjustingSelectedFillOpacity(true)
        XCTAssertTrue(editor.updateSelectedShapeFillColor(
            AnnotationColorOpacity.applying(60, to: original)
        ))
        XCTAssertTrue(editor.updateSelectedShapeFillColor(
            AnnotationColorOpacity.applying(25, to: original)
        ))
        editor.setAdjustingSelectedFillOpacity(false)

        XCTAssertEqual(
            AnnotationColorOpacity.percent(for: editor.annotations[0].fillColor),
            25
        )
        editor.undo()
        XCTAssertEqual(editor.annotations[0].fillColor, original)
    }

    func testFillOpacityKeepsRgbWhenChangingAlpha() {
        let color = Color(red: 0.2, green: 0.4, blue: 0.8)
        let faded = AnnotationColorOpacity.applying(35, to: color)

        let resolved = NSColor(faded).usingColorSpace(.sRGB)!
        XCTAssertEqual(resolved.redComponent, 0.2, accuracy: 0.001)
        XCTAssertEqual(resolved.greenComponent, 0.4, accuracy: 0.001)
        XCTAssertEqual(resolved.blueComponent, 0.8, accuracy: 0.001)
        XCTAssertEqual(AnnotationColorOpacity.percent(for: faded), 35)
    }

    @MainActor
    func testEditorOpensOnTheDefaultToolAndBackgroundDragsDrawNothing() throws {
        let settings = CaptureSettings.shared
        let savedTool = settings.screenshotEditorDefaultTool
        defer { settings.screenshotEditorDefaultTool = savedTool }

        settings.screenshotEditorDefaultEditTool = .arrow
        let (arrowEditor, arrowCleanup) = try makeEditor()
        defer { arrowCleanup() }
        XCTAssertEqual(arrowEditor.selectedTool, .arrow)

        settings.screenshotEditorDefaultEditTool = .background
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        XCTAssertEqual(editor.selectedTool, .background)
        let start = CGPoint(x: 0.2, y: 0.2)
        editor.handleDrag(start: start, current: CGPoint(x: 0.6, y: 0.6))
        XCTAssertNil(editor.currentAnnotation)
        editor.handleDragEnd(start: start, end: CGPoint(x: 0.6, y: 0.6))
        XCTAssertTrue(editor.annotations.isEmpty)
        XCTAssertFalse(editor.hasUnsavedChanges)
    }

    // MARK: - Helpers

    @MainActor
    private func makeEditor() throws -> (ScreenshotEditorViewModel, () -> Void) {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("ScreenshotEditorViewModelTests-\(UUID().uuidString).png")
        let context = try XCTUnwrap(CGContext(
            data: nil, width: 400, height: 200, bitsPerComponent: 8, bytesPerRow: 0,
            space: XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB)),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.setFillColor(CGColor(srgbRed: 1, green: 1, blue: 1, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: 400, height: 200))
        let bitmap = try NSBitmapImageRep(cgImage: XCTUnwrap(context.makeImage()))
        try XCTUnwrap(bitmap.representation(using: .png, properties: [:])).write(to: url)
        let editor = ScreenshotEditorViewModel(url: url)
        XCTAssertNotNil(editor.originalImage)
        return (editor, { try? FileManager.default.removeItem(at: url) })
    }
}
