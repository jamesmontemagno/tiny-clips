import XCTest
@testable import TinyClips

final class SaveServiceTests: XCTestCase {
    private var directoryURL: URL!

    override func setUpWithError() throws {
        directoryURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("TinyClipsTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(
            at: directoryURL,
            withIntermediateDirectories: true
        )
    }

    override func tearDownWithError() throws {
        if let directoryURL {
            try? FileManager.default.removeItem(at: directoryURL)
        }
        directoryURL = nil
    }

    func testFilenameTokensSanitizationAndExtensionNormalization() throws {
        let calendar = Calendar(identifier: .gregorian)
        let date = try XCTUnwrap(
            calendar.date(from: DateComponents(
                year: 2026,
                month: 8,
                day: 12,
                hour: 9,
                minute: 7,
                second: 5
            ))
        )

        let filename = SaveService.generatedFileName(
            for: .video,
            fileExtension: " .MP4 ",
            template: "{app}: {type} / {datetime}",
            date: date
        )

        XCTAssertEqual(filename, "TinyClips- Video - 2026-08-12_09.07.05.mp4")
    }

    func testEmptyTemplateAndStemFallBackToStableNames() {
        let date = Date(timeIntervalSince1970: 0)
        let defaultName = SaveService.generatedFileName(
            for: .screenshot,
            fileExtension: "png",
            template: " ",
            date: date
        )
        let sanitizedFallback = SaveService.generatedFileName(
            for: .screenshot,
            fileExtension: "",
            template: ".",
            date: date
        )

        XCTAssertTrue(defaultName.hasPrefix("TinyClips "))
        XCTAssertTrue(defaultName.hasSuffix(".png"))
        XCTAssertTrue(sanitizedFallback.hasPrefix("TinyClips "))
    }

    func testStemSuffixPreservesExtension() {
        XCTAssertEqual(
            SaveService.appendingStemSuffix("edited", to: "capture.png"),
            "capture edited.png"
        )
        XCTAssertEqual(
            SaveService.appendingStemSuffix("edited", to: "capture"),
            "capture edited"
        )
        XCTAssertEqual(
            SaveService.appendingStemSuffix(" ", to: "capture.png"),
            "capture.png"
        )
    }

    func testCollisionNumberingUsesNextAvailableSuffix() {
        let original = directoryURL.appendingPathComponent("capture.png")
        let second = directoryURL.appendingPathComponent("capture 2.png")
        XCTAssertTrue(FileManager.default.createFile(atPath: original.path, contents: Data()))
        XCTAssertTrue(FileManager.default.createFile(atPath: second.path, contents: Data()))

        let unique = SaveService.uniqueURL(in: directoryURL, filename: "capture.png")

        XCTAssertEqual(unique.lastPathComponent, "capture 3.png")
    }
}
