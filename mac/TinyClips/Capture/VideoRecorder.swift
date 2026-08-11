import ScreenCaptureKit
import AVFoundation
import CoreMedia
import CoreVideo
import AudioToolbox

private func isPositiveNumericTime(_ time: CMTime) -> Bool {
    time.isNumeric && CMTimeCompare(time, .zero) > 0
}

private func monotonicSampleBuffer(
    _ sampleBuffer: CMSampleBuffer,
    lastPresentationTime: inout CMTime?,
    fallbackStep: CMTime
) -> (sampleBuffer: CMSampleBuffer, didClamp: Bool)? {
    let sampleCount = max(1, CMSampleBufferGetNumSamples(sampleBuffer))
    var timing = Array(repeating: CMSampleTimingInfo(), count: sampleCount)
    var timingCount = 0
    let status = CMSampleBufferGetSampleTimingInfoArray(
        sampleBuffer,
        entryCount: timing.count,
        arrayToFill: &timing,
        entriesNeededOut: &timingCount
    )
    guard status == noErr, timingCount > 0 else { return nil }

    let defaultStep = isPositiveNumericTime(fallbackStep)
        ? fallbackStep
        : CMTime(value: 1, timescale: 600)
    let bufferDuration = CMSampleBufferGetDuration(sampleBuffer)
    let perSampleDuration = isPositiveNumericTime(bufferDuration)
        ? CMTimeMultiplyByRatio(bufferDuration, multiplier: 1, divisor: Int32(timingCount))
        : defaultStep
    var latestPresentationTime = lastPresentationTime
    var didClamp = false

    for index in 0..<timingCount {
        let presentationTime = timing[index].presentationTimeStamp
        if let latestPresentationTime, !latestPresentationTime.isNumeric {
            return nil
        }
        guard presentationTime.isNumeric || latestPresentationTime != nil else {
            return nil
        }

        let step = isPositiveNumericTime(timing[index].duration)
            ? timing[index].duration
            : perSampleDuration
        if let latestPresentationTime,
           !presentationTime.isNumeric || CMTimeCompare(presentationTime, latestPresentationTime) <= 0 {
            timing[index].presentationTimeStamp = CMTimeAdd(latestPresentationTime, step)
            didClamp = true
        }
        latestPresentationTime = timing[index].presentationTimeStamp
    }

    guard didClamp else {
        lastPresentationTime = latestPresentationTime
        return (sampleBuffer, false)
    }

    var correctedSampleBuffer: CMSampleBuffer?
    let copyStatus = CMSampleBufferCreateCopyWithNewTiming(
        allocator: kCFAllocatorDefault,
        sampleBuffer: sampleBuffer,
        sampleTimingEntryCount: timingCount,
        sampleTimingArray: timing,
        sampleBufferOut: &correctedSampleBuffer
    )
    guard copyStatus == noErr, let correctedSampleBuffer else { return nil }
    lastPresentationTime = latestPresentationTime
    return (correctedSampleBuffer, true)
}

struct MicrophoneDeviceOption: Identifiable, Hashable {
    let id: String
    let name: String
}

enum MicrophoneDeviceCatalog {
    private static func audioDevices() -> [AVCaptureDevice] {
        AVCaptureDevice.DiscoverySession(deviceTypes: [.microphone, .external], mediaType: .audio, position: .unspecified).devices
    }

    static func availableOptions() -> [MicrophoneDeviceOption] {
        audioDevices()
            .map { MicrophoneDeviceOption(id: $0.uniqueID, name: $0.localizedName) }
            .sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    static func device(for uniqueID: String) -> AVCaptureDevice? {
        guard !uniqueID.isEmpty else { return nil }
        return audioDevices().first(where: { $0.uniqueID == uniqueID })
    }
}

struct WebcamDeviceOption: Identifiable, Hashable {
    let id: String
    let name: String
}

enum WebcamDeviceCatalog {
    private static func videoDevices() -> [AVCaptureDevice] {
        AVCaptureDevice.DiscoverySession(
            deviceTypes: [.builtInWideAngleCamera, .external, .continuityCamera],
            mediaType: .video,
            position: .unspecified
        ).devices
    }

    static func availableOptions() -> [WebcamDeviceOption] {
        videoDevices()
            .map { WebcamDeviceOption(id: $0.uniqueID, name: $0.localizedName) }
            .sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    static func device(for uniqueID: String) -> AVCaptureDevice? {
        guard !uniqueID.isEmpty else { return nil }
        return videoDevices().first(where: { $0.uniqueID == uniqueID })
    }
}

final class WebcamRecorder: NSObject, @unchecked Sendable {
    private let recorderID = UUID().uuidString
    private let writingQueue = DispatchQueue(label: "com.tinyclips.webcam-writing")
    private let captureQueue = DispatchQueue(label: "com.tinyclips.webcam-capture")
    private var session: AVCaptureSession?
    private var writer: AVAssetWriter?
    private var videoInput: AVAssetWriterInput?
    private var outputURL: URL?
    private var hasStartedWriting = false
    private var hasWrittenVideoFrame = false
    private var isPaused = false
    private var pauseStartedAt: CMTime?
    private var totalPausedDuration = CMTime.zero
    private var lastPresentationTime: CMTime?
    private var fallbackFrameDuration = CMTime(value: 1, timescale: 60)
    /// Presentation timestamp (host-clock based) of the first webcam frame written.
    /// Compared against the screen recorder's first sample time to align the webcam
    /// overlay with the audio timeline. Intentionally preserved across `reset()`.
    private(set) var firstSampleTime: CMTime?

    var onWebcamDeviceName: ((String) -> Void)?
    var onWebcamError: ((String) -> Void)?
    var previewSession: AVCaptureSession? { session }

