import AVFoundation
import CoreImage
import XCTest
@testable import TinyClips

final class StudioRenderGeometryTests: XCTestCase {
    func testDisplayRotationIsConvertedToCoreImageBeforeCropping() {
        let size = CGSize(width: 200, height: 100)
        let rotations: [(StudioRenderAffineTransform, CGPoint, CGPoint)] = [
            (.identity, CGPoint(x: 0, y: 100), CGPoint(x: 200, y: 0)),
            (.init(a: 0, b: 1, c: -1, d: 0, tx: 100, ty: 0), CGPoint(x: 100, y: 200), .zero),
            (.init(a: -1, b: 0, c: 0, d: -1, tx: 200, ty: 100), CGPoint(x: 200, y: 0), CGPoint(x: 0, y: 100)),
            (.init(a: 0, b: -1, c: 1, d: 0, tx: 0, ty: 200), .zero, CGPoint(x: 100, y: 200)),
        ]
        for (preferred, topLeft, bottomRight) in rotations {
            let transform = StudioRenderGeometry.sourceOrientationTransform(preferred: preferred, sourceSize: size)
            XCTAssertEqual(transform.applying(to: CGPoint(x: 0, y: 100)), topLeft)
            XCTAssertEqual(transform.applying(to: CGPoint(x: 200, y: 0)), bottomRight)
        }
    }

    func testConvertsTopLeftRectToCoreImageRect() {
        let rect = StudioRect(x: 10, y: 20, width: 300, height: 120)
        let converted = StudioRenderGeometry.ciRect(forTopLeftRect: rect, renderSize: CGSize(width: 800, height: 600))

        XCTAssertEqual(converted.origin.x, 10, accuracy: 1e-6)
        XCTAssertEqual(converted.origin.y, 460, accuracy: 1e-6)
        XCTAssertEqual(converted.width, 300, accuracy: 1e-6)
        XCTAssertEqual(converted.height, 120, accuracy: 1e-6)
    }

    func testBuildsSourceCropRectInBottomLeftSpace() {
        let crop = StudioRect(x: 0.25, y: 0.1, width: 0.5, height: 0.4)
        let rect = StudioRenderGeometry.sourceCropRect(source: crop, naturalSize: CGSize(width: 1920, height: 1080))

        XCTAssertEqual(rect.origin.x, 480, accuracy: 1e-6)
        XCTAssertEqual(rect.origin.y, 540, accuracy: 1e-6)
        XCTAssertEqual(rect.width, 960, accuracy: 1e-6)
        XCTAssertEqual(rect.height, 432, accuracy: 1e-6)
    }

    func testSourceToDestinationTransformMapsCropCornersAndMirrorsWhenRequested() {
        let sourceSize = CGSize(width: 1000, height: 500)
        let crop = StudioRect(x: 0.2, y: 0.1, width: 0.5, height: 0.4)
        let destination = StudioRect(x: 100, y: 50, width: 300, height: 200)
        let renderSize = CGSize(width: 800, height: 600)

        let transform = StudioRenderGeometry.sourceToDestinationTransform(
            sourceSize: sourceSize,
            sourceCrop: crop,
            destinationRect: destination,
            renderSize: renderSize,
            mirrored: false
        )
        let mirrored = StudioRenderGeometry.sourceToDestinationTransform(
            sourceSize: sourceSize,
            sourceCrop: crop,
            destinationRect: destination,
            renderSize: renderSize,
            mirrored: true
        )

        let cropRect = StudioRenderGeometry.sourceCropRect(source: crop, naturalSize: sourceSize)
        XCTAssertEqual(transform.applying(to: cropRect.origin).x, 100, accuracy: 1e-6)
        XCTAssertEqual(transform.applying(to: cropRect.origin).y, 350, accuracy: 1e-6)
        XCTAssertEqual(transform.applying(to: CGPoint(x: cropRect.maxX, y: cropRect.maxY)).x, 400, accuracy: 1e-6)
        XCTAssertEqual(transform.applying(to: CGPoint(x: cropRect.maxX, y: cropRect.maxY)).y, 550, accuracy: 1e-6)

        XCTAssertEqual(mirrored.applying(to: cropRect.origin).x, 400, accuracy: 1e-6)
        XCTAssertEqual(mirrored.applying(to: CGPoint(x: cropRect.maxX, y: cropRect.minY)).x, 100, accuracy: 1e-6)
    }

