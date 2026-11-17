import AVFoundation
import Foundation

enum NativeAudioMix {
    static func combineTracks(in url: URL) async throws {
        let asset = AVURLAsset(url: url)
        let inputs = try await asset.loadTracks(withMediaType: .audio)
        guard inputs.count > 1 else { return }
        let composition = AVMutableComposition()
        guard let video = try await asset.loadTracks(withMediaType: .video).first,
              let outputVideo = composition.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid) else {
            throw SnipperError.problem("The recording had no video to combine with the audio.")
        }
        let videoRange = try await video.load(.timeRange)
        try outputVideo.insertTimeRange(videoRange, of: video, at: videoRange.start)
        outputVideo.preferredTransform = try await video.load(.preferredTransform)
        var parameters: [AVAudioMixInputParameters] = []
        for input in inputs {
            guard let track = composition.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid) else {
                throw SnipperError.problem("Could not combine the recording's audio tracks.")
            }
            let range = try await input.load(.timeRange)
            try track.insertTimeRange(range, of: input, at: range.start)
            let settings = AVMutableAudioMixInputParameters(track: track)
            settings.setVolume(1 / Float(inputs.count), at: .zero)
            parameters.append(settings)
        }
        let mix = AVMutableAudioMix()
        mix.inputParameters = parameters
        // Explicit H.264/AAC presets preserve MP4 compatibility when separate tracks need mixing.
        let preset = AVAssetExportPresetHighestQuality
        guard let exporter = AVAssetExportSession(asset: composition, presetName: preset) else {
            throw SnipperError.problem("Could not prepare an audio mix.")
        }
        exporter.audioMix = mix
        let mixed = url.deletingLastPathComponent().appendingPathComponent(".mixed-\(UUID().uuidString).mp4")
        defer { try? FileManager.default.removeItem(at: mixed) }
        try await exporter.export(to: mixed, as: .mp4)
        _ = try FileManager.default.replaceItemAt(url, withItemAt: mixed)
    }
}
