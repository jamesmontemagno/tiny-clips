import AVFoundation
import Foundation
import Speech

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

/// What the speech recognition built into macOS can do on this Mac. It is what makes captions, on
/// macOS 26 and later. Apple says whether a Mac has it depends on the hardware too, without listing
/// which. A Mac without it can do everything else with captions.
enum StudioAppleSpeech {
    /// What agents call it, in the place of the Whisper models Windows has.
    static let modelID = "AppleSpeech"

    static let isAvailable: Bool = {
        guard #available(macOS 26.0, *) else { return false }
        return SpeechTranscriber.isAvailable
    }()

    /// The languages it can listen for, by name. Empty on a Mac without it.
    static func languages() async -> [StudioCaptionLanguage] {
        guard #available(macOS 26.0, *), SpeechTranscriber.isAvailable else { return [] }
        let installed = Set(await SpeechTranscriber.installedLocales.map { $0.identifier(.bcp47) })
        return await SpeechTranscriber.supportedLocales
            .map { locale in
                let id = locale.identifier(.bcp47)
                return StudioCaptionLanguage(
                    id: id,
                    name: Locale.current.localizedString(forIdentifier: locale.identifier) ?? id,
                    isInstalled: installed.contains(id)
                )
            }
            .sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    /// The language it would listen for when asked for `id`, or for the system's with nil. It says
    /// nil when it cannot. Asked on a Mac without it, the system answers as if it could, so
    /// that is checked first.
    static func language(equivalentTo id: String?) async -> String? {
        guard #available(macOS 26.0, *), SpeechTranscriber.isAvailable else { return nil }
        let wanted = id.map { Locale(identifier: $0) } ?? Locale.current
        return await SpeechTranscriber.supportedLocale(equivalentTo: wanted)?.identifier(.bcp47)
    }
}

/// Turns what SpeechAnalyzer's modules report into stretches of speech. Pure, so that what they
/// look like, as read from a real run, can be tested without a model.
@available(macOS 26.0, *)
enum StudioAppleSpeechResults {
    /// A real result's runs look like `I `, `believe `, and `out of `: each has its own time, it
    /// ends with its space, and it can be more than one word. A run with no time cannot be a word
    /// of its own, so it joins the one before it, and so does punctuation with no space before it.
    /// Words in languages without spaces stay apart, so that a long result can be broken up.
    static func segment(text: AttributedString, range: CMTimeRange) -> StudioTranscriptSegment? {
        let whole = String(text.characters)
        guard whole.contains(where: { !$0.isWhitespace }) else { return nil }

        var words: [StudioCaptionWord] = []
        var afterSpace = true
        for run in text.runs {
            let piece = String(text[run.range].characters)
            guard let first = piece.first else { continue }
            let joinsLast = !words.isEmpty && !afterSpace && !first.isWhitespace && !first.isLetter && !first.isNumber
            let time = run.audioTimeRange
            if joinsLast || (time == nil && !words.isEmpty) {
                words[words.count - 1].text += piece
                if let time, time.end.isNumeric {
                    words[words.count - 1].end = max(words[words.count - 1].end, time.end.seconds)
                }
            } else if let time, time.start.isNumeric, time.end.isNumeric {
                words.append(StudioCaptionWord(start: time.start.seconds, end: time.end.seconds, text: piece))
            } else if !piece.allSatisfy(\.isWhitespace) {
                // Speech before anything has a time: there are no words to be had here.
                words = []
                break
            }
            afterSpace = piece.last?.isWhitespace == true
        }

        return StudioTranscriptSegment(
            start: range.start.seconds,
            end: range.end.seconds,
            text: whole,
            words: words
        )
    }
}

/// Listens to a recording with a SpeechAnalyzer module. Apart from the module, so that the same
/// steps can be run with another one on a machine where `SpeechTranscriber` is not available.
@available(macOS 26.0, *)
enum StudioSpeechAnalysis {
    static func segments<Results: AsyncSequence & Sendable>(
        module: any SpeechModule,
        results: Results,
        text: @escaping @Sendable (Results.Element) -> AttributedString,
        locale: Locale,
        samples: [Float],
        duration: Double,
        progress: @escaping @Sendable (StudioCaptionProgress) -> Void
    ) async throws -> [StudioTranscriptSegment] where Results.Element: SpeechModuleResult {
        try await install(module, locale: locale, progress: progress)
        try Task.checkCancellation()

        let sound = try StudioCaptionSound.writeTemporaryFile(samples)
        defer { try? FileManager.default.removeItem(at: sound) }

        progress(StudioCaptionProgress(stage: .recognizingSpeech, fraction: 0))
        let reports = StudioCaptionPercentReports(stage: .recognizingSpeech, progress)
        let analyzer = SpeechAnalyzer(modules: [module])
        let collecting = Task { () -> [StudioTranscriptSegment] in
            var segments: [StudioTranscriptSegment] = []
            for try await result in results {
                if duration > 0 {
                    reports.report(result.range.end.seconds / duration)
                }
                if result.isFinal, let segment = StudioAppleSpeechResults.segment(text: text(result), range: result.range) {
                    segments.append(segment)
                }
            }
            return segments
        }

        return try await withTaskCancellationHandler {
            do {
                // SpeechAnalyzer converts what it reads from a file, but not what it is handed
                // as buffers, and the helper for those needs macOS 27.
                let file = try AVAudioFile(forReading: sound)
                if let last = try await analyzer.analyzeSequence(from: file) {
                    try Task.checkCancellation()
                    try await analyzer.finalizeAndFinish(through: last)
                } else {
                    await analyzer.cancelAndFinishNow()
                }
                return try await collecting.value
            } catch {
                await analyzer.cancelAndFinishNow()
                collecting.cancel()
                if Task.isCancelled {
                    throw CancellationError()
                }
                throw error
            }
        } onCancel: {
            Task { await analyzer.cancelAndFinishNow() }
        }
    }

