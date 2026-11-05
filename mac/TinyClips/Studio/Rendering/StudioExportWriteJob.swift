import AVFoundation
import Foundation
import VideoToolbox

// MARK: - Write Job

/// Reads a composition's frames and sound and writes them to an MP4 at a chosen bitrate. The
/// reader, the writer, and the streams are used only on the job's queue until `run` returns, and
/// `cancel` touches only the reader, which may be told to stop from any thread.
final class StudioExportWriteJob: @unchecked Sendable {
    private struct Stream {
        let output: AVAssetReaderOutput
        let input: AVAssetWriterInput
        let isVideo: Bool
    }

    private let reader: AVAssetReader
    private let writer: AVAssetWriter
    private let streams: [Stream]
    private let duration: CMTime
    private let onProgress: (@Sendable (Double) -> Void)?

    init(
        build: StudioCompositionBuildResult,
        audioMix: AVAudioMix?,
        outputURL: URL,
        renderSize: CGSize,
        framesPerSecond: Double,
        codec: VideoCodec,
        quality: StudioExportQuality,
        onProgress: (@Sendable (Double) -> Void)?
    ) throws {
        duration = build.duration
        self.onProgress = onProgress

        reader = try AVAssetReader(asset: build.composition)
        reader.timeRange = CMTimeRange(start: .zero, duration: build.duration)
        writer = try AVAssetWriter(url: outputURL, fileType: .mp4)
        writer.shouldOptimizeForNetworkUse = true

        let videoOutput = AVAssetReaderVideoCompositionOutput(
            videoTracks: build.composition.tracks(withMediaType: .video),
            videoSettings: [kCVPixelBufferPixelFormatTypeKey as String: Int(kCVPixelFormatType_32BGRA)]
        )
        videoOutput.videoComposition = build.videoComposition
        videoOutput.alwaysCopiesSampleData = false
        guard reader.canAdd(videoOutput) else { throw StudioExporter.Error.readerSetupFailed }
        reader.add(videoOutput)

        let videoInput = try StudioExportWriteJob.makeVideoInput(
            writer: writer,
            renderSize: renderSize,
            framesPerSecond: framesPerSecond,
            codec: codec,
            quality: quality
        )
        writer.add(videoInput)
        var streams = [Stream(output: videoOutput, input: videoInput, isVideo: true)]

        let audioTracks = build.composition.tracks(withMediaType: .audio)
        if !audioTracks.isEmpty {
            let audioOutput = AVAssetReaderAudioMixOutput(audioTracks: audioTracks, audioSettings: [
                AVFormatIDKey: kAudioFormatLinearPCM,
                AVSampleRateKey: 48000,
                AVNumberOfChannelsKey: 2,
                AVLinearPCMBitDepthKey: 32,
                AVLinearPCMIsFloatKey: true,
                AVLinearPCMIsNonInterleaved: false,
                AVLinearPCMIsBigEndianKey: false,
            ])
            audioOutput.audioMix = audioMix
            audioOutput.alwaysCopiesSampleData = false
            let audioInput = AVAssetWriterInput(mediaType: .audio, outputSettings: [
                AVFormatIDKey: kAudioFormatMPEG4AAC,
                AVSampleRateKey: 48000,
                AVNumberOfChannelsKey: 2,
                AVEncoderBitRateKey: 192000,
            ])
            audioInput.expectsMediaDataInRealTime = false
            guard reader.canAdd(audioOutput) else { throw StudioExporter.Error.readerSetupFailed }
            guard writer.canAdd(audioInput) else { throw StudioExporter.Error.writerSetupFailed }
            reader.add(audioOutput)
            writer.add(audioInput)
            streams.append(Stream(output: audioOutput, input: audioInput, isVideo: false))
        }
        self.streams = streams
    }

    /// Whatever is being read comes to an end, and `run` throws.
    func cancel() {
        reader.cancelReading()
    }

    func run() async throws {
        guard reader.startReading() else {
            throw reader.error ?? StudioExporter.Error.readerSetupFailed
        }
        guard writer.startWriting() else {
            reader.cancelReading()
            throw writer.error ?? StudioExporter.Error.writerSetupFailed
        }
        writer.startSession(atSourceTime: .zero)

        do {
            try await copySamples()
        } catch {
            reader.cancelReading()
            writer.cancelWriting()
            throw error
        }

        writer.endSession(atSourceTime: duration)
        await writer.finishWriting()
        guard writer.status == .completed else {
            throw writer.error ?? StudioExporter.Error.incompleteOutput
        }
    }

