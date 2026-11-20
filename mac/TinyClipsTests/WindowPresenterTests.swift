import AppKit
import Combine
import UniformTypeIdentifiers
import XCTest
@testable import TinyClips

final class StudioVideoAssociationTests: XCTestCase {
    func testShellDispatchRecognizesLocalVideosOnly() throws {
        XCTAssertTrue(StudioVideoFile.supports(URL(fileURLWithPath: "/tmp/My Video.MP4")))
        XCTAssertTrue(StudioVideoFile.supports(URL(fileURLWithPath: "/tmp/My Video.mov")))
        XCTAssertFalse(StudioVideoFile.supports(URL(fileURLWithPath: "/tmp/My Video.mp4.png")))
        XCTAssertFalse(StudioVideoFile.supports(URL(fileURLWithPath: "/tmp/My Project.tinyclips")))
        let remoteVideo = try XCTUnwrap(URL(string: "https://example.com/video.mp4"))
        XCTAssertFalse(StudioVideoFile.supports(remoteVideo))
    }

    func testBothDistributionsRegisterVideosAsAlternateHandlers() throws {
        let targetDirectory = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
            .deletingLastPathComponent().appendingPathComponent("TinyClips")
        for name in ["Info.plist", "Info-MAS.plist"] {
            let data = try Data(contentsOf: targetDirectory.appendingPathComponent(name))
            let plist = try XCTUnwrap(
                PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any]
            )
            let types = try XCTUnwrap(plist["CFBundleDocumentTypes"] as? [[String: Any]])
            for contentType in [UTType.mpeg4Movie.identifier, UTType.quickTimeMovie.identifier] {
                let handlers = types.filter { ($0["LSItemContentTypes"] as? [String])?.contains(contentType) == true }
                XCTAssertEqual(handlers.count, 1, "\(contentType) in \(name)")
                XCTAssertEqual(handlers.first?["LSHandlerRank"] as? String, "Alternate", name)
                XCTAssertEqual(handlers.first?["CFBundleTypeRole"] as? String, "Editor", name)
            }
        }
    }
}

@MainActor
final class WindowPresenterTests: XCTestCase {
    @MainActor
    private final class FakeWindow: WindowPresentationTarget {
        var isVisible = false
        var isMiniaturized = false
        var isKeyWindow = false
        var hasAttachedSheet = false
        let record: (String) -> Void

        init(record: @escaping (String) -> Void) {
            self.record = record
        }

        func prepareForPresentation() {
            record("prepare")
        }

        func deminiaturize(_ sender: Any?) {
            isMiniaturized = false
            record("restore")
        }

        func makeKeyAndOrderFront(_ sender: Any?) {
            isVisible = true
            record("makeKey")
        }

        func orderFrontRegardless() {
            record("orderFront")
        }
    }

    @MainActor
    private final class Harness {
        let ownProcess: pid_t = 42
        var frontmostProcess: pid_t? = 10
        var applicationIsActive = false
        var hasKeyWindow = false
        var events: [String] = []
        var delays: [TimeInterval] = []
        var retries: [@MainActor () -> Void] = []

        lazy var presenter = WindowPresenter(
            processIdentifier: ownProcess,
            activate: { [unowned self] in events.append("activate") },
            isApplicationActive: { [unowned self] in applicationIsActive },
            hasKeyWindow: { [unowned self] in hasKeyWindow },
            frontmostProcessIdentifier: { [unowned self] in frontmostProcess },
            scheduleRetry: { [unowned self] delay, action in
                delays.append(delay)
                retries.append(action)
            }
        )

        func makeWindow() -> FakeWindow {
            FakeWindow { [unowned self] in events.append($0) }
        }
    }

    func testPresentationRestoresAndOrdersTheRequestedWindow() {
        let harness = Harness()
        let window = harness.makeWindow()
        window.isMiniaturized = true

        harness.presenter.present(window)

        XCTAssertEqual(harness.events, ["prepare", "activate", "restore", "makeKey", "orderFront"])
        XCTAssertEqual(harness.delays, [0.1])
    }

