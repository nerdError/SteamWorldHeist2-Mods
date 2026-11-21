using System.Xml.Linq;
using SWH2.Core;

namespace SWH2.Mods.Randomizer;

/// <summary>Things that change how the game looks and sounds, not how it plays: music, mission lighting, names.</summary>
public static class FunShuffle
{
    const string RegionsFile = "Definitions/worldmap_regions.xml";
    const string MusicListsFile = "Definitions/music_lists.xml";

    /// <summary>
    /// Music: mission music (missions*.de Mission/Music), the regions' world map music (worldmap_regions.xml Region/Music)
    /// and the songs of the bar bands (music_lists.xml Entry@MusicCue; when a song plays stays with its slot). Each of
    /// the three is shuffled on its own, as they are different kinds of cues. Music that scripts start (cutscenes) stays.
    /// </summary>
    public static void ApplyMusic(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var rng = ctx.RngFor("music");
        ctx.Spoiler.Section("Music");

        var missions = c.Find("Definitions", "missions*.de").SelectMany(f => c.Xml(f).Definitions("Mission"))
            .Select(m => (Name: "mission " + m.Name(), Value: m.Element("Music")))
            .Where(x => x.Value != null && x.Value.Value.Trim().Length > 0)
            .Select(x => (x.Name, Get: (Func<string>)(() => x.Value!.Value.Trim()), Set: (Action<string>)(v => x.Value!.Value = v)));
        var regions = c.Xml(RegionsFile).Definitions("Region")
            .Select(r => (Name: "world map " + r.Name(), Value: r.Element("Music")))
            .Where(x => x.Value != null && x.Value.Value.Trim().Length > 0)
            .Select(x => (x.Name, Get: (Func<string>)(() => x.Value!.Value.Trim()), Set: (Action<string>)(v => x.Value!.Value = v)));
        var songs = c.Xml(MusicListsFile).Definitions("MusicList")
            .SelectMany(l => (l.Element("Entries")?.Elements("Entry") ?? []).Select(e => (Name: "bar band " + l.Name(), Value: e.Attribute("MusicCue"))))
            .Where(x => x.Value != null && x.Value.Value.Length > 0)
            .Select(x => (x.Name, Get: (Func<string>)(() => x.Value!.Value), Set: (Action<string>)(v => x.Value!.Value = v)));

        int count = 0;
        foreach (var group in new[] { missions.ToList(), regions.ToList(), songs.ToList() })
        {
            var values = group.Select(x => x.Get()).ToList();
            rng.Shuffle(values);
            for (int i = 0; i < group.Count; i++)
            {
                var was = group[i].Get();
                if (was == values[i]) continue;
                group[i].Set(values[i]);
                ctx.Spoiler.Line($"{group[i].Name}: {values[i]}    (was {was})");
                count++;
            }
        }
        ctx.Log($"Music: {count} changed.");
    }

    /// <summary>
    /// Mission lighting, sky and background (missions*.de Mission/Environment, see environments.xml).
    /// Balanced: only between the same kind of place (navy, pirate, industrial, atomic, bars, open sea). The tutorial stays.
    /// </summary>
    public static void ApplyEnvironments(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var rng = ctx.RngFor("environments");
        bool balanced = ctx.Options.EnvironmentMode == Mode.Balanced;
        var slots = c.Find("Definitions", "missions*.de").SelectMany(f => c.Xml(f).Definitions("Mission"))
            .Select(m => (Mission: m.Name(), Env: m.Element("Environment")))
            .Where(x => x.Env != null && x.Env.Value.Trim() is { Length: > 0 } v && v != "test" && v != "tutorial")
            .ToList();

        ctx.Spoiler.Section($"Mission lighting ({ctx.Options.EnvironmentMode})");
        int count = 0;
        foreach (var group in slots.GroupBy(x => balanced ? Kind(x.Env!.Value.Trim()) : ""))
        {
            var list = group.ToList();
            var values = list.Select(x => x.Env!.Value.Trim()).ToList();
            rng.Shuffle(values);
            for (int i = 0; i < list.Count; i++)
            {
                var was = list[i].Env!.Value.Trim();
                if (was == values[i]) continue;
                list[i].Env!.Value = values[i];
                ctx.Spoiler.Line($"{list[i].Mission}: {values[i]}    (was {was})");
                count++;
            }
        }
        ctx.Log($"Mission lighting: {count} missions changed.");
    }

