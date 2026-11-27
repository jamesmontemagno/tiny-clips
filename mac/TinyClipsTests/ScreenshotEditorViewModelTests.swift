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
        XCTAssertEqual(editor.selectedAnnotationIndex, 1)
        XCTAssertEqual(editor.selectedTool, .pencil)
        XCTAssertTrue(editor.hasUnsavedChanges)
    }

    @MainActor
    func testEmojiClicksPlaceNewStickersButDraggingOrClickingTheSelectionDoesNot() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        // Placing records a recent emoji in the app's defaults; keep the developer's list.
        let recentKey = "screenshotEditor.recentEmoji"
        let savedRecent = UserDefaults.standard.object(forKey: recentKey)
        defer { UserDefaults.standard.set(savedRecent, forKey: recentKey) }

        editor.selectedTool = .emoji
        let center = CGPoint(x: 0.5, y: 0.5)
        editor.handleDragEnd(start: center, end: center)
        editor.handleDragEnd(start: center, end: center)
        let moved = CGPoint(x: 0.7, y: 0.3)
        editor.handleDrag(start: center, current: CGPoint(x: 0.65, y: 0.4))
        editor.handleDragEnd(start: center, end: moved)
        XCTAssertEqual(editor.annotations.count, 1)
        let sticker = try XCTUnwrap(editor.annotations.first)
        XCTAssertEqual(sticker.rect.midX, moved.x, accuracy: 1e-9)
        XCTAssertEqual(sticker.rect.midY, moved.y, accuracy: 1e-9)
        XCTAssertEqual(editor.selectedAnnotationIndex, 0)
        XCTAssertTrue(editor.showsRotationControl)
        XCTAssertEqual(editor.selectedTool, .emoji)

        editor.handleDragEnd(start: center, end: center)
        XCTAssertEqual(editor.selectedAnnotationIndex, 1)
        XCTAssertTrue(editor.deleteSelectedAnnotation())
        XCTAssertEqual(editor.annotations.map(\.id), [sticker.id])
        XCTAssertFalse(editor.canDeleteSelectedAnnotation)
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
        let placed = editor.annotations[0]

        XCTAssertEqual(editor.selectedTool, .text)
        XCTAssertEqual(editor.selectedAnnotationIndex, 0)
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
    func testDrawingOverAnOlderAnnotationDoesNotPickItOrChangeTheToolDefaults() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        editor.selectedTool = .rectangle
        editor.handleDragEnd(start: CGPoint(x: 0.1, y: 0.1), end: CGPoint(x: 0.4, y: 0.4))
        XCTAssertTrue(editor.updateSelectedShapeColor(.blue))
        let first = try XCTUnwrap(editor.annotations.first)
        editor.handleDragEnd(start: CGPoint(x: 0.6, y: 0.6), end: CGPoint(x: 0.8, y: 0.8))
        XCTAssertEqual(editor.selectedAnnotationIndex, 1)
        XCTAssertEqual(editor.selectedShapeColor(), .red)

        // Start on the older item, cross the current selection, and release elsewhere.
        let start = CGPoint(x: 0.2, y: 0.2)
        editor.handleDrag(start: start, current: CGPoint(x: 0.7, y: 0.7))
        editor.handleDragEnd(start: start, end: CGPoint(x: 0.9, y: 0.5))
        XCTAssertEqual(editor.annotations.count, 3)
        XCTAssertEqual(editor.selectedAnnotationIndex, 2)
        XCTAssertEqual(editor.annotations.first?.rect, first.rect)
        XCTAssertEqual(editor.annotations.first?.color, .blue)

        editor.selectedTool = .move
        let olderOnly = CGPoint(x: 0.15, y: 0.15)
        editor.handleDragEnd(start: olderOnly, end: olderOnly)
        XCTAssertEqual(editor.selectedAnnotationIndex, 0)
    }

    @MainActor
    func testNewEllipseCornerAndRotationGripsUseDisplayedCoordinates() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let size = CGSize(width: 200, height: 100)
        editor.selectedTool = .circle
        let start = CGPoint(x: 0.2, y: 0.1)
        let corner = CGPoint(x: 0.6, y: 0.6)
        let newCorner = CGPoint(x: 0.8, y: 0.7)
        editor.handleDragEnd(start: start, end: corner, displaySize: size)
        editor.handleDragEnd(start: corner, end: newCorner, displaySize: size)
        let rect = try XCTUnwrap(editor.annotations.first?.rect)
        XCTAssertEqual(rect.maxX, newCorner.x, accuracy: 1e-9)
        XCTAssertEqual(rect.maxY, newCorner.y, accuracy: 1e-9)
        let grip = RotatableAnnotationGeometry.rotationHandle(for: rect, rotation: 0, in: size)
        XCTAssertLessThan(grip.y, 0) // Visible in the viewport margin, outside the image.
        editor.handleDragEnd(
            start: CGPoint(x: grip.x / size.width, y: grip.y / size.height),
            end: CGPoint(x: rect.maxX, y: rect.midY), displaySize: size
        )
        XCTAssertEqual(try XCTUnwrap(editor.selectedAnnotationRotationDegrees()), 90, accuracy: 0.001)
        XCTAssertEqual(editor.selectedTool, .circle)
        XCTAssertEqual(editor.annotations.count, 1)
    }

    @MainActor
    func testFreshArrowBendsAndMovesEndpointsWithoutDrawingAnother() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        editor.selectedTool = .arrow
        let tail = CGPoint(x: 0.2, y: 0.5), head = CGPoint(x: 0.8, y: 0.5)
        editor.handleDragEnd(start: tail, end: head)
        editor.handleDragEnd(start: CGPoint(x: 0.5, y: 0.5), end: CGPoint(x: 0.5, y: 0.8))
        XCTAssertNotEqual(try XCTUnwrap(editor.selectedArrowShape()).bend, 0)
        XCTAssertEqual(editor.arrowShape.bend, 0)
        let moved = CGPoint(x: 0.9, y: 0.25)
        editor.handleDragEnd(start: head, end: moved)
        XCTAssertEqual(editor.annotations.first?.points, [tail, moved])
        XCTAssertEqual(editor.annotations.count, 1)
        XCTAssertEqual(editor.selectedTool, .arrow)
    }

    @MainActor
    func testCancelledOrEmptyDragsDoNotCommitOrLeaveStaleEditingState() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let start = CGPoint(x: 0.1, y: 0.1), end = CGPoint(x: 0.6, y: 0.6)
        editor.selectedTool = .rectangle
        editor.handleDragEnd(start: start, end: start)
        editor.handleDragEnd(start: start, end: CGPoint(x: 0.6, y: start.y))
        XCTAssertFalse(editor.canUndo)
        editor.handleDrag(start: start, current: end)
        editor.selectedTool = .number
        editor.handleDragEnd(start: start, end: end)
        XCTAssertTrue(editor.annotations.isEmpty)
        XCTAssertNil(editor.currentAnnotation)

        editor.handleDragEnd(start: end, end: end)
        let badge = try XCTUnwrap(editor.annotations.first)
        editor.markSaved()
        editor.handleDrag(start: end, current: start)
        editor.finishCancelledGesture()
        XCTAssertEqual(editor.annotations.first?.rect, badge.rect)
        XCTAssertFalse(editor.hasUnsavedChanges)
        editor.handleDrag(start: start, current: end)
        editor.handleDragEnd(start: start, end: start)
        XCTAssertEqual(editor.nextNumberLabel, 2) // A drag returning to its origin isn't a click.
        editor.handleDragEnd(start: start, end: start)
        XCTAssertEqual(editor.annotations.map(\.text), ["1", "2"])
        XCTAssertEqual(editor.selectedAnnotationIndex, 1)
    }

    @MainActor
    func testTextClickAwayStartsDraftAndCancelOrEmptySubmitDoesNotPlaceText() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        editor.selectedTool = .text
        let start = CGPoint(x: 0.25, y: 0.4), other = CGPoint(x: 0.8, y: 0.7)
        editor.handleDragEnd(start: start, end: start)
        editor.textEditValue = "Example"
        editor.commitTextAnnotation()
        let placed = try XCTUnwrap(editor.annotations.first)
        let center = CGPoint(x: placed.rect.midX, y: placed.rect.midY)
        editor.handleDragEnd(start: center, end: center)
        XCTAssertFalse(editor.isEditingText)
        XCTAssertEqual(editor.selectedAnnotationIndex, 0)
        editor.handleDragEnd(start: other, end: other)
        XCTAssertTrue(editor.isEditingText)
        XCTAssertFalse(editor.canDeleteSelectedAnnotation)
        editor.textEditValue = "Discard"
        editor.cancelTextAnnotation()
        editor.handleDragEnd(start: other, end: other)
        editor.commitTextAnnotation()
        XCTAssertFalse(editor.isEditingText)
        XCTAssertEqual(editor.annotations.map(\.id), [placed.id])
        XCTAssertEqual(editor.selectedAnnotationIndex, 0)
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
