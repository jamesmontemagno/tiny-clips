import AVFoundation
import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

// MARK: - Exporter

enum StudioExporter {
    enum Error: LocalizedError {
        case nothingToExport
        case readerSetupFailed
        case writerSetupFailed
        case incompleteOutput
        case posterDestinationCreationFailed
        case posterWriteFailed
        case outputNameTaken(String)
        case sizeNotEncodable(width: Int, height: Int)
        case sizeNeedsHEVC(width: Int, height: Int)

        var errorDescription: String? {
            switch self {
            case .nothingToExport:
                return "There is nothing to export. The trimmed video is empty."
            case .readerSetupFailed:
                return "Could not read the Studio project's video to export it."
            case .writerSetupFailed:
                return "Could not create the exported video at this size and quality."
            case .incompleteOutput:
                return "The exported video could not be finished."
            case .posterDestinationCreationFailed:
                return "Could not create the Studio poster image destination."
            case .posterWriteFailed:
                return "Could not write the Studio poster image."
            case .outputNameTaken(let name):
                return "Another file was saved as \(name) in the meantime. Export again to give the video another name."
            case .sizeNotEncodable(let width, let height):
                return "This Mac can't encode a \(width) × \(height) video. Choose 4K or a smaller resolution and export again."
            case .sizeNeedsHEVC(let width, let height):
                return "This Mac can't encode a \(width) × \(height) video as H.264. Choose 4K or a smaller resolution, or set the video codec to H.265 / HEVC in Settings, and export again."
            }
        }
    }

    @discardableResult
    static func export(
        project: StudioProject,
        events: StudioEvents,
        paths: StudioProjectPaths,
        outputURL: URL,
        renderSize: CGSize,
        codec: VideoCodec,
        quality: StudioExportQuality,
        onProgress: (@Sendable (Double) -> Void)? = nil,
        destination: StudioExportDestination? = nil
    ) async throws -> URL {
        let replacementDirectory: URL?
        if destination != nil {
            replacementDirectory = try FileManager.default.url(
                for: .itemReplacementDirectory, in: .userDomainMask, appropriateFor: outputURL, create: true)
        } else {
            replacementDirectory = nil
        }
        let renderURL = replacementDirectory?.appendingPathComponent("export.mp4") ?? outputURL
        defer {
            if let replacementDirectory {
                do { try FileManager.default.removeItem(at: replacementDirectory) }
                catch { NSLog("Studio could not remove export staging folder: %@", error.localizedDescription) }
            }
        }
        let build = try await StudioCompositionBuilder.build(
            project: project,
            events: events,
            paths: paths,
            renderSize: renderSize
        )
        guard build.duration > .zero else {
            throw Error.nothingToExport
        }

        // The name was free when it was made for this video, a moment ago. A file that has it
        // now is something else that was saved since, and not this export's to remove.
        guard !FileManager.default.fileExists(atPath: renderURL.path) else {
            throw Error.outputNameTaken(outputURL.lastPathComponent)
        }

        let codec = try await StudioExportWriteJob.codec(
            renderSize: renderSize,
            framesPerSecond: project.sources.screen.frameRate,
            preferred: codec,
            quality: quality
        )

        // An export session has no setting for its bitrate, so the frames and the sound are read
        // and written here. The one sound track an export gets comes from the mix output either
        // way, as `exportAudioMix` describes.
        let job = try StudioExportWriteJob(
            build: build,
            audioMix: build.exportAudioMix(for: project),
            outputURL: renderURL,
            renderSize: renderSize,
            framesPerSecond: project.sources.screen.frameRate,
            codec: codec,
            quality: quality,
            onProgress: onProgress
        )
        do {
            try await withTaskCancellationHandler {
                try await job.run()
                try Task.checkCancellation()
                if let destination { try destination.commit(staged: renderURL) }
            } onCancel: {
                job.cancel()
            }
            onProgress?(1)
            return outputURL
        } catch {
            // What this export wrote so far is removed. Not when it never began because a
            // file had taken the name in the last moment: that file is not this export's.
            if !isBecauseTheFileExists(error) {
                try? FileManager.default.removeItem(at: renderURL)
            }
            if Task.isCancelled {
                throw CancellationError()
            }
            // An encoder that gives up past the 4K frame says only that something went wrong.
            let nsError = error as NSError
            if !StudioExportPresets.fitsFourK(width: renderSize.width, height: renderSize.height),
               nsError.domain == AVFoundationErrorDomain, nsError.code == AVError.Code.unknown.rawValue {
                throw Error.sizeNotEncodable(width: Int(renderSize.width), height: Int(renderSize.height))
            }
            throw error
        }
    }

    /// Whether an export failed because a file already had its name.
    private static func isBecauseTheFileExists(_ error: any Swift.Error) -> Bool {
        let error = error as NSError
        return (error.domain == AVFoundationErrorDomain && error.code == AVError.Code.fileAlreadyExists.rawValue)
            || (error.domain == NSCocoaErrorDomain && error.code == CocoaError.Code.fileWriteFileExists.rawValue)
    }

    static func writePoster(
        project: StudioProject,
        events: StudioEvents,
        paths: StudioProjectPaths,
        posterURL: URL,
        renderSize: CGSize,
        time: Double = 0
    ) async throws {
        let build = try await StudioCompositionBuilder.build(
            project: project,
            events: events,
            paths: paths,
            renderSize: renderSize
        )
        let generator = AVAssetImageGenerator(asset: build.composition)
        generator.videoComposition = build.videoComposition
        generator.requestedTimeToleranceBefore = .zero
        generator.requestedTimeToleranceAfter = .zero

        guard build.duration > .zero else {
            throw Error.nothingToExport
        }
        // Stay a little inside the end, where there is still a frame to draw.
        let lastFrameTime = max(0, build.duration.seconds - 0.05)
        let requestedTime = CMTime(seconds: max(0, min(time, lastFrameTime)), preferredTimescale: 600)
        let image = try await cgImage(from: generator, at: requestedTime)
        try? FileManager.default.removeItem(at: posterURL)
        guard let destination = CGImageDestinationCreateWithURL(
            posterURL as CFURL,
            UTType.jpeg.identifier as CFString,
            1,
            nil
        ) else {
            throw Error.posterDestinationCreationFailed
        }
        CGImageDestinationAddImage(destination, image, [
            kCGImageDestinationLossyCompressionQuality: 0.86,
        ] as CFDictionary)
        guard CGImageDestinationFinalize(destination) else {
            throw Error.posterWriteFailed
        }
    }

    private static func cgImage(from generator: AVAssetImageGenerator, at time: CMTime) async throws -> CGImage {
        try await withCheckedThrowingContinuation { continuation in
            generator.generateCGImageAsynchronously(for: time) { image, _, error in
                if let image {
                    continuation.resume(returning: image)
                } else {
                    continuation.resume(throwing: error ?? Error.posterWriteFailed)
                }
            }
        }
    }
}
