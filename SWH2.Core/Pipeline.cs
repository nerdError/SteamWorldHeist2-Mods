using SWH2.Core;
using SWH2.Modding;

namespace SWH2;

/// <summary>High-level operations shared by the GUI and the CLI.</summary>
public static class Pipeline
{
    /// <summary>
    /// Builds the modded game files from the originals with every enabled mod of the profile and installs them
    /// (or, with <paramref name="dryRunDir"/>, writes the changed files there and installs nothing).
    /// Empty seeds are filled in and stored back into <paramref name="profile"/>; the caller saves it.
    /// </summary>
    public static void Install(GameInstall game, ModProfile profile, Action<string> log, string? dryRunDir = null)
    {
        if (dryRunDir == null) game.MigrateLegacyLayout(log);
        var all = ModCatalog.Discover(game, e => log("Skipped mod " + e));
        var mods = all.Where(m => profile.IsEnabled(m.Id)).ToList();
        foreach (var missing in profile.Enabled.Where(id => !all.Any(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
            log($"Enabled mod '{missing}' is not installed, skipped.");
        foreach (var m in mods)
            foreach (var other in m.Conflicts.Where(c => mods.Any(x => x.Id.Equals(c, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException($"{m.Id} can't be used together with {other}; disable one of them.");

        var files = new GameFiles(game, log);
        if (dryRunDir == null) files.EnsureBackups();

        using var content = GameContent.OpenOriginal(files);
        content.Overridden += (path, by) => log($"  WARNING: {content.CurrentOwner} replaces {path}, discarding changes from {string.Join(", ", by)}.");
        ExeImage? exe = null;
        ExeImage Exe() => exe ??= new ExeImage(files.ReadOriginalExe());

        var options = new GameOptions();
        var reports = new List<(ModManifest Mod, ModContext Ctx)>();
        var summary = new List<string>();
        foreach (var mod in mods)
        {
            log($"--- {mod.Title}");
            var settings = profile.SettingsFor(mod);
            if (settings.FillSeeds()) log($"  New seed: {settings.Describe()}");
            profile.Store(mod, settings);

            content.CurrentOwner = mod.Id;
            if (exe != null) exe.CurrentOwner = mod.Id;
            ApplyLooseFiles(content, mod, log);

            var ctx = new ModContext(mod, settings, content, () => { var e = Exe(); e.CurrentOwner = mod.Id; return e; }, s => log("  " + s), options);
            if (mod.Plugin != null)
            {
                try { ModCatalog.LoadPlugin(mod).Apply(ctx); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException($"Mod {mod.Id} failed: {ex.Message}", ex);
                }
            }
            reports.Add((mod, ctx));
            var details = ctx.Summary ?? settings.Describe();
            summary.Add(details.Length > 0 ? $"{mod.Title} ({details})" : mod.Title);
        }
        content.CurrentOwner = null;
        if (mods.Count > 0) AddMainMenuModList(content, mods);
        options.Install(content, () => { var e = Exe(); e.CurrentOwner = "ModLoader"; return e; }, log);
        foreach (var (path, owners) in content.SharedFiles)
            log($"Note: {path} changed by {string.Join(" + ", owners)} (edits combined).");

        var changes = content.CollectChanges();
        int total = changes.Values.Sum(d => d.Count);
        var exeBytes = exe is { Modified: true } ? exe.ToArray() : null;
        var description = mods.Count > 0 ? string.Join("; ", summary) : null;

        if (dryRunDir != null)
        {
            Directory.CreateDirectory(dryRunDir);
            foreach (var (path, data) in changes.Values.SelectMany(d => d))
            {
                var dst = Path.Combine(dryRunDir, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.WriteAllBytes(dst, data);
            }
            if (exeBytes != null) File.WriteAllBytes(Path.Combine(dryRunDir, GameInstall.ExeName), exeBytes);
            foreach (var (mod, ctx) in reports.Where(r => !r.Ctx.Report.IsEmpty))
                File.WriteAllText(Path.Combine(dryRunDir, $"{mod.Id}_{ctx.ReportName}.txt"), ctx.Report.ToString());
            log($"Dry run: {total} changed file(s){(exeBytes != null ? " + patched exe" : "")} written to {dryRunDir}");
            return;
        }

        foreach (var pak in GameInstall.PakNames)
            files.InstallPak(pak, changes[pak], description);
        files.InstallExe(exeBytes, description);

        foreach (var (mod, ctx) in reports.Where(r => !r.Ctx.Report.IsEmpty))
        {
            var dir = Path.Combine(game.ReportsDir, mod.Id);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{ctx.ReportName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(file, ctx.Report.ToString());
            log($"{mod.Id} report: {file}");
        }
        log(total == 0 && exeBytes == null
            ? "Nothing to change — game files are original."
            : $"Done: {total} file(s) changed{(exeBytes != null ? ", exe patched" : "")}. Start a NEW save to see every change.");
    }

    /// <summary>
    /// The enabled mods (name + version) in medium text at the bottom right of the main menu: a panel inserted into the
    /// "main" panel of the ui.me template main_menu_start (fades with the buttons). Text insert, the rest of ui.me stays as it is.
    /// </summary>
    static void AddMainMenuModList(GameContent content, IReadOnlyList<ModManifest> mods)
    {
        const string file = "UI/ui.me";
        var xml = content.ReadText(file);
        int template = xml.IndexOf("Name=\"main_menu_start\"", StringComparison.Ordinal);
        int main = template < 0 ? -1 : xml.IndexOf("Name=\"main\"", template, StringComparison.Ordinal);
        int open = main < 0 ? -1 : xml.IndexOf('>', main) + 1;
        if (open <= 0 || xml.IndexOf("</Template>", template, StringComparison.Ordinal) < open)
            throw new InvalidOperationException($"{file}: main menu layout not found.");
        static string Esc(string s) => System.Security.SecurityElement.Escape(s);
        var indent = "\r\n\t\t\t\t\t\t";
        var labels = new[] { "Mods:" }.Concat(mods.Select(m => m.Title)).Select((t, i) =>
            $"{indent}<Label Name=\"modloader_mod_{i}\" Text=\"{Esc(t)}\" Style=\"ModLoaderModListLabel\" Anchor=\"East\" Width=\"1400\" Height=\"75\" />");
        var panel = $"\r\n\t\t\t\t\t<Panel Name=\"modloader_mods\" Anchor=\"SouthEast\" Margins=\"auto auto 30 30\" LayoutChildren=\"flow-vertical\">" +
                    string.Concat(labels) + "\r\n\t\t\t\t\t</Panel>";
        // One size up from the options' version label: body_medium_drop (like the options' items), right-aligned.
        const string style = "<Style Name=\"ModLoaderModListLabel\">\r\n\t\t\t<Font></Font>\r\n\t\t\t<TextSettings>label_generic_drop</TextSettings>\r\n" +
                             "\t\t\t<ContentAlignment>ne</ContentAlignment>\r\n\t\t</Style>\r\n\t\t";
        int styles = xml.IndexOf("<Style Name=\"OptionsMenuVersionLabelStyle\">", StringComparison.Ordinal);
        if (styles < 0 || styles > template) throw new InvalidOperationException($"{file}: OptionsMenuVersionLabelStyle not found.");
        xml = xml.Insert(open, panel).Insert(styles, style);   // back to front: offsets stay valid
        content.WriteText(file, xml, bom: content.ReadOriginal(file) is [0xEF, 0xBB, 0xBF, ..]);
    }

    /// <summary>
    /// Loose files from the mod's files folder (pak layout). Only files that differ from the originals count,
    /// so a mod folder may be a full extraction with a few edited files. Unknown files in the folder root
    /// (readme etc.) are not game files.
    /// </summary>
    static void ApplyLooseFiles(GameContent content, ModManifest mod, Action<string> log)
    {
        var dir = Path.GetFullPath(Path.Combine(mod.Dir, mod.Files));
        if (!Directory.Exists(dir)) return;
        int changed = 0, added = 0, same = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = GameContent.Normalize(PathEx.GetRelativePath(dir, file));
            bool exists = content.ExistsInOriginal(rel);
            if (!exists && !rel.Contains('/')) continue;
            if (mod.Plugin != null && Path.GetFullPath(file) == Path.GetFullPath(Path.Combine(mod.Dir, mod.Plugin))) continue;

            var data = File.ReadAllBytes(file);
            if (exists && content.ReadOriginal(rel).AsSpan().SequenceEqual(data)) { same++; continue; }
            content.Write(rel, data);
            if (exists) changed++; else added++;
        }
        log($"  Files: {changed} changed, {added} new, {same} unchanged.");
    }

    public static void Restore(GameInstall game, Action<string> log)
    {
        game.MigrateLegacyLayout(log);
        new GameFiles(game, log).RestoreOriginals();
    }
    /// <summary>Extracts the ORIGINAL paks (from backup when the game is modded).</summary>
    public static void Extract(GameInstall game, string outDir, bool includeAssets, Action<string> log)
    {
        var paks = new GameFiles(game, log);
        using var content = GameContent.OpenOriginal(paks);
        int n = 0;
        foreach (var path in content.OriginalFiles)
        {
            if (!includeAssets && content.PakOf(path) != GameInstall.PakNames[0]) continue;
            var dst = Path.Combine(outDir, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.WriteAllBytes(dst, content.ReadOriginal(path));
            n++;
        }
        log($"Extracted {n} file(s) to {outDir}");
    }

    /// <summary>Parses and re-saves every XML file unchanged and reports any byte differences.</summary>
    public static int XmlRoundTripCheck(GameInstall game, Action<string> log)
    {
        using var content = GameContent.OpenOriginal(new GameFiles(game, log));
        int ok = 0, diff = 0, failed = 0;
        foreach (var path in content.OriginalFiles.Where(f => f.StartsWith("Definitions/") && (f.EndsWith(".xml") || f.EndsWith(".de"))))
        {
            var original = content.ReadOriginal(path);
            try
            {
                // 1. Unmodified load + save must be byte-identical.
                var again = XmlDoc.Parse(path, original).Serialize();
                if (!again.AsSpan().SequenceEqual(original))
                {
                    diff++;
                    int i = 0;
                    while (i < Math.Min(again.Length, original.Length) && again[i] == original[i]) i++;
                    string Around(byte[] b) => System.Text.Encoding.UTF8.GetString(b, Math.Max(0, i - 40), Math.Min(80, b.Length - Math.Max(0, i - 40))).Replace("\r", "\\r").Replace("\n", "\\n");
                    log($"DIFF {path} at byte {i} (len {original.Length} -> {again.Length})\n   orig: {Around(original)}\n   ours: {Around(again)}");
                    continue;
                }

                // 2. Edits the randomizer makes (attribute values, new attributes, simple text values)
                //    must be patched in place and read back exactly as edited.
                var doc = XmlDoc.Parse(path, original);
                int n = 0;
                foreach (var el in doc.Root.Descendants().ToList())
                {
                    n++;
                    if (n % 5 == 0 && el.Attributes().FirstOrDefault() is { } a) a.Value += "_<&\"x";
                    if (n % 11 == 0) el.SetAttributeValue("SwhTest", "1");
                    if (n % 7 == 0 && !el.HasElements && int.TryParse(el.Value, out var v)) el.Value = (v + 1).ToString();
                }
                var patched = doc.Serialize();
                var reread = System.Xml.Linq.XDocument.Parse(System.Text.Encoding.UTF8.GetString(patched).TrimStart('\uFEFF'), System.Xml.Linq.LoadOptions.PreserveWhitespace);
                if (doc.LastSaveWasFullRewrite) { diff++; log($"PATCH {path}: fell back to full rewrite"); }
                else if (!System.Xml.Linq.XNode.DeepEquals(reread.Root, doc.Root)) { diff++; log($"PATCH {path}: edited content differs after re-reading"); }
                else ok++;
            }
            catch (Exception ex)
            {
                failed++;
                log($"FAIL {path}: {ex.Message}");
            }
        }
        log($"Round trip: {ok} identical, {diff} different, {failed} failed to parse.");
        return diff + failed;
    }
}
