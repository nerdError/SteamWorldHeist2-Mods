using System.Text.RegularExpressions;
using System.Xml.Linq;
using SWH2.Core;
using SWH2.Modding;

namespace SWH2.Mods.Challenges;

/// <summary>
/// "No grind" challenge: everything that can be farmed indefinitely is reduced to a one-time reward.
/// Pure data edits, no Lua — the game's own quest system does the bookkeeping, so it is per save.
/// </summary>
public static class NoGrind
{
    const string QuestsFile = "Definitions/quests.xml";
    const string QuestPrefix = "nogrind_";

    public static void Apply(ModContext ctx, bool lockReplays, bool noRespawningLoot, bool noShipLoot)
    {
        ctx.Report.Section("No-grind challenge");
        if (noRespawningLoot) DisableRespawningLoot(ctx);
        if (noShipLoot) DisableShipLoot(ctx);
        if (lockReplays) LockReplays(ctx);
    }

    /// <summary>
    /// Flagships/artifacts respawn every day and, once their item is taken, drop "ghost" money + fragments forever;
    /// small floating loot pickups also respawn daily.
    /// </summary>
    static void DisableRespawningLoot(ModContext ctx)
    {
        var regions = ctx.Content.Xml("Definitions/worldmap_regions.xml");
        int spawns = 0;
        foreach (var spawn in regions.Root.Descendants("ReputationSpawns").Elements("Spawn"))
        {
            // Mission spawns (the pirate boss) are handled by KeepMissionOnMap / LockReplays instead.
            if (spawn.Attribute("Mission") != null || spawn.AttrIsTrue("KeepMissionOnMap")) continue;
            if (spawn.Attribute("RespawnIfAchievedReputation")?.Value.ToLowerInvariant() is "false" or "0") continue;
            spawn.SetAttributeValue("RespawnIfAchievedReputation", "false");
            spawns++;
            ctx.Report.Line($"No respawn after looting: {spawn.Attribute("Entity")?.Value} ({spawn.Parent!.Parent!.Name()})");
        }

        // Ghost loot still appears when a region is already at max reputation; make it empty.
        int ghost = 0;
        foreach (var lootType in regions.Root.Descendants("LootType").Where(t => t.Name() == "worldmap_ghost"))
            foreach (var entry in lootType.Descendants("Entry"))
            {
                entry.SetAttributeValue("Base", "0");
                entry.SetAttributeValue("Random", "0");
                ghost++;
            }

        var wmSpawns = ctx.Content.Xml("Definitions/worldmap_spawns.xml");
        int pickups = 0;
        foreach (var spawn in wmSpawns.Definitions("Spawn"))
        {
            var entries = spawn.Element("SpawnEntries")?.Elements("SpawnEntry").ToList();
            if (entries is not { Count: > 0 } || entries.Any(e => !e.Name().StartsWith("wm_loot"))) continue;
            if (spawn.Element("Count") is not { } count) continue;
            count.SetAttributeValue("Base", "0");
            count.SetAttributeValue("Random", "0");
            pickups++;
            ctx.Report.Line($"No daily floating loot: {spawn.Name()}");
        }
        ctx.Log($"No grind: {spawns} flagship/artifact spawns stop respawning, {ghost} ghost loot entries emptied, {pickups} floating loot spawns removed.");
    }

    /// <summary>World map ships respawn daily; they keep fighting you but drop nothing.</summary>
    static void DisableShipLoot(ModContext ctx)
    {
        int n = 0;
        foreach (var e in ctx.Content.Xml("Definitions/entities.worldmap.xml").Definitions("Entity"))
        {
            // Edit the existing text node instead of setting Value, so the file is patched in place.
            if (e.Element("WorldMapActor")?.Element("LootEntity") is not { } loot || loot.FirstNode is not XText text) continue;
            if (string.IsNullOrWhiteSpace(text.Value)) continue;
            ctx.Report.Line($"No ship loot: {e.Name()} (was {text.Value.Trim()})");
            text.Value = "";
            n++;
        }
        ctx.Log($"No grind: ship loot removed from {n} world map ship template(s).");
    }

