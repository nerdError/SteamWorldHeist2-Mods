using System.Text.RegularExpressions;
using System.Xml.Linq;
using SWH2.Core;

namespace SWH2.Mods.Randomizer;

/// <summary>The submarine part of the game: your sub's weapons, enemy ship weapons, sub upgrade costs, loot decks.</summary>
public static class SubShuffle
{
    const string EquipmentFile = "Definitions/ship_equipment.xml";
    const string WorldMapEntitiesFile = "Definitions/entities.worldmap.xml";
    const string UpgradesFile = "Definitions/ship_upgrades.xml";
    const string LootTablesFile = "Definitions/loot_tables.xml";

    /// <summary>Bosses, the kraken, mines and the snake keep their weapons: they're built around them.</summary>
    static readonly Regex SpecialShip = new(@"boss|kraken|mine|centipede", RegexOptions.IgnoreCase);

    /// <summary>
    /// Sub weapons. Your ship weapon items (ship_equipment.xml WeaponMounts/Mount@Weapon) fire each other's guns; the
    /// mount angles, name, icon and price stay with the item. Enemy ships (entities.worldmap.xml) swap guns too.
    /// Balanced: your torpedoes stay torpedoes (inside the weapon type, similar tier), enemies inside their faction.
    /// Chaos: a "machine gun" may fire torpedoes sideways, and any enemy ship any enemy gun.
    /// </summary>
    public static void ApplyWeapons(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var rng = ctx.RngFor("subweapons");
        bool balanced = ctx.Options.SubMode == Mode.Balanced;

        var defs = c.Xml(EquipmentFile).Definitions("ShipEquipment").ToDictionary(e => e.Name(), StringComparer.Ordinal);
        string? Type(XElement e)
        {
            for (int i = 0; i < 16 && e != null; i++)
            {
                if (e.Element("WeaponType")?.Value.Trim() is { Length: > 0 } t) return t;
                if (e.Attribute("Template")?.Value is not { } tpl || !defs.TryGetValue(tpl, out e!)) break;
            }
            return null;
        }
        // Every mount of an item fires the same gun (e.g. the machine guns' left and right mount), so an item is one slot.
        var items = defs.Values
            .Where(e => !e.IsAbstract() && e.Element("WeaponMounts")?.Elements("Mount").Any(m => m.Attribute("Weapon") != null) == true)
            .Select(e => (Item: e, Type: Type(e) ?? "", Tier: int.TryParse(e.Element("Rarity")?.Attribute("Tier")?.Value, out var t) ? t : 0,
                Mounts: e.Element("WeaponMounts")!.Elements("Mount").Select(m => m.Attribute("Weapon")).Where(a => a != null).Select(a => a!).ToList()))
            .Where(x => x.Mounts.Select(a => a.Value).Distinct().Count() == 1)
            .ToList();
        var group = new Dictionary<XElement, string>();
        foreach (var byType in items.GroupBy(x => balanced ? x.Type : ""))
        {
            var sorted = byType.OrderBy(x => x.Tier).ThenBy(x => x.Item.Name(), StringComparer.Ordinal).ToList();
            for (int i = 0; i < sorted.Count; i++) group[sorted[i].Item] = balanced ? $"{byType.Key} band {i / 3}" : "";
        }

        ctx.Spoiler.Section($"Sub weapons ({ctx.Options.SubMode})");
        int n = Slots.Shuffle(ctx, rng, items.Select(x => new Slot(ctx.Items.Display(x.Item.Name()), group[x.Item],
            () => x.Mounts[0].Value, v => x.Mounts.ForEach(a => a.Value = v))));

        var ships = c.Xml(WorldMapEntitiesFile).Definitions("Entity")
            .Where(e => e.Name().StartsWith("wm_enemy_", StringComparison.Ordinal) && !SpecialShip.IsMatch(e.Name()))
            .SelectMany(e => e.Descendants("WeaponMounts").Elements("Mount").Select(m => (Ship: e.Name(), Weapon: m.Attribute("Weapon"))))
            .Where(x => x.Weapon != null && x.Weapon.Value.Length > 0)
            .Select(x => Slot.Of(x.Ship, balanced ? x.Ship.Split('_')[2] : "", x.Weapon!))
            .ToList();
        int enemies = Slots.Shuffle(ctx, rng, ships);
        ctx.Log($"Sub weapons: {n} of {items.Count} ship weapon items and {enemies} of {ships.Count} enemy ship guns changed.");
    }

    /// <summary>
    /// Fragment costs of sub upgrades (ship_upgrades.xml CostFragments) swap between upgrades of the same tech tier.
    /// Upgrades that need a story key item stay.
    /// </summary>
    public static void ApplyUpgradeCosts(RandomizerContext ctx)
    {
        var rng = ctx.RngFor("subupgrades");
        var slots = ctx.Content.Xml(UpgradesFile).Definitions("ShipUpgrade")
            .Where(u => !u.IsAbstract() && u.Element("KeyItem") == null && u.Element("CostFragments") is { } cost && cost.Value.Trim().Length > 0)
            .Select(u => Slot.Of(u.Name(), "tier " + (u.Element("TechTier")?.Value.Trim() ?? "?"), u.Element("CostFragments")!))
            .ToList();
        ctx.Spoiler.Section("Sub upgrade costs");
        int n = Slots.Shuffle(ctx, rng, slots);
        ctx.Log($"Sub upgrade costs: {n} of {slots.Count} changed.");
    }

    /// <summary>
    /// Loot decks (loot_tables.xml Deck/Entry@Count): how often each kind of loot comes up — more guns and less money,
    /// or the other way round. Every entry stays possible (count at least 1). Balanced: each count changes by at most one.
    /// Chaos: 1 to 3 times the original. Note: changing this file resets the loot decks of existing saves.
    /// </summary>
    public static void ApplyLootDecks(RandomizerContext ctx)
    {
        var rng = ctx.RngFor("lootdecks");
        bool balanced = ctx.Options.LootDeckMode == Mode.Balanced;
        ctx.Spoiler.Section($"Loot decks ({ctx.Options.LootDeckMode})");
        int n = 0, total = 0;
        foreach (var g in ctx.Content.Xml(LootTablesFile).Definitions("LootGroup"))
            foreach (var deck in g.Element("Decks")?.Elements("Deck") ?? [])
                foreach (var e in deck.Elements("Entry"))
                {
                    if (e.Attribute("Count") is not { } a || !int.TryParse(a.Value, out var old) || old < 1) continue;
                    total++;
                    // Decks with a single entry always draw it; the count changes nothing there.
                    if (deck.Elements("Entry").Count() < 2) continue;
                    int now = balanced ? Math.Max(1, old + rng.Next(-1, 2)) : rng.Next(1, 3 * old + 1);
                    if (now == old) continue;
                    a.Value = now.ToString();
                    ctx.Spoiler.Line($"{g.Name()} / {deck.Name()}: {e.Attribute("Name")?.Value} x{now}    (was x{old})");
                    n++;
                }
        ctx.Log($"Loot decks: {n} of {total} entry counts changed.");
    }
}
