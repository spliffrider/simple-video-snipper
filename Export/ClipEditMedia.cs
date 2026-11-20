using System.Globalization;
using System.Text.Json;

namespace SimpleVideoSnipper;

internal sealed record MusicTrack(string Path, int VolumePercent);

internal sealed record ClipEdits(TimeSpan Start, TimeSpan? End, Rectangle? Crop, Size SourceSize, bool Mute)
{
    public MusicTrack? Music { get; init; }
    public bool HasOriginalAudio { get; init; }
}

internal sealed class ClipEditMedia(PendingClip clip, string ffmpeg, string ffprobe, string previewPath, string? previewCopy,
    TimeSpan duration, Size videoSize, bool hasAudio) : IDisposable
{
    private (string Path, TimeSpan Duration)? playbackCopy;
    private ClipEdits? playbackEdits;

    public TimeSpan Duration { get; } = duration;
    public Size VideoSize { get; } = videoSize;
    public bool HasAudio { get; } = hasAudio;

    public static async Task<ClipEditMedia> OpenAsync(PendingClip clip)
    {
        var ffmpeg = MediaTool.Find("ffmpeg");
        var ffprobe = MediaTool.Find("ffprobe");
        if (ffmpeg is null || ffprobe is null)
            throw new IOException("Editing needs FFmpeg and FFprobe. Use the Windows installer or put both tools beside the app.");

        var info = await ProbeAsync(ffprobe, clip.Path).ConfigureAwait(false);
        if (info.Duration is { } duration)
            return new ClipEditMedia(clip, ffmpeg, ffprobe, clip.Path, null, duration, info.Size, info.HasAudio);

        // Chrome MediaRecorder can emit WebM without a duration or seek index.
        var copy = Path.Combine(Path.GetDirectoryName(clip.Path)!, $".editor-{Guid.NewGuid():N}.partial");
        try
        {
            await MediaTool.RunAsync(ffmpeg, ["-nostdin", "-v", "error", "-xerror", "-n", "-protocol_whitelist", "file",
                "-i", clip.Path, "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-f",
                clip.Format == ExportFormat.WebM ? "webm" : "mp4", copy], "Could not prepare this recording for editing.")
                .ConfigureAwait(false);
            info = await ProbeAsync(ffprobe, copy).ConfigureAwait(false);
            return new ClipEditMedia(clip, ffmpeg, ffprobe, copy, copy, info.Duration ??
                throw new IOException("The recording has no usable duration."), info.Size, info.HasAudio);
        }
        catch
        {
            RecordingExporter.TryDelete(copy);
            throw;
        }
    }

    public async Task<(string Path, TimeSpan Duration)> PlaybackSourceAsync(ClipEdits? edits, CancellationToken cancellationToken)
    {
        if (edits is null && clip.Format == ExportFormat.Mp4)
            return (clip.Path, Duration);
        if (playbackCopy is { } cached && playbackEdits == edits && File.Exists(cached.Path))
            return cached;

        RemovePlaybackCopy();
        var output = Path.Combine(Path.GetDirectoryName(clip.Path)!, $".editor-playback-{Guid.NewGuid():N}.mp4");
        try
        {
            await RecordingExporter.RenderPlaybackAsync(ffmpeg, clip, edits, output, cancellationToken).ConfigureAwait(false);
            var info = await ProbeAsync(ffprobe, output, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var source = (output, info.Duration ?? throw new IOException("The preview has no playable duration."));
            playbackCopy = source;
            playbackEdits = edits;
            return source;
        }
        catch
        {
            RecordingExporter.TryDelete(output);
            throw;
        }
    }

    public async Task<Image?> FrameAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        using var pixels = new MemoryStream();
        await MediaTool.RunAsync(ffmpeg, ["-nostdin", "-v", "error", "-xerror", "-protocol_whitelist", "file",
            "-ss", MediaTool.Seconds(position), "-i", previewPath, "-map", "0:v:0", "-frames:v", "1", "-an",
            "-vf", FitTo(VideoSize) + ",scale=1200:700:force_original_aspect_ratio=decrease",
            "-c:v", "png", "-threads", "1", "-f", "image2pipe", "pipe:1"],
            "Could not read the preview frame.", pixels, cancellationToken).ConfigureAwait(false);
        if (pixels.Length == 0)
            return null;
        pixels.Position = 0;
        using var image = Image.FromStream(pixels);
        return new Bitmap(image);
    }

    public async Task<string> RenderGifAsync(ClipEdits? edits, int longestSide, int frameRate, bool holdLastFrame,
        CancellationToken cancellationToken)
    {
        var start = edits?.Start ?? TimeSpan.Zero;
        var end = edits?.End ?? Duration;
        if (start < TimeSpan.Zero || end <= start)
            throw new ArgumentException("Choose a start time before the end time.");

        var folder = Path.GetDirectoryName(clip.Path)!;
        var output = Path.Combine(folder, $".editor-gif-{Guid.NewGuid():N}.gif");
        var palette = Path.ChangeExtension(output, ".png");
        var crop = edits?.Crop is { } area ? CropFilter(area, edits.SourceSize) : FitTo(VideoSize);
        var filters = FormattableString.Invariant(
            $"trim=duration={MediaTool.Seconds(end - start)},setpts=PTS-STARTPTS,fps={frameRate}:round=up,{crop},scale=w='min(iw,{longestSide})':h='min(ih,{longestSide})':force_original_aspect_ratio=decrease:flags=lanczos,setsar=1");
        string[] source = ["-nostdin", "-v", "error", "-xerror", "-n", "-protocol_whitelist", "file",
            "-ss", MediaTool.Seconds(start), "-i", previewPath];
        try
        {
            // Two passes let FFmpeg generate one palette without buffering every frame of a long clip.
            await MediaTool.RunAsync(ffmpeg, [.. source, "-map", "0:v:0", "-an", "-vf", filters + ",palettegen",
                "-frames:v", "1", "-threads", "1", "-f", "image2", "-update", "1", palette],
                "Could not prepare the GIF colors.", cancellationToken: cancellationToken).ConfigureAwait(false);
            await MediaTool.RunAsync(ffmpeg, [.. source, "-protocol_whitelist", "file", "-i", palette,
                "-filter_complex", $"[0:v:0]{filters}[video];[video][1:v:0]paletteuse=diff_mode=rectangle[gif]",
                "-map", "[gif]", "-an", "-abort_on", "empty_output_stream", "-loop", "0",
                "-final_delay", holdLastFrame ? "100" : "-1", "-f", "gif", output],
                "Could not render the GIF.", cancellationToken: cancellationToken).ConfigureAwait(false);
            return output;
        }
        catch
        {
            RecordingExporter.TryDelete(output);
            throw;
        }
        finally
        {
            RecordingExporter.TryDelete(palette);
        }
    }

    public static string FitTo(Size size) => FormattableString.Invariant(
        $"scale={size.Width}:{size.Height}:force_original_aspect_ratio=decrease,pad={size.Width}:{size.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1");

    public static string CropFilter(Rectangle crop, Size size)
    {
        if (crop.Width < 2 || crop.Height < 2 || !new Rectangle(Point.Empty, size).Contains(crop))
            throw new ArgumentException("The crop must fit within the recording.");
        return FitTo(size) + FormattableString.Invariant($",crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}:exact=1");
    }

    private static async Task<(TimeSpan? Duration, Size Size, bool HasAudio)> ProbeAsync(string ffprobe, string path,
        CancellationToken cancellationToken = default)
    {
        using var output = new MemoryStream();
        await MediaTool.RunAsync(ffprobe, ["-v", "error", "-protocol_whitelist", "file", "-show_entries",
            "stream=codec_type,width,height:format=duration", "-of", "json", path],
            "Could not read the recording details.", output, cancellationToken).ConfigureAwait(false);
        output.Position = 0;
        try
        {
            using var document = await JsonDocument.ParseAsync(output).ConfigureAwait(false);
            var root = document.RootElement;
            var size = Size.Empty;
            var hasAudio = false;
            foreach (var stream in root.GetProperty("streams").EnumerateArray())
            {
                switch (stream.GetProperty("codec_type").GetString())
                {
                    case "video" when size.IsEmpty:
                        size = new Size(stream.GetProperty("width").GetInt32(), stream.GetProperty("height").GetInt32());
                        break;
                    case "audio":
                        hasAudio = true;
                        break;
                }
            }
            if (size.Width < 2 || size.Height < 2)
                throw new IOException("No usable video stream was found in this recording.");
            var duration = root.GetProperty("format").TryGetProperty("duration", out var durationText) &&
                double.TryParse(durationText.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
                seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
            return (duration, size, hasAudio);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new IOException("Could not read the recording details.", exception);
        }
    }

    public void Dispose()
    {
        RemovePlaybackCopy();
        if (previewCopy is not null)
            RecordingExporter.TryDelete(previewCopy);
    }

    private void RemovePlaybackCopy()
    {
        if (playbackCopy is { } copy)
        {
            RecordingExporter.TryDelete(copy.Path);
            playbackCopy = null;
        }
    }
}
