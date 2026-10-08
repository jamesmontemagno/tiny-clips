import AVFoundation
import XCTest
@testable import TinyClips

final class RecordingAudioMixdownTests: XCTestCase {
    func testOnlyMoreThanOneAudioTrackNeedsMixing() {
        XCTAssertFalse(RecordingAudioMixdown.needsMix(audioTrackCount: 0))
        XCTAssertFalse(RecordingAudioMixdown.needsMix(audioTrackCount: 1))
        XCTAssertTrue(RecordingAudioMixdown.needsMix(audioTrackCount: 2))
        XCTAssertTrue(RecordingAudioMixdown.needsMix(audioTrackCount: 3))
    }

    func testCompositionWithoutAudioGetsNoMix() {
        let composition = makeComposition(audioTrackCount: 0)

        XCTAssertNil(RecordingAudioMixdown.audioMix(for: composition))
    }

    func testCompositionWithOneAudioTrackGetsNoMix() {
        let composition = makeComposition(audioTrackCount: 1)

        XCTAssertNil(RecordingAudioMixdown.audioMix(for: composition))
    }

    func testCompositionWithTwoAudioTracksGetsEveryTrackAtRecordedVolume() throws {
        let composition = makeComposition(audioTrackCount: 2)
        let audioTrackIDs = composition.tracks(withMediaType: .audio).map(\.trackID)

        let mix = try XCTUnwrap(RecordingAudioMixdown.audioMix(for: composition))

        XCTAssertEqual(mix.inputParameters.map(\.trackID), audioTrackIDs)
        for parameters in mix.inputParameters {
            var startVolume: Float = 0
            var endVolume: Float = 0
            var timeRange = CMTimeRange.zero
            XCTAssertTrue(parameters.getVolumeRamp(
                for: .zero,
                startVolume: &startVolume,
                endVolume: &endVolume,
                timeRange: &timeRange
            ))
            XCTAssertEqual(startVolume, 1)
            XCTAssertEqual(endVolume, 1)
        }
    }

    func testVideoTracksDoNotCountTowardsTheMix() {
        let composition = makeComposition(audioTrackCount: 1)
        composition.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid)
        composition.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid)

        XCTAssertNil(RecordingAudioMixdown.audioMix(for: composition))
    }

    private func makeComposition(audioTrackCount: Int) -> AVMutableComposition {
        let composition = AVMutableComposition()
        composition.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid)
        for _ in 0..<audioTrackCount {
            composition.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid)
        }
        return composition
    }
}
