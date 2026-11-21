using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SWH2.Core;
using SWH2.Modding;

namespace SWH2.Mods.Randomizer;

/// <summary>
/// Replaces the enemies in Definitions/spawn_tables.de (initial spawns and reinforcements) with enemies and spawn
/// groups from the whole game, without repeats inside a table. Flying stays flying and a group stays a group
/// (spawn points and unit counts). Entries that need a dedicated spawn slot (RequireSlot, e.g. snipers), promotion
/// tables, the tutorial, bosses, mission-specific variants and enemies that mission scripts check by name
/// stay as they are.
///
/// Stats are adjusted by cloning definitions, the way the game itself makes variants (e.g. enemy_atomic_skelebot
/// = enemy_pirate_skelebot with other health and faction): a clone "rnd_&lt;enemy&gt;_&lt;hash&gt;" templates the
/// new enemy and overrides health/armor, damage (via a cloned weapon), spawn cost and faction.
///  - Balanced: health, armor, damage and spawn cost of the replaced enemy.
///  - Chaos: own stats, capped by <see cref="EnemyLimit"/>; stronger enemies cost more spawn points, so fewer spawn.
/// </summary>
public static class EnemyShuffle
{
    static readonly string[] EnemyFiles =
    [
        "Definitions/entities.enemies.xml",
        "Definitions/entities.enemies.navy.xml",
        "Definitions/entities.enemies.pirates.xml",
        "Definitions/entities.enemies.atomics.xml",
    ];
    /// <summary>Enemy weapons only: weapons.xml holds the player's (with Rarity), whose clones could turn up as loot.</summary>
    static readonly string[] WeaponFiles =
    [
        "Definitions/weapons.navy.xml",
        "Definitions/weapons.pirates.xml",
        "Definitions/weapons.atomics.xml",
    ];
    const string GroupsFile = "Definitions/spawn_groups.de";
    const string TablesFile = "Definitions/spawn_tables.de";

    /// <summary>Health and damage are lists with one value per difficulty level.</summary>
    const int Levels = 6;
    /// <summary>A chaos enemy never costs more than this many times the spawn points of the one it replaces.</summary>
    const double MaxCostFactor = 3;

    /// <summary>Never used as a replacement and never replaced: mimic forms, bosses, mission-specific variants.</summary>
    static readonly Regex Special = new(@"mimic|kraken|boss|prison_break|keyholder|medal|big_steve|tutorial|fancy|heist|_(west|east)_caribbea_\d|_[we]c\d", RegexOptions.IgnoreCase);

    /// <summary>Promotions (what mimics turn into) and the scripted tutorial stay vanilla.</summary>
    static readonly Regex SkippedTable = new(@"^promotions|tutorial", RegexOptions.IgnoreCase);

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    sealed class Unit
    {
        public string Name = "";
        public bool Group, Flying;
        /// <summary>Units per spawn: 1, or a group's Entries plus one Deck pick.</summary>
        public int Size = 1;
        public string Faction = "";
        public double[]? Health, Damage;
        public double Cost = 1;
        // Single enemies.
        public string? Armor, Weapon;
        public double[]? Shoot, Explosive, Melee;
        public XmlDoc? Doc;
        // Spawn groups.
        public XElement? Element;
        public List<string> Members = [];
    }

    /// <summary>What a slot gets: an enemy or group plus the stat changes; realized as the unit itself or a clone.</summary>
    /// <remarks>MeleeScale applies to BaseMeleeDamage of enemies with a gun; for melee-only enemies DamageScale does.</remarks>
    sealed record Plan(Unit Unit, double[] HealthScale, double[] DamageScale, double[] MeleeScale, string? Faction, double? Cost, string? Armor)
    {
        public bool IsPlain => IsOne(HealthScale) && IsOne(DamageScale) && IsOne(MeleeScale) && Faction == null && Armor == null &&
                               (Cost == null || Math.Abs(Cost.Value - Unit.Cost) < 1e-6);
    }

