import XCTest
@testable import TinyClips

final class StudioExportPresetsTests: XCTestCase {
    private struct Size: Decodable {
        var width: Double
        var height: Double
    }

    private struct Fixture: Decodable {
        struct SizeCase: Decodable {
            var natural: Size
            var resolution: String
            var expected: Size
        }

        struct OfferedCase: Decodable {
            var natural: Size
            var expected: [String]
        }

        struct BitrateCase: Decodable {
            var width: Double
            var height: Double
            var frameRate: Double
            var codec: String
            var quality: String
            var expected: Int
        }

        struct EstimateCase: Decodable {
            var videoBitrate: Int
            var duration: Double
            var hasSound: Bool
            var expected: Int64
        }

        var sizes: [SizeCase]
        var offered: [OfferedCase]
        var bitrates: [BitrateCase]
        var estimates: [EstimateCase]
    }

    private func fixture() throws -> Fixture {
        // This file lives at <repo>/mac/TinyClipsTests/, so the repository root is three levels up.
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("shared/studio/fixtures/export/presets.json")
        return try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: url))
    }

    func testSharedFixtureSizes() throws {
        for testCase in try fixture().sizes {
            let resolution = try XCTUnwrap(StudioExportResolution(rawValue: testCase.resolution))
            let size = StudioExportPresets.size(
                naturalWidth: testCase.natural.width,
                naturalHeight: testCase.natural.height,
                resolution: resolution
            )
            XCTAssertEqual(size.width, testCase.expected.width, "\(testCase.natural) at \(testCase.resolution)")
            XCTAssertEqual(size.height, testCase.expected.height, "\(testCase.natural) at \(testCase.resolution)")
        }
    }

    func testSharedFixtureOfferedResolutions() throws {
        for testCase in try fixture().offered {
            let offered = StudioExportPresets.resolutionOptions(
                naturalWidth: testCase.natural.width,
                naturalHeight: testCase.natural.height
            ).map(\.resolution.rawValue)
            XCTAssertEqual(offered, testCase.expected, "\(testCase.natural)")
        }
    }

    func testSharedFixtureBitrates() throws {
        for testCase in try fixture().bitrates {
            let quality = try XCTUnwrap(StudioExportQuality(rawValue: testCase.quality))
            let bitrate = StudioExportPresets.videoBitrate(
                width: testCase.width,
                height: testCase.height,
                framesPerSecond: testCase.frameRate,
                isHEVC: testCase.codec == "hevc",
                quality: quality
            )
            XCTAssertEqual(bitrate, testCase.expected, "\(testCase)")
        }
    }

    func testSharedFixtureEstimates() throws {
        for testCase in try fixture().estimates {
            let bytes = StudioExportPresets.estimatedBytes(
                videoBitrate: testCase.videoBitrate,
                duration: testCase.duration,
                hasSound: testCase.hasSound
            )
            XCTAssertEqual(bytes, testCase.expected, "\(testCase)")
        }
    }

    func testOptionsOfferTheSavedChoiceAndSayWhatItComesTo() {
        var options = model(width: 3840, height: 2160, saved: .default)

        XCTAssertEqual(options.choice, .default)
        XCTAssertEqual(options.detailsText, "1920 × 1080 · 30 fps · H.264")

        let chosen = StudioExportChoice(resolution: .p720, quality: .small)
        options.choice = chosen

        XCTAssertEqual(options.detailsText, "1280 × 720 · 30 fps · H.264")
        XCTAssertEqual(options.choiceToRemember, chosen)
    }

    func testOptionsOnASmallCanvasOfferSourceAndKeepTheSavedResolutionUnlessItIsChanged() {
        var options = model(width: 1280, height: 720, saved: .default)

        XCTAssertEqual(options.choice.resolution, .source)
        XCTAssertEqual(options.choiceToRemember.resolution, .p1080)

        options.choice.resolution = .p480

        XCTAssertEqual(options.choiceToRemember.resolution, .p480)
    }

    func testOnlySizesPastThe4KFrameAreCheckedWithTheEncoder() {
        XCTAssertTrue(StudioExportPresets.fitsFourK(width: 3840, height: 2160))
        XCTAssertTrue(StudioExportPresets.fitsFourK(width: 2160, height: 3840))
        XCTAssertFalse(StudioExportPresets.fitsFourK(width: 3840, height: 2162))
        XCTAssertFalse(StudioExportPresets.fitsFourK(width: 5120, height: 2880))
    }

    func testOptionsOfferEveryQualityFromBestToSmallest() {
        let qualities = model(width: 1920, height: 1080, saved: .default).qualities

        XCTAssertEqual(qualities.map(\.quality), [.best, .high, .standard, .small])
        XCTAssertEqual(qualities.map(\.estimatedBytes), qualities.map(\.estimatedBytes).sorted(by: >))
    }

    func testDefaultChoiceIs1080pAtHighQuality() {
        XCTAssertEqual(StudioExportChoice.default, StudioExportChoice(resolution: .p1080, quality: .high))
    }

    @MainActor
    func testAnAgentsChoiceFallsBackToTheSavedOneWhenLeftOutOrNull() throws {
        let saved = StudioExportChoice(resolution: .p720, quality: .small)

        XCTAssertEqual(try AutomationCommandRouter.studioExportChoice([:], saved: saved), saved)
        XCTAssertEqual(try AutomationCommandRouter.studioExportChoice(["resolution": NSNull(), "quality": NSNull()], saved: saved), saved)
        XCTAssertEqual(
            try AutomationCommandRouter.studioExportChoice(["resolution": "source", "quality": "best"], saved: saved),
            StudioExportChoice(resolution: .source, quality: .best)
        )
        XCTAssertThrowsError(try AutomationCommandRouter.studioExportChoice(["resolution": "8k"], saved: saved))
        XCTAssertThrowsError(try AutomationCommandRouter.studioExportChoice(["quality": 3], saved: saved))
    }

    func testAStoredChoiceThatIsNotKnownCountsAsTheDefault() {
        XCTAssertEqual(StudioExportResolution(stored: "720p"), .p720)
        XCTAssertEqual(StudioExportResolution(stored: "8k"), StudioExportChoice.default.resolution)
        XCTAssertEqual(StudioExportQuality(stored: "small"), .small)
        XCTAssertEqual(StudioExportQuality(stored: "nice"), StudioExportChoice.default.quality)
        XCTAssertEqual(CaptureSettings.studioExportResolutionKey, "studioExportResolution")
    }

    private func model(width: Double, height: Double, saved: StudioExportChoice) -> StudioExportOptionsModel {
        StudioExportOptionsModel(
            naturalWidth: width,
            naturalHeight: height,
            framesPerSecond: 30,
            isHEVC: false,
            duration: 10,
            hasSound: false,
            saved: saved
        )
    }
}
