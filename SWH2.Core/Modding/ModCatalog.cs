using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using SWH2.Core;

namespace SWH2.Modding;

/// <summary>Finds installed mods (Mods\*\mod.json, plus legacy loose-file folders) and loads their plugins.</summary>
public static class ModCatalog
{
    public const string ManifestFile = "mod.json";

    /// <summary>Version of the loader / mod API (SWH2.Core assembly version).</summary>
    public static Version LoaderVersion => typeof(ModCatalog).Assembly.GetName().Version ?? new Version(1, 0);

    /// <summary>All mods sorted in the order they run. Broken manifests are reported via <paramref name="errors"/>.</summary>
    public static List<ModManifest> Discover(GameInstall game, Action<string>? errors = null)
    {
        var mods = new List<ModManifest>();
        foreach (var root in new[] { game.ModsDir, game.LegacyModsDir })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var folder = Path.GetFileName(dir);
                if (folder.StartsWith('.') || mods.Any(m => m.Id.Equals(folder, StringComparison.OrdinalIgnoreCase))) continue;
                var file = Path.Combine(dir, ManifestFile);
                ModManifest m;
                if (File.Exists(file))
                {
                    try
                    {
                        m = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(file), ModManifest.Json)
                            ?? throw new JsonException("empty file");
                    }
                    catch (Exception ex) when (ex is JsonException or IOException)
                    {
                        errors?.Invoke($"{folder}\\{ManifestFile}: {ex.Message}");
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(m.Id)) m.Id = folder;
                }
                else m = new ModManifest { Id = folder, Name = folder, Version = "", Files = ".", IsLegacy = true, Description = "Loose files (no mod.json)." };
                m.Dir = dir;
                if (Validate(m) is { } problem) { errors?.Invoke($"{folder}: {problem}"); continue; }
                mods.Add(m);
            }
        }
        return [.. mods.OrderBy(m => m.Order).ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)];
    }

    static string? Validate(ModManifest m)
    {
        if (m.MinLoaderVersion != null && Version.TryParse(m.MinLoaderVersion, out var need) && need > LoaderVersion)
            return $"needs mod loader {need} or newer (this is {LoaderVersion.ToString(3)}).";
        if (m.Plugin != null && !File.Exists(Path.Combine(m.Dir, m.Plugin)))
            return $"plugin {m.Plugin} is missing.";
        var dup = m.Settings.Where(s => s.Type != SettingType.Header).GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) return $"setting '{dup.Key}' is defined twice.";
        return null;
    }

    /// <summary>Entry path with "/" separators (some zip tools store "\\").</summary>
    static string EntryPath(ZipArchiveEntry e) => e.FullName.Replace('\\', '/');

    /// <summary>Reads the mod.json of a mod archive (.zip) without installing it.</summary>
    public static ModManifest ReadArchive(string zipPath) => OpenArchive(zipPath, out _, out _).Manifest;

    static (ModManifest Manifest, ZipArchive Zip) OpenArchive(string zipPath, out string prefix, out string id)
    {
        var zip = ZipFile.OpenRead(zipPath);
        try
        {
            // Accepted layouts: Mods/<Id>/mod.json (game-folder root, as on Nexus/Vortex), <Id>/mod.json, mod.json.
            var entry = zip.Entries
                .Where(e => e.Name.Equals(ManifestFile, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => EntryPath(e).Count(c => c == '/'))
                .FirstOrDefault()
                ?? throw new InvalidDataException($"{Path.GetFileName(zipPath)} has no {ManifestFile} — not an SWH2 mod archive.");
            using var s = entry.Open();
            var m = JsonSerializer.Deserialize<ModManifest>(s, ModManifest.Json) ?? throw new InvalidDataException("Empty mod.json.");
            var path = EntryPath(entry);
            prefix = path[..(path.LastIndexOf('/') + 1)];
            var folder = prefix.TrimEnd('/').Split('/').LastOrDefault();
            id = !string.IsNullOrWhiteSpace(m.Id) ? m.Id : !string.IsNullOrEmpty(folder) ? folder : Path.GetFileNameWithoutExtension(zipPath);
            if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id is "." or "..") throw new InvalidDataException($"Bad mod id '{id}'.");
            m.Id = id;
            return (m, zip);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Installs (or updates) a mod from a .zip into Mods\&lt;Id&gt;\. The old folder is replaced. Files outside the
    /// mod's folder in the archive are ignored. Returns the installed manifest.
    /// </summary>
    public static ModManifest InstallArchive(GameInstall game, string zipPath)
    {
        var (manifest, zip) = OpenArchive(zipPath, out var prefix, out var id);
        using (zip)
        {
            var dst = Path.Combine(game.ModsDir, id);
            var tmp = dst + ".installing";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            var root = Path.GetFullPath(tmp) + Path.DirectorySeparatorChar;
            foreach (var e in zip.Entries.Where(e => EntryPath(e).StartsWith(prefix, StringComparison.Ordinal) && !EntryPath(e).EndsWith('/')))
            {
                var target = Path.GetFullPath(Path.Combine(tmp, EntryPath(e)[prefix.Length..]));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                e.ExtractToFile(target, true);
            }
            if (Directory.Exists(dst)) Directory.Delete(dst, true);
            Directory.Move(tmp, dst);
            manifest.Dir = dst;
            return manifest;
        }
    }

    /// <summary>Deletes the mod's folder. The game keeps the mod's changes until the next install/restore.</summary>
    public static void Uninstall(ModManifest mod)
    {
        if (Directory.Exists(mod.Dir)) Directory.Delete(mod.Dir, true);
    }

    static readonly Dictionary<string, (DateTime Stamp, IModPlugin Plugin)> Loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Loads the mod's plugin. The dll is read from memory, so it isn't locked and can be updated while the loader
    /// runs (an updated dll is loaded again). Its references resolve first from the loader's folder (SWH2.Core and
    /// System.Text.Json etc. are always the loader's copies), then from the mod's own folder.
    /// </summary>
    public static IModPlugin LoadPlugin(ModManifest mod)
    {
        var path = Path.GetFullPath(Path.Combine(mod.Dir, mod.Plugin!));
        var stamp = File.GetLastWriteTimeUtc(path);
        lock (Loaded)
        {
            if (Loaded.TryGetValue(path, out var cached) && cached.Stamp == stamp) return cached.Plugin;
            HookResolve();
            lock (PluginDirs) if (!PluginDirs.Contains(mod.Dir, StringComparer.OrdinalIgnoreCase)) PluginDirs.Add(mod.Dir);
            var asm = Assembly.Load(File.ReadAllBytes(path));
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { throw new InvalidOperationException($"{mod.Id}: plugin failed to load: {ex.LoaderExceptions.FirstOrDefault()?.Message}"); }
            var type = types.SingleOrDefault(t => typeof(IModPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsPublic: true })
                       ?? throw new InvalidOperationException($"{mod.Id}: {mod.Plugin} has no public IModPlugin class (built against another SWH2.Core?).");
            var plugin = (IModPlugin)Activator.CreateInstance(type)!;
            Loaded[path] = (stamp, plugin);
            return plugin;
        }
    }

    static readonly List<string> PluginDirs = [];
    static bool _hooked;

    static void HookResolve()
    {
        if (_hooked) return;
        _hooked = true;
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var name = new AssemblyName(e.Name).Name;
            // Assemblies loaded from bytes aren't found by normal probing; reuse the copy that is already loaded.
            var loaded = AppDomain.CurrentDomain.GetAssemblies().LastOrDefault(a => a.GetName().Name == name);
            if (loaded != null) return loaded;
            lock (PluginDirs)
                foreach (var dir in PluginDirs)
                {
                    var file = Path.Combine(dir, name + ".dll");
                    if (File.Exists(file)) return Assembly.Load(File.ReadAllBytes(file));
                }
            return null;
        };
    }
}

