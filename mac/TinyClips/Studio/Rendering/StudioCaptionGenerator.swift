import AVFoundation
import Foundation
import WhisperKit

/// Reads a recording's sound as speech recognition wants it: one channel of floats at 16 kHz.
enum StudioCaptionSound {
    static let sampleRate = 16_000.0

    static func read(
        url: URL,
        project: StudioProject,
        progress: @Sendable (Double) -> Void
    ) async throws -> [Float] {
        guard FileManager.default.fileExists(atPath: url.path) else {
            throw StudioCaptionGenerationError.missingRecording
        }

        let asset = AVURLAsset(url: url)
        let tracks: [AVAssetTrack]
        do {
            tracks = try await asset.loadTracks(withMediaType: .audio)
        } catch {
            throw StudioCaptionGenerationError.unreadableSound
        }
        guard !tracks.isEmpty else {
            throw StudioCaptionGenerationError.noSound
        }

        let chosen = StudioSound.speechTrackIndexes(project: project, trackCount: tracks.count).map { tracks[$0] }
        do {
            let reader = try AVAssetReader(asset: asset)
            let output = AVAssetReaderAudioMixOutput(audioTracks: chosen, audioSettings: [
                AVFormatIDKey: kAudioFormatLinearPCM,
                AVSampleRateKey: sampleRate,
                AVNumberOfChannelsKey: 1,
                AVLinearPCMBitDepthKey: 32,
                AVLinearPCMIsFloatKey: true,
                AVLinearPCMIsBigEndianKey: false,
                AVLinearPCMIsNonInterleaved: false,
            ])
            output.alwaysCopiesSampleData = false
            reader.add(output)
            guard reader.startReading() else {
                throw StudioCaptionGenerationError.unreadableSound
            }

            let duration = project.sources.screen.duration
            let expected = duration > 0 ? Int(duration * sampleRate) : 0
            var samples = [Float]()
            samples.reserveCapacity(expected)
            while let buffer = output.copyNextSampleBuffer() {
                try Task.checkCancellation()
                guard let block = CMSampleBufferGetDataBuffer(buffer) else { continue }
                let bytes = CMBlockBufferGetDataLength(block)
                let start = samples.count
                let count = bytes / MemoryLayout<Float>.size
                samples.append(contentsOf: repeatElement(0, count: count))
                let status = samples.withUnsafeMutableBytes { target in
                    CMBlockBufferCopyDataBytes(
                        block,
                        atOffset: 0,
                        dataLength: count * MemoryLayout<Float>.size,
                        destination: target.baseAddress!.advanced(by: start * MemoryLayout<Float>.size)
                    )
                }
                guard status == kCMBlockBufferNoErr else {
                    throw StudioCaptionGenerationError.unreadableSound
                }
                if expected > 0 {
                    progress(min(1, Double(samples.count) / Double(expected)))
                }
            }
            guard reader.status != .failed else {
                throw StudioCaptionGenerationError.unreadableSound
            }
            return samples
        } catch let error as StudioCaptionGenerationError {
            throw error
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            throw StudioCaptionGenerationError.unreadableSound
        }
    }
}

extension StudioCaptionSound {
    /// The samples as a file in the temporary folder, for SpeechAnalyzer to read. The caller
    /// deletes it.
    static func writeTemporaryFile(_ samples: [Float]) throws -> URL {
        guard let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: sampleRate, channels: 1, interleaved: false) else {
            throw StudioCaptionGenerationError.unreadableSound
        }
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("tinyclips-captions-\(UUID().uuidString).wav")
        do {
            let file = try AVAudioFile(forWriting: url, settings: format.settings, commonFormat: .pcmFormatFloat32, interleaved: false)
            let framesAtOnce = 65_536
            guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(framesAtOnce)),
                  let channel = buffer.floatChannelData?[0] else {
                throw StudioCaptionGenerationError.unreadableSound
            }
            var written = 0
            while written < samples.count {
                try Task.checkCancellation()
                let count = min(framesAtOnce, samples.count - written)
                samples.withUnsafeBufferPointer { source in
                    channel.update(from: source.baseAddress!.advanced(by: written), count: count)
                }
                buffer.frameLength = AVAudioFrameCount(count)
                try file.write(from: buffer)
                written += count
            }
            return url
        } catch {
            try? FileManager.default.removeItem(at: url)
            throw error
        }
    }
}

