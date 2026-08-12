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
