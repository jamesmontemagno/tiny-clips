import Carbon.HIToolbox
import XCTest
@testable import TinyClips

final class CaptureSettingsTests: XCTestCase {
    private var suiteName: String!
    private var defaults: UserDefaults!

    override func setUpWithError() throws {
        suiteName = "TinyClipsTests.\(UUID().uuidString)"
        defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defaults.removePersistentDomain(forName: suiteName)
    }

    override func tearDownWithError() throws {
        defaults.removePersistentDomain(forName: suiteName)
        defaults = nil
        suiteName = nil
    }

    func testImageFormatFallsBackToJpeg() {
        XCTAssertEqual(CaptureSettings.imageFormat(from: "png"), .png)
        XCTAssertEqual(CaptureSettings.imageFormat(from: "invalid"), .jpeg)
    }

    func testHotKeyDefaultsAndRoundTripUseIsolatedDefaults() {
        let settings = CaptureSettings(defaults: defaults, performMigrations: false)

        for action in HotKeyAction.allCases {
            XCTAssertEqual(
                settings.hotKeyBinding(for: action),
                HotKeyBinding.defaultBinding(for: action)
            )

            let custom = HotKeyBinding(
                keyCode: kVK_ANSI_A + Int(action.rawValue),
                carbonModifiers: Int(cmdKey | shiftKey)
            )
            settings.setHotKeyBinding(custom, for: action)

            XCTAssertEqual(
                settings.hotKeyBinding(for: action),
                custom
            )
        }
    }
}
