import AppKit
import ImageIO
import XCTest
@testable import TinyClips

final class GifTrimmerTimingTests: XCTestCase {
    private func makeData(delays: [Double] = [0.1, 0.5, 0.2], maxWidth: CGFloat = 64) throws -> GifCaptureData {
        let frames = try delays.indices.map { index -> CGImage in
            let context = try XCTUnwrap(CGContext(
                data: nil, width: 64, height: 32, bitsPerComponent: 8, bytesPerRow: 0,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
            ))
            context.setFillColor(CGColor(red: CGFloat(index + 1) / CGFloat(delays.count + 1),
                                         green: 0.2, blue: 0.7, alpha: 1))
            context.fill(CGRect(x: 0, y: 0, width: 64, height: 32))
            return try XCTUnwrap(context.makeImage())
        }
        return try GifCaptureData(frames: frames, frameDelays: delays, maxWidth: maxWidth)
    }

    private func withFolder(_ body: (URL) throws -> Void) throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer {
            do { try FileManager.default.removeItem(at: folder) }
            catch { XCTFail("Could not clean GIF fixture: \(error)") }
        }
        try body(folder)
    }

    private func readDelays(_ url: URL) throws -> [Double] {
        let source = try XCTUnwrap(CGImageSourceCreateWithURL(url as CFURL, nil))
        return try (0..<CGImageSourceGetCount(source)).map { index in
            let properties = try XCTUnwrap(CGImageSourceCopyPropertiesAtIndex(source, index, nil) as? [CFString: Any])
            let gif = try XCTUnwrap(properties[kCGImagePropertyGIFDictionary] as? [CFString: Any])
            return try XCTUnwrap((gif[kCGImagePropertyGIFUnclampedDelayTime] as? Double)
                                ?? (gif[kCGImagePropertyGIFDelayTime] as? Double))
        }
    }

    private func assertDelays(_ actual: [Double], _ expected: [Double], file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertEqual(actual.count, expected.count, file: file, line: line)
        for (actual, expected) in zip(actual, expected) {
            XCTAssertEqual(actual, expected, accuracy: 0.000001, file: file, line: line)
        }
    }

    func testImportPathsRetainIndependentImageIODelays() throws {
        try withFolder { folder in
            let url = folder.appendingPathComponent("unequal.gif")
            let data = try makeData()
            // Build independently of GifWriter, so an encoder bug cannot hide a decoder bug.
            let destination = try XCTUnwrap(CGImageDestinationCreateWithURL(
                url as CFURL, "com.compuserve.gif" as CFString, data.frames.count, nil
            ))
            for (index, frame) in data.frames.enumerated() {
                CGImageDestinationAddImage(destination, frame, [
                    kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFDelayTime: data.timing.frameDelays[index]],
                ] as CFDictionary)
            }
            XCTAssertTrue(CGImageDestinationFinalize(destination))
            for loaded in [
                try GifCaptureData(contentsOf: url),
                try GifCaptureData(contentsOf: url, defaultFrameDelay: 0.08),
                try MouseClickOverlayProcessor.loadGifCaptureData(from: url),
            ] {
                assertDelays(loaded.timing.frameDelays, [0.1, 0.5, 0.2])
                XCTAssertEqual(loaded.frames.count, 3)
            }
        }
    }

    @MainActor
    func testExportSelectedFramesAtEverySpeedAndDownscale() async throws {
        try withFolder { folder in
            let model = GifTrimmerViewModel(gifData: try makeData(maxWidth: 32))
            model.trimStartFrame = 1
            model.trimEndFrame = 2
            for speed in [0.5, 1.0, 2.0] {
                model.speed = speed
                let url = folder.appendingPathComponent("trim-\(speed).gif")
                _ = try model.exportGif(to: url, trimmed: true)
                assertDelays(try readDelays(url), [0.5 / speed, 0.2 / speed])
                let loaded = try GifCaptureData(contentsOf: url)
                XCTAssertEqual(loaded.frames.map(\.width), [32, 32])
                XCTAssertEqual(loaded.frames.map(\.height), [16, 16])
                XCTAssertEqual(model.trimmedDurationSeconds, 0.7 / speed, accuracy: 1e-12)
            }
        }
    }

    @MainActor
    func testSingleFrameFullExportAndMinimumDelay() async throws {
        try withFolder { folder in
            let model = GifTrimmerViewModel(gifData: try makeData(delays: [0.01, 0.5, 0.2]))
            model.trimStartFrame = 1
            model.trimEndFrame = 1
            model.speed = 2
            let url = folder.appendingPathComponent("selected.gif")
            _ = try model.exportGif(to: url, trimmed: true)
            assertDelays(try readDelays(url), [0.25])
            _ = try model.exportGif(to: url, trimmed: false)
            assertDelays(try readDelays(url), [0.01, 0.25, 0.1])
        }
    }

    func testUniformCaptureAndBrandingPreserveTiming() throws {
        let source = try makeData()
        let uniform = try GifCaptureData(frames: source.frames, frameDelay: 0.1, maxWidth: 64)
        try withFolder { folder in
            let url = folder.appendingPathComponent("capture.gif")
            try GifWriter.writeGIF(uniform, to: url)
            assertDelays(try readDelays(url), [0.1, 0.1, 0.1])
            let branded = try BrandingOverlayProcessor.applyToGifData(source)
            XCTAssertEqual(branded.timing, source.timing)
            try GifWriter.writeGIF(branded, to: url)
            assertDelays(try readDelays(url), [0.1, 0.5, 0.2])
        }
    }

    func testFrameAlignmentAndSelectedImages() throws {
        let source = try makeData()
        XCTAssertThrowsError(try GifCaptureData(frames: source.frames, frameDelays: [0.1], maxWidth: 64))
        XCTAssertThrowsError(try GifCaptureData(frames: [], frameDelay: 0.1, maxWidth: 64))
        XCTAssertThrowsError(try source.replacingFrames(Array(source.frames.prefix(2))))
        let selected = try source.selecting(1...2, speed: 1)
        XCTAssertTrue(selected.frames[0] === source.frames[1])
        XCTAssertTrue(selected.frames[1] === source.frames[2])
        assertDelays(selected.timing.frameDelays, [0.5, 0.2])
    }

    @MainActor
    func testClickOverlayUsesCumulativeFrameTimes() async throws {
        guard let screen = NSScreen.screens.first,
              let displayID = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID else {
            throw XCTSkip("A native display is required to map click coordinates.")
        }
        let source = try makeData()
        let region = CaptureRegion(sourceRect: CGRect(x: 0, y: 0, width: 64, height: 32),
                                   displayID: displayID, scaleFactor: 1)
        let point = CGPoint(x: screen.frame.minX + 16, y: screen.frame.maxY - 16)
        let style = MouseClickOverlayStyle(colorHex: "#FF0000", size: 10, strokeWidth: 2, opacity: 1, duration: 0.2)
        let actual = try MouseClickOverlayProcessor.overlayOnGif(
            gifData: source, region: region,
            events: [MouseClickEvent(timeOffset: 0.55, globalLocation: point, button: .left)], style: style
        )
        let baseline = try MouseClickOverlayProcessor.overlayOnGif(
            gifData: source, region: region,
            events: [MouseClickEvent(timeOffset: 100, globalLocation: point, button: .left)], style: style
        )
        XCTAssertEqual(actual.timing, source.timing)
        for index in 0...1 {
            XCTAssertEqual(try pixels(actual.frames[index]), try pixels(baseline.frames[index]))
        }
        // The third frame starts at 0.6s, not twice the average delay (0.533s).
        XCTAssertNotEqual(try pixels(actual.frames[2]), try pixels(baseline.frames[2]))
    }

    private func pixels(_ image: CGImage) throws -> Data {
        var data = Data(count: image.width * image.height * 4)
        try data.withUnsafeMutableBytes { bytes in
            let context = try XCTUnwrap(CGContext(
                data: bytes.baseAddress, width: image.width, height: image.height,
                bitsPerComponent: 8, bytesPerRow: image.width * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
            ))
            context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        }
        return data
    }

    func testMalformedInputAndFailedExportAreErrorsWithoutReplacingSource() throws {
        try withFolder { folder in
            let url = folder.appendingPathComponent("original.gif")
            let original = Data("not a GIF".utf8)
            try original.write(to: url)
            XCTAssertThrowsError(try GifCaptureData(contentsOf: url))
            XCTAssertThrowsError(try GifWriter.writeGIF(makeData(maxWidth: 0), to: url))
            XCTAssertEqual(try Data(contentsOf: url), original)
            XCTAssertThrowsError(try GifWriter.writeGIF(makeData(), to: folder.appendingPathComponent("missing/output.gif")))
            XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: folder.path), ["original.gif"])
        }
    }

    @MainActor
    private final class Clock {
        var delays: [Double] = []
        var actions: [@MainActor () -> Void] = []
        var cancelled: Set<Int> = []

        func schedule(_ delay: Double, _ action: @escaping @MainActor () -> Void) -> () -> Void {
            let index = actions.count
            delays.append(delay)
            actions.append(action)
            return { self.cancelled.insert(index) }
        }
    }

    @MainActor
    func testPlaybackWaitsForDisplayedFrameAndLoopsInclusiveRange() async throws {
        let clock = Clock()
        let model = GifTrimmerViewModel(gifData: try makeData(), schedulePlayback: clock.schedule)
        model.trimStartFrame = 1
        model.trimEndFrame = 2
        model.togglePlayback()
        XCTAssertEqual(model.currentFrameIndex, 1)
        XCTAssertEqual(clock.delays, [0.5])
        clock.actions[0]()
        XCTAssertEqual(model.currentFrameIndex, 2)
        XCTAssertEqual(model.currentDurationSeconds, 0.6, accuracy: 1e-12)
        XCTAssertEqual(clock.delays, [0.5, 0.2])
        clock.actions[1]()
        XCTAssertEqual(model.currentFrameIndex, 1)
        XCTAssertEqual(clock.delays, [0.5, 0.2, 0.5])
        model.stopPlayback()
    }

    @MainActor
    func testSpeedSeekAndStopRejectAlreadyQueuedTicks() async throws {
        let clock = Clock()
        let model = GifTrimmerViewModel(gifData: try makeData(), schedulePlayback: clock.schedule)
        model.togglePlayback()
        model.speed = 2
        clock.actions[0]()
        XCTAssertEqual(model.currentFrameIndex, 0)
        XCTAssertTrue(clock.cancelled.contains(0))
        XCTAssertEqual(clock.delays, [0.1, 0.05])
        model.seekTo(frame: 2)
        clock.actions[1]()
        XCTAssertEqual(model.currentFrameIndex, 2)
        XCTAssertEqual(clock.delays, [0.1, 0.05, 0.1])
        model.stopPlayback()
        clock.actions[2]()
        XCTAssertFalse(model.isPlaying)
        XCTAssertEqual(model.currentFrameIndex, 2)
        XCTAssertEqual(clock.actions.count, 3)
    }

    @MainActor
    func testOneFramePreviewAndFrameStepCancellation() async throws {
        let clock = Clock()
        let model = GifTrimmerViewModel(gifData: try makeData(delays: [0.5]), schedulePlayback: clock.schedule)
        model.togglePlayback()
        clock.actions[0]()
        XCTAssertEqual(model.currentFrameIndex, 0)
        XCTAssertEqual(clock.delays, [0.5, 0.5])
        model.stepFrame(by: 1)
        clock.actions[1]()
        XCTAssertFalse(model.isPlaying)
        XCTAssertEqual(model.currentFrameIndex, 0)
        XCTAssertEqual(clock.actions.count, 2)
    }
}
