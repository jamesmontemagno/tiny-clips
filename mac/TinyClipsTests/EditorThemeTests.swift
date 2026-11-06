import AppKit
import SwiftUI
import XCTest
@testable import TinyClips

@MainActor
final class EditorThemeTests: XCTestCase {
    private static var repoRoot: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
    }

    private struct InvalidFixture: Decodable {
        struct Case: Decodable {
            var name: String
            var field: String
        }
        var cases: [Case]
    }

    private func fixtureData(_ name: String) throws -> Data {
        try Data(contentsOf: Self.repoRoot.appendingPathComponent("shared/editor-themes/fixtures/\(name)"))
    }

    private func brandTheme() throws -> EditorTheme {
        try EditorTheme.parse(try fixtureData("brand-theme.json"))
    }

    // MARK: - Format

    func testFixturesRoundTrip() throws {
        for name in ["default-theme.json", "brand-theme.json"] {
            let data = try fixtureData(name)
            let theme = try EditorTheme.parse(data)
            let encoded = try XCTUnwrap(JSONSerialization.jsonObject(with: try theme.encodedData()) as? NSDictionary)
            let expected = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? NSDictionary)
            XCTAssertEqual(encoded, expected, name)
        }
    }

    func testMissingFieldsExpandToTheDefaultTheme() throws {
        let expanded = try EditorTheme.parse(Data(#"{ "id": "default", "name": "Default" }"#.utf8))
        XCTAssertEqual(expanded, try EditorTheme.parse(try fixtureData("default-theme.json")))
    }

    func testEveryInvalidFixtureNamesItsField() throws {
        let data = try fixtureData("invalid-themes.json")
        let fixture = try JSONDecoder().decode(InvalidFixture.self, from: data)
        let root = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        let cases = try XCTUnwrap(root["cases"] as? [[String: Any]])
        XCTAssertEqual(cases.count, fixture.cases.count)
        XCTAssertFalse(cases.isEmpty)

        for (testCase, raw) in zip(fixture.cases, cases) {
            XCTAssertThrowsError(try EditorTheme.parse(jsonObject: try XCTUnwrap(raw["theme"])), testCase.name) { error in
                XCTAssertEqual((error as? EditorThemeValidationError)?.field, testCase.field, testCase.name)
                XCTAssertTrue(error.localizedDescription.contains(testCase.field), testCase.name)
            }
        }
    }

    func testNormalizesColorsAndName() throws {
        let theme = try EditorTheme.parse(Data(#"""
        { "id": "a", "name": "  Acme  ", "line": { "color": "#a0b0c0" }, "number": { "textColor": "#80abcdef" } }
        """#.utf8))
        XCTAssertEqual(theme.name, "Acme")
        XCTAssertEqual(theme.line.color, "#FFA0B0C0")
        XCTAssertEqual(theme.number.textColor, "#80ABCDEF")
    }

    func testOmittedIDGetsANewValidOne() throws {
        let theme = try EditorTheme.parse(Data(#"{ "name": "A" }"#.utf8))
        XCTAssertFalse(theme.id.isEmpty)
        XCTAssertNoThrow(try theme.validated())
    }

    func testColorsRoundTripThroughEditorColors() {
        for hex in ["#FF102030", "#80405060", "#F0112233", "#59000000", "#01FFFFFF"] {
            XCTAssertEqual(EditorThemeColor.hex(EditorThemeColor.color(hex)), hex)
        }
        XCTAssertEqual(EditorThemeColor.color("#00000000"), .clear)
        XCTAssertEqual(EditorThemeColor.hex(.clear), "#00000000")
    }

    func testNamesMapToEditorEnumsAndBack() {
        for name in EditorThemeNames.frames {
            XCTAssertEqual(EditorThemeNames.name(for: EditorThemeNames.frame(name)), name)
        }
        for name in EditorThemeNames.backgrounds {
            XCTAssertEqual(EditorThemeNames.name(for: EditorThemeNames.background(name)), name)
        }
        for name in EditorThemeNames.arrowStyles {
            XCTAssertEqual(EditorThemeNames.name(for: EditorThemeNames.arrowStyle(name)), name)
        }
        for name in EditorThemeNames.verticalAlignments {
            XCTAssertEqual(EditorThemeNames.name(for: EditorThemeNames.verticalAlignment(name)), name)
        }
        XCTAssertEqual(EditorThemeNames.horizontalAlignment("left"), .leading)
        XCTAssertEqual(EditorThemeNames.horizontalAlignment("right"), .trailing)
        XCTAssertEqual(EditorThemeNames.name(for: ExportHorizontalAlignment.trailing), "right")
        XCTAssertNil(EditorThemeNames.name(for: ExportBackgroundStyle.wallpaper))
    }

    // MARK: - Store

    private func makeDirectory() throws -> (URL, () -> Void) {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("EditorThemeTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return (directory, { try? FileManager.default.removeItem(at: directory) })
    }

    func testStorePersists() throws {
        let (directory, cleanup) = try makeDirectory()
        defer { cleanup() }

        let store = EditorThemeStore(directory: directory)
        let saved = try store.save(try brandTheme())
        try store.setActive(id: saved.id)

        let reopened = EditorThemeStore(directory: directory)
        XCTAssertEqual(reopened.themes, [saved])
        XCTAssertEqual(reopened.activeThemeID, saved.id)
        XCTAssertEqual(reopened.activeTheme, saved)

        let document = try XCTUnwrap(
            JSONSerialization.jsonObject(with: try Data(contentsOf: reopened.fileURL)) as? [String: Any]
        )
        XCTAssertEqual(document["version"] as? Int, 1)
        XCTAssertEqual(document["activeThemeId"] as? String, saved.id)
        XCTAssertEqual((document["themes"] as? [Any])?.count, 1)
    }

    func testSaveReplacesTheSameIDAndRejectsInvalidThemes() throws {
        let (directory, cleanup) = try makeDirectory()
        defer { cleanup() }

        let store = EditorThemeStore(directory: directory)
        var theme = try store.save(EditorTheme(id: "a", name: "A"))
        theme.name = "B"
        try store.save(theme)
        XCTAssertEqual(store.themes.map(\.name), ["B"])

        theme.line.width = 101
        XCTAssertThrowsError(try store.save(theme))
        XCTAssertEqual(store.themes.first?.line.width, 4)
    }

    func testDeleteClearsTheActiveTheme() throws {
        let (directory, cleanup) = try makeDirectory()
        defer { cleanup() }

        let store = EditorThemeStore(directory: directory)
        try store.save(EditorTheme(id: "a", name: "A"))
        try store.setActive(id: "a")
        try store.delete(id: "a")
        XCTAssertNil(store.activeThemeID)
        XCTAssertTrue(store.themes.isEmpty)
        XCTAssertThrowsError(try store.delete(id: "a"))
        XCTAssertThrowsError(try store.setActive(id: "a"))
    }

    func testCorruptFileIsBackedUpAndStartsOver() throws {
        let (directory, cleanup) = try makeDirectory()
        defer { cleanup() }

        let corrupt = Data("not json".utf8)
        try corrupt.write(to: directory.appendingPathComponent(EditorThemeStore.fileName))
        let store = EditorThemeStore(directory: directory)
        XCTAssertTrue(store.themes.isEmpty)
        XCTAssertEqual(try Data(contentsOf: store.backupURL), corrupt)
        XCTAssertNoThrow(try store.save(EditorTheme(id: "a", name: "A")))
    }

    func testInvalidThemeIsSkippedAndTheFileBackedUp() throws {
        let (directory, cleanup) = try makeDirectory()
        defer { cleanup() }

        let contents = Data(#"""
        { "version": 1, "activeThemeId": "bad", "themes": [
          { "id": "good", "name": "Good" },
          { "id": "bad", "name": "Bad", "line": { "width": 500 } }
        ] }
        """#.utf8)
        try contents.write(to: directory.appendingPathComponent(EditorThemeStore.fileName))
        let store = EditorThemeStore(directory: directory)
        XCTAssertEqual(store.themes.map(\.id), ["good"])
        XCTAssertNil(store.activeThemeID)
        XCTAssertEqual(try Data(contentsOf: store.backupURL), contents)
    }

    func testStoreHoldsAtMostTheLimit() throws {
        let (directory, cleanup) = try makeDirectory()
        defer { cleanup() }

        let themes = (0..<EditorThemeStore.maximumThemes).map { #"{ "id": "t\#($0)", "name": "T\#($0)" }"# }
        let document = #"{ "version": 1, "activeThemeId": null, "themes": [\#(themes.joined(separator: ","))] }"#
        try Data(document.utf8).write(to: directory.appendingPathComponent(EditorThemeStore.fileName))
        let store = EditorThemeStore(directory: directory)
        XCTAssertEqual(store.themes.count, EditorThemeStore.maximumThemes)
        XCTAssertThrowsError(try store.save(EditorTheme(id: "one-more", name: "One More"))) { error in
            XCTAssertEqual(error as? EditorThemeStoreError, .limitReached)
        }
        XCTAssertNoThrow(try store.save(EditorTheme(id: "t0", name: "Replaced")))
    }

    func testImportNeverReplacesAndExportRoundTrips() throws {
        let (directory, cleanup) = try makeDirectory()
        defer { cleanup() }

        let store = EditorThemeStore(directory: directory)
        let original = try store.importTheme(from: try fixtureData("brand-theme.json"))
        XCTAssertEqual(original.id, "acme-brand")

        let copy = try store.importTheme(from: try store.exportData(id: original.id))
        XCTAssertNotEqual(copy.id, original.id)
        XCTAssertEqual(copy.name, "Acme Brand (2)")
        XCTAssertEqual(copy.shape, original.shape)
        XCTAssertEqual(store.themes.count, 2)

        let third = try store.importTheme(from: try store.exportData(id: original.id))
        XCTAssertEqual(third.name, "Acme Brand (3)")
    }

    // MARK: - Contract

    func testThemeCommandsAreInTheContractAndRouter() throws {
        let contract = try AutomationContract.load(from: Self.repoRoot.appendingPathComponent("shared/automation/commands.json"))
        let expectedScopes = [
            "editor.themes.list": "read",
            "editor.themes.get": "read",
            "editor.themes.save": "settings",
            "editor.themes.setActive": "settings",
            "editor.themes.delete": "delete",
        ]
        for (name, scope) in expectedScopes {
            XCTAssertEqual(contract.command(named: name)?.scope, scope, name)
            XCTAssertTrue(AutomationCommandRouter.supportedCommands.contains(name), name)
        }
    }

    // MARK: - Applying

    private func makeEditor(width: Int = 400, height: Int = 200) throws -> (ScreenshotEditorViewModel, () -> Void) {
        let context = try XCTUnwrap(CGContext(
            data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
            space: XCTUnwrap(CGColorSpace(name: CGColorSpace.sRGB)),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.setFillColor(CGColor(gray: 1, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))

        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("EditorThemeTests-\(UUID().uuidString).png")
        let bitmap = NSBitmapImageRep(cgImage: try XCTUnwrap(context.makeImage()))
        try XCTUnwrap(bitmap.representation(using: .png, properties: [:])).write(to: url)
        let editor = ScreenshotEditorViewModel(url: url)
        XCTAssertNotNil(editor.originalImage)
        return (editor, { try? FileManager.default.removeItem(at: url) })
    }

    private func annotation(_ tool: EditTool, color: Color = .blue) -> ScreenshotAnnotation {
        ScreenshotAnnotation(
            tool: tool,
            rect: CGRect(x: 0.2, y: 0.2, width: 0.2, height: 0.2),
            color: color,
            lineWidth: 3,
            text: tool == .emoji ? "🙂" : "x",
            points: []
        )
    }

    private func hex(_ color: Color) -> String {
        EditorThemeColor.hex(color)
    }

    func testApplyRestylesEachGroup() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let theme = try brandTheme()

        editor.annotations = [EditTool.rectangle, .arrow, .line, .pencil, .text, .number].map { annotation($0) }
        editor.applyTheme(theme)
        let styled = editor.annotations

        XCTAssertEqual(hex(styled[0].color), theme.shape.strokeColor)
        XCTAssertEqual(styled[0].lineWidth, 5.5)
        XCTAssertEqual(hex(styled[0].fillColor), theme.shape.fillColor)
        XCTAssertTrue(styled[0].shapeEffects.isGradientFill)
        XCTAssertEqual(styled[0].shapeEffects.gradientDirectionDegrees, 120)
        XCTAssertEqual(styled[0].shapeEffects.shadowBlur, 18)
        XCTAssertTrue(styled[0].shapeEffects.drawsShadow)

        XCTAssertEqual(hex(styled[1].color), theme.line.color)
        XCTAssertEqual(styled[1].lineWidth, 7)
        XCTAssertEqual(styled[1].arrowStyle, .curvedRight)
        XCTAssertEqual(styled[2].arrowStyle, .straight, "Only arrows take the arrow style")
        XCTAssertEqual(styled[3].arrowStyle, .straight)
        XCTAssertEqual(hex(styled[3].color), theme.line.color)

        XCTAssertEqual(hex(styled[4].color), theme.text.color)
        XCTAssertEqual(styled[4].fontSize, 22)
        XCTAssertEqual(styled[4].fontFamily, "Georgia")
        XCTAssertTrue(styled[4].isBold && styled[4].isItalic && styled[4].isUnderlined)
        XCTAssertEqual(styled[4].textBoxStyle.padding, 10)

        XCTAssertEqual(hex(styled[5].color), theme.number.badgeColor)
        XCTAssertEqual(hex(styled[5].textColor), theme.number.textColor)
    }

    func testApplyNeverTouchesRedactionsOrEmoji() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }

        var redaction = annotation(.blur)
        redaction.isOpaqueRedaction = true
        let emoji = annotation(.emoji)
        editor.annotations = [redaction, emoji]
        editor.applyTheme(try brandTheme())

        XCTAssertEqual(hex(editor.annotations[0].color), hex(redaction.color))
        XCTAssertEqual(editor.annotations[0].lineWidth, redaction.lineWidth)
        XCTAssertEqual(editor.annotations[0].rect, redaction.rect)
        XCTAssertTrue(editor.annotations[0].isOpaqueRedaction)
        XCTAssertEqual(hex(editor.annotations[1].color), hex(emoji.color))
        XCTAssertEqual(editor.annotations[1].rect, emoji.rect)
        XCTAssertEqual(editor.annotations[1].text, "🙂")
    }

    func testRestylingNeverMovesAnAnnotation() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }

        let text = annotation(.text)
        let number = annotation(.number)
        editor.annotations = [text, number]
        editor.applyTheme(try brandTheme())

        XCTAssertEqual(editor.annotations[0].rect.midX, text.rect.midX, accuracy: 0.0001)
        XCTAssertEqual(editor.annotations[0].rect.midY, text.rect.midY, accuracy: 0.0001)
        XCTAssertEqual(editor.annotations[1].rect.midX, number.rect.midX, accuracy: 0.0001)
        XCTAssertEqual(editor.annotations[1].rect.midY, number.rect.midY, accuracy: 0.0001)
        // 1.5 times the 20-pixel base badge of a 400-pixel-wide image.
        XCTAssertEqual(editor.annotations[1].rect.width * 400, 30, accuracy: 0.0001)
        XCTAssertEqual(editor.annotations[1].rect.height * 200, 30, accuracy: 0.0001)
    }

    // MARK: - Modified

    func testNotModifiedRightAfterApplying() throws {
        for theme in [try brandTheme(), try EditorTheme.parse(try fixtureData("default-theme.json"))] {
            let (editor, cleanup) = try makeEditor()
            defer { cleanup() }

            editor.annotations = [EditTool.rectangle, .arrow, .line, .pencil, .text, .number, .blur, .emoji].map { annotation($0) }
            editor.applyTheme(theme)
            XCTAssertFalse(editor.isThemeModified, theme.name)
        }
    }

    func testSmallNumberMultiplierClampedToTheMinimumIsNotModified() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }

        var theme = try brandTheme()
        theme.number.size = 0.25
        editor.annotations = [annotation(.number)]
        editor.applyTheme(theme)
        XCTAssertFalse(editor.isThemeModified)
    }

    func testCapturePicksTheNewestDifferingAnnotation() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let theme = try brandTheme()

        editor.annotations = [annotation(.rectangle), annotation(.rectangle), annotation(.rectangle), annotation(.arrow)]
        editor.applyTheme(theme)
        editor.annotations[0].color = EditorThemeColor.color("#FF0000FF")
        editor.annotations[1].color = EditorThemeColor.color("#FF00FF00")

        XCTAssertTrue(editor.isThemeModified)
        let look = try XCTUnwrap(editor.capturedThemeLook())
        XCTAssertEqual(look.shape.strokeColor, "#FF00FF00")
        XCTAssertEqual(look.line, theme.line)
        XCTAssertEqual(look.id, theme.id)
    }

    func testCaptureFallsBackToDefaults() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }

        editor.applyTheme(try brandTheme())
        XCTAssertFalse(editor.isThemeModified)
        editor.selectedTool = .arrow
        editor.selectedColor = EditorThemeColor.color("#FF0000FF")
        XCTAssertTrue(editor.isThemeModified)
        XCTAssertEqual(editor.capturedThemeLook()?.line.color, "#FF0000FF")
    }

    func testAnImperceptibleSizeChangeIsNotModified() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }

        editor.annotations = [annotation(.rectangle)]
        editor.applyTheme(try brandTheme())
        editor.annotations[0].lineWidth += 0.001
        XCTAssertFalse(editor.isThemeModified)
        editor.annotations[0].lineWidth += 1
        XCTAssertTrue(editor.isThemeModified)
    }

    func testCanvasIsComparedWithItsStateWhenTheThemeBecameActive() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }

        var theme = try brandTheme()
        theme.canvas.applyOnOpen = false
        editor.activateThemeOnOpen(theme)
        XCTAssertEqual(editor.canvasPadding, 0)
        XCTAssertFalse(editor.isThemeModified, "A canvas the theme didn't apply is not an edit")

        editor.canvasPadding = 12
        XCTAssertTrue(editor.isThemeModified)
        XCTAssertEqual(editor.capturedThemeLook()?.canvas.padding, 12)
        XCTAssertEqual(editor.capturedThemeLook()?.canvas.applyOnOpen, false)
    }

    func testUpdateSavesTheLookAndStopsReadingAsModified() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let (directory, cleanupDirectory) = try makeDirectory()
        defer { cleanupDirectory() }
        let store = EditorThemeStore(directory: directory)
        let theme = try store.save(try brandTheme())

        editor.annotations = [annotation(.rectangle), annotation(.rectangle)]
        editor.applyTheme(theme)
        editor.annotations[1].color = EditorThemeColor.color("#FF00FF00")

        try editor.updateActiveTheme(in: store)
        XCTAssertEqual(store.theme(id: theme.id)?.shape.strokeColor, "#FF00FF00")
        XCTAssertEqual(store.themes.count, 1)
        // The older rectangle still has the old stroke, and saving must settle anyway.
        XCTAssertFalse(editor.isThemeModified)

        editor.annotations[0].color = EditorThemeColor.color("#FF0000FF")
        XCTAssertTrue(editor.isThemeModified)
    }

    func testSaveAsNewMakesTheNewThemeActive() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let (directory, cleanupDirectory) = try makeDirectory()
        defer { cleanupDirectory() }
        let store = EditorThemeStore(directory: directory)
        let theme = try store.save(try brandTheme())

        editor.annotations = [annotation(.arrow)]
        editor.applyTheme(theme)
        editor.annotations[0].lineWidth = 9

        let saved = try XCTUnwrap(try editor.saveAsNewTheme(name: "  Second  ", in: store))
        XCTAssertNotEqual(saved.id, theme.id)
        XCTAssertEqual(saved.name, "Second")
        XCTAssertEqual(saved.line.width, 9)
        XCTAssertEqual(store.activeThemeID, saved.id)
        XCTAssertEqual(editor.activeThemeID, saved.id)
        XCTAssertEqual(store.theme(id: theme.id)?.line.width, 7)
        XCTAssertFalse(editor.isThemeModified)
    }

    // MARK: - Undo

    func testSwitchingIsOneUndoStepAndRedoRestoresIt() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let theme = try brandTheme()

        let original = annotation(.rectangle)
        editor.annotations = [original]
        editor.selectedTool = .rectangle
        let originalDefault = hex(editor.selectedColor)

        editor.applyTheme(theme)
        XCTAssertEqual(editor.activeThemeID, theme.id)
        XCTAssertEqual(editor.canvasPadding, 48)
        XCTAssertEqual(editor.backgroundStyle, .gradient)
        XCTAssertEqual(editor.exportFramePreset, .landscapeSixteenByNine)
        XCTAssertEqual(editor.horizontalExportAlignment, .leading)
        XCTAssertEqual(editor.verticalExportAlignment, .bottom)
        XCTAssertEqual(hex(editor.selectedColor), theme.shape.strokeColor)

        editor.undo()
        XCTAssertFalse(editor.canUndo, "A switch is one step")
        XCTAssertTrue(editor.canRedo)
        XCTAssertNil(editor.activeThemeID)
        XCTAssertNil(editor.baselineTheme)
        XCTAssertEqual(hex(editor.annotations[0].color), hex(original.color))
        XCTAssertEqual(editor.canvasPadding, 0)
        XCTAssertEqual(editor.backgroundStyle, .transparent)
        XCTAssertEqual(editor.exportFramePreset, .original)
        XCTAssertEqual(hex(editor.selectedColor), originalDefault)

        editor.redo()
        XCTAssertEqual(editor.activeThemeID, theme.id)
        XCTAssertEqual(hex(editor.annotations[0].color), theme.shape.strokeColor)
        XCTAssertEqual(editor.canvasPadding, 48)
        XCTAssertEqual(editor.backgroundStyle, .gradient)
        XCTAssertEqual(hex(editor.selectedColor), theme.shape.strokeColor)
        XCTAssertFalse(editor.isThemeModified)

        editor.undo()
        XCTAssertEqual(editor.canvasPadding, 0)
        XCTAssertNil(editor.activeThemeID)
    }

    func testOtherUndoStepsLeaveTheCanvasAlone() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }

        editor.applyTheme(try brandTheme())
        editor.canvasPadding = 20
        editor.selectedTool = .rectangle
        editor.handleDragEnd(start: CGPoint(x: 0.1, y: 0.1), end: CGPoint(x: 0.5, y: 0.5))
        XCTAssertEqual(editor.annotations.count, 1)

        editor.undo()
        XCTAssertTrue(editor.annotations.isEmpty)
        XCTAssertEqual(editor.canvasPadding, 20)
        XCTAssertEqual(editor.activeThemeID, "acme-brand")

        editor.redo()
        XCTAssertEqual(editor.annotations.count, 1)
        XCTAssertEqual(editor.canvasPadding, 20)
    }

    // MARK: - Switching

    func testNoneChangesNothingAndIsNotAnUndoStep() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let theme = try brandTheme()

        editor.annotations = [annotation(.rectangle)]
        editor.selectedTool = .rectangle
        editor.applyTheme(theme)
        editor.undo()
        editor.redo()

        editor.clearTheme()
        XCTAssertNil(editor.activeThemeID)
        XCTAssertFalse(editor.isThemeModified)
        XCTAssertEqual(hex(editor.annotations[0].color), theme.shape.strokeColor)
        XCTAssertEqual(editor.canvasPadding, 48)
        XCTAssertEqual(hex(editor.selectedColor), theme.shape.strokeColor)

        editor.undo()
        XCTAssertEqual(editor.canvasPadding, 0, "Undo goes back past the switch, not the None")
    }

    func testToolsShareColorWithNone() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }

        editor.selectedTool = .rectangle
        editor.selectedColor = EditorThemeColor.color("#FF00FF00")
        editor.selectedTool = .arrow
        XCTAssertEqual(hex(editor.selectedColor), "#FF00FF00")
    }

    func testEachGroupKeepsItsOwnColorAndWidthWhileThemed() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let theme = try brandTheme()

        editor.selectedTool = .rectangle
        editor.applyTheme(theme)
        XCTAssertEqual(editor.lineWidth, 5.5)

        editor.selectedColor = EditorThemeColor.color("#FF00FF00")
        editor.lineWidth = 2
        editor.selectedTool = .arrow
        XCTAssertEqual(hex(editor.selectedColor), theme.line.color)
        XCTAssertEqual(editor.lineWidth, 7)

        editor.selectedTool = .line
        XCTAssertEqual(hex(editor.selectedColor), theme.line.color, "Same group")

        editor.selectedTool = .move
        editor.selectedTool = .number
        XCTAssertEqual(hex(editor.selectedColor), theme.number.badgeColor)

        editor.selectedTool = .circle
        XCTAssertEqual(hex(editor.selectedColor), "#FF00FF00")
        XCTAssertEqual(editor.lineWidth, 2)
    }

    func testImageBackgroundStaysWhenAThemeApplies() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }

        let wallpaper = NSImage(size: NSSize(width: 4, height: 4))
        editor.wallpaperImage = wallpaper
        editor.backgroundStyle = .wallpaper
        editor.selectedBackgroundPresetID = "wallpaper"

        editor.applyTheme(try brandTheme())
        XCTAssertEqual(editor.backgroundStyle, .wallpaper)
        XCTAssertTrue(editor.wallpaperImage === wallpaper)
        XCTAssertEqual(editor.selectedBackgroundPresetID, "wallpaper")
        XCTAssertEqual(editor.canvasPadding, 48)
        XCTAssertFalse(editor.isThemeModified)
    }

    func testOpeningAppliesDefaultsAndOptedInCanvasWithoutPrompting() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let theme = try brandTheme()

        editor.activateThemeOnOpen(theme)
        XCTAssertEqual(editor.canvasPadding, 48)
        XCTAssertEqual(editor.backgroundStyle, .gradient)
        XCTAssertEqual(editor.textFontSize, 22)
        XCTAssertEqual(editor.textFontFamily, "Georgia")
        XCTAssertEqual(editor.selectedArrowStylePreset, .curvedRight)
        XCTAssertEqual(editor.numberSizeMultiplier, 1.5)
        XCTAssertFalse(editor.hasUnsavedChanges)
        XCTAssertFalse(editor.canUndo)
        XCTAssertFalse(editor.isThemeModified)
    }

    func testOpeningWithoutOptInLeavesTheCanvas() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }

        var theme = try brandTheme()
        theme.canvas.applyOnOpen = false
        editor.activateThemeOnOpen(theme)
        XCTAssertEqual(editor.canvasPadding, 0)
        XCTAssertEqual(editor.backgroundStyle, .transparent)
        XCTAssertEqual(hex(editor.selectedColor), theme.shape.strokeColor)
        XCTAssertFalse(editor.hasUnsavedChanges)
    }

    func testNewAnnotationsTakeTheThemesDefaults() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let theme = try brandTheme()

        editor.activateThemeOnOpen(theme)
        editor.selectedTool = .rectangle
        editor.handleDragEnd(start: CGPoint(x: 0.1, y: 0.1), end: CGPoint(x: 0.5, y: 0.5))
        editor.selectedTool = .arrow
        editor.handleDragEnd(start: CGPoint(x: 0.1, y: 0.1), end: CGPoint(x: 0.5, y: 0.5))
        editor.selectedTool = .number
        editor.placeNumberAnnotation(at: CGPoint(x: 0.5, y: 0.5))

        XCTAssertEqual(hex(editor.annotations[0].color), theme.shape.strokeColor)
        XCTAssertEqual(editor.annotations[0].shapeEffects.shadowBlur, 18)
        XCTAssertEqual(hex(editor.annotations[1].color), theme.line.color)
        XCTAssertEqual(editor.annotations[1].arrowStyle, .curvedRight)
        XCTAssertEqual(hex(editor.annotations[2].color), theme.number.badgeColor)
        XCTAssertEqual(editor.annotations[2].rect.width * 400, 30, accuracy: 0.0001)
        XCTAssertFalse(editor.isThemeModified)
    }

    // MARK: - Agents

    private let bounds = ["x": 10, "y": 10, "width": 100, "height": 50]

    func testAnnotateWithThemeWhereExplicitValuesWin() throws {
        let theme = try brandTheme()
        let parsed = try AutomationAnnotations.parse([
            ["type": "rectangle", "bounds": bounds],
            ["type": "rectangle", "bounds": bounds, "color": "#FF0000FF", "lineWidth": 20, "fillColor": "#FF00FF00", "shadowBlur": 0],
            ["type": "arrow", "bounds": bounds],
            ["type": "text", "bounds": bounds, "text": "Hi"],
            ["type": "text", "bounds": bounds, "text": "Hi", "fontSize": 40, "color": "#FF0000FF"],
            ["type": "number", "bounds": bounds],
            ["type": "redact", "bounds": bounds],
        ], imagePixelSize: CGSize(width: 1800, height: 900), theme: theme)

        XCTAssertEqual(hex(parsed[0].color), theme.shape.strokeColor)
        XCTAssertEqual(parsed[0].lineWidth, 5.5, "Theme sizes are editor units")
        XCTAssertEqual(hex(parsed[0].fillColor), theme.shape.fillColor)
        XCTAssertTrue(parsed[0].shapeEffects.isGradientFill)
        XCTAssertEqual(parsed[0].shapeEffects.shadowBlur, 18)

        XCTAssertEqual(hex(parsed[1].color), "#FF0000FF")
        XCTAssertEqual(parsed[1].lineWidth, 10, accuracy: 0.0001, "Agent sizes are image pixels")
        XCTAssertEqual(hex(parsed[1].fillColor), "#FF00FF00")
        XCTAssertFalse(parsed[1].shapeEffects.drawsShadow)

        XCTAssertEqual(parsed[2].arrowStyle, .curvedRight)
        XCTAssertEqual(parsed[2].lineWidth, 7)

        XCTAssertEqual(parsed[3].fontSize, 22)
        XCTAssertEqual(parsed[3].fontFamily, "Georgia")
        XCTAssertEqual(parsed[4].fontSize, 40 * 800 / 1800, accuracy: 0.0001)
        XCTAssertEqual(hex(parsed[4].color), "#FF0000FF")
        XCTAssertTrue(parsed[4].isBold)

        XCTAssertEqual(hex(parsed[5].color), theme.number.badgeColor)
        XCTAssertEqual(parsed[5].rect.width * 1800, 100, accuracy: 0.0001, "Bounds set a number's size")

        XCTAssertEqual(parsed[6].lineWidth, 4, "Redactions take nothing from a theme")
    }

    func testAnnotateWithoutThemeIsUnchanged() throws {
        let parsed = try AutomationAnnotations.parse(
            [["type": "rectangle", "bounds": bounds], ["type": "arrow", "bounds": bounds]],
            imagePixelSize: CGSize(width: 400, height: 200)
        )
        XCTAssertEqual(parsed[0].fillColor, .clear)
        XCTAssertEqual(parsed[0].lineWidth, 4)
        XCTAssertEqual(parsed[1].arrowStyle, .straight)
    }

    private func renderedWidth(theme: EditorTheme?) throws -> Int {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let output = FileManager.default.temporaryDirectory.appendingPathComponent("EditorThemeTests-\(UUID().uuidString)")
        let rendered = try AutomationAnnotations.render(
            sourceURL: editor.sourceURL,
            annotationsJSON: [["type": "rectangle", "bounds": bounds]],
            theme: theme
        ) { output.appendingPathExtension($0) }
        defer { try? FileManager.default.removeItem(at: rendered.url) }
        let image = try XCTUnwrap(ImagePixelDensity.loadImage(contentsOf: rendered.url))
        return try XCTUnwrap(image.representations.first).pixelsWide
    }

    func testAnnotateAppliesTheCanvasOnlyIfTheThemeOptsIn() throws {
        var theme = try brandTheme()
        theme.canvas.frame = "original"
        // The render uses the configured export scale.
        let scaled = { (width: Int) in Int((Double(width) * Double(CaptureSettings.shared.screenshotScale) / 100).rounded()) }
        XCTAssertEqual(try renderedWidth(theme: theme), scaled(400 + 2 * 48))
        theme.canvas.applyOnOpen = false
        XCTAssertEqual(try renderedWidth(theme: theme), scaled(400))
        XCTAssertEqual(try renderedWidth(theme: nil), scaled(400))
    }

    private func assertCode(_ code: AutomationErrorCode, _ work: () throws -> Any, file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertThrowsError(try work(), file: file, line: line) { error in
            XCTAssertEqual((error as? AutomationError)?.code, code, file: file, line: line)
        }
    }

    func testAutomationCommands() throws {
        let (directory, cleanup) = try makeDirectory()
        defer { cleanup() }
        let store = EditorThemeStore(directory: directory)

        var listed = try AutomationEditorThemes.list([:], store: store)
        XCTAssertTrue(listed["activeThemeId"] is NSNull)
        XCTAssertEqual((listed["themes"] as? [[String: String]])?.count, 0)

        let brand = try XCTUnwrap(JSONSerialization.jsonObject(with: try fixtureData("brand-theme.json")))
        let saved = try AutomationEditorThemes.save(["theme": brand], store: store)
        XCTAssertEqual((saved["theme"] as? [String: Any])?["id"] as? String, "acme-brand")

        let created = try AutomationEditorThemes.save(["theme": ["name": "No ID"]], store: store)
        XCTAssertNotNil((created["theme"] as? [String: Any])?["id"] as? String)
        XCTAssertEqual(store.themes.count, 2)

        let fetched = try AutomationEditorThemes.get(["id": "acme-brand"], store: store)
        let line = (fetched["theme"] as? [String: Any])?["line"] as? [String: Any]
        XCTAssertEqual(line?["arrowStyle"] as? String, "curvedRight")

        let active = try AutomationEditorThemes.setActive(["id": "acme-brand"], store: store)
        XCTAssertEqual(active["activeThemeId"] as? String, "acme-brand")
        listed = try AutomationEditorThemes.list([:], store: store)
        XCTAssertEqual(listed["activeThemeId"] as? String, "acme-brand")
        XCTAssertEqual((listed["themes"] as? [[String: String]])?.first?["name"], "Acme Brand")

        let cleared = try AutomationEditorThemes.setActive([:], store: store)
        XCTAssertTrue(cleared["activeThemeId"] is NSNull)

        let deleted = try AutomationEditorThemes.delete(["id": "acme-brand"], store: store)
        XCTAssertEqual(deleted["deleted"] as? Bool, true)
        XCTAssertNil(store.theme(id: "acme-brand"))
    }

    func testAutomationErrors() throws {
        let (directory, cleanup) = try makeDirectory()
        defer { cleanup() }
        let store = EditorThemeStore(directory: directory)

        assertCode(.notFound) { try AutomationEditorThemes.get(["id": "nope"], store: store) }
        assertCode(.notFound) { try AutomationEditorThemes.delete(["id": "nope"], store: store) }
        assertCode(.notFound) { try AutomationEditorThemes.setActive(["id": "nope"], store: store) }
        assertCode(.notFound) { try AutomationEditorThemes.theme(forParam: "nope", store: store) as Any }
        assertCode(.invalidParams) { try AutomationEditorThemes.get([:], store: store) }
        assertCode(.invalidParams) { try AutomationEditorThemes.list(["extra": 1], store: store) }
        assertCode(.invalidParams) { try AutomationEditorThemes.save(["theme": "nope"], store: store) }
        assertCode(.invalidParams) { try AutomationEditorThemes.save(["theme": ["id": "a/b", "name": "A"]], store: store) }
        assertCode(.invalidParams) { try AutomationEditorThemes.theme(forParam: 4, store: store) as Any }
        XCTAssertNil(try AutomationEditorThemes.theme(forParam: nil, store: store))
    }
}
