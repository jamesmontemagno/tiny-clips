import CoreGraphics
import XCTest
@testable import TinyClips

/// Section 15.4 of the project format: the look of a caption, and what Core Text makes of it.
/// Only a bitmap is involved, so these need nothing of the machine they run on.
final class StudioCaptionPainterTests: XCTestCase {
    private struct Pixels {
        let width: Int
        let height: Int
        let bytes: [UInt8]

        init(_ image: CGImage) {
            width = image.width
            height = image.height
            var bytes = [UInt8](repeating: 0, count: width * height * 4)
            bytes.withUnsafeMutableBytes { buffer in
                let context = CGContext(
                    data: buffer.baseAddress,
                    width: image.width,
                    height: image.height,
                    bitsPerComponent: 8,
                    bytesPerRow: image.width * 4,
                    space: CGColorSpaceCreateDeviceRGB(),
                    bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
                )
                context?.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
            }
            self.bytes = bytes
        }

        /// How many pixels are about this colour, fully covered.
        func count(red: UInt8, green: UInt8, blue: UInt8, alpha: UInt8 = 255, tolerance: Int = 3) -> Int {
            var count = 0
            for at in stride(from: 0, to: bytes.count, by: 4) {
                if abs(Int(bytes[at]) - Int(red)) <= tolerance,
                   abs(Int(bytes[at + 1]) - Int(green)) <= tolerance,
                   abs(Int(bytes[at + 2]) - Int(blue)) <= tolerance,
                   abs(Int(bytes[at + 3]) - Int(alpha)) <= tolerance {
                    count += 1
                }
            }
            return count
        }

        /// The rows with anything on them, counted down from the top of the bitmap.
        var inkRows: ClosedRange<Int>? {
            var first: Int?
            var last: Int?
            for row in 0..<height {
                for column in 0..<width where bytes[(row * width + column) * 4 + 3] > 0 {
                    first = first ?? row
                    last = row
                    break
                }
            }
            guard let first, let last else { return nil }
            return first...last
        }
    }

    private func overlay(
        size: StudioCaptionSize = .medium,
        position: StudioCaptionPosition = .bottom,
        appearance: StudioCaptionAppearance = .classicOutline
    ) -> StudioCaptionOverlay {
        var overlay = StudioCaptionOverlay()
        overlay.enabled = true
        overlay.size = size
        overlay.position = position
        overlay.appearance = appearance
        return overlay
    }

    private func picture(
        _ text: String = "Captions look right",
        overlay: StudioCaptionOverlay? = nil,
        language: String? = nil,
        width: Int = 1920,
        height: Int = 1080
    ) throws -> StudioCaptionPicture {
        try XCTUnwrap(StudioCaptionPainter.picture(
            text: text,
            overlay: overlay ?? self.overlay(),
            language: language,
            canvasWidth: width,
            canvasHeight: height
        ))
    }

    func testClassicOutline_IsWhiteLettersOnABlackOutline() throws {
        let pixels = Pixels(try picture().image)
        XCTAssertGreaterThan(pixels.count(red: 255, green: 255, blue: 255), 500)
        XCTAssertGreaterThan(pixels.count(red: 0, green: 0, blue: 0), 500)
    }

    func testContrastPanel_IsHalfSeeThrough_WithNoOutline() throws {
        let pixels = Pixels(try picture(overlay: overlay(appearance: .contrastPanel)).image)
        XCTAssertGreaterThan(pixels.count(red: 255, green: 255, blue: 255), 500)

        // Premultiplied: black at 60 percent is black with an alpha of 153.
        XCTAssertGreaterThan(pixels.count(red: 0, green: 0, blue: 0, alpha: 153), 500)
        XCTAssertEqual(pixels.count(red: 0, green: 0, blue: 0), 0)
    }

    func testBoldImpact_IsGoldLettersOnABlackOutline() throws {
        let pixels = Pixels(try picture(overlay: overlay(appearance: .boldImpact)).image)
        XCTAssertGreaterThan(pixels.count(red: 0xFF, green: 0xD7, blue: 0x00), 500)
        XCTAssertGreaterThan(pixels.count(red: 0, green: 0, blue: 0), 500)
    }

    func testCleanShadow_IsOffWhite_AndLaysEachSheetOnOnce() throws {
        let pixels = Pixels(try picture(overlay: overlay(appearance: .cleanShadow)).image)
        XCTAssertGreaterThan(pixels.count(red: 0xF5, green: 0xF5, blue: 0xF5), 500)

        // Black at half, the shadow alone. Both sheets over each other come to three quarters.
        // Black more solid than that means a sheet lay on itself, where two letters overlap. A
        // letter's edge is solid too, but it is not black.
        XCTAssertGreaterThan(pixels.count(red: 0, green: 0, blue: 0, alpha: 128), 100)
        var tooDark = 0
        for at in stride(from: 0, to: pixels.bytes.count, by: 4) where pixels.bytes[at] <= 1 {
            tooDark += pixels.bytes[at + 3] > 195 ? 1 : 0
        }
        XCTAssertEqual(tooDark, 0)
    }

