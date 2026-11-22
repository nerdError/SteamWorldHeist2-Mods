using SWH2;
using SWH2.Core;
using SWH2.Modding;

const string Usage = """
swh2 — SteamWorld Heist 2 mod loader

Usage:
  swh2 status                      game files + installed mods
  swh2 mods                        list mods, enabled state and settings
  swh2 add     MOD.zip [...]       install or update a mod from its archive (enables new mods)
  swh2 remove  ID                  delete a mod's folder (run install afterwards to undo its changes)
  swh2 enable  ID [ID...]          enable mods in the profile (takes effect on install)
  swh2 disable ID [ID...]
  swh2 set     ID key=value [...]  change a mod's settings, e.g. swh2 set Randomizer seed=123 damage=on
  swh2 install [--only A,B] [--set ID.key=value]... [--dry-run OUTDIR]
               builds the game files from the originals with every enabled mod and installs them.
               --only / --set override the profile for this run without saving it.
               --dry-run writes the changed files (+ patched exe, reports) to OUTDIR instead.
  swh2 restore                     put the original game files back
  swh2 extract OUTDIR [--assets]   unpack the original game data
  swh2 saves                       save slots and save banks (copies of slots kept outside the game)
  swh2 saves store SLOT NAME [--overwrite]   copy slot 1..N into bank NAME
  swh2 saves load  NAME SLOT       put bank NAME into a slot (a used slot is kept as an "auto …" bank first)
  swh2 saves rename NAME NEW  |  swh2 saves delete NAME
               --account ID picks the Steam account (default: the one that saved last)
  swh2 roundtrip                   developer check: XML load/save is lossless
  swh2 <command> ...               commands added by installed mods (listed below), e.g. swh2 cheat win

Common option: --game DIR (default: the game folder the loader sits in, else auto-detected from Steam).
Mods live in <game>\Mods\<Id>\ (mod.json + plugin and/or files\ in pak layout).
The profile (enabled mods + settings) is <game>\SWH2ModLoader\profile.json, shared with the GUI.
""";

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    if (GameInstall.TryDetect() is { } detected)
    {
        var commands = ModCatalog.Discover(new GameInstall(detected)).Where(m => m.Command != null).ToList();
        if (commands.Count > 0) Console.WriteLine("Mod commands:");
        foreach (var m in commands) Console.WriteLine($"  {m.CommandHelp ?? "swh2 " + m.Command}    ({m.Title})");
    }
    return 0;
}

var opts = ParseOptions(args.Skip(1).ToArray(), out var positional, out var sets);
void Log(string s) => Console.WriteLine(s);

