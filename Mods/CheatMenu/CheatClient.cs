namespace SWH2.Mods.CheatMenu;

/// <summary>
/// Talks to the Lua side of the mod (Lua/cheats.lua + mission hook, installed by CheatMenuMod) through files in the game's save folder:
/// commands are appended to swh2_cheats_cmd.txt, the mod polls it from the mission OnUpdate, writes results to
/// swh2_cheats.log and a heartbeat (current mission + toggles) to swh2_cheats_status.txt.
/// Works while the game is running; cheats only take effect inside a mission.
/// </summary>
public static class CheatClient
{
    public static readonly string[] Actions = ["win", "heal", "kill", "weaken", "alarm_off", "give", "mission"];
    public static readonly string[] Toggles = ["god", "freeze", "spinup"];

    /// <summary>
    /// Data folder of the first copy of the game. With several copies running (multiplayer tests; the Coop dll gives
    /// the 2nd+ one its own instanceN\ folder), set SWH2_INSTANCE=N for the loader / swh2 to talk to that one.
    /// </summary>
    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Thunderful", "SteamWorld Heist II");
            return int.TryParse(Environment.GetEnvironmentVariable("SWH2_INSTANCE"), out var n) && n > 1 ? Path.Combine(dir, $"instance{n}") : dir;
        }
    }
    static string CmdFile => Path.Combine(DataDir, "swh2_cheats_cmd.txt");
    static string StatusFile => Path.Combine(DataDir, "swh2_cheats_status.txt");
    public static string LogFile => Path.Combine(DataDir, "swh2_cheats.log");

    /// <summary>Queues a command; returns its id (it appears in the log line with the result).</summary>
    public static string Send(string cmd, string? arg = null)
    {
        if (!Actions.Contains(cmd) && !Toggles.Contains(cmd)) throw new ArgumentException($"Unknown cheat '{cmd}'.");
        if (arg != null && arg.Any(char.IsWhiteSpace)) throw new ArgumentException("Argument must not contain spaces.");
        var id = DateTime.UtcNow.ToString("HHmmssfff");
        var line = $"{id} {DateTimeOffset.UtcNow.ToUnixTimeSeconds()} {cmd}{(string.IsNullOrEmpty(arg) ? "" : " " + arg)}\n";
        Directory.CreateDirectory(DataDir);
        // The mod renames the file away while reading it; retry if we hit that moment.
        for (int attempt = 0; ; attempt++)
        {
            try { File.AppendAllText(CmdFile, line); return id; }
            catch (IOException) when (attempt < 20) { Thread.Sleep(25); }
        }
    }

    public static void SetToggle(string name, bool on) => Send(name, on ? "1" : "0");

    public sealed record Status(string Script, DateTimeOffset LastSeen, IReadOnlyDictionary<string, bool> Toggles)
    {
        /// <summary>The mod writes a heartbeat every ~2 s while a mission runs.</summary>
        public bool InMission => DateTimeOffset.UtcNow - LastSeen < TimeSpan.FromSeconds(6);
    }

    public static Status? ReadStatus()
    {
        try
        {
            if (!File.Exists(StatusFile)) return null;
            var kv = File.ReadAllLines(StatusFile)
                .Select(l => l.Split(['='], 2)).Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim(), p => p[1].Trim());
            var time = long.TryParse(kv.GetValueOrDefault("time"), out var t) ? DateTimeOffset.FromUnixTimeSeconds(t) : DateTimeOffset.MinValue;
            return new Status(kv.GetValueOrDefault("script", "?"), time, Toggles.ToDictionary(n => n, n => kv.GetValueOrDefault(n) == "1"));
        }
        catch (IOException) { return null; }
    }

    public static IReadOnlyList<string> ReadLogTail(int lines)
    {
        try
        {
            if (!File.Exists(LogFile)) return [];
            using var s = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var all = new StreamReader(s).ReadToEnd().SplitTrim('\n');
            return all.TakeLast(lines).ToList();
        }
        catch (IOException) { return []; }
    }
}
