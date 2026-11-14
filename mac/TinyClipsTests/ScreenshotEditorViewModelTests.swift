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
