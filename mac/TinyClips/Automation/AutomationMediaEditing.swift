import AVFoundation
import CoreGraphics
import Darwin
import Foundation
import ImageIO
import UniformTypeIdentifiers

/// Maps agent-supplied seconds onto video time ranges and GIF frames.
enum AutomationMediaRange {
    struct Seconds: Equatable {
        var start: Double
        var end: Double
    }

    /// Validates a trim range and clamps its end to the media duration.
    static func clamp(start: Double, end: Double, duration: Double) throws -> Seconds {
        guard start.isFinite, end.isFinite, start >= 0 else {
            throw AutomationError(.invalidParams, "start and end must be non-negative numbers of seconds.")
        }
        guard end > start else {
            throw AutomationError(.invalidParams, "end must be greater than start.")
        }
        guard duration > 0, start < duration else {
            throw AutomationError(.invalidParams, "start must be before the end of the media (\(formatted(duration)) s).")
        }
        return Seconds(start: start, end: min(end, duration))
    }

    /// The frames shown between `range.start` and `range.end`.
    static func gifFrames(for range: Seconds, timing: GifTiming) -> ClosedRange<Int> {
        let first = frameIndex(at: range.start, timing: timing)
        var last = first
        let count = timing.frameDelays.count
        var nextStartTicks = gifStartTicks(of: first + 1, timing: timing)
        while last + 1 < count, nextStartTicks / 100 < range.end {
            last += 1
            nextStartTicks += gifDelayTicks(timing.frameDelays[last])
        }
        return first...last
    }

    /// The frame on screen at `time` seconds.
    static func gifFrame(at time: Double, timing: GifTiming) throws -> Int {
        let duration = gifStartTime(of: timing.frameDelays.count, timing: timing)
        guard time.isFinite, time >= 0, time < duration else {
            throw AutomationError(.invalidParams, "time must be within the GIF (0–\(formatted(duration)) s).")
        }
        return frameIndex(at: time, timing: timing)
    }

    static func gifStartTime(of frame: Int, timing: GifTiming) -> Double {
        gifStartTicks(of: frame, timing: timing) / 100
    }

    // GIF delays are stored in centiseconds; summing seconds drifts across frame boundaries.
    private static func gifStartTicks(of frame: Int, timing: GifTiming) -> Double {
        timing.frameDelays.prefix(frame).reduce(0) { $0 + gifDelayTicks($1) }
    }

    private static func gifDelayTicks(_ delay: Double) -> Double {
        (delay * 100).rounded()
    }

    private static func frameIndex(at time: Double, timing: GifTiming) -> Int {
        var index = 0
        let count = timing.frameDelays.count
        var nextStartTicks = count > 0 ? gifDelayTicks(timing.frameDelays[0]) : 0
        while index + 1 < count, nextStartTicks / 100 <= time {
            index += 1
            nextStartTicks += gifDelayTicks(timing.frameDelays[index])
        }
        return index
    }

    private static func formatted(_ seconds: Double) -> String {
        String(format: "%.2f", seconds)
    }
}

/// Non-destructive crop, trim, and frame export for automation. Sources are never modified or deleted.
@MainActor
enum AutomationMediaEditing {
    struct TrimResult {
        var url: URL
        var range: AutomationMediaRange.Seconds
        var frameCount: Int?
    }

    struct FrameResult {
        var url: URL
        var time: Double
        var frame: Int?
    }

