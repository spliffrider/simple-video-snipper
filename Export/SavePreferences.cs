using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleVideoSnipper;

internal enum SaveMode
{
    Ask,
    QuickSave,
}

internal sealed record SavePreferences
{
    public static string DefaultFolder { get; } = Path.Combine(VideosFolder(), "Simple Video Snipper");

    public SaveMode Mode { get; init; }
    public ExportFormat Format { get; init; }
    public string Folder { get; init; } = DefaultFolder;
    public bool AutoCopy { get; init; }

    private static string VideosFolder()
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos, Environment.SpecialFolderOption.DoNotVerify);
        return folder.Length > 0
            ? folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
    }
}

internal sealed class PreferencesStore(string file)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public SavePreferences Load(out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(file))
                return new SavePreferences();
            if (JsonSerializer.Deserialize<SavePreferences>(File.ReadAllText(file), Options)
                is { Folder: { } folder } value && Path.IsPathFullyQualified(folder))
                return value;
            warning = "Saving preferences contain an invalid folder. Using the defaults.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            warning = "Saving preferences could not be read. Using the defaults.";
        }
        return new SavePreferences();
    }

    public void Save(SavePreferences preferences)
    {
        var partial = file + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            File.WriteAllText(partial, JsonSerializer.Serialize(preferences, Options));
            File.Move(partial, file, overwrite: true);
        }
        finally
        {
            RecordingExporter.TryDelete(partial);
        }
    }
}