    func testAtTheBottom_TheInkEndsNearTheMargin() throws {
        let picture = try picture()
        let rows = try XCTUnwrap(Pixels(picture.image).inkRows)
        let inkBottomFromCanvasBottom = picture.originY + Double(picture.image.height - 1 - rows.upperBound)

        // 12 units at 1080 are 45 pixels. The outline may cross the margin, and the descenders stay above it.
        XCTAssertGreaterThanOrEqual(inkBottomFromCanvasBottom, 45 - 4.5 - 2)
        XCTAssertLessThanOrEqual(inkBottomFromCanvasBottom, 45 + 40)
    }

    func testAtTheTop_TheInkStartsNearTheMargin() throws {
        let picture = try picture(overlay: overlay(position: .top))
        let rows = try XCTUnwrap(Pixels(picture.image).inkRows)
        let bitmapTop = 1080 - picture.originY - Double(picture.image.height)
        let inkTop = bitmapTop + Double(rows.lowerBound)
        XCTAssertGreaterThanOrEqual(inkTop, 45 - 4.5 - 2)
        XCTAssertLessThanOrEqual(inkTop, 45 + 40)
    }

    func testTheBitmapStaysOnTheCanvas_EvenWithMoreTextThanFits() throws {
        let text = Array(repeating: "A line of a caption that goes on", count: 200).joined(separator: "\n")
        let picture = try picture(text)
        XCTAssertEqual(picture.image.width, 1920)
        XCTAssertLessThanOrEqual(picture.originY + Double(picture.image.height), 1080)
        XCTAssertGreaterThanOrEqual(picture.originY, 0)
    }

    func testALongLineWraps_InsteadOfRunningOffTheCanvas() throws {
        let line = "Captions stay above everything else in the picture"
        let one = try XCTUnwrap(Pixels(try picture(line).image).inkRows)
        let many = try XCTUnwrap(Pixels(try picture(Array(repeating: line, count: 4).joined(separator: " ")).image).inkRows)
        XCTAssertGreaterThan(many.count, one.count * 2)
    }

    func testTextTheSystemFontHasNoLettersFor_IsStillDrawn() throws {
        for (language, text) in [("ja", "これは字幕です"), ("ar", "مرحبا بالعالم"), ("hi", "नमस्ते दुनिया")] {
            let pixels = Pixels(try picture(text, language: language).image)
            XCTAssertGreaterThan(pixels.count(red: 255, green: 255, blue: 255), 100, language)
        }
    }

    func testSizes_FollowTheShortSide() {
        let landscape = StudioCaptionStyle(overlay: overlay(size: .large), canvasWidth: 1920, canvasHeight: 1080)
        let portrait = StudioCaptionStyle(overlay: overlay(size: .large), canvasWidth: 1080, canvasHeight: 1920)
        XCTAssertEqual(landscape.textHeight, 90, accuracy: 1e-9)
        XCTAssertEqual(portrait.textHeight, 90, accuracy: 1e-9)
        XCTAssertEqual(landscape.longestLine, 1728, accuracy: 1e-9)
        XCTAssertEqual(portrait.longestLine, 972, accuracy: 1e-9)
    }

    func testTheEditor_SavesEachChoice_AndUndoTakesItBack() throws {
        var project = StudioProject(id: "3f0013cf-ba10-4453-af91-792b7882dae6", sources: StudioSources(screen: StudioScreenSource(width: 1920, height: 1080, frameRate: 30, duration: 10)))
        project.captions.cues = [StudioCaptionCue(start: 1, end: 2, text: "Hi")]
        var editor = StudioEditorModel(project: project)
        XCTAssertFalse(editor.project.overlays.captions.enabled)

        editor.setCaptionsDrawn(true)
        editor.setCaptionSize(.large)
        editor.setCaptionPosition(.top)
        editor.setCaptionAppearance(.boldImpact)

        XCTAssertTrue(editor.project.overlays.captions.enabled)
        XCTAssertEqual(editor.project.overlays.captions.size, .large)
        XCTAssertEqual(editor.project.overlays.captions.position, .top)
        XCTAssertEqual(editor.project.overlays.captions.appearance, .boldImpact)

        editor.undo()
        XCTAssertEqual(editor.project.overlays.captions.appearance, .classicOutline)
        XCTAssertEqual(editor.project.overlays.captions.position, .top)
    }

    func testARenderSnapshot_HasCaptionsOnlyWhenTheyAreDrawn() {
        var project = StudioProject(id: "3f0013cf-ba10-4453-af91-792b7882dae6", sources: StudioSources(screen: StudioScreenSource(width: 1920, height: 1080, frameRate: 30, duration: 10)))
        project.captions.cues = [StudioCaptionCue(start: 1, end: 2, text: "Hi")]
        XCTAssertTrue(StudioRenderSnapshot(project: project, events: StudioEvents()).captions.isEmpty)

        project.overlays.captions.enabled = true
        XCTAssertEqual(StudioRenderSnapshot(project: project, events: StudioEvents()).captions.map(\.text), ["Hi"])
    }
}