    func testGradientDirectionDisplayClampsBeforeIntegerConversion() {
        for (stored, expected) in [(1e20, 360), (-1e20, 0), (Double.infinity, 45), (Double.nan, 45), (123, 123)] {
            XCTAssertEqual(Int(StudioRenderGeometry.clampedGradientDirectionDegrees(stored)), expected)
        }
    }

    func testGradientEndpointsMatchScreenshotEditorDirection() {
        let leftToRight = StudioRenderGeometry.gradientEndpoints(
            renderSize: CGSize(width: 200, height: 100),
            directionDegrees: 0
        )
        let topToBottom = StudioRenderGeometry.gradientEndpoints(
            renderSize: CGSize(width: 200, height: 100),
            directionDegrees: 90
        )
        let diagonal = StudioRenderGeometry.gradientEndpoints(
            renderSize: CGSize(width: 200, height: 100),
            directionDegrees: 45
        )

        XCTAssertEqual(leftToRight.start.x, 0, accuracy: 1e-6)
        XCTAssertEqual(leftToRight.start.y, 50, accuracy: 1e-6)
        XCTAssertEqual(leftToRight.end.x, 200, accuracy: 1e-6)
        XCTAssertEqual(leftToRight.end.y, 50, accuracy: 1e-6)
        XCTAssertEqual(topToBottom.start.x, 100, accuracy: 1e-6)
        XCTAssertEqual(topToBottom.start.y, 100, accuracy: 1e-6)
        XCTAssertEqual(topToBottom.end.x, 100, accuracy: 1e-6)
        XCTAssertEqual(topToBottom.end.y, 0, accuracy: 1e-6)
        XCTAssertEqual(diagonal.start.x, 25, accuracy: 1e-6)
        XCTAssertEqual(diagonal.start.y, 125, accuracy: 1e-6)
        XCTAssertEqual(diagonal.end.x, 175, accuracy: 1e-6)
        XCTAssertEqual(diagonal.end.y, -25, accuracy: 1e-6)
    }

    func testSquirclePointsStayOnSuperellipse() {
        let rect = StudioRect(x: 10, y: 20, width: 120, height: 80)
        let points = StudioRenderGeometry.squirclePoints(in: rect, pointCount: 32)

        XCTAssertEqual(points.count, 32)
        let a = rect.width / 2
        let b = rect.height / 2
        let center = CGPoint(x: rect.x + a, y: rect.y + b)
        for point in points {
            let value = pow(abs((point.x - center.x) / a), 5) + pow(abs((point.y - center.y) / b), 5)
            XCTAssertEqual(value, 1, accuracy: 1e-6)
        }
    }

    func testClickRingUsesMousePulseTimingAndSourceCrop() {
        let screen = StudioResolvedScreen(
            rect: StudioRect(x: 100, y: 50, width: 800, height: 400),
            source: StudioRect(x: 0.25, y: 0.25, width: 0.5, height: 0.5),
            cornerRadius: 10,
            shadow: StudioResolvedShadow(blur: 0, offsetY: 0, opacity: 0)
        )
        let overlay = StudioClickOverlay(size: 40, strokeWidth: 4, opacity: 0.8, duration: 0.5)
        let capture = StudioCaptureInfo(width: 2000, height: 1000, scale: 2)
        let event = StudioClickEvent(t: 1, x: 0.5, y: 0.5)

        let ring = StudioRenderGeometry.clickRing(
            for: event,
            at: 1.25,
            screen: screen,
            overlay: overlay,
            capture: capture
        )

        XCTAssertEqual(ring?.center.x ?? -1, 500, accuracy: 1e-6)
        XCTAssertEqual(ring?.center.y ?? -1, 250, accuracy: 1e-6)
        XCTAssertEqual(ring?.radius ?? -1, 50.56, accuracy: 1e-6)
        XCTAssertEqual(ring?.strokeWidth ?? -1, 6.4, accuracy: 1e-6)
        XCTAssertEqual(ring?.opacity ?? -1, 0.4, accuracy: 1e-6)

        XCTAssertNil(StudioRenderGeometry.clickRing(for: event, at: 0.9, screen: screen, overlay: overlay, capture: capture))
        XCTAssertNil(StudioRenderGeometry.clickRing(for: StudioClickEvent(t: 1, x: 0.1, y: 0.5), at: 1.1, screen: screen, overlay: overlay, capture: capture))
    }

