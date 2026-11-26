import XCTest
import AppKit
import ImageIO
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

    func testWebPEncoderProducesDecodableScreenshot() throws {
        let context = try XCTUnwrap(CGContext(
            data: nil, width: 2, height: 2, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.setFillColor(NSColor.red.cgColor)
        context.fill(CGRect(x: 0, y: 0, width: 2, height: 2))
        let image = try XCTUnwrap(context.makeImage())
        let data = try XCTUnwrap(WebPImageEncoder.encode(image, quality: 0.85))
        XCTAssertEqual(String(data: data.prefix(4), encoding: .ascii), "RIFF")
        XCTAssertEqual(String(data: data.dropFirst(8).prefix(4), encoding: .ascii), "WEBP")
        let source = try XCTUnwrap(CGImageSourceCreateWithData(data as CFData, nil))
        let decoded = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil))
        XCTAssertEqual(decoded.width, 2)
        XCTAssertEqual(decoded.height, 2)
    }

    func testWebPEncoderPreservesTwoXPixelDensity() throws {
        let context = try XCTUnwrap(CGContext(
            data: nil, width: 200, height: 100, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        let image = try XCTUnwrap(context.makeImage())
        let data = try XCTUnwrap(
            WebPImageEncoder.encode(image, quality: 0.85, scaleFactor: 2)
        )
        let source = try XCTUnwrap(CGImageSourceCreateWithData(data as CFData, nil))
        let properties = try XCTUnwrap(
            CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any]
        )
        let dpiWidth = try XCTUnwrap(properties[kCGImagePropertyDPIWidth] as? NSNumber)
        let dpiHeight = try XCTUnwrap(properties[kCGImagePropertyDPIHeight] as? NSNumber)
        XCTAssertEqual(dpiWidth.doubleValue, 144, accuracy: 0.01)
        XCTAssertEqual(dpiHeight.doubleValue, 144, accuracy: 0.01)

        let url = directoryURL.appendingPathComponent("retina.webp")
        try data.write(to: url)
        let loaded = try XCTUnwrap(ImagePixelDensity.loadImage(contentsOf: url))
        XCTAssertEqual(loaded.size.width, 100, accuracy: 0.01)
        XCTAssertEqual(loaded.size.height, 50, accuracy: 0.01)
        XCTAssertEqual(ImagePixelDensity.scaleFactor(for: loaded), 2, accuracy: 0.01)
    }

    @MainActor
    func testClipboardRepresentationsPreserveTwoXLogicalSize() throws {
        let bitmap = try XCTUnwrap(NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: 200,
            pixelsHigh: 100,
            bitsPerSample: 8,
            samplesPerPixel: 4,
            hasAlpha: true,
            isPlanar: false,
            colorSpaceName: .deviceRGB,
            bytesPerRow: 0,
            bitsPerPixel: 0
        ))
        bitmap.size = NSSize(width: 100, height: 50)
        let image = NSImage(size: bitmap.size)
        image.addRepresentation(bitmap)

        let representations = try XCTUnwrap(SaveService.clipboardRepresentations(for: image))
        let tiff = try XCTUnwrap(NSBitmapImageRep(data: representations.tiffData))
        XCTAssertEqual(tiff.pixelsWide, 200)
        XCTAssertEqual(tiff.pixelsHigh, 100)
        XCTAssertEqual(tiff.size.width, 100, accuracy: 0.01)
        XCTAssertEqual(tiff.size.height, 50, accuracy: 0.01)

        let source = try XCTUnwrap(CGImageSourceCreateWithData(representations.pngData as CFData, nil))
        let properties = try XCTUnwrap(
            CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any]
        )
        let dpiWidth = try XCTUnwrap(properties[kCGImagePropertyDPIWidth] as? NSNumber)
        let dpiHeight = try XCTUnwrap(properties[kCGImagePropertyDPIHeight] as? NSNumber)
        XCTAssertEqual(dpiWidth.doubleValue, 144, accuracy: 0.01)
        XCTAssertEqual(dpiHeight.doubleValue, 144, accuracy: 0.01)
    }

    @MainActor
    func testRecentCaptureRemovalByURLRemovesMatchingCapture() {
        let captureURL = directoryURL.appendingPathComponent("capture.png")
        XCTAssertTrue(FileManager.default.createFile(atPath: captureURL.path, contents: Data()))

        addTeardownBlock {
            RecentCaptureStore.shared.remove(url: captureURL)
        }
        RecentCaptureStore.shared.record(url: captureURL, type: .screenshot)
        RecentCaptureStore.shared.remove(url: captureURL)

        XCTAssertFalse(RecentCaptureStore.shared.items.contains { $0.url == captureURL })
    }

    func testRecentCapturesLimitPersistsAndClampsToSupportedRange() throws {
        let suiteName = "SaveServiceTests.RecentCapturesLimit.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer {
            defaults.removePersistentDomain(forName: suiteName)
        }
        let settings = CaptureSettings(defaults: defaults, performMigrations: false)

        XCTAssertEqual(settings.recentCapturesLimit, 10)
        settings.recentCapturesLimit = 30
        XCTAssertEqual(settings.recentCapturesLimit, 30)
        XCTAssertEqual(defaults.integer(forKey: "recentCapturesLimit"), 30)
        settings.recentCapturesLimit = 2
        XCTAssertEqual(settings.recentCapturesLimit, 5)
        settings.recentCapturesLimit = 100
        XCTAssertEqual(settings.recentCapturesLimit, 30)
        CaptureSettings.resetStoredDefaults(defaults)
        XCTAssertEqual(settings.recentCapturesLimit, 10)
    }

    func testScreenshotRoutingCoversEveryOutputCombination() throws {
        for mask in 0..<8 {
            let policy = ScreenshotOutputPolicy(
                saveToDisk: mask & 1 != 0,
                copyToClipboard: mask & 2 != 0,
                openEditor: mask & 4 != 0
            )
            var events: [String] = []
            func deliver() throws -> Bool {
                try ScreenshotOutputRouter.deliver(
                    policy: policy,
                    recordSaved: { events.append("saved") },
                    copy: { events.append("copy"); return true },
                    recoverCopy: { events.append("recover"); return .discarded },
                    openEditor: { events.append("editor"); return true },
                    releaseTemporary: { events.append("release") }
                )
            }

            if mask == 0 {
                XCTAssertNil(policy.destination)
                XCTAssertThrowsError(try deliver())
                XCTAssertTrue(events.isEmpty)
                continue
            }

            XCTAssertEqual(policy.destination, policy.saveToDisk ? .saved : .temporary)
            XCTAssertEqual(try deliver(), policy.openEditor)
            var expected: [String] = []
            if policy.saveToDisk { expected.append("saved") }
            if policy.copyToClipboard { expected.append("copy") }
            expected.append(policy.openEditor ? "editor" : "release")
            XCTAssertEqual(events, expected, "output mask \(mask)")
        }
    }

    func testScreenshotRoutingContinuesToEditorWithRetainedOrSavedSource() throws {
        let policy = ScreenshotOutputPolicy(saveToDisk: false, copyToClipboard: true, openEditor: true)
        for result: ScreenshotCopyRecoveryResult in [.available, .retained, .discarded] {
            var events: [String] = []
            var source: String? = "temporary"
            let presented = try ScreenshotOutputRouter.deliver(
                policy: policy,
                recordSaved: { events.append("saved") },
                copy: { events.append("copy"); return false },
                recoverCopy: {
                    events.append("recover")
                    switch result {
                    case .available: source = "saved"
                    case .retained: break
                    case .discarded: source = nil
                    }
                    return result
                },
                openEditor: {
                    events.append("editor:\(source ?? "missing")")
                    return true
                },
                releaseTemporary: { events.append("release") }
            )
            switch result {
            case .available:
                XCTAssertTrue(presented)
                XCTAssertEqual(events, ["copy", "recover", "editor:saved"])
            case .retained:
                XCTAssertTrue(presented)
                XCTAssertEqual(events, ["copy", "recover", "editor:temporary"])
            case .discarded:
                XCTAssertFalse(presented)
                XCTAssertEqual(events, ["copy", "recover"])
            }
        }

        var events: [String] = []
        let presented = try ScreenshotOutputRouter.deliver(
            policy: ScreenshotOutputPolicy(saveToDisk: false, copyToClipboard: false, openEditor: true),
            recordSaved: { events.append("saved") },
            copy: { events.append("copy"); return true },
            recoverCopy: { events.append("recover-copy"); return .discarded },
            openEditor: { events.append("recover-editor"); return false },
            releaseTemporary: { events.append("release") }
        )
        XCTAssertFalse(presented)
        XCTAssertEqual(events, ["recover-editor"])

        events = []
        let kept = try ScreenshotOutputRouter.deliver(
            policy: ScreenshotOutputPolicy(saveToDisk: false, copyToClipboard: true, openEditor: false),
            recordSaved: { events.append("saved") },
            copy: { events.append("copy"); return false },
            recoverCopy: { events.append("keep"); return .retained },
            openEditor: { events.append("editor"); return true },
            releaseTemporary: { events.append("release") }
        )
        XCTAssertFalse(kept)
        XCTAssertEqual(events, ["copy", "keep"])
    }

    func testScreenshotMemoryRecoveryRetriesAndKeepsIndependentOutputs() {
        var actions: [ScreenshotMemoryRecoveryAction] = [.retry, .copy, .copy, .retry]
        var retries = 0
        var copies = 0
        var prompts: [(String, Bool)] = []
        let outcome: ScreenshotMemoryRecoveryResult<String> = ScreenshotMemoryRecovery.recover(
            initialError: ScreenshotTemporaryFileError.unsafeDirectory,
            requiresSave: false,
            requiresEditor: true,
            choose: { error, copied in
                prompts.append((error, copied))
                return actions.removeFirst()
            },
            retry: {
                retries += 1
                if retries == 1 { throw ScreenshotTemporaryFileError.unsafeDirectory }
                return "new-safe-source"
            },
            copy: {
                copies += 1
                return copies == 2
            },
            saveAs: { XCTFail("Save As was not selected"); return nil }
        )
        guard case let .useSource(source, copied, saved) = outcome else {
            return XCTFail("A successful retry should continue the editor output")
        }
        XCTAssertEqual(source, "new-safe-source")
        XCTAssertTrue(copied)
        XCTAssertFalse(saved)
        XCTAssertEqual(retries, 2)
        XCTAssertEqual(copies, 2)
        XCTAssertFalse(prompts[2].1)
        XCTAssertTrue(prompts[3].1)
    }

    func testScreenshotMemoryRecoverySaveCancelCopyOnlyAndDiscard() {
        var actions: [ScreenshotMemoryRecoveryAction] = [.saveAs, .saveAs]
        var saves = 0
        let saved: ScreenshotMemoryRecoveryResult<String> = ScreenshotMemoryRecovery.recover(
            initialError: ScreenshotTemporaryFileError.unsafeDirectory,
            requiresSave: true,
            requiresEditor: false,
            choose: { _, _ in actions.removeFirst() },
            retry: { XCTFail("Retry was not selected"); return "unexpected" },
            copy: { XCTFail("Copy was not selected"); return false },
            saveAs: {
                saves += 1
                return saves == 1 ? nil : "user-selected-save"
            }
        )
        guard case let .useSource(source, copied, wasSaved) = saved else {
            return XCTFail("A successful Save As should supply the permanent source")
        }
        XCTAssertEqual(source, "user-selected-save")
        XCTAssertFalse(copied)
        XCTAssertTrue(wasSaved)
        XCTAssertEqual(saves, 2)

        let copiedOnly: ScreenshotMemoryRecoveryResult<String> = ScreenshotMemoryRecovery.recover(
            initialError: ScreenshotTemporaryFileError.unsafeDirectory,
            requiresSave: false,
            requiresEditor: false,
            choose: { _, _ in .copy },
            retry: { XCTFail("Retry was not selected"); return "unexpected" },
            copy: { true },
            saveAs: { XCTFail("Save As was not selected"); return nil }
        )
        guard case .finished = copiedOnly else { return XCTFail("A copy-only recovery should finish") }

        let discarded: ScreenshotMemoryRecoveryResult<String> = ScreenshotMemoryRecovery.recover(
            initialError: ScreenshotTemporaryFileError.unsafeDirectory,
            requiresSave: false,
            requiresEditor: true,
            choose: { _, _ in .discard },
            retry: { XCTFail("A discarded image must not be written"); return "unexpected" },
            copy: { XCTFail("A discarded image must not be copied"); return false },
            saveAs: { XCTFail("A discarded image must not be saved"); return nil }
        )
        guard case .finished = discarded else { return XCTFail("Explicit discard should finish recovery") }
    }

    func testMissingScreenshotSourceRecoversFrameForCopyAndEditorFailures() throws {
        let frame = Data([1, 2, 3])
        for missingDuringCopy in [true, false] {
            let original = directoryURL.appendingPathComponent("\(UUID().uuidString).png")
            let replacement = directoryURL.appendingPathComponent("\(UUID().uuidString).png")
            try frame.write(to: original)
            var source = original
            var retries = 0
            var opened = false
            func recover() -> Bool {
                let result = ScreenshotUnavailableSourceRecovery.ifMissing(
                    sourceExists: FileManager.default.fileExists(atPath: source.path),
                    saveToDisk: false,
                    recover: {
                        ScreenshotMemoryRecovery.recover(
                            initialError: ScreenshotTemporaryFileError.missingImage,
                            requiresSave: false,
                            requiresEditor: true,
                            choose: { _, _ in .retry },
                            retry: {
                                retries += 1
                                try frame.write(to: replacement)
                                return replacement
                            },
                            copy: { XCTFail("Copy was not selected"); return false },
                            saveAs: { XCTFail("Save As was not selected"); return nil }
                        )
                    },
                    recordSavedRetry: { _ in XCTFail("An editor-only retry must not be recorded as saved") }
                )
                guard case let .useSource(url, copied, saved) = result else { return false }
                XCTAssertFalse(copied)
                XCTAssertFalse(saved)
                source = url
                return true
            }
            let policy = ScreenshotOutputPolicy(
                saveToDisk: false,
                copyToClipboard: missingDuringCopy,
                openEditor: true
            )
            let presented = try ScreenshotOutputRouter.deliver(
                policy: policy,
                recordSaved: { XCTFail("The capture must not be recorded as saved") },
                copy: {
                    do {
                        try FileManager.default.removeItem(at: source)
                    } catch {
                        XCTFail("Could not remove the synthetic source: \(error)")
                    }
                    return false
                },
                recoverCopy: { recover() ? .available : .discarded },
                openEditor: {
                    if !missingDuringCopy {
                        do {
                            try FileManager.default.removeItem(at: source)
                        } catch {
                            XCTFail("Could not remove the synthetic source: \(error)")
                            return false
                        }
                        guard recover() else { return false }
                    }
                    XCTAssertEqual(try? Data(contentsOf: source), frame)
                    opened = true
                    return true
                },
                releaseTemporary: { XCTFail("The editor owns the recovered source") }
            )
            XCTAssertTrue(presented)
            XCTAssertTrue(opened)
            XCTAssertEqual(retries, 1)
        }
    }

    func testMissingScreenshotRecoveryRegistersRetryButNotSaveAsTwice() throws {
        let source = directoryURL.appendingPathComponent("missing.png")
        let frame = Data([4, 5, 6])
        for useSaveAs in [false, true] {
            let replacement = directoryURL.appendingPathComponent("\(UUID().uuidString).png")
            var saved: [URL] = []
            let result = ScreenshotUnavailableSourceRecovery.ifMissing(
                sourceExists: FileManager.default.fileExists(atPath: source.path),
                saveToDisk: true,
                recover: {
                    ScreenshotMemoryRecovery.recover(
                        initialError: ScreenshotTemporaryFileError.missingImage,
                        requiresSave: true,
                        requiresEditor: true,
                        choose: { _, _ in useSaveAs ? .saveAs : .retry },
                        retry: {
                            try frame.write(to: replacement)
                            return replacement
                        },
                        copy: { XCTFail("Copy was not selected"); return false },
                        saveAs: {
                            do {
                                try frame.write(to: replacement)
                            } catch {
                                XCTFail("Could not save the synthetic frame: \(error)")
                                return nil
                            }
                            saved.append(replacement)
                            return replacement
                        }
                    )
                },
                recordSavedRetry: { saved.append($0) }
            )
            guard case let .useSource(url, _, _) = result else {
                return XCTFail("A missing source should recover from the captured frame")
            }
            XCTAssertEqual(url, replacement)
            XCTAssertEqual(try Data(contentsOf: url), frame)
            XCTAssertEqual(saved, [replacement])
        }
        XCTAssertNil(ScreenshotUnavailableSourceRecovery.ifMissing(
            sourceExists: true,
            saveToDisk: true,
            recover: { XCTFail("An available source must not trigger recovery"); return .finished },
            recordSavedRetry: { (_: URL) in XCTFail("Nothing should be recorded") }
        ))
    }

    func testScreenshotRequestSnapshotsPolicyAndRejectsNoOutputs() throws {
        let suiteName = "SaveServiceTests.ScreenshotRequest.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }
        let settings = CaptureSettings(defaults: defaults, performMigrations: false)
        settings.saveScreenshotToDisk = false
        settings.copyScreenshotToClipboard = true
        settings.showScreenshotEditor = false
        settings.imageFormat = .png
        settings.jpegQuality = 0.6
        settings.showBrandingOverlay = true

        let request = try ScreenshotCaptureRequest(
            settings: settings,
            temporaryRoot: directoryURL
        )
        settings.saveScreenshotToDisk = true
        settings.copyScreenshotToClipboard = false
        settings.showScreenshotEditor = true
        settings.imageFormat = .jpeg
        settings.jpegQuality = 0.9
        settings.showBrandingOverlay = false
        XCTAssertEqual(
            request.policy,
            ScreenshotOutputPolicy(saveToDisk: false, copyToClipboard: true, openEditor: false)
        )
        XCTAssertEqual(request.encoding.format, .png)
        XCTAssertEqual(request.encoding.quality, 0.6, accuracy: 0.001)
        XCTAssertTrue(request.encoding.brandingEnabled)
        XCTAssertNotNil(request.temporaryFile)
        request.temporaryFile?.discard()

        settings.saveScreenshotToDisk = false
        settings.copyScreenshotToClipboard = false
        settings.showScreenshotEditor = false
        XCTAssertThrowsError(try ScreenshotCaptureRequest(
            settings: settings,
            temporaryRoot: directoryURL
        ))
    }

    func testScreenshotEncodingSnapshotPreservesFormatAndPixelDensity() throws {
        let suiteName = "SaveServiceTests.ScreenshotEncoding.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }
        let settings = CaptureSettings(defaults: defaults, performMigrations: false)
        settings.imageFormat = .png
        settings.showBrandingOverlay = false
        let options = ScreenshotEncodingOptions(settings: settings)
        settings.imageFormat = .jpeg
        settings.showBrandingOverlay = true
        let context = try XCTUnwrap(CGContext(
            data: nil, width: 20, height: 10, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        let image = try XCTUnwrap(context.makeImage())
        let url = directoryURL.appendingPathComponent("capture.png")
        try Data([1]).write(to: url)
        let unrelated = directoryURL.appendingPathComponent("unrelated.png")
        try Data([7]).write(to: unrelated)
        _ = try ScreenshotCapture.saveImage(image, to: url, scaleFactor: 2, options: options)
        let source = try XCTUnwrap(CGImageSourceCreateWithURL(url as CFURL, nil))
        let type = try XCTUnwrap(CGImageSourceGetType(source))
        XCTAssertEqual(String(describing: type), "public.png")
        let decoded = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil))
        XCTAssertEqual(decoded.width, 20)
        XCTAssertEqual(decoded.height, 10)
        let properties = try XCTUnwrap(
            CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any]
        )
        let dpi = try XCTUnwrap(properties[kCGImagePropertyDPIWidth] as? NSNumber)
        XCTAssertEqual(dpi.doubleValue, 144, accuracy: 0.01)
        XCTAssertEqual(try Data(contentsOf: unrelated), Data([7]))
        XCTAssertEqual(
            Set(try FileManager.default.contentsOfDirectory(atPath: directoryURL.path)),
            ["capture.png", "unrelated.png"]
        )
    }

    func testScreenshotMemoryClipboardPreservesPixelDensity() throws {
        let suiteName = "SaveServiceTests.ScreenshotMemoryClipboard.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }
        let settings = CaptureSettings(defaults: defaults, performMigrations: false)
        settings.showBrandingOverlay = false
        let context = try XCTUnwrap(CGContext(
            data: nil, width: 200, height: 100, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        let image = try XCTUnwrap(context.makeImage())
        let clipboardImage = ScreenshotCapture.clipboardImage(
            image,
            scaleFactor: 2,
            options: ScreenshotEncodingOptions(settings: settings)
        )
        XCTAssertEqual(clipboardImage.size.width, 100, accuracy: 0.01)
        XCTAssertEqual(clipboardImage.size.height, 50, accuracy: 0.01)
        let representations = try XCTUnwrap(SaveService.clipboardRepresentations(for: clipboardImage))
        let tiff = try XCTUnwrap(NSBitmapImageRep(data: representations.tiffData))
        XCTAssertEqual(tiff.pixelsWide, 200)
        XCTAssertEqual(tiff.pixelsHigh, 100)
        XCTAssertEqual(tiff.size.width, 100, accuracy: 0.01)
        XCTAssertEqual(tiff.size.height, 50, accuracy: 0.01)
        let source = try XCTUnwrap(CGImageSourceCreateWithData(representations.pngData as CFData, nil))
        let properties = try XCTUnwrap(
            CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any]
        )
        let dpiWidth = try XCTUnwrap(properties[kCGImagePropertyDPIWidth] as? NSNumber)
        let dpiHeight = try XCTUnwrap(properties[kCGImagePropertyDPIHeight] as? NSNumber)
        XCTAssertEqual(dpiWidth.doubleValue, 144, accuracy: 0.01)
        XCTAssertEqual(dpiHeight.doubleValue, 144, accuracy: 0.01)
    }

    func testActiveLibraryDirectoriesIgnoreDormantHistoricalFolders() throws {
        let historical = directoryURL.appendingPathComponent("historical", isDirectory: true)
        let screenshots = directoryURL.appendingPathComponent("screenshots", isDirectory: true)
        let videos = directoryURL.appendingPathComponent("videos", isDirectory: true)
        try FileManager.default.createDirectory(at: historical, withIntermediateDirectories: true)
        let active = SaveService.captureLibraryDirectories { type in
            switch type {
            case .screenshot: return screenshots
            case .video, .gif: return videos
            }
        }
        XCTAssertEqual(active, [screenshots, videos])
        let lease = try TinyClipsTemporaryFiles.makeScreenshotLease(
            fileExtension: "png", root: historical
        )
        try Data([1]).write(to: lease.url)
        lease.discard()
        let stale = historical.appendingPathComponent("TinyClips-\(UUID().uuidString).png")
        try Data([2]).write(to: stale)
        XCTAssertEqual(try TinyClipsTemporaryFiles.purge(avoiding: active, root: historical).removedCount, 1)
        XCTAssertFalse(FileManager.default.fileExists(atPath: stale.path))
    }

    func testScreenshotTemporaryLeasesProtectActiveAndSessionRetainedImages() throws {
        let active = try TinyClipsTemporaryFiles.makeScreenshotLease(
            fileExtension: "png", root: directoryURL
        )
        let retained = try TinyClipsTemporaryFiles.makeScreenshotLease(
            fileExtension: "jpg", root: directoryURL
        )
        try Data([1]).write(to: active.url)
        try Data([2]).write(to: retained.url)
        try retained.keepForRecovery()
        try retained.keepForRecovery()

        let screenshotDirectory = active.url.deletingLastPathComponent()
        let stale = directoryURL.appendingPathComponent("TinyClips-\(UUID().uuidString).mov")
        let unrelated = screenshotDirectory.appendingPathComponent("TinyClips-\(UUID().uuidString).txt")
        let target = directoryURL.appendingPathComponent("unrelated.png")
        let symlink = screenshotDirectory.appendingPathComponent("TinyClips-\(UUID().uuidString).png")
        try Data([3]).write(to: stale)
        try Data([4]).write(to: unrelated)
        try Data([5]).write(to: target)
        try FileManager.default.createSymbolicLink(at: symlink, withDestinationURL: target)

        XCTAssertEqual(
            try TinyClipsTemporaryFiles.removeStaleFiles(
                olderThan: Date().addingTimeInterval(60), root: directoryURL
            ),
            1
        )
        XCTAssertTrue(FileManager.default.fileExists(atPath: active.url.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: retained.url.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: unrelated.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: symlink.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: target.path))

        try Data([6]).write(to: stale)
        let report = try TinyClipsTemporaryFiles.purge(root: directoryURL)
        XCTAssertEqual(report.removedCount, 1)
        XCTAssertEqual(report.protectedCount, 2)
        let summary = try TinyClipsTemporaryFiles.summary(root: directoryURL)
        XCTAssertEqual(summary.fileCount, 2)
        XCTAssertEqual(summary.protectedCount, 2)
        active.discard()
        XCTAssertFalse(FileManager.default.fileExists(atPath: active.url.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: retained.url.path))
        XCTAssertEqual(
            try TinyClipsTemporaryFiles.purge(root: directoryURL).protectedCount,
            1
        )
        try retained.discardExplicitly()
        XCTAssertFalse(FileManager.default.fileExists(atPath: retained.url.path))
    }

    func testScreenshotLeaseDeinitializationDeletesOnlyItsTemporaryFile() throws {
        var lease: ScreenshotTemporaryFileLease? = try TinyClipsTemporaryFiles.makeScreenshotLease(
            fileExtension: "png", root: directoryURL
        )
        let url = try XCTUnwrap(lease).url
        let saved = directoryURL.appendingPathComponent("saved.png")
        try Data([1]).write(to: url)
        try Data([9]).write(to: saved)
        lease = nil
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))
        XCTAssertEqual(try Data(contentsOf: saved), Data([9]))
    }

    func testStartupSweepRemovesOldUnleasedScreenshotsButKeepsRecentOnes() throws {
        let screenshotDirectory = directoryURL.appendingPathComponent("TinyClips-Screenshots", isDirectory: true)
        try FileManager.default.createDirectory(at: screenshotDirectory, withIntermediateDirectories: true)
        let cutoff = Date().addingTimeInterval(-24 * 60 * 60)
        let recent = screenshotDirectory.appendingPathComponent("TinyClips-\(UUID().uuidString).png")
        try Data([2]).write(to: recent)
        for ext in ["png", "jpg", "jpeg", "webp"] {
            // A file left by a previous process has no active or session-retained lease.
            let abandoned = screenshotDirectory.appendingPathComponent("TinyClips-\(UUID().uuidString).\(ext)")
            try Data([1]).write(to: abandoned)
            try FileManager.default.setAttributes(
                [.modificationDate: cutoff.addingTimeInterval(-60)], ofItemAtPath: abandoned.path
            )
            XCTAssertEqual(try TinyClipsTemporaryFiles.removeStaleFiles(
                olderThan: cutoff, avoiding: [directoryURL], root: directoryURL
            ), 1)
            XCTAssertFalse(FileManager.default.fileExists(atPath: abandoned.path))
            XCTAssertEqual(try Data(contentsOf: recent), Data([2]))
        }
    }

    func testTemporarySweepLeavesUnrelatedFilesAndPreservesLegacyCleanup() throws {
        let screenshotDirectory = directoryURL.appendingPathComponent("TinyClips-Screenshots", isDirectory: true)
        try FileManager.default.createDirectory(at: screenshotDirectory, withIntermediateDirectories: true)
        let extensions = ["png", "jpg", "jpeg", "webp"]
        let managedCandidates = extensions.map { ext in
            screenshotDirectory.appendingPathComponent("TinyClips-\(UUID().uuidString).\(ext)")
        }
        let legacyCandidates = extensions.map { ext in
            directoryURL.appendingPathComponent("TinyClips-\(UUID().uuidString).\(ext)")
        }
        let unrelated = screenshotDirectory.appendingPathComponent("personal.png")
        let video = directoryURL.appendingPathComponent("TinyClips-\(UUID().uuidString).mov")
        let gif = directoryURL.appendingPathComponent("TinyClips-\(UUID().uuidString).gif")
        for url in managedCandidates + legacyCandidates + [unrelated, video, gif] {
            try Data([7]).write(to: url)
        }
        XCTAssertEqual(
            try TinyClipsTemporaryFiles.removeStaleFiles(
                olderThan: Date().addingTimeInterval(60), root: directoryURL
            ),
            managedCandidates.count + legacyCandidates.count + 2
        )
        for url in legacyCandidates {
            XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: video.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: gif.path))
        try Data([8]).write(to: video)
        try Data([8]).write(to: legacyCandidates[0])
        let report = try TinyClipsTemporaryFiles.purge(root: directoryURL)
        XCTAssertEqual(report.removedCount, 2)
        XCTAssertEqual(report.protectedCount, 0)
        for url in managedCandidates {
            XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))
        }
        XCTAssertEqual(try Data(contentsOf: unrelated), Data([7]))
    }

    func testTemporaryScreenshotFormatsWriteDirectlyAndDiscard() throws {
        let suiteName = "SaveServiceTests.TemporaryFormats.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }
        let settings = CaptureSettings(defaults: defaults, performMigrations: false)
        settings.showBrandingOverlay = false
        let context = try XCTUnwrap(CGContext(
            data: nil, width: 20, height: 10, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        let image = try XCTUnwrap(context.makeImage())

        for format: ImageFormat in [.png, .jpeg, .webp] {
            settings.imageFormat = format
            let lease = try TinyClipsTemporaryFiles.makeScreenshotLease(
                fileExtension: format.rawValue, root: directoryURL
            )
            _ = try ScreenshotCapture.saveImage(
                image, to: lease.url, scaleFactor: 2,
                options: ScreenshotEncodingOptions(settings: settings)
            )
            let source = try XCTUnwrap(CGImageSourceCreateWithURL(lease.url as CFURL, nil))
            let decoded = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil))
            XCTAssertEqual(decoded.width, 20, "\(format)")
            XCTAssertEqual(decoded.height, 10, "\(format)")
            let properties = try XCTUnwrap(
                CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any]
            )
            let dpi = try XCTUnwrap(properties[kCGImagePropertyDPIWidth] as? NSNumber)
            XCTAssertEqual(dpi.doubleValue, 144, accuracy: 0.01, "\(format)")
            lease.discard()
            XCTAssertFalse(FileManager.default.fileExists(atPath: lease.url.path), "\(format)")
        }
    }

    func testScreenshotTemporaryLocationsRejectSymlinkDirectoryAndPermanentSaves() throws {
        let root = directoryURL.appendingPathComponent("temp", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let lease = try TinyClipsTemporaryFiles.makeScreenshotLease(
            fileExtension: "png", root: root
        )
        XCTAssertFalse(TinyClipsTemporaryFiles.canSaveScreenshotPermanently(at: lease.url, root: root))
        XCTAssertTrue(TinyClipsTemporaryFiles.canSaveScreenshotPermanently(
            at: directoryURL.appendingPathComponent("saved.png"), root: root
        ))
        let screenshotAlias = directoryURL.appendingPathComponent("screenshot-alias", isDirectory: true)
        try FileManager.default.createSymbolicLink(
            at: screenshotAlias,
            withDestinationURL: lease.url.deletingLastPathComponent()
        )
        XCTAssertFalse(TinyClipsTemporaryFiles.canSaveScreenshotPermanently(
            at: screenshotAlias.appendingPathComponent("saved.png"), root: root
        ))
        lease.discard()
        let libraryFile = root.appendingPathComponent("TinyClips-\(UUID().uuidString).mov")
        try Data([1]).write(to: libraryFile)
        XCTAssertEqual(
            try TinyClipsTemporaryFiles.purge(avoiding: [root], root: root).protectedCount,
            1
        )
        XCTAssertTrue(FileManager.default.fileExists(atPath: libraryFile.path))

        let otherRoot = directoryURL.appendingPathComponent("other-temp", isDirectory: true)
        try FileManager.default.createDirectory(at: otherRoot, withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(
            at: otherRoot.appendingPathComponent("TinyClips-Screenshots"),
            withDestinationURL: root
        )
        XCTAssertThrowsError(try TinyClipsTemporaryFiles.makeScreenshotLease(
            fileExtension: "png", root: otherRoot
        ))
    }

    func testTemporaryScreenshotEditorProtectionEndsWhenReleased() throws {
        let source = directoryURL.appendingPathComponent("TinyClips-\(UUID().uuidString).png")
        try Data([1]).write(to: source)
        let protection = TinyClipsTemporaryFiles.protectEditorFile(source)
        XCTAssertEqual(try TinyClipsTemporaryFiles.purge(root: directoryURL).protectedCount, 1)
        XCTAssertTrue(FileManager.default.fileExists(atPath: source.path))
        protection.release()
        XCTAssertEqual(try TinyClipsTemporaryFiles.purge(root: directoryURL).removedCount, 1)
        XCTAssertFalse(FileManager.default.fileExists(atPath: source.path))
    }

    func testScreenshotEditorFirstSaveUsesPermanentDestination() {
        let temporaryRoot = directoryURL.appendingPathComponent("temp", isDirectory: true)
        let source = temporaryRoot
            .appendingPathComponent("TinyClips-Screenshots", isDirectory: true)
            .appendingPathComponent("TinyClips-\(UUID().uuidString).png")
        let saveDirectory = directoryURL.appendingPathComponent("saved", isDirectory: true)
        let saved = saveDirectory.appendingPathComponent("capture.png")
        var generations = 0

        let first = ScreenshotEditorSaveDestination.resolve(
            currentURL: source,
            format: .png,
            needsPermanentURL: true,
            configuredDirectory: saveDirectory,
            temporaryRoot: temporaryRoot,
            generate: { generations += 1; return saved }
        )
        XCTAssertEqual(first, saved)
        XCTAssertEqual(generations, 1)

        let unsafe = ScreenshotEditorSaveDestination.resolve(
            currentURL: source,
            format: .png,
            needsPermanentURL: true,
            configuredDirectory: source.deletingLastPathComponent(),
            temporaryRoot: temporaryRoot,
            generate: { XCTFail("An unsafe directory must not be used"); return source }
        )
        XCTAssertNil(unsafe)

        let again = ScreenshotEditorSaveDestination.resolve(
            currentURL: saved,
            format: .png,
            needsPermanentURL: false,
            configuredDirectory: saveDirectory,
            temporaryRoot: temporaryRoot,
            generate: { XCTFail("An existing save should be reused"); return source }
        )
        XCTAssertEqual(again, saved)
        let changedFormat = ScreenshotEditorSaveDestination.resolve(
            currentURL: saved,
            format: .jpeg,
            needsPermanentURL: false,
            configuredDirectory: saveDirectory,
            temporaryRoot: temporaryRoot,
            generate: { XCTFail("A format change should not generate a new capture name"); return source }
        )
        XCTAssertEqual(changedFormat, saveDirectory.appendingPathComponent("capture.jpg"))
    }
}
