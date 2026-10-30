import Foundation
import XCTest
#if canImport(TinyClips)
import AppKit
import Combine
@testable import TinyClips
#endif

final class SensitiveTextDetectionTests: XCTestCase {
    func testEmailRangesUseUTF16AndExcludeSurroundingText() {
        let text = "🔒 Contact first.last+tag@example.co.uk, please."
        let matches = SensitiveTextDetection.matches(in: text)
        XCTAssertEqual(matches.count, 1)
        XCTAssertEqual(matches.first?.kind, .email)
        XCTAssertEqual(matches.first.map { (text as NSString).substring(with: $0.range) },
                       "first.last+tag@example.co.uk")
        for address in ["o'brien@example.com", "first!#$%&'*+/=?^_`{|}~-last@example.com"] {
            let results = SensitiveTextDetection.matches(in: "Contact \(address), please.")
            XCTAssertEqual(results.count, 1)
            XCTAssertEqual(results.first?.kind, .email)
            XCTAssertEqual(results.first.map { ("Contact \(address), please." as NSString).substring(with: $0.range) }, address)
        }
    }

    func testFormattedPhoneNumbers() {
        for value in ["555-0123", "555 0123", "+1 (415) 555-0123", "(415) 555-0123",
                      "415.555.0123", "+44 20 7946 0958", "+123456789012345"] {
            XCTAssertEqual(SensitiveTextDetection.matches(in: value).map(\.kind), [.phone], value)
        }
    }

    func testIPv4AndIPv6IncludingCompressedAndMappedAddresses() {
        for value in ["0.0.0.0", "192.168.1.1", "255.255.255.255", "2001:db8:0:1:2:3:4:5",
                      "2001:db8::1", "::1", "::", "::ffff:192.0.2.128"] {
            XCTAssertEqual(SensitiveTextDetection.matches(in: "[\(value)]").map(\.kind), [.ipAddress], value)
        }
        XCTAssertEqual(SensitiveTextDetection.matches(in: "Server 192.168.1.1.").map(\.kind), [.ipAddress])
    }

    func testFalsePositivesAndInvalidAddressesAreRejected() {
        for value in ["hello@example", "12345", "5550123", "4155550123", "1234567890123456",
                      "2026-10-30", "30-10-2026", "01-02-2026", "1-2-2026",
                      "2026-10-30 1234", "1.2.3", "999.168.1.1", "192.168.1.1.2",
                      "01.2.3.4", "192.168.001.1", "::ffff:192.168.001.1",
                      "555.0123", "12.345.6789", "1.2.3456789", "foo..bar@example.com",
                      "1.2.3.456", "2001:::1", "1:2:3:4:5:6:7", "1:2:3:4:5:6:7:8:9",
                      "::ffff:999.2.3.4", "1:2:3:4:5:6:7::8", "1:2:3:4:5:6:7:8::",
                      "gggg::1", "12:30:45", "version 123.456.789.012", "abc192.168.1.1xyz"] {
            XCTAssertTrue(SensitiveTextDetection.matches(in: value).isEmpty, value)
        }
    }

    func testMultipleKindsDoNotOverlapAndPreserveReadingOrder() {
        let text = "user@example.com\n+1 (415) 555-0123\n192.168.1.1\n2001:db8::1"
        let matches = SensitiveTextDetection.matches(in: text)
        XCTAssertEqual(matches.map(\.kind), [.email, .phone, .ipAddress, .ipAddress])
        for pair in zip(matches, matches.dropFirst()) {
            XCTAssertLessThanOrEqual(NSMaxRange(pair.0.range), pair.1.range.location)
        }
    }

    func testFallbackWordRangesCoverOnlyWordsOverlappingTheMatch() throws {
        let text = "🔒 Phone: +1 (415) 555-0123 today"
        let match = try XCTUnwrap(SensitiveTextDetection.matches(in: text).first)
        let words = SensitiveTextDetection.wordRanges(overlapping: match.range, in: text)
        XCTAssertEqual(words.map { (text as NSString).substring(with: $0) }, ["+1", "(415)", "555-0123"])
        XCTAssertTrue(SensitiveTextDetection.wordRanges(overlapping: NSRange(location: 0, length: 0), in: text).isEmpty)
    }