    func testRetryWhenOriginalAppIsStillFrontmost() {
        let harness = Harness()
        let window = harness.makeWindow()
        harness.presenter.present(window)
        harness.events.removeAll()

        harness.retries[0]()

        XCTAssertEqual(harness.events, ["activate", "makeKey", "orderFront"])
        harness.retries[0]()
        XCTAssertEqual(harness.events, ["activate", "makeKey", "orderFront"])
    }

    func testRetryWhenAppIsActiveButHasNoKeyWindow() {
        let harness = Harness()
        let window = harness.makeWindow()
        harness.presenter.present(window)
        harness.applicationIsActive = true
        harness.events.removeAll()

        harness.retries[0]()

        XCTAssertEqual(harness.events, ["activate", "makeKey", "orderFront"])
    }

    func testRetryRestoresRequestedWindowWhenAnotherKeyWindowExists() {
        let harness = Harness()
        let window = harness.makeWindow()
        harness.presenter.present(window)
        harness.applicationIsActive = true
        harness.hasKeyWindow = true
        window.isKeyWindow = false
        harness.events.removeAll()

        harness.retries[0]()

        XCTAssertEqual(harness.events, ["activate", "makeKey", "orderFront"])
    }

    func testRetryDoesNotReopenAClosedOrMinimizedWindow() {
        for minimized in [false, true] {
            let harness = Harness()
            let window = harness.makeWindow()
            harness.presenter.present(window)
            window.isVisible = minimized
            window.isMiniaturized = minimized
            harness.events.removeAll()

            harness.retries[0]()

            XCTAssertTrue(harness.events.isEmpty)
        }
    }

    func testRetryDoesNotStealFocusFromAnotherApp() {
        let harness = Harness()
        let window = harness.makeWindow()
        harness.presenter.present(window)
        harness.frontmostProcess = 20
        harness.events.removeAll()

        harness.retries[0]()

        XCTAssertTrue(harness.events.isEmpty)
    }

    func testRetryDoesNotStealFocusAfterReturningToTheOriginalApp() {
        let harness = Harness()
        let window = harness.makeWindow()
        harness.presenter.present(window)
        harness.presenter.frontmostApplicationDidChange(to: harness.ownProcess)
        harness.presenter.frontmostApplicationDidChange(to: 10)
        harness.events.removeAll()

        harness.retries[0]()

        XCTAssertTrue(harness.events.isEmpty)
    }

    func testRetryDoesNotStealFocusAfterAThirdAppWasActivated() {
        let harness = Harness()
        let window = harness.makeWindow()
        harness.presenter.present(window)
        harness.presenter.frontmostApplicationDidChange(to: 20)
        harness.presenter.frontmostApplicationDidChange(to: 10)
        harness.events.removeAll()

        harness.retries[0]()

        XCTAssertTrue(harness.events.isEmpty)
    }

    func testRetryDoesNotStealFocusAfterResignation() {
        let harness = Harness()
        let window = harness.makeWindow()
        harness.presenter.present(window)
        harness.presenter.applicationDidResignActive()
        harness.events.removeAll()

        harness.retries[0]()

        XCTAssertTrue(harness.events.isEmpty)
    }

    func testNewPresentationSupersedesAnEarlierRetry() {
        let harness = Harness()
        let first = harness.makeWindow()
        let second = harness.makeWindow()
        harness.presenter.present(first)
        harness.presenter.present(second)
        harness.events.removeAll()

        harness.retries[0]()
        XCTAssertTrue(harness.events.isEmpty)
        harness.retries[1]()
        XCTAssertEqual(harness.events, ["activate", "makeKey", "orderFront"])
    }

