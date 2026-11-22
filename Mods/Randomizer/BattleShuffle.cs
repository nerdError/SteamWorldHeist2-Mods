using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SWH2.Core;

namespace SWH2.Mods.Randomizer;

/// <summary>How enemies fight and what the battlefield is made of: enemy weapons, enemy AI, explosive cover.</summary>
public static class BattleShuffle
{
    static readonly string[] EnemyWeaponFiles =
    [
        "Definitions/weapons.navy.xml",
        "Definitions/weapons.pirates.xml",
        "Definitions/weapons.atomics.xml",
    ];

    /// <summary>Enemies (and their weapons) that are machines with built-in guns or special mechanics: their weapons and AI stay.</summary>
    static readonly Regex Machine = new(@"turret|drone|mech|sphere|kraken|totem|mimic|bomb|megaphone|deployer|reviver_turret|fabio|retribution|eye", RegexOptions.IgnoreCase);

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static bool Eligible(string enemy) => !EnemyShuffle.Special.IsMatch(enemy) && !Machine.IsMatch(enemy);

    /// <summary>
    /// Enemies carry each other's guns (entities.enemies*.xml Actor/Weapon, the definition's own value; a weapon
    /// brings its model, projectile, sound and damage). Bosses, turrets, drones, mechs and other machines keep theirs.
    /// Runs before <see cref="EnemyShuffle"/>, so its Balanced stats still scale the damage to the replaced enemy.
    /// Balanced: only between guns of similar damage held the same way (one or two hands). Chaos: any gun.
    /// </summary>
    public static void ApplyEnemyWeapons(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var rng = ctx.RngFor("enemyweapons");
        bool balanced = ctx.Options.EnemyWeaponMode == Mode.Balanced;
        var weapons = new EnemyShuffle.Defs(c, EnemyWeaponFiles, "Weapon");

        var slots = EnemyShuffle.EnemyFiles
            .SelectMany(f => c.Xml(f).Definitions("Entity"))
            .Where(e => Eligible(e.Name()) && e.Element("Actor")?.Element("Weapon") is { } w && weapons.Map.ContainsKey(w.Value.Trim()) && Eligible(w.Value.Trim()))
            .Select(e => (Enemy: e.Name(), Weapon: e.Element("Actor")!.Element("Weapon")!))
            .ToList();

        double Strength(string weapon)
        {
            double Mid(string path) => EnemyShuffle.Values(weapons.Get(weapon, path)) is { } v ? v[2] : 0;
            double count = 1;
            var spread = weapons.Chain(weapon).Select(w => w.Element("Spread")?.Attribute("Count")?.Value).FirstOrDefault(v => v != null);
            if (spread != null && double.TryParse(spread.Split(',')[0], NumberStyles.Float, Inv, out var s)) count = s;
            return Math.Max(Mid("ShootDamage/EnemyAmount"), Mid("ExplosiveDamage/EnemyAmount")) * Math.Max(1, count);
        }

        // Balanced: bands of similar damage (sorted by strength, 4 guns per band), one- and two-handed apart.
        var group = new Dictionary<string, string>(StringComparer.Ordinal);
        if (balanced)
            foreach (var hands in slots.GroupBy(x => x.Weapon.Value.Contains("_2h") ? "2h" : "1h"))
            {
                var sorted = hands.OrderBy(x => Strength(x.Weapon.Value.Trim())).ThenBy(x => x.Enemy, StringComparer.Ordinal).ToList();
                for (int i = 0; i < sorted.Count; i++) group[sorted[i].Enemy] = $"{hands.Key} band {i / 4}";
            }

        ctx.Spoiler.Section($"Enemy weapons ({ctx.Options.EnemyWeaponMode})");
        int count = Slots.Shuffle(ctx, rng, slots.Select(x => Slot.Of(x.Enemy, balanced ? group[x.Enemy] : "", x.Weapon)));
        ctx.Log($"Enemy weapons: {count} of {slots.Count} enemies got another gun.");
    }

    /// <summary>The AI of gunmen: who keeps their distance, who rushes in, who hides.</summary>
    static readonly string[] RangedAi = ["ai_basic_range", "ai_machinegunner", "ai_shotgunner", "ai_sniper", "ai_basic_range_atomic"];
    static readonly string[] WildAi = ["ai_melee", "ai_berserker", "ai_navy_coward", "ai_navy_guard", "ai_navy_recruit"];