    func start(outputURL: URL, selectedWebcamID: String) async throws {
        guard await PermissionManager.shared.requestCameraPermission() else {
            throw CaptureError.webcamPermissionDenied
        }

        let device: AVCaptureDevice
        if let selected = WebcamDeviceCatalog.device(for: selectedWebcamID) {
            device = selected
        } else if selectedWebcamID.isEmpty {
            if let `default` = AVCaptureDevice.default(for: .video) {
                device = `default`
            } else if let first = WebcamDeviceCatalog.availableOptions().first,
                      let resolved = WebcamDeviceCatalog.device(for: first.id) {
                device = resolved
            } else {
                throw CaptureError.webcamUnavailable
            }
        } else {
            throw CaptureError.webcamUnavailable
        }

        onWebcamDeviceName?(device.localizedName)
        self.outputURL = outputURL

        let writer: AVAssetWriter
        do {
            writer = try AVAssetWriter(url: outputURL, fileType: .mp4)
        } catch {
            removeOutputFile(at: outputURL)
            reset()
            throw error
        }

        do {
            let dimensions = CMVideoFormatDescriptionGetDimensions(device.activeFormat.formatDescription)
            let width = max(1, Int(dimensions.width))
            let height = max(1, Int(dimensions.height))
            let activeFrameDuration = device.activeVideoMinFrameDuration
            if isPositiveNumericTime(activeFrameDuration) {
                fallbackFrameDuration = activeFrameDuration
            }
            let input = AVAssetWriterInput(mediaType: .video, outputSettings: [
                AVVideoCodecKey: AVVideoCodecType.h264,
                AVVideoWidthKey: width,
                AVVideoHeightKey: height,
            ])
            input.expectsMediaDataInRealTime = true
            guard writer.canAdd(input) else {
                throw CaptureError.saveFailed
            }
            writer.add(input)

            let session = AVCaptureSession()
            session.sessionPreset = .high
            let cameraInput = try AVCaptureDeviceInput(device: device)
            guard session.canAddInput(cameraInput) else {
                throw CaptureError.webcamConnectionFailed
            }
            session.addInput(cameraInput)

            let output = AVCaptureVideoDataOutput()
            output.videoSettings = [
                kCVPixelBufferPixelFormatTypeKey as String: Int(kCVPixelFormatType_32BGRA)
            ]
            output.alwaysDiscardsLateVideoFrames = true
            output.setSampleBufferDelegate(self, queue: writingQueue)
            guard session.canAddOutput(output) else {
                throw CaptureError.webcamReadFailed
            }
            session.addOutput(output)

            self.writer = writer
            self.videoInput = input
            self.session = session
            hasStartedWriting = false
            hasWrittenVideoFrame = false
            lastPresentationTime = nil

            captureQueue.sync {
                session.startRunning()
            }
        } catch {
            writer.cancelWriting()
            removeOutputFile(at: outputURL)
            reset()
            throw error
        }
        debugLifecycle("session started")
    }

    func stop() async throws -> URL {
        captureQueue.sync {
            if let session, session.isRunning {
                session.stopRunning()
            }
        }

        return try await withCheckedThrowingContinuation { continuation in
            writingQueue.async {
                guard let outputURL = self.outputURL,
                      let writer = self.writer,
                      let videoInput = self.videoInput else {
                    if let outputURL = self.outputURL {
                        self.removeOutputFile(at: outputURL)
                    }
                    self.reset()
                    continuation.resume(throwing: CaptureError.saveFailed)
                    return
                }

                guard self.hasWrittenVideoFrame else {
                    let writerError = writer.status == .failed ? writer.error : nil
                    writer.cancelWriting()
                    self.removeOutputFile(at: outputURL)
                    self.reset()
                    continuation.resume(throwing: writerError ?? CaptureError.noFrames)
                    return
                }

                videoInput.markAsFinished()
                writer.finishWriting {
                    self.writingQueue.async {
                        if writer.status == .completed {
                            self.debugLifecycle("finishWriting completed")
                            self.reset()
                            continuation.resume(returning: outputURL)
                        } else {
                            let message = writer.error?.localizedDescription ?? CaptureError.saveFailed.localizedDescription
                            self.onWebcamError?(message)
                            self.debugLifecycle("finishWriting failed: \(message)")
                            self.removeOutputFile(at: outputURL)
                            self.reset()
                            continuation.resume(throwing: writer.error ?? CaptureError.saveFailed)
                        }
                    }
                }
            }
        }
    }

    func pause() {
        writingQueue.async {
            guard !self.isPaused else { return }
            self.isPaused = true
            self.pauseStartedAt = CMClockGetTime(CMClockGetHostTimeClock())
        }
        captureQueue.async {
            if let session = self.session, session.isRunning {
                session.stopRunning()
            }
        }
    }

    func resume() {
        writingQueue.async {
            guard self.isPaused else { return }
            if let pauseStartedAt = self.pauseStartedAt {
                let now = CMClockGetTime(CMClockGetHostTimeClock())
                self.totalPausedDuration = CMTimeAdd(self.totalPausedDuration, CMTimeSubtract(now, pauseStartedAt))
            }
            self.pauseStartedAt = nil
            self.isPaused = false
        }
        captureQueue.async {
            self.session?.startRunning()
        }
    }

    func cancel() async {
        captureQueue.sync {
            if let session, session.isRunning {
                session.stopRunning()
            }
        }
        writingQueue.sync {
            writer?.cancelWriting()
            if let outputURL {
                removeOutputFile(at: outputURL)
            }
            reset()
        }
    }