    public static void Apply(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var o = ctx.Options;
        var rng = ctx.RngFor("enemies");
        var entities = new Defs(c, EnemyFiles, "Entity");
        var weapons = new Defs(c, WeaponFiles, "Weapon");
        var units = BuildUnits(c, entities, weapons);
        var clones = new Cloner(c, weapons, units);
        var watched = WatchedByScripts(c);
        bool Protected(Unit u) => watched.Contains(u.Name) || u.Members.Any(watched.Contains);

        var slots = c.Xml(TablesFile).Definitions("SpawnTable")
            .Where(t => !t.IsAbstract() && !SkippedTable.IsMatch(t.Name()))
            .SelectMany(t => (t.Element("Entries")?.Elements("Entry") ?? []).Select(e => (Table: t.Name(), Entry: e)))
            .Where(x => x.Entry.Attribute("Name") is { } n && units.TryGetValue(n.Value, out var u) && !Special.IsMatch(n.Value) && !Protected(u) && !x.Entry.AttrIsTrue("RequireSlot"))
            .ToList();
        var pool = slots.Select(x => units[x.Entry.Name()]).Distinct().OrderBy(u => u.Name, StringComparer.Ordinal).ToList();

        ctx.Spoiler.Section($"Enemies ({(o.EnemyStats == EnemyStats.Balanced ? "stats of the replaced enemy" : "own stats, limit " + o.EnemyLimit.Id)}, " +
                            $"{(o.EnemiesKeepSlotFaction ? "faction of the replaced enemy" : "own faction")})");
        string? table = null;
        var used = new HashSet<Unit>();
        foreach (var (t, entry) in slots)
        {
            if (t != table) { ctx.Spoiler.Line($"[{table = t}]"); used.Clear(); }
            var old = units[entry.Name()];
            var fits = pool.Where(u => u != old && u.Group == old.Group && u.Size == old.Size && u.Flying == old.Flying).ToList();
            var fresh = fits.Where(u => !used.Contains(u)).ToList();
            var pick = fresh.Count > 0 ? rng.Pick(fresh) : fits.Count > 0 ? rng.Pick(fits) : old;
            used.Add(pick);

            var plan = PlanFor(old, pick, o);
            var name = clones.Realize(plan);
            entry.SetAttributeValue("Name", name);
            ctx.Spoiler.Line($"    {old.Name} -> {pick.Name}{Describe(plan, old)}");
        }
        ctx.Log($"Enemies: {slots.Count} spawn table entries replaced from {pool.Count} enemy types ({clones.Count} adjusted variants).");
    }

    static Plan PlanFor(Unit old, Unit pick, RandomizerOptions o)
    {
        string? faction = o.EnemiesKeepSlotFaction && !pick.Faction.Equals(old.Faction, StringComparison.OrdinalIgnoreCase) ? old.Faction : null;
        if (o.EnemyStats == EnemyStats.Balanced)
            return new Plan(pick, Ratio(old.Health, pick.Health, double.PositiveInfinity), Ratio(old.Damage, pick.Damage, double.PositiveInfinity),
                Ratio(old.Melee, pick.Melee, double.PositiveInfinity), faction, old.Cost, pick.Group ? null : old.Armor ?? (pick.Armor != null ? "0" : null));

        var limit = o.EnemyLimit;
        var hs = Ratio(old.Health, pick.Health, limit.Health);
        var ds = Ratio(old.Damage, pick.Damage, limit.Damage);
        var ms = Ratio(old.Melee, pick.Melee, limit.Damage);
        double? cost = null;
        if (!limit.IsOff)
        {
            // Spawn points are budgeted per room: a stronger enemy costs more, so fewer of them spawn.
            double strength = Math.Sqrt(Relative(pick.Health, hs, old.Health) * Relative(pick.Damage, ds, old.Damage));
            cost = old.Cost * Math.Min(MaxCostFactor, Math.Max(1, strength));
        }
        return new Plan(pick, hs, ds, ms, faction, cost, null);
    }

