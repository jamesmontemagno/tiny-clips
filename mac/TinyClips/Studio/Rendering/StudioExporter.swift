import AVFoundation
import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

// MARK: - Exporter

enum StudioExporter {
    enum Error: LocalizedError {
        case nothingToExport
        case exportSessionCreationFailed
        case posterDestinationCreationFailed
        case posterWriteFailed
        case outputNameTaken(String)

        var errorDescription: String? {
            switch self {
            case .nothingToExport:
                return "There is nothing to export. The trimmed video is empty."
            case .exportSessionCreationFailed:
                return "Could not create the Studio export session."
            case .posterDestinationCreationFailed:
                return "Could not create the Studio poster image destination."
            case .posterWriteFailed:
                return "Could not write the Studio poster image."
            case .outputNameTaken(let name):
                return "Another file was saved as \(name) in the meantime. Export again to give the video another name."
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
        onProgress: ((Double) -> Void)? = nil
    ) async throws -> URL {
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
        guard !FileManager.default.fileExists(atPath: outputURL.path) else {
            throw Error.outputNameTaken(outputURL.lastPathComponent)
        }

        guard let exportSession = AVAssetExportSession(
            asset: build.composition,
            presetName: RecordingVideoCodecResolver.exportPreset(for: codec)
        ) else {
            throw Error.exportSessionCreationFailed
        }
        exportSession.outputURL = outputURL
        exportSession.outputFileType = .mp4
        exportSession.videoComposition = build.videoComposition
        exportSession.audioMix = build.audioMix(for: project)
        exportSession.shouldOptimizeForNetworkUse = true

        // The sequence ends by itself when the export finishes, fails, or is cancelled.
        let progressTask = Task {
            for await state in exportSession.states(updateInterval: 0.1) {
                if case .exporting(let progress) = state {
                    onProgress?(progress.fractionCompleted)
                }
            }
        }

        // An export may be cancelled from any thread; the session is only not marked `Sendable`.
        nonisolated(unsafe) let cancellableSession = exportSession
        do {
            try await withTaskCancellationHandler {
                try await exportSession.export(to: outputURL, as: .mp4)
            } onCancel: {
                cancellableSession.cancelExport()
            }
            progressTask.cancel()
            onProgress?(1)
            return outputURL
        } catch {
            progressTask.cancel()
            exportSession.cancelExport()

            // What this export wrote so far is removed. Not when it never began because a
            // file had taken the name in the last moment: that file is not this export's.
            if !isBecauseTheFileExists(error) {
                try? FileManager.default.removeItem(at: outputURL)
            }
            if Task.isCancelled {
                throw CancellationError()
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
