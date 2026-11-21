using System.Globalization;
using System.Xml.Linq;
using SWH2.Core;

namespace SWH2.Mods.Randomizer;

/// <summary>
/// Shop stock: every shop podium in the levels (Patchsets/*.le, CustomEntity "itemshop" with an Item property; bars,
/// the shop boats, Atomic City). Podiums with a fixed price (fragment packs, mystery weapons), key items (celestial gears)
/// and the buy-back podiums stay.
///  - Balanced: items swap between podiums of all shops, only with the same kind (weapon, utility, ship equipment, hat)
///    and a similar tier. A podium's "sold once" flag goes with its item.
///  - Chaos: each podium gets a random item from the whole game (anything with a price), no repeats inside a shop.
/// </summary>
public static class ShopShuffle
{
    static readonly string[] Kinds = ["Weapon", "Utility", "ShipEquipment", "Hat"];

    sealed class Podium
    {
        public string File = "";
        public string Name = "";
        public XElement Item = null!;
        public XElement? OneTime;
        public string Value => Item.Value.Trim();
    }

    public static void Apply(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var items = ctx.Items;
        var rng = ctx.RngFor("shops");
        var pinned = Story.PinnedItems(c, items);

        var podiums = new List<Podium>();
        foreach (var file in c.OriginalFiles.Where(f => f.StartsWith("Patchsets/", StringComparison.OrdinalIgnoreCase) &&
                                                         f.EndsWith(".le", StringComparison.OrdinalIgnoreCase))
                              .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (!c.ReadText(file).Contains("<Definition>itemshop</Definition>")) continue;
            foreach (var e in c.Xml(file).Root.Descendants("CustomEntity").Where(e => e.Element("Definition")?.Value.Trim() == "itemshop"))
            {
                var props = e.Elements("Property").GroupBy(p => p.Element("Name")?.Value.Trim() ?? "")
                    .ToDictionary(g => g.Key, g => g.First().Element("Value"));
                if (props.GetValueOrDefault("Item") is not { } item) continue;
                var cost = props.GetValueOrDefault("Cost")?.Value.Trim();
                var name = item.Value.Trim();
                if (!items.Exists(name) || pinned.Contains(name) || !Kinds.Contains(items.Get(name)!.Kind) || (cost != null && cost != "-1")) continue;
                podiums.Add(new Podium { File = file, Name = e.Element("Name")?.Value.Trim() ?? "", Item = item, OneTime = props.GetValueOrDefault("OneTime") });
            }
        }

        var before = podiums.ToDictionary(p => p, p => p.Value);
        if (ctx.Options.ShopMode == Mode.Balanced)
        {
            foreach (var group in podiums.GroupBy(p => Bucket(items.Get(p.Value)!)))
            {
                var list = group.ToList();
                var stock = list.Select(p => (Item: p.Value, OneTime: p.OneTime?.Value)).ToList();
                rng.Shuffle(stock);
                for (int i = 0; i < list.Count; i++)
                {
                    list[i].Item.Value = stock[i].Item;
                    if (list[i].OneTime != null && stock[i].OneTime != null) list[i].OneTime!.Value = stock[i].OneTime!;
                }
            }
        }
        else
        {
            var pool = Purchasable(c, items, pinned);
            foreach (var shop in podiums.GroupBy(p => p.File))
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in shop)
                {
                    var fresh = pool.Where(i => !used.Contains(i.Name)).ToList();
                    var pick = rng.Pick(fresh.Count > 0 ? fresh : pool);
                    used.Add(pick.Name);
                    p.Item.Value = pick.Name;
                    // Weapons and hats are unique finds; consumables may stay restockable as the podium was.
                    if (p.OneTime != null && pick.Kind is "Weapon" or "Hat") p.OneTime.Value = "True";
                }
            }
        }

        ctx.Spoiler.Section($"Shops ({ctx.Options.ShopMode})");
        foreach (var p in podiums.Where(p => p.Value != before[p]))
            ctx.Spoiler.Line($"{Path.GetFileNameWithoutExtension(p.File)} {p.Name}: {items.Display(p.Value)}    (was {items.Display(before[p])})");
        ctx.Log($"Shops: {podiums.Count(p => p.Value != before[p])} of {podiums.Count} podiums in {podiums.Select(p => p.File).Distinct().Count()} levels changed.");
    }

    static string Bucket(ItemDef item) => item.Kind + " " + (item.Kind == "Hat" ? "" : item.Tier switch
    {
        null => "untiered",
        <= 1 => "tier 0-1",
        >= 5 => "tier 5+",
        _ => "tier " + item.Tier,
    });

    /// <summary>Every real item the game puts a price on (LootCost, possibly inherited), except story items and starting gear.</summary>
    static List<ItemDef> Purchasable(GameContent c, ItemCatalog items, HashSet<string> pinned)
    {
        var result = new List<ItemDef>();
        foreach (var file in ItemCatalog.SourceFiles)
        {
            var defs = c.Xml(file).Definitions().ToDictionary(e => e.Name(), StringComparer.OrdinalIgnoreCase);
            foreach (var (name, e) in defs)
            {
                var item = items.Get(name);
                if (item == null || item.Abstract || !Kinds.Contains(item.Kind) || pinned.Contains(name) || item.RarityType == "default") continue;
                var cost = Inherited(e, defs, "LootCost");
                if (cost == null || !int.TryParse(cost.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0) continue;
                result.Add(item);
            }
        }
        return result;
    }

    static string? Inherited(XElement e, Dictionary<string, XElement> defs, string child)
    {
        for (int depth = 0; depth < 16; depth++)
        {
            if (e.Element(child) is { } found) return found.Value;
            if (e.Attribute("Template")?.Value is not { } t || !defs.TryGetValue(t, out e!)) return null;
        }
        return null;
    }
}
