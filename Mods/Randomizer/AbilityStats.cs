using System.Globalization;
using System.Xml.Linq;
using SWH2.Core;

namespace SWH2.Mods.Randomizer;

/// <summary>
/// Numbers of utilities and of class/personal abilities. Only a definition's own values change (no new elements), and
/// actions that enemies also use stay as they are.
/// </summary>
public static class AbilityStats
{
    const string UtilitiesFile = "Definitions/utilities.xml";
    const string ActionsFile = "Definitions/actions.xml";
    static readonly string[] UpgradeFiles = ["Definitions/job_upgrades.xml", "Definitions/job_upgrades.personal.xml"];
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A number in the XML: an element's text or an attribute (e.g. Property@HealAmount).</summary>
    sealed class Num
    {
        public string Label = "";
        public XElement? Element;
        public XAttribute? Attribute;
        /// <summary>Higher is worse (cooldowns, costs), for the balanced direction.</summary>
        public bool Lower;
        public string Text { get => Element?.Value.Trim() ?? Attribute!.Value; set { if (Element != null) Element.Value = value; else Attribute!.Value = value; } }
        public double Value => double.Parse(Text, Inv);
        public bool IsInt => !Text.Contains('.');
    }

    // ---- utilities --------------------------------------------------------------------------

    /// <summary>
    /// Utilities: the action of usable ones (grenade damage and radius, repair amount, charges, cooldown) and the
    /// passive Stats of worn ones (move, health, armor, aim, cogs, crit, jump height, xp bonus…).
    ///  - Balanced: each utility gets stronger or weaker by up to ±variance and its price follows, so it's still worth its money.
    ///  - Chaos: every number on its own, price unchanged.
    /// </summary>
    public static void ApplyUtilities(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var rng = ctx.RngFor("utilities");
        double v = ctx.Options.UtilityVariance / 100.0;
        bool balanced = ctx.Options.UtilityMode == Mode.Balanced;
        var actions = Actions(c);
        var enemyActions = EnemyActions(c);
        var done = new HashSet<XElement>(); // an action used by two utilities changes once

        ctx.Spoiler.Section($"Utility stats ({ctx.Options.UtilityMode}, ±{ctx.Options.UtilityVariance}%)");
        int count = 0;
        foreach (var u in c.Xml(UtilitiesFile).Definitions("Utility").Where(u => !u.IsAbstract()))
        {
            var nums = new List<Num>();
            foreach (var s in u.Element("Stats")?.Elements() ?? [])
                if (IsNumber(s.Value)) nums.Add(new Num { Label = s.Name.LocalName, Element = s });
            if (u.Element("Action")?.Value.Trim() is { } a && actions.TryGetValue(a, out var action) && !enemyActions.Contains(a) && done.Add(action))
                nums.AddRange(ActionNumbers(action));
            if (nums.Count == 0) continue;

            double factor = 1;
            var changes = balanced ? Scale(rng, nums, v, out factor) : Vary(rng, nums, v);
            if (balanced && changes.Count > 0 && u.Element("LootCost") is { } cost && int.TryParse(cost.Value.Trim(), out var price) && price > 0)
            {
                int now = Math.Max(5, (int)Math.Round(price * factor / 5) * 5);
                if (now != price) { cost.Value = now.ToString(Inv); changes.Add($"price {price} -> {now}"); }
            }
            if (changes.Count == 0) continue;
            count++;
            ctx.Spoiler.Line($"{ctx.Items.Display(u.Name())}: {string.Join(", ", changes)}");
        }
        ctx.Log($"Utility stats: {count} utilities changed.");
    }

    // ---- abilities --------------------------------------------------------------------------

    /// <summary>
    /// Class and personal abilities (job_upgrades*.xml): their action's cooldown, charges, damage, healing, range,
    /// and the upgrade's cog cost. Passive stat perks (+1 health…) and modifiers stay.
    ///  - Balanced: an ability that costs cogs gets one step stronger or weaker (one of its numbers), and costs one cog
    ///    more or less for it. Free core abilities (level 0) stay.
    ///  - Chaos: every number on its own, cog cost 0–5.
    /// </summary>
    public static void ApplyAbilities(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var rng = ctx.RngFor("abilities");
        double v = ctx.Options.UtilityVariance / 100.0;
        bool balanced = ctx.Options.AbilityMode == Mode.Balanced;
        var actions = Actions(c);
        var enemyActions = EnemyActions(c);
        var done = new HashSet<XElement>();

        ctx.Spoiler.Section($"Ability stats ({ctx.Options.AbilityMode})");
        int count = 0;
        foreach (var up in UpgradeFiles.SelectMany(f => c.Xml(f).Definitions("JobUpgrade")).Where(u => !u.IsAbstract()))
        {
            var nums = new List<Num>();
            if (up.Element("Action")?.Value.Trim() is { } a && actions.TryGetValue(a, out var action) && !enemyActions.Contains(a) && done.Add(action))
                nums.AddRange(ActionNumbers(action));
            var cog = up.Element("CogCost") is { } cc && int.TryParse(cc.Value.Trim(), out _) ? new Num { Label = "cogs", Element = cc, Lower = true } : null;

            var changes = new List<string>();
            if (balanced)
            {
                if (cog == null || nums.Count == 0) continue;
                // One step: +1 (or -1 for cooldowns) on one number, paid for with a cog — or the other way round.
                int dir = rng.Next(3) - 1;
                if (dir == 0) continue;
                var n = rng.Pick(nums);
                int cogs = (int)cog.Value;
                if (dir < 0 && cogs == 0) dir = 1; // can't get cheaper than free
                var step = n.Lower ? -dir : dir;
                var now = n.Value + (n.IsInt ? step : step * Math.Max(0.1, n.Value * 0.25));
                if (now < (n.Lower ? 1 : n.Value > 0 ? 1 : 0)) continue; // a cooldown/charge/amount never drops to nothing
                Set(n, now, changes);
                Set(cog, cogs + dir, changes);
            }
            else
            {
                changes = Vary(rng, nums, v);
                if (cog != null) Set(cog, Math.Min(5, Math.Max(0, cog.Value + rng.Next(-1, 2))), changes);
            }
            if (changes.Count == 0) continue;
            count++;
            ctx.Spoiler.Line($"{Display(ctx, up.Name())}: {string.Join(", ", changes)}");
        }
        ctx.Log($"Ability stats: {count} abilities changed.");
    }