    /// Moves every sample to its writer input. The writer takes the streams interleaved, so each
    /// input is fed whenever it is ready and none is waited for.
    private func copySamples() async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Swift.Error>) in
            DispatchQueue.global(qos: .userInitiated).async {
                do {
                    try self.copyAllSamples()
                    continuation.resume()
                } catch {
                    continuation.resume(throwing: error)
                }
            }
        }
    }

    private func copyAllSamples() throws {
        var finished = Array(repeating: false, count: streams.count)
        var reported = -1.0
        let seconds = max(duration.seconds, .leastNonzeroMagnitude)
        while finished.contains(false) {
            guard writer.status == .writing else {
                throw writer.error ?? StudioExporter.Error.incompleteOutput
            }
            var didAppend = false
            for (index, stream) in streams.enumerated() where !finished[index] && stream.input.isReadyForMoreMediaData {
                guard let sample = stream.output.copyNextSampleBuffer() else {
                    stream.input.markAsFinished()
                    finished[index] = true
                    continue
                }
                guard stream.input.append(sample) else {
                    throw writer.error ?? StudioExporter.Error.incompleteOutput
                }
                didAppend = true
                if stream.isVideo {
                    let progress = min(max(CMSampleBufferGetPresentationTimeStamp(sample).seconds / seconds, 0), 0.99)
                    if progress - reported >= 0.005 {
                        reported = progress
                        onProgress?(progress)
                    }
                }
            }
            if !didAppend {
                Thread.sleep(forTimeInterval: 0.002)
            }
        }

        // A reader that stops early also returns nil, so the end is only good if it completed.
        switch reader.status {
        case .completed:
            return
        case .cancelled:
            throw CancellationError()
        default:
            throw reader.error ?? StudioExporter.Error.incompleteOutput
        }
    }

    private static func makeVideoInput(
        writer: AVAssetWriter,
        renderSize: CGSize,
        framesPerSecond: Double,
        codec: VideoCodec,
        quality: StudioExportQuality
    ) throws -> AVAssetWriterInput {
        // Where the hardware or the system has no HEVC encoder, the video is H.264 at H.264's bitrate.
        var settings = videoSettings(renderSize: renderSize, framesPerSecond: framesPerSecond, codec: codec, quality: quality)
        if codec == .hevc, !writer.canApply(outputSettings: settings, forMediaType: .video) {
            settings = videoSettings(renderSize: renderSize, framesPerSecond: framesPerSecond, codec: .h264, quality: quality)
        }
        guard writer.canApply(outputSettings: settings, forMediaType: .video) else {
            throw StudioExporter.Error.writerSetupFailed
        }
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: settings)
        input.expectsMediaDataInRealTime = false
        guard writer.canAdd(input) else { throw StudioExporter.Error.writerSetupFailed }
        return input
    }

    private static func videoSettings(
        renderSize: CGSize,
        framesPerSecond: Double,
        codec: VideoCodec,
        quality: StudioExportQuality
    ) -> [String: Any] {
        let bitrate = StudioExportPresets.videoBitrate(
            width: Double(renderSize.width),
            height: Double(renderSize.height),
            framesPerSecond: framesPerSecond,
            isHEVC: codec == .hevc,
            quality: quality
        )
        return [
            AVVideoCodecKey: codec == .hevc ? AVVideoCodecType.hevc : AVVideoCodecType.h264,
            AVVideoWidthKey: Int(renderSize.width),
            AVVideoHeightKey: Int(renderSize.height),
            // The compositor works on encoded values without color management, so what it draws
            // is what a video of ordinary color is expected to hold.
            AVVideoColorPropertiesKey: [
                AVVideoColorPrimariesKey: AVVideoColorPrimaries_ITU_R_709_2,
                AVVideoTransferFunctionKey: AVVideoTransferFunction_ITU_R_709_2,
                AVVideoYCbCrMatrixKey: AVVideoYCbCrMatrix_ITU_R_709_2,
            ],
            AVVideoCompressionPropertiesKey: [
                AVVideoAverageBitRateKey: bitrate,
                AVVideoMaxKeyFrameIntervalDurationKey: 2,
                AVVideoProfileLevelKey: codec == .hevc
                    ? kVTProfileLevel_HEVC_Main_AutoLevel as String
                    : AVVideoProfileLevelH264HighAutoLevel,
            ] as [String: Any],
        ]
    }
}
