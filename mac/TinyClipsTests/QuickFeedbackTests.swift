import XCTest
@testable import TinyClips

final class QuickFeedbackTests: XCTestCase {
    private let context = QuickBugReportContext(
        platform: "macOS",
        version: "1.9.0",
        build: "1",
        distribution: "Direct Download",
        osVersion: "macOS 15.0"
    )

    func testBugRequestPrefillsQuickBugTemplateAndAppInfo() throws {
        let url = QuickBugReportURLBuilder.makeURL(
            type: .bug,
            title: "Capture & save",
            description: "It stopped working.\nI expected a screenshot.",
            context: context
        )
        let values = try queryValues(from: url)

        XCTAssertEqual(values["template"], "quick_bug_report.yml")
        XCTAssertEqual(values["labels"], "bug")
        XCTAssertEqual(values["title"], "[Bug]: Capture & save")
        XCTAssertEqual(values["happened"], "It stopped working.\nI expected a screenshot.")
        XCTAssertNil(values["request"])
        assertAppInfo(in: values)
    }

    func testFeatureRequestPrefillsQuickFeatureTemplateAndAppInfo() throws {
        let url = QuickBugReportURLBuilder.makeURL(
            type: .featureRequest,
            title: "Capture & save",
            description: "Add a save option.",
            context: context
        )
        let values = try queryValues(from: url)

        XCTAssertEqual(values["template"], "quick_feature_request.yml")
        XCTAssertEqual(values["labels"], "enhancement")
        XCTAssertEqual(values["title"], "[Feature]: Capture & save")
        XCTAssertEqual(values["request"], "Add a save option.")
        XCTAssertNil(values["happened"])
        assertAppInfo(in: values)
    }

    private func queryValues(from url: URL) throws -> [String: String] {
        let items = try XCTUnwrap(URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems)
        return Dictionary(uniqueKeysWithValues: items.compactMap { item in
            guard let value = item.value else { return nil }
            return (item.name, value)
        })
    }

    private func assertAppInfo(
        in values: [String: String],
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        XCTAssertEqual(values["platform"], context.platform, file: file, line: line)
        XCTAssertEqual(values["version"], context.version, file: file, line: line)
        XCTAssertEqual(values["build"], context.build, file: file, line: line)
        XCTAssertEqual(values["distribution"], context.distribution, file: file, line: line)
        XCTAssertEqual(values["os"], context.osVersion, file: file, line: line)
    }
}

final class QuickFeedbackKeyboardShortcutTests: XCTestCase {
    func testFeedbackFieldsUseResponderChainForAllPasteboardCommands() {
        for action in [
            ScreenshotEditorPasteboardShortcutPolicy.Action.cut,
            .copy,
            .paste
        ] {
            XCTAssertEqual(
                ScreenshotEditorPasteboardShortcutPolicy.route(
                    action,
                    hasActiveEditor: false,
                    isEditingText: false
                ),
                .responderChain
            )
        }
    }

    func testScreenshotEditorCopiesImageOutsideTextEditing() {
        XCTAssertEqual(route(.cut, isEditingText: false), .unavailable)
        XCTAssertEqual(route(.copy, isEditingText: false), .screenshot)
        XCTAssertEqual(route(.paste, isEditingText: false), .unavailable)
    }

    func testScreenshotTextEditingUsesResponderChain() {
        XCTAssertEqual(route(.cut, isEditingText: true), .responderChain)
        XCTAssertEqual(route(.copy, isEditingText: true), .responderChain)
        XCTAssertEqual(route(.paste, isEditingText: true), .responderChain)
    }

    private func route(
        _ action: ScreenshotEditorPasteboardShortcutPolicy.Action,
        isEditingText: Bool
    ) -> ScreenshotEditorPasteboardShortcutPolicy.Route {
        ScreenshotEditorPasteboardShortcutPolicy.route(
            action,
            hasActiveEditor: true,
            isEditingText: isEditingText
        )
    }
}