    func testVisionCoordinatesFlipYAndIncludePixelMarginAtRetinaResolution() throws {
        let rect = try XCTUnwrap(SensitiveTextDetection.imageRect(
            fromVision: CGRect(x: 0.125, y: 0.625, width: 0.25, height: 0.125),
            pixelSize: CGSize(width: 2048, height: 1024)
        ))
        XCTAssertEqual(rect.minX, CGFloat(254) / 2048, accuracy: 0.000001)
        XCTAssertEqual(rect.minY, CGFloat(254) / 1024, accuracy: 0.000001)
        XCTAssertEqual(rect.maxX, CGFloat(770) / 2048, accuracy: 0.000001)
        XCTAssertEqual(rect.maxY, CGFloat(386) / 1024, accuracy: 0.000001)
        // The same normalized region scales with the image, not with the backing screen.
        XCTAssertEqual(rect.minX * 512, rect.minX * 2048 / 4, accuracy: 0.0001)
    }

    func testVisionBoundsClampAndRejectEmptyOrNonfiniteInputs() throws {
        XCTAssertEqual(SensitiveTextDetection.imageRect(
            fromVision: CGRect(x: 0, y: 0, width: 1, height: 1),
            pixelSize: CGSize(width: 100, height: 50)
        ), CGRect(x: 0, y: 0, width: 1, height: 1))
        XCTAssertNil(SensitiveTextDetection.imageRect(fromVision: .zero, pixelSize: CGSize(width: 100, height: 50)))
        XCTAssertNil(SensitiveTextDetection.imageRect(
            fromVision: CGRect(x: .infinity, y: 0, width: 0.2, height: 0.1),
            pixelSize: CGSize(width: 100, height: 50)
        ))
        XCTAssertNil(SensitiveTextDetection.imageRect(
            fromVision: CGRect(x: 0, y: 0, width: 1, height: 1), pixelSize: .zero
        ))
    }

    #if canImport(TinyClips)
    @MainActor
    private func makeEditor() throws -> ScreenshotEditorViewModel {
        let macDirectory = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
        let url = macDirectory.appendingPathComponent("TinyClips/Assets.xcassets/AppIcon.appiconset/icon_128x128@2x.png")
        let editor = ScreenshotEditorViewModel(url: url)
        XCTAssertNotNil(editor.originalImage)
        return editor
    }

    @MainActor
    func testApprovalAppliesOpaquePixelsThroughRenderAndUndoRedo() async throws {
        let editor = try makeEditor()
        let suggestion = SensitiveTextSuggestion(kind: .email, text: "user@example.com", rect: CGRect(x: 0.1, y: 0.1, width: 0.5, height: 0.25))
        let dismissed = SensitiveTextSuggestion(kind: .phone, text: "555-0123", rect: CGRect(x: 0.1, y: 0.5, width: 0.5, height: 0.1))
        let unapproved = SensitiveTextSuggestion(kind: .ipAddress, text: "192.168.1.1", rect: CGRect(x: 0.1, y: 0.7, width: 0.5, height: 0.1))
        let finished = expectation(description: "Detection finished")
        let observer = editor.$isFindingSensitiveText
            .drop(while: { !$0 }).filter { !$0 }.prefix(1)
            .sink { _ in finished.fulfill() }
        editor.findSensitiveText(recognize: { _ in [suggestion, dismissed, unapproved] })
        await fulfillment(of: [finished], timeout: 2)
        withExtendedLifetime(observer) {}
        XCTAssertTrue(editor.annotations.isEmpty)
        XCTAssertFalse(editor.canUndo)
        XCTAssertEqual(editor.sensitiveTextSuggestions.count, 3)
        XCTAssertEqual(editor.sensitiveTextSuggestions.first?.text, "user@example.com")
        editor.approveSensitiveText(suggestion.id)
        editor.dismissSensitiveText(dismissed.id)
        XCTAssertEqual(editor.sensitiveTextSuggestions.count, 2)
        XCTAssertEqual(editor.approvedSensitiveTextCount, 1)
        XCTAssertTrue(editor.annotations.isEmpty)
        editor.applyApprovedSensitiveText()
        XCTAssertEqual(editor.annotations.count, 1)
        XCTAssertTrue(editor.annotations[0].isOpaqueRedaction)
        XCTAssertTrue(editor.sensitiveTextSuggestions.isEmpty)
        let bitmap = try XCTUnwrap(editor.renderFinalImage())
        let color = try XCTUnwrap(bitmap.colorAt(x: Int(CGFloat(bitmap.pixelsWide) * 0.3),
                                               y: Int(CGFloat(bitmap.pixelsHigh) * 0.2))?.usingColorSpace(.deviceRGB))
        XCTAssertEqual(color.redComponent, 0, accuracy: 0.001)
        XCTAssertEqual(color.greenComponent, 0, accuracy: 0.001)
        XCTAssertEqual(color.blueComponent, 0, accuracy: 0.001)
        XCTAssertEqual(color.alphaComponent, 1, accuracy: 0.001)
        let image = NSImage(size: bitmap.size)
        image.addRepresentation(bitmap)
        let clipboard = try XCTUnwrap(SaveService.clipboardRepresentations(for: image))
        let decodedPNG = try XCTUnwrap(NSBitmapImageRep(data: clipboard.pngData))
        let copiedColor = try XCTUnwrap(decodedPNG.colorAt(x: Int(CGFloat(decodedPNG.pixelsWide) * 0.3),
                                                         y: Int(CGFloat(decodedPNG.pixelsHigh) * 0.2))?.usingColorSpace(.deviceRGB))
        XCTAssertEqual(copiedColor.redComponent, 0, accuracy: 0.001)
        XCTAssertEqual(copiedColor.greenComponent, 0, accuracy: 0.001)
        XCTAssertEqual(copiedColor.blueComponent, 0, accuracy: 0.001)
        XCTAssertEqual(copiedColor.alphaComponent, 1, accuracy: 0.001)
        editor.undo()
        XCTAssertTrue(editor.annotations.isEmpty)
        editor.redo()
        XCTAssertTrue(editor.annotations[0].isOpaqueRedaction)
    }

