import Foundation

// MARK: - Export Choices

/// How large an exported video is. A video is never made larger than its canvas.
enum StudioExportResolution: String, CaseIterable, Sendable {
    /// The canvas at its own size, however large that is.
    case source
    case p2160 = "2160p"
    case p1440 = "1440p"
    case p1080 = "1080p"
    case p720 = "720p"
    case p480 = "480p"

    /// What a stored setting stands for. A value that is not known counts as the default.
    init(stored: String) {
        self = StudioExportResolution(rawValue: stored) ?? StudioExportChoice.default.resolution
    }

    var label: String {
        switch self {
        case .source: return "Source"
        case .p2160: return "4K"
        default: return rawValue
        }
    }

    /// The long and short sides of the frame a named resolution fits a canvas inside.
    fileprivate var frame: (long: Double, short: Double)? {
        switch self {
        case .source: return nil
        case .p2160: return (3840, 2160)
        case .p1440: return (2560, 1440)
        case .p1080: return (1920, 1080)
        case .p720: return (1280, 720)
        case .p480: return (854, 480)
        }
    }
}

/// How many bits an exported video spends on each frame, from most to fewest.
enum StudioExportQuality: String, CaseIterable, Sendable {
    case best
    case high
    case standard
    case small

    init(stored: String) {
        self = StudioExportQuality(rawValue: stored) ?? StudioExportChoice.default.quality
    }

    var label: String {
        switch self {
        case .best: return "Best"
        case .high: return "High"
        case .standard: return "Standard"
        case .small: return "Small File"
        }
    }

    fileprivate var bitrateMultiple: Double {
        switch self {
        case .best: return 2.5
        case .high: return 1.5
        case .standard: return 1
        case .small: return 0.5
        }
    }
}

/// What the Export popover asks, and what an agent may pass to `studio.export`.
struct StudioExportChoice: Equatable, Sendable {
    var resolution: StudioExportResolution
    var quality: StudioExportQuality

    /// What Export offers until another choice has been made.
    static let `default` = StudioExportChoice(resolution: .p1080, quality: .high)
}

struct StudioExportResolutionOption: Equatable, Sendable {
    var resolution: StudioExportResolution
    var width: Double
    var height: Double
}

struct StudioExportQualityOption: Equatable, Sendable {
    var quality: StudioExportQuality
    var videoBitrate: Int
    var estimatedBytes: Int64
}

// MARK: - Sizes and Bitrates

/// The sizes, bitrates and estimates behind the Export popover. The numbers are the same on
/// Windows; `shared/studio/fixtures/export` holds what both have to come to.
enum StudioExportPresets {
    private static let soundBitrate = 192_000.0

    /// The size a canvas exports at. A named resolution fits the canvas inside a frame of that
    /// resolution, turned to match the canvas: 1080p is 1920 × 1080 for a wide canvas and
    /// 1080 × 1920 for a tall one. Source is the canvas's own size. The shape never changes, and
    /// nothing is scaled up.
    static func size(naturalWidth: Double, naturalHeight: Double, resolution: StudioExportResolution) -> StudioRect {
        guard let frame = resolution.frame else {
            return StudioCanvasMath.exportSize(naturalWidth: naturalWidth, naturalHeight: naturalHeight, longSideLimit: 0)
        }
        let canvasLong = max(naturalWidth, naturalHeight)
        let canvasShort = min(naturalWidth, naturalHeight)
        let scale = min(1, min(frame.long / canvasLong, frame.short / canvasShort))
        return StudioRect(
            x: 0,
            y: 0,
            width: StudioCanvasMath.even(naturalWidth * scale),
            height: StudioCanvasMath.even(naturalHeight * scale)
        )
    }

    static func size(project: StudioProject, resolution: StudioExportResolution) -> StudioRect {
        let natural = StudioCanvasMath.naturalCanvas(project: project)
        return size(naturalWidth: natural.width, naturalHeight: natural.height, resolution: resolution)
    }

    /// Whether a size fits inside the 4K frame, turned to match it. Past it, the largest video
    /// H.264 and HEVC can make depends on the Mac and on the shape of the frame.
    static func fitsFourK(width: Double, height: Double) -> Bool {
        max(width, height) <= 3840 && min(width, height) <= 2160
    }

    /// The resolutions worth offering for a canvas, largest first. One that would give the same
    /// size as a larger one is left out, so a 720p recording offers Source, not a 1080p that is
    /// not there, and a 720p that says the same thing.
    static func resolutionOptions(naturalWidth: Double, naturalHeight: Double) -> [StudioExportResolutionOption] {
        var options: [StudioExportResolutionOption] = []
        for resolution in StudioExportResolution.allCases {
            let exported = size(naturalWidth: naturalWidth, naturalHeight: naturalHeight, resolution: resolution)
            guard !options.contains(where: { $0.width == exported.width && $0.height == exported.height }) else { continue }
            options.append(StudioExportResolutionOption(resolution: resolution, width: exported.width, height: exported.height))
        }
        return options
    }