    private func reset() {
        session = nil
        writer = nil
        videoInput = nil
        outputURL = nil
        hasStartedWriting = false
        hasWrittenVideoFrame = false
        isPaused = false
        pauseStartedAt = nil
        totalPausedDuration = .zero
        lastPresentationTime = nil
    }

    private func removeOutputFile(at url: URL) {
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        do {
            try FileManager.default.removeItem(at: url)
        } catch {
            debugLifecycle("failed to remove incomplete output: \(error.localizedDescription)")
        }
    }

    private func debugLifecycle(_ message: String) {
#if DEBUG
        print("[WebcamRecorder \(recorderID)] \(message)")
#endif
    }
}

extension WebcamRecorder: AVCaptureVideoDataOutputSampleBufferDelegate {
    func captureOutput(
        _ output: AVCaptureOutput,
        didOutput sampleBuffer: CMSampleBuffer,
        from connection: AVCaptureConnection
    ) {
        guard sampleBuffer.isValid else { return }
        guard let writer, let videoInput else { return }
        guard !isPaused else { return }

        let pauseAdjustedSampleBuffer = adjustedSampleBuffer(sampleBuffer) ?? sampleBuffer
        var proposedLastPresentationTime = lastPresentationTime
        guard let (adjustedSampleBuffer, didClamp) = monotonicSampleBuffer(
            pauseAdjustedSampleBuffer,
            lastPresentationTime: &proposedLastPresentationTime,
            fallbackStep: fallbackFrameDuration
        ) else {
            debugLifecycle("dropped webcam sample with unusable timing")
            return
        }
        if didClamp {
            debugLifecycle("clamped non-monotonic webcam presentation timestamp")
        }

        if !hasStartedWriting {
            guard writer.startWriting() else {
                onWebcamError?(writer.error?.localizedDescription ?? CaptureError.saveFailed.localizedDescription)
                return
            }
            writer.startSession(atSourceTime: adjustedSampleBuffer.presentationTimeStamp)
            firstSampleTime = adjustedSampleBuffer.presentationTimeStamp
            hasStartedWriting = true
        }

        guard videoInput.isReadyForMoreMediaData else { return }
        if videoInput.append(adjustedSampleBuffer) {
            hasWrittenVideoFrame = true
            lastPresentationTime = proposedLastPresentationTime
        }
    }

    private func adjustedSampleBuffer(_ sampleBuffer: CMSampleBuffer) -> CMSampleBuffer? {
        guard totalPausedDuration > .zero else { return sampleBuffer }
        var timing = CMSampleTimingInfo()
        guard CMSampleBufferGetSampleTimingInfo(sampleBuffer, at: 0, timingInfoOut: &timing) == noErr else {
            return sampleBuffer
        }
        if timing.presentationTimeStamp.isValid {
            timing.presentationTimeStamp = CMTimeSubtract(timing.presentationTimeStamp, totalPausedDuration)
        }
        if timing.decodeTimeStamp.isValid {
            timing.decodeTimeStamp = CMTimeSubtract(timing.decodeTimeStamp, totalPausedDuration)
        }
        var adjusted: CMSampleBuffer?
        let status = CMSampleBufferCreateCopyWithNewTiming(
            allocator: kCFAllocatorDefault,
            sampleBuffer: sampleBuffer,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timing,
            sampleBufferOut: &adjusted
        )
        return status == noErr ? adjusted : sampleBuffer
    }
}

class VideoRecorder: NSObject, @unchecked Sendable {
    private let recorderID = UUID().uuidString
    private let microphoneSignalThreshold = 0.01
    private let microphoneSignalTimeoutSeconds: TimeInterval = 2
    private let microphoneLimiterKnee: Float = 0.98
    private var stream: SCStream?
    private var writer: AVAssetWriter?
    private var videoInput: AVAssetWriterInput?
    private var systemAudioInput: AVAssetWriterInput?
    private var micAudioInput: AVAssetWriterInput?
    private var microphoneSession: AVCaptureSession?
    private var microphoneOutputDelegate: MicrophoneOutputDelegate?
    private var microphoneObservers: [NSObjectProtocol] = []
    private var lastMicSignalAt = CACurrentMediaTime()
    private var hasStartedWriting = false
    private var hasWrittenVideoFrame = false
    private var isPaused = false
    private var pauseStartedAt: CMTime?
    private var totalPausedDuration = CMTime.zero
    private var lastVideoPresentationTime: CMTime?
    private var lastSystemAudioPresentationTime: CMTime?
    private var lastMicrophonePresentationTime: CMTime?
    private var videoFallbackFrameDuration = CMTime(value: 1, timescale: 60)
    private var recordSystemAudio = false
    private var recordMicrophone = false
    private var microphoneLimiterEnabled = true
    private var windNoiseRemovalEnabled = false
    private var systemAudioMuted = false
    private var microphoneMuted = false
    private var selectedMicrophoneID = ""
    private var outputURL: URL?
    private var recordingStartedAtUptime: TimeInterval?
    private var didReportStreamFailure = false
    /// Presentation timestamp (host-clock based) of the first screen frame written.
    /// Used to align the separately-recorded webcam track to the audio timeline.
    private(set) var firstScreenSampleTime: CMTime?
    private let writingQueue = DispatchQueue(label: "com.tinyclips.video-writing")
    private let microphoneQueue = DispatchQueue(label: "com.tinyclips.microphone-capture")
    var onMicrophoneLevel: ((Double) -> Void)?
    var onMicrophoneWarning: ((String?) -> Void)?
    var onMicrophoneDeviceName: ((String) -> Void)?
    var onMicrophoneError: ((String) -> Void)?
    var onStreamFailure: ((Error) -> Void)?

    var isMicrophoneCaptureActive: Bool {
        microphoneSession != nil && recordMicrophone
    }