/// Makes captions with WhisperKit, on this computer. Only the model is downloaded.
struct StudioWhisperCaptionGenerator: StudioCaptionGenerating {
    static let shared = StudioWhisperCaptionGenerator()

    let store: StudioCaptionModelStore

    init(store: StudioCaptionModelStore = StudioCaptionModelStore()) {
        self.store = store
    }

    func isModelInstalled(_ model: StudioCaptionModel) -> Bool {
        store.isInstalled(model)
    }

    func generate(
        project: StudioProject,
        paths: StudioProjectPaths,
        model: StudioCaptionModel,
        language: String?,
        progress: @escaping @Sendable (StudioCaptionProgress) -> Void
    ) async throws -> StudioGeneratedCaptions {
        // The sound first: a recording without any should not cost a download.
        let reading = StudioCaptionPercentReports(stage: .readingSound, progress)
        let samples = try await StudioCaptionSound.read(url: paths.screenURL, project: project) { fraction in
            reading.report(fraction)
        }
        guard !samples.isEmpty else {
            throw StudioCaptionGenerationError.noSound
        }
        try Task.checkCancellation()

        let folder = try await modelFolder(for: model, progress: progress)
        try Task.checkCancellation()

        progress(StudioCaptionProgress(stage: .preparingModel, fraction: 0))
        let whisper: WhisperKit
        do {
            whisper = try await WhisperKit(WhisperKitConfig(
                modelFolder: folder.path,
                verbose: false,
                logLevel: .error,
                prewarm: false,
                load: true,
                download: false
            ))
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            throw StudioCaptionGenerationError(message: "The \(model.name) model could not be loaded. It may be damaged, or too large for this computer. Choose a smaller model, or delete the downloaded models in Settings, under Studio, to download it again.")
        }

        var options = DecodingOptions()
        options.verbose = false
        options.task = .transcribe
        options.language = nil
        options.detectLanguage = !model.isEnglishOnly
        options.skipSpecialTokens = true
        options.wordTimestamps = true
        options.chunkingStrategy = .vad

        let duration = project.sources.screen.duration
        progress(StudioCaptionProgress(stage: .recognizingSpeech, fraction: 0))

        // The overall measure, because a recording of more than 30 seconds is heard in pieces and
        // WhisperKit does not say which segments it finds in them while it works.
        let overall = whisper.progress
        let recognizing = StudioCaptionPercentReports(stage: .recognizingSpeech, progress)
        let results: [TranscriptionResult]
        do {
            results = try await whisper.transcribe(
                audioArray: samples,
                decodeOptions: options,
                callback: { _ in
                    recognizing.report(overall.fractionCompleted)
                    return nil
                }
            )
        } catch is CancellationError {
            await whisper.unloadModels()
            throw CancellationError()
        } catch {
            await whisper.unloadModels()
            throw StudioCaptionGenerationError(message: "The speech could not be recognized: \(error.localizedDescription)")
        }
        await whisper.unloadModels()
        try Task.checkCancellation()

        let segments = results
            .flatMap(\.segments)
            .sorted { $0.start < $1.start }
            .map { segment in
                StudioTranscriptSegment(
                    start: Double(segment.start),
                    end: Double(segment.end),
                    text: segment.text,
                    words: (segment.words ?? []).map {
                        StudioCaptionWord(start: Double($0.start), end: Double($0.end), text: $0.word)
                    }
                )
            }
        let cues = StudioCaptionTranscript.cues(from: segments, sourceDuration: duration)
        guard !cues.isEmpty else {
            throw StudioCaptionGenerationError.noSpeech
        }
        return StudioGeneratedCaptions(
            language: results.first?.language,
            cues: StudioCaptionQuality.fix(cues, sourceDuration: duration).cues
        )
    }