    static func crop(source: URL, region: Any?, destination: () -> URL) throws -> (url: URL, width: Int, height: Int) {
        let bounds = try AutomationCaptureTargets.parseRegion(region)
        // Authorizing a library entry must not also authorize data behind a symlink or a FIFO.
        let fd = open(source.path, O_RDONLY | O_NOFOLLOW_ANY | O_NONBLOCK | O_CLOEXEC)
        guard fd >= 0 else { throw AutomationError(.notFound, "That file is not a library screenshot.") }
        defer { close(fd) }
        var info = stat()
        guard fstat(fd, &info) == 0, info.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG) else {
            throw AutomationError(.notFound, "That file is not a regular library screenshot.")
        }
        // Decode only the bytes from the authorized descriptor; never reopen the client path.
        let input = FileHandle(fileDescriptor: fd, closeOnDealloc: false)
        guard info.st_size > 0, info.st_size <= 256 * 1024 * 1024 else {
            throw AutomationError(.invalidParams, "Expected a nonempty screenshot file of 256 MiB or less.")
        }
        guard let data = try input.read(upToCount: Int(info.st_size)), data.count == Int(info.st_size),
              let imageSource = CGImageSourceCreateWithData(data as CFData, nil),
              let type = CGImageSourceGetType(imageSource) as String?,
              [UTType.png, .jpeg, .webP, .heic].contains(where: { $0.identifier == type }),
              CGImageSourceGetCount(imageSource) == 1,
              let properties = CGImageSourceCopyPropertiesAtIndex(imageSource, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int, width > 0,
              let height = properties[kCGImagePropertyPixelHeight] as? Int, height > 0,
              width <= 32_000_000 / height else {
            throw AutomationError(.invalidParams, "Expected a readable still screenshot of 32 megapixels or less.")
        }
        // Parsed components are bounded 32-bit integers; their sums are exact in CGFloat on macOS.
        guard bounds.maxX <= CGFloat(width), bounds.maxY <= CGFloat(height) else {
            throw AutomationError(.invalidParams, "region must fit inside the source image (\(width) × \(height) pixels).")
        }
        guard let image = CGImageSourceCreateImageAtIndex(imageSource, 0, nil),
              let crop = image.cropping(to: bounds) else {
            throw AutomationError(.invalidParams, "Could not crop the source image.")
        }
        let png = NSMutableData()
        guard let encoder = CGImageDestinationCreateWithData(png as CFMutableData, UTType.png.identifier as CFString, 1, nil) else {
            throw AutomationError(.internal, "Could not create the cropped PNG.")
        }
        CGImageDestinationAddImage(encoder, crop, nil)
        guard CGImageDestinationFinalize(encoder) else { throw AutomationError(.internal, "Could not encode the cropped PNG.") }
        let result = destination()
        do {
            try (png as Data).write(to: result, options: .withoutOverwriting)
        } catch {
            throw AutomationError(.internal, "Could not save the cropped PNG as a new file: \(error.localizedDescription)")
        }
        return (result, crop.width, crop.height)
    }

