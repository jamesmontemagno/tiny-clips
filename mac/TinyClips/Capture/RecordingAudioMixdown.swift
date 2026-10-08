import AVFoundation
import Foundation

/// A recording made with system audio and the microphone is written with one audio track for
/// each. Many players play only the first, so a saved video gets them mixed into a single track.
enum RecordingAudioMixdown {
    enum Outcome: Equatable {
        /// There was nothing to mix: the file has at most one audio track, or could not be read.
        case notNeeded
        /// The file now has a single audio track holding every sound.
        case mixed
        /// The file could not be rewritten and is unchanged.
        case failed
    }

    /// Whether this many audio tracks have to be mixed to end up with one.
    static func needsMix(audioTrackCount: Int) -> Bool {
        audioTrackCount > 1
    }

    /// The mix an export of `composition` needs so that it writes one audio track, or nil when
    /// the composition has no more than one and can be exported as it is.
    static func audioMix(for composition: AVComposition) -> AVAudioMix? {
        audioMix(for: composition.tracks(withMediaType: .audio))
    }

    /// Every track at the volume it was recorded at, or nil when there is nothing to mix.
    static func audioMix(for audioTracks: [AVAssetTrack]) -> AVAudioMix? {
        guard needsMix(audioTrackCount: audioTracks.count) else { return nil }
        let mix = AVMutableAudioMix()
        mix.inputParameters = audioTracks.map { track in
            let parameters = AVMutableAudioMixInputParameters(track: track)
            parameters.setVolume(1, at: .zero)
            return parameters
        }
        return mix
    }

    /// Rewrites the video at `url` with its audio tracks mixed into one, when it has more than
    /// one. The picture is copied as it is, not encoded again. The new file is written elsewhere
    /// and takes the place of the original only once it is complete, so a failure leaves the
    /// original as it was.
    @discardableResult
    static func mixDownIfNeeded(at url: URL) async -> Outcome {
        let asset = AVURLAsset(url: url)
        guard let audioTracks = try? await asset.loadTracks(withMediaType: .audio),
              needsMix(audioTrackCount: audioTracks.count) else {
            return .notNeeded
        }

        let replacement = makeReplacementURL(for: url)
        defer { try? FileManager.default.removeItem(at: replacement.cleanupURL) }

        do {
            try await writeMixedCopy(of: asset, audioTracks: audioTracks, to: replacement.fileURL)
            try await verifyMixedCopy(at: replacement.fileURL, against: asset)
            _ = try FileManager.default.replaceItemAt(url, withItemAt: replacement.fileURL)
            return .mixed
        } catch {
            NSLog("TinyClips could not mix the recording's audio tracks into one: \(error.localizedDescription)")
            return .failed
        }
    }

    // MARK: - Private helpers

    private enum MixdownError: LocalizedError {
        case unsupportedSource
        case readerSetupFailed
        case writerSetupFailed
        case incompleteOutput

        var errorDescription: String? {
            switch self {
            case .unsupportedSource:
                return "The recording's video track could not be copied."
            case .readerSetupFailed:
                return "The recording could not be opened for reading."
            case .writerSetupFailed:
                return "The mixed copy could not be opened for writing."
            case .incompleteOutput:
                return "The mixed copy did not match the recording."
            }
        }
    }

    /// Where the mixed copy is written, and what to delete afterwards. A directory made for
    /// replacing `url` is on the same volume, so the copy can be moved into place.
    private static func makeReplacementURL(for url: URL) -> (fileURL: URL, cleanupURL: URL) {
        let fileName = "TinyClips-\(UUID().uuidString).mp4"
        if let directory = try? FileManager.default.url(
            for: .itemReplacementDirectory,
            in: .userDomainMask,
            appropriateFor: url,
            create: true
        ) {
            return (directory.appendingPathComponent(fileName), directory)
        }
        let fileURL = FileManager.default.temporaryDirectory.appendingPathComponent(fileName)
        return (fileURL, fileURL)
    }

