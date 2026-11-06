import AVFoundation
import Foundation
import VideoToolbox

// MARK: - Write Job

/// Reads a composition's frames and sound and writes them to an MP4 at a chosen bitrate. The
/// reader, the writer, and the streams are used only on the job's queue until `run` returns.
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
    /// The codec the video is written in: the one asked for, or H.264 where this Mac has no HEVC encoder.
    let codec: VideoCodec
    private let cancellationLock = NSLock()
    private var isCancelledFlag = false

    private var isCancelled: Bool {
        cancellationLock.lock()
        defer { cancellationLock.unlock() }
        return isCancelledFlag
    }

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

        let (videoInput, writtenCodec) = try StudioExportWriteJob.makeVideoInput(
            writer: writer,
            renderSize: renderSize,
            framesPerSecond: framesPerSecond,
            codec: codec,
            quality: quality
        )
        self.codec = writtenCodec
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

    /// Asks `run` to stop, from any thread. The reader is not told from here: asked for a sample
    /// once it has been cancelled, as the copy loop could be at this very moment, it raises an
    /// exception that stops the app. The loop looks at this between samples and stops it itself.
    func cancel() {
        cancellationLock.lock()
        isCancelledFlag = true
        cancellationLock.unlock()
    }

    func run() async throws {
        guard !isCancelled else { throw CancellationError() }
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
        reading: while finished.contains(false) {
            guard !isCancelled else { throw CancellationError() }
            guard writer.status == .writing else {
                throw writer.error ?? StudioExporter.Error.incompleteOutput
            }
            var didAppend = false
            for (index, stream) in streams.enumerated() where !finished[index] && stream.input.isReadyForMoreMediaData {
                // A reader that has failed or completed raises an exception when asked for more.
                guard reader.status == .reading else { break reading }
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
        guard reader.status == .completed else {
            throw reader.error ?? StudioExporter.Error.incompleteOutput
        }
        for (index, stream) in streams.enumerated() where !finished[index] {
            stream.input.markAsFinished()
        }
    }

    /// The codec to export a video of this size in. Within the 4K frame it is the one asked for.
    /// Past it, H.264 and HEVC each stop at sizes that depend on the Mac, and the writer accepts
    /// a size its encoder then refuses at the first frame, after the export has begun, so one
    /// frame is written first to find out.
    static func codec(
        renderSize: CGSize,
        framesPerSecond: Double,
        preferred: VideoCodec,
        quality: StudioExportQuality
    ) async throws -> VideoCodec {
        guard !StudioExportPresets.fitsFourK(width: renderSize.width, height: renderSize.height) else { return preferred }
        func encodes(_ codec: VideoCodec) async -> Bool {
            await canEncodeOneFrame(
                settings: videoSettings(renderSize: renderSize, framesPerSecond: framesPerSecond, codec: codec, quality: quality),
                renderSize: renderSize
            )
        }
        if await encodes(preferred) { return preferred }
        let width = Int(renderSize.width)
        let height = Int(renderSize.height)
        switch preferred {
        case .hevc:
            // As where there is no HEVC encoder at all, the video is H.264 instead.
            if await encodes(.h264) { return .h264 }
        case .h264:
            if await encodes(.hevc) { throw StudioExporter.Error.sizeNeedsHEVC(width: width, height: height) }
        }
        throw StudioExporter.Error.sizeNotEncodable(width: width, height: height)
    }

    private static func canEncodeOneFrame(settings: [String: Any], renderSize: CGSize) async -> Bool {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("TinyClips-encoder-check-\(UUID().uuidString).mp4")
        defer { try? FileManager.default.removeItem(at: url) }
        guard let writer = try? AVAssetWriter(url: url, fileType: .mp4),
              writer.canApply(outputSettings: settings, forMediaType: .video) else { return false }
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: settings)
        input.expectsMediaDataInRealTime = false
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: Int(kCVPixelFormatType_32BGRA),
            kCVPixelBufferWidthKey as String: Int(renderSize.width),
            kCVPixelBufferHeightKey as String: Int(renderSize.height),
        ])
        guard writer.canAdd(input) else { return false }
        writer.add(input)
        guard writer.startWriting() else { return false }
        writer.startSession(atSourceTime: .zero)
        for _ in 0..<1000 where !input.isReadyForMoreMediaData && writer.status == .writing {
            try? await Task.sleep(nanoseconds: 2_000_000)
        }
        var buffer: CVPixelBuffer?
        guard writer.status == .writing,
              let pool = adaptor.pixelBufferPool,
              CVPixelBufferPoolCreatePixelBuffer(nil, pool, &buffer) == kCVReturnSuccess,
              let buffer,
              adaptor.append(buffer, withPresentationTime: .zero)
        else {
            if writer.status == .writing { writer.cancelWriting() }
            return false
        }
        input.markAsFinished()
        writer.endSession(atSourceTime: CMTime(value: 1, timescale: 30))
        await writer.finishWriting()
        return writer.status == .completed
    }

    private static func makeVideoInput(
        writer: AVAssetWriter,
        renderSize: CGSize,
        framesPerSecond: Double,
        codec: VideoCodec,
        quality: StudioExportQuality
    ) throws -> (input: AVAssetWriterInput, codec: VideoCodec) {
        // Where the hardware or the system has no HEVC encoder, the video is H.264 at H.264's bitrate.
        var codec = codec
        var settings = videoSettings(renderSize: renderSize, framesPerSecond: framesPerSecond, codec: codec, quality: quality)
        if codec == .hevc, !writer.canApply(outputSettings: settings, forMediaType: .video) {
            codec = .h264
            settings = videoSettings(renderSize: renderSize, framesPerSecond: framesPerSecond, codec: codec, quality: quality)
        }
        guard writer.canApply(outputSettings: settings, forMediaType: .video) else {
            throw StudioExporter.Error.writerSetupFailed
        }
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: settings)
        input.expectsMediaDataInRealTime = false
        guard writer.canAdd(input) else { throw StudioExporter.Error.writerSetupFailed }
        return (input, codec)
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
