using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SWH2.Core;

namespace SWH2.Mods.Coop;

/// <summary>
/// The netplay record/replay test (native/netplay.c, docs/MULTIPLAYER.md) as steps for the loader window and
/// <c>swh2 nettest</c>: keep a start save in a bank, play from it with recording on, replay from it, compare.
/// Files are those of the first game instance (%APPDATA%\Thunderful\SteamWorld Heist II).
/// </summary>
public static class NetTest
{
    public const string StartBank = "nettest start";

    public static string Dir => SaveBanks.SavesRoot;
    public static string ControlFile => Path.Combine(Dir, "swh2_net.txt");
    public static string RecordLog => Path.Combine(Dir, "swh2_net_record.log");
    public static string ReplayLog => Path.Combine(Dir, "swh2_net_replay.log");
    public static string ReplayedLog => Path.Combine(Dir, "swh2_net_replayed.log");

    /// <summary>The installed exe was patched with the netTest setting (".coop" section + 0x60 = "NET1").</summary>
    public static bool InstalledWithNetTest(GameInstall game)
    {
        try
        {
            var exe = File.ReadAllBytes(game.ExePath);
            int pe = BitConverter.ToInt32(exe, 0x3c);
            int sections = BitConverter.ToUInt16(exe, pe + 6), optional = BitConverter.ToUInt16(exe, pe + 20);
            int table = pe + 24 + optional;
            for (int i = 0; i < sections; i++)
            {
                int s = table + i * 40;
                if (Encoding.ASCII.GetString(exe, s, 8).TrimEnd('\0') != ".coop") continue;
                int raw = BitConverter.ToInt32(exe, s + 20), size = BitConverter.ToInt32(exe, s + 16);
                return size >= 0x64 && Encoding.ASCII.GetString(exe, raw + 0x60, 4) == "NET1";
            }
        }
        catch (IOException) { }
        return false;
    }

    public static SaveBanks? Account() => SaveBanks.Accounts().FirstOrDefault();

    /// <summary>Copies a save slot (0-based) into the start bank.</summary>
    public static string StoreStart(int slot)
    {
        var account = Account() ?? throw new InvalidOperationException("No game saves found.");
        account.Store(slot, StartBank, overwrite: true);
        return $"Slot {slot + 1} stored as the test start (bank \"{StartBank}\").";
    }

