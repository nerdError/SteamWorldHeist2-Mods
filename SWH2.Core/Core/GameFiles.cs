using System.Security.Cryptography;
using System.Text.Json;

namespace SWH2.Core;

/// <summary>What the loader has done to the game folder; persisted as SWH2ModLoader/state.json.</summary>
public sealed class InstallState
{
    /// <summary>SHA-256 of each file this tool wrote (paks, exe), so we can tell our files from a game update.</summary>
    public Dictionary<string, string> InstalledHashes { get; set; } = new();
    /// <summary>SHA-256 of each backed-up original.</summary>
    public Dictionary<string, string> BackupHashes { get; set; } = new();
    public string? Description { get; set; }
    public DateTime? InstalledAt { get; set; }
}

/// <summary>
/// Owns the game files mods change — Game.pak, Game2.pak and the exe: keeps pristine backups, writes modified
/// versions and restores originals. Every modified file is always rebuilt from the backup, so applying twice
/// never stacks changes.
/// </summary>
public sealed class GameFiles
{
    const string Exe = GameInstall.ExeName;

    readonly GameInstall _game;
    readonly Action<string> _log;
    InstallState _state;

    public GameFiles(GameInstall game, Action<string>? log = null)
    {
        _game = game;
        _log = log ?? (_ => { });
        _state = LoadState();
    }

    public InstallState State => _state;

    /// <summary>The unmodified version of a pak: the backup if we have one, otherwise the live file.</summary>
    public string OriginalPakPath(string pak)
    {
        var bak = _game.BackupPath(pak);
        if (File.Exists(bak)) return bak;
        if (_state.InstalledHashes.ContainsKey(pak))
            throw new InvalidOperationException(
                $"{pak} was modified by this tool but its backup is missing. " +
                "Use Steam → Properties → Installed Files → Verify integrity of game files.");
        return _game.PakPath(pak);
    }

    public string DescribeStatus()
    {
        var lines = new List<string>();
        foreach (var pak in GameInstall.PakNames)
        {
            var hash = Hash(_game.PakPath(pak));
            string status;
            if (_state.InstalledHashes.TryGetValue(pak, out var inst) && inst == hash) status = "modded";
            else if (_state.BackupHashes.TryGetValue(pak, out var bak) && bak == hash) status = "original";
            else if (!File.Exists(_game.BackupPath(pak))) status = "original (no backup yet)";
            else status = "changed outside the loader (game update?) — backup will be refreshed on next apply";
            lines.Add($"{pak}: {status}");
        }
        lines.Add($"{Exe}: {DescribeExe()}");
        if (_state.InstalledHashes.Count > 0 && _state.Description != null)
            lines.Add($"Installed: {_state.Description} ({_state.InstalledAt:yyyy-MM-dd HH:mm})");
        return string.Join(Environment.NewLine, lines);
    }

    string DescribeExe()
    {
        var hash = Hash(_game.ExePath);
        if (_state.InstalledHashes.TryGetValue(Exe, out var inst) && inst == hash) return "patched by mods";
        if (hash == ExeImage.SupportedSha256) return "original";
        if (IsLegacyPatch()) return "patched by the old exe_patch.py (will be replaced on next apply)";
        return "unknown version — exe mods unavailable";
    }

    /// <summary>Makes sure SWH2ModLoader/Backup holds the current original of every pak.</summary>
    public void EnsureBackups()
    {
        Directory.CreateDirectory(_game.BackupDir);
        foreach (var pak in GameInstall.PakNames)
        {
            string cur = _game.PakPath(pak), bak = _game.BackupPath(pak);
            var curHash = Hash(cur);
            _state.InstalledHashes.TryGetValue(pak, out var installed);

            if (!File.Exists(bak))
            {
                if (installed == curHash)
                    throw new InvalidOperationException(
                        $"{pak} is modified by this tool but its backup is missing. Verify game files in Steam first.");
                File.Copy(cur, bak, true);
                _state.BackupHashes[pak] = curHash;
                _state.InstalledHashes.Remove(pak);
                _log($"Backed up original {pak}.");
                continue;
            }

            if (!_state.BackupHashes.TryGetValue(pak, out var bakHash))
                _state.BackupHashes[pak] = bakHash = Hash(bak);

            if (curHash == bakHash)
            {
                _state.InstalledHashes.Remove(pak); // e.g. Steam "verify files" put the original back
                continue;
            }
            if (curHash == installed) continue;

            _log($"{pak} was changed outside this tool (probably a game update) — refreshing the backup.");
            File.Copy(cur, bak, true);
            _state.BackupHashes[pak] = curHash;
            _state.InstalledHashes.Remove(pak);
        }
        SaveState();
    }

