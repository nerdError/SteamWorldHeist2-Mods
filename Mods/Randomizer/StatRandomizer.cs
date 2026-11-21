using System.Xml.Linq;
using SWH2.Core;

namespace SWH2.Mods.Randomizer;

/// <summary>Numeric tweaks: player weapon damage (weapons.xml) and item prices (LootCost).</summary>
public static class StatRandomizer
{
    public static void ApplyWeaponDamage(RandomizerContext ctx)
    {
        var rng = ctx.RngFor("damage");
        double v = ctx.Options.WeaponDamageVariance / 100.0;
        ctx.Spoiler.Section($"Weapon damage (±{ctx.Options.WeaponDamageVariance}%)");
        int count = 0;

        // Player weapons are the ones with a Rarity; enemy weapons live in weapons.<faction>.xml.
        foreach (var w in ctx.Content.Xml("Definitions/weapons.xml").Definitions("Weapon"))
        {
            if (w.IsAbstract() || w.Element("Rarity") == null) continue;
            var changes = new List<string>();
            foreach (var block in new[] { "ShootDamage", "ExplosiveDamage" })
            {
                if (w.Element(block)?.Element("Amount") is not { } amount || !int.TryParse(amount.Value.Trim(), out var old)) continue;
                int now = Math.Max(1, (int)Math.Round(old * (1 + (rng.NextDouble() * 2 - 1) * v)));
                amount.Value = now.ToString();
                changes.Add($"{block} {old} -> {now}");
            }
            if (changes.Count == 0) continue;
            count++;
            ctx.Spoiler.Line($"{ctx.Items.Display(w.Name())}: {string.Join(", ", changes)}");
        }
        ctx.Log($"Weapon damage: {count} weapons changed.");
    }

    public static void ApplyPrices(RandomizerContext ctx)
    {
        var rng = ctx.RngFor("prices");
        double v = ctx.Options.PriceVariance / 100.0;
        ctx.Spoiler.Section($"Item prices (±{ctx.Options.PriceVariance}%)");
        int count = 0;

        foreach (var file in ItemCatalog.SourceFiles)
            foreach (var def in ctx.Content.Xml(file).Definitions())
            {
                if (def.Element("LootCost") is not XElement cost || !int.TryParse(cost.Value.Trim(), out var old) || old <= 0) continue;
                int now = Math.Max(5, (int)Math.Round(old * (1 + (rng.NextDouble() * 2 - 1) * v) / 5) * 5);
                cost.Value = now.ToString();
                count++;
                ctx.Spoiler.Line($"{ctx.Items.Display(def.Name())}: {old} -> {now}");
            }
        ctx.Log($"Prices: {count} items changed.");
    }
}
