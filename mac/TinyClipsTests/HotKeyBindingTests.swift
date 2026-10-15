import AppKit
import Carbon.HIToolbox
import SwiftUI
import XCTest
@testable import TinyClips

final class HotKeyBindingTests: XCTestCase {
    func testDefaultBindingsUseExpectedKeysAndModifiers() {
        XCTAssertEqual(
            HotKeyBinding.defaultBinding(for: .screenshot),
            HotKeyBinding(keyCode: 23, carbonModifiers: HotKeyBinding.defaultCaptureModifiers)
        )
        XCTAssertEqual(
            HotKeyBinding.defaultBinding(for: .recordVideo),
            HotKeyBinding(keyCode: 22, carbonModifiers: HotKeyBinding.defaultCaptureModifiers)
        )
        XCTAssertEqual(
            HotKeyBinding.defaultBinding(for: .recordGif),
            HotKeyBinding(keyCode: 26, carbonModifiers: HotKeyBinding.defaultCaptureModifiers)
        )
        for action in [HotKeyAction.copyTextFromRegion, .screenshotRegion, .screenshotWindow, .screenshotScreen] {
            XCTAssertTrue(
                HotKeyBinding.defaultBinding(for: action).isUnbound,
                "\(action.displayName) should be unbound by default"
            )
        }
    }

    func testOnlyScreenshotVideoAndGifAreBoundByDefault() {
        let boundActions = HotKeyAction.allCases.filter {
            !HotKeyBinding.defaultBinding(for: $0).isUnbound
        }

        XCTAssertEqual(Set(boundActions), [.screenshot, .recordVideo, .recordGif])
    }

    func testUnboundBindingRepresentsAClearedShortcut() {
        XCTAssertTrue(HotKeyBinding.unbound.isUnbound)
        XCTAssertEqual(HotKeyBinding.unbound.displayString, "Not set")

        for action in HotKeyAction.allCases {
            XCTAssertNil(
                HotKeyBinding.validationError(
                    for: .unbound,
                    action: action,
                    bindings: [:]
                )
            )
        }
    }

    func testDirectScreenshotActionsHaveDisplayNamesAndUniqueDefaults() {
        XCTAssertEqual(HotKeyAction.screenshotRegion.displayName, "Screenshot Region")
        XCTAssertEqual(HotKeyAction.screenshotWindow.displayName, "Screenshot Window")
        XCTAssertEqual(HotKeyAction.screenshotScreen.displayName, "Screenshot Screen")

        let defaults = Dictionary(
            uniqueKeysWithValues: HotKeyAction.allCases.map {
                ($0, HotKeyBinding.defaultBinding(for: $0))
            }
        )
        let boundDefaults = defaults.values.filter { !$0.isUnbound }
        XCTAssertEqual(Set(boundDefaults).count, boundDefaults.count)

        for action in [HotKeyAction.screenshotRegion, .screenshotWindow, .screenshotScreen] {
            let binding = HotKeyBinding.defaultBinding(for: action)
            XCTAssertTrue(binding.isUnbound)
            XCTAssertEqual(binding.displayString, "Not set")
            XCTAssertNil(
                HotKeyBinding.validationError(
                    for: binding,
                    action: action,
                    bindings: defaults
                )
            )
        }
    }

    func testCarbonAndSwiftUIModifierConversions() {
        let carbon = HotKeyBinding.carbonModifiers(
            from: [.command, .shift, .option, .control, .capsLock]
        )
        let binding = HotKeyBinding(keyCode: kVK_ANSI_5, carbonModifiers: carbon)

        XCTAssertEqual(
            carbon,
            Int(cmdKey | shiftKey | optionKey | controlKey)
        )
        XCTAssertTrue(binding.swiftUIModifiers.contains(.command))
        XCTAssertTrue(binding.swiftUIModifiers.contains(.shift))
        XCTAssertTrue(binding.swiftUIModifiers.contains(.option))
        XCTAssertTrue(binding.swiftUIModifiers.contains(.control))
        XCTAssertEqual(binding.modifiersString, "⌃⌥⇧⌘")
    }

    func testValidationRejectsUnmodifiedReservedAndConflictingBindings() {
        let unmodified = HotKeyBinding(keyCode: kVK_ANSI_A, carbonModifiers: 0)
        XCTAssertNotNil(
            HotKeyBinding.validationError(
                for: unmodified,
                action: .screenshot,
                bindings: [:]
            )
        )

        XCTAssertTrue(
            HotKeyBinding.validationError(
                for: .stopRecording,
                action: .screenshot,
                bindings: [:]
            )?.contains("reserved") == true
        )

        let duplicate = HotKeyBinding.defaultBinding(for: .recordVideo)
        XCTAssertTrue(
            HotKeyBinding.validationError(
                for: duplicate,
                action: .screenshot,
                bindings: [.recordVideo: duplicate]
            )?.contains("Record Video") == true
        )

        let regionDuplicate = HotKeyBinding(
            keyCode: kVK_ANSI_1,
            carbonModifiers: HotKeyBinding.defaultCaptureModifiers
        )
        XCTAssertTrue(
            HotKeyBinding.validationError(
                for: regionDuplicate,
                action: .screenshotWindow,
                bindings: [.screenshotRegion: regionDuplicate]
            )?.contains("Screenshot Region") == true
        )
    }

    func testSpecialAndUnknownKeyDisplayFallbacks() {
        XCTAssertEqual(
            HotKeyBinding(keyCode: kVK_F5, carbonModifiers: Int(cmdKey)).keyString,
            "F5"
        )
        XCTAssertEqual(
            HotKeyBinding(keyCode: Int.max, carbonModifiers: Int(cmdKey)).displayString,
            "⌘?"
        )
    }
}
