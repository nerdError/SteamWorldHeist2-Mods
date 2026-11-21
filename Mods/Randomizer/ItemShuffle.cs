using System.Text.RegularExpressions;
using System.Xml.Linq;
using SWH2.Core;

namespace SWH2.Mods.Randomizer;

/// <summary>
/// Shuffles the game's fixed item rewards between their locations:
///  - mission master loot      (Definitions/missions*.de   Mission/MasterLoot/Item@Definition)
///  - region bounty rewards    (Definitions/worldmap_regions.xml   Region/Bounties/Bounty@Item)
///  - world map artifacts etc. (Definitions/worldmap_regions.xml   Region/ReputationSpawns/Spawn/Item@Definition)
///  - quest rewards            (Definitions/quests.xml   Quest/ItemRewards/Item@Name)
///
/// Progression-critical items never move: key items, and anything a quest condition or a Lua
/// script refers to by name. Missions nothing references (test levels) are left out so items
/// can't get lost there.
///
/// With "key items" on, story key items move too, with logic: see <see cref="PlaceKeyItems"/>.
/// </summary>
public static class ItemShuffle
{
    sealed record Slot(string Location, XElement Element, string ItemAttr, string? Mission = null, XElement? Quest = null)
    {
        public string Item => Element.Attribute(ItemAttr)!.Value;
        public string? Count => Element.Attribute("Count")?.Value;
    }

    public static void Apply(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var items = ctx.Items;
        var rng = ctx.RngFor("items");

        var pinned = Story.PinnedItems(c, items);
        var referenced = TokensOutsideMissionFiles(c);
        var slots = new List<Slot>();

        foreach (var file in c.Find("Definitions", "missions*.de"))
            foreach (var mission in c.Xml(file).Definitions("Mission"))
            {
                var name = mission.Name();
                if (mission.IsAbstract() || !referenced.Contains(name) || IsTestMission(name)) continue;
                var title = ctx.Loc.Get("mission_" + name);
                foreach (var it in mission.Element("MasterLoot")?.Elements("Item") ?? [])
                    slots.Add(new Slot($"Mission {(title != null ? $"\"{title}\" " : "")}({name})", it, "Definition", Mission: name));
            }

        var regions = c.Xml("Definitions/worldmap_regions.xml");
        foreach (var region in regions.Definitions("Region"))
        {
            foreach (var b in region.Element("Bounties")?.Elements("Bounty") ?? [])
                if (b.Attribute("Item") != null)
                    slots.Add(new Slot($"Bounty {region.Name()} (cost {b.Attribute("Cost")?.Value})", b, "Item"));
            foreach (var s in region.Element("ReputationSpawns")?.Elements("Spawn") ?? [])
                foreach (var it in s.Elements("Item"))
                    slots.Add(new Slot($"World map {region.Name()}: {s.Attribute("Entity")?.Value}", it, "Definition"));
        }

        foreach (var quest in c.Xml("Definitions/quests.xml").Definitions("Quest"))
            foreach (var it in quest.Element("ItemRewards")?.Elements("Item") ?? [])
                slots.Add(new Slot($"Quest reward {quest.Name()}", it, "Name", Quest: quest));

        var eligible = slots.Where(s =>
            s.Element.Attribute(s.ItemAttr) != null &&
            items.Exists(s.Item) &&
            !pinned.Contains(s.Item) &&
            (ctx.Options.IncludeResources || !ItemCatalog.IsResource(s.Item))).ToList();

        ctx.Log($"Items: {slots.Count} reward locations found, {eligible.Count} shuffled " +
                $"({pinned.Count} progression items kept in place).");

        var bands = AmountBands(eligible);
        var groups = ctx.Options.ItemMode == ItemShuffleMode.Balanced
            ? eligible.GroupBy(s => bands.TryGetValue(s, out var band) ? band : Bucket(s.Item, items)).ToList()
            : eligible.GroupBy(_ => "all").ToList();

        var original = eligible.ToDictionary(s => s, s => (s.Item, s.Count));
        foreach (var group in groups)
        {
            var pool = group.Select(s => (s.Item, s.Count)).ToList();
            rng.Shuffle(pool);
            int i = 0;
            foreach (var slot in group) Assign(slot, pool[i++].Item, pool[i - 1].Count);
        }

        ctx.Spoiler.Section($"Item rewards ({ctx.Options.ItemMode})");
        foreach (var s in eligible.OrderBy(s => s.Location, StringComparer.Ordinal))
        {
            var (was, wasCount) = original[s];
            ctx.Spoiler.Line($"{s.Location}: {Describe(items, s.Item, s.Count)}    (was {Describe(items, was, wasCount)})");
        }

        if (ctx.Options.ShuffleKeyItems) PlaceKeyItems(ctx, slots, pinned);
    }