    /// <summary>
    /// Rebuilds a pak from its backup with the given files replaced or added.
    /// An empty change set restores the original pak.
    /// </summary>
    public void InstallPak(string pak, IReadOnlyDictionary<string, byte[]> changes, string? description = null)
    {
        string bak = _game.BackupPath(pak), cur = _game.PakPath(pak);
        if (!File.Exists(bak)) throw new InvalidOperationException($"No backup of {pak}; call EnsureBackups first.");

        if (changes.Count == 0)
        {
            if (_state.InstalledHashes.Remove(pak))
            {
                File.Copy(bak, cur, true);
                _log($"{pak}: no changes, original restored.");
            }
            SaveState();
            return;
        }

        // ".tmp" suffix keeps the half-written file out of the engine's "*.pak" mount pattern.
        var tmp = cur + ".tmp";
        ZipRewriter.Rewrite(bak, tmp, changes);
        PathEx.MoveOverwrite(tmp, cur);

        _state.InstalledHashes[pak] = Hash(cur);
        _state.InstalledAt = DateTime.Now;
        if (description != null) _state.Description = description;
        SaveState();
        _log($"{pak}: written with {changes.Count} changed file(s).");
    }

    // ---- exe ------------------------------------------------------------------------------

    /// <summary>
    /// The pristine exe for mods to patch. Throws when the installed game isn't the supported build
    /// (exe mods only work with known addresses); pak mods are not affected by this.
    /// </summary>
    public byte[] ReadOriginalExe()
    {
        var bak = _game.BackupPath(Exe);
        var data = File.ReadAllBytes(File.Exists(bak) ? bak : _game.ExePath);
        if (!ExeImage.IsSupported(data))
            throw new NotSupportedException(File.Exists(bak)
                ? $"The backed-up exe ({bak}) is not the supported game build, so exe mods are unavailable."
                : $"{Exe} is not the original supported build (already patched by another tool, or a different version). " +
                  "Verify game files in Steam to use exe mods.");
        return data;
    }

    /// <summary>Writes a patched exe (null = put the original back if the loader had patched it).</summary>
    public void InstallExe(byte[]? patched, string? description = null)
    {
        if (patched == null)
        {
            if (_state.InstalledHashes.ContainsKey(Exe) || IsLegacyPatch())
            {
                var bak = _game.BackupPath(Exe);
                if (!File.Exists(bak)) { _log($"{Exe}: backup missing! Verify game files in Steam to restore it."); return; }
                if (Hash(_game.ExePath) != Hash(bak))
                {
                    WriteInPlace(_game.ExePath, File.ReadAllBytes(bak));
                    _log($"{Exe}: original restored.");
                }
                _state.InstalledHashes.Remove(Exe);
                SaveState();
            }
            return;
        }

        var backup = _game.BackupPath(Exe);
        if (!File.Exists(backup))
        {
            Directory.CreateDirectory(_game.BackupDir);
            File.Copy(_game.ExePath, backup);  // ReadOriginalExe already checked that the live exe is the original
            _log($"Backed up original {Exe}.");
        }
        _state.BackupHashes[Exe] = ExeImage.SupportedSha256;
        WriteInPlace(_game.ExePath, patched);
        _state.InstalledHashes[Exe] = Hash(_game.ExePath);
        _state.InstalledAt = DateTime.Now;
        if (description != null) _state.Description = description;
        SaveState();
        _log($"{Exe}: patched.");
    }

    /// <summary>The exe was patched by tools/re/exe_patch.py before the loader managed it (its sections/bytes are known).</summary>
    bool IsLegacyPatch()
    {
        var bak = _game.BackupPath(Exe);
        if (!File.Exists(bak) || !File.Exists(_game.ExePath) || Hash(bak) != ExeImage.SupportedSha256) return false;
        var cur = File.ReadAllBytes(_game.ExePath);
        if (ExeImage.IsSupported(cur)) return false;
        try
        {
            var img = new ExeImage(cur);
            return img.HasSection(".swh2") || cur.AsSpan(img.OffsetOf(0xC0F80), 3).SequenceEqual(new byte[] { 0x30, 0xC0, 0xC3 });
        }
        catch (Exception) { return false; }
    }

    /// <summary>Overwrites a file without recreating it (recreating the exe fails in some setups).</summary>
    static void WriteInPlace(string path, byte[] data)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Write(data);
        fs.SetLength(data.Length);
    }

    /// <summary>Puts every original file back.</summary>
    public void RestoreOriginals()
    {
        foreach (var pak in GameInstall.PakNames)
        {
            var bak = _game.BackupPath(pak);
            if (!File.Exists(bak))
            {
                if (_state.InstalledHashes.ContainsKey(pak))
                    _log($"{pak}: backup missing! Verify game files in Steam to restore it.");
                continue;
            }
            if (Hash(_game.PakPath(pak)) != (_state.BackupHashes.GetValueOrDefault(pak) ?? Hash(bak)))
            {
                File.Copy(bak, _game.PakPath(pak), true);
                _log($"{pak}: original restored.");
            }
            else _log($"{pak}: already original.");
            _state.InstalledHashes.Remove(pak);
        }
        InstallExe(null);
        _state.Description = null;
        _state.InstalledAt = null;
        SaveState();
    }

    public static string Hash(string path)
    {
        if (!File.Exists(path)) return "";
        using var fs = File.OpenRead(path);
        return Hex.Sha256(fs);
    }

    InstallState LoadState()
    {
        try
        {
            if (File.Exists(_game.StatePath))
                return JsonSerializer.Deserialize<InstallState>(File.ReadAllText(_game.StatePath)) ?? new();
        }
        catch (JsonException) { }
        return new();
    }

    void SaveState()
    {
        Directory.CreateDirectory(_game.ToolDir);
        File.WriteAllText(_game.StatePath, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
    }
}
