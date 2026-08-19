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
            HotKeyBinding.defaultBinding(for: .copyTextFromRegion),
            HotKeyBinding(keyCode: 28, carbonModifiers: HotKeyBinding.defaultCaptureModifiers)
        )
        XCTAssertEqual(
            HotKeyBinding.defaultBinding(for: .screenshotRegion),
            HotKeyBinding(keyCode: 18, carbonModifiers: HotKeyBinding.defaultCaptureModifiers)
        )
        XCTAssertEqual(
            HotKeyBinding.defaultBinding(for: .screenshotWindow),
            HotKeyBinding(keyCode: 19, carbonModifiers: HotKeyBinding.defaultCaptureModifiers)
        )
    }

    func testDirectScreenshotActionsHaveDisplayNamesAndUniqueDefaults() {
        XCTAssertEqual(HotKeyAction.screenshotRegion.displayName, "Screenshot Region")
        XCTAssertEqual(HotKeyAction.screenshotWindow.displayName, "Screenshot Window")

        let defaults = Dictionary(
            uniqueKeysWithValues: HotKeyAction.allCases.map {
                ($0, HotKeyBinding.defaultBinding(for: $0))
            }
        )
        XCTAssertEqual(Set(defaults.values).count, HotKeyAction.allCases.count)
        XCTAssertNil(
            HotKeyBinding.validationError(
                for: HotKeyBinding.defaultBinding(for: .screenshotRegion),
                action: .screenshotRegion,
                bindings: defaults
            )
        )
        XCTAssertNil(
            HotKeyBinding.validationError(
                for: HotKeyBinding.defaultBinding(for: .screenshotWindow),
                action: .screenshotWindow,
                bindings: defaults
            )
        )
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

        let regionDuplicate = HotKeyBinding.defaultBinding(for: .screenshotRegion)
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