    /// <summary>
    /// Logic for story key items (glow rods, heist keys, diving plates…), in the spirit of SWD2Randomizer but using the
    /// game's own story timeline (<see cref="Story.Timeline"/>): a key item only moves to a location the story completes at
    /// an EARLIER point than the item first shows up (in its own location or in an inventory). A location done before the
    /// item ever existed can't need it, and every item only gets earlier, so whatever needed it is still reachable — the
    /// game stays beatable. Locations whose timing isn't known (bounties, world map spawns, moved missions) take no key items,
    /// nor do the ones a "skip tutorial" start counts as already done;
    /// key items that scripts hand out or that also lie elsewhere (world map pickups, shops) stay.
    /// </summary>
    static void PlaceKeyItems(RandomizerContext ctx, List<Slot> slots, HashSet<string> pinned)
    {
        var c = ctx.Content;
        var items = ctx.Items;
        var rng = ctx.RngFor("keyitems");
        var time = new Story.Timeline(c);
        var scripted = Story.GivenByScripts(c);
        var elsewhere = TokensIn(c, f =>
            f.EndsWith(".le", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tmx", StringComparison.OrdinalIgnoreCase) ||
            f.Equals("Definitions/entities.worldmap.xml", StringComparison.OrdinalIgnoreCase));

        int? When(Slot s)
        {
            if (s.Mission != null)
                return !ctx.MovedMissions.Contains(s.Mission) && time.Missions.TryGetValue(s.Mission, out var m) ? m : null;
            if (s.Quest == null || s.Quest.Element("ItemRewards")!.AttrIsTrue("IgnoreIfQuestSkipped")) return null;
            if (Story.ObjectiveMissions(s.Quest).Any(ctx.MovedMissions.Contains)) return null;
            return time.Quests.TryGetValue(s.Quest.Name(), out var q) ? q : null;
        }

        var keys = slots.Where(s => s.Element.Attribute(s.ItemAttr) != null && items.IsKeyItem(s.Item)).GroupBy(s => s.Item)
            .Where(g => g.Count() == 1 && !scripted.Contains(g.Key) && !elsewhere.Contains(g.Key))
            .Select(g => (Item: g.Key, Home: g.Single(), Limit: Min(When(g.Single()), time.Items.TryGetValue(g.Key, out var t) ? t : null)))
            .Where(k => k.Limit != null)
            .OrderBy(k => k.Limit).ThenBy(k => k.Item, StringComparer.Ordinal).ToList();

        var taken = new HashSet<Slot>(keys.Select(k => k.Home));
        ctx.Spoiler.Section("Key items (logic: only to places the story reaches earlier)");
        int moved = 0;
        foreach (var (item, home, limit) in keys)
        {
            var spots = slots.Where(s => !taken.Contains(s) && s.Element.Attribute(s.ItemAttr) != null && items.Exists(s.Item) &&
                                         !pinned.Contains(s.Item) && When(s) is { } w && w < limit && w > time.StartDepth).ToList();
            // Staying home is one of the options, so not every key item moves.
            int pick = rng.Next(spots.Count + 1);
            if (pick == spots.Count)
            {
                ctx.Spoiler.Line($"{items.Display(item)}: stays at {home.Location} ({spots.Count} earlier places possible)");
                continue;
            }
            var spot = spots[pick];
            taken.Add(spot);
            var (other, count) = (spot.Item, spot.Count);
            Assign(spot, item, null);
            Assign(home, other, count);
            moved++;
            ctx.Spoiler.Line($"{items.Display(item)}: {spot.Location}    (was {home.Location}, story step {limit}; this place: step {When(spot)}; {Describe(items, other, count)} went there)");
        }
        ctx.Log($"Key items: {moved} of {keys.Count} movable key items placed earlier in the story.");
    }

    static int? Min(int? a, int? b) => a == null ? b : b == null ? a : Math.Min(a.Value, b.Value);

    static HashSet<string> TokensIn(GameContent c, Func<string, bool> files)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in c.OriginalFiles.Where(files))
            foreach (Match m in Regex.Matches(c.ReadText(f), "[A-Za-z0-9_]+"))
                tokens.Add(m.Value);
        return tokens;
    }

    static void Assign(Slot slot, string item, string? count)
    {
        slot.Element.SetAttributeValue(slot.ItemAttr, item);
        var countAttr = slot.Element.Attribute("Count");
        if (ItemCatalog.IsResource(item))
        {
            if (count != null) slot.Element.SetAttributeValue("Count", count);
        }
        // A slot that held e.g. 12 fragments must not hand out 12 copies of a weapon.
        else if (countAttr != null && int.TryParse(countAttr.Value, out var n) && n > 1)
            countAttr.Value = "1";
    }

    static string Describe(ItemCatalog items, string item, string? count) =>
        ItemCatalog.IsResource(item) && count != null ? $"{count}x {items.Display(item)}" : items.Display(item);

    static bool IsTestMission(string name) =>
        Regex.IsMatch(name, @"(^|_)(test|prototype|debug)(_|\d|$)", RegexOptions.IgnoreCase) || name == "Mission1";

    /// <summary>
    /// Balanced mode: money and fragments only swap with similar amounts (one of four bands by amount, from the
    /// game's own rewards), so an early bounty can't hand out late-game money.
    /// </summary>
    static Dictionary<Slot, string> AmountBands(List<Slot> slots)
    {
        const int Bands = 4;
        var result = new Dictionary<Slot, string>();
        foreach (var resource in slots.Where(s => ItemCatalog.IsResource(s.Item)).GroupBy(s => s.Item))
        {
            var sorted = resource.OrderBy(s => int.TryParse(s.Count, out var n) ? n : 1).ThenBy(s => s.Location, StringComparer.Ordinal).ToList();
            for (int i = 0; i < sorted.Count; i++) result[sorted[i]] = $"{resource.Key} band {i * Bands / sorted.Count}";
        }
        return result;
    }

    /// <summary>Balanced mode swaps items only inside these groups.</summary>
    static string Bucket(string item, ItemCatalog items)
    {
        if (ItemCatalog.IsResource(item)) return item; // money only with money, fragments with fragments
        var tier = items.Get(item)?.Tier;
        return tier switch
        {
            null => "untiered",
            <= 1 => "tier 0-1",
            >= 5 => "tier 5+",
            _ => "tier " + tier,
        };
    }

    /// <summary>Every identifier-like token in game data other than the mission files themselves.</summary>
    static HashSet<string> TokensOutsideMissionFiles(GameContent c)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = c.OriginalFiles.Where(f =>
            (f.StartsWith("Definitions/", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).StartsWith("missions", StringComparison.OrdinalIgnoreCase)) ||
            f.StartsWith("Lua/", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".tmx", StringComparison.OrdinalIgnoreCase));
        foreach (var f in files)
            foreach (Match m in Regex.Matches(c.ReadText(f), "[A-Za-z0-9_]+"))
                tokens.Add(m.Value);
        return tokens;
    }
}
