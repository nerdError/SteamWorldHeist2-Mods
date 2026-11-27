namespace SWH2.Core;

/// <summary>
/// Save banks: copies of single save slots kept outside the game, so any number of runs can be swapped into the
/// game's slots. The game keeps slot N in
///   %APPDATA%\Thunderful\SteamWorld Heist II\&lt;steam account id&gt;\Save000\savegame_{N:000}.dat (+ .dat.backup);
/// banks live in %APPDATA%\SWH2ModLoader\SaveBanks\&lt;account id&gt;\&lt;name&gt;\ (outside the Steam Cloud folder).
/// Slot numbers in this API are 0-based like the files; show them to players as N+1.
/// </summary>
public sealed class SaveBanks
{
    public const int GameSlots = 3;
    const string Dat = "savegame.dat", Backup = "savegame.dat.backup", Info = "bank.txt";
    const string AutoPrefix = "auto ";
    const int KeepAuto = 20;

    /// <summary>%APPDATA%; SWH2_APPDATA overrides it so tests can run on a copy of the saves.</summary>
    static string AppData =>
        Environment.GetEnvironmentVariable("SWH2_APPDATA") is { Length: > 0 } dir ? dir : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string SavesRoot => Path.Combine(AppData, "Thunderful", "SteamWorld Heist II");
    static string BanksRoot => Path.Combine(AppData, "SWH2ModLoader", "SaveBanks");

    SaveBanks(string accountId, string? saveDir = null)
    {
        AccountId = accountId;
        SaveDir = saveDir ?? Path.Combine(SavesRoot, accountId, "Save000");
        BanksDir = Path.Combine(BanksRoot, accountId);
    }

    /// <summary>
    /// The saves a game copy uses: Steam = the most recently saved account; GOG = &lt;saves&gt;\Save000 (no account folder),
    /// banks under "gog". Null when a Steam copy has no saves yet.
    /// </summary>
    public static SaveBanks? ForGame(GameInstall game) =>
        game.Store == "GOG" ? new SaveBanks("gog", Path.Combine(SavesRoot, "Save000")) : Accounts().FirstOrDefault();

    public string AccountId { get; }
    public string SaveDir { get; }
    public string BanksDir { get; }

    /// <summary>Steam accounts that have played the game, most recently saved first.</summary>
    public static List<SaveBanks> Accounts()
    {
        if (!Directory.Exists(SavesRoot)) return [];
        return Directory.GetDirectories(SavesRoot)
            .Select(Path.GetFileName)
            .Where(id => id!.All(char.IsDigit) && Directory.Exists(Path.Combine(SavesRoot, id, "Save000")))
            .Select(id => new SaveBanks(id!))
            .OrderByDescending(a => Directory.GetLastWriteTimeUtc(a.SaveDir))
            .ToList();
    }

    public sealed record Slot(int Index, DateTime? Modified, long Size)
    {
        public bool Used => Modified != null;
    }

    public sealed record Bank(string Name, string Dir, DateTime Saved, int? FromSlot)
    {
        public bool IsAuto => Name.StartsWith(AutoPrefix, StringComparison.Ordinal);
    }

    string SlotFile(int slot) => Path.Combine(SaveDir, $"savegame_{slot:000}.dat");
    string SlotBackup(int slot) => SlotFile(slot) + ".backup";

    /// <summary>Slots 0..count-1, plus any higher slot that has a file (e.g. from More Save Slots).</summary>
    public List<Slot> Slots(int count = GameSlots)
    {
        if (Directory.Exists(SaveDir))
            foreach (var f in Directory.GetFiles(SaveDir, "savegame_*.dat"))
                if (int.TryParse(Path.GetFileNameWithoutExtension(f)["savegame_".Length..], out int n)) count = Math.Max(count, n + 1);
        return Enumerable.Range(0, count).Select(i =>
        {
            var f = new FileInfo(SlotFile(i));
            return f.Exists ? new Slot(i, f.LastWriteTime, f.Length) : new Slot(i, null, 0);
        }).ToList();
    }

    public List<Bank> Banks()
    {
        if (!Directory.Exists(BanksDir)) return [];
        return Directory.GetDirectories(BanksDir)
            .Where(d => File.Exists(Path.Combine(d, Dat)))
            .Select(ReadBank)
            .OrderByDescending(b => b.Saved)
            .ToList();
    }