    /// <summary>Per level: target/actual, but only scaling down to "limit × target" when limit is finite.</summary>
    static double[] Ratio(double[]? target, double[]? actual, double limit)
    {
        var r = Enumerable.Repeat(1.0, Levels).ToArray();
        if (target == null || actual == null) return r;
        for (int i = 0; i < Levels; i++)
        {
            if (actual[i] <= 0 || target[i] <= 0) continue;
            double want = target[i] / actual[i];
            r[i] = double.IsPositiveInfinity(limit) ? want : Math.Min(1, limit * want);
        }
        return r;
    }

    /// <summary>Average of the scaled values relative to the replaced enemy's (1 when unknown).</summary>
    static double Relative(double[]? values, double[] scale, double[]? reference)
    {
        if (values == null || reference == null) return 1;
        double a = values.Select((v, i) => v * scale[i]).Average(), b = reference.Average();
        return b > 0 ? a / b : 1;
    }

    static bool IsOne(double[] scale) => scale.All(s => Math.Abs(s - 1) < 1e-6);

    static string Describe(Plan p, Unit old)
    {
        var parts = new List<string>();
        if (p.Unit.Health is { } h)
        {
            var now = h.Select((v, i) => Scaled(v, p.HealthScale[i])).ToArray();
            parts.Add($"HP {Fmt(now)}{(old.Health != null ? $" (was {Fmt(old.Health)})" : "")}");
        }
        if (p.Unit.Damage is { } d)
        {
            var now = d.Select((v, i) => Scaled(v, p.DamageScale[i])).ToArray();
            parts.Add($"dmg {Fmt(now)}{(old.Damage != null ? $" (was {Fmt(old.Damage)})" : "")}");
        }
        if (p.Cost is { } cost && Math.Abs(cost - p.Unit.Cost) > 1e-6) parts.Add($"cost {cost.ToString("0.##", Inv)}");
        parts.Add($"faction {p.Faction ?? p.Unit.Faction}");
        return "    [" + string.Join(", ", parts) + "]";
    }

    // ---- reading the definitions ----------------------------------------------------------

    /// <summary>
    /// Enemies that missions check by definition name (Lua "== \"enemy_x\"", objectives in mission_rules.de, e.g.
    /// "kill all walking bombs"). Replacing them in spawn tables would break those missions, so they stay.
    /// </summary>
    static HashSet<string> WatchedByScripts(GameContent c)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lua in c.OriginalFiles.Where(f => f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)))
            foreach (Match m in Regex.Matches(c.ReadText(lua), @"[=~]=\s*""(enemy_\w+)""|""(enemy_\w+)""\s*[=~]="))
                names.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        foreach (Match m in Regex.Matches(c.ReadText("Definitions/mission_rules.de"), @"""(enemy_\w+)"""))
            names.Add(m.Groups[1].Value);
        return names;
    }

    /// <summary>Definitions by name (with their file) and lookups through the Template chain.</summary>
    sealed class Defs
    {
        // Case-sensitive: the game has both ENEMY_PIRATE_SWAB (abstract template) and enemy_pirate_swab.
        public readonly Dictionary<string, (XElement Element, XmlDoc Doc)> Map = new(StringComparer.Ordinal);

        public Defs(GameContent c, string[] files, string tag)
        {
            foreach (var f in files)
            {
                var doc = c.Xml(f);
                foreach (var e in doc.Definitions(tag)) Map[e.Name()] = (e, doc);
            }
        }

        public IEnumerable<XElement> Chain(string? name)
        {
            for (int i = 0; i < 16 && name != null && Map.TryGetValue(name, out var d); i++)
            {
                yield return d.Element;
                name = d.Element.Attribute("Template")?.Value;
            }
        }

        public string? Get(string? name, string path) =>
            Chain(name).Select(e => At(e, path)?.Value.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));

        public static XElement? At(XElement e, string path)
        {
            XElement? x = e;
            foreach (var part in path.Split('/')) x = x?.Element(part);
            return x;
        }
    }

    static Dictionary<string, Unit> BuildUnits(GameContent c, Defs entities, Defs weapons)
    {
        var units = new Dictionary<string, Unit>(StringComparer.Ordinal);
        foreach (var (name, (e, doc)) in entities.Map)
        {
            if (e.IsAbstract() || !name.StartsWith("enemy_", StringComparison.Ordinal)) continue;
            var weapon = entities.Get(name, "Actor/Weapon");
            var u = new Unit
            {
                Name = name,
                Doc = doc,
                Faction = entities.Get(name, "Actor/Faction") ?? "",
                Health = Values(entities.Get(name, "Health/HitPoints")),
                Armor = entities.Get(name, "Health/Armor"),
                Weapon = weapon != null && weapons.Map.ContainsKey(weapon) ? weapon : null,
                Melee = Values(entities.Get(name, "Actor/BaseMeleeDamage/EnemyAmount")),
                Cost = Number(entities.Get(name, "Actor/SpawnCost")) ?? 1,
                Flying = entities.Chain(name).Any(x =>
                    x.Name().Contains("FLYING", StringComparison.OrdinalIgnoreCase) ||
                    x.Element("TacticsNavigator")?.Element("SpawnSlot")?.Value.Trim() == "enemy_flying"),
            };
            if (u.Weapon != null)
            {
                u.Shoot = Values(weapons.Get(u.Weapon, "ShootDamage/EnemyAmount"));
                u.Explosive = Values(weapons.Get(u.Weapon, "ExplosiveDamage/EnemyAmount"));
            }
            u.Damage = Max(u.Shoot, u.Explosive) ?? u.Melee;
            units[name] = u;
        }

        foreach (var g in c.Xml(GroupsFile).Definitions("SpawnGroup"))
        {
            var always = (g.Element("Entries")?.Elements("Entry") ?? []).Select(x => x.Value.Trim()).ToList();
            var deck = (g.Element("Deck")?.Elements("Entry") ?? []).Select(x => x.Value.Trim()).ToList();
            if (always.Count + deck.Count == 0 || !always.Concat(deck).All(units.ContainsKey)) continue;
            var members = always.Concat(deck).Select(n => units[n]).ToList();
            units[g.Name()] = new Unit
            {
                Name = g.Name(),
                Group = true,
                Element = g,
                Size = always.Count + (deck.Count > 0 ? 1 : 0),
                Members = always.Concat(deck).Distinct().ToList(),
                Faction = members[0].Faction,
                Flying = members.All(m => m.Flying),
                Health = Aggregate(always, deck, units, u => u.Health),
                Damage = Aggregate(always, deck, units, u => u.Damage),
                Cost = Number(g.Element("SpawnCostOverride")?.Value) ??
                       always.Sum(n => units[n].Cost) + (deck.Count > 0 ? deck.Max(n => units[n].Cost) : 0),
            };
        }
        return units;
    }

    /// <summary>Per member and level: all Entries plus the strongest Deck entry (as the game counts spawn cost), divided by the group size.</summary>
    static double[]? Aggregate(List<string> always, List<string> deck, Dictionary<string, Unit> units, Func<Unit, double[]?> get)
    {
        var lists = always.Concat(deck).Select(n => get(units[n])).ToList();
        if (lists.Any(l => l == null)) return null;
        var r = new double[Levels];
        for (int i = 0; i < Levels; i++)
            r[i] = (always.Sum(n => get(units[n])![i]) + (deck.Count > 0 ? deck.Max(n => get(units[n])![i]) : 0)) / (always.Count + (deck.Count > 0 ? 1 : 0));
        return r;
    }

    static double[]? Max(double[]? a, double[]? b) =>
        a == null ? b : b == null ? a : a.Select((v, i) => Math.Max(v, b[i])).ToArray();

    /// <summary>"2,3,3,4" → one value per level (the last one repeated); null for formulas like "39+1*x".</summary>
    static double[]? Values(string? text)
    {
        if (text == null) return null;
        var parts = text.Split(',');
        var v = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, Inv, out v[i])) return null;
        return Enumerable.Range(0, Levels).Select(i => v[Math.Min(i, v.Length - 1)]).ToArray();
    }

    static double? Number(string? text) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, Inv, out var v) ? v : null;

    static double Scaled(double v, double s) => v <= 0 ? v : Math.Max(1, Math.Round(v * s, MidpointRounding.AwayFromZero));

    static string Fmt(double[] v) => string.Join(",", v.Select(x => x.ToString("0.##", Inv)));

    static string ScaledText(double[] v, double[] s) => Fmt(v.Select((x, i) => Scaled(x, s[i])).ToArray());

    // ---- writing the clones ---------------------------------------------------------------

    /// <summary>Creates (once) the cloned definitions a plan needs and returns the name to put in the spawn table.</summary>
    sealed class Cloner(GameContent c, Defs weapons, Dictionary<string, Unit> units)
    {
        readonly Dictionary<string, string> _made = new(StringComparer.Ordinal);
        public int Count => _made.Count;

        public string Realize(Plan p)
        {
            if (p.IsPlain) return p.Unit.Name;
            var key = string.Join("|", p.Unit.Name, Fmt(p.HealthScale), Fmt(p.DamageScale), Fmt(p.MeleeScale), p.Faction, p.Cost?.ToString("0.###", Inv), p.Armor);
            if (_made.TryGetValue(key, out var made)) return made;
            var name = $"rnd_{p.Unit.Name}_{(uint)ModContext.StableHash(key):x8}";
            _made[key] = name;
            if (p.Unit.Group) CloneGroup(p, name);
            else CloneEnemy(p, name);
            return name;
        }

        void CloneEnemy(Plan p, string name)
        {
            var u = p.Unit;
            var e = new XElement("Entity", new XAttribute("Name", name), new XAttribute("Template", u.Name));
            var health = new XElement("Health");
            if (u.Health != null && !IsOne(p.HealthScale)) health.Add(new XElement("HitPoints", ScaledText(u.Health, p.HealthScale)));
            if (p.Armor != null) health.Add(new XElement("Armor", p.Armor));
            if (health.HasElements) e.Add(health);

            var actor = new XElement("Actor");
            bool gun = u.Weapon != null && (u.Shoot != null || u.Explosive != null);
            if (gun && !IsOne(p.DamageScale)) actor.Add(new XElement("Weapon", CloneWeapon(u, p.DamageScale)));
            var melee = gun ? p.MeleeScale : p.DamageScale;
            if (u.Melee != null && !IsOne(melee))
                actor.Add(new XElement("BaseMeleeDamage", new XElement("EnemyAmount", ScaledText(u.Melee, melee))));
            if (p.Faction != null) actor.Add(new XElement("Faction", p.Faction));
            if (p.Cost is { } cost) actor.Add(new XElement("SpawnCost", cost.ToString("0.###", Inv)));
            if (actor.HasElements) e.Add(actor);
            u.Doc!.Root.Add(e);
        }

        string CloneWeapon(Unit u, double[] scale)
        {
            var key = "weapon|" + u.Weapon + "|" + Fmt(scale);
            if (_made.TryGetValue(key, out var made)) return made;
            var name = $"rnd_{u.Weapon}_{(uint)ModContext.StableHash(key):x8}";
            _made[key] = name;
            var w = new XElement("Weapon", new XAttribute("Name", name), new XAttribute("Template", u.Weapon));
            if (u.Shoot != null) w.Add(new XElement("ShootDamage", new XElement("EnemyAmount", ScaledText(u.Shoot, scale))));
            if (u.Explosive != null) w.Add(new XElement("ExplosiveDamage", new XElement("EnemyAmount", ScaledText(u.Explosive, scale))));
            weapons.Map[u.Weapon!].Doc.Root.Add(w);
            return name;
        }

        /// <summary>A copy of the group whose members are cloned with the same stat factors and faction.</summary>
        void CloneGroup(Plan p, string name)
        {
            var g = new XElement(p.Unit.Element!);
            g.SetAttributeValue("Name", name);
            foreach (var entry in g.Elements("Entries").Concat(g.Elements("Deck")).Elements("Entry"))
                entry.Value = Realize(new Plan(units[entry.Value.Trim()], p.HealthScale, p.DamageScale, p.DamageScale, p.Faction, null, null));
            g.Element("SpawnCostOverride")?.Remove();
            if (p.Cost is { } cost) g.Add(new XElement("SpawnCostOverride", cost.ToString("0.###", Inv)));
            c.Xml(GroupsFile).Root.Add(g);
        }
    }
}
