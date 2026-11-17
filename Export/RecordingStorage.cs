using Microsoft.Win32;

namespace SimpleVideoSnipper;

internal sealed class RecordingStorage(string appData, string profile)
{
    private const long MinimumFreeBytes = 10 * 1024 * 1024;
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private SavePreferences settings = new();

    public string DefaultFolder { get; } = Path.Combine(appData, "Recordings");

    private string InstalledFolder { get; } =
        LocalPath(Registry.GetValue($@"HKEY_CURRENT_USER\Software\{profile}", "TemporaryRecordingsFolder", null) as string)
        ?? Path.Combine(appData, "Recordings");

    public string CurrentFolder => Current(Volatile.Read(ref settings));

    private string Current(SavePreferences value) => LocalPath(value.TemporaryFolder) ?? InstalledFolder;

    public void Apply(SavePreferences value) => Volatile.Write(ref settings, value);

    public SavePreferences WithFolder(SavePreferences edited)
    {
        var folder = LocalPath(edited.TemporaryFolder) ??
            throw new IOException("Choose a full path on a local drive for temporary recordings.");
        var old = Volatile.Read(ref settings);
        if (PathComparer.Equals(folder, Current(old)))
            return edited with { TemporaryFolder = old.TemporaryFolder, PreviousTemporaryFolders = old.PreviousTemporaryFolders };

        Probe(PendingFolder(folder));
        return edited with
        {
            TemporaryFolder = folder,
            PreviousTemporaryFolders = Roots(old).Where(path => !PathComparer.Equals(path, folder)).ToArray(),
        };
    }

    public string PrepareCapture()
    {
        var folder = PendingFolder(CurrentFolder);
        Probe(folder);
        return folder;
    }

    public IEnumerable<string> PendingFolders() => Roots(Volatile.Read(ref settings)).Select(PendingFolder);

    public bool IsInPendingFolder(string path, SavePreferences? preferences = null)
    {
        var full = Path.GetFullPath(path);
        var folders = preferences is null ? PendingFolders() : Roots(preferences).Select(PendingFolder);
        return folders.Any(folder => PathComparer.Equals(full, folder) ||
            full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<string> Roots(SavePreferences value) =>
        new[] { Current(value), DefaultFolder, InstalledFolder }
            .Concat((value.PreviousTemporaryFolders ?? []).Select(LocalPath).OfType<string>())
            .Distinct(PathComparer);

    private string PendingFolder(string root) => Path.Combine(root,
        PathComparer.Equals(root, DefaultFolder) ? "Pending" : "Simple Video Snipper Pending");

    private static string? LocalPath(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;
        try
        {
            var path = input.Trim();
            if (!Path.IsPathFullyQualified(path) || Path.GetPathRoot(path) is not { Length: 3 } root || root[1] != ':')
                return null;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static void Probe(string folder)
    {
        var drive = new DriveInfo(Path.GetPathRoot(folder)!);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
            throw new IOException("Choose a temporary recordings folder on an available local drive.");
        if (drive.AvailableFreeSpace < MinimumFreeBytes)
            throw new IOException("Free at least 10 MB in the temporary recordings folder before recording.");

        Directory.CreateDirectory(folder);
        var probe = Path.Combine(folder, $".snipper-probe-{Guid.NewGuid():N}");
        using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
            FileOptions.DeleteOnClose);
        file.WriteByte(0);
        file.Flush();
    }
}