    func testActiveClickRingsAndGradientHelpers() {
        let frame = StudioResolvedFrame(
            sceneIndex: 0,
            layout: .screen,
            screen: StudioResolvedScreen(
                rect: StudioRect(x: 0, y: 0, width: 100, height: 100),
                source: StudioRect(x: 0, y: 0, width: 1, height: 1),
                cornerRadius: 0,
                shadow: StudioResolvedShadow(blur: 0, offsetY: 0, opacity: 0)
            ),
            camera: nil
        )
        let events = StudioEvents(
            capture: StudioCaptureInfo(width: 100, height: 100, scale: 1),
            clicks: [
                StudioClickEvent(t: 0.8, x: 0.5, y: 0.5),
                StudioClickEvent(t: 0.1, x: 0.5, y: 0.5)
            ]
        )

        XCTAssertEqual(StudioRenderGeometry.activeClickRings(at: 1, events: events, frame: frame, overlay: StudioClickOverlay()).count, 1)
        let gradient = StudioRenderGeometry.gradientEndpoints(renderSize: CGSize(width: 200, height: 100))
        XCTAssertEqual(gradient.start.x, 25, accuracy: 1e-6)
        XCTAssertEqual(gradient.start.y, 125, accuracy: 1e-6)
        XCTAssertEqual(gradient.end.x, 175, accuracy: 1e-6)
        XCTAssertEqual(gradient.end.y, -25, accuracy: 1e-6)
    }

    // MARK: - Dividing a Piece at the Edges of a Track

    private func time(_ seconds: Double, _ timescale: CMTimeScale = 600) -> CMTime {
        CMTime(seconds: seconds, preferredTimescale: timescale)
    }

    private func range(_ start: Double, _ end: Double) -> CMTimeRange {
        CMTimeRange(start: time(start), end: time(end))
    }

    func testARangeIsDividedAtEveryEdgeInsideItInOrder() {
        let parts = StudioCompositionBuilder.divided(range(2, 8), at: [time(6.5, 48_000), time(3), time(7)])

        XCTAssertEqual(parts.map(\.start.seconds), [2, 3, 6.5, 7])
        XCTAssertEqual(parts.map(\.end.seconds), [3, 6.5, 7, 8])
        // The parts are the range again, with nothing between them.
        XCTAssertEqual(parts.first?.start, time(2))
        XCTAssertEqual(parts.last?.end, time(8))
        for (part, next) in zip(parts, parts.dropFirst()) {
            XCTAssertEqual(part.end, next.start)
        }
    }

    func testAnEdgeAtAnEndOfTheRangeOrOutsideItDividesNothing() {
        let whole = range(2, 8)

        XCTAssertEqual(StudioCompositionBuilder.divided(whole, at: []), [whole])
        XCTAssertEqual(StudioCompositionBuilder.divided(whole, at: [time(2), time(8), time(1), time(9)]), [whole])
        XCTAssertEqual(StudioCompositionBuilder.divided(whole, at: [.invalid, .positiveInfinity, .indefinite]), [whole])
    }

    func testAnEdgeNamedTwiceDividesOnce() {
        let parts = StudioCompositionBuilder.divided(range(2, 8), at: [time(5), time(5, 48_000), time(5)])

        XCTAssertEqual(parts.map(\.start.seconds), [2, 5])
        XCTAssertEqual(parts.map(\.end.seconds), [5, 8])
    }
}

final class StudioTimelineRenderingTests: XCTestCase {
    // Two 1-second synthetic H.264 test cards (24 fps green + 440 Hz, 60 fps blue + 880 Hz).
    // The second has a 90° display matrix; neither file needs a recorder or capture permission.
    private let size = CGSize(width: 96, height: 64)
    private var paths: StudioProjectPaths {
        let dir = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
            .appendingPathComponent("Fixtures/StudioRendering", isDirectory: true)
        return StudioProjectPaths(id: "render-test", projectDirectory: dir,
                                  projectJSONURL: dir.appendingPathComponent("project.json"),
                                  screenURL: dir.appendingPathComponent("not-used.mp4"), cameraURL: nil,
                                  eventsURL: dir.appendingPathComponent("events.json"), posterURL: dir.appendingPathComponent("poster.jpg"))
    }

