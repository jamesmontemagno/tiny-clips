import AppKit
import Darwin
import ImageIO
import UniformTypeIdentifiers
import XCTest
@testable import TinyClips

final class AutomationMediaEditingTests: XCTestCase {
    private func assertInvalid(_ block: () throws -> Void, file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertThrowsError(try block(), file: file, line: line) { error in
            XCTAssertEqual((error as? AutomationError)?.code, .invalidParams, file: file, line: line)
        }
    }

    func testClampKeepsValidRangeAndCapsEnd() throws {
        XCTAssertEqual(try AutomationMediaRange.clamp(start: 1, end: 3, duration: 10), .init(start: 1, end: 3))
        XCTAssertEqual(try AutomationMediaRange.clamp(start: 2, end: 99, duration: 10), .init(start: 2, end: 10))
    }

    func testClampRejectsInvalidRanges() {
        assertInvalid { _ = try AutomationMediaRange.clamp(start: -1, end: 2, duration: 10) }
        assertInvalid { _ = try AutomationMediaRange.clamp(start: 3, end: 3, duration: 10) }
        assertInvalid { _ = try AutomationMediaRange.clamp(start: 4, end: 2, duration: 10) }
        assertInvalid { _ = try AutomationMediaRange.clamp(start: 10, end: 12, duration: 10) }
        assertInvalid { _ = try AutomationMediaRange.clamp(start: .nan, end: 2, duration: 10) }
    }

    func testGifFramesCoverRequestedSeconds() throws {
        // Frames start at 0, 0.1, 0.3, 0.6 and the GIF lasts 1.0 s.
        let timing = try GifTiming(frameDelays: [0.1, 0.2, 0.3, 0.4], frameCount: 4)
        XCTAssertEqual(AutomationMediaRange.gifFrames(for: .init(start: 0, end: 1), timing: timing), 0...3)
        XCTAssertEqual(AutomationMediaRange.gifFrames(for: .init(start: 0.15, end: 0.3), timing: timing), 1...1)
        XCTAssertEqual(AutomationMediaRange.gifFrames(for: .init(start: 0.15, end: 0.31), timing: timing), 1...2)
        XCTAssertEqual(AutomationMediaRange.gifFrames(for: .init(start: 0.65, end: 0.7), timing: timing), 3...3)
    }

    func testGifFrameAtTime() throws {
        let timing = try GifTiming(frameDelays: [0.1, 0.2, 0.3, 0.4], frameCount: 4)
        XCTAssertEqual(try AutomationMediaRange.gifFrame(at: 0, timing: timing), 0)
        XCTAssertEqual(try AutomationMediaRange.gifFrame(at: 0.1, timing: timing), 1)
        XCTAssertEqual(try AutomationMediaRange.gifFrame(at: 0.59, timing: timing), 2)
        XCTAssertEqual(try AutomationMediaRange.gifFrame(at: 0.99, timing: timing), 3)
        assertInvalid { _ = try AutomationMediaRange.gifFrame(at: 1.0, timing: timing) }
        assertInvalid { _ = try AutomationMediaRange.gifFrame(at: -0.1, timing: timing) }
    }

    func testFractionalGifBoundariesSelectExactlyOverlappingFrames() throws {
        let timing = try GifTiming(frameCount: 10, frameDelay: 0.1)
        XCTAssertEqual(AutomationMediaRange.gifStartTime(of: 10, timing: timing), 1.0)
        XCTAssertEqual(try AutomationMediaRange.gifFrame(at: 0.3, timing: timing), 3)
        XCTAssertEqual(try AutomationMediaRange.gifFrame(at: 0.3 - 0.000001, timing: timing), 2)
        XCTAssertEqual(try AutomationMediaRange.gifFrame(at: 0.3 + 0.000001, timing: timing), 3)
        XCTAssertEqual(AutomationMediaRange.gifFrames(for: .init(start: 0, end: 0.8), timing: timing), 0...7)
        XCTAssertEqual(AutomationMediaRange.gifFrames(for: .init(start: 0.3, end: 0.8), timing: timing), 3...7)
        XCTAssertEqual(AutomationMediaRange.gifFrames(for: .init(start: 0, end: 0.8 + 0.000001), timing: timing), 0...8)
        assertInvalid { _ = try AutomationMediaRange.gifFrame(at: 1.0, timing: timing) }
    }

    func testCentisecondBoundaryDoesNotDriftAcrossManyFrames() throws {
        let timing = try GifTiming(frameCount: 1000, frameDelay: 0.01)
        XCTAssertEqual(AutomationMediaRange.gifStartTime(of: 1000, timing: timing), 10.0)
        XCTAssertEqual(try AutomationMediaRange.gifFrame(at: 0.29, timing: timing), 29)
        XCTAssertEqual(AutomationMediaRange.gifFrames(for: .init(start: 0, end: 0.29), timing: timing), 0...28)
    }