    /// <summary>
    /// For every combat mission on the world map: a hidden quest that completes once the mission has been won
    /// (mission_static, so it also works for missions won before installing), unlocking a hidden quest that
    /// never completes and keeps the mission in LockMissions.
    /// Missions that a story quest asks you to complete (Condition "mission", which only counts while that quest
    /// is active — e.g. the Fabio rematch) are locked only after all those quests are done.
    /// </summary>
    static void LockReplays(ModContext ctx)
    {
        var missions = new Dictionary<string, XElement>();
        foreach (var file in ctx.Content.Find("Definitions", "missions*.de"))
            foreach (var m in ctx.Content.Xml(file).Definitions("Mission"))
                missions.TryAdd(m.Name(), m);

        string? Resolve(string name, string field)
        {
            for (int guard = 0; guard < 20 && missions.TryGetValue(name, out var m); guard++)
            {
                if (m.Element(field)?.Value.Trim() is { Length: > 0 } v) return v;
                name = m.Attribute("Template")?.Value ?? "";
            }
            return null;
        }

        var onMap = new SortedSet<string>(StringComparer.Ordinal);
        var tmx = ctx.Content.ReadText("WorldMap/Maps/WorldMap.tmx");
        foreach (Match m in Regex.Matches(tmx, "<property name=\"Mission\"[^>]*value=\"([^\"]+)\"")) onMap.Add(m.Groups[1].Value);
        foreach (var s in ctx.Content.Xml("Definitions/worldmap_regions.xml").Root.Descendants("Spawn"))
            if (s.Attribute("Mission")?.Value is { Length: > 0 } mission) onMap.Add(mission);

        var quests = ctx.Content.Xml(QuestsFile);
        var root = quests.Root;
        if (root.Elements("Quest").Any(q => q.Name().StartsWith(QuestPrefix)))
            throw new InvalidOperationException("quests.xml already contains no-grind quests (built from a modded file?)");

        var requiredBy = new Dictionary<string, List<string>>();
        foreach (var quest in root.Elements("Quest"))
            foreach (var obj in quest.Descendants("Objective"))
                if (obj.Element("Condition")?.Value.Trim() == "mission" && obj.Element("Parameter")?.Value.Trim() is { Length: > 0 } p)
                {
                    var list = requiredBy.TryGetValue(p, out var l) ? l : requiredBy[p] = [];
                    if (!list.Contains(quest.Name())) list.Add(quest.Name());
                }

        var indent = root.Elements("Quest").FirstOrDefault()?.PreviousNode is XText t ? t.Value : "\n\t";
        var tail = root.LastNode;
        void Add(XNode e)
        {
            if (tail is XText) tail.AddBeforeSelf(new XText(indent), e);
            else root.Add(new XText(indent), e);
        }
        Add(new XComment(" SWH2 Challenges: no-grind — won missions can't be replayed "));

        int locked = 0;
        foreach (var name in onMap)
        {
            if (!missions.ContainsKey(name) || Resolve(name, "StartingMode") != "Combat") continue;
            var won = $"{QuestPrefix}won_{name}";
            var unlockedBy = new List<string> { won };
            if (requiredBy.TryGetValue(name, out var story)) unlockedBy.AddRange(story);

            Add(new XElement("Quest", new XAttribute("Name", won), new XAttribute("Template", "BASE"),
                new XElement("Objectives", new XElement("Objective",
                    new XElement("Condition", "mission_static"), new XElement("Parameter", name)))));
            Add(new XElement("Quest", new XAttribute("Name", $"{QuestPrefix}lock_{name}"), new XAttribute("Template", "BASE"),
                new XElement("UnlockedBy", string.Join(", ", unlockedBy)),
                new XElement("Objectives", new XElement("Objective", new XElement("Condition", "none"))),
                new XElement("LockMissions", new XElement("Mission", name))));
            locked++;
            ctx.Report.Line(story is { Count: > 0 }
                ? $"Replay locked: {name} (after story quests {string.Join(", ", story)})"
                : $"Replay locked: {name}");
        }
        ctx.Log($"No grind: {locked} combat missions lock after they are won.");
    }
}
