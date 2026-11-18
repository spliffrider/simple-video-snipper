namespace SimpleVideoSnipper;

internal enum ExportFormat
{
    Mp4,
    WebM,
}

internal static class ExportFormats
{
    public const string Filter = "MP4 video (*.mp4)|*.mp4|WebM video (*.webm)|*.webm";

    public static string Extension(this ExportFormat format) => format == ExportFormat.WebM ? ".webm" : ".mp4";

    public static ExportFormat? FromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" => ExportFormat.Mp4,
        ".webm" => ExportFormat.WebM,
        _ => null,
    };
}

internal sealed record PendingClip(string Path, ExportFormat Format)
{
    public string Title
    {
        get
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(Path);
            var separator = name.LastIndexOf(' ');
            return separator >= 0 && Guid.TryParseExact(name[(separator + 1)..], "N", out _)
                ? name[..separator] : name;
        }
    }

    public static string NewPath(string folder, string source, ExportFormat format) =>
        System.IO.Path.Combine(folder, $"{source} {DateTime.Now:yyyy-MM-dd HH-mm-ss} {Guid.NewGuid():N}{format.Extension()}");

    public static PendingClip? FromFile(string path)
    {
        if (ExportFormats.FromPath(path) is not { } format)
            return null;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var separator = name.LastIndexOf(' ');
        return separator >= 0 && Guid.TryParseExact(name[(separator + 1)..], "N", out _)
            ? new PendingClip(path, format) : null;
    }

    public override string ToString() => Title;
}

internal readonly record struct ExportRequest(string Path, ExportFormat Format, bool Replace = false, bool UniqueName = false)
{
    public ClipEdits? Edits { get; init; }

    public static ExportRequest Quick(PendingClip clip, SavePreferences preferences) =>
        new(System.IO.Path.Combine(preferences.Folder, clip.Title + preferences.Format.Extension()),
            preferences.Format, UniqueName: true);
}

internal readonly record struct ExportResult(string Path, string? CleanupWarning);