    func testRetryDoesNotRetainTheWindow() {
        let harness = Harness()
        do {
            let window = harness.makeWindow()
            harness.presenter.present(window)
        }
        harness.events.removeAll()

        harness.retries[0]()

        XCTAssertTrue(harness.events.isEmpty)
    }

    func testRetryRequiresAKnownOriginalAppWhenInactive() {
        let harness = Harness()
        harness.frontmostProcess = nil
        let window = harness.makeWindow()
        harness.presenter.present(window)
        harness.events.removeAll()

        harness.retries[0]()

        XCTAssertTrue(harness.events.isEmpty)
    }

    func testScreenshotWindowIsPresentedOnceWhenItAttaches() {
        let harness = Harness()
        let window = harness.makeWindow()
        let registry = ScreenshotEditorRegistry(
            presentWindow: { harness.presenter.present($0) },
            applyActivationPolicy: {}
        )
        var sessionID: UUID?
        registry.installOpener { sessionID = $0 }
        registry.present(imageURL: URL(fileURLWithPath: "/test/screenshot.png")) { _ in }
        XCTAssertTrue(harness.events.isEmpty)

        guard let sessionID else {
            return XCTFail("The screenshot scene was not opened")
        }
        registry.windowDidAttach(window, for: sessionID)
        XCTAssertEqual(harness.events, ["prepare", "activate", "makeKey", "orderFront"])
        harness.events.removeAll()
        registry.windowDidAttach(window, for: sessionID)
        XCTAssertTrue(harness.events.isEmpty)
    }

    func testScreenshotWindowDoesNotPresentAfterSessionFinishes() {
        let harness = Harness()
        let window = harness.makeWindow()
        let registry = ScreenshotEditorRegistry(
            presentWindow: { harness.presenter.present($0) },
            applyActivationPolicy: {}
        )
        var sessionID: UUID?
        registry.present(imageURL: URL(fileURLWithPath: "/test/screenshot.png")) { _ in }
        registry.installOpener { sessionID = $0 }

        guard let sessionID else {
            return XCTFail("The pending screenshot scene was not opened")
        }
        registry.finish(sessionID, result: nil)
        registry.windowDidAttach(window, for: sessionID)
        XCTAssertTrue(harness.events.isEmpty)
    }

    func testReopeningTheSameScreenshotReusesPendingAndAttachedSessions() throws {
        let harness = Harness()
        let window = harness.makeWindow()
        let registry = ScreenshotEditorRegistry(
            presentWindow: { harness.presenter.present($0) },
            applyActivationPolicy: {}
        )
        var opened: [UUID] = []
        var originalCompletions = 0
        var duplicateCompletions = 0
        registry.installOpener { opened.append($0) }
        let source = URL(fileURLWithPath: "/test/screenshot.png")
        registry.present(imageURL: source) { _ in originalCompletions += 1 }
        registry.present(imageURL: source) { _ in duplicateCompletions += 1 }
        XCTAssertEqual(opened.count, 1)

        let id = try XCTUnwrap(opened.first)
        registry.windowDidAttach(window, for: id)
        harness.events.removeAll()
        window.isMiniaturized = true
        registry.present(imageURL: source) { _ in duplicateCompletions += 1 }
        XCTAssertEqual(opened.count, 1)
        XCTAssertTrue(harness.events.contains("restore"))

        registry.session(for: id)?.lifecycle.completionResult = source
        registry.windowWillClose(for: id)
        registry.windowWillClose(for: id)
        XCTAssertEqual(originalCompletions, 1)
        XCTAssertEqual(duplicateCompletions, 0)
        registry.present(imageURL: source) { _ in }
        registry.present(imageURL: URL(fileURLWithPath: "/test/other.png")) { _ in }
        XCTAssertEqual(opened.count, 3)
    }

