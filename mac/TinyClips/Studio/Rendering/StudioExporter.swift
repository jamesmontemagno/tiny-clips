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
        try? FileManager.default.removeItem(at: outputURL)

        guard let exportSession = AVAssetExportSession(
            asset: build.composition,
            presetName: RecordingVideoCodecResolver.exportPreset(for: codec)
        ) else {
            throw Error.exportSessionCreationFailed
        }
        exportSession.outputURL = outputURL
        exportSession.outputFileType = .mp4
        exportSession.videoComposition = build.videoComposition
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
            try? FileManager.default.removeItem(at: outputURL)
            if Task.isCancelled {
                throw CancellationError()
            }
            throw error
        }
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
