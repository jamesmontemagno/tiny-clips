import Foundation

// MARK: - Sound

/// What a sound track of the screen recording holds (`sources.screen.audioTracks`).
enum StudioAudioTrackKind: String, Sendable {
    /// The computer's sound.
    case system
    case microphone
    /// Both in one track, or a track nothing is known about.
    case mixed
}

/// How loud each sound track of the screen recording is in the video (section 7 of the project
/// format). Muting is not part of it: a muted project has no sound at all.
enum StudioSound {
    /// What each of the file's sound tracks holds. Every track counts as mixed when the project
    /// does not say, or when it lists another number of tracks than the file has, because a list
    /// like that cannot be matched to the tracks.
    static func trackKinds(project: StudioProject, trackCount: Int) -> [StudioAudioTrackKind] {
        let count = max(0, trackCount)
        guard let listed = project.sources.screen.audioTracks, listed.count == count else {
            return Array(repeating: .mixed, count: count)
        }
        return listed.map { StudioAudioTrackKind(rawValue: $0) ?? .mixed }
    }

    /// The gain of each of the file's sound tracks, from 0 (silent) to 1 (as recorded): the
    /// volume of what the track holds, times the volume of the video's sound as a whole
    /// (`audio.volume`). A track that holds everything has only the second.
    static func trackGains(project: StudioProject, trackCount: Int) -> [Double] {
        let whole = volume(project.audio.volume)
        return trackKinds(project: project, trackCount: trackCount).map { kind in
            switch kind {
            case .system:
                return volume(project.audio.systemVolume) * whole
            case .microphone:
                return volume(project.audio.microphoneVolume) * whole
            case .mixed:
                return whole
            }
        }
    }

    /// Whether an export adds the sound tracks together itself: when there is more than one, so
    /// that the video has one sound track, and when one is not at the volume it was recorded
    /// at. A single track as recorded is copied as it is.
    static func exportIsMixed(trackGains: [Double], soundTracksInVideo: Int) -> Bool {
        soundTracksInVideo > 1 || (soundTracksInVideo == 1 && trackGains.contains { $0 != 1 })
    }

    /// A stored volume as it is used: between 0 and 1, and 1 when it is not a number.
    static func volume(_ value: Double) -> Double {
        guard !value.isNaN else { return 1 }
        return min(1, max(0, value))
    }
}