    private func manifest() throws -> StudioProject {
        try StudioJSON.makeDecoder().decode(StudioProject.self, from: Data(contentsOf: paths.projectJSONURL))
    }

    func testExplicitEventFileCannotSilentlyDisappear() async throws {
        var project = try manifest()
        project.mediaSources?[0].sources.events = "missing-events.json"
        do {
            _ = try await StudioCompositionBuilder.build(project: project, paths: paths, renderSize: size)
            XCTFail("A missing capture-events sidecar must fail the build")
        } catch let error as NSError {
            XCTAssertEqual(error.domain, NSCocoaErrorDomain)
            XCTAssertEqual(error.code, NSFileReadNoSuchFileError)
        }
    }

    func testFractionalClipDurationsDoNotAccumulateRoundingAtCuts() async throws {
        var project = try manifest()
        let clips = (0..<19).map {
            StudioTimelineClip(id: "repeat-\($0)", sourceId: "import", sourceStart: 0.01, sourceEnd: 0.51,
                               edits: StudioEdits(speed: [.init(start: 0, end: 1, rate: 3.7)]))
        }
        project.timeline = StudioTimeline(clips: clips)
        let build = try await StudioCompositionBuilder.build(project: project, paths: paths, renderSize: size)
        let instructions = try XCTUnwrap(build.videoComposition.instructions as? [StudioVideoCompositionInstruction])
        XCTAssertEqual(instructions.count, clips.count)
        for (index, instruction) in instructions.enumerated() {
            let exactStart = Double(index) * 0.5 / 3.7
            XCTAssertEqual(instruction.timeRange.start, CMTime(seconds: exactStart, preferredTimescale: 48_000))
            XCTAssertEqual(instruction.primary.position(at: instruction.timeRange.start, map: instruction.primary.renderState.read().timeMap).source, 0.01)
        }
        XCTAssertEqual(build.composition.duration, CMTime(seconds: Double(clips.count) * 0.5 / 3.7, preferredTimescale: 48_000))
        XCTAssertEqual(build.composition.tracks(withMediaType: .video).count, 1)
    }

