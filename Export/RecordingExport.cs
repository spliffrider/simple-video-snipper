using System.ComponentModel;
using System.Diagnostics;

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

    public override string ToString() => Title;
}

internal readonly record struct ExportRequest(string Path, ExportFormat Format, bool Replace = false, bool UniqueName = false)
{
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
        if (request.UniqueName)
            Directory.CreateDirectory(folder);

        var partial = Path.Combine(folder, $".snipper-{Guid.NewGuid():N}.partial");
        try
        {
            var ffmpeg = clip.Format != request.Format || clip.Format == ExportFormat.WebM ? FindFfmpeg() : null;
            if (clip.Format == request.Format && ffmpeg is null)
            {
                // Native MP4 is already finalized. A WebM can still be saved as captured without FFmpeg.
                await CopyAsync(clip.Path, partial).ConfigureAwait(false);
            }
            else
            {
                if (ffmpeg is null)
                    throw new IOException("Changing formats requires FFmpeg. Put ffmpeg.exe beside the app and retry, or use Save As to keep the original format.");
                await ConvertAsync(ffmpeg, clip, request.Format, partial).ConfigureAwait(false);
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

    private static string? FindFfmpeg()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local))
            return local;
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory))
                continue;
            var candidate = Path.Combine(directory, "ffmpeg.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static async Task ConvertAsync(string ffmpeg, PendingClip clip, ExportFormat format, string output)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        Add("-nostdin", "-hide_banner", "-loglevel", "error", "-xerror", "-n",
            "-protocol_whitelist", "file", "-i", clip.Path, "-map", "0:v:0", "-map", "0:a:0?");
        if (clip.Format == format)
            Add("-c", "copy");
        else if (format == ExportFormat.Mp4)
            Add("-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-c:v", "libx264", "-preset", "fast",
                "-crf", "22", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart");
        else
            Add("-c:v", "libvpx-vp9", "-deadline", "good", "-cpu-used", "4", "-row-mt", "1",
                "-crf", "32", "-b:v", "0", "-pix_fmt", "yuv420p", "-c:a", "libopus", "-b:a", "128k");
        Add("-f", format == ExportFormat.Mp4 ? "mp4" : "webm", output);

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new IOException("FFmpeg could not start. Check the executable and retry.", exception);
        }
        var errorText = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var error = (await errorText.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
            throw new IOException($"FFmpeg could not export this clip. {(error.Length <= 650 ? error : error[..650])}");

        void Add(params string[] arguments)
        {
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
        }
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