    var isSystemAudioCaptureActive: Bool {
        systemAudioInput != nil && recordSystemAudio
    }

    var currentRecordingDuration: TimeInterval {
        guard let recordingStartedAtUptime else { return 0 }
        return max(0, ProcessInfo.processInfo.systemUptime - recordingStartedAtUptime)
    }

    func currentTimelineTime() -> CMTime {
        writingQueue.sync {
            guard let firstScreenSampleTime else { return .zero }
            let now = CMClockGetTime(CMClockGetHostTimeClock())
            let activePauseDuration = pauseStartedAt.map { CMTimeSubtract(now, $0) } ?? .zero
            return CMTimeMaximum(
                .zero,
                CMTimeSubtract(CMTimeSubtract(now, totalPausedDuration), CMTimeAdd(firstScreenSampleTime, activePauseDuration))
            )
        }
    }

    func start(
        target: CaptureTarget,
        alwaysExcludedWindows: [SCWindow],
        outputURL: URL,
        recordSystemAudio: Bool,
        recordMicrophone: Bool,
        selectedMicrophoneID: String
    ) async throws {
        debugLifecycle("start requested")
        let preparedTarget = try await target.prepare(alwaysExcluding: alwaysExcludedWindows)
        let filter = preparedTarget.filter
        let config = preparedTarget.config

        let settings = CaptureSettings.shared
        config.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(settings.videoFrameRate))
        videoFallbackFrameDuration = config.minimumFrameInterval
        config.showsCursor = true
        config.queueDepth = 8
        config.pixelFormat = kCVPixelFormatType_32BGRA

        self.recordSystemAudio = recordSystemAudio
        self.recordMicrophone = recordMicrophone
        self.microphoneLimiterEnabled = settings.microphoneLimiterEnabled
        self.windNoiseRemovalEnabled = settings.windNoiseRemovalEnabled
        self.selectedMicrophoneID = selectedMicrophoneID

        if recordSystemAudio {
            config.capturesAudio = true
            config.sampleRate = 48000
            config.channelCount = 2
        }

        self.outputURL = outputURL

        let writer = try AVAssetWriter(url: outputURL, fileType: .mp4)
        let videoInput = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: preparedTarget.pixelWidth,
            AVVideoHeightKey: preparedTarget.pixelHeight,
        ])
        videoInput.expectsMediaDataInRealTime = true
        writer.add(videoInput)

        if recordSystemAudio {
            let audioInput = AVAssetWriterInput(mediaType: .audio, outputSettings: [
                AVFormatIDKey: kAudioFormatMPEG4AAC,
                AVSampleRateKey: 48000,
                AVNumberOfChannelsKey: 2,
                AVEncoderBitRateKey: 128000,
            ])
            audioInput.expectsMediaDataInRealTime = true
            writer.add(audioInput)
            self.systemAudioInput = audioInput
        }

        if recordMicrophone {
            let micInput = AVAssetWriterInput(mediaType: .audio, outputSettings: [
                AVFormatIDKey: kAudioFormatMPEG4AAC,
                AVSampleRateKey: 48000,
                AVNumberOfChannelsKey: 1,
                AVEncoderBitRateKey: 128000,
            ])
            micInput.expectsMediaDataInRealTime = true
            writer.add(micInput)
            self.micAudioInput = micInput
        }

        self.writer = writer
        self.videoInput = videoInput
        self.hasStartedWriting = false
        self.hasWrittenVideoFrame = false
        self.didReportStreamFailure = false
        self.lastVideoPresentationTime = nil
        self.lastSystemAudioPresentationTime = nil
        self.lastMicrophonePresentationTime = nil

        do {
            let stream = SCStream(filter: filter, configuration: config, delegate: self)
            try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: writingQueue)
            if recordSystemAudio {
                try stream.addStreamOutput(self, type: .audio, sampleHandlerQueue: writingQueue)
            }
            self.stream = stream
            try await stream.startCapture()
            recordingStartedAtUptime = ProcessInfo.processInfo.systemUptime
            debugLifecycle("stream started")