    @MainActor
    func testHitTestingPicksOpaqueRedactionDrawnAboveNewerAnnotations() throws {
        let editor = try makeEditor()
        var redaction = ScreenshotAnnotation(tool: .blur, rect: CGRect(x: 0.2, y: 0.2, width: 0.4, height: 0.4),
                                             color: .black, lineWidth: 0, text: "", points: [])
        redaction.isOpaqueRedaction = true
        let rectangle = ScreenshotAnnotation(tool: .rectangle, rect: CGRect(x: 0.1, y: 0.1, width: 0.6, height: 0.6),
                                             color: .red, lineWidth: 4, text: "", points: [])
        editor.annotations = [redaction, rectangle]
        XCTAssertEqual(editor.annotations.drawOrderIndices, [1, 0])
        XCTAssertEqual(editor.annotationIndex(at: CGPoint(x: 0.4, y: 0.4)), 0)
        XCTAssertEqual(editor.annotationIndex(at: CGPoint(x: 0.12, y: 0.12)), 1)
        editor.selectedTool = .move
        editor.selectedAnnotationIndex = editor.annotationIndex(at: CGPoint(x: 0.4, y: 0.4))
        XCTAssertTrue(editor.deleteSelectedAnnotation())
        XCTAssertEqual(editor.annotations.count, 1)
        XCTAssertFalse(editor.annotations[0].isOpaqueRedaction)
    }

    @MainActor
    func testEmptyAndFailedDetectionDoNotChangeTheImage() async throws {
        let editor = try makeEditor()
        for fails in [false, true] {
            let finished = expectation(description: "Detection finished")
            let observer = editor.$isFindingSensitiveText
                .drop(while: { !$0 }).filter { !$0 }.prefix(1)
                .sink { _ in finished.fulfill() }
            editor.findSensitiveText(recognize: { _ in
                if fails { throw NSError(domain: "SensitiveTextTests", code: 1) }
                return []
            })
            await fulfillment(of: [finished], timeout: 2)
            withExtendedLifetime(observer) {}
            XCTAssertTrue(editor.annotations.isEmpty)
            XCTAssertTrue(editor.sensitiveTextSuggestions.isEmpty)
            XCTAssertFalse(editor.canUndo)
            XCTAssertTrue(editor.sensitiveTextStatus?.contains(fails ? "Could not" : "No likely") == true)
        }
    }

    @MainActor
    func testMutationDiscardsInFlightRecognition() async throws {
        let editor = try makeEditor()
        let started = expectation(description: "Recognition started")
        let returned = expectation(description: "Recognition returned")
        var resume: CheckedContinuation<[SensitiveTextSuggestion], Never>?
        editor.findSensitiveText(recognize: { _ in
            let result = await withCheckedContinuation { continuation in
                resume = continuation
                started.fulfill()
            }
            returned.fulfill()
            return result
        })
        await fulfillment(of: [started], timeout: 2)
        editor.annotations.append(ScreenshotAnnotation(tool: .rectangle, rect: .zero, color: .red,
                                                      lineWidth: 4, text: "", points: []))
        resume?.resume(returning: [SensitiveTextSuggestion(kind: .phone, text: "555-0123", rect: CGRect(x: 0, y: 0, width: 1, height: 1))])
        await fulfillment(of: [returned], timeout: 2)
        await Task.yield()
        XCTAssertTrue(editor.sensitiveTextSuggestions.isEmpty)
        XCTAssertFalse(editor.isFindingSensitiveText)
        XCTAssertEqual(editor.annotations.count, 1)
    }
    #endif
}