/// <summary>Which mods are enabled and their settings; SWH2ModLoader\profile.json, shared by the GUI and the CLI.</summary>
public sealed class ModProfile
{
    public List<string> Enabled { get; set; } = [];
    public Dictionary<string, Dictionary<string, string>> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEnabled(string id) => Enabled.Contains(id, StringComparer.OrdinalIgnoreCase);

    public void SetEnabled(string id, bool on)
    {
        Enabled.RemoveAll(e => e.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (on) Enabled.Add(id);
    }

    public ModSettings SettingsFor(ModManifest mod) => new(mod, Settings.GetValueOrDefault(mod.Id));

    public void Store(ModManifest mod, ModSettings settings) =>
        Settings[mod.Id] = settings.Values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    public static ModProfile Load(GameInstall game)
    {
        try
        {
            if (File.Exists(game.ProfilePath))
            {
                var p = JsonSerializer.Deserialize<ModProfile>(File.ReadAllText(game.ProfilePath), ModManifest.Json) ?? new();
                p.Settings = new(p.Settings, StringComparer.OrdinalIgnoreCase);
                return p;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return new();
    }

    public void Save(GameInstall game)
    {
        Directory.CreateDirectory(game.ToolDir);
        File.WriteAllText(game.ProfilePath, JsonSerializer.Serialize(this, ModManifest.Json));
    }
}