            if recordMicrophone {
                let micGranted = await AVCaptureDevice.requestAccess(for: .audio)
                if micGranted {
                    try startMicCapture(selectedMicrophoneID: self.selectedMicrophoneID)
                } else {
                    self.recordMicrophone = false
                    self.micAudioInput = nil
                    onMicrophoneError?("Microphone permission was denied.")
                }
            }
        } catch {
            debugLifecycle("start failed: \(error.localizedDescription)")
            await resetAfterFailedStart(removeOutputFile: true)
            throw error
        }
    }

    private func startMicCapture(selectedMicrophoneID: String) throws {
        let device: AVCaptureDevice
        if let selected = MicrophoneDeviceCatalog.device(for: selectedMicrophoneID) {
            device = selected
        } else if selectedMicrophoneID.isEmpty, let `default` = AVCaptureDevice.default(for: .audio) {
            device = `default`
        } else {
            throw CaptureError.microphoneUnavailable
        }

        onMicrophoneDeviceName?(device.localizedName)
        lastMicSignalAt = CACurrentMediaTime()

        let session = AVCaptureSession()
        let input = try AVCaptureDeviceInput(device: device)
        if windNoiseRemovalEnabled, input.isWindNoiseRemovalSupported {
            input.isWindNoiseRemovalEnabled = true
        }
        guard session.canAddInput(input) else {
            throw CaptureError.microphoneConnectionFailed
        }
        session.addInput(input)

        let output = AVCaptureAudioDataOutput()
        let delegate = MicrophoneOutputDelegate { [weak self] sampleBuffer in
            self?.handleMicrophoneSampleBuffer(sampleBuffer)
        }
        output.setSampleBufferDelegate(delegate, queue: microphoneQueue)
        guard session.canAddOutput(output) else {
            throw CaptureError.microphoneReadFailed
        }
        session.addOutput(output)

        microphoneOutputDelegate = delegate
        microphoneSession = session
        observeMicrophoneSession(session)
        microphoneQueue.async {
            session.startRunning()
        }
    }

    private func handleMicrophoneSampleBuffer(_ sampleBuffer: CMSampleBuffer) {
        guard sampleBuffer.isValid else { return }

        let level = rmsLevel(from: sampleBuffer)
        onMicrophoneLevel?(level)

        let now = CACurrentMediaTime()
        if level > microphoneSignalThreshold {
            lastMicSignalAt = now
            onMicrophoneWarning?(nil)
        } else if now - lastMicSignalAt > microphoneSignalTimeoutSeconds {
            onMicrophoneWarning?("No microphone input detected or microphone may be muted.")
        }

        writingQueue.async { [weak self] in
            guard let self, !self.isPaused, !self.microphoneMuted, self.hasStartedWriting, let micAudioInput = self.micAudioInput, micAudioInput.isReadyForMoreMediaData else { return }
            let pauseAdjustedSampleBuffer = self.adjustedSampleBuffer(sampleBuffer) ?? sampleBuffer
            let limitedSampleBuffer = self.limitedMicrophoneSampleBuffer(from: pauseAdjustedSampleBuffer)
            var proposedLastPresentationTime = self.lastMicrophonePresentationTime
            guard let (adjustedSampleBuffer, didClamp) = monotonicSampleBuffer(
                limitedSampleBuffer,
                lastPresentationTime: &proposedLastPresentationTime,
                fallbackStep: CMTime(value: 1024, timescale: 48_000)
            ) else {
                self.debugLifecycle("dropped microphone sample with unusable timing")
                return
            }
            if didClamp {
                self.debugLifecycle("clamped non-monotonic microphone presentation timestamp")
            }
            if micAudioInput.append(adjustedSampleBuffer) {
                self.lastMicrophonePresentationTime = proposedLastPresentationTime
            }
        }
    }

    private func limitedMicrophoneSampleBuffer(from sampleBuffer: CMSampleBuffer) -> CMSampleBuffer {
        guard microphoneLimiterEnabled,
              let formatDescription = CMSampleBufferGetFormatDescription(sampleBuffer),
              let streamDescriptionPointer = CMAudioFormatDescriptionGetStreamBasicDescription(formatDescription) else {
            return sampleBuffer
        }

        let streamDescription = streamDescriptionPointer.pointee
        guard streamDescription.mFormatID == kAudioFormatLinearPCM,
              streamDescription.mBitsPerChannel == 32,
              (streamDescription.mFormatFlags & kAudioFormatFlagIsFloat) != 0 else {
            return sampleBuffer
        }

        let isNonInterleaved = (streamDescription.mFormatFlags & kAudioFormatFlagIsNonInterleaved) != 0
        let bufferCount = isNonInterleaved ? max(1, Int(streamDescription.mChannelsPerFrame)) : 1
        let audioBufferListSize = MemoryLayout<AudioBufferList>.size
            + max(0, bufferCount - 1) * MemoryLayout<AudioBuffer>.size
        let audioBufferListMemory = UnsafeMutableRawPointer.allocate(
            byteCount: audioBufferListSize,
            alignment: MemoryLayout<AudioBufferList>.alignment
        )
        defer { audioBufferListMemory.deallocate() }

        let audioBufferList = audioBufferListMemory.assumingMemoryBound(to: AudioBufferList.self)
        var sourceBlockBuffer: CMBlockBuffer?
        let audioBufferListStatus = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sampleBuffer,
            bufferListSizeNeededOut: nil,
            bufferListOut: audioBufferList,
            bufferListSize: audioBufferListSize,
            blockBufferAllocator: nil,
            blockBufferMemoryAllocator: nil,
            flags: UInt32(kCMSampleBufferFlag_AudioBufferList_Assure16ByteAlignment),
            blockBufferOut: &sourceBlockBuffer
        )
        guard audioBufferListStatus == noErr else {
            return sampleBuffer
        }
        defer { withExtendedLifetime(sourceBlockBuffer) {} }

        let audioBuffers = UnsafeMutableAudioBufferListPointer(audioBufferList)
        guard !audioBuffers.isEmpty else {
            return sampleBuffer
        }

        var totalByteCount = 0
        for audioBuffer in audioBuffers {
            let byteCount = Int(audioBuffer.mDataByteSize)
            guard audioBuffer.mData != nil,
                  byteCount > 0,
                  byteCount.isMultiple(of: MemoryLayout<Float>.stride) else {
                return sampleBuffer
            }
            totalByteCount += byteCount
        }

        var limitedBlockBuffer: CMBlockBuffer?
        let blockBufferStatus = CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault,
            memoryBlock: nil,
            blockLength: totalByteCount,
            blockAllocator: kCFAllocatorDefault,
            customBlockSource: nil,
            offsetToData: 0,
            dataLength: totalByteCount,
            flags: 0,
            blockBufferOut: &limitedBlockBuffer
        )
        guard blockBufferStatus == noErr, let limitedBlockBuffer else {
            return sampleBuffer
        }

        var contiguousLength = 0
        var totalLength = 0
        var destinationData: UnsafeMutablePointer<Int8>?
        let dataPointerStatus = CMBlockBufferGetDataPointer(
            limitedBlockBuffer,
            atOffset: 0,
            lengthAtOffsetOut: &contiguousLength,
            totalLengthOut: &totalLength,
            dataPointerOut: &destinationData
        )
        guard dataPointerStatus == noErr,
              totalLength == totalByteCount,
              contiguousLength == totalByteCount,
              let destinationData else {
            return sampleBuffer
        }

        var destinationOffset = 0
        for audioBuffer in audioBuffers {
            guard let sourceData = audioBuffer.mData else {
                return sampleBuffer
            }

            let sampleCount = Int(audioBuffer.mDataByteSize) / MemoryLayout<Float>.stride
            let sourceSamples = sourceData.bindMemory(to: Float.self, capacity: sampleCount)
            let destinationSamples = UnsafeMutableRawPointer(destinationData.advanced(by: destinationOffset))
                .bindMemory(to: Float.self, capacity: sampleCount)

            for sampleIndex in 0..<sampleCount {
                let sample = sourceSamples[sampleIndex]
                guard sample.isFinite else {
                    return sampleBuffer
                }
                destinationSamples[sampleIndex] = softKneeLimitedSample(sample)
            }
            destinationOffset += Int(audioBuffer.mDataByteSize)
        }

        var limitedSampleBuffer: CMSampleBuffer?
        let sampleBufferStatus = CMAudioSampleBufferCreateWithPacketDescriptions(
            allocator: kCFAllocatorDefault,
            dataBuffer: limitedBlockBuffer,
            dataReady: true,
            makeDataReadyCallback: nil,
            refcon: nil,
            formatDescription: formatDescription,
            sampleCount: CMSampleBufferGetNumSamples(sampleBuffer),
            presentationTimeStamp: CMSampleBufferGetPresentationTimeStamp(sampleBuffer),
            packetDescriptions: nil,
            sampleBufferOut: &limitedSampleBuffer
        )
        guard sampleBufferStatus == noErr, let limitedSampleBuffer else {
            return sampleBuffer
        }

        let timingEntryCount = CMSampleBufferGetNumSamples(sampleBuffer)
        guard timingEntryCount > 0 else {
            return sampleBuffer
        }
        var timing = Array(repeating: CMSampleTimingInfo(), count: timingEntryCount)
        var timingCount = 0
        let timingStatus = CMSampleBufferGetSampleTimingInfoArray(
            sampleBuffer,
            entryCount: timing.count,
            arrayToFill: &timing,
            entriesNeededOut: &timingCount
        )
        guard timingStatus == noErr, timingCount > 0 else {
            return sampleBuffer
        }

        var timestampPreservingSampleBuffer: CMSampleBuffer?
        let copyStatus = CMSampleBufferCreateCopyWithNewTiming(
            allocator: kCFAllocatorDefault,
            sampleBuffer: limitedSampleBuffer,
            sampleTimingEntryCount: timingCount,
            sampleTimingArray: timing,
            sampleBufferOut: &timestampPreservingSampleBuffer
        )
        return copyStatus == noErr ? (timestampPreservingSampleBuffer ?? sampleBuffer) : sampleBuffer
    }

    private func softKneeLimitedSample(_ sample: Float) -> Float {
        let magnitude = abs(sample)
        guard magnitude > microphoneLimiterKnee else {
            return sample
        }

        let normalizedOverage = (magnitude - microphoneLimiterKnee) / (1 - microphoneLimiterKnee)
        let limitedMagnitude = microphoneLimiterKnee
            + (1 - microphoneLimiterKnee) * (2 / .pi) * atan(.pi / 2 * normalizedOverage)
        return sample.sign == .minus ? -limitedMagnitude : limitedMagnitude
    }

    private func rmsLevel(from sampleBuffer: CMSampleBuffer) -> Double {
        guard let formatDesc = CMSampleBufferGetFormatDescription(sampleBuffer),
              let asbdPointer = CMAudioFormatDescriptionGetStreamBasicDescription(formatDesc) else {
            return 0
        }

        let asbd = asbdPointer.pointee
        var audioBufferList = AudioBufferList(
            mNumberBuffers: 1,
            mBuffers: AudioBuffer(mNumberChannels: 1, mDataByteSize: 0, mData: nil)
        )
        var blockBuffer: CMBlockBuffer?
        let status = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sampleBuffer,
            bufferListSizeNeededOut: nil,
            bufferListOut: &audioBufferList,
            bufferListSize: MemoryLayout<AudioBufferList>.size,
            blockBufferAllocator: nil,
            blockBufferMemoryAllocator: nil,
            flags: UInt32(kCMSampleBufferFlag_AudioBufferList_Assure16ByteAlignment),
            blockBufferOut: &blockBuffer
        )
        guard status == noErr else { return 0 }

        let buffer = audioBufferList.mBuffers
        guard let data = buffer.mData, buffer.mDataByteSize > 0 else { return 0 }

        let isFloat = (asbd.mFormatFlags & kAudioFormatFlagIsFloat) != 0
        let bytesPerSample = Int(asbd.mBitsPerChannel / 8)
        let channels = max(1, Int(asbd.mChannelsPerFrame))
        guard bytesPerSample > 0 else { return 0 }
        let sampleCount = Int(buffer.mDataByteSize) / (bytesPerSample * channels)
        guard sampleCount > 0 else { return 0 }

        var sumSquares = 0.0
        if isFloat {
            let floatSamples = data.bindMemory(to: Float.self, capacity: sampleCount)
            for index in 0..<sampleCount {
                let value = Double(floatSamples[index])
                sumSquares += value * value
            }
        } else {
            let intSamples = data.bindMemory(to: Int16.self, capacity: sampleCount)
            for index in 0..<sampleCount {
                let value = Double(intSamples[index]) / Double(Int16.max)
                sumSquares += value * value
            }
        }

        return min(1, sqrt(sumSquares / Double(sampleCount)))
    }

    private func observeMicrophoneSession(_ session: AVCaptureSession) {
        let runtimeObserver = NotificationCenter.default.addObserver(
            forName: AVCaptureSession.runtimeErrorNotification,
            object: session,
            queue: .main
        ) { [weak self] notification in
            let error = (notification.userInfo?[AVCaptureSessionErrorKey] as? NSError)?.localizedDescription
                ?? "Microphone became unavailable."
            self?.onMicrophoneError?(error)
        }
        microphoneObservers.append(runtimeObserver)

        let interruptedObserver = NotificationCenter.default.addObserver(
            forName: AVCaptureSession.wasInterruptedNotification,
            object: session,
            queue: .main
        ) { [weak self] _ in
            self?.onMicrophoneError?("Microphone input was interrupted.")
        }
        microphoneObservers.append(interruptedObserver)

        let interruptionEndedObserver = NotificationCenter.default.addObserver(
            forName: AVCaptureSession.interruptionEndedNotification,
            object: session,
            queue: .main
        ) { [weak self] _ in
            self?.onMicrophoneWarning?(nil)
        }
        microphoneObservers.append(interruptionEndedObserver)
    }

    func stop() async throws -> URL {
        defer { recordingStartedAtUptime = nil }
        debugLifecycle("stop requested")

        // Stop mic capture first
        stopMicrophoneCapture()

        try await stream?.stopCapture()
        stream = nil
        debugLifecycle("stream stopped")

        return try await finishWriting()
    }

    func finishAfterStreamFailure() async throws -> URL {
        defer { recordingStartedAtUptime = nil }
        debugLifecycle("finalizing after stream failure")

        stopMicrophoneCapture()
        stream = nil

        return try await finishWriting()
    }

    private func finishWriting() async throws -> URL {
        return try await withCheckedThrowingContinuation { continuation in
            writingQueue.async {
                guard let outputURL = self.outputURL,
                      let writer = self.writer else {
                    if let outputURL = self.outputURL {
                        self.removeOutputFile(at: outputURL)
                    }
                    continuation.resume(throwing: CaptureError.saveFailed)
                    return
                }

                guard self.hasWrittenVideoFrame else {
                    let writerError = writer.status == .failed ? writer.error : nil
                    writer.cancelWriting()
                    self.removeOutputFile(at: outputURL)
                    continuation.resume(throwing: writerError ?? CaptureError.noFrames)
                    return
                }

                guard let videoInput = self.videoInput else {
                    writer.cancelWriting()
                    self.removeOutputFile(at: outputURL)
                    continuation.resume(throwing: CaptureError.saveFailed)
                    return
                }

                let systemAudioInput = self.systemAudioInput
                let micAudioInput = self.micAudioInput
                videoInput.markAsFinished()
                systemAudioInput?.markAsFinished()
                micAudioInput?.markAsFinished()
                writer.finishWriting {
                    self.writingQueue.async {
                        if writer.status == .completed {
                            self.debugLifecycle("finishWriting completed")
                            continuation.resume(returning: outputURL)
                        } else {
                            self.debugLifecycle("finishWriting failed: \(writer.error?.localizedDescription ?? "unknown error")")
                            self.removeOutputFile(at: outputURL)
                            continuation.resume(throwing: writer.error ?? CaptureError.saveFailed)
                        }
                    }
                }
            }
        }
    }

    func pause() {
        writingQueue.async {
            guard !self.isPaused else { return }
            self.isPaused = true
            self.pauseStartedAt = CMClockGetTime(CMClockGetHostTimeClock())
        }
        microphoneQueue.async {
            self.microphoneSession?.stopRunning()
        }
    }

    func setSystemAudioMuted(_ muted: Bool) {
        writingQueue.async {
            guard self.systemAudioInput != nil else { return }
            self.systemAudioMuted = muted
        }
    }

    func setMicrophoneMuted(_ muted: Bool) {
        writingQueue.async {
            guard self.micAudioInput != nil else { return }
            self.microphoneMuted = muted
        }
    }

    func resume() {
        writingQueue.async {
            guard self.isPaused else { return }
            if let pauseStartedAt = self.pauseStartedAt {
                let now = CMClockGetTime(CMClockGetHostTimeClock())
                self.totalPausedDuration = CMTimeAdd(self.totalPausedDuration, CMTimeSubtract(now, pauseStartedAt))
            }
            self.pauseStartedAt = nil
            self.isPaused = false
        }
        microphoneQueue.async {
            self.microphoneSession?.startRunning()
        }
    }

    func cancel() async {
        await resetAfterFailedStart(removeOutputFile: true)
    }

    private func stopMicrophoneCapture() {
        if let session = microphoneSession {
            if session.isRunning {
                session.stopRunning()
            }
        }
        for observer in microphoneObservers {
            NotificationCenter.default.removeObserver(observer)
        }
        microphoneObservers.removeAll()
        microphoneOutputDelegate = nil
        microphoneSession = nil
    }

    private func resetAfterFailedStart(removeOutputFile shouldRemoveOutputFile: Bool) async {
        stopMicrophoneCapture()
        if let stream {
            try? await stream.stopCapture()
        }
        stream = nil
        writer?.cancelWriting()
        writer = nil
        videoInput = nil
        systemAudioInput = nil
        micAudioInput = nil
        hasStartedWriting = false
        hasWrittenVideoFrame = false
        isPaused = false
        pauseStartedAt = nil
        totalPausedDuration = .zero
        lastVideoPresentationTime = nil
        lastSystemAudioPresentationTime = nil
        lastMicrophonePresentationTime = nil
        recordSystemAudio = false
        recordMicrophone = false
        microphoneLimiterEnabled = true
        windNoiseRemovalEnabled = false
        systemAudioMuted = false
        microphoneMuted = false
        selectedMicrophoneID = ""
        onMicrophoneWarning?(nil)
        onMicrophoneLevel?(0)
        onMicrophoneDeviceName?("")
        if shouldRemoveOutputFile, let outputURL {
            removeOutputFile(at: outputURL)
        }
        outputURL = nil
        recordingStartedAtUptime = nil
        didReportStreamFailure = false
    }

    private func removeOutputFile(at url: URL) {
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        do {
            try FileManager.default.removeItem(at: url)
        } catch {
            debugLifecycle("failed to remove incomplete output: \(error.localizedDescription)")
        }
    }

    private func debugLifecycle(_ message: String) {
#if DEBUG
        print("[VideoRecorder \(recorderID)] \(message)")
#endif
    }
}

