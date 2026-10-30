using System.Text.Json;

namespace Postern.App.Services;

/// <summary>
/// The app's own preferences, in <c>settings.json</c> in the data folder, so each profile (and a reinstall) keeps its
/// own. Nothing here affects what the broker allows.
/// </summary>
internal sealed class AppSettings
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>The version that last started here. An update shows the changelog sections after it.</summary>
    public string? LastSeenVersion { get; set; }

    public bool ShowChangelogAfterUpdate { get; set; } = true;

    static string PathIn(string dataDirectory) => Path.Combine(dataDirectory, "settings.json");

    /// <summary>The defaults when the file is missing or unreadable: losing a preference beats failing to start.</summary>
    public static AppSettings Load(string dataDirectory)
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathIn(dataDirectory)), Options) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = PathIn(dataDirectory);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Options));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