    @MainActor
    func testPreviewAndExportFramesAcrossCut() async throws {
        let project = try manifest()
        let player = StudioPlayback(project: project, paths: paths, renderSize: size)
        let item = try await player.makePlayerItem()
        let preview = AVAssetImageGenerator(asset: item.asset)
        preview.videoComposition = item.videoComposition
        preview.requestedTimeToleranceBefore = .zero
        preview.requestedTimeToleranceAfter = .zero
        let directory = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent(".build/studio-rendering-tests")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let url = directory.appendingPathComponent("\(UUID().uuidString).mp4")
        defer { try? FileManager.default.removeItem(at: url) }
        try await StudioExporter.export(project: project, events: StudioEvents(), paths: paths,
                                        outputURL: url, renderSize: size, codec: .h264, quality: .standard)
        let movie = AVURLAsset(url: url)
        let export = AVAssetImageGenerator(asset: movie)
        export.requestedTimeToleranceBefore = .zero
        export.requestedTimeToleranceAfter = .zero

        let ci = CIContext(options: [.useSoftwareRenderer: true])
        func color(_ image: CIImage, x: Int) -> String {
            var pixel = [UInt8](repeating: 0, count: 4)
            ci.render(image, toBitmap: &pixel, rowBytes: 4, bounds: CGRect(x: x, y: 32, width: 1, height: 1),
                      format: .RGBA8, colorSpace: CGColorSpaceCreateDeviceRGB())
            if pixel[1] > 180 && pixel[2] < 50 { return "green" }
            if pixel[2] > 180 && pixel[1] < 50 { return "blue" }
            return pixel.prefix(3).allSatisfy { $0 < 50 } ? "black" : "\(pixel)"
        }
        func hasCaption(_ image: CIImage) -> Bool {
            let rect = CGRect(x: 24, y: 0, width: 48, height: 14)
            var rgba = [UInt8](repeating: 0, count: 48 * 14 * 4)
            ci.render(image, toBitmap: &rgba, rowBytes: 48 * 4, bounds: rect,
                      format: .RGBA8, colorSpace: CGColorSpaceCreateDeviceRGB())
            return stride(from: 0, to: rgba.count, by: 4).contains { rgba[$0] > 100 && rgba[$0 + 1] > 100 }
        }
        let expected = [0: "black", 4: "green", 14: "green", 15: "blue", 16: "blue", 23: "blue"]
        for (frame, center) in expected.sorted(by: { $0.key < $1.key }) {
            let time = CMTime(value: CMTimeValue(frame), timescale: 30)
            let written = CIImage(cgImage: try await export.image(at: time).image)
            let shown = CIImage(cgImage: try await preview.image(at: time).image)
            for image in [shown, written] {
                XCTAssertEqual(image.extent.size, size)
                XCTAssertEqual(color(image, x: 48), center, "frame \(frame)")
                if frame >= 15 { XCTAssertEqual(color(image, x: 10), "black", "Portrait is fitted, not stretched") }
                XCTAssertEqual(hasCaption(image), frame == 15 || frame == 16, "Caption timing at frame \(frame)")
            }
        }
        let windows = [0.08..<0.18, 0.32..<0.42, 0.62..<0.72]
        let rendered = try await readSound(movie, windows: windows)
        XCTAssertEqual(Double(rendered[0].crossings), 44, accuracy: 2)
        XCTAssertLessThan(rendered[1].rms, 0.001, "Speed-edited picture must have no soundtrack")
        XCTAssertEqual(Double(rendered[2].crossings), 88, accuracy: 2)
        // AAC sources don't have identical levels. Compare gain against their decoded source
        // windows; using the ratio cancels the common mono/stereo rematrix in the export.
        let native = try await readSound(AVURLAsset(url: paths.projectDirectory.appendingPathComponent("native.mp4")),
                                         windows: [windows[0]])
        let flat = try await readSound(AVURLAsset(url: paths.projectDirectory.appendingPathComponent("flat.mp4")),
                                       windows: [0.37..<0.47])
        let gainRatio = (rendered[0].rms / native[0].rms) / (rendered[2].rms / flat[0].rms)
        XCTAssertEqual(gainRatio, 3, accuracy: 0.4, "Clip-specific microphone/mixed gains")
    }

