using System.Text.Json;

namespace SWH2.ModLoader;

/// <summary>
/// GUI preferences, stored in %APPDATA%\SWH2ModLoader\settings.json. Enabled mods and their settings are not
/// here but in the game's SWH2ModLoader\profile.json (shared with the CLI).
/// </summary>
public sealed class AppSettings
{
    public string? GamePath { get; set; }

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SWH2ModLoader", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
    }
}
