import CoreGraphics
import Foundation

struct PixelCrop: Equatable, Sendable {
    let x: Int
    let y: Int
    let width: Int
    let height: Int
}

struct ClipMusic: Equatable, Sendable {
    let url: URL
    var volume: Double
}

struct ClipEdits: Equatable, Sendable {
    var start: Double
    var end: Double
    var crop: PixelCrop?
    var muteOriginal = false
    var music: ClipMusic?
    let source: ClipMediaInfo

    var duration: Double { end - start }
}

struct ClipMediaInfo: Equatable, Sendable {
    let width: Int
    let height: Int
    let duration: Double
    let hasAudio: Bool

    var fitFilter: String {
        "scale=\(width):\(height):force_original_aspect_ratio=decrease,pad=\(width):\(height):(ow-iw)/2:(oh-ih)/2,setsar=1"
    }

    static func read(_ url: URL) async throws -> ClipMediaInfo? {
        let data = try await MediaTool.ffprobe.run([
            "-v", "error", "-protocol_whitelist", "file", "-show_entries",
            "stream=codec_type,width,height:format=duration", "-of", "json", url.path,
        ], context: "Could not read the recording.")
        let probe = try JSONDecoder().decode(Probe.self, from: data)
        guard let video = probe.streams.first(where: { $0.codec_type == "video" }),
              let width = video.width, let height = video.height, width >= 2, height >= 2 else {
            throw SnipperError.problem("The recording has no usable video stream.")
        }
        guard let value = probe.format.duration, let duration = Double(value),
              duration.isFinite, duration > 0 else { return nil }
        return ClipMediaInfo(width: width, height: height, duration: duration,
                             hasAudio: probe.streams.contains { $0.codec_type == "audio" })
    }

    private struct Probe: Decodable {
        struct Stream: Decodable {
            let codec_type: String
            let width: Int?
            let height: Int?
        }

        struct Format: Decodable {
            let duration: String?
        }

        let streams: [Stream]
        let format: Format
    }
}

final class ClipEditMedia: Sendable {
    let clip: PendingClip
    let info: ClipMediaInfo
    private let scratch: URL
    private let frameSource: URL

    private init(clip: PendingClip, info: ClipMediaInfo, scratch: URL, frameSource: URL) {
        self.clip = clip
        self.info = info
        self.scratch = scratch
        self.frameSource = frameSource
    }

    deinit { removePreviewFiles() }

    func removePreviewFiles() {
        try? FileManager.default.removeItem(at: scratch)
    }

    static func open(_ clip: PendingClip) async throws -> ClipEditMedia {
        guard MediaTool.ffmpeg.available, MediaTool.ffprobe.available else {
            throw SnipperError.problem("Install FFmpeg and FFprobe with Homebrew to edit recordings.")
        }
        let scratch = FileManager.default.temporaryDirectory
            .appendingPathComponent("SnipperEditor-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: scratch, withIntermediateDirectories: false,
                                                attributes: [.posixPermissions: 0o700])
        do {
            if let info = try await ClipMediaInfo.read(clip.url) {
                return ClipEditMedia(clip: clip, info: info, scratch: scratch, frameSource: clip.url)
            }
            // MediaRecorder can omit the seek index and file duration from its WebM stream.
            let indexed = scratch.appendingPathComponent("indexed.\(clip.format.rawValue)")
            _ = try await MediaTool.ffmpeg.run([
                "-nostdin", "-v", "error", "-n", "-protocol_whitelist", "file", "-i", clip.url.path,
                "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", indexed.path,
            ], context: "Could not prepare the recording for scrubbing.")
            guard let info = try await ClipMediaInfo.read(indexed) else {
                throw SnipperError.problem("The recording has no usable duration.")
            }
            return ClipEditMedia(clip: clip, info: info, scratch: scratch, frameSource: indexed)
        } catch {
            try? FileManager.default.removeItem(at: scratch)
            throw error
        }
    }

    func frame(at position: Double) async throws -> Data {
        // Seek near the requested frame. Clone the final decoded frame for sparse screen recordings.
        let seek = max(0, min(info.duration, position) - 0.25)
        let offset = max(0, position - seek - 1.0 / 60)
        let data = try await renderFrame(seek: seek, offset: offset)
        if data.isEmpty && seek > 0 {
            return try await renderFrame(seek: 0, offset: max(0, position - 1.0 / 60))
        }
        return data
    }

    private func renderFrame(seek: Double, offset: Double) async throws -> Data {
        let filters = [
            info.fitFilter,
            "scale=w='min(iw,1200)':h='min(ih,700)':force_original_aspect_ratio=decrease",
            "fps=30", "tpad=stop_mode=clone:stop_duration=\(MediaTool.seconds(info.duration))",
        ].joined(separator: ",")
        return try await MediaTool.ffmpeg.run([
            "-nostdin", "-v", "error", "-reinit_filter:v", "0", "-ss", MediaTool.seconds(seek),
            "-protocol_whitelist", "file", "-i", frameSource.path,
            "-ss", MediaTool.seconds(offset), "-map", "0:v:0", "-an", "-vf", filters,
            "-frames:v", "1", "-threads", "1", "-c:v", "png", "-f", "image2pipe", "pipe:1",
        ], context: "Could not read the preview frame.")
    }

