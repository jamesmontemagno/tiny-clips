import XCTest
@testable import TinyClips

@MainActor
final class WindowPresenterTests: XCTestCase {
    @MainActor
    private final class FakeWindow: WindowPresentationTarget {
        var isVisible = false
        var isMiniaturized = false
        var isKeyWindow = false
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
}