try
{
    var gamePath = opts.GetValueOrDefault("game") ?? GameInstall.TryDetect();
    if (!GameInstall.IsValid(gamePath))
    {
        Console.Error.WriteLine("Game folder not found. Pass --game \"C:\\...\\SteamWorld Heist 2\".");
        return 2;
    }
    var game = new GameInstall(gamePath!);
    var profile = ModProfile.Load(game);

    List<ModManifest> Mods() => ModCatalog.Discover(game, e => Console.Error.WriteLine("Skipped mod " + e));
    ModManifest FindMod(List<ModManifest> all, string id) =>
        all.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"No mod '{id}'. Installed: {string.Join(", ", all.Select(m => m.Id))}");

    switch (args[0])
    {
        case "status":
            Log($"Game: {game.Root}");
            Log($"Loader: {ModCatalog.LoaderVersion.ToString(3)}");
            Log(new GameFiles(game).DescribeStatus());
            Log("Enabled: " + (profile.Enabled.Count > 0 ? string.Join(", ", profile.Enabled) : "(none)"));
            return 0;

        case "mods":
            foreach (var m in Mods())
            {
                Log($"[{(profile.IsEnabled(m.Id) ? "x" : " ")}] {m.Id,-14} {m.Version,-8} {m.Name}{(m.PatchesExe ? "  (patches exe)" : "")}{(m.IsLegacy ? "  (loose files)" : "")}");
                var s = profile.SettingsFor(m);
                foreach (var def in m.Settings.Where(d => d.Type != SettingType.Header))
                    Log($"      {def.Id} = {s.String(def.Id)}{(def.Choices != null ? "  [" + string.Join("|", def.Choices.Select(c => c.Value)) + "]" : "")}   {def.DisplayLabel}");
            }
            return 0;

        case "add":
            if (positional.Count == 0) throw new ArgumentException("add: MOD.zip required");
            foreach (var zip in positional)
            {
                bool existed = Mods().Any(m => m.Id.Equals(ModCatalog.ReadArchive(zip).Id, StringComparison.OrdinalIgnoreCase));
                var installed = ModCatalog.InstallArchive(game, zip);
                if (!existed) profile.SetEnabled(installed.Id, true);
                Log($"{(existed ? "Updated" : "Installed and enabled")} {installed.Title}.");
            }
            profile.Save(game);
            Log("Run 'swh2 install' to apply.");
            return 0;

        case "remove":
        {
            if (positional.Count != 1) throw new ArgumentException("remove: mod id required");
            var m = FindMod(Mods(), positional[0]);
            ModCatalog.Uninstall(m);
            profile.SetEnabled(m.Id, false);
            profile.Settings.Remove(m.Id);
            profile.Save(game);
            Log($"Removed {m.Title}. Run 'swh2 install' to remove its changes from the game.");
            return 0;
        }

        case "enable" or "disable":
        {
            if (positional.Count == 0) throw new ArgumentException($"{args[0]}: mod id required");
            var all = Mods();
            foreach (var id in positional)
            {
                var m = FindMod(all, id);
                profile.SetEnabled(m.Id, args[0] == "enable");
                Log($"{m.Id}: {args[0]}d");
            }
            profile.Save(game);
            Log("Run 'swh2 install' to apply.");
            return 0;
        }

        case "set":
        {
            if (positional.Count < 2) throw new ArgumentException("set: ID key=value required");
            var m = FindMod(Mods(), positional[0]);
            var s = profile.SettingsFor(m);
            foreach (var kv in positional.Skip(1))
            {
                var (k, v) = SplitKv(kv);
                s.Set(k, v);
            }
            profile.Store(m, s);
            profile.Save(game);
            Log($"{m.Id}: {s.Describe()}");
            return 0;
        }

        case "install":
        {
            bool adHoc = opts.ContainsKey("only") || sets.Count > 0;
            var all = Mods();
            if (opts.TryGetValue("only", out var only))
                profile.Enabled = only.SplitTrim(',').Select(id => FindMod(all, id).Id).ToList();
            foreach (var set in sets)
            {
                var (key, value) = SplitKv(set);
                int dot = key.IndexOf('.');
                if (dot < 0) throw new ArgumentException($"--set {set}: expected ID.key=value");
                var m = FindMod(all, key[..dot]);
                var s = profile.SettingsFor(m);
                s.Set(key[(dot + 1)..], value);
                profile.Store(m, s);
            }
            var dry = opts.GetValueOrDefault("dry-run");
            if (dry == null && GameRunning()) { Console.Error.WriteLine("Close the game first — its files are in use."); return 1; }
            Pipeline.Install(game, profile, Log, dry);
            if (dry == null && !adHoc) profile.Save(game); // keeps generated seeds
            return 0;
        }

        case "restore":
            if (GameRunning()) { Console.Error.WriteLine("Close the game first — its files are in use."); return 1; }
            Pipeline.Restore(game, Log);
            return 0;

        case "extract":
            if (positional.Count == 0) { Console.Error.WriteLine("extract: OUTDIR required"); return 2; }
            Pipeline.Extract(game, positional[0], opts.ContainsKey("assets"), Log);
            return 0;

        case "saves":
            return Saves(game, profile, positional, opts, Log);

        case "roundtrip":
            return Pipeline.XmlRoundTripCheck(game, Log) == 0 ? 0 : 1;

        default:
        {
            // Commands provided by mods (mod.json "command"), e.g. "swh2 cheat win" from CheatMenu.
            var mod = Mods().FirstOrDefault(m => string.Equals(m.Command, args[0], StringComparison.OrdinalIgnoreCase));
            if (mod?.Plugin != null && ModCatalog.LoadPlugin(mod) is IModCommand command)
                return command.RunCommand(positional, new ModToolContext(mod, game, profile.SettingsFor(mod), Log));
            Console.Error.WriteLine($"Unknown command '{args[0]}'.\n\n{Usage}");
            return 2;
        }
    }
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}