    /// <summary>Record (replay = false) or replay run: start save into the slot, control file, start the game.</summary>
    public static string Prepare(GameInstall game, int slot, bool replay, bool launch, bool autoplay = false)
    {
        if (game.IsRunning) throw new InvalidOperationException("Close the game first (it keeps the save slots in memory).");
        if (!InstalledWithNetTest(game))
            throw new InvalidOperationException("The game was installed without Co-op → Netplay test: tick it in the mod settings and click Apply to game.");
        var account = Account() ?? throw new InvalidOperationException("No game saves found.");
        if (account.Find(StartBank) == null) throw new InvalidOperationException("No test start yet: store a slot first (step 1).");
        var lines = new List<string>();
        if (replay)
        {
            if (!File.Exists(RecordLog)) throw new InvalidOperationException("No record yet: do a record run first (step 2).");
            File.Copy(RecordLog, ReplayLog, true);
            lines.Add("Record copied to swh2_net_replay.log.");
        }
        Guard(account, slot, lines);
        var auto = account.Load(StartBank, slot);
        lines.Add($"Test start put into slot {slot + 1}" + (auto != null ? $" (what was there is kept as bank \"{auto.Name}\")." : "."));
        Directory.CreateDirectory(Dir);
        File.WriteAllText(ControlFile, autoplay ? "autoplay\n" : replay ? "replay\n" : "record\n");
        lines.Add(replay
            ? $"Replay on. In the game: Continue / load slot {slot + 1}, enter the mission and don't touch anything until the replay is done (then quit and compare)."
            : $"Recording on. In the game: Continue / load slot {slot + 1}, play a few turns (let the enemies act), then quit the game.");
        if (launch)
        {
            Process.Start(new ProcessStartInfo(game.ExePath) { WorkingDirectory = game.Root, UseShellExecute = true });
            lines.Add("Game started.");
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static string AutoScriptFile => Path.Combine(Dir, "swh2_net_autorun.txt");
    public static string AutoLog => Path.Combine(Dir, "swh2_coop_input.log");

    /// <summary>
    /// From game start into the mission saved in the slot, for the dll autopilot (swh2coop.c): Return until the mission
    /// world runs — skips the intro, title Start, slot list (the cursor stands on the save just put there, the most recent
    /// one), which loads the mission.
    /// Tutorial popups and dialogue are clicked through on the way and during the replay.
    /// </summary>
    public static IEnumerable<string> DefaultAutoScript(int slot)
    {
        yield return $"# slot {slot + 1}: the slot list opens on it";
        yield return "popups on";
        yield return "cutscenes on";
        yield return "enter 2500";      // return until the mission runs: intro, title, slot, loading
    }

    /// <summary>
    /// A whole unattended test run: start save into the slot, control file, autopilot script, start the game, wait until
    /// the replay is over (or nothing happens for <paramref name="stallSec"/> s / <paramref name="timeoutSec"/> s in all),
    /// close the game, compare. Returns a short report; <paramref name="differs"/> as Compare.
    /// </summary>
    /// <param name="autoplay">Make a fresh record instead: the player's commands of the current record are played, the
    /// rest of the battle runs as in a normal recording; the result replaces the record.</param>
    public static string Run(GameInstall game, int slot, bool replay, int timeoutSec, int stallSec, IEnumerable<string>? script, out bool differs, bool autoplay = false)
    {
        differs = true;
        var sb = new StringBuilder();
        if (autoplay) replay = true;   // the record becomes the script (swh2_net_replay.log)
        sb.AppendLine(Prepare(game, slot, replay, launch: false, autoplay));
        File.WriteAllLines(AutoScriptFile, script ?? DefaultAutoScript(slot));
        string outLog = replay && !autoplay ? ReplayedLog : RecordLog;
        string? settings = Windowed();
        var account = SaveBanks.ForGame(game);
        string? profile = LastSlot(account, slot);
        try { return RunGame(game, replay && !autoplay, timeoutSec, stallSec, outLog, sb, out differs); }
        finally { if (settings != null) File.WriteAllText(SettingsFile, settings); PutBack(account, profile); ClearControlFiles(); }
    }

    /// <summary>Data folder of the second game copy started (the Coop dll gives it instanceN\, N = 2).</summary>
    /// <summary>--next: the host's next battle (the test mission again: its crew and script are known to work).</summary>
    const string NextMission = "west_caribbea_23";

    public static string GuestDir => Path.Combine(Dir, "instance2");
    public static string GuestReplayedLog => Path.Combine(GuestDir, "swh2_net_replayed.log");

    /// <summary>
    /// Network test on one PC (docs/MULTIPLAYER.md, "Сеть"): <paramref name="host"/> (started first, instance 1) plays the
    /// test start by itself (autoplay of the current record) and streams its log over TCP; <paramref name="guest"/>
    /// (instance 2) loads the same start and replays live what arrives, its AI off. Then the host's record is compared
    /// with what the guest did. The guest has to keep its saves apart from the host's: a GOG copy (saves in
    /// &lt;data&gt;\Save000, the Steam copy in &lt;data&gt;\&lt;account&gt;\Save000).
    /// </summary>
    /// <param name="guestPlays">Test of the guest commands and the owner split: the crew is dealt host / guest / host...;
    /// the host plays the record's commands of its own crewmates, the guest those of its own and sends them to the host,
    /// which plays them (logged <c>by=guest</c>; the host waits for them where the record has them).</param>
    /// <param name="shots">Look at the UI: the game windows are moved off the screen instead of minimized (minimized ones
    /// do not draw) and both are captured every 15 s to <see cref="ShotsDir"/> (host_NNN.png / guest_NNN.png).</param>
    /// <param name="desyncSec">This long into the guest's battle the guest alone gets the CheatMenu cheat "kill" (every enemy
    /// dies there, not at the host): its next state correction finds them missing, and its dll is to rejoin the host's
    /// battle from its save (hard correction).</param>
    /// <param name="checkpointSec">The host loads its checkpoint this long after its game starts (autopilot "checkpoint" =
    /// the pause menu's Load checkpoint): its dll asks for a save, the guest's is to quit and join from it.</param>
    /// <param name="rejoinSec">The guest quits to the main menu this long into its battle (autopilot "quit"); its dll is to
    /// take the host's latest save of the battle, Continue by itself and replay on from there (compared from that save).</param>
    public static string RunNet(GameInstall host, GameInstall guest, int slot, int port, int timeoutSec, int stallSec, out bool differs, bool guestPlays = false, bool shots = false, string[]? names = null, int dropSec = 0, bool late = false, int rejoinSec = 0, int afterSec = 0, bool next = false, bool mapQuit = false, int winSec = 0, int desyncSec = 0, int checkpointSec = 0, bool menuJoin = false)
    {
        differs = true;
        if (GameInstall.AnyRunning) throw new InvalidOperationException("Close every copy of the game first.");
        if (guest.Store != "GOG") throw new InvalidOperationException("The guest copy must be the GOG one (a second Steam copy would share the host's save slots).");
        if (!InstalledWithNetTest(guest))
            throw new InvalidOperationException($"The guest ({guest.Root}) was installed without Co-op → Netplay test.");
        var sb = new StringBuilder();
        sb.AppendLine(Prepare(host, slot, replay: true, launch: false, autoplay: true));
        // The record to play: the guest takes it (guest play) before the host starts a new one over it.
        if (guestPlays) { Directory.CreateDirectory(GuestDir); File.Copy(RecordLog, Path.Combine(GuestDir, "swh2_net_guestplay.log"), true); }
        var account0 = Account() ?? throw new InvalidOperationException("No game saves found.");
        File.WriteAllText(ControlFile, $"autoplay host 127.0.0.1:{port}\n" + (names is [var hostName, ..] ? $"name {hostName}\n" : "") +
            (late || menuJoin || rejoinSec > 0 || desyncSec > 0 || checkpointSec > 0 || next || mapQuit ? $"save {account0.SlotPath(slot)}\n" : ""));   // this PC only: no firewall question
        // --next: after the lost mission the host loads the checkpoint (its battle again, a new save there), the guest goes
        // to the world map: its dll is to see the host's new battle, quit to the main menu from the map and Continue into it.
        var hostScript = DefaultAutoScript(slot).ToList();
        // To the world map after the mission: lost = the fail popup's second button; won (--win) = Return through the results.
        string[] toMap = winSec > 0 ? new[] { "over", "cutscenes off", "popups off", "wait 6000", "key return", "wait 3000", "key return", "wait 3000", "key return", "wait 3000", "key return" }
                                    : new[] { "over", "cutscenes off", "popups off", "wait 8000", "key down", "wait 800", "key return" };
        if (mapQuit) hostScript.AddRange(toMap);
        if (checkpointSec > 0) hostScript.AddRange(new[] { $"wait {checkpointSec * 1000}", "checkpoint" });
        // --next (with --win): both go to the world map; the host's ship stands at the next mission's marker, Return enters it
        // (its game saves there, held until the guest comes); the guest's dll is to quit to the main menu and Continue into it.
        // --next: the host goes straight into its next battle (CheatMenu "mission", the game's ChangeMission) while the
        // guest wins and goes to the world map: its dll is to see the host's new battle, quit to the main menu and Continue.
        File.WriteAllLines(AutoScriptFile, hostScript);

        // The guest: same start save in its own slot, live replay from the host, same autopilot.
        var account = Account() ?? throw new InvalidOperationException("No game saves found.");
        var bank = account.Find(StartBank) ?? throw new InvalidOperationException("No test start yet.");
        string guestSaves = Path.Combine(Dir, "Save000"), guestSlot = Path.Combine(guestSaves, $"savegame_{slot:000}.dat");
        Directory.CreateDirectory(guestSaves);
        if (File.Exists(guestSlot) && !File.Exists(guestSlot + ".nettest-kept")) File.Copy(guestSlot, guestSlot + ".nettest-kept");
        File.Copy(Path.Combine(bank.Dir, "savegame.dat"), guestSlot, true);
        Directory.CreateDirectory(GuestDir);
        string guestControl = $"replay connect 127.0.0.1:{port}{(guestPlays ? " play" : "")}\n" + (names is [_, var guestName, ..] ? $"name {guestName}\n" : "") +
            $"slot {guestSlot}\n" +
            (dropSec > 0 ? $"drop {dropSec}\n" : "");
        File.WriteAllText(Path.Combine(GuestDir, "swh2_net.txt"), guestControl);
        // --menu-join: as a guest that Steam's "Join game" started (no loader, no "from"): nothing pressed in the menu —
        // its dll is to fetch the host's save there, bank the slot's old file, put the save in and Continue by itself.
        var guestScript = menuJoin ? new List<string> { "popups on", "cutscenes on" } : DefaultAutoScript(slot).ToList();
        if (rejoinSec > 0) guestScript.AddRange(new[] { $"wait {rejoinSec * 1000}", "quit" });
        if (next) guestScript.AddRange(toMap);
        if (mapQuit) guestScript.AddRange(toMap.Concat(new[] { "wait 30000", "quit" }));
        File.WriteAllLines(Path.Combine(GuestDir, "swh2_net_autorun.txt"), guestScript);
        sb.AppendLine($"Guest: test start in its slot {slot + 1} ({guestSlot}, what was there kept as .nettest-kept), replays live from 127.0.0.1:{port}.");

        string? settings = Windowed();
        string? hostProfile = LastSlot(account0, slot);
        try
        {
            var started = DateTime.Now;
            Process.Start(new ProcessStartInfo(host.ExePath) { WorkingDirectory = host.Root, UseShellExecute = true })?.Dispose();
            Thread.Sleep(8000);   // the host claims instance 1 before the guest starts
            long logFrom = 0;
            if (late)
            {
                // Joining inside the host's mission: the host goes into the battle alone (held: it waits for the guest),
                // its game saves the slot there; that save goes into the guest's slot, the guest takes the log from it.
                var saveFile = account0.SlotPath(slot);
                var before = File.GetLastWriteTimeUtc(saveFile);
                var minimizedHost = new HashSet<IntPtr>();
                while (File.GetLastWriteTimeUtc(saveFile) == before)
                {
                    if (!host.IsRunning) throw new InvalidOperationException("The host's game closed before it saved in the mission.");
                    if ((DateTime.Now - started).TotalSeconds > timeoutSec) throw new InvalidOperationException("The host's game didn't save in the mission.");
                    MinimizeAll(minimizedHost);
                    Thread.Sleep(1000);
                }
                Thread.Sleep(3000);
                var got = NetSession.FetchSave("127.0.0.1", port);
                File.WriteAllBytes(guestSlot, got.Data);
                if (File.Exists(guestSlot + ".backup")) File.Delete(guestSlot + ".backup");
                logFrom = got.LogFrom;
                File.WriteAllText(Path.Combine(GuestDir, "swh2_net.txt"), guestControl + $"from {got.LogFrom} {got.TurnEnds} {(got.InBattle ? "battle" : "map")}\n");
                sb.AppendLine($"Late join: the host saved in its battle {(DateTime.Now - started).TotalSeconds:0} s in, at log byte {got.LogFrom} ({got.TurnEnds} turn ends before, {(got.InBattle ? "in the battle" : "NOT in the battle")}); that save is the guest's start.");
            }
            Process.Start(new ProcessStartInfo(guest.ExePath) { WorkingDirectory = guest.Root, UseShellExecute = true })?.Dispose();
            long lastSize = -1;
            var lastChange = DateTime.Now;
            var minimized = new HashSet<IntPtr>();
            string end = "timeout";
            int shot = 0;
            DateTime? inAt = null;   // --win: the guest entered the battle (the cheat goes to both that long after)
            bool winSent = false, desyncSent = false;
            DateTime? overAt = null;   // the guest's mission over: the games run afterSec more (screens after a battle)
            var lastShot = DateTime.Now;
            if (shots) { Directory.CreateDirectory(ShotsDir); foreach (var f in Directory.GetFiles(ShotsDir, "*.png")) File.Delete(f); }
            while (true)
            {
                Thread.Sleep(2000);
                if (!shots) MinimizeAll(minimized);
                else
                {
                    OffScreenAll(minimized);
                    if ((DateTime.Now - lastShot).TotalSeconds >= 15) { lastShot = DateTime.Now; Capture(++shot); }
                }
                if (!host.IsRunning || !guest.IsRunning) { end = (host.IsRunning ? "the guest" : "the host") + " closed"; break; }
                string log = GuestReplayedLog;
                long size = File.Exists(log) && File.GetLastWriteTime(log) >= started ? new FileInfo(log).Length : 0;
                if (size != lastSize) { lastSize = size; lastChange = DateTime.Now; }
                if ((winSec > 0 || desyncSec > 0) && size > 0 && inAt == null && ReadShared(log).Contains("# to host: ready")) inAt = DateTime.Now;
                if (inAt != null && desyncSec > 0 && !desyncSent && (DateTime.Now - inAt.Value).TotalSeconds >= desyncSec)
                {
                    desyncSent = true;
                    File.AppendAllText(Path.Combine(GuestDir, "swh2_cheats_cmd.txt"), $"{DateTime.UtcNow:HHmmssfff} {DateTimeOffset.UtcNow.ToUnixTimeSeconds()} kill\n");
                    sb.AppendLine($"Cheat \"kill\" sent to the guest only {desyncSec} s into the battle.");
                }
                if (inAt != null && winSec > 0 && !winSent && (DateTime.Now - inAt.Value).TotalSeconds >= winSec)
                {
                    winSent = true;
                    foreach (var (dir, cheat) in new[] { (Dir, next ? "mission " + NextMission : "win"), (GuestDir, "win") })
                        File.AppendAllText(Path.Combine(dir, "swh2_cheats_cmd.txt"), $"{DateTime.UtcNow:HHmmssfff} {DateTimeOffset.UtcNow.ToUnixTimeSeconds()} {cheat}\n");
                    sb.AppendLine(next ? $"Cheats {winSec} s into the battle: host \"mission {NextMission}\", guest \"win\"." : $"Cheat \"win\" sent to both {winSec} s into the battle.");
                }
                if (size > 0 && overAt == null && (next || menuJoin ? ReadShared(log).Contains("# joining the host's battle") : ReadShared(log).Contains("# mission over"))) overAt = DateTime.Now;
                if (overAt != null && (DateTime.Now - overAt.Value).TotalSeconds >= Math.Max(3, afterSec))
                {
                    end = "the guest's mission over (" + (ReadShared(log).Contains("# mission over: victory") ? "victory" : "crew lost") + ")" +
                          (afterSec > 0 ? $", then {afterSec} s more" : "");
                    break;
                }
                if (overAt == null && size > 0 && (DateTime.Now - lastChange).TotalSeconds > stallSec) { end = $"the guest logged nothing for {stallSec} s"; break; }
                if ((DateTime.Now - started).TotalSeconds > timeoutSec) break;
            }
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameInstall.ExeName)))
                using (p) { try { p.Kill(); p.WaitForExit(10000); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
            sb.AppendLine($"Run over after {(DateTime.Now - started).TotalSeconds:0} s: {end}.");
            foreach (var (who, path) in new[] { ("Host", RecordLog), ("Guest", GuestReplayedLog) })
            {
                if (!File.Exists(path) || File.GetLastWriteTime(path) < started) { sb.AppendLine($"{who}: no log (mission never entered?)."); return sb.ToString().TrimEnd(); }
                var lines = ReadShared(path).Split('\n');
                int Count(string prefix) => lines.Count(l => l.StartsWith(prefix, StringComparison.Ordinal));
                sb.AppendLine($"{who}: {Count("cmd ")} commands, {Count("end ")} turn ends, {Count("# DIFF")} DIFF lines, {Count("# ERROR")} errors" +
                              (who == "Guest" ? $", {Count("fix ")} corrections ({lines.Count(l => l.StartsWith("fix ", StringComparison.Ordinal) && FixChanged.IsMatch(l))} changed something)." : "."));
                if (guestPlays && who == "Host")
                    sb.AppendLine($"Host: {lines.Count(l => l.StartsWith("cmd ", StringComparison.Ordinal) && l.Contains(" by=guest"))} commands and " +
                                  $"{lines.Count(l => l.StartsWith("end ", StringComparison.Ordinal) && l.Contains(" by=guest"))} turn ends played for the guest, " +
                                  $"{Count("# guest's turn end")} turn ends asked for too late, {Count("# guest command")} commands ignored.");
                if (guestPlays && who == "Guest")
                    sb.AppendLine($"Guest: {lines.Count(l => l.StartsWith("# to host: ", StringComparison.Ordinal) && !l.Contains("gend"))} commands and " +
                                  $"{lines.Count(l => l.StartsWith("# to host: gend", StringComparison.Ordinal))} turn ends sent to the host.");
            }
            if (dropSec > 0)
                sb.AppendLine($"Connection broken by the test {dropSec} s after connecting: the guest connected again " +
                              $"{ReadShared(GuestReplayedLog).Split('\n').Count(l => l.StartsWith("# net: connected to the host again", StringComparison.Ordinal))} time(s) in the mission.");
            string record = RecordLog, replayed = GuestReplayedLog;
            if (rejoinSec > 0 || desyncSec > 0 || checkpointSec > 0 || next || menuJoin)
            {
                // Compared from the host's save the guest came back with: the part of its log after the rejoin.
                var text = ReadShared(GuestReplayedLog);
                const string mark = "# joining the host's battle from its save (host log byte ";
                int at = text.LastIndexOf(mark, StringComparison.Ordinal);
                var lines = text.Split('\n');
                string Line(string prefix) => lines.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))?.TrimEnd() ?? "(none)";
                sb.AppendLine($"Rejoin: quit {Line("# quit to the main menu")}; slot {Line("# main menu: the host's save")}; continue {Line("# main menu: Continue")}" +
                              $"{(lines.Any(l => l.StartsWith("# ERROR: main menu", StringComparison.Ordinal)) ? "; " + Line("# ERROR: main menu") : "")}.");
                if (at < 0) { sb.AppendLine("Rejoin: the guest did NOT come back into the host's battle."); return sb.ToString().TrimEnd(); }
                int close = text.IndexOf(')', at);
                logFrom = long.Parse(text.Substring(at + mark.Length, close - at - mark.Length));
                replayed = Path.Combine(GuestDir, "swh2_net_replayed_after_rejoin.log");
                File.WriteAllText(replayed, text.Substring(text.IndexOf('\n', at) + 1));
                sb.AppendLine($"Rejoin: the guest came back into the host's battle from its save at host log byte {logFrom}.");
            }
            if (logFrom > 0)
            {
                // The guest's battle starts at the save: compare the host's record from there on.
                var bytes = File.ReadAllBytes(RecordLog);
                record = Path.Combine(Dir, "swh2_net_record_from_save.log");
                File.WriteAllBytes(record, bytes.Skip((int)Math.Min(logFrom, bytes.Length)).ToArray());
            }
            sb.Append(Compare(out differs, record: record, replayed: replayed));
            return sb.ToString().TrimEnd();
        }
        finally { if (settings != null) File.WriteAllText(SettingsFile, settings); PutBack(account0, hostProfile); ClearControlFiles(); }
    }

    /// <summary>
    /// After an unattended run: the control files go, so the next normal start of the game is not a test run (the Coop
    /// dll records or replays whenever swh2_net.txt exists).
    /// </summary>
    static void ClearControlFiles()
    {
        foreach (var f in new[] { ControlFile, Path.Combine(GuestDir, "swh2_net.txt") })
            try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { }
    }

    public static string ShotsDir => Path.Combine(Dir, "nettest_shots");

    /// <summary>Moves the game windows off the screen without activating them: they keep drawing, the desktop stays free.</summary>
    static void OffScreenAll(HashSet<IntPtr> done)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameInstall.ExeName)))
            using (p)
                if (p.MainWindowHandle != IntPtr.Zero && done.Add(p.MainWindowHandle))
                    SetWindowPos(p.MainWindowHandle, (IntPtr)1, -4000, 100, 0, 0, 0x0001 | 0x0010);   // HWND_BOTTOM, SWP_NOSIZE | SWP_NOACTIVATE
    }

    /// <summary>Both game windows to PNG (PrintWindow with PW_RENDERFULLCONTENT: works for windows off the screen).</summary>
    static void Capture(int n)
    {
        int k = 0;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameInstall.ExeName)).OrderBy(p => p.StartTime))
            using (p)
            {
                var h = p.MainWindowHandle;
                if (h == IntPtr.Zero || !GetClientRect(h, out var r) || r.Right <= 0 || r.Bottom <= 0) { k++; continue; }
                using var bmp = new System.Drawing.Bitmap(r.Right, r.Bottom);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    var dc = g.GetHdc();
                    PrintWindow(h, dc, 1 | 2);   // PW_CLIENTONLY | PW_RENDERFULLCONTENT
                    g.ReleaseHdc(dc);
                }
                bmp.Save(Path.Combine(ShotsDir, $"{(k++ == 0 ? "host" : "guest")}_{n:000}.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
    }

    struct Rect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out Rect r);

    static void MinimizeAll(HashSet<IntPtr> done)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameInstall.ExeName)))
            using (p)
                if (p.MainWindowHandle != IntPtr.Zero && done.Add(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, 7);
    }

    static string SettingsFile => Path.Combine(Dir, "settings.xml");

    /// <summary>
    /// The slot list and Continue start on the game's last used slot (Session.CurrentProfile in the account's
    /// settings.xml): a player who played their own save since the last test would get it loaded by the autopilot. The
    /// test slot is made the last one for the run; returns the old text to put back (null = nothing changed).
    /// </summary>
    static string? LastSlot(SaveBanks? account, int slot)
    {
        if (account == null) return null;
        string file = Path.Combine(Path.GetDirectoryName(account.SaveDir)!, "settings.xml");
        if (!File.Exists(file)) return null;
        string text = File.ReadAllText(file);
        string set = System.Text.RegularExpressions.Regex.Replace(text, "Key=\"Session.CurrentProfile\" Value=\"[0-9]+\"", $"Key=\"Session.CurrentProfile\" Value=\"{slot}\"");
        if (set == text) return null;
        File.WriteAllText(file, set);
        return text;
    }

    /// <summary>
    /// The player's own slots are banked before a run ("nettest guard slot N &lt;time of the slot file&gt;", a new bank only when
    /// the slot changed since): on 2026-11-28 Steam Cloud's "cloud or local?" question at the host's start brought back a
    /// settings.xml with the player's slot 3 as the last one — the autopilot's Continue loaded it and the test played and
    /// saved over it.
    /// </summary>
    static void Guard(SaveBanks account, int testSlot, List<string> lines)
    {
        for (int i = 0; i < 64; i++)
        {
            if (i == testSlot || !File.Exists(account.SlotPath(i))) continue;
            string name = $"nettest guard slot {i + 1} {File.GetLastWriteTime(account.SlotPath(i)):yyyy-MM-dd HH-mm-ss}";
            if (account.Find(name) != null) continue;
            try { account.Store(i, name); lines.Add($"Slot {i + 1} kept as bank \"{name}\" (in case the test loads it)."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { lines.Add($"WARNING: slot {i + 1} could not be banked: {ex.Message}"); }
        }
    }

    static void PutBack(SaveBanks? account, string? text)
    {
        if (account != null && text != null) File.WriteAllText(Path.Combine(Path.GetDirectoryName(account.SaveDir)!, "settings.xml"), text);
    }

    /// <summary>Test runs play in a window: Screen.Fullscreen = 0 in the game's settings.xml; returns the old text to put back.</summary>
    static string? Windowed()
    {
        if (!File.Exists(SettingsFile)) return null;
        string text = File.ReadAllText(SettingsFile);
        string windowed = text.Replace("Key=\"Screen.Fullscreen\" Value=\"1\"", "Key=\"Screen.Fullscreen\" Value=\"0\"");
        if (windowed == text) return null;
        File.WriteAllText(SettingsFile, windowed);
        return text;
    }

    static string RunGame(GameInstall game, bool replay, int timeoutSec, int stallSec, string outLog, StringBuilder sb, out bool differs)
    {
        differs = true;
        var started = DateTime.Now;
        using var proc = Process.Start(new ProcessStartInfo(game.ExePath) { WorkingDirectory = game.Root, UseShellExecute = true });
        long lastSize = -1;
        var lastChange = DateTime.Now;
        bool minimized = false;
        DateTime? finished = null;
        string end = "timeout";
        while (true)
        {
            Thread.Sleep(2000);
            if (!minimized) minimized = MinimizeGame();
            if (!game.IsRunning && (DateTime.Now - started).TotalSeconds > 20) { end = "the game closed"; break; }
            long size = File.Exists(outLog) && File.GetLastWriteTime(outLog) >= started ? new FileInfo(outLog).Length : 0;
            if (size != lastSize) { lastSize = size; lastChange = DateTime.Now; }
            if (finished == null && size > 0 && ReadShared(outLog) is var text)
            {
                if (text.Contains("# mission over")) { end = "mission over (" + (text.Contains("# mission over: victory") ? "victory" : "crew lost") + ")"; break; }
                if (text.Contains("# replay finished")) finished = DateTime.Now;
            }
            if (finished != null && (DateTime.Now - finished.Value).TotalSeconds > 20) { end = "replay finished"; break; }
            // Before the mission the log stays empty (title, loading): only the overall timeout applies then.
            if (size > 0 && (DateTime.Now - lastChange).TotalSeconds > stallSec) { end = $"nothing logged for {stallSec} s"; break; }
            if ((DateTime.Now - started).TotalSeconds > timeoutSec) break;
        }
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameInstall.ExeName)))
            using (p) { try { p.Kill(); p.WaitForExit(10000); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
        sb.AppendLine($"Run over after {(DateTime.Now - started).TotalSeconds:0} s: {end}.");
        if (!File.Exists(outLog) || File.GetLastWriteTime(outLog) < started) { sb.AppendLine("No log written: the mission was never entered (see the autopilot lines in swh2_coop_input.log)."); return sb.ToString().TrimEnd(); }
        var lines = ReadShared(outLog).Split('\n');
        int Count(string prefix) => lines.Count(l => l.StartsWith(prefix, StringComparison.Ordinal));
        sb.AppendLine($"Log: {Count("cmd ")} commands, {Count("end ")} turn ends, {Count("state ")} states, {Count("# DIFF")} DIFF lines, " +
                      $"{Count("fix ")} corrections ({lines.Where(l => l.StartsWith("fix ", StringComparison.Ordinal) && FixChanged.IsMatch(l)).Count()} changed something), {Count("# ERROR")} errors.");
        if (replay) sb.Append(Compare(out differs));
        else differs = false;
        return sb.ToString().TrimEnd();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);

    /// <summary>
    /// Minimizes the game window without activating it (SW_SHOWMINNOACTIVE), once it exists: test runs stay out of the
    /// way. The Coop dll keeps the "minimized" / "focus lost" events from the game, so it does not pause.
    /// </summary>
    static bool MinimizeGame()
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameInstall.ExeName)))
            using (p)
                if (p.MainWindowHandle != IntPtr.Zero) { ShowWindow(p.MainWindowHandle, 7); return true; }
        return false;
    }

    /// <summary>A log the game may still have open.</summary>
    static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs, Encoding.GetEncoding(28591));
        return r.ReadToEnd();
    }

    public static string Status(GameInstall? game)
    {
        var sb = new StringBuilder();
        if (game == null) sb.AppendLine("Game folder: not set.");
        else
        {
            sb.AppendLine($"Game: {game.Store} build, {(game.IsRunning ? "running" : "not running")}.");
            sb.AppendLine($"Installed with Netplay test: {(InstalledWithNetTest(game) ? "yes" : "NO — tick Co-op → Netplay test, Apply to game")}.");
        }
        var start = Account()?.Find(StartBank);
        sb.AppendLine(start != null ? $"Test start: stored {start.Saved.ToLocalTime():yyyy-MM-dd HH:mm} (from slot {(start.FromSlot ?? -1) + 1})." : "Test start: none yet.");
        string mode = File.Exists(ControlFile) ? File.ReadAllText(ControlFile).Trim() : "record (no swh2_net.txt)";
        sb.AppendLine($"Next game start: {mode}.");
        foreach (var (name, path) in new[] { ("Record", RecordLog), ("Replay input", ReplayLog), ("Replay result", ReplayedLog) })
            sb.AppendLine(File.Exists(path)
                ? $"{name}: {File.GetLastWriteTime(path):HH:mm:ss}, {File.ReadLines(path).Count(l => l.StartsWith("cmd ", StringComparison.Ordinal))} commands."
                : $"{name}: —");
        return sb.ToString().TrimEnd();
    }

    // ---- comparison (record vs replay) ------------------------------------------------------------

    sealed record Checkpoint(int Line, string Kind, string Key, string? Sync, Dictionary<string, (string Pos, string Hp, string Side)>? State);

    static Dictionary<string, string> Fields(string line)
    {
        var d = new Dictionary<string, string>();
        foreach (var part in line.Split(' ').Skip(2))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && !d.ContainsKey(part.Substring(0, eq))) d[part.Substring(0, eq)] = part.Substring(eq + 1);
        }
        return d;
    }

    /// <summary>
    /// Movement paths (type 4): the last 4 bytes of every 16-byte node are padding with leftovers of a pointer (differ
    /// between game starts); the dll zeroes them since run 8, older logs have them.
    /// </summary>
    static string MaskPath(string t)
    {
        if (!t.StartsWith("4:", StringComparison.Ordinal)) return t;
        var c = t.ToCharArray();
        for (int node = 2 + 16; node + 32 <= c.Length; node += 32)
            for (int i = 24; i < 32; i++) c[node + i] = '0';
        return new string(c);
    }

    static readonly Regex StatePart = new(@"^(.*?)@(.*)#([^>]*)(?:>(.*))?$");   // id@x,y#hp>L|R (facing since 2026-11-28)
    static readonly Regex FixChanged = new(@"=[1-9]");

    /// <summary>
    /// Checkpoints in order: top-level commands (d=0), turn ends, turn starts, each with the state line just before it
    /// (if there was one since the previous checkpoint). Actors are named by ordinal ("actor ID o=N" lines) when the log
    /// has them, so ids that differ between runs still compare equal. o=replay counts as o=player.
    /// </summary>
    static List<Checkpoint> Checkpoints(string path)
    {
        var list = new List<Checkpoint>();
        var ord = new Dictionary<string, string>();
        Dictionary<string, (string, string, string)>? state = null;
        int n = 0;
        string Name(string id) => ord.TryGetValue(id, out var o) ? "#" + o : id;
        foreach (var line in File.ReadLines(path, Encoding.GetEncoding(28591)))
        {
            n++;
            if (line.StartsWith("actor ", StringComparison.Ordinal))
            {
                var p = line.Split(' ');
                if (p.Length > 2 && p[2].StartsWith("o=", StringComparison.Ordinal)) ord[p[1]] = p[2].Substring(2);
            }
            else if (line.StartsWith("state ", StringComparison.Ordinal))
            {
                state = [];
                foreach (var part in line.Substring(6).Split(' '))
                    if (StatePart.Match(part) is { Success: true } m) state[Name(m.Groups[1].Value)] = (m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
            }
            else if (line.StartsWith("cmd ", StringComparison.Ordinal))
            {
                var f = Fields(line);
                if (f.TryGetValue("d", out var d) && d != "0") continue;
                string who = f.TryGetValue("o", out var o) && o == "replay" ? "player" : o ?? "?";
                string actor = f.TryGetValue("ao", out var ao) && ao != "-1" ? "#" + ao : Name(f.GetValueOrDefault("a") ?? "?");
                string target = MaskPath(f.GetValueOrDefault("t") ?? "?");
                if (f.TryGetValue("to", out var to) && to != "-1") target = "2:#" + to;
                list.Add(new(n, "cmd", $"{who} {actor} {f.GetValueOrDefault("i")} {f.GetValueOrDefault("n")} {target} fl={f.GetValueOrDefault("fl")}",
                    $"{f.GetValueOrDefault("r")} e={f.GetValueOrDefault("e")}", state));
                state = null;
            }
            else if (line.StartsWith("end ", StringComparison.Ordinal))
            {
                var f = Fields(line);
                string who = f.TryGetValue("o", out var o) && o == "replay" ? "player" : o ?? "?";
                list.Add(new(n, "end", who, $"{f.GetValueOrDefault("r")} e={f.GetValueOrDefault("e")}", state));
                state = null;
            }
            else if (line.StartsWith("turn ", StringComparison.Ordinal))
            {
                list.Add(new(n, "turn", line.Substring(5), null, state));
                state = null;
            }
        }
        return list;
    }

    static IEnumerable<string> StateDiff(Dictionary<string, (string Pos, string Hp, string Side)> a, Dictionary<string, (string Pos, string Hp, string Side)> b, double tol)
    {
        foreach (var k in a.Keys.Union(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!a.TryGetValue(k, out var x) || !b.TryGetValue(k, out var y))
            {
                yield return $"actor {k} only in the {(a.ContainsKey(k) ? "record" : "replay")}";
                continue;
            }
            if (x.Hp != y.Hp) yield return $"actor {k} health {x.Hp} vs {y.Hp}";
            if (x.Side != "" && y.Side != "" && x.Side != y.Side) yield return $"actor {k} facing {x.Side} vs {y.Side}";
            if (Pos(x.Pos) is { } p && Pos(y.Pos) is { } q)
            {
                double off = Math.Max(Math.Abs(p.X - q.X), Math.Abs(p.Y - q.Y));
                if (off > tol) yield return $"actor {k} position {x.Pos} vs {y.Pos} (off by {off:0.##})";
            }
            else if (x.Pos != y.Pos) yield return $"actor {k} position {x.Pos} vs {y.Pos}";
        }
    }

    static (double X, double Y)? Pos(string s)
    {
        var p = s.Split(',');
        return p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
               && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ? (x, y) : null;
    }

    /// <summary>Report of record vs replay; differs = the runs differ (or the replay noted a difference).</summary>
    public static string Compare(out bool differs, bool all = false, double tol = 2.0, string? record = null, string? replayed = null)
    {
        record ??= ReplayLog;
        replayed ??= ReplayedLog;
        foreach (var f in new[] { record, replayed })
            if (!File.Exists(f)) throw new InvalidOperationException($"Missing {f} — do a record run and a replay run first.");
        var a = Checkpoints(record);
        var b = Checkpoints(replayed);
        var sb = new StringBuilder();
        sb.AppendLine($"record: {record} ({a.Count} checkpoints)");
        sb.AppendLine($"replay: {replayed} ({b.Count} checkpoints)");
        int shown = 0, same = 0;
        var drift = new List<string>();
        for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            if (i >= a.Count || i >= b.Count)
            {
                var (longer, which) = i >= a.Count ? (b, "replay") : (a, "record");
                sb.AppendLine($"#{i}: the {which} goes on (line {longer[i].Line}: {longer[i].Kind} {longer[i].Key})");
                shown++;
                break;
            }
            var (x, y) = (a[i], b[i]);
            var msg = new List<string>();
            if (x.Kind != y.Kind || x.Key != y.Key) msg.Add($"{x.Kind} {x.Key}   vs   {y.Kind} {y.Key}");
            if (x.State != null && y.State != null) msg.AddRange(StateDiff(x.State, y.State, tol));
            if (x.Sync != null && y.Sync != null && x.Sync != y.Sync) drift.Add($"#{i} {x.Kind} (lines {x.Line}/{y.Line}): {x.Sync} vs {y.Sync}");
            if (msg.Count == 0) { same++; continue; }
            sb.AppendLine($"#{i} (record line {x.Line}, replay line {y.Line}):");
            foreach (var m in msg) sb.AppendLine("    " + m);
            shown++;
            if (!all) break;
        }
        if (drift.Count > 0)
        {
            sb.AppendLine($"RNG / entity counter differ at {drift.Count} checkpoints (first {Math.Min(drift.Count, 5)}):");
            foreach (var d in drift.Take(5)) sb.AppendLine("    " + d);
        }
        var notes = File.ReadLines(replayed).Where(l => l.StartsWith("# DIFF", StringComparison.Ordinal) || l.StartsWith("# ERROR", StringComparison.Ordinal)).ToList();
        foreach (var l in notes.Take(12)) sb.AppendLine("replay log: " + l);
        if (notes.Count > 12) sb.AppendLine($"replay log: … {notes.Count - 12} more");
        differs = shown > 0 || notes.Count > 0;
        sb.Append($"{same} checkpoints identical" + (differs ? "" : ": the replay matches the record"));
        return sb.ToString();
    }
}
