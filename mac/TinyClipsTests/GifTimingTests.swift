import XCTest
@testable import TinyClips

final class GifTimingTests: XCTestCase {
    func testUnequalDelaysAndFrameStartTimes() throws {
        let timing = try GifTiming(frameDelays: [0.1, 0.5, 0.2], frameCount: 3)
        XCTAssertEqual(timing.frameDelays, [0.1, 0.5, 0.2])
        XCTAssertEqual(timing.duration, 0.8, accuracy: 1e-12)
        XCTAssertEqual(timing.startTime(of: 0), 0)
        XCTAssertEqual(timing.startTime(of: 1), 0.1, accuracy: 1e-12)
        XCTAssertEqual(timing.startTime(of: 2), 0.6, accuracy: 1e-12)
    }

    func testInclusiveTrimKeepsItsOwnDelaysAndResetsOrigin() throws {
        let timing = try GifTiming(frameDelays: [0.1, 0.5, 0.2], frameCount: 3)
        let selected = try timing.selecting(1...2)
        XCTAssertEqual(selected.frameDelays, [0.5, 0.2])
        XCTAssertEqual(selected.startTime(of: 0), 0)
        XCTAssertEqual(selected.startTime(of: 1), 0.5, accuracy: 1e-12)
        XCTAssertEqual(selected.duration, 0.7, accuracy: 1e-12)
        XCTAssertEqual(timing.duration(in: 1...2), selected.duration, accuracy: 1e-12)
        XCTAssertEqual(try timing.selecting(0...2), timing)
    }

    func testEverySingleFrameRangeRetainsItsDelay() throws {
        let timing = try GifTiming(frameDelays: [0.1, 0.5, 0.2], frameCount: 3)
        for index in timing.frameDelays.indices {
            let selected = try timing.selecting(index...index)
            XCTAssertEqual(selected.frameDelays, [timing.frameDelays[index]])
            XCTAssertEqual(selected.duration, timing.frameDelays[index], accuracy: 1e-12)
            XCTAssertEqual(timing.duration(in: index...index), selected.duration, accuracy: 1e-12)
        }
    }

    func testSpeedScalesEveryDelayWithoutChangingSource() throws {
        let timing = try GifTiming(frameDelays: [0.1, 0.5, 0.2], frameCount: 3)
        for speed in [0.5, 0.75, 1.0, 1.1, 1.25, 1.5, 2.0] {
            let scaled = timing.scaled(by: speed)
            for index in timing.frameDelays.indices {
                XCTAssertEqual(scaled.frameDelays[index], timing.frameDelays[index] / speed, accuracy: 1e-12)
            }
            XCTAssertEqual(scaled.duration, 0.8 / speed, accuracy: 1e-12)
            XCTAssertEqual(scaled.startTime(of: 2), 0.6 / speed, accuracy: 1e-12)
        }
        XCTAssertEqual(timing.frameDelays, [0.1, 0.5, 0.2])
    }

    func testSpeedMinimumIsAppliedPerFrameBeforeSumming() throws {
        let timing = try GifTiming(frameDelays: [0.01, 0.02, 0.5], frameCount: 3)
        let fast = timing.scaled(by: 2)
        XCTAssertEqual(fast.frameDelays, [0.01, 0.01, 0.25])
        XCTAssertEqual(fast.duration, 0.27, accuracy: 1e-12)
        XCTAssertEqual(fast.duration(in: 0...1), 0.02, accuracy: 1e-12)
        XCTAssertEqual(try timing.selecting(0...1).scaled(by: 2).duration, 0.02, accuracy: 1e-12)
    }

    func testUniformCaptureTimingIsUnchanged() throws {
        for rate in [5.0, 10.0, 30.0] {
            let timing = try GifTiming(frameCount: 30, frameDelay: 1 / rate)
            XCTAssertEqual(timing.frameDelays, Array(repeating: 1 / rate, count: 30))
            XCTAssertEqual(timing.duration, 30 / rate, accuracy: 1e-12)
        }
    }

    func testMetadataPrecedenceAndExistingMissingDelayDefaults() {
        XCTAssertEqual(GifTiming.decodedDelay(unclamped: 0.03, clamped: 0.1), 0.03)
        XCTAssertEqual(GifTiming.decodedDelay(unclamped: nil, clamped: 0.2), 0.2)
        XCTAssertEqual(GifTiming.decodedDelay(unclamped: nil), 0.1)
        XCTAssertEqual(GifTiming.decodedDelay(unclamped: nil, fallback: 0.08), 0.08)
    }

    func testZeroNegativeAndSubminimumDelaysKeepTenMillisecondMinimum() throws {
        let timing = try GifTiming(frameDelays: [0, -1, 0.001, 0.01], frameCount: 4)
        XCTAssertEqual(timing.frameDelays, Array(repeating: 0.01, count: 4))
        XCTAssertEqual(GifTiming.decodedDelay(unclamped: 0, clamped: 0.1), 0.01)
    }

    func testNonfiniteMetadataUsesEntryPointDefault() throws {
        for value in [Double.nan, Double.infinity, -Double.infinity] {
            XCTAssertEqual(GifTiming.decodedDelay(unclamped: value), 0.1)
            XCTAssertEqual(GifTiming.decodedDelay(unclamped: value, fallback: 0.08), 0.08)
        }
        XCTAssertEqual(try GifTiming(frameDelays: [.nan, .infinity], frameCount: 2).duration, 0.2)
    }

    func testEmptyAndMisalignedTimingAreErrors() {
        XCTAssertThrowsError(try GifTiming(frameDelays: [], frameCount: 0))
        XCTAssertThrowsError(try GifTiming(frameCount: 0, frameDelay: 0.1))
        XCTAssertThrowsError(try GifTiming(frameCount: -1, frameDelay: 0.1))
        XCTAssertThrowsError(try GifTiming(frameDelays: [0.1], frameCount: 2))
        XCTAssertThrowsError(try GifTiming(frameDelays: [0.1, 0.2], frameCount: 1))
    }

    func testOutOfBoundsSelectionIsAnError() throws {
        let timing = try GifTiming(frameDelays: [0.1, 0.5, 0.2], frameCount: 3)
        XCTAssertThrowsError(try timing.selecting(-1...1))
        XCTAssertThrowsError(try timing.selecting(1...3))
    }
}