    private static func writeMixedCopy(of asset: AVAsset, audioTracks: [AVAssetTrack], to outputURL: URL) async throws {
        let videoTracks = try await asset.loadTracks(withMediaType: .video)
        guard videoTracks.count == 1, let videoTrack = videoTracks.first else {
            throw MixdownError.unsupportedSource
        }
        let formatDescriptions = try await videoTrack.load(.formatDescriptions)
        guard formatDescriptions.count == 1, let videoFormat = formatDescriptions.first else {
            throw MixdownError.unsupportedSource
        }
        let preferredTransform = try await videoTrack.load(.preferredTransform)
        let videoTimeScale = try await videoTrack.load(.naturalTimeScale)

        let reader = try AVAssetReader(asset: asset)
        let videoOutput = AVAssetReaderTrackOutput(track: videoTrack, outputSettings: nil)
        videoOutput.alwaysCopiesSampleData = false
        let audioOutput = AVAssetReaderAudioMixOutput(audioTracks: audioTracks, audioSettings: [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: 48000,
            AVNumberOfChannelsKey: 2,
            AVLinearPCMBitDepthKey: 32,
            AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsNonInterleaved: false,
            AVLinearPCMIsBigEndianKey: false,
        ])
        audioOutput.audioMix = audioMix(for: audioTracks)
        audioOutput.alwaysCopiesSampleData = false
        guard reader.canAdd(videoOutput), reader.canAdd(audioOutput) else {
            throw MixdownError.readerSetupFailed
        }
        reader.add(videoOutput)
        reader.add(audioOutput)

        let writer = try AVAssetWriter(url: outputURL, fileType: .mp4)
        writer.shouldOptimizeForNetworkUse = true
        let videoInput = AVAssetWriterInput(mediaType: .video, outputSettings: nil, sourceFormatHint: videoFormat)
        videoInput.transform = preferredTransform
        videoInput.mediaTimeScale = videoTimeScale
        videoInput.expectsMediaDataInRealTime = false
        let audioInput = AVAssetWriterInput(mediaType: .audio, outputSettings: [
            AVFormatIDKey: kAudioFormatMPEG4AAC,
            AVSampleRateKey: 48000,
            AVNumberOfChannelsKey: 2,
            AVEncoderBitRateKey: 256000,
        ])
        audioInput.expectsMediaDataInRealTime = false
        guard writer.canAdd(videoInput), writer.canAdd(audioInput) else {
            throw MixdownError.writerSetupFailed
        }
        writer.add(videoInput)
        writer.add(audioInput)

        guard reader.startReading() else {
            throw reader.error ?? MixdownError.readerSetupFailed
        }
        guard writer.startWriting() else {
            reader.cancelReading()
            throw writer.error ?? MixdownError.writerSetupFailed
        }
        writer.startSession(atSourceTime: .zero)

        do {
            try await copySamples(
                reader: reader,
                writer: writer,
                streams: [(videoOutput, videoInput), (audioOutput, audioInput)]
            )
        } catch {
            reader.cancelReading()
            writer.cancelWriting()
            throw error
        }

        await writer.finishWriting()
        guard writer.status == .completed else {
            throw writer.error ?? MixdownError.incompleteOutput
        }
    }

    /// Moves every sample from each reader output to its writer input. The writer takes the
    /// streams interleaved, so each input is fed whenever it is ready and none is waited for.
    private static func copySamples(
        reader: AVAssetReader,
        writer: AVAssetWriter,
        streams: [(output: AVAssetReaderOutput, input: AVAssetWriterInput)]
    ) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            DispatchQueue.global(qos: .userInitiated).async {
                var finished = Array(repeating: false, count: streams.count)
                while finished.contains(false) {
                    guard writer.status == .writing else {
                        continuation.resume(throwing: writer.error ?? MixdownError.incompleteOutput)
                        return
                    }
                    var didAppend = false
                    for (index, stream) in streams.enumerated() where !finished[index] && stream.input.isReadyForMoreMediaData {
                        if let sampleBuffer = stream.output.copyNextSampleBuffer() {
                            guard stream.input.append(sampleBuffer) else {
                                continuation.resume(throwing: writer.error ?? MixdownError.incompleteOutput)
                                return
                            }
                            didAppend = true
                        } else {
                            stream.input.markAsFinished()
                            finished[index] = true
                        }
                    }
                    if !didAppend {
                        Thread.sleep(forTimeInterval: 0.002)
                    }
                }
                // A reader that stops early also returns nil, so the end is only good if it completed.
                if reader.status == .completed {
                    continuation.resume()
                } else {
                    continuation.resume(throwing: reader.error ?? MixdownError.incompleteOutput)
                }
            }
        }
    }

    private static func verifyMixedCopy(at url: URL, against source: AVAsset) async throws {
        let copy = AVURLAsset(url: url)
        let audioTrackCount = try await copy.loadTracks(withMediaType: .audio).count
        let videoTrackCount = try await copy.loadTracks(withMediaType: .video).count
        let copyDuration = try await copy.load(.duration)
        let sourceDuration = try await source.load(.duration)
        guard audioTrackCount == 1,
              videoTrackCount == 1,
              abs(copyDuration.seconds - sourceDuration.seconds) <= 0.25 else {
            throw MixdownError.incompleteOutput
        }
    }
}
