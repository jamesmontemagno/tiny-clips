import Carbon.HIToolbox
import AVFoundation
import AppKit
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

    func testWelcomeCountdownChoiceUpdatesBothWithoutChangingDurationsOrScreenshotCountdown() {
        let settings = CaptureSettings(defaults: defaults, performMigrations: false)
        settings.videoCountdownEnabled = true
        settings.gifCountdownEnabled = false
        settings.videoCountdownDuration = 7
        settings.gifCountdownDuration = 5
        settings.screenshotCountdownEnabled = true
        settings.screenshotCountdownDuration = 9

        for enabled in [false, true] {
            settings.setRecordingCountdown(enabled)

            let reopened = CaptureSettings(defaults: defaults, performMigrations: false)
            XCTAssertEqual(reopened.videoCountdownEnabled, enabled)
            XCTAssertEqual(reopened.gifCountdownEnabled, enabled)
            XCTAssertEqual(reopened.videoCountdownDuration, 7)
            XCTAssertEqual(reopened.gifCountdownDuration, 5)
            XCTAssertTrue(reopened.screenshotCountdownEnabled)
            XCTAssertEqual(reopened.screenshotCountdownDuration, 9)
        }
    }

    func testStudioPickerIsDistinctFromVideoButRetainsVideoCaptureRouting() {
        let studio = CapturePickerIntent(captureType: .video, forStudio: true)
        XCTAssertEqual(studio, .studio)
        XCTAssertEqual(studio.label, "Studio")
        XCTAssertEqual(studio.captureType, .video)

        let video = CapturePickerIntent(captureType: .video)
        XCTAssertEqual(video, .video)
        XCTAssertEqual(video.label, "Video")
        XCTAssertEqual(video.captureType, .video)
    }

    func testOtherPickerModesCannotInheritStudioIntent() {
        for type in [CaptureType.screenshot, .gif] {
            let intent = CapturePickerIntent(captureType: type, forStudio: true)
            XCTAssertNotEqual(intent, .studio)
            XCTAssertEqual(intent.label, type.label)
            XCTAssertEqual(intent.captureType, type)
        }
    }

    func testReopenedPickerResolvesCurrentStudioIntentWithoutChangingCaptureTypes() {
        let studio = CapturePickerIntent(captureType: .video, forStudio: true)
        let reopened = CapturePickerIntent(captureType: studio.captureType, forStudio: true)
        XCTAssertEqual(reopened, .studio)

        // Cancel Studio and request ordinary Video.
        let ordinary = CapturePickerIntent(captureType: studio.captureType, forStudio: false)
        XCTAssertEqual(ordinary, .video)
        XCTAssertEqual(ordinary.label, "Video")
        XCTAssertEqual(CaptureType.video.rawValue, "video")
    }

    func testWhatsNewAppearsForExistingUsersUntilDismissed() {
        defaults.set(true, forKey: "hasCompletedOnboarding")
        XCTAssertTrue(CaptureSettings.shouldShowWhatsNew(in: defaults))

        CaptureSettings.markWhatsNewSeen(in: defaults)

        XCTAssertFalse(CaptureSettings.shouldShowWhatsNew(in: defaults))
        XCTAssertEqual(defaults.integer(forKey: CaptureSettings.lastSeenWhatsNewRevisionKey),
                       CaptureSettings.whatsNewContentRevision)
    }

    func testNewInstallsSeeOnboardingInsteadOfWhatsNew() {
        XCTAssertFalse(CaptureSettings.shouldShowWhatsNew(in: defaults))
        CaptureSettings.markWhatsNewSeen(in: defaults)
        defaults.set(true, forKey: "hasCompletedOnboarding")

        XCTAssertFalse(CaptureSettings.shouldShowWhatsNew(in: defaults))
    }

    func testPreviouslyDisabledStudioPreservesProjectsWithoutRepeatedlyResettingStorageRules() {
        defaults.set(false, forKey: "studioPreviewEnabled")
        defaults.set(14, forKey: "studioSourceRetentionDays")
        defaults.set(5, forKey: "studioStorageCapGigabytes")
        defaults.set(false, forKey: "showTrimmer")

        _ = CaptureSettings(defaults: defaults)

        XCTAssertEqual(defaults.integer(forKey: "studioSourceRetentionDays"), 0)
        XCTAssertEqual(defaults.integer(forKey: "studioStorageCapGigabytes"), 0)
        XCTAssertNil(defaults.object(forKey: "studioPreviewEnabled"))
        XCTAssertFalse(defaults.bool(forKey: "showTrimmer"))

        defaults.set(7, forKey: "studioSourceRetentionDays")
        defaults.set(2, forKey: "studioStorageCapGigabytes")
        _ = CaptureSettings(defaults: defaults)
        XCTAssertEqual(defaults.integer(forKey: "studioSourceRetentionDays"), 7)
        XCTAssertEqual(defaults.integer(forKey: "studioStorageCapGigabytes"), 2)
    }

    func testPreviouslyEnabledStudioKeepsItsStorageRules() {
        defaults.set(true, forKey: "studioPreviewEnabled")
        defaults.set(14, forKey: "studioSourceRetentionDays")
        defaults.set(5, forKey: "studioStorageCapGigabytes")

        _ = CaptureSettings(defaults: defaults)

        XCTAssertEqual(defaults.integer(forKey: "studioSourceRetentionDays"), 14)
        XCTAssertEqual(defaults.integer(forKey: "studioStorageCapGigabytes"), 5)
    }

    func testWhatsNewRevisionCanBeBumpedIndependentlyOfVersion() {
        defaults.set(true, forKey: "hasCompletedOnboarding")
        defaults.set(CaptureSettings.whatsNewContentRevision - 1,
                     forKey: CaptureSettings.lastSeenWhatsNewRevisionKey)
        XCTAssertTrue(CaptureSettings.shouldShowWhatsNew(in: defaults))

        defaults.set(CaptureSettings.whatsNewContentRevision + 1,
                     forKey: CaptureSettings.lastSeenWhatsNewRevisionKey)
        XCTAssertFalse(CaptureSettings.shouldShowWhatsNew(in: defaults))
    }

    func testResettingSettingsDoesNotReplayWhatsNewAndRemovesLegacyStudioFlag() {
        CaptureSettings.markWhatsNewSeen(in: defaults)
        defaults.set(false, forKey: "studioPreviewEnabled")

        CaptureSettings.resetStoredDefaults(defaults)

        defaults.set(true, forKey: "hasCompletedOnboarding")
        XCTAssertFalse(CaptureSettings.shouldShowWhatsNew(in: defaults))
        XCTAssertNil(defaults.object(forKey: "studioPreviewEnabled"))
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

    func testScreenshotEditorNativeSizeIsResetWithStoredSettings() {
        defaults.set(true, forKey: CaptureSettings.screenshotEditorNativeSizeKey)

        CaptureSettings.resetStoredDefaults(defaults)

        XCTAssertNil(defaults.object(forKey: CaptureSettings.screenshotEditorNativeSizeKey))
    }

    func testClickLightScreenshotExclusionDefaultsOff() {
        let settings = CaptureSettings(defaults: defaults, performMigrations: false)

        XCTAssertFalse(settings.excludeClickLightFromScreenshots)
        XCTAssertTrue(settings.screenshotExcludedApplicationBundleIdentifiers.isEmpty)
    }

    func testClickLightScreenshotExclusionPersistsAndMatchesOnlyClickLight() {
        let settings = CaptureSettings(defaults: defaults, performMigrations: false)
        settings.excludeClickLightFromScreenshots = true

        let restored = CaptureSettings(defaults: defaults, performMigrations: false)
        XCTAssertTrue(defaults.bool(forKey: "excludeClickLightFromScreenshots"))
        XCTAssertTrue(restored.excludeClickLightFromScreenshots)
        XCTAssertEqual(restored.screenshotExcludedApplicationBundleIdentifiers, ["com.aurorascharff.ClickLight"])
    }

    func testDisablingClickLightScreenshotExclusionRemovesApplicationExclusion() {
        let settings = CaptureSettings(defaults: defaults, performMigrations: false)
        settings.excludeClickLightFromScreenshots = true
        settings.excludeClickLightFromScreenshots = false

        XCTAssertFalse(defaults.bool(forKey: "excludeClickLightFromScreenshots"))
        XCTAssertTrue(settings.screenshotExcludedApplicationBundleIdentifiers.isEmpty)
    }

    func testClickLightScreenshotExclusionIsResetWithStoredSettings() {
        defaults.set(true, forKey: "excludeClickLightFromScreenshots")

        CaptureSettings.resetStoredDefaults(defaults)

        XCTAssertNil(defaults.object(forKey: "excludeClickLightFromScreenshots"))
        let settings = CaptureSettings(defaults: defaults, performMigrations: false)
        XCTAssertFalse(settings.excludeClickLightFromScreenshots)
        XCTAssertTrue(settings.screenshotExcludedApplicationBundleIdentifiers.isEmpty)
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
        XCTAssertTrue(settings.confirmEditorEscape)

        defaults.set(false, forKey: CaptureSettings.confirmEditorEscapeKey)
        CaptureSettings.resetStoredDefaults(defaults)

        XCTAssertNil(defaults.object(forKey: CaptureSettings.confirmEditorEscapeKey))
    }

    func testWebPFormatPersists() {
        let settings = CaptureSettings(defaults: defaults, performMigrations: false)
        settings.imageFormat = .webp
        XCTAssertEqual(settings.screenshotFormat, "webp")
        XCTAssertEqual(settings.imageFormat, .webp)
    }

    func testActivationPolicyIsRegularWhenDockPreferenceIsEnabled() {
        XCTAssertEqual(
            TinyClipsActivationPolicy.resolve(showInDock: true, hasOpenEditors: false),
            .regular
        )
    }

    func testActivationPolicyIsRegularWhileAnEditorIsOpen() {
        XCTAssertEqual(
            TinyClipsActivationPolicy.resolve(showInDock: false, hasOpenEditors: true),
            .regular
        )
    }

    func testOpeningMoreEditorsDoesNotReregisterTheApplicationInTheDock() {
        var current = NSApplication.ActivationPolicy.accessory
        var changes: [NSApplication.ActivationPolicy] = []
        for count in 1...5 {
            let policy = TinyClipsActivationPolicy.resolve(showInDock: false, hasOpenEditors: count > 0)
            XCTAssertTrue(TinyClipsActivationPolicy.apply(policy, current: current) {
                changes.append($0)
                current = $0
                return true
            })
        }
        XCTAssertEqual(changes, [.regular])
        XCTAssertFalse(TinyClipsActivationPolicy.apply(.accessory, current: current) { _ in false })
    }

    func testActivationPolicyIsAccessoryWithoutDockPreferenceOrOpenEditor() {
        XCTAssertEqual(
            TinyClipsActivationPolicy.resolve(showInDock: false, hasOpenEditors: false),
            .accessory
        )
    }

    func testActivationPolicyIsRegularWhileASettingsOrLibraryWindowIsOpen() {
        XCTAssertEqual(
            TinyClipsActivationPolicy.resolve(
                showInDock: false,
                hasOpenEditors: false,
                hasOpenUserWindows: true
            ),
            .regular
        )
    }

    func testQuitOnlyTerminatesTheAppWhenRequestedByTheMenuBar() {
        XCTAssertEqual(
            TinyClipsTerminationPolicy.resolve(explicitQuitRequested: false),
            .terminateCancel
        )
        XCTAssertEqual(
            TinyClipsTerminationPolicy.resolve(explicitQuitRequested: true),
            .terminateNow
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

            settings.setHotKeyBinding(.unbound, for: action)
            XCTAssertEqual(
                settings.hotKeyBinding(for: action),
                .unbound
            )
        }
    }

    func testUnboundDefaultHotKeysTreatLegacyZeroPairAsUnbound() {
        let legacyZeroActions: [(HotKeyAction, String)] = [
            (.recordVideo, "video"),
            (.studioRecording, "studioRecording"),
            (.recordGif, "gif"),
            (.stopRecording, "stopRecording"),
            (.screenshotScreen, "screenshotScreen"),
            (.screenshotRegion, "screenshotRegion"),
            (.screenshotWindow, "screenshotWindow"),
            (.copyTextFromRegion, "copyTextFromRegion"),
        ]

        for (action, prefix) in legacyZeroActions {
            defaults.set(0, forKey: "\(prefix)HotKeyCode")
            defaults.set(0, forKey: "\(prefix)HotKeyModifiers")

            XCTAssertTrue(
                CaptureSettings.hotKeyBinding(for: action, defaults: defaults).isUnbound,
                "\(action.displayName) should resolve a legacy zero pair as unbound"
            )
        }
    }

    func testSavedRecordingBindingsSurviveNewDefaultsAndResetCanClearThem() {
        let video = HotKeyBinding(keyCode: kVK_ANSI_6, carbonModifiers: HotKeyBinding.defaultCaptureModifiers)
        let studio = HotKeyBinding(keyCode: kVK_ANSI_8, carbonModifiers: HotKeyBinding.defaultCaptureModifiers)
        let gif = HotKeyBinding(keyCode: kVK_ANSI_7, carbonModifiers: HotKeyBinding.defaultCaptureModifiers)
        let stop = HotKeyBinding(keyCode: kVK_ANSI_Period, carbonModifiers: Int(cmdKey))
        for (action, binding) in [(HotKeyAction.recordVideo, video), (.studioRecording, studio), (.recordGif, gif), (.stopRecording, stop)] {
            CaptureSettings.setHotKeyBinding(binding, for: action, defaults: defaults)
        }

        let settings = CaptureSettings(defaults: defaults, performMigrations: false)
        XCTAssertEqual(settings.hotKeyBinding(for: .recordVideo), video)
        XCTAssertEqual(settings.hotKeyBinding(for: .studioRecording), studio)
        XCTAssertEqual(settings.hotKeyBinding(for: .recordGif), gif)
        XCTAssertEqual(settings.hotKeyBinding(for: .stopRecording), stop)

        CaptureSettings.resetStoredDefaults(defaults, preservingHotKeys: true)
        XCTAssertEqual(settings.hotKeyBinding(for: .studioRecording), studio)
        XCTAssertEqual(settings.hotKeyBinding(for: .stopRecording), stop)
        CaptureSettings.resetStoredDefaults(defaults)
        XCTAssertFalse(settings.hotKeyBinding(for: .screenshot).isUnbound)
        for action in HotKeyAction.allCases where action != .screenshot {
            XCTAssertTrue(settings.hotKeyBinding(for: action).isUnbound)
        }
    }
}