    private func readSound(_ movie: AVAsset, windows: [Range<Double>]) async throws -> [(rms: Double, crossings: Int)] {
        let audio = try await movie.loadTracks(withMediaType: .audio)
        XCTAssertEqual(audio.count, 1)
        let reader = try AVAssetReader(asset: movie)
        let sound = AVAssetReaderTrackOutput(track: try XCTUnwrap(audio.first), outputSettings: [
            AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: 48_000, AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true, AVLinearPCMIsNonInterleaved: false,
        ])
        reader.add(sound)
        XCTAssertTrue(reader.startReading())
        var crossings = Array(repeating: 0, count: windows.count), counts = crossings
        var energy = Array(repeating: 0.0, count: windows.count)
        var previous: Float = 0
        while let sample = sound.copyNextSampleBuffer() {
            let block = try XCTUnwrap(sample.dataBuffer)
            var data = [Float](repeating: 0, count: block.dataLength / MemoryLayout<Float>.size)
            XCTAssertEqual(CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: block.dataLength, destination: &data), noErr)
            let start = sample.presentationTimeStamp.seconds
            for (i, value) in data.enumerated() {
                let seconds = start + Double(i) / 48_000
                if let window = windows.firstIndex(where: { $0.contains(seconds) }) {
                    energy[window] += Double(value * value)
                    counts[window] += 1
                    if previous < 0, value >= 0 { crossings[window] += 1 }
                }
                previous = value
            }
        }
        XCTAssertEqual(reader.status, .completed)
        return windows.indices.map { (sqrt(energy[$0] / Double(counts[$0])), crossings[$0]) }
    }

    @MainActor
    func testColorFadesAndDissolveInPreviewAndWrittenMovie() async throws {
        var project = try manifest()
        func clip(_ id: String, _ source: String, _ entry: StudioClipTransition = .cut) -> StudioTimelineClip {
            StudioTimelineClip(id: id, sourceId: source, sourceStart: 0, sourceEnd: 1, transitionIn: entry,
                               scenes: [StudioScene(layout: .screen)], audio: StudioAudio())
        }
        var outgoing = clip("c", "recording", .fadeThroughWhite)
        outgoing.overlays = StudioOverlays(annotations: [
            StudioAnnotation(id: "hide", kind: "redact", attach: .frame, start: 0.60, end: 0.68,
                             x: 0.5, y: 0.5, fill: "#000000", width: 1, height: 1, redactStyle: .solid)
        ])
        var incoming = clip("d", "import")
        // A source cut inside the dissolve must not restart its ramp. A later slow
        // piece makes up the cut's duration so both source cards still last one second.
        incoming.edits = StudioEdits(cuts: [.init(start: 0.1875, end: 0.3125)], speed: [.init(start: 0.625, end: 0.75, rate: 0.5)])
        project.timeline = StudioTimeline(clips: [clip("a", "recording"), clip("b", "import", .fadeThroughBlack), outgoing, incoming])
        let playback = StudioPlayback(project: project, paths: paths, renderSize: size)
        let before = try await playback.makePlayerItem()
        let originalDuration = try await before.asset.load(.duration)
        XCTAssertEqual(originalDuration.seconds, 4, accuracy: 0.0001)
        project.timeline?.clips[3].transitionIn = .crossDissolve
        let item = try await playback.apply(project: project)
        XCTAssertFalse(item === before)
        let editedDuration = try await item.asset.load(.duration)
        XCTAssertEqual(editedDuration.seconds, 3.5, accuracy: 0.0001)
        let mid = CMTime(seconds: 2.75, preferredTimescale: 48_000)
        let ramps = try XCTUnwrap(item.audioMix).inputParameters.compactMap { parameters -> [Float]? in
            var start: Float = 0, end: Float = 0
            var range = CMTimeRange.zero
            guard parameters.getVolumeRamp(for: mid, startVolume: &start, endVolume: &end, timeRange: &range) else { return nil }
            XCTAssertEqual(range.start.seconds, 2.5, accuracy: 0.0001)
            XCTAssertEqual(range.duration.seconds, 0.5, accuracy: 0.0001)
            return [start, end]
        }
        XCTAssertEqual(ramps.sorted { $0[0] < $1[0] }, [[0, 1], [1, 0]])

        let url = FileManager.default.temporaryDirectory.appendingPathComponent("\(UUID().uuidString).mp4")
        defer { try? FileManager.default.removeItem(at: url) }
        try await StudioExporter.export(project: project, events: StudioEvents(), paths: paths, outputURL: url,
                                        renderSize: size, codec: .h264, quality: .standard)
        let movie = AVURLAsset(url: url)
        let writtenDuration = try await movie.load(.duration)
        XCTAssertEqual(writtenDuration.seconds, 3.5, accuracy: 1.0 / 30)
        let preview = AVAssetImageGenerator(asset: item.asset)
        preview.videoComposition = item.videoComposition
        let context = CIContext(options: [.useSoftwareRenderer: true])
        // At frame 79 only the outgoing image is redacted: the incoming blue must still show.
        let samples = [(15, [0, 255, 0]), (26, [0, 136, 0]), (30, [0, 0, 0]), (34, [0, 0, 136]),
                       (45, [0, 0, 255]), (56, [119, 119, 255]), (60, [255, 255, 255]), (64, [119, 255, 119]),
                       (75, [0, 255, 0]), (79, [0, 0, 68]), (82, [0, 136, 119]), (90, [0, 0, 255])]
        for generator in [preview, AVAssetImageGenerator(asset: movie)] {
            generator.requestedTimeToleranceBefore = .zero
            generator.requestedTimeToleranceAfter = .zero
            for (frame, rgb) in samples {
                let image = try await generator.image(at: CMTime(value: Int64(frame), timescale: 30)).image
                var pixel = [UInt8](repeating: 0, count: 4)
                context.render(CIImage(cgImage: image), toBitmap: &pixel, rowBytes: 4,
                               bounds: CGRect(x: 48, y: 32, width: 1, height: 1), format: .RGBA8,
                               colorSpace: CGColorSpaceCreateDeviceRGB())
                for channel in 0..<3 {
                    XCTAssertEqual(Double(pixel[channel]), Double(rgb[channel]), accuracy: 28,
                                   "\(generator === preview ? "Preview" : "Export") frame \(frame): \(pixel)")
                }
            }
        }
        let sound = try await readSound(movie, windows: [2.30..<2.40, 2.70..<2.80, 3.25..<3.35])
        XCTAssertEqual(Double(sound[0].crossings), 44, accuracy: 2)
        XCTAssertEqual(Double(sound[2].crossings), 88, accuracy: 2)
        let midpointLevel = hypot(sound[0].rms, sound[2].rms) / 2
        XCTAssertEqual(sound[1].rms, midpointLevel, accuracy: midpointLevel * 0.12)
    }

    @MainActor
    func testClipIsolationAndLiveUpdates() async throws {
        var project = try manifest()
        let playback = StudioPlayback(project: project, paths: paths, renderSize: size)
        let item = try await playback.makePlayerItem()
        let composition = try XCTUnwrap(item.asset as? AVComposition)
        let video = try XCTUnwrap(item.videoComposition)
        let instructions = try XCTUnwrap(video.instructions as? [StudioVideoCompositionInstruction])
        XCTAssertEqual(composition.duration.seconds, 1, accuracy: 0.0001)
        XCTAssertEqual(video.frameDuration, CMTime(value: 1, timescale: 30))
        XCTAssertEqual(instructions.map(\.timeRange.start.seconds), [0, 0.25, 0.5])
        XCTAssertEqual(instructions.map { $0.requiredSourceTrackIDs?.count }, [2, 2, 1])
        XCTAssertEqual(instructions.map { $0.primary.renderState.read().events.clicks.count }, [1, 1, 0])
        XCTAssertEqual(instructions.map { $0.primary.position(at: $0.timeRange.start, map: $0.primary.renderState.read().timeMap).source },
                       [0, 0.5, 0.25])
        let imported = try XCTUnwrap(instructions.last?.primary)
        XCTAssertEqual(imported.renderState.read().captions, [.init(start: 0, end: 0.25, text: "Second")])
        let tracks = composition.tracks(withMediaType: .audio)
        XCTAssertEqual(tracks.count, 1)
        let sound = try XCTUnwrap(tracks.first)
        XCTAssertEqual(sound.segments.filter { !$0.isEmpty }.map(\.timeMapping.target), [
            CMTimeRange(start: .zero, duration: CMTime(value: 1, timescale: 4)),
            CMTimeRange(start: CMTime(value: 1, timescale: 2), duration: CMTime(value: 1, timescale: 2)),
        ])
        func volume(at seconds: Double) throws -> Float {
            let mix = try XCTUnwrap(item.audioMix?.inputParameters.first)
            var start: Float = 0, end: Float = 0
            var range = CMTimeRange.zero
            XCTAssertTrue(mix.getVolumeRamp(for: CMTime(seconds: seconds, preferredTimescale: 600),
                                            startVolume: &start, endVolume: &end, timeRange: &range))
            return start
        }
        XCTAssertEqual(try volume(at: 0), 0.75)
        XCTAssertEqual(try volume(at: 0.5), 0.25)
        project.timeline?.clips[1].audio?.volume = 0.5
        project.timeline?.clips[1].captions?.cues[0].text = "Edited"
        let changedLook = try await playback.apply(project: project)
        XCTAssertTrue(changedLook === item)
        XCTAssertEqual(try volume(at: 0.5), 0.5)
        XCTAssertEqual(imported.renderState.read().captions.first?.text, "Edited")
        project.timeline?.clips.reverse()
        let reordered = try await playback.apply(project: project)
        XCTAssertFalse(reordered === item)
        let first = try XCTUnwrap(reordered.videoComposition?.instructions.first as? StudioVideoCompositionInstruction).primary
        XCTAssertNil(first.cameraTrackID)
        XCTAssertEqual(first.renderState.read().project.sources.screen.file, "flat.mp4")
    }
}