    // ---- shared -------------------------------------------------------------------------------

    static Dictionary<string, XElement> Actions(GameContent c) =>
        c.Xml(ActionsFile).Definitions("Action").GroupBy(a => a.Name(), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    static HashSet<string> EnemyActions(GameContent c) =>
        new(EnemyShuffle.EnemyFiles.SelectMany(f => c.Xml(f).Root.Descendants("Action")).Select(a => a.Value.Trim()), StringComparer.Ordinal);

    /// <summary>The gameplay numbers of an action (its own values only).</summary>
    static IEnumerable<Num> ActionNumbers(XElement action)
    {
        foreach (var block in new[] { "ShootDamage", "ExplosiveDamage" })
            if (action.Element(block) is { } b)
            {
                if (b.Element("Amount") is { } am && IsNumber(am.Value) && double.Parse(am.Value.Trim(), Inv) > 0)
                    yield return new Num { Label = block + "/Amount", Element = am };
                if (b.Element("DamageRadius") is { } r && IsNumber(r.Value)) yield return new Num { Label = block + "/DamageRadius", Element = r };
            }
        if (action.Element("Cooldown") is { } cd && IsNumber(cd.Value) && double.Parse(cd.Value.Trim(), Inv) > 0)
            yield return new Num { Label = "Cooldown", Element = cd, Lower = true };
        if (action.Element("StartingCharges") is { } ch && IsNumber(ch.Value) && double.Parse(ch.Value.Trim(), Inv) > 0)
            yield return new Num { Label = "Charges", Element = ch };
        if (action.Element("NavRange") is { } nr && IsNumber(nr.Value) && double.Parse(nr.Value.Trim(), Inv) > 0)
            yield return new Num { Label = "Range", Element = nr };
        foreach (var p in action.Elements("Property"))
            foreach (var name in new[] { "HealAmount", "Points" })
                if (p.Attribute(name) is { } at && IsNumber(at.Value))
                    yield return new Num { Label = name, Attribute = at };
    }

    /// <summary>Chaos: each number changes on its own. Small integers change by ±1 now and then, so 1s and 2s move too.</summary>
    static List<string> Vary(Random rng, List<Num> nums, double v)
    {
        var changes = new List<string>();
        foreach (var n in nums)
        {
            double x = n.Value, y = x * (1 + (rng.NextDouble() * 2 - 1) * v);
            if (n.IsInt)
            {
                int r = (int)Math.Floor(y) + (rng.NextDouble() < y - Math.Floor(y) ? 1 : 0);
                if (r == (int)x && Math.Abs(x) <= 3 && rng.NextDouble() < v) r += rng.Next(2) * 2 - 1;
                Set(n, Math.Max(1, r), changes);
            }
            else Set(n, Math.Max(0.05, y), changes);
        }
        return changes;
    }

    /// <summary>Balanced: one factor for the whole utility (cooldowns the other way); returns it for the price.</summary>
    static List<string> Scale(Random rng, List<Num> nums, double v, out double factor)
    {
        // Small integers can only move in whole steps, so the factor is snapped to what the first one can do.
        var f = 1 + (rng.NextDouble() * 2 - 1) * v;
        var lead = nums.FirstOrDefault(n => !n.Lower) ?? nums[0];
        if (lead.IsInt)
        {
            int target = Math.Max(1, (int)Math.Round(lead.Value * (lead.Lower ? 1 / f : f), MidpointRounding.AwayFromZero));
            f = lead.Lower ? lead.Value / target : target / lead.Value;
        }
        factor = f;
        var changes = new List<string>();
        foreach (var n in nums)
        {
            var y = n.Lower ? n.Value / f : n.Value * f;
            Set(n, n.IsInt ? Math.Max(1, Math.Round(y, MidpointRounding.AwayFromZero)) : y, changes);
        }
        return changes;
    }

    static void Set(Num n, double value, List<string> changes)
    {
        var old = n.Text;
        var text = n.IsInt ? ((int)Math.Round(value)).ToString(Inv) : Math.Round(value, 2).ToString("0.##", Inv);
        if (text == old) return;
        n.Text = text;
        changes.Add($"{n.Label} {old} -> {text}");
    }

    static bool IsNumber(string s) => double.TryParse(s.Trim(), NumberStyles.Float, Inv, out _) && s.Trim().Length > 0;

    static string Display(RandomizerContext ctx, string upgrade) =>
        ctx.Loc.Get("jobupgrade_" + upgrade) is { } name ? $"{name} [{upgrade}]" : upgrade;
}
