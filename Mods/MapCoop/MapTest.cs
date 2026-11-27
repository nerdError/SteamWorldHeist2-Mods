using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SWH2.Core;
using SWH2.Modding;

namespace SWH2.Mods.MapCoop;

/// <summary>
/// Unattended world map tests (developer): <c>swh2 maptest …</c>. The game runs in a window in the background, the dll
/// (native/swh2map.c) presses Return until the world map runs and logs what it is told to (swh2map.txt), the result is
/// read back as text. Saves: the start save is the bank "mapcoop start" (a save on the world map), put into a slot.
/// </summary>
static class MapTest
{
    public const string StartBank = "mapcoop start";
    const int DefaultSlot = 4;   // 0-based: "slot 5" (MoreSlots), a test slot

    static string GameData => SaveBanks.SavesRoot;
    static string SettingsFile => Path.Combine(GameData, "settings.xml");

    public static int Run(IReadOnlyList<string> args, ModToolContext ctx)
    {
        try
        {
            if (args.Count == 0) { ctx.Log(Usage); return 2; }
            var game = ctx.Game ?? throw new InvalidOperationException("No game folder.");
            switch (args[0])
            {
                case "store":
                {
                    int slot = args.Count > 1 ? int.Parse(args[1]) - 1 : DefaultSlot;
                    var account = Account();
                    account.Store(slot, StartBank, overwrite: true);
                    ctx.Log($"Slot {slot + 1} stored as the map test start (bank \"{StartBank}\").");
                    return 0;
                }
                case "probe":
                {
                    int sec = Opt(args, "--sec", 40), slot = Opt(args, "--slot", DefaultSlot + 1) - 1;
                    ctx.Log(Probe(game, slot, sec));
                    return 0;
                }
                default: ctx.Log(Usage); return 2;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or FormatException or UnauthorizedAccessException)
        {
            ctx.Log("maptest: " + ex.Message);
            return 1;
        }
    }

    const string Usage = "swh2 maptest store [slot]            save in that slot (default 5) = the map test start\n" +
                         "swh2 maptest probe [--sec N] [--slot S]  start the game on the map with the probe, log for N s";

    static int Opt(IReadOnlyList<string> args, string name, int def)
    {
        for (int i = 0; i + 1 < args.Count; i++) if (args[i] == name) return int.Parse(args[i + 1]);
        return def;
    }

    static SaveBanks Account() => SaveBanks.Accounts().FirstOrDefault() ?? throw new InvalidOperationException("No game saves found.");

    static string DllDir(GameInstall game) => Path.Combine(game.ModsDir, "MapCoop");

    /// <summary>Start save into the slot, control file, run the game for sec seconds, the dll's log back.</summary>
    static string Probe(GameInstall game, int slot, int sec)
    {
        if (GameInstall.AnyRunning) throw new InvalidOperationException("Close every copy of the game first.");
        string dir = DllDir(game);
        if (!File.Exists(Path.Combine(dir, "swh2map.dll"))) throw new InvalidOperationException($"No {dir}\\swh2map.dll: deploy and install MapCoop first.");
        var account = Account();
        if (account.Find(StartBank) == null) throw new InvalidOperationException($"No map test start: swh2 maptest store <slot> first.");
        var sb = new StringBuilder();
        var auto = account.Load(StartBank, slot);
        sb.AppendLine($"Map test start in slot {slot + 1}" + (auto != null ? $" (what was there kept as bank \"{auto.Name}\")." : "."));
        File.WriteAllText(Path.Combine(dir, "swh2map.txt"), "probe\nauto\n");
        string? settings = Edit(SettingsFile, "Key=\"Screen.Fullscreen\" Value=\"1\"", "Key=\"Screen.Fullscreen\" Value=\"0\"");
        string profileFile = Path.Combine(Path.GetDirectoryName(account.SaveDir)!, "settings.xml");
        string? profile = EditRegex(profileFile, "Key=\"Session.CurrentProfile\" Value=\"[0-9]+\"", $"Key=\"Session.CurrentProfile\" Value=\"{slot}\"");
        try
        {
            var started = DateTime.Now;
            using (Process.Start(new ProcessStartInfo(game.ExePath) { WorkingDirectory = game.Root, UseShellExecute = true })) { }
            bool minimized = false;
            while ((DateTime.Now - started).TotalSeconds < sec)
            {
                Thread.Sleep(1000);
                if (!minimized) minimized = MinimizeGame();
                if (!game.IsRunning && (DateTime.Now - started).TotalSeconds > 20) { sb.AppendLine("The game closed by itself."); break; }
            }
            KillGame();
            sb.AppendLine($"Run over after {(DateTime.Now - started).TotalSeconds:0} s.");
        }
        finally
        {
            if (settings != null) File.WriteAllText(SettingsFile, settings);
            if (profile != null) File.WriteAllText(profileFile, profile);
            File.Delete(Path.Combine(dir, "swh2map.txt"));
        }
        string log = Path.Combine(dir, "swh2map.log");
        sb.AppendLine(File.Exists(log) ? $"Log: {log}" : "No log: the dll didn't load (is MapCoop installed?).");
        return sb.ToString().TrimEnd();
    }

    static string? Edit(string file, string from, string to)
    {
        if (!File.Exists(file)) return null;
        string text = File.ReadAllText(file), set = text.Replace(from, to);
        if (set == text) return null;
        File.WriteAllText(file, set);
        return text;
    }

    static string? EditRegex(string file, string pattern, string to)
    {
        if (!File.Exists(file)) return null;
        string text = File.ReadAllText(file), set = Regex.Replace(text, pattern, to);
        if (set == text) return null;
        File.WriteAllText(file, set);
        return text;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);

    /// <summary>Minimized without activation (7 = SW_SHOWMINNOACTIVE): the test never takes the user's foreground window.</summary>
    static bool MinimizeGame()
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameInstall.ExeName)))
            using (p)
                if (p.MainWindowHandle != IntPtr.Zero) { ShowWindow(p.MainWindowHandle, 7); return true; }
        return false;
    }

    static void KillGame()
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameInstall.ExeName)))
            using (p) { try { p.Kill(); p.WaitForExit(10000); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
    }
}