    /// The offered resolution that gives what `resolution` gives this canvas.
    static func effective(
        naturalWidth: Double,
        naturalHeight: Double,
        resolution: StudioExportResolution
    ) -> StudioExportResolution {
        let exported = size(naturalWidth: naturalWidth, naturalHeight: naturalHeight, resolution: resolution)
        return resolutionOptions(naturalWidth: naturalWidth, naturalHeight: naturalHeight)
            .first { $0.width == exported.width && $0.height == exported.height }?.resolution ?? resolution
    }

    /// The video bitrate of an export. Up to 4K, Standard is the recorder's rule on Windows; past
    /// 4K its ceiling grows with the pixels, so a Source export of a larger canvas is not starved.
    /// The others are multiples of it. HEVC gets the same picture from fewer bits.
    static func videoBitrate(
        width: Double,
        height: Double,
        framesPerSecond: Double,
        isHEVC: Bool,
        quality: StudioExportQuality
    ) -> Int {
        let fps = framesPerSecond.isFinite && framesPerSecond > 0 ? min(240, max(1, framesPerSecond)) : 30
        let ceiling = (24_000_000 * max(1, width * height / (3840 * 2160))).rounded(.towardZero)
        let standard = min(ceiling, max(2_000_000, (width * height * fps / 10).rounded(.towardZero)))
        return Int(standard * quality.bitrateMultiple * (isHEVC ? 0.6 : 1))
    }

    /// About how large the file comes to: the video and sound bitrates over its length.
    static func estimatedBytes(videoBitrate: Int, duration: Double, hasSound: Bool) -> Int64 {
        let seconds = duration.isFinite ? max(0, duration) : 0
        return Int64((Double(videoBitrate) + (hasSound ? soundBitrate : 0)) * seconds / 8)
    }
}

// MARK: - Popover Model

/// What the Export popover shows and holds while it is open, without the controls that show it.
/// The choice that was offered is kept apart from the one that was saved: a small project offers
/// Source where 1080p was saved, and that alone should not change what larger projects offer.
struct StudioExportOptionsModel: Equatable, Sendable {
    let naturalWidth: Double
    let naturalHeight: Double
    let framesPerSecond: Double
    let isHEVC: Bool
    let duration: Double
    let hasSound: Bool
    let resolutions: [StudioExportResolutionOption]
    private let saved: StudioExportChoice
    private let offered: StudioExportChoice

    var choice: StudioExportChoice

    init(
        naturalWidth: Double,
        naturalHeight: Double,
        framesPerSecond: Double,
        isHEVC: Bool,
        duration: Double,
        hasSound: Bool,
        saved: StudioExportChoice
    ) {
        self.naturalWidth = naturalWidth
        self.naturalHeight = naturalHeight
        self.framesPerSecond = framesPerSecond
        self.isHEVC = isHEVC
        self.duration = duration
        self.hasSound = hasSound
        self.saved = saved
        resolutions = StudioExportPresets.resolutionOptions(naturalWidth: naturalWidth, naturalHeight: naturalHeight)
        offered = StudioExportChoice(
            resolution: StudioExportPresets.effective(
                naturalWidth: naturalWidth,
                naturalHeight: naturalHeight,
                resolution: saved.resolution
            ),
            quality: saved.quality
        )
        choice = offered
    }

    init(project: StudioProject, isHEVC: Bool, saved: StudioExportChoice) {
        let natural = StudioCanvasMath.naturalCanvas(project: project)
        let timeMap = StudioTimeMap(sourceDuration: project.sources.screen.duration, edits: project.edits)
        self.init(
            naturalWidth: natural.width,
            naturalHeight: natural.height,
            framesPerSecond: project.sources.screen.frameRate,
            isHEVC: isHEVC,
            duration: timeMap.outputDuration,
            hasSound: !project.audio.muted,
            saved: saved
        )
    }

    var size: StudioRect {
        StudioExportPresets.size(naturalWidth: naturalWidth, naturalHeight: naturalHeight, resolution: choice.resolution)
    }

    var qualities: [StudioExportQualityOption] {
        StudioExportQuality.allCases.map { quality in
            let bitrate = StudioExportPresets.videoBitrate(
                width: size.width,
                height: size.height,
                framesPerSecond: framesPerSecond,
                isHEVC: isHEVC,
                quality: quality
            )
            return StudioExportQualityOption(
                quality: quality,
                videoBitrate: bitrate,
                estimatedBytes: StudioExportPresets.estimatedBytes(videoBitrate: bitrate, duration: duration, hasSound: hasSound)
            )
        }
    }

    var estimatedBytes: Int64 {
        qualities.first { $0.quality == choice.quality }?.estimatedBytes ?? 0
    }

    /// For example `1920 × 1080 · 30 fps · H.264`.
    var detailsText: String {
        let fps = framesPerSecond.isFinite && framesPerSecond > 0 ? framesPerSecond : 30
        let fpsText = fps == fps.rounded() ? String(Int(fps)) : String(format: "%.2f", fps)
        return "\(Int(size.width)) × \(Int(size.height)) · \(fpsText) fps · \(isHEVC ? "HEVC" : "H.264")"
    }

    /// The choice to keep for next time: the saved one, with what was changed here.
    var choiceToRemember: StudioExportChoice {
        StudioExportChoice(
            resolution: choice.resolution == offered.resolution ? saved.resolution : choice.resolution,
            quality: choice.quality
        )
    }
}
