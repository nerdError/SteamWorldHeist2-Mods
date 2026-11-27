using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

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
    const string GogGameId = "1965670180";
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
    /// <summary>"GOG" or "Steam" (by the store's files in the game folder).</summary>
    public string Store => File.Exists(Path.Combine(Root, $"goggame-{GogGameId}.info")) || File.Exists(Path.Combine(Root, "Galaxy64.dll")) ? "GOG" : "Steam";

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

    /// <summary>The game folder the loader was unpacked into (…\SteamWorld Heist 2\SWH2ModLoader\), if any.</summary>
    public static string? FromLoaderLocation()
    {
        var exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        return new[] { Path.GetDirectoryName(exeDir) ?? exeDir, exeDir }.FirstOrDefault(IsValid);
    }

    /// <summary>
    /// The game folder the loader was unpacked into, then the default Steam folder and every Steam library,
    /// then the GOG install.
    /// </summary>
    public static string? TryDetect() => DetectAll().FirstOrDefault();

    /// <summary>Every game folder found, in <see cref="TryDetect"/> order (e.g. both a Steam and a GOG install).</summary>
    public static List<string> DetectAll()
    {
        var candidates = new List<string>();
        if (FromLoaderLocation() is { } own) candidates.Add(own);

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

        candidates.AddRange(GogCandidates());
        return candidates.Where(IsValid).Select(c => Path.GetFullPath(c).TrimEnd(Path.DirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>GOG: the install path GOG writes to the registry, then its default folders.</summary>
    static IEnumerable<string> GogCandidates()
    {
        string? registered = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\WOW6432Node\GOG.com\Games\{GogGameId}")
                ?? Registry.LocalMachine.OpenSubKey($@"SOFTWARE\GOG.com\Games\{GogGameId}");
            registered = key?.GetValue("path") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        if (!string.IsNullOrEmpty(registered)) yield return registered!;
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            yield return Path.Combine(drive.RootDirectory.FullName, "GOG Games", GameFolderName);
        if (Environment.GetEnvironmentVariable("ProgramFiles(x86)") is { Length: > 0 } pf)
            yield return Path.Combine(pf, "GOG Galaxy", "Games", GameFolderName);
    }

    // ---- running game -----------------------------------------------------------------------

    /// <summary>Running game processes started from this folder (another install, e.g. Steam vs GOG, doesn't count).</summary>
    public int RunningCount() => RunningExePaths().Count(p => p != null && string.Equals(Path.GetFullPath(p), ExePath, StringComparison.OrdinalIgnoreCase));
    public bool IsRunning => RunningCount() > 0;

    /// <summary>Any copy of the game is running.</summary>
    public static bool AnyRunning => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)).Length > 0;

    /// <summary>Exe paths of every running game process (null when it can't be read; counted as not this install).</summary>
    static IEnumerable<string?> RunningExePaths()
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
            using (p) yield return ProcessPath(p.Id);
    }

    /// <summary>Full exe path of a process; QueryFullProcessImageName also works for elevated / 64-bit processes.</summary>
    public static string? ProcessPath(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
}