private final class MicrophoneOutputDelegate: NSObject, AVCaptureAudioDataOutputSampleBufferDelegate {
    private let onSampleBuffer: (CMSampleBuffer) -> Void

    init(onSampleBuffer: @escaping (CMSampleBuffer) -> Void) {
        self.onSampleBuffer = onSampleBuffer
    }

    func captureOutput(
        _ output: AVCaptureOutput,
        didOutput sampleBuffer: CMSampleBuffer,
        from connection: AVCaptureConnection
    ) {
        onSampleBuffer(sampleBuffer)
    }
}

extension VideoRecorder: SCStreamOutput, SCStreamDelegate {
    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard sampleBuffer.isValid else { return }
        guard let writer else { return }
        guard !isPaused else { return }

        switch type {
        case .screen:
            // Only process frames with actual content
            guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
                  let statusValue = attachments.first?[.status] as? Int,
                  let status = SCFrameStatus(rawValue: statusValue),
                  status == .complete else {
                return
            }

            guard let videoInput else { return }

            let pauseAdjustedSampleBuffer = adjustedSampleBuffer(sampleBuffer) ?? sampleBuffer
            var proposedLastPresentationTime = lastVideoPresentationTime
            guard let (adjustedSampleBuffer, didClamp) = monotonicSampleBuffer(
                pauseAdjustedSampleBuffer,
                lastPresentationTime: &proposedLastPresentationTime,
                fallbackStep: videoFallbackFrameDuration
            ) else {
                debugLifecycle("dropped screen sample with unusable timing")
                return
            }
            if didClamp {
                debugLifecycle("clamped non-monotonic screen presentation timestamp")
            }

            if !hasStartedWriting {
                guard writer.startWriting() else { return }
                writer.startSession(atSourceTime: adjustedSampleBuffer.presentationTimeStamp)
                firstScreenSampleTime = adjustedSampleBuffer.presentationTimeStamp
                hasStartedWriting = true
            }

            if videoInput.isReadyForMoreMediaData {
                if videoInput.append(adjustedSampleBuffer) {
                    hasWrittenVideoFrame = true
                    lastVideoPresentationTime = proposedLastPresentationTime
                }
            }

        case .audio:
            guard !systemAudioMuted, hasStartedWriting, let systemAudioInput, systemAudioInput.isReadyForMoreMediaData else { return }
            let pauseAdjustedSampleBuffer = adjustedSampleBuffer(sampleBuffer) ?? sampleBuffer
            var proposedLastPresentationTime = lastSystemAudioPresentationTime
            guard let (adjustedSampleBuffer, didClamp) = monotonicSampleBuffer(
                pauseAdjustedSampleBuffer,
                lastPresentationTime: &proposedLastPresentationTime,
                fallbackStep: CMTime(value: 1024, timescale: 48_000)
            ) else {
                debugLifecycle("dropped system-audio sample with unusable timing")
                return
            }
            if didClamp {
                debugLifecycle("clamped non-monotonic system-audio presentation timestamp")
            }
            if systemAudioInput.append(adjustedSampleBuffer) {
                lastSystemAudioPresentationTime = proposedLastPresentationTime
            }

        case .microphone:
            break

        @unknown default:
            break
        }
    }

    private func adjustedSampleBuffer(_ sampleBuffer: CMSampleBuffer) -> CMSampleBuffer? {
        guard totalPausedDuration > .zero else { return sampleBuffer }

        let count = CMSampleBufferGetNumSamples(sampleBuffer)
        var timing = Array(repeating: CMSampleTimingInfo(), count: max(1, count))
        var timingCount = 0
        let status = CMSampleBufferGetSampleTimingInfoArray(
            sampleBuffer,
            entryCount: timing.count,
            arrayToFill: &timing,
            entriesNeededOut: &timingCount
        )
        guard status == noErr else { return sampleBuffer }

        for index in 0..<timingCount {
            if timing[index].presentationTimeStamp.isValid {
                timing[index].presentationTimeStamp = CMTimeSubtract(timing[index].presentationTimeStamp, totalPausedDuration)
            }
            if timing[index].decodeTimeStamp.isValid {
                timing[index].decodeTimeStamp = CMTimeSubtract(timing[index].decodeTimeStamp, totalPausedDuration)
            }
        }

        var adjusted: CMSampleBuffer?
        let copyStatus = CMSampleBufferCreateCopyWithNewTiming(
            allocator: kCFAllocatorDefault,
            sampleBuffer: sampleBuffer,
            sampleTimingEntryCount: timingCount,
            sampleTimingArray: timing,
            sampleBufferOut: &adjusted
        )
        return copyStatus == noErr ? adjusted : sampleBuffer
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        writingQueue.async { [weak self] in
            guard let self, self.stream === stream, !self.didReportStreamFailure else { return }
            self.didReportStreamFailure = true
            self.debugLifecycle("stream stopped unexpectedly: \(error.localizedDescription)")
            self.onStreamFailure?(error)
        }
    }
}