    static func trimVideo(source: URL, start: Double, end: Double, removeAudio: Bool) async throws -> TrimResult {
        let asset = AVURLAsset(url: source)
        guard let videoTrack = try await asset.loadTracks(withMediaType: .video).first else {
            throw AutomationError(.invalidParams, "The file has no video track.")
        }
        let duration = try await asset.load(.duration).seconds
        let range = try AutomationMediaRange.clamp(start: start, end: end, duration: duration)
        let timeRange = CMTimeRange(
            start: CMTime(seconds: range.start, preferredTimescale: 600),
            end: CMTime(seconds: range.end, preferredTimescale: 600)
        )

        let composition = AVMutableComposition()
        guard let compositionVideo = composition.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid) else {
            throw AutomationError(.internal, "Could not prepare the trimmed video.")
        }
        try compositionVideo.insertTimeRange(timeRange, of: videoTrack, at: .zero)
        compositionVideo.preferredTransform = try await videoTrack.load(.preferredTransform)
        if !removeAudio {
            // Keep every audio track (system audio and microphone) and mix them into one.
            for audioTrack in try await asset.loadTracks(withMediaType: .audio) {
                guard let compositionAudio = composition.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid) else {
                    throw AutomationError(.internal, "Could not prepare the trimmed audio.")
                }
                try compositionAudio.insertTimeRange(timeRange, of: audioTrack, at: .zero)
            }
        }

        let formatDescriptions = try await videoTrack.load(.formatDescriptions)
        let codec: VideoCodec = formatDescriptions.first.map {
            CMFormatDescriptionGetMediaSubType($0) == kCMVideoCodecType_HEVC ? .hevc : .h264
        } ?? .h264
        guard let session = AVAssetExportSession(
            asset: composition,
            presetName: RecordingVideoCodecResolver.exportPreset(for: codec)
        ) else {
            throw AutomationError(.internal, "Could not start the video export.")
        }
        session.audioMix = RecordingAudioMixdown.audioMix(for: composition)

        let outputURL = SaveService.shared.generateURL(
            for: .video,
            fileExtension: "mp4",
            stemSuffix: removeAudio ? "(trimmed, no audio)" : "(trimmed)"
        )
        try ensureNewFile(outputURL, source: source)
        do {
            try await session.export(to: outputURL, as: .mp4)
        } catch {
            try? FileManager.default.removeItem(at: outputURL)
            throw AutomationError(.internal, "Could not export the trimmed video: \(error.localizedDescription)")
        }
        return TrimResult(url: outputURL, range: range, frameCount: nil)
    }

    static func trimGif(source: URL, start: Double, end: Double) throws -> TrimResult {
        let gif = try loadGif(source)
        let duration = AutomationMediaRange.gifStartTime(of: gif.frames.count, timing: gif.timing)
        let range = try AutomationMediaRange.clamp(start: start, end: end, duration: duration)
        let frames = AutomationMediaRange.gifFrames(for: range, timing: gif.timing)
        let trimmed = try gif.selecting(frames, speed: 1)

        let outputURL = SaveService.shared.generateURL(for: .gif, fileExtension: "gif", stemSuffix: "(trimmed)")
        try ensureNewFile(outputURL, source: source)
        do {
            try GifWriter.writeGIF(trimmed, to: outputURL)
        } catch {
            try? FileManager.default.removeItem(at: outputURL)
            throw AutomationError(.internal, "Could not write the trimmed GIF: \(error.localizedDescription)")
        }
        let actual = AutomationMediaRange.Seconds(
            start: AutomationMediaRange.gifStartTime(of: frames.lowerBound, timing: gif.timing),
            end: AutomationMediaRange.gifStartTime(of: frames.upperBound + 1, timing: gif.timing)
        )
        return TrimResult(url: outputURL, range: actual, frameCount: frames.count)
    }

    static func saveVideoFrame(source: URL, time: Double) async throws -> FrameResult {
        let asset = AVURLAsset(url: source)
        guard try await !asset.loadTracks(withMediaType: .video).isEmpty else {
            throw AutomationError(.invalidParams, "The file has no video track.")
        }
        let duration = try await asset.load(.duration).seconds
        guard time.isFinite, time >= 0, time < duration else {
            throw AutomationError(.invalidParams, "time must be within the video (0–\(String(format: "%.2f", duration)) s).")
        }

        let generator = AVAssetImageGenerator(asset: asset)
        generator.appliesPreferredTrackTransform = true
        generator.requestedTimeToleranceBefore = .zero
        generator.requestedTimeToleranceAfter = .zero
        let image: CGImage
        let actualTime: CMTime
        do {
            (image, actualTime) = try await generator.image(at: CMTime(seconds: time, preferredTimescale: 600))
        } catch {
            throw AutomationError(.internal, "Could not read the video frame: \(error.localizedDescription)")
        }
        let url = try saveFrameImage(image, source: source)
        return FrameResult(url: url, time: actualTime.seconds, frame: nil)
    }

    static func saveGifFrame(source: URL, time: Double?, frame: Int?) throws -> FrameResult {
        let gif = try loadGif(source)
        let index: Int
        if let frame {
            guard frame >= 0, frame < gif.frames.count else {
                throw AutomationError(.invalidParams, "frame must be between 0 and \(gif.frames.count - 1).")
            }
            index = frame
        } else {
            index = try AutomationMediaRange.gifFrame(at: time ?? 0, timing: gif.timing)
        }
        let url = try saveFrameImage(gif.frames[index], source: source)
        return FrameResult(url: url, time: AutomationMediaRange.gifStartTime(of: index, timing: gif.timing), frame: index)
    }

    private static func loadGif(_ source: URL) throws -> GifCaptureData {
        do {
            return try GifCaptureData(contentsOf: source)
        } catch {
            throw AutomationError(.invalidParams, "The GIF could not be read: \(error.localizedDescription)")
        }
    }

    private static func saveFrameImage(_ image: CGImage, source: URL) throws -> URL {
        let outputURL = SaveService.shared.generateURL(for: .screenshot, stemSuffix: "Frame")
        try ensureNewFile(outputURL, source: source)
        do {
            return try ScreenshotCapture.saveImage(image, to: outputURL)
        } catch {
            try? FileManager.default.removeItem(at: outputURL)
            throw AutomationError(.internal, "Could not save the frame: \(error.localizedDescription)")
        }
    }

    private static func ensureNewFile(_ output: URL, source: URL) throws {
        guard output.standardizedFileURL != source.standardizedFileURL,
              !FileManager.default.fileExists(atPath: output.path)
        else {
            throw AutomationError(.internal, "Refusing to overwrite an existing file.")
        }
    }
}