    func testScreenshotIdentityResolvesSymlinks() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let source = directory.appendingPathComponent("source.png")
        try Data().write(to: source)
        let alias = directory.appendingPathComponent("alias.png")
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: source)
        let registry = ScreenshotEditorRegistry(presentWindow: { _ in }, applyActivationPolicy: {})
        var opened: [UUID] = []
        registry.installOpener { opened.append($0) }
        registry.present(imageURL: source) { _ in }
        registry.present(imageURL: alias) { _ in }
        XCTAssertEqual(opened.count, 1)
    }

    func testFinishingPendingSessionDoesNotOpenItLater() throws {
        let registry = ScreenshotEditorRegistry(presentWindow: { _ in }, applyActivationPolicy: {})
        registry.present(imageURL: URL(fileURLWithPath: "/test/source.png")) { _ in }
        let value = try XCTUnwrap(registry.listEditors().first?["id"] as? String)
        let id = try XCTUnwrap(UUID(uuidString: value))
        registry.finish(id, result: nil)
        var opened: [UUID] = []
        registry.installOpener { opened.append($0) }
        XCTAssertTrue(opened.isEmpty)
    }

    func testSafeCloseLeavesDirtyUnsavedAndSavingEditorsOpen() async throws {
        let registry = ScreenshotEditorRegistry(presentWindow: { _ in }, applyActivationPolicy: {})
        var ids: [UUID] = []
        registry.installOpener { ids.append($0) }
        let models = (0..<4).map { ScreenshotEditorViewModel(url: URL(fileURLWithPath: "/test/\($0).png")) }
        let windows = models.map { _ in FakeWindow(record: { _ in }) }
        for index in models.indices {
            registry.present(
                imageURL: URL(fileURLWithPath: "/test/\(index).png"),
                deleteSourceAfterSave: index == 1
            ) { _ in }
            let session = try XCTUnwrap(registry.session(for: ids[index]))
            registry.windowDidAttach(windows[index], for: ids[index])
            session.lifecycle.attach(models[index])
        }
        models[0].markDirty()
        registry.session(for: ids[2])?.lifecycle.isSaving = true
        let clean = try XCTUnwrap(registry.session(for: ids[3]))
        let request = expectation(description: "Safe editor requested closure")
        let observer = clean.lifecycle.$closeRequest.dropFirst().compactMap { $0 }.sink { _ in
            request.fulfill()
        }
        let closing = Task { try await registry.closeAll(mode: .safeOnly) }
        await fulfillment(of: [request], timeout: 2)
        registry.finish(ids[3], result: nil)
        let result = try await closing.value
        withExtendedLifetime(observer) {}
        XCTAssertEqual(result.closed, [ids[3].uuidString])
        XCTAssertFalse(result.cancelled)
        XCTAssertEqual(
            registry.listEditors().compactMap { $0["reason"] as? String },
            ["unsavedChanges", "unsavedCapture", "saving"]
        )
    }

    func testSafeCloseKeepsAnEditorWithADrawingInProgressOrAPenStroke() async throws {
        let registry = ScreenshotEditorRegistry(presentWindow: { _ in }, applyActivationPolicy: {})
        var ids: [UUID] = []
        registry.installOpener { ids.append($0) }
        let source = URL(fileURLWithPath: "/test/pen.png")
        let model = ScreenshotEditorViewModel(url: source)
        let window = FakeWindow(record: { _ in })
        registry.present(imageURL: source) { _ in }
        let lifecycle = try XCTUnwrap(registry.session(for: ids[0])?.lifecycle)
        registry.windowDidAttach(window, for: ids[0])
        lifecycle.attach(model)
        XCTAssertNil(lifecycle.blockReason)

        model.selectedTool = .pencil
        let start = CGPoint(x: 0.2, y: 0.2)
        model.handleDrag(start: start, current: CGPoint(x: 0.3, y: 0.3))
        model.handleDrag(start: start, current: CGPoint(x: 0.4, y: 0.35))
        // A close request for an editor wrongly judged safe would wait on a view this test lacks.
        guard lifecycle.blockReason == .busy else {
            return XCTFail("An editor with a stroke in progress must be busy")
        }
        var result = try await registry.closeAll(mode: .safeOnly)
        XCTAssertTrue(result.closed.isEmpty)
        XCTAssertTrue(result.blocked)

        model.handleDragEnd(start: start, end: CGPoint(x: 0.4, y: 0.35))
        XCTAssertEqual(model.annotations.count, 1)
        guard lifecycle.blockReason == .unsavedChanges else {
            return XCTFail("An editor with a new pen stroke must have unsaved changes")
        }
        result = try await registry.closeAll(mode: .safeOnly)
        XCTAssertTrue(result.closed.isEmpty)
        XCTAssertNotNil(registry.session(for: ids[0]))
        XCTAssertNil(lifecycle.closeRequest)
    }

    func testInteractiveCloseStopsOnCancelAndRejectsConcurrentBulkClose() async throws {
        var presented: [ObjectIdentifier] = []
        let registry = ScreenshotEditorRegistry(
            presentWindow: { presented.append(ObjectIdentifier($0)) },
            applyActivationPolicy: {}
        )
        var ids: [UUID] = []
        registry.installOpener { ids.append($0) }
        let models = (0..<2).map { ScreenshotEditorViewModel(url: URL(fileURLWithPath: "/test/\($0).png")) }
        let windows = models.map { _ in FakeWindow(record: { _ in }) }
        for index in models.indices {
            registry.present(imageURL: URL(fileURLWithPath: "/test/\(index).png")) { _ in }
            let session = try XCTUnwrap(registry.session(for: ids[index]))
            registry.windowDidAttach(windows[index], for: ids[index])
            session.lifecycle.attach(models[index])
        }
        models[0].markDirty()
        let lifecycle = try XCTUnwrap(registry.session(for: ids[0])?.lifecycle)
        let request = expectation(description: "First editor requested closure")
        let observer = lifecycle.$closeRequest.dropFirst().compactMap { $0 }.sink { _ in request.fulfill() }
        let closing = Task { try await registry.closeAll(mode: .interactive) }
        await fulfillment(of: [request], timeout: 2)
        // The editor that has to ask is brought forward so its prompt can be seen.
        XCTAssertEqual(presented.last, ObjectIdentifier(windows[0]))
        XCTAssertEqual(presented.count, 3)
        do {
            _ = try await registry.closeAll(mode: .safeOnly)
            XCTFail("Concurrent bulk close should fail")
        } catch let error as AutomationError {
            XCTAssertEqual(error.code, .busy)
        }
        lifecycle.resolve(.cancelled)
        let result = try await closing.value
        withExtendedLifetime(observer) {}
        XCTAssertTrue(result.cancelled)
        XCTAssertTrue(result.closed.isEmpty)
        XCTAssertNil(registry.session(for: ids[1])?.lifecycle.closeRequest)
        XCTAssertFalse(registry.isClosingAll)
    }

    func testSafeCloseRechecksAccessBeforeEveryEditor() async throws {
        let registry = ScreenshotEditorRegistry(presentWindow: { _ in }, applyActivationPolicy: {})
        registry.present(imageURL: URL(fileURLWithPath: "/test/one.png")) { _ in }
        registry.present(imageURL: URL(fileURLWithPath: "/test/two.png")) { _ in }
        var checks = 0
        do {
            _ = try await registry.closeAll(mode: .safeOnly) {
                checks += 1
                if checks == 2 { throw AutomationError(.unauthorized) }
            }
            XCTFail("Revoked access should stop the command")
        } catch let error as AutomationError {
            XCTAssertEqual(error.code, .unauthorized)
        }
        XCTAssertEqual(checks, 2)
        XCTAssertFalse(registry.isClosingAll)
        XCTAssertEqual(registry.listEditors().count, 2)
    }
}
