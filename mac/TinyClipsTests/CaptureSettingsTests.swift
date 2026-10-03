import Carbon.HIToolbox
import AVFoundation
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
        XCTAssertEqual(CaptureSettings.imageFormat(from: "webp"), .webp)
        XCTAssertEqual(ImageFormat.webp.utType.preferredFilenameExtension, "webp")
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
        XCTAssertEqual(RecordingVideoCodecResolver.exportPreset(for: resolved.actual), AVAssetExportPresetHEVCHighestQuality)
    }

    func testRecordingVideoCodecResolverFallsBackWhenHevcUnavailable() {
        let resolved = RecordingVideoCodecResolver.resolved(requested: .hevc, hevcAvailable: false)

        XCTAssertEqual(resolved.actual, .h264)
        XCTAssertTrue(resolved.didFallback)
        XCTAssertEqual(RecordingVideoCodecResolver.exportPreset(for: resolved.actual), AVAssetExportPresetHighestQuality)
        XCTAssertEqual(
            resolved.fallbackMessage(context: "the webcam overlay"),
            "H.265 / HEVC is not available for the webcam overlay. TinyClips is recording with H.264 instead."
        )
    }

    func testRecordingVideoCodecResolverCreatesH264InputWithoutFallback() throws {
        let outputURL = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
            .appendingPathExtension("mp4")
        defer {
            try? FileManager.default.removeItem(at: outputURL)
        }

        let writer = try AVAssetWriter(url: outputURL, fileType: .mp4)
        let result = RecordingVideoCodecResolver.makeVideoInput(
            codec: .h264,
            width: 16,
            height: 16,
            writer: writer
        )

        XCTAssertEqual(result.codec, .h264)
        XCTAssertNil(result.fallback)
        let outputSettings = try XCTUnwrap(result.input.outputSettings)
        XCTAssertEqual(outputSettings[AVVideoCodecKey] as? AVVideoCodecType, .h264)
    }

    func testAudioOffsetIsClampedToSupportedRange() {
        XCTAssertEqual(CaptureSettings.clampedAudioOffsetMs(-501), -500)
        XCTAssertEqual(CaptureSettings.clampedAudioOffsetMs(-500), -500)
        XCTAssertEqual(CaptureSettings.clampedAudioOffsetMs(0), 0)
        XCTAssertEqual(CaptureSettings.clampedAudioOffsetMs(500), 500)
        XCTAssertEqual(CaptureSettings.clampedAudioOffsetMs(501), 500)
    }

    func testAudioOffsetIsResetWithStoredSettings() {
        defaults.set(250, forKey: "audioOffsetMs")

        CaptureSettings.resetStoredDefaults(defaults)

        XCTAssertNil(defaults.object(forKey: "audioOffsetMs"))
    }

    func testEscapeConfirmationDefaultsOnAndResets() {
        let settings = CaptureSettings(defaults: defaults, performMigrations: false)
        XCTAssertTrue(settings.confirmScreenshotEditorEscape)

        defaults.set(false, forKey: CaptureSettings.confirmScreenshotEditorEscapeKey)
        CaptureSettings.resetStoredDefaults(defaults)

        XCTAssertNil(defaults.object(forKey: CaptureSettings.confirmScreenshotEditorEscapeKey))
    }

    func testWebPFormatPersists() {
        let settings = CaptureSettings(defaults: defaults, performMigrations: false)
        settings.imageFormat = .webp
        XCTAssertEqual(settings.screenshotFormat, "webp")
        XCTAssertEqual(settings.imageFormat, .webp)
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
