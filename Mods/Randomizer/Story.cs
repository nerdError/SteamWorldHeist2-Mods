using System.Text.RegularExpressions;
using System.Xml.Linq;
using SWH2.Core;

namespace SWH2.Mods.Randomizer;

/// <summary>What the story depends on, read from quests.xml, outsets.xml and the Lua scripts. Shared by the modules.</summary>
public static class Story
{
    const string QuestsFile = "Definitions/quests.xml";
    const string OutsetsFile = "Definitions/outsets.xml";

    /// <summary>Items that story logic depends on: key items, and anything a quest condition or a Lua script refers to by name.</summary>
    public static HashSet<string> PinnedItems(GameContent c, ItemCatalog items)
    {
        var pinned = new HashSet<string>(items.All.Where(i => i.Kind == "KeyItem").Select(i => i.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var p in c.Xml(QuestsFile).Root.Descendants("Parameter"))
            if (items.Exists(p.Value.Trim())) pinned.Add(p.Value.Trim());

        foreach (var lua in LuaFiles(c))
            foreach (Match m in Regex.Matches(c.ReadText(lua), "\"([A-Za-z0-9_]+)\""))
                if (items.Exists(m.Groups[1].Value)) pinned.Add(m.Groups[1].Value);

        return pinned;
    }

    /// <summary>Items a Lua script hands out with GiveItem (cutscenes, world map events).</summary>
    public static HashSet<string> GivenByScripts(GameContent c)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lua in LuaFiles(c))
            foreach (Match m in Regex.Matches(c.ReadText(lua), @"^[^-\n]*GiveItem\s*\(\s*""(\w+)""", RegexOptions.Multiline))
                names.Add(m.Groups[1].Value);
        return names;
    }

    /// <summary>
    /// Missions the story needs at a certain time: objectives of quests that other quests, scripts or the world map
    /// depend on (unlocking cordons, layers, follow-up quests), static mission checks, and missions that cutscenes
    /// or conversations refer to. Quests that only track a mission for dialogue don't count.
    /// </summary>
    public static HashSet<string> ProgressionMissions(GameContent c)
    {
        var quests = c.Xml(QuestsFile).Definitions("Quest").ToList();
        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in quests)
        {
            foreach (var name in (q.Element("UnlockedBy")?.Value ?? "").Split(','))
                if (name.Trim().Length > 0) needed.Add(name.Trim());
            foreach (var o in q.Descendants("Objective").Where(o => o.Element("Condition")?.Value.Trim() == "quest"))
                needed.Add(o.Element("Parameter")?.Value.Trim() ?? "");
        }
        foreach (var lua in LuaFiles(c))
            foreach (Match m in Regex.Matches(c.ReadText(lua), "\"([A-Za-z0-9_]+)\""))
                needed.Add(m.Groups[1].Value);

        string[] worldEffects = ["UnlockCordonsWhenComplete", "LockCordonsWhenActive", "ShowWorldMapLayers", "HideWorldMapLayers",
                                 "OnCompleted", "OnUnlocked", "LockMissions", "LockAllMissionsExcept", "ReportCheckpoint"];
        var missions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in quests)
        {
            bool progression = needed.Contains(q.Name()) || worldEffects.Any(e => q.Element(e) != null);
            foreach (var o in q.Descendants("Objective"))
            {
                var condition = o.Element("Condition")?.Value.Trim();
                var mission = o.Element("Parameter")?.Value.Trim();
                if (string.IsNullOrEmpty(mission)) continue;
                if (condition == "mission_static" || (condition == "mission" && progression)) missions.Add(mission!);
            }
        }

        foreach (var file in new[] { "Definitions/intermission_cutscenes.xml", "Definitions/conversations.xml" })
            foreach (Match m in Regex.Matches(c.ReadText(file), "[A-Za-z0-9_]+"))
                missions.Add(m.Value); // only names of missions matter; other tokens are harmless here
        return missions;
    }

    /// <summary>Missions a quest's objectives refer to (mission / mission_static conditions).</summary>
    public static IEnumerable<string> ObjectiveMissions(XElement quest) =>
        quest.Descendants("Objective")
            .Where(o => o.Element("Condition")?.Value.Trim() is "mission" or "mission_static")
            .Select(o => o.Element("Parameter")?.Value.Trim() ?? "")
            .Where(m => m.Length > 0);

    /// <summary>
    /// The story timeline from outsets.xml: the developers' debug starting points (new_game_skip_tutorial → west_caribbea_b →
    /// … → atomic_late) form a tree through Template, each listing the quests and missions completed and the items owned at
    /// that point. Depth in that tree = how far into the story. Test outsets are left out.
    /// </summary>
    public sealed class Timeline
    {
        public readonly Dictionary<string, int> Quests = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> Missions = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> Items = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Depth of the outsets a player can start a new game from (game_constants.xml: normal and "skip tutorial").
        /// Whatever they already count as done (depth &lt;= this) is never played after skipping the tutorial.
        /// </summary>
        public readonly int StartDepth;

        public Timeline(GameContent c)
        {
            var outsets = c.Xml(OutsetsFile).Definitions("Outset").ToDictionary(o => o.Name(), StringComparer.Ordinal);
            int Depth(XElement o)
            {
                int depth = 0;
                for (var t = o.Attribute("Template")?.Value; t != null && depth < 64 && outsets.TryGetValue(t, out var parent); t = parent.Attribute("Template")?.Value)
                    depth++;
                return depth;
            }

            var constants = c.Xml("Definitions/game_constants.xml").Root.Descendants()
                .Where(e => e.Name.LocalName.StartsWith("NewGameOutset", StringComparison.Ordinal));
            foreach (var start in constants)
                if (outsets.TryGetValue(start.Value.Trim(), out var o)) StartDepth = Math.Max(StartDepth, Depth(o));

            foreach (var (name, o) in outsets)
            {
                if (o.IsAbstract() || name.StartsWith("test", StringComparison.OrdinalIgnoreCase) || name.Contains("debug")) continue;
                int depth = Depth(o);
                Add(Quests, o.Element("CompletedQuests")?.Elements("Quest"), depth);
                Add(Missions, o.Element("CompletedMissions")?.Elements("Mission"), depth);
                Add(Items, o.Element("Items")?.Elements("Item"), depth);
            }

            // A completed quest with a mission objective means that mission was completed too.
            foreach (var q in c.Xml(QuestsFile).Definitions("Quest"))
                if (Quests.TryGetValue(q.Name(), out var depth))
                    foreach (var m in ObjectiveMissions(q))
                        if (!Missions.TryGetValue(m, out var d) || depth < d) Missions[m] = depth;
        }

        static void Add(Dictionary<string, int> map, IEnumerable<XElement>? list, int depth)
        {
            foreach (var e in list ?? [])
            {
                var key = e.Value.Trim();
                if (key.Length > 0 && (!map.TryGetValue(key, out var d) || depth < d)) map[key] = depth;
            }
        }
    }

    static IEnumerable<string> LuaFiles(GameContent c) =>
        c.OriginalFiles.Where(f => f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
}