    /// <summary>
    /// Enemy AI (entities.enemies*.xml Actor/AI): ordinary enemies swap behaviors. Bosses, machines and AI that
    /// needs special actions (grenade throwers, totems, revivers) stay. Balanced: only gunman behaviors (a shotgunner
    /// may keep its distance like a sniper). Chaos: also melee rushers, berserkers, cowards and guards.
    /// </summary>
    public static void ApplyEnemyAi(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var rng = ctx.RngFor("enemyai");
        var pool = new HashSet<string>(ctx.Options.EnemyAiMode == Mode.Balanced ? RangedAi : RangedAi.Concat(WildAi), StringComparer.Ordinal);
        var slots = EnemyShuffle.EnemyFiles
            .SelectMany(f => c.Xml(f).Definitions("Entity"))
            .Where(e => Eligible(e.Name()) && e.Element("Actor")?.Element("AI") is { } ai && pool.Contains(ai.Value.Trim()))
            .Select(e => Slot.Of(e.Name(), "", e.Element("Actor")!.Element("AI")!))
            .ToList();
        ctx.Spoiler.Section($"Enemy AI ({ctx.Options.EnemyAiMode})");
        int count = Slots.Shuffle(ctx, rng, slots);
        ctx.Log($"Enemy AI: {count} of {slots.Count} enemy types behave differently.");
    }

    /// <summary>
    /// Explosive cover. Levels place cover through placeholders that each mission maps to real objects
    /// (missions*.de Placeholders/Placeholder Name→ReplaceWith). Explosive cover (ph_explosive_cover*, hanging
    /// explosives) gets a random kind of explosive: barrels, fire or ice. Besides, in some missions all weak low cover
    /// (Balanced: 20%, Chaos: 50%) or hanging cover (Balanced: 0%, Chaos: 30%) becomes explosive — chain reactions!
    /// </summary>
    public static void ApplyExplosiveCover(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var rng = ctx.RngFor("explosivecover");
        bool balanced = ctx.Options.CoverMode == Mode.Balanced;
        var missions = c.Find("Definitions", "missions*.de").SelectMany(f => c.Xml(f).Definitions("Mission")).ToList();

        static IEnumerable<XElement> Placeholders(XElement m) => m.Element("Placeholders")?.Elements("Placeholder") ?? [];
        static string Ph(XElement p) => p.Attribute("Name")?.Value ?? "";
        static string With(XElement p) => p.Attribute("ReplaceWith")?.Value ?? "";

        var all = missions.SelectMany(Placeholders).ToList();
        var barrels = all.Where(p => Ph(p).StartsWith("ph_explosive_cover", StringComparison.Ordinal) && !With(p).StartsWith("deck_", StringComparison.Ordinal))
            .Select(With).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        var hanging = all.Where(p => Ph(p) == "ph_hanging_explosive").Select(With).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

        ctx.Spoiler.Section($"Explosive cover ({ctx.Options.CoverMode})");
        int changed = 0;
        void Set(XElement mission, XElement p, string value, string what)
        {
            if (With(p) == value) return;
            ctx.Spoiler.Line($"{mission.Name()}: {what} {Ph(p)} -> {value}    (was {With(p)})");
            p.SetAttributeValue("ReplaceWith", value);
            changed++;
        }

        foreach (var m in missions)
        {
            // Decide per mission in a fixed order, so the result only depends on the seed.
            bool lowBlows = rng.NextDouble() < (balanced ? 0.2 : 0.5);
            bool hangBlows = rng.NextDouble() < (balanced ? 0 : 0.3);
            string barrel = barrels.Count > 0 ? rng.Pick(barrels) : "";
            string hang = hanging.Count > 0 ? rng.Pick(hanging) : "";
            foreach (var p in Placeholders(m).ToList())
            {
                var ph = Ph(p);
                if (ph.StartsWith("ph_explosive_cover", StringComparison.Ordinal) && barrel != "" && !With(p).StartsWith("deck_", StringComparison.Ordinal)) Set(m, p, barrel, "explosives");
                else if (ph == "ph_hanging_explosive" && hang != "") Set(m, p, hang, "explosives");
                else if (ph == "ph_cover_weak_low" && lowBlows && barrel != "") Set(m, p, barrel, "all low cover explodes:");
                else if (ph == "ph_hanging_cover" && hangBlows && hang != "") Set(m, p, hang, "all hanging cover explodes:");
            }
        }
        ctx.Log($"Explosive cover: {changed} placeholder mappings changed.");
    }
}