    /// The model's folder, downloading it first when it is not here. A download cannot be asked
    /// to stop, so cancelling stops the wait at once and leaves it to finish behind the scenes,
    /// ready for next time.
    private func modelFolder(
        for model: StudioCaptionModel,
        progress: @escaping @Sendable (StudioCaptionProgress) -> Void
    ) async throws -> URL {
        if store.isInstalled(model) {
            return store.folder(for: model)
        }

        progress(StudioCaptionProgress(stage: .downloadingModel, fraction: 0))
        let download = await StudioCaptionDownloads.shared.task(for: model, store: store, progress: progress)
        let wait = StudioCaptionWait<URL>()
        do {
            return try await withTaskCancellationHandler {
                try await withCheckedThrowingContinuation { continuation in
                    wait.begin(continuation)
                    Task {
                        wait.finish(await download.result)
                    }
                }
            } onCancel: {
                wait.finish(.failure(CancellationError()))
            }
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            throw StudioCaptionGenerationError(message: "The \(model.name) model could not be downloaded: \(error.localizedDescription)")
        }
    }
}

/// Passes on how far a stage is once for each percent. Reading the sound and recognizing speech
/// each say so thousands of times, and all of that would otherwise land on the main thread.
final class StudioCaptionPercentReports: @unchecked Sendable {
    private let lock = NSLock()
    private let stage: StudioCaptionProgress.Stage
    private let progress: @Sendable (StudioCaptionProgress) -> Void
    private var lastPercent = 0

    init(stage: StudioCaptionProgress.Stage, _ progress: @escaping @Sendable (StudioCaptionProgress) -> Void) {
        self.stage = stage
        self.progress = progress
    }

    func report(_ fraction: Double) {
        let percent = Int(min(1, max(0, fraction)) * 100)
        lock.lock()
        let isNews = percent > lastPercent
        if isNews {
            lastPercent = percent
        }
        lock.unlock()
        if isNews {
            progress(StudioCaptionProgress(stage: stage, fraction: Double(percent) / 100))
        }
    }
}

/// Two editors that ask for one model wait for one download.
private actor StudioCaptionDownloads {
    static let shared = StudioCaptionDownloads()

    private var running: [String: Task<URL, Error>] = [:]

    func task(
        for model: StudioCaptionModel,
        store: StudioCaptionModelStore,
        progress: @escaping @Sendable (StudioCaptionProgress) -> Void
    ) -> Task<URL, Error> {
        if let task = running[model.id] {
            return task
        }
        let task = Task.detached { () throws -> URL in
            defer {
                Task { await self.finished(model) }
            }
            try FileManager.default.createDirectory(at: store.directory, withIntermediateDirectories: true)
            let folder = try await WhisperKit.download(
                variant: model.variant,
                downloadBase: store.directory,
                progressCallback: { update in
                    progress(StudioCaptionProgress(stage: .downloadingModel, fraction: update.fractionCompleted))
                }
            )
            try store.markInstalled(model)
            return folder
        }
        running[model.id] = task
        return task
    }

    private func finished(_ model: StudioCaptionModel) {
        running[model.id] = nil
    }
}

/// Ends a wait for a download with whichever comes first: the download, or being cancelled.
final class StudioCaptionWait<Value: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Value, Error>?
    private var outcome: Result<Value, Error>?

    func begin(_ continuation: CheckedContinuation<Value, Error>) {
        lock.lock()
        if let outcome {
            lock.unlock()
            continuation.resume(with: outcome)
            return
        }
        self.continuation = continuation
        lock.unlock()
    }

    func finish(_ result: Result<Value, Error>) {
        lock.lock()
        guard outcome == nil else {
            lock.unlock()
            return
        }
        outcome = result
        let waiting = continuation
        continuation = nil
        lock.unlock()
        waiting?.resume(with: result)
    }
}
