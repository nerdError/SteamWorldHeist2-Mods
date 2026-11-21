using System.Xml.Linq;
using SWH2.Core;

namespace SWH2.Mods.Randomizer;

/// <summary>Numeric tweaks: player weapon stats (weapons.xml) and item prices (LootCost).</summary>
public static class StatRandomizer
{
    /// <summary>
    /// Player weapons (the ones with a Rarity; enemy weapons live in weapons.&lt;faction&gt;.xml), per damage block
    /// (ShootDamage, ExplosiveDamage):
    ///  - Balanced: base damage changes by up to ±variance and crit (or else random) damage makes up the difference,
    ///    so the weapon's total punch stays about the same (crit and random damage count half, as they don't always land).
    ///  - Chaos: damage, crit, random damage and blast radius each change on their own by up to ±variance.
    /// </summary>
    public static void ApplyWeaponStats(RandomizerContext ctx)
    {
        var rng = ctx.RngFor("damage");
        double v = ctx.Options.WeaponVariance / 100.0;
        bool balanced = ctx.Options.WeaponMode == Mode.Balanced;
        ctx.Spoiler.Section($"Weapon stats ({ctx.Options.WeaponMode}, ±{ctx.Options.WeaponVariance}%)");
        int count = 0;
        double Roll() => 1 + (rng.NextDouble() * 2 - 1) * v;

        foreach (var w in ctx.Content.Xml("Definitions/weapons.xml").Definitions("Weapon"))
        {
            if (w.IsAbstract() || w.Element("Rarity") == null) continue;
            var changes = new List<string>();
            foreach (var block in new[] { "ShootDamage", "ExplosiveDamage" })
            {
                if (w.Element(block) is not { } b || Stat(b, "Amount") is not { } amount) continue;
                var crit = Stat(b, "CritAmount");
                var random = Stat(b, "AmountRandom");
                int a0 = amount.Value;
                if (balanced)
                {
                    // Keep a + crit/2 + random/2: what base damage gains, crit (or random) damage gives back twice.
                    // Without either, damage only varies half as much.
                    var other = crit ?? random;
                    double r = Roll() - 1;
                    int a1 = Round(a0 * (1 + (other == null ? r / 2 : r)));
                    if (other != null) a1 = Math.Min(a1, a0 + other.Value / 2);
                    a1 = Math.Max(1, a1);
                    Set(amount, a1, block, changes);
                    if (other != null) Set(other, other.Value - 2 * (a1 - a0), block, changes);
                    if (Stat(b, "DamageRadius") is { } radius)
                        Set(radius, Math.Max(50, (int)Math.Round(radius.Value * (1 + (Roll() - 1) / 2) / 10) * 10), block, changes);
                }
                else
                {
                    Set(amount, Math.Max(1, Round(a0 * Roll())), block, changes);
                    foreach (var s in new[] { crit, random })
                        if (s != null) Set(s, Math.Max(0, Round(s.Value * Roll())), block, changes);
                    if (Stat(b, "DamageRadius") is { } radius)
                        Set(radius, Math.Max(50, (int)Math.Round(radius.Value * Roll() / 10) * 10), block, changes);
                }
            }
            if (changes.Count == 0) continue;
            count++;
            ctx.Spoiler.Line($"{ctx.Items.Display(w.Name())}: {string.Join(", ", changes)}");
        }
        ctx.Log($"Weapon stats: {count} weapons changed.");
    }

    static int Round(double x) => (int)Math.Round(x, MidpointRounding.AwayFromZero);

    sealed record IntStat(XElement Element, int Value);

    static IntStat? Stat(XElement block, string name) =>
        block.Element(name) is { } e && int.TryParse(e.Value.Trim(), out var n) ? new IntStat(e, n) : null;

    static void Set(IntStat stat, int value, string block, List<string> changes)
    {
        if (value == stat.Value) return;
        stat.Element.Value = value.ToString();
        changes.Add($"{block}/{stat.Element.Name.LocalName} {stat.Value} -> {value}");
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

    /// <summary>Balanced trade-offs (health, cogs, move): about equal in worth, one per crew member.</summary>
    static readonly (int Health, int Cogs, int Move)[] Trades =
        [(0, 0, 0), (2, -1, 0), (-2, 1, 0), (2, 0, -1), (-2, 0, 1), (0, 1, -1), (0, -1, 1)];

    /// <summary>
    /// Crew stats: health and cog capacity (personas.xml BaseStats; every crew member inherits them from CREW) and move
    /// distance (entities.crew.xml Actor/MoveDistance, from the CREW entity) get per-member values. The captain stays.
    ///  - Balanced: one trade-off each, e.g. +2 health for -1 cog, or nothing.
    ///  - Chaos: health -3..+4, cogs -1..+2, move -1..+1, each on its own.
    /// </summary>
    public static void ApplyCrewStats(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var rng = ctx.RngFor("crewstats");
        bool balanced = ctx.Options.CrewStatsMode == Mode.Balanced;
        var personas = c.Xml("Definitions/personas.xml").Definitions("Persona").ToList();
        var entities = c.Xml("Definitions/entities.crew.xml").Definitions("Entity").ToDictionary(e => e.Name(), StringComparer.Ordinal);
        var baseStats = personas.First(p => p.Name() == "CREW").Element("BaseStats")!;
        int health = int.Parse(baseStats.Element("HitPoints")!.Value.Trim()), cogs = int.Parse(baseStats.Element("CogCapacity")!.Value.Trim());
        int move = int.Parse(entities["CREW"].Element("Actor")!.Element("MoveDistance")!.Value.Trim());

        ctx.Spoiler.Section($"Crew stats ({ctx.Options.CrewStatsMode}; base: health {health}, cogs {cogs}, move {move})");
        foreach (var p in personas.Where(p => p.Attribute("Template")?.Value == "CREW" && !p.IsAbstract()))
        {
            if (p.Element("TacticsEntity")?.Value.Trim() is not { } entityName || !entities.TryGetValue(entityName, out var entity)) continue;
            var (dh, dc, dm) = balanced ? rng.Pick(Trades) : (rng.Next(-3, 5), rng.Next(-1, 3), rng.Next(-1, 2));
            int h = Math.Max(4, health + dh), cg = Math.Max(1, cogs + dc), mv = Math.Max(4, move + dm);

            if (p.Element("BaseStats") is not { } stats) p.Add(stats = new XElement("BaseStats"));
            stats.SetElementValue("HitPoints", h);
            stats.SetElementValue("CogCapacity", cg);
            if (entity.Element("Actor") is not { } actor) entity.Add(actor = new XElement("Actor"));
            actor.SetElementValue("MoveDistance", mv);
            ctx.Spoiler.Line($"{ctx.Loc.Get("persona_" + p.Name()) ?? p.Name()}: health {h}, cogs {cg}, move {mv}");
        }
        ctx.Log("Crew stats randomized.");
    }
}
