using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace SimpleVideoSnipper;

internal static class MediaTool
{
    private static readonly string BundledFolder = Path.Combine(AppContext.BaseDirectory, "ffmpeg");

    public static string? Find(string name)
    {
        var file = name + ".exe";
        foreach (var directory in new[] { AppContext.BaseDirectory, BundledFolder }.Concat(
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(entry => entry.Trim().Trim('"'))))
        {
            if (!Path.IsPathFullyQualified(directory))
                continue;
            var candidate = Path.Combine(directory, file);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    public static bool IsBundled(string file) =>
        string.Equals(Path.GetDirectoryName(file), BundledFolder, StringComparison.OrdinalIgnoreCase);

    public static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);

    public static async Task RunAsync(string executable, IEnumerable<string> arguments, string failure,
        Stream? output = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = output is not null,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new IOException($"{Path.GetFileName(executable)} could not start. Check the executable and retry.", exception);
        }

        using var cancellation = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // The process exited before it could be stopped.
            }
        });
        var errorText = process.StandardError.ReadToEndAsync();
        var readOutput = output is null ? Task.CompletedTask : process.StandardOutput.BaseStream.CopyToAsync(output);
        await Task.WhenAll(process.WaitForExitAsync(), readOutput, errorText).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            var error = (await errorText.ConfigureAwait(false)).Trim();
            throw new IOException($"{failure} {(error.Length <= 650 ? error : error[..650])}");
        }
    }
}
