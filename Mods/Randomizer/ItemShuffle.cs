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
/// </summary>
public static class ItemShuffle
{
    sealed record Slot(string Location, XElement Element, string ItemAttr)
    {
        public string Item => Element.Attribute(ItemAttr)!.Value;
        public string? Count => Element.Attribute("Count")?.Value;
    }

    public static void Apply(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var items = ctx.Items;
        var rng = ctx.RngFor("items");

        var pinned = FindPinnedItems(c, items);
        var referenced = TokensOutsideMissionFiles(c);
        var slots = new List<Slot>();

        foreach (var file in c.Find("Definitions", "missions*.de"))
            foreach (var mission in c.Xml(file).Definitions("Mission"))
            {
                var name = mission.Name();
                if (mission.IsAbstract() || !referenced.Contains(name) || IsTestMission(name)) continue;
                var title = ctx.Loc.Get("mission_" + name);
                foreach (var it in mission.Element("MasterLoot")?.Elements("Item") ?? [])
                    slots.Add(new Slot($"Mission {(title != null ? $"\"{title}\" " : "")}({name})", it, "Definition"));
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
                slots.Add(new Slot($"Quest reward {quest.Name()}", it, "Name"));

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

    /// <summary>Items that story logic depends on: never moved.</summary>
    static HashSet<string> FindPinnedItems(GameContent c, ItemCatalog items)
    {
        var pinned = new HashSet<string>(items.All.Where(i => i.Kind == "KeyItem").Select(i => i.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var p in c.Xml("Definitions/quests.xml").Root.Descendants("Parameter"))
            if (items.Exists(p.Value.Trim())) pinned.Add(p.Value.Trim());

        foreach (var lua in c.OriginalFiles.Where(f => f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)))
            foreach (Match m in Regex.Matches(c.ReadText(lua), "\"([A-Za-z0-9_]+)\""))
                if (items.Exists(m.Groups[1].Value)) pinned.Add(m.Groups[1].Value);

        return pinned;
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
