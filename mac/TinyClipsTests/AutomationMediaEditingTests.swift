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

    @MainActor
    func testRouterSupportsMediaEditingCommands() {
        XCTAssertTrue(AutomationCommandRouter.supportedCommands.contains("editor.trim"))
        XCTAssertTrue(AutomationCommandRouter.supportedCommands.contains("editor.saveFrame"))
    }
}