    static string Kind(string environment)
    {
        if (environment.EndsWith("_bar", StringComparison.Ordinal)) return "bar";
        var first = environment.Split('_')[0];
        return first is "navy" or "pirate" or "industrial" or "atomic" ? first : "sea";
    }

    /// <summary>
    /// Names of items (weapons, utilities, ship equipment, hats) and enemies swap, in every language: the texts of their
    /// name ids are swapped in Language/&lt;lang&gt;.csv. Descriptions stay, so they still tell what a thing really is.
    /// Balanced: only inside the same kind. Runs last, so the spoiler log of the other modules shows the real names.
    /// </summary>
    public static void ApplyNames(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var rng = ctx.RngFor("names");
        var groups = new List<(string Kind, List<string> Keys)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string kind, IEnumerable<string> keys) =>
            groups.Add((kind, keys.Where(k => ctx.Loc.Get(k) != null && seen.Add(k)).ToList()));

        foreach (var kind in new[] { "Weapon", "Utility", "ShipEquipment", "Hat" })
            Add(kind, ctx.Items.All
                .Where(i => i.Kind == kind && !i.Abstract && (kind != "Weapon" || i.File == "Definitions/weapons.xml") && i.RarityType != "default")
                .OrderBy(i => i.Name, StringComparer.Ordinal)
                .Select(i => i.NameId ?? (kind == "Weapon" ? "weapon_" + i.Name : i.Name)));
        Add("Enemy", EnemyShuffle.EnemyFiles
            .SelectMany(f => c.Xml(f).Definitions("Entity"))
            .Select(e => e.Element("Actor")?.Element("NameStringId")?.Value.Trim() ?? "")
            .Where(k => k.Length > 0));

        if (ctx.Options.NameMode == Mode.Chaos) groups = [("everything", groups.SelectMany(g => g.Keys).ToList())];

        var source = new Dictionary<string, string>(StringComparer.Ordinal); // name id -> id whose text it shows now
        ctx.Spoiler.Section($"Names ({ctx.Options.NameMode})");
        foreach (var (kind, keys) in groups)
        {
            var from = keys.ToList();
            rng.Shuffle(from);
            for (int i = 0; i < keys.Count; i++)
            {
                if (keys[i] == from[i]) continue;
                source[keys[i]] = from[i];
                ctx.Spoiler.Line($"{ctx.Loc.Get(keys[i])} [{keys[i]}] is now called \"{ctx.Loc.Get(from[i])}\"");
            }
        }

        // Language/<lang>.csv only (not *_conversations / *_speakers): "id<TAB>text[<TAB>…]".
        int files = 0;
        foreach (var path in c.Find("Language", "*.csv").Where(p => !Path.GetFileName(p).Contains('_')))
        {
            var raw = c.Read(path);
            bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            var lines = c.ReadText(path).Split('\n');
            var texts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in lines)
                if (line.Length > 0 && line[0] != '#' && line.TrimEnd('\r').Split('\t') is { Length: >= 2 } p && !texts.ContainsKey(p[0]))
                    texts[p[0]] = p[1];

            bool changed = false;
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length == 0 || line[0] == '#') continue;
                bool cr = line.EndsWith("\r", StringComparison.Ordinal);
                var parts = line.TrimEnd('\r').Split('\t');
                if (parts.Length < 2 || !source.TryGetValue(parts[0], out var from) || !texts.TryGetValue(from, out var text) || text.Length == 0) continue;
                parts[1] = text;
                lines[i] = string.Join("\t", parts) + (cr ? "\r" : "");
                changed = true;
            }
            if (!changed) continue;
            c.WriteText(path, string.Join("\n", lines), bom);
            files++;
        }
        ctx.Log($"Names: {source.Count} names swapped in {files} languages.");
    }
}