    private func cropDirectory() throws -> URL {
        // Darwin's /var can be a symlink; exercise the same no-symlinks policy as real input.
        let physicalPath = try XCTUnwrap(realpath(FileManager.default.temporaryDirectory.path, nil))
        defer { free(physicalPath) }
        let directory = URL(fileURLWithPath: String(cString: physicalPath)).appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    @MainActor
    func testCropUsesEncodedPixelsAndNeverOverwritesFiles() throws {
        let directory = try cropDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let source = directory.appendingPathComponent("source.png")
        let output = directory.appendingPathComponent("crop.png")
        let bitmap = try XCTUnwrap(NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 4, pixelsHigh: 2,
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
            bytesPerRow: 0, bitsPerPixel: 0))
        bitmap.size = CGSize(width: 2, height: 1) // 2× pixels, not points
        for x in 0..<4 {
            bitmap.setColor(x == 2 ? .red : .green, atX: x, y: 0)
            bitmap.setColor(.blue, atX: x, y: 1)
        }
        let original = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
        try original.write(to: source)
        let region = ["x": 2, "y": 0, "width": 2, "height": 1]
        let result = try AutomationMediaEditing.crop(source: source, region: region) { output }
        XCTAssertEqual(result.width, 2)
        XCTAssertEqual(result.height, 1)
        let bytes = try Data(contentsOf: result.url)
        XCTAssertTrue(bytes.starts(with: [137, 80, 78, 71, 13, 10, 26, 10]))
        let png = try XCTUnwrap(NSBitmapImageRep(data: bytes))
        XCTAssertEqual(png.pixelsWide, 2)
        XCTAssertEqual(png.pixelsHigh, 1)
        let first = try XCTUnwrap(png.colorAt(x: 0, y: 0)?.usingColorSpace(.deviceRGB))
        let last = try XCTUnwrap(png.colorAt(x: 1, y: 0)?.usingColorSpace(.deviceRGB))
        XCTAssertGreaterThan(first.redComponent, 0.9)
        XCTAssertGreaterThan(last.greenComponent, 0.9)
        XCTAssertThrowsError(try AutomationMediaEditing.crop(source: source, region: region) { source })
        XCTAssertThrowsError(try AutomationMediaEditing.crop(source: source, region: region) { output })
        XCTAssertEqual(try Data(contentsOf: source), original)
        XCTAssertEqual(try Data(contentsOf: output), bytes)

        let rotated = directory.appendingPathComponent("rotated.jpg")
        let encoder = try XCTUnwrap(CGImageDestinationCreateWithURL(rotated as CFURL, UTType.jpeg.identifier as CFString, 1, nil))
        CGImageDestinationAddImage(encoder, try XCTUnwrap(bitmap.cgImage), [kCGImagePropertyOrientation: 6] as CFDictionary)
        XCTAssertTrue(CGImageDestinationFinalize(encoder))
        let fromJPEG = try AutomationMediaEditing.crop(source: rotated, region: region) { directory.appendingPathComponent("jpeg-crop.png") }
        XCTAssertEqual(fromJPEG.width, 2) // x = 2 would be outside an EXIF-rotated 2 × 4 preview.

        let bad: [[String: Any]] = [[:], ["x": -1, "y": 0, "width": 1, "height": 1],
            ["x": true, "y": 0, "width": 1, "height": 1], ["x": 0, "y": 0.5, "width": 1, "height": 1],
            ["x": 0, "y": 0, "width": 0, "height": 1], ["x": 0, "y": 1, "width": 1, "height": 2],
            ["x": 4, "y": 0, "width": Int32.max - 1, "height": 1], ["x": Int.max, "y": 0, "width": 1, "height": 1]]
        for invalid in bad {
            assertInvalid { _ = try AutomationMediaEditing.crop(source: source, region: invalid) { XCTFail("Unexpected write"); return output } }
        }
        let alias = directory.appendingPathComponent("alias.png")
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: source)
        XCTAssertThrowsError(try AutomationMediaEditing.crop(source: alias, region: region) { output }) {
            XCTAssertEqual(($0 as? AutomationError)?.code, .notFound)
        }
    }

    func testCropLibraryMembershipDoesNotAuthorizeTargetsOfRecentSymlinks() throws {
        let root = try cropDirectory()
        let library = root.appendingPathComponent("clips")
        try FileManager.default.createDirectory(at: library, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let outside = root.appendingPathComponent("outside.png")
        let inside = library.appendingPathComponent("inside.png")
        let link = library.appendingPathComponent("link.png")
        try Data([1]).write(to: outside)
        try Data([1]).write(to: inside)
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: outside)
        func allowed(_ url: URL, recent: [String] = []) -> Bool {
            AutomationLibrary.contains(url, directories: [library], recentPaths: recent, allowRecentAliases: false)
        }
        XCTAssertTrue(allowed(inside))
        XCTAssertFalse(allowed(outside))
        XCTAssertFalse(allowed(link))
        XCTAssertFalse(allowed(outside, recent: [link.path]))
        XCTAssertTrue(allowed(outside, recent: [outside.path]))
    }

    @MainActor
    func testRouterSupportsMediaEditingCommands() {
        XCTAssertTrue(AutomationCommandRouter.supportedCommands.contains("editor.trim"))
        XCTAssertTrue(AutomationCommandRouter.supportedCommands.contains("editor.saveFrame"))
    }
}
