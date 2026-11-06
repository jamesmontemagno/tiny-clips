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
        XCTAssertEqual(copy.rectangle, original.rectangle)
        XCTAssertEqual(copy.pen, original.pen)
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

    func testApplyUsesEachTypesOwnSection() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let theme = try brandTheme()

        editor.annotations = [EditTool.rectangle, .circle, .arrow, .line, .pencil, .text, .number].map { annotation($0) }
        editor.applyTheme(theme)
        let styled = editor.annotations

        XCTAssertEqual(hex(styled[0].color), theme.rectangle.strokeColor)
        XCTAssertEqual(styled[0].lineWidth, 5.5)
        XCTAssertEqual(hex(styled[0].fillColor), theme.rectangle.fillColor)
        XCTAssertTrue(styled[0].shapeEffects.isGradientFill)
        XCTAssertEqual(hex(styled[0].shapeEffects.gradientEndColor), "#FF708090")
        XCTAssertEqual(styled[0].shapeEffects.gradientDirectionDegrees, 120)
        XCTAssertEqual(styled[0].shapeEffects.shadowBlur, 18)
        XCTAssertEqual(hex(styled[0].shapeEffects.shadowColor), "#66000011")
        XCTAssertTrue(styled[0].shapeEffects.drawsShadow)

        XCTAssertEqual(hex(styled[1].color), theme.ellipse.strokeColor)
        XCTAssertEqual(styled[1].lineWidth, 3.5)
        XCTAssertEqual(hex(styled[1].fillColor), theme.ellipse.fillColor)
        XCTAssertFalse(styled[1].shapeEffects.isGradientFill)
        XCTAssertEqual(styled[1].shapeEffects.gradientDirectionDegrees, 200)
        XCTAssertEqual(styled[1].shapeEffects.shadowBlur, 9)
        XCTAssertEqual(hex(styled[1].shapeEffects.shadowColor), "#55110000")

        XCTAssertEqual(hex(styled[2].color), theme.arrow.color)
        XCTAssertEqual(styled[2].lineWidth, 7)
        XCTAssertEqual(styled[2].arrowStyle, .curvedRight)

        XCTAssertEqual(hex(styled[3].color), theme.line.color)
        XCTAssertEqual(styled[3].lineWidth, 6)
        XCTAssertEqual(styled[3].arrowStyle, .straight, "Only arrows take the arrow style")

        XCTAssertEqual(hex(styled[4].color), theme.pen.color)
        XCTAssertEqual(styled[4].lineWidth, 2.5)
        XCTAssertEqual(styled[4].arrowStyle, .straight)

        XCTAssertEqual(hex(styled[5].color), theme.text.color)
        XCTAssertEqual(styled[5].fontSize, 22)
        XCTAssertEqual(styled[5].fontFamily, "Georgia")
        XCTAssertTrue(styled[5].isBold && styled[5].isItalic && styled[5].isUnderlined)
        XCTAssertEqual(styled[5].textBoxStyle.padding, 10)

        XCTAssertEqual(hex(styled[6].color), theme.number.badgeColor)
        XCTAssertEqual(hex(styled[6].textColor), theme.number.textColor)
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

            editor.annotations = [EditTool.rectangle, .circle, .arrow, .line, .pencil, .text, .number, .blur, .emoji].map { annotation($0) }
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
        XCTAssertEqual(look.rectangle.strokeColor, "#FF00FF00")
        XCTAssertEqual(look.arrow, theme.arrow)
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
        XCTAssertEqual(editor.capturedThemeLook()?.arrow.color, "#FF0000FF")
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
        XCTAssertEqual(store.theme(id: theme.id)?.rectangle.strokeColor, "#FF00FF00")
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
        XCTAssertEqual(saved.arrow.width, 9)
        XCTAssertEqual(store.activeThemeID, saved.id)
        XCTAssertEqual(editor.activeThemeID, saved.id)
        XCTAssertEqual(store.theme(id: theme.id)?.arrow.width, 7)
        XCTAssertFalse(editor.isThemeModified)
    }

    func testSaveAsNewWithNoneStartsFromTheDocument() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let (directory, cleanupDirectory) = try makeDirectory()
        defer { cleanupDirectory() }
        let store = EditorThemeStore(directory: directory)

        var arrow = annotation(.arrow)
        arrow.lineWidth = 9
        editor.annotations = [arrow]
        editor.canvasPadding = 24

        let saved = try XCTUnwrap(try editor.saveAsNewTheme(name: "First", in: store))
        XCTAssertEqual(saved.arrow.width, 9)
        XCTAssertEqual(saved.line.width, 4, "An arrow's width isn't a line's")
        XCTAssertEqual(saved.canvas.padding, 24)
        XCTAssertEqual(store.activeThemeID, saved.id)
        XCTAssertEqual(editor.activeThemeID, saved.id)
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
        XCTAssertEqual(hex(editor.selectedColor), theme.rectangle.strokeColor)

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
        XCTAssertEqual(hex(editor.annotations[0].color), theme.rectangle.strokeColor)
        XCTAssertEqual(editor.canvasPadding, 48)
        XCTAssertEqual(editor.backgroundStyle, .gradient)
        XCTAssertEqual(hex(editor.selectedColor), theme.rectangle.strokeColor)
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
        XCTAssertEqual(hex(editor.annotations[0].color), theme.rectangle.strokeColor)
        XCTAssertEqual(editor.canvasPadding, 48)
        XCTAssertEqual(hex(editor.selectedColor), theme.rectangle.strokeColor)

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
        XCTAssertEqual(hex(editor.selectedColor), theme.rectangle.strokeColor)
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

        XCTAssertEqual(hex(editor.annotations[0].color), theme.rectangle.strokeColor)
        XCTAssertEqual(editor.annotations[0].shapeEffects.shadowBlur, 18)
        XCTAssertEqual(hex(editor.annotations[1].color), theme.arrow.color)
        XCTAssertEqual(editor.annotations[1].arrowStyle, .curvedRight)
        XCTAssertEqual(hex(editor.annotations[2].color), theme.number.badgeColor)
        XCTAssertEqual(editor.annotations[2].rect.width * 400, 30, accuracy: 0.0001)
        XCTAssertFalse(editor.isThemeModified)
    }

    // MARK: - One section per type

    private static let green = "#FF00FF00"

    private func differingSections(_ lhs: EditorTheme, _ rhs: EditorTheme) -> Set<String> {
        var result = Set<String>()
        if lhs.rectangle != rhs.rectangle { result.insert("rectangle") }
        if lhs.ellipse != rhs.ellipse { result.insert("ellipse") }
        if lhs.arrow != rhs.arrow { result.insert("arrow") }
        if lhs.line != rhs.line { result.insert("line") }
        if lhs.pen != rhs.pen { result.insert("pen") }
        if lhs.text != rhs.text { result.insert("text") }
        if lhs.number != rhs.number { result.insert("number") }
        if lhs.canvas != rhs.canvas { result.insert("canvas") }
        return result
    }

    func testEachSectionRoundTripsIndependently() throws {
        let edits: [(String, (inout EditorTheme) -> Void)] = [
            ("rectangle", { $0.rectangle.strokeWidth = 9 }),
            ("ellipse", {
                $0.ellipse.fillColor = "#FF112233"
                $0.ellipse.gradientEndColor = "#FF445566"
            }),
            ("arrow", { $0.arrow.style = "curvedLeft" }),
            ("line", { $0.line.color = "#FF112233" }),
            ("pen", { $0.pen.width = 9 }),
            ("text", { $0.text.bold = true }),
            ("number", { $0.number.size = 2 }),
            ("canvas", { $0.canvas.padding = 9 }),
        ]
        let base = EditorTheme(id: "a", name: "A")
        for (section, edit) in edits {
            var theme = base
            edit(&theme)
            let parsed = try EditorTheme.parse(try theme.encodedData())
            XCTAssertEqual(parsed, theme, section)
            XCTAssertEqual(differingSections(parsed, base), [section])
        }
    }

    func testEditingOneAnnotationCapturesOnlyItsSection() throws {
        let sections: [(EditTool, String)] = [
            (.rectangle, "rectangle"), (.circle, "ellipse"), (.arrow, "arrow"), (.line, "line"),
            (.pencil, "pen"), (.text, "text"), (.number, "number"),
        ]
        let theme = try brandTheme()
        for (tool, section) in sections {
            let (editor, cleanup) = try makeEditor()
            defer { cleanup() }

            editor.annotations = sections.map { annotation($0.0) }
            editor.applyTheme(theme)
            let index = try XCTUnwrap(editor.annotations.firstIndex { $0.tool == tool })
            editor.annotations[index].color = EditorThemeColor.color(Self.green)

            XCTAssertTrue(editor.isThemeModified, section)
            let look = try XCTUnwrap(editor.capturedThemeLook())
            XCTAssertEqual(differingSections(look, theme), [section])
        }
    }

    func testEditingOneToolsDefaultsCapturesOnlyItsSection() throws {
        let sections: [(EditTool, String)] = [
            (.rectangle, "rectangle"), (.circle, "ellipse"), (.arrow, "arrow"), (.line, "line"),
            (.pencil, "pen"), (.text, "text"), (.number, "number"),
        ]
        let theme = try brandTheme()
        for (tool, section) in sections {
            let (editor, cleanup) = try makeEditor()
            defer { cleanup() }

            editor.applyTheme(theme)
            editor.selectedTool = tool
            editor.selectedColor = EditorThemeColor.color(Self.green)
            // Leaving the tool must keep the edit, and only for this type.
            editor.selectedTool = tool == .rectangle ? .arrow : .rectangle

            let look = try XCTUnwrap(editor.capturedThemeLook())
            XCTAssertEqual(differingSections(look, theme), [section])
        }
    }

    func testShapeFillAndEffectsCaptureOnlyTheirOwnSection() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let theme = try brandTheme()

        editor.applyTheme(theme)
        editor.selectedTool = .circle
        editor.updateShapeFillColor(EditorThemeColor.color(Self.green))
        editor.updateShapeEffects { $0.shadowBlur = 30 }
        editor.selectedTool = .arrow

        let look = try XCTUnwrap(editor.capturedThemeLook())
        XCTAssertEqual(differingSections(look, theme), ["ellipse"])
        XCTAssertEqual(look.ellipse.fillColor, Self.green)
        XCTAssertEqual(look.ellipse.shadowBlur, 30)
    }

    func testSwitchingToolsShowsEachToolsValuesAndNeverReadsAsModified() throws {
        for theme in [try brandTheme(), try EditorTheme.parse(try fixtureData("default-theme.json"))] {
            let (editor, cleanup) = try makeEditor()
            defer { cleanup() }

            editor.annotations = [annotation(.rectangle), annotation(.arrow)]
            editor.applyTheme(theme)
            for tool in EditTool.allCases + EditTool.allCases.reversed() {
                editor.selectedTool = tool
                XCTAssertFalse(editor.isThemeModified, "\(theme.name) \(tool.label)")

                switch tool {
                case .rectangle:
                    XCTAssertEqual(hex(editor.selectedColor), theme.rectangle.strokeColor)
                    XCTAssertEqual(Double(editor.lineWidth), theme.rectangle.strokeWidth)
                    XCTAssertEqual(hex(editor.shapeFillColorForInspector()), theme.rectangle.fillColor)
                    XCTAssertEqual(editor.shapeEffectsForInspector().isGradientFill, theme.rectangle.gradientEndColor != nil)
                    XCTAssertEqual(editor.shapeEffectsForInspector().gradientDirectionDegrees, theme.rectangle.gradientDirection)
                case .circle:
                    XCTAssertEqual(hex(editor.selectedColor), theme.ellipse.strokeColor)
                    XCTAssertEqual(Double(editor.lineWidth), theme.ellipse.strokeWidth)
                    XCTAssertEqual(hex(editor.shapeFillColorForInspector()), theme.ellipse.fillColor)
                    XCTAssertEqual(editor.shapeEffectsForInspector().isGradientFill, theme.ellipse.gradientEndColor != nil)
                    XCTAssertEqual(editor.shapeEffectsForInspector().gradientDirectionDegrees, theme.ellipse.gradientDirection)
                case .arrow:
                    XCTAssertEqual(hex(editor.selectedColor), theme.arrow.color)
                    XCTAssertEqual(Double(editor.lineWidth), theme.arrow.width)
                case .line:
                    XCTAssertEqual(hex(editor.selectedColor), theme.line.color)
                    XCTAssertEqual(Double(editor.lineWidth), theme.line.width)
                case .pencil:
                    XCTAssertEqual(hex(editor.selectedColor), theme.pen.color)
                    XCTAssertEqual(Double(editor.lineWidth), theme.pen.width)
                case .text:
                    XCTAssertEqual(hex(editor.selectedColor), theme.text.color)
                case .number:
                    XCTAssertEqual(hex(editor.selectedColor), theme.number.badgeColor)
                case .move, .crop, .emoji, .blur:
                    break
                }
            }
        }
    }

    func testEachToolKeepsItsOwnEditsWhileThemed() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let theme = try brandTheme()

        editor.selectedTool = .rectangle
        editor.applyTheme(theme)
        editor.selectedColor = EditorThemeColor.color(Self.green)
        editor.lineWidth = 2
        editor.updateShapeFillColor(EditorThemeColor.color("#FF0000FF"))
        editor.updateShapeEffects { $0.isGradientFill = false }

        editor.selectedTool = .circle
        XCTAssertEqual(hex(editor.selectedColor), theme.ellipse.strokeColor)
        XCTAssertEqual(editor.lineWidth, 3.5)
        XCTAssertEqual(hex(editor.shapeFillColorForInspector()), theme.ellipse.fillColor)

        // A tool with no fill must not disturb the live shape's.
        editor.selectedTool = .arrow
        editor.selectedTool = .move
        editor.selectedTool = .rectangle
        XCTAssertEqual(hex(editor.selectedColor), Self.green)
        XCTAssertEqual(editor.lineWidth, 2)
        XCTAssertEqual(hex(editor.shapeFillColorForInspector()), "#FF0000FF")
        XCTAssertFalse(editor.shapeEffectsForInspector().isGradientFill)

        editor.selectedTool = .line
        XCTAssertEqual(hex(editor.selectedColor), theme.line.color)
        XCTAssertEqual(editor.lineWidth, 6)
        editor.selectedTool = .pencil
        XCTAssertEqual(hex(editor.selectedColor), theme.pen.color)
        XCTAssertEqual(editor.lineWidth, 2.5)
    }

    func testPerToolDefaultsSurviveUndoAndRedo() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let first = try brandTheme()
        var second = try brandTheme()
        second.id = "second"
        second.rectangle.strokeColor = "#FF111111"
        second.rectangle.fillColor = "#FF222222"
        second.ellipse.strokeColor = "#FF333333"
        second.arrow.color = "#FF444444"

        editor.selectedTool = .rectangle
        editor.applyTheme(first)
        editor.selectedColor = EditorThemeColor.color(Self.green)
        editor.updateShapeFillColor(EditorThemeColor.color("#FF0000FF"))
        editor.selectedTool = .arrow
        editor.lineWidth = 9

        editor.applyTheme(second)
        XCTAssertEqual(hex(editor.selectedColor), second.arrow.color)
        XCTAssertEqual(editor.lineWidth, 7)

        // The tool changes after the switch, so undo has to load this tool's values.
        editor.selectedTool = .rectangle
        XCTAssertEqual(hex(editor.selectedColor), second.rectangle.strokeColor)
        XCTAssertEqual(hex(editor.selectedFillColor), second.rectangle.fillColor)

        editor.undo()
        XCTAssertEqual(editor.activeThemeID, first.id)
        XCTAssertEqual(hex(editor.selectedColor), Self.green)
        XCTAssertEqual(hex(editor.selectedFillColor), "#FF0000FF")
        editor.selectedTool = .arrow
        XCTAssertEqual(editor.lineWidth, 9)
        editor.selectedTool = .circle
        XCTAssertEqual(hex(editor.selectedColor), first.ellipse.strokeColor)
        XCTAssertEqual(hex(editor.selectedFillColor), first.ellipse.fillColor)

        editor.redo()
        XCTAssertEqual(editor.activeThemeID, second.id)
        XCTAssertEqual(hex(editor.selectedColor), second.ellipse.strokeColor)
        editor.selectedTool = .rectangle
        XCTAssertEqual(hex(editor.selectedColor), second.rectangle.strokeColor)
        XCTAssertEqual(hex(editor.selectedFillColor), second.rectangle.fillColor)
        XCTAssertFalse(editor.isThemeModified)
    }

    func testNewAnnotationsTakeTheirOwnToolsValues() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let theme = try brandTheme()
        let start = CGPoint(x: 0.1, y: 0.1)
        let end = CGPoint(x: 0.5, y: 0.5)

        editor.activateThemeOnOpen(theme)
        for tool in [EditTool.rectangle, .circle, .arrow, .line] {
            editor.selectedTool = tool
            editor.handleDragEnd(start: start, end: end)
        }
        editor.selectedTool = .pencil
        editor.handleDrag(start: start, current: start)
        editor.handleDrag(start: start, current: end)
        editor.handleDragEnd(start: start, end: end)
        editor.selectedTool = .text
        editor.textEditPosition = end
        editor.textEditValue = "Hi"
        editor.commitTextAnnotation()
        editor.selectedTool = .number
        editor.placeNumberAnnotation(at: end)

        let added = editor.annotations
        XCTAssertEqual(added.map(\.tool), [.rectangle, .circle, .arrow, .line, .pencil, .text, .number])

        XCTAssertEqual(hex(added[0].color), theme.rectangle.strokeColor)
        XCTAssertEqual(added[0].lineWidth, 5.5)
        XCTAssertEqual(hex(added[0].fillColor), theme.rectangle.fillColor)
        XCTAssertTrue(added[0].shapeEffects.isGradientFill)
        XCTAssertEqual(added[0].shapeEffects.shadowBlur, 18)

        XCTAssertEqual(hex(added[1].color), theme.ellipse.strokeColor)
        XCTAssertEqual(added[1].lineWidth, 3.5)
        XCTAssertEqual(hex(added[1].fillColor), theme.ellipse.fillColor)
        XCTAssertFalse(added[1].shapeEffects.isGradientFill)
        XCTAssertEqual(added[1].shapeEffects.shadowBlur, 9)

        XCTAssertEqual(hex(added[2].color), theme.arrow.color)
        XCTAssertEqual(added[2].lineWidth, 7)
        XCTAssertEqual(added[2].arrowStyle, .curvedRight)
        XCTAssertEqual(hex(added[3].color), theme.line.color)
        XCTAssertEqual(added[3].lineWidth, 6)
        XCTAssertEqual(hex(added[4].color), theme.pen.color)
        XCTAssertEqual(added[4].lineWidth, 2.5)
        XCTAssertEqual(hex(added[5].color), theme.text.color)
        XCTAssertEqual(hex(added[6].color), theme.number.badgeColor)
        XCTAssertFalse(editor.isThemeModified)
    }

    func testTextStillBeingTypedKeepsItsColorWhenAnotherToolIsChosen() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let theme = try brandTheme()

        editor.applyTheme(theme)
        editor.selectedTool = .text
        editor.textEditPosition = CGPoint(x: 0.5, y: 0.5)
        editor.textEditValue = "Hi"
        editor.selectedTool = .arrow
        XCTAssertEqual(hex(editor.newTextColor), theme.text.color)

        editor.commitTextAnnotation()
        XCTAssertEqual(hex(editor.annotations[0].color), theme.text.color)
    }

    func testSelectingAnAnnotationEditsItsOwnTypesDefaults() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let theme = try brandTheme()

        editor.annotations = [annotation(.arrow)]
        editor.selectedTool = .rectangle
        editor.applyTheme(theme)
        editor.selectedTool = .move
        editor.selectedAnnotationIndex = 0
        XCTAssertEqual(hex(editor.selectedColor), theme.arrow.color)
        XCTAssertFalse(editor.isThemeModified)

        editor.selectedColor = EditorThemeColor.color(Self.green)
        let look = try XCTUnwrap(editor.capturedThemeLook())
        XCTAssertEqual(differingSections(look, theme), ["arrow"])

        editor.selectedAnnotationIndex = nil
        XCTAssertEqual(hex(editor.selectedColor), Self.green)
        editor.selectedTool = .rectangle
        XCTAssertEqual(hex(editor.selectedColor), theme.rectangle.strokeColor)
    }

    func testUpdateSavesOneTypesEditAndKeepsTheOthers() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let (directory, cleanupDirectory) = try makeDirectory()
        defer { cleanupDirectory() }
        let store = EditorThemeStore(directory: directory)
        let theme = try store.save(try brandTheme())

        editor.applyTheme(theme)
        editor.selectedTool = .line
        editor.selectedColor = EditorThemeColor.color(Self.green)
        editor.selectedTool = .pencil

        let saved = try XCTUnwrap(try editor.updateActiveTheme(in: store))
        XCTAssertEqual(differingSections(saved, theme), ["line"])
        XCTAssertEqual(saved.line.color, Self.green)
        XCTAssertFalse(editor.isThemeModified)
        XCTAssertEqual(hex(editor.selectedColor), theme.pen.color)
        editor.selectedTool = .line
        XCTAssertEqual(hex(editor.selectedColor), Self.green)
        XCTAssertFalse(editor.isThemeModified)
    }

    func testNoneKeepsToolsSharingTheirDefaults() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let start = CGPoint(x: 0.1, y: 0.1)
        let end = CGPoint(x: 0.5, y: 0.5)

        editor.selectedTool = .rectangle
        editor.selectedColor = EditorThemeColor.color(Self.green)
        editor.lineWidth = 2
        editor.updateShapeFillColor(EditorThemeColor.color("#FF0000FF"))
        editor.updateShapeEffects { $0.hasShadow = true }

        for tool in [EditTool.circle, .arrow, .line, .pencil, .text, .number, .move] {
            editor.selectedTool = tool
            XCTAssertEqual(hex(editor.selectedColor), Self.green, tool.label)
            XCTAssertEqual(editor.lineWidth, 2, tool.label)
            XCTAssertEqual(hex(editor.selectedFillColor), "#FF0000FF", tool.label)
            XCTAssertTrue(editor.shapeEffects.hasShadow, tool.label)
        }

        for tool in [EditTool.rectangle, .circle] {
            editor.selectedTool = tool
            editor.handleDragEnd(start: start, end: end)
        }
        for added in editor.annotations {
            XCTAssertEqual(hex(added.color), Self.green)
            XCTAssertEqual(added.lineWidth, 2)
            XCTAssertEqual(hex(added.fillColor), "#FF0000FF")
            XCTAssertTrue(added.shapeEffects.hasShadow)
        }
    }

    func testClearingAThemeReturnsToSharedDefaults() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let theme = try brandTheme()

        editor.selectedTool = .rectangle
        editor.applyTheme(theme)
        editor.clearTheme()
        XCTAssertEqual(hex(editor.selectedColor), theme.rectangle.strokeColor)

        editor.selectedTool = .circle
        XCTAssertEqual(hex(editor.selectedColor), theme.rectangle.strokeColor)
        XCTAssertEqual(editor.lineWidth, 5.5)
        XCTAssertEqual(hex(editor.selectedFillColor), theme.rectangle.fillColor)
    }

    func testSaveAsNewWithNoneGivesEveryTypeTheSharedDefaults() throws {
        let (editor, cleanup) = try makeEditor()
        defer { cleanup() }
        let (directory, cleanupDirectory) = try makeDirectory()
        defer { cleanupDirectory() }
        let store = EditorThemeStore(directory: directory)

        editor.selectedColor = EditorThemeColor.color(Self.green)
        editor.lineWidth = 2
        editor.updateShapeFillColor(EditorThemeColor.color("#FF0000FF"))

        let saved = try XCTUnwrap(try editor.saveAsNewTheme(name: "Shared", in: store))
        XCTAssertEqual(saved.rectangle, saved.ellipse)
        XCTAssertEqual(saved.rectangle.strokeColor, Self.green)
        XCTAssertEqual(saved.rectangle.strokeWidth, 2)
        XCTAssertEqual(saved.rectangle.fillColor, "#FF0000FF")
        XCTAssertEqual(saved.arrow.color, Self.green)
        XCTAssertEqual(saved.line, saved.pen)
        XCTAssertEqual(saved.line.width, 2)
        XCTAssertEqual(saved.text.color, Self.green)
        XCTAssertEqual(saved.number.badgeColor, Self.green)
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
            ["type": "ellipse", "bounds": bounds],
            ["type": "line", "bounds": bounds],
            ["type": "pen", "bounds": bounds, "points": [["x": 10, "y": 10], ["x": 20, "y": 20]]],
            ["type": "text", "bounds": bounds, "text": "Hi"],
            ["type": "text", "bounds": bounds, "text": "Hi", "fontSize": 40, "color": "#FF0000FF"],
            ["type": "number", "bounds": bounds],
            ["type": "redact", "bounds": bounds],
        ], imagePixelSize: CGSize(width: 1800, height: 900), theme: theme)

        XCTAssertEqual(hex(parsed[0].color), theme.rectangle.strokeColor)
        XCTAssertEqual(parsed[0].lineWidth, 5.5, "Theme sizes are editor units")
        XCTAssertEqual(hex(parsed[0].fillColor), theme.rectangle.fillColor)
        XCTAssertTrue(parsed[0].shapeEffects.isGradientFill)
        XCTAssertEqual(parsed[0].shapeEffects.shadowBlur, 18)

        XCTAssertEqual(hex(parsed[1].color), "#FF0000FF")
        XCTAssertEqual(parsed[1].lineWidth, 10, accuracy: 0.0001, "Agent sizes are image pixels")
        XCTAssertEqual(hex(parsed[1].fillColor), "#FF00FF00")
        XCTAssertFalse(parsed[1].shapeEffects.drawsShadow)

        XCTAssertEqual(hex(parsed[2].color), theme.arrow.color)
        XCTAssertEqual(parsed[2].arrowStyle, .curvedRight)
        XCTAssertEqual(parsed[2].lineWidth, 7)

        XCTAssertEqual(hex(parsed[3].color), theme.ellipse.strokeColor)
        XCTAssertEqual(parsed[3].lineWidth, 3.5)
        XCTAssertEqual(hex(parsed[3].fillColor), theme.ellipse.fillColor)
        XCTAssertFalse(parsed[3].shapeEffects.isGradientFill)
        XCTAssertEqual(parsed[3].shapeEffects.shadowBlur, 9)
        XCTAssertEqual(hex(parsed[3].shapeEffects.shadowColor), "#55110000")

        XCTAssertEqual(hex(parsed[4].color), theme.line.color)
        XCTAssertEqual(parsed[4].lineWidth, 6)
        XCTAssertEqual(parsed[4].arrowStyle, .straight)

        XCTAssertEqual(hex(parsed[5].color), theme.pen.color)
        XCTAssertEqual(parsed[5].lineWidth, 2.5)

        XCTAssertEqual(parsed[6].fontSize, 22)
        XCTAssertEqual(parsed[6].fontFamily, "Georgia")
        XCTAssertEqual(parsed[7].fontSize, 40 * 800 / 1800, accuracy: 0.0001)
        XCTAssertEqual(hex(parsed[7].color), "#FF0000FF")
        XCTAssertTrue(parsed[7].isBold)

        XCTAssertEqual(hex(parsed[8].color), theme.number.badgeColor)
        XCTAssertEqual(parsed[8].rect.width * 1800, 100, accuracy: 0.0001, "Bounds set a number's size")

        XCTAssertEqual(parsed[9].lineWidth, 4, "Redactions take nothing from a theme")
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
        let arrow = (fetched["theme"] as? [String: Any])?["arrow"] as? [String: Any]
        XCTAssertEqual(arrow?["style"] as? String, "curvedRight")

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
