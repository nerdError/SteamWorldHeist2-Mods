using System.Text.RegularExpressions;
using System.Xml.Linq;
using SWH2.Core;

namespace SWH2.Mods.Randomizer;

/// <summary>
/// Shuffles which mission each world map marker starts (WorldMap/Maps/WorldMap.tmx, objects with a "Mission"
/// property). The marker keeps its place and layer; the mission moves with its name, so its quests, script,
/// loot, icon (Mission/WorldMapEntity) and quest locks come along.
///
/// Missions stay where they are when the story needs them at a certain time (see <see cref="Story.ProgressionMissions"/>:
/// quest objectives that open cordons or lead to other quests — "mission" objectives only count while their quest is
/// active), when they are set pieces (bosses, heists, parley, shops, roaming locations) or when their loot is a story item.
///  - Balanced: only inside the same sea (West/East Caribbea, Arctica, Atomic City), so threat stays similar.
///  - Chaos: across the whole map.
/// </summary>
public static class MissionShuffle
{
    const string MapFile = "WorldMap/Maps/WorldMap.tmx";
    const string HideProperty = "Hide If Locked By Quest";
    static readonly Regex SetPiece = new(@"boss|heist|parley|shop|roaming|frozen_vessel|tutorial|prison", RegexOptions.IgnoreCase);
    static readonly string[] Seas = ["west_caribbea", "east_caribbea", "arctica", "atomic"];

    sealed record Marker(XElement Obj, XAttribute Mission)
    {
        public XAttribute? Hide => Property(Obj, HideProperty);
    }

    public static void Apply(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var rng = ctx.RngFor("missions");
        var defs = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in c.Find("Definitions", "missions*.de"))
            foreach (var m in c.Xml(file).Definitions("Mission")) defs[m.Name()] = m;

        var progression = Story.ProgressionMissions(c);
        var pinnedItems = Story.PinnedItems(c, ctx.Items);
        bool Pinned(string mission) =>
            !defs.TryGetValue(mission, out var def) || SetPiece.IsMatch(mission) || progression.Contains(mission) ||
            (def.Element("MasterLoot")?.Elements("Item") ?? []).Any(i => pinnedItems.Contains(i.Attribute("Definition")?.Value ?? ""));

        var markers = c.Xml(MapFile).Root.Descendants("object")
            .Select(o => Property(o, "Mission") is { } m ? new Marker(o, m) : null)
            .OfType<Marker>().ToList();
        var free = markers.Where(m => !Pinned(m.Mission.Value)).ToList();
        ctx.Log($"Missions: {markers.Count} world map missions, {free.Count} shuffled (story missions and set pieces stay).");

        // Only markers that look the same way swap (the "hide while locked" flag, Tiled type), so the map file is only
        // patched in place; Balanced also keeps each sea's missions in that sea.
        var groups = free.GroupBy(m => string.Join("|",
            ctx.Options.MissionMode == Mode.Balanced ? SeaOf(m.Mission.Value) : "all",
            m.Hide?.Value ?? "-", m.Obj.Attribute("type")?.Value ?? "-"));

        var moves = new List<(Marker Marker, string Was, string Now)>();
        foreach (var group in groups)
        {
            var list = group.ToList();
            var payload = list.Select(m => m.Mission.Value).ToList();
            rng.Shuffle(payload);
            for (int i = 0; i < list.Count; i++)
            {
                var (m, mission) = (list[i], payload[i]);
                var was = m.Mission.Value;
                if (was == mission) continue;
                m.Mission.Value = mission; // the object's name is only an editor label and stays
                ctx.MovedMissions.Add(mission);
                moves.Add((m, was, mission));
            }
        }

        ctx.Spoiler.Section($"Missions ({(ctx.Options.MissionMode == Mode.Balanced ? "inside each sea" : "whole map")})");
        foreach (var (_, was, now) in moves.OrderBy(x => x.Was, StringComparer.Ordinal))
            ctx.Spoiler.Line($"Marker of {Describe(ctx, was)} now starts {Describe(ctx, now)}");
    }

    static string Describe(RandomizerContext ctx, string mission) =>
        ctx.Loc.Get("mission_" + mission) is { } title ? $"\"{title}\" ({mission})" : mission;

    static string SeaOf(string mission) => Seas.FirstOrDefault(s => mission.StartsWith(s, StringComparison.OrdinalIgnoreCase)) ?? "other";

    static XAttribute? Property(XElement obj, string name) =>
        obj.Element("properties")?.Elements("property").FirstOrDefault(p => p.Attribute("name")?.Value == name)?.Attribute("value");
}
