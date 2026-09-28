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

    func testVideoCodecDefaultsAndFallsBackToH264() {
        XCTAssertEqual(CaptureSettings.videoCodec(from: nil), .h264)
        XCTAssertEqual(CaptureSettings.videoCodec(from: "h264"), .h264)
        XCTAssertEqual(CaptureSettings.videoCodec(from: "hevc"), .hevc)
        XCTAssertEqual(CaptureSettings.videoCodec(from: "invalid"), .h264)
    }

    func testVideoCodecPersistsInDefaults() {
        XCTAssertEqual(CaptureSettings.videoCodec(in: defaults), .h264)

        CaptureSettings.setVideoCodec(.hevc, in: defaults)

        XCTAssertEqual(defaults.string(forKey: CaptureSettings.videoCodecKey), VideoCodec.hevc.rawValue)
        XCTAssertEqual(CaptureSettings.videoCodec(in: defaults), .hevc)
    }

    func testVideoCodecIsResetWithStoredSettings() {
        CaptureSettings.setVideoCodec(.hevc, in: defaults)

        CaptureSettings.resetStoredDefaults(defaults)

        XCTAssertNil(defaults.object(forKey: CaptureSettings.videoCodecKey))
        XCTAssertEqual(CaptureSettings.videoCodec(in: defaults), .h264)
    }

    func testRecordingVideoCodecResolverDefaultsToH264() {
        let resolved = RecordingVideoCodecResolver.resolved(requested: .h264, hevcAvailable: true)

        XCTAssertEqual(resolved.actual, .h264)
        XCTAssertFalse(resolved.didFallback)
        XCTAssertNil(resolved.fallbackMessage())
    }

    func testRecordingVideoCodecResolverUsesHevcWhenAvailable() {
        let resolved = RecordingVideoCodecResolver.resolved(requested: .hevc, hevcAvailable: true)

        XCTAssertEqual(resolved.actual, .hevc)
        XCTAssertFalse(resolved.didFallback)
        XCTAssertNil(resolved.fallbackMessage())
    }

    func testRecordingVideoCodecResolverFallsBackWhenHevcUnavailable() {
        let resolved = RecordingVideoCodecResolver.resolved(requested: .hevc, hevcAvailable: false)

        XCTAssertEqual(resolved.actual, .h264)
        XCTAssertTrue(resolved.didFallback)
        XCTAssertEqual(
            resolved.fallbackMessage(context: "the webcam overlay"),
            "H.265 / HEVC is not available for the webcam overlay. TinyClips is recording with H.264 instead."
        )
    }

    func testActivationPolicyIsRegularWhenDockPreferenceIsEnabled() {
        XCTAssertEqual(
            TinyClipsActivationPolicy.resolve(showInDock: true, hasOpenScreenshotEditors: false),
            .regular
        )
    }

    func testActivationPolicyIsRegularWhileScreenshotEditorIsOpen() {
        XCTAssertEqual(
            TinyClipsActivationPolicy.resolve(showInDock: false, hasOpenScreenshotEditors: true),
            .regular
        )
    }

    func testActivationPolicyIsAccessoryWithoutDockPreferenceOrScreenshotEditor() {
        XCTAssertEqual(
            TinyClipsActivationPolicy.resolve(showInDock: false, hasOpenScreenshotEditors: false),
            .accessory
        )
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