    static Bank ReadBank(string dir)
    {
        DateTime saved = File.GetLastWriteTime(Path.Combine(dir, Dat));
        int? from = null;
        var info = Path.Combine(dir, Info);
        if (File.Exists(info))
            foreach (var line in File.ReadAllLines(info))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                var (k, v) = (line[..eq].Trim(), line[(eq + 1)..].Trim());
                if (k == "slot" && int.TryParse(v, out int s)) from = s;
                if (k == "saved" && DateTime.TryParse(v, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)) saved = t.ToLocalTime();
            }
        return new Bank(Path.GetFileName(dir), dir, saved, from);
    }

    public Bank? Find(string name) => Banks().FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A folder-safe bank name, or throws if nothing is left.</summary>
    public static string CleanName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length == 0) throw new ArgumentException("Bank name is empty.");
        return clean;
    }

    /// <summary>Copies a slot into a bank. Overwrites an existing bank of that name only when asked.</summary>
    public Bank Store(int slot, string name, bool overwrite = false)
    {
        name = CleanName(name);
        if (!File.Exists(SlotFile(slot))) throw new InvalidOperationException($"Slot {slot + 1} is empty.");
        var dir = Path.Combine(BanksDir, name);
        if (Directory.Exists(dir))
        {
            if (!overwrite) throw new InvalidOperationException($"Bank '{name}' already exists.");
            Directory.Delete(dir, true);
        }
        // Copy into a temp folder first so a failed copy never leaves half a bank.
        var tmp = dir + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        File.Copy(SlotFile(slot), Path.Combine(tmp, Dat));
        if (File.Exists(SlotBackup(slot))) File.Copy(SlotBackup(slot), Path.Combine(tmp, Backup));
        File.WriteAllLines(Path.Combine(tmp, Info), [$"slot={slot}", $"saved={DateTime.UtcNow:o}"]);
        Directory.Move(tmp, dir);
        return ReadBank(dir);
    }

    /// <summary>
    /// Puts a bank into a slot. A used slot is first stored as an "auto …" bank (returned), so nothing is lost.
    /// The game must be closed: it keeps the slots in memory and would write over the file.
    /// </summary>
    public Bank? Load(string name, int slot)
    {
        var bank = Find(name) ?? throw new InvalidOperationException($"No bank '{name}'.");
        Bank? auto = null;
        if (File.Exists(SlotFile(slot)))
        {
            auto = Store(slot, $"{AutoPrefix}{DateTime.Now:yyyy-MM-dd HH-mm-ss} slot {slot + 1}", overwrite: true);
            PruneAuto();
        }
        Directory.CreateDirectory(SaveDir);
        File.Copy(Path.Combine(bank.Dir, Dat), SlotFile(slot), true);
        // The game falls back to .backup when the main file is broken: never leave another run's backup next to it.
        var bak = Path.Combine(bank.Dir, Backup);
        if (File.Exists(bak)) File.Copy(bak, SlotBackup(slot), true);
        else File.Delete(SlotBackup(slot));
        return auto;
    }

    /// <summary>
    /// Puts a save file's bytes into a slot (e.g. one received over the network); a used slot is first stored as an
    /// "auto …" bank (returned). The game must be closed.
    /// </summary>
    public Bank? Put(int slot, byte[] dat)
    {
        Bank? auto = null;
        if (File.Exists(SlotFile(slot)))
        {
            auto = Store(slot, $"{AutoPrefix}{DateTime.Now:yyyy-MM-dd HH-mm-ss} slot {slot + 1}", overwrite: true);
            PruneAuto();
        }
        Directory.CreateDirectory(SaveDir);
        File.WriteAllBytes(SlotFile(slot) + ".tmp", dat);
        File.Copy(SlotFile(slot) + ".tmp", SlotFile(slot), true);
        File.Delete(SlotFile(slot) + ".tmp");
        File.Delete(SlotBackup(slot));
        return auto;
    }

    public string SlotPath(int slot) => SlotFile(slot);

    public void Delete(string name)
    {
        var bank = Find(name) ?? throw new InvalidOperationException($"No bank '{name}'.");
        Directory.Delete(bank.Dir, true);
    }

    public Bank Rename(string name, string newName)
    {
        var bank = Find(name) ?? throw new InvalidOperationException($"No bank '{name}'.");
        newName = CleanName(newName);
        var dst = Path.Combine(BanksDir, newName);
        if (string.Equals(bank.Dir, dst, StringComparison.OrdinalIgnoreCase))
        {
            // Only the case changes: Windows needs a detour.
            var tmp = dst + ".rename";
            Directory.Move(bank.Dir, tmp);
            Directory.Move(tmp, dst);
        }
        else
        {
            if (Directory.Exists(dst)) throw new InvalidOperationException($"Bank '{newName}' already exists.");
            Directory.Move(bank.Dir, dst);
        }
        return ReadBank(dst);
    }

    void PruneAuto()
    {
        foreach (var old in Banks().Where(b => b.IsAuto).Skip(KeepAuto))
            Directory.Delete(old.Dir, true);
    }
}