    /// Has macOS get what the module needs. macOS keeps it and shares it between apps, so a language
    /// is usually only downloaded once, and it takes care of the download itself. That cannot be
    /// asked to stop, so cancelling ends the wait and leaves it to finish.
    private static func install(
        _ module: any SpeechModule,
        locale: Locale,
        progress: @escaping @Sendable (StudioCaptionProgress) -> Void
    ) async throws {
        guard await AssetInventory.status(forModules: [module]) != .installed else { return }

        let reserved = await AssetInventory.reservedLocales
        if let release = StudioCaptionLanguages.reservationToRelease(
            reserved: reserved.map { $0.identifier(.bcp47) },
            wanted: locale.identifier(.bcp47),
            maximum: AssetInventory.maximumReservedLocales
        ), let other = reserved.first(where: { $0.identifier(.bcp47) == release }) {
            await AssetInventory.release(reservedLocale: other)
        }

        guard let request = try await AssetInventory.assetInstallationRequest(supporting: [module]) else { return }
        progress(StudioCaptionProgress(stage: .downloadingLanguage, fraction: 0))
        let reports = StudioCaptionPercentReports(stage: .downloadingLanguage, progress)
        let watching = request.progress.observe(\.fractionCompleted) { measure, _ in
            reports.report(measure.fractionCompleted)
        }
        defer { watching.invalidate() }

        let wait = StudioCaptionWait<Void>()
        let installing = StudioInstallRequest(request: request)
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                wait.begin(continuation)
                Task.detached {
                    do {
                        try await installing.request.downloadAndInstall()
                        wait.finish(.success(()))
                    } catch {
                        wait.finish(.failure(error))
                    }
                }
            }
        } onCancel: {
            wait.finish(.failure(CancellationError()))
        }
    }
}

/// Lets an install request go to the task that waits for it. Nothing else uses it meanwhile.
@available(macOS 26.0, *)
private struct StudioInstallRequest: @unchecked Sendable {
    let request: AssetInstallationRequest
}

/// Makes captions with the speech recognition built into macOS, on this Mac. Sound never leaves it.
struct StudioAppleSpeechCaptionGenerator: StudioCaptionGenerating {
    func generate(
        project: StudioProject,
        paths: StudioProjectPaths,
        language: String?,
        progress: @escaping @Sendable (StudioCaptionProgress) -> Void
    ) async throws -> StudioGeneratedCaptions {
        guard #available(macOS 26.0, *), SpeechTranscriber.isAvailable else {
            throw StudioCaptionGenerationError(message: StudioCaptionText.needsNewerMac)
        }

        let wanted = language.map { Locale(identifier: $0) } ?? Locale.current
        guard let locale = await SpeechTranscriber.supportedLocale(equivalentTo: wanted) else {
            let name = Locale.current.localizedString(forIdentifier: wanted.identifier) ?? wanted.identifier
            throw StudioCaptionGenerationError(message: "Apple Speech cannot listen for \(name) on this Mac. Choose another language.")
        }

        // The sound first: a recording without any should not cost a download.
        let reading = StudioCaptionPercentReports(stage: .readingSound, progress)
        let samples = try await StudioCaptionSound.read(url: paths.screenURL, project: project) { fraction in
            reading.report(fraction)
        }
        guard !samples.isEmpty else {
            throw StudioCaptionGenerationError.noSound
        }
        try Task.checkCancellation()

        let duration = project.sources.screen.duration

        // Results that are not final yet only move the progress bar along.
        let transcriber = SpeechTranscriber(
            locale: locale,
            transcriptionOptions: [],
            reportingOptions: [.volatileResults],
            attributeOptions: [.audioTimeRange]
        )
        let segments: [StudioTranscriptSegment]
        do {
            segments = try await StudioSpeechAnalysis.segments(
                module: transcriber,
                results: transcriber.results,
                text: { $0.text },
                locale: locale,
                samples: samples,
                duration: duration,
                progress: progress
            )
        } catch is CancellationError {
            throw CancellationError()
        } catch let error as StudioCaptionGenerationError {
            throw error
        } catch {
            throw StudioCaptionGenerationError(message: "The speech could not be recognized: \(error.localizedDescription)")
        }

        let cues = StudioCaptionTranscript.cues(from: segments, sourceDuration: duration)
        guard !cues.isEmpty else {
            throw StudioCaptionGenerationError.noSpeech
        }
        return StudioGeneratedCaptions(
            language: locale.identifier(.bcp47),
            cues: StudioCaptionQuality.fix(cues, sourceDuration: duration).cues
        )
    }
}