static int Saves(GameInstall game, ModProfile profile, List<string> args, Dictionary<string, string> opts, Action<string> log)
{
    var accounts = SaveBanks.Accounts();
    var banks = opts.TryGetValue("account", out var id)
        ? accounts.FirstOrDefault(a => a.AccountId == id) ?? throw new ArgumentException($"No saves for account {id}. Found: {string.Join(", ", accounts.Select(a => a.AccountId))}")
        : accounts.FirstOrDefault() ?? throw new ArgumentException($"No saves found in {SaveBanks.SavesRoot}.");
    int Slot(string s) => int.TryParse(s, out int n) && n >= 1 ? n - 1 : throw new ArgumentException($"'{s}': slot number expected (1, 2, 3…)");
    void NeedClosed() { if (GameRunning()) throw new InvalidOperationException("Close the game first — it would write over the slot."); }
    string sub = args.Count > 0 ? args[0] : "list";
    switch (sub)
    {
        case "list":
            log($"Account {banks.AccountId}: {banks.SaveDir}");
            foreach (var s in banks.Slots(profile.SaveSlots(game)))
                log($"  slot {s.Index + 1,2}  {(s.Used ? $"{s.Modified:yyyy-MM-dd HH:mm}  {s.Size / 1024} KB" : "(empty)")}");
            log($"Banks: {banks.BanksDir}");
            var list = banks.Banks();
            if (list.Count == 0) log("  (none)");
            foreach (var b in list)
                log($"  {b.Saved:yyyy-MM-dd HH:mm}  {b.Name}{(b.FromSlot is { } f ? $"  (from slot {f + 1})" : "")}");
            return 0;
        case "store" when args.Count == 3:
        {
            var b = banks.Store(Slot(args[1]), args[2], opts.ContainsKey("overwrite"));
            log($"Slot {args[1]} saved as bank '{b.Name}'.");
            return 0;
        }
        case "load" when args.Count == 3:
        {
            NeedClosed();
            var auto = banks.Load(args[1], Slot(args[2]));
            if (auto != null) log($"Slot {args[2]} was kept as bank '{auto.Name}'.");
            log($"Bank '{args[1]}' is now in slot {args[2]}.");
            return 0;
        }
        case "rename" when args.Count == 3:
            log($"Renamed to '{banks.Rename(args[1], args[2]).Name}'.");
            return 0;
        case "delete" when args.Count == 2:
            banks.Delete(args[1]);
            log($"Deleted bank '{args[1]}'.");
            return 0;
        default:
            throw new ArgumentException("saves: list | store SLOT NAME | load NAME SLOT | rename NAME NEW | delete NAME");
    }
}

static bool GameRunning() => System.Diagnostics.Process.GetProcessesByName("SteamWorld Heist 2").Length > 0;

static (string Key, string Value) SplitKv(string kv)
{
    int eq = kv.IndexOf('=');
    if (eq <= 0) throw new ArgumentException($"'{kv}': expected key=value");
    return (kv[..eq].Trim(), kv[(eq + 1)..]);
}

static Dictionary<string, string> ParseOptions(string[] args, out List<string> positional, out List<string> sets)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    positional = [];
    sets = [];
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) { positional.Add(args[i]); continue; }
        var key = args[i][2..];
        bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
        var value = hasValue ? args[++i] : "true";
        if (key.Equals("set", StringComparison.OrdinalIgnoreCase)) sets.Add(value);
        else result[key] = value;
    }
    return result;
}
