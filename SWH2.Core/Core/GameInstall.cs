using System.Text.RegularExpressions;

namespace SWH2.Core;

/// <summary>
/// Paths of a SteamWorld Heist 2 installation and of the mod loader's folders inside it:
///   &lt;game&gt;\Mods\&lt;ModId&gt;\        installed mods (mod.json + plugin dll and/or files\)
///   &lt;game&gt;\SWH2ModLoader\        the loader itself, backups, state, profile, reports
/// </summary>
public sealed class GameInstall
{
    public static readonly string[] PakNames = ["Game.pak", "Game2.pak"];
    public const string ExeName = "SteamWorld Heist 2.exe";
    const string GameFolderName = "SteamWorld Heist 2";
    const string ToolFolderName = "SWH2ModLoader";
    /// <summary>Folder of the pre-1.0 "SWH2 Randomizer" tool, migrated by <see cref="MigrateLegacyLayout"/>.</summary>
    const string LegacyToolFolderName = "SWH2Randomizer";

    public GameInstall(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }
    public string CoreDir => Path.Combine(Root, "Bundle", "Core");
    public string ExePath => Path.Combine(Root, ExeName);

    /// <summary>Everything the loader creates lives here (plus the Mods folder), so the game folder stays clean.</summary>
    public string ToolDir => Directory.Exists(LegacyToolDir) && !Directory.Exists(NewToolDir) ? LegacyToolDir : NewToolDir;
    string NewToolDir => Path.Combine(Root, ToolFolderName);
    string LegacyToolDir => Path.Combine(Root, LegacyToolFolderName);

    public string BackupDir => Path.Combine(ToolDir, "Backup");
    public string ReportsDir => Path.Combine(ToolDir, "Reports");
    public string ExtractDir => Path.Combine(ToolDir, "Extracted");
    public string StatePath => Path.Combine(ToolDir, "state.json");
    public string ProfilePath => Path.Combine(ToolDir, "profile.json");
    public string ModsDir => Path.Combine(Root, "Mods");
    /// <summary>Mods folder of the old tool; still scanned until it is migrated.</summary>
    public string LegacyModsDir => Path.Combine(LegacyToolDir, "Mods");

    public string PakPath(string pak) => Path.Combine(CoreDir, pak);
    public string BackupPath(string file) => Path.Combine(BackupDir, file);

    /// <summary>
    /// Moves the old tool's folder (SWH2Randomizer\: backups, state, spoilers) to SWH2ModLoader\ and its
    /// SWH2Randomizer\Mods\* to Mods\. Only call before an operation that writes to the game folder.
    /// </summary>
    public void MigrateLegacyLayout(Action<string> log)
    {
        if (!Directory.Exists(LegacyToolDir)) return;
        if (Directory.Exists(LegacyModsDir))
        {
            Directory.CreateDirectory(ModsDir);
            foreach (var dir in Directory.GetDirectories(LegacyModsDir))
            {
                var dst = Path.Combine(ModsDir, Path.GetFileName(dir));
                if (Directory.Exists(dst)) { log($"Migration: Mods\\{Path.GetFileName(dir)} already exists, old copy left in {LegacyModsDir}"); continue; }
                Directory.Move(dir, dst);
            }
            if (!Directory.EnumerateFileSystemEntries(LegacyModsDir).Any()) Directory.Delete(LegacyModsDir);
        }
        if (Directory.Exists(NewToolDir))
        {
            log($"Migration: both {LegacyToolFolderName} and {ToolFolderName} exist; using {ToolFolderName}, the old folder is left as is.");
            return;
        }
        Directory.Move(LegacyToolDir, NewToolDir);
        var spoilers = Path.Combine(NewToolDir, "Spoilers");
        if (Directory.Exists(spoilers) && !Directory.Exists(ReportsDir))
        {
            Directory.CreateDirectory(ReportsDir);
            Directory.Move(spoilers, Path.Combine(ReportsDir, "Randomizer"));
        }
        log($"Migrated {LegacyToolFolderName}\\ to {ToolFolderName}\\ (backups and state kept).");
    }

    public static bool IsValid(string? root) =>
        !string.IsNullOrWhiteSpace(root) &&
        PakNames.All(p => File.Exists(Path.Combine(root, "Bundle", "Core", p)));

    /// <summary>
    /// The game folder the loader was unpacked into (…\SteamWorld Heist 2\SWH2ModLoader\), then the default
    /// Steam folder and every Steam library.
    /// </summary>
    public static string? TryDetect()
    {
        var candidates = new List<string>();
        var exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        candidates.Add(Path.GetDirectoryName(exeDir) ?? exeDir);
        candidates.Add(exeDir);

        var steamRoots = new List<string>();
        foreach (var env in new[] { "ProgramFiles(x86)", "ProgramFiles" })
        {
            var pf = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(pf)) steamRoots.Add(Path.Combine(pf, "Steam"));
        }
        foreach (var steam in steamRoots)
        {
            candidates.Add(Path.Combine(steam, "steamapps", "common", GameFolderName));
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\""))
                candidates.Add(Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps", "common", GameFolderName));
        }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            candidates.Add(Path.Combine(drive.RootDirectory.FullName, "SteamLibrary", "steamapps", "common", GameFolderName));

        return candidates.FirstOrDefault(IsValid);
    }
}