    func preview(_ edits: ClipEdits) async throws -> URL {
        let output = scratch.appendingPathComponent("preview-\(UUID().uuidString).mp4")
        do {
            try await ClipRenderer.render(frameSource, to: output, format: .mp4, edits: edits, preview: true)
            try Task.checkCancellation()
            return output
        } catch {
            try? FileManager.default.removeItem(at: output)
            throw error
        }
    }
}

enum ClipRenderer {
    static func render(
        _ source: URL, to destination: URL, format: ClipFormat, edits: ClipEdits? = nil, preview: Bool = false
    ) async throws {
        var args = ["-nostdin", "-v", "error", "-n"]
        if edits != nil {
            // A browser resize can change the input dimensions. Keep the trim timeline continuous.
            args += ["-reinit_filter:v", "0"]
        }
        args += ["-protocol_whitelist", "file", "-i", source.path]
        if let edits {
            try validate(edits)
            if let music = edits.music {
                args += ["-protocol_whitelist", "file", "-i", music.url.path]
            }
            let graph = filters(for: edits, preview: preview)
            args += ["-filter_complex_threads", "2", "-filter_complex", graph.filters, "-map", "[video]"]
            if graph.audio { args += ["-map", "[audio]"] }
            args += ["-t", MediaTool.seconds(edits.duration), "-abort_on", "empty_output_stream"]
        } else {
            args += ["-map", "0:v:0", "-map", "0:a:0?", "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2"]
        }
        switch format {
        case .mp4:
            args += ["-c:v", "libx264", "-preset", preview ? "ultrafast" : "fast",
                     "-crf", preview ? "25" : "22", "-pix_fmt", "yuv420p",
                     "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]
        case .webm:
            args += ["-c:v", "libvpx-vp9", "-pix_fmt", "yuv420p", "-crf", "31", "-b:v", "0",
                     "-cpu-used", "4", "-row-mt", "1", "-c:a", "libopus", "-b:a", "128k"]
        }
        _ = try await MediaTool.ffmpeg.run(args + [destination.path], context: "Could not render the recording.")
    }

    private static func validate(_ edits: ClipEdits) throws {
        guard edits.start.isFinite, edits.end.isFinite, edits.start >= 0,
              edits.end > edits.start, edits.end <= edits.source.duration else {
            throw SnipperError.problem("Choose a start time before the end time.")
        }
        if let crop = edits.crop,
           (crop.x < 0 || crop.y < 0 || crop.width < 2 || crop.height < 2
            || crop.x + crop.width > edits.source.width || crop.y + crop.height > edits.source.height) {
            throw SnipperError.problem("The crop must fit inside the recording.")
        }
        if let music = edits.music, (!music.url.isFileURL || !(0...1).contains(music.volume)) {
            throw SnipperError.problem("Choose a local music file and a volume from 0 to 100 percent.")
        }
    }

    private static func filters(for edits: ClipEdits, preview: Bool) -> (filters: String, audio: Bool) {
        let start = MediaTool.seconds(edits.start)
        let end = MediaTool.seconds(edits.end)
        let length = MediaTool.seconds(edits.duration)
        var video = [
            "setpts=PTS-STARTPTS", "fps=30", "tpad=stop_mode=clone:stop_duration=\(end)",
            "trim=start=\(start):end=\(end)", "setpts=PTS-STARTPTS", edits.source.fitFilter,
        ]
        if let crop = edits.crop {
            video.append("crop=\(crop.width):\(crop.height):\(crop.x):\(crop.y):exact=1")
        }
        if preview {
            video.append("scale=w='min(iw,960)':h='min(ih,540)':force_original_aspect_ratio=decrease")
        }
        video += ["pad=ceil(iw/2)*2:ceil(ih/2)*2", "setsar=1"]
        var graph = ["[0:v:0]" + video.joined(separator: ",") + "[video]"]

        let original = edits.source.hasAudio && !edits.muteOriginal
        let music = edits.music
        let stereo = "aformat=sample_rates=48000:channel_layouts=stereo"
        let fittedAudio = "apad,atrim=duration=\(length)"
        if original {
            let label = music == nil ? "audio" : "original"
            graph.append("[0:a:0]aresample=48000:async=1:first_pts=0,atrim=start=\(start):end=\(end),"
                         + "asetpts=PTS-STARTPTS,\(stereo),\(fittedAudio)[\(label)]")
        }
        if let music {
            let label = original ? "music" : "audio"
            let gain = MediaTool.seconds(music.volume)
            graph.append("[1:a:0]asetpts=PTS-STARTPTS,\(stereo),volume=\(gain),\(fittedAudio)[\(label)]")
        }
        if original && music != nil {
            graph.append("[original][music]amix=inputs=2:duration=longest:normalize=0,"
                         + "alimiter=limit=0.95:level=0:latency=1[audio]")
        }
        return (graph.joined(separator: ";"), original || music != nil)
    }
}