internal static class RecordingExporter
{
    public static async Task<ExportResult> SaveAsync(PendingClip clip, ExportRequest request)
    {
        var destination = Path.GetFullPath(request.Path);
        var folder = Path.GetDirectoryName(destination)!;
        if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(clip.Path)), folder, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a save location outside the temporary recordings folder.");
        if (request.Edits?.Music is { } music && string.Equals(Path.GetFullPath(music.Path), destination,
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a different filename so the music file stays intact.");
        if (request.UniqueName)
            Directory.CreateDirectory(folder);

        var partial = Path.Combine(folder, $".snipper-{Guid.NewGuid():N}.partial");
        try
        {
            var reencode = request.Edits is not null || clip.Format != request.Format;
            var ffmpeg = reencode || clip.Format == ExportFormat.WebM ? MediaTool.Find("ffmpeg") : null;
            if (!reencode && ffmpeg is null)
            {
                // Native MP4 is already finalized. A WebM can still be saved as captured without FFmpeg.
                await CopyAsync(clip.Path, partial).ConfigureAwait(false);
            }
            else
            {
                if (ffmpeg is null)
                    throw new IOException("Editing or changing formats requires FFmpeg. Put ffmpeg.exe beside the app and retry.");
                await ConvertAsync(ffmpeg, clip, request, partial).ConfigureAwait(false);
            }

            if (new FileInfo(partial).Length == 0)
                throw new IOException("The export produced an empty file.");
            destination = Publish(partial, destination, request);
        }
        finally
        {
            TryDelete(partial);
        }

        var deleted = TryDelete(clip.Path);
        return new ExportResult(destination, deleted ? null : "The final file was saved, but its temporary copy could not be removed.");
    }

    private static async Task CopyAsync(string source, string destination)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 128 * 1024, useAsync: true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, bufferSize: 128 * 1024, useAsync: true);
        await input.CopyToAsync(output).ConfigureAwait(false);
    }

    private static string Publish(string partial, string requestedPath, ExportRequest request)
    {
        if (!request.UniqueName)
        {
            File.Move(partial, requestedPath, request.Replace);
            return requestedPath;
        }

        var folder = Path.GetDirectoryName(requestedPath)!;
        var title = Path.GetFileNameWithoutExtension(requestedPath);
        for (var index = 1; ; index++)
        {
            var candidate = index == 1
                ? requestedPath
                : Path.Combine(folder, $"{title} ({index}){request.Format.Extension()}");
            if (Path.Exists(candidate))
                continue;
            try
            {
                File.Move(partial, candidate);
                return candidate;
            }
            catch (IOException) when (Path.Exists(candidate))
            {
                // Another save claimed this name while the export was being written.
            }
        }
    }

    public static Task RenderPlaybackAsync(string ffmpeg, PendingClip clip, ClipEdits? edits, string output,
        CancellationToken cancellationToken) => ConvertAsync(ffmpeg, clip, new ExportRequest(output, ExportFormat.Mp4) { Edits = edits },
            output, playback: true, cancellationToken);

    private static Task ConvertAsync(string ffmpeg, PendingClip clip, ExportRequest request, string output,
        bool playback = false, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>();
        var format = request.Format;
        var edits = request.Edits;
        var music = edits?.Music;
        var includeAudio = edits?.Mute != true || music is not null;
        var reencode = playback || clip.Format != format || edits is not null;
        Add("-nostdin", "-hide_banner", "-loglevel", "error", "-xerror", "-n",
            "-protocol_whitelist", "file", "-i", clip.Path);
        if (music is not null)
        {
            if (music.VolumePercent is < 0 or > 100)
                throw new ArgumentException("Choose a music volume between 0 and 100 percent.");
            Add("-protocol_whitelist", "file", "-i", music.Path);
        }
        if (edits is not null)
        {
            if (edits.Start < TimeSpan.Zero || edits.End <= edits.Start)
                throw new ArgumentException("Choose a start time before the end time.");
            if (music is null && edits.Start > TimeSpan.Zero)
                Add("-ss", MediaTool.Seconds(edits.Start));
            if (music is null && edits.End is { } end)
                Add("-t", MediaTool.Seconds(end - edits.Start));
            Add("-abort_on", "empty_output_stream");
        }

        var filters = new List<string>();
        if (edits?.Crop is { } crop)
        {
            if (crop.Width < 2 || crop.Height < 2 || !new Rectangle(Point.Empty, edits.SourceSize).Contains(crop))
                throw new ArgumentException("The crop must fit within the recording.");
            filters.Add(ClipEditMedia.FitTo(edits.SourceSize));
            filters.Add(FormattableString.Invariant($"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}:exact=1"));
        }
        if (playback)
            filters.Add("scale=w='min(iw,960)':h='min(ih,540)':force_original_aspect_ratio=decrease");
        if (reencode && (format == ExportFormat.Mp4 || edits?.Crop is not null))
            filters.Add("pad=ceil(iw/2)*2:ceil(ih/2)*2");
        if (music is not null)
        {
            Add("-filter_complex", MusicFilters(edits!, music, filters), "-map", "[video]", "-map", "[audio]", "-shortest");
        }
        else
        {
            Add("-map", "0:v:0");
            if (includeAudio)
                Add("-map", "0:a:0?");
            if (filters.Count > 0)
                Add("-vf", string.Join(',', filters));
        }

        if (!reencode)
            Add("-c", "copy");
        else if (format == ExportFormat.Mp4)
        {
            if (MediaTool.IsBundled(ffmpeg))
                Add("-c:v", "h264_mf", "-hw_encoding", "0", "-pix_fmt", "nv12",
                    "-rate_control", "quality", "-quality", playback ? "65" : "80");
            else
                Add("-c:v", "libx264", "-preset", playback ? "ultrafast" : "fast", "-crf", playback ? "25" : "22",
                    "-pix_fmt", "yuv420p");
            if (includeAudio)
                Add("-c:a", "aac", "-b:a", "192k");
            Add("-movflags", "+faststart");
        }
        else
        {
            Add("-c:v", "libvpx-vp9", "-deadline", "good", "-cpu-used", "4", "-row-mt", "1",
                "-crf", "32", "-b:v", "0", "-pix_fmt", "yuv420p");
            if (includeAudio)
                Add("-c:a", "libopus", "-b:a", "128k");
        }
        Add("-f", format == ExportFormat.Mp4 ? "mp4" : "webm", output);
        return MediaTool.RunAsync(ffmpeg, arguments, "FFmpeg could not export this clip.", cancellationToken: cancellationToken);

        void Add(params string[] values) => arguments.AddRange(values);
    }

    private static string MusicFilters(ClipEdits edits, MusicTrack music, List<string> videoFilters)
    {
        var trim = $"start={MediaTool.Seconds(edits.Start)}";
        if (edits.End is { } end)
            trim += $":end={MediaTool.Seconds(end)}";
        var video = string.Join(',', new[] { $"trim={trim}", "setpts=PTS-STARTPTS" }.Concat(videoFilters));
        const string stereo = "aformat=sample_rates=48000:channel_layouts=stereo";
        var gain = FormattableString.Invariant($"{music.VolumePercent / 100.0:0.00}");
        var track = $"[1:a:0]asetpts=PTS-STARTPTS,{stereo},volume={gain},apad";
        if (edits.Mute || !edits.HasOriginalAudio)
            return $"[0:v:0]{video}[video];{track}[audio]";
        return $"[0:v:0]{video}[video];{track}[music];" +
            $"[0:a:0]atrim={trim},asetpts=PTS-STARTPTS,{stereo}[original];" +
            "[original][music]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.95:level=0:latency=1[audio]";
    }

    internal static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
