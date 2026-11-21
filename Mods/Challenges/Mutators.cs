using System.Text.RegularExpressions;
using System.Xml.Linq;
using SWH2.Core;
using SWH2.Modding;

namespace SWH2.Mods.Challenges;

/// <summary>
/// Whole-game mutators: pure data edits, so they apply to every mission (scripted or procedural) and every actor.
/// Values are edited in place where possible; added elements make the file a full rewrite (still lossless XML).
/// </summary>
public static class Mutators
{
    const string ExplosionEffect = "explosion_medium";

    // ---- 1 HP crew ---------------------------------------------------------------------------------------------

    /// <summary>Every persona's base HP becomes 1 and every HP bonus (level-up perks, armor items, weapons) 0.</summary>
    public static void CrewOneHp(ModContext ctx)
    {
        ctx.Report.Section("Crew have 1 HP");
        int personas = 0, bonuses = 0;
        foreach (var p in ctx.Content.Xml("Definitions/personas.xml").Definitions("Persona"))
            if (p.Element("BaseStats")?.Element("HitPoints") is { } hp && hp.Value.Trim() != "1")
            {
                SetText(hp, "1");
                personas++;
            }

        var files = ctx.Content.Find("Definitions", "job_upgrades*.xml")
            .Concat(ctx.Content.Find("Definitions", "weapons*.xml"))
            .Concat(["Definitions/utilities.xml", "Definitions/hats.xml"]);
        foreach (var file in files)
            foreach (var def in ctx.Content.Xml(file).Definitions())
                // Negative bonuses too (the Reaper's "Close to Death"): 1 - 1 would be a dead bot.
                foreach (var hp in def.Descendants("HitPoints").Where(h => h.Parent?.Name.LocalName is "PassiveStats" or "Stats"))
                {
                    if (hp.Value.Trim() == "0") continue;
                    ctx.Report.Line($"HP bonus removed: {def.Name()} (was {hp.Value.Trim()})");
                    SetText(hp, "0");
                    bonuses++;
                }
        ctx.Log($"Crew have 1 HP: {personas} persona templates, {bonuses} HP bonuses removed.");
    }

    // ---- 1 HP enemies ------------------------------------------------------------------------------------------

    /// <summary>
    /// Every enemy gets 1 HP. Bosses keep their HP, and so does every enemy that a script with HP logic (health
    /// point triggers for boss phases, reading or setting HP) looks up by name or definition: 1 HP would skip those phases.
    /// </summary>
    public static void EnemiesOneHp(ModContext ctx)
    {
        ctx.Report.Section("Enemies have 1 HP");
        var lua = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in ctx.Content.OriginalFiles.Where(f => f.StartsWith("Lua/", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)))
        {
            var text = ctx.Content.ReadText(f);
            if (!Regex.IsMatch(text, "HealthPointTrigger|ActorHealth|ActorMaxHealth")) continue;
            // Only lookups and HP calls: the same scripts also spawn ordinary reinforcements by name.
            foreach (Match m in Regex.Matches(text, @"(?:HealthPointTrigger|ActorHealth|ActorMaxHealth|EntityWithDefinition|EntityHasDefinition|EntityWithName|EntityIsNamed)[^\n]*?""([A-Za-z0-9_]+)"""))
                lua.Add(m.Groups[1].Value);
        }
        // Ordinary enemies that random spawns use too (guards, seabots) get 1 HP even when such a script names them.
        var spawnTables = string.Concat(ctx.Content.Find("Definitions", "spawn_*.de").Select(ctx.Content.ReadText));
        lua.RemoveWhere(name => spawnTables.Contains($"\"{name}\""));

        var entities = AllEntities(ctx);
        string? OriginalHp(string name) => Resolve(entities, name, e => e.Element("Health")?.Element("HitPoints")?.Value.Trim());
        bool IsBoss(string name)
        {
            for (int guard = 0; guard < 20 && entities.TryGetValue(name, out var e); guard++)
            {
                if (name.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                name = e.Attribute("Template")?.Value ?? "";
            }
            return false;
        }

        // Resolve before editing: a kept enemy that inherits HP from an edited template gets its own copy.
        var enemies = ctx.Content.Find("Definitions", "entities.enemies*.xml").SelectMany(f => ctx.Content.Xml(f).Definitions("Entity")).ToList();
        var original = enemies.ToDictionary(e => e, e => OriginalHp(e.Name()));
        int set = 0, kept = 0;
        foreach (var e in enemies)
        {
            if (original[e] is null) continue;
            bool keep = IsBoss(e.Name()) || lua.Contains(e.Name());
            var own = e.Element("Health")?.Element("HitPoints");
            if (keep)
            {
                if (!e.IsAbstract()) { kept++; ctx.Report.Line($"Keeps its HP: {e.Name()} ({original[e]})"); }
                if (own == null)
                {
                    var health = e.Element("Health") ?? AddChild(e, new XElement("Health"));
                    health.Add(new XElement("HitPoints", original[e]));
                }
                continue;
            }
            if (own == null || own.Value.Trim() == "1") continue;
            SetText(own, "1");
            set++;
        }
        ctx.Log($"Enemies have 1 HP: {set} definitions set to 1 HP, {kept} bosses and HP-scripted enemies keep theirs.");
    }

    // ---- explosive shots ---------------------------------------------------------------------------------------

    /// <summary>
    /// Every gun (crew and enemy) gets explosive damage on top of its shot damage, like the Skelectric sniper
    /// (sniper_01_rare): 1 damage per projectile in a small radius, friendly fire as usual for explosions.
    /// Bullets without an impact effect get one, so the blast is visible.
    /// </summary>
    public static void ExplosiveShots(ModContext ctx, int radius)
    {
        ctx.Report.Section("Every shot explodes");
        var weapons = new Dictionary<string, XElement>();
        foreach (var file in ctx.Content.Find("Definitions", "weapons*.xml"))
            foreach (var w in ctx.Content.Xml(file).Definitions("Weapon"))
                weapons.TryAdd(w.Name(), w);
        string? W(string name, Func<XElement, string?> get) => Resolve(weapons, name, get);

        var projectiles = new SortedSet<string>(StringComparer.Ordinal);
        int n = 0;
        foreach (var w in weapons.Values.Where(w => !w.IsAbstract()))
        {
            var name = w.Name();
            if (W(name, e => e.Element("Projectile")?.Value.Trim()) is not { Length: > 0 } projectile) continue;
            if (W(name, e => e.Element("ExplosiveDamage")?.Element("DamageRadius")?.Value.Trim()) is { Length: > 0 } r && r != "0") continue;

            int Count(string? v) => int.TryParse(v, out var c) && c > 0 ? c : 1;
            int perShot = Count(W(name, e => e.Element("ShotCount")?.Value.Trim()))
                * Count(W(name, e => e.Element("RapidFire")?.Attribute("Count")?.Value))
                * Count(W(name, e => e.Element("Spread")?.Attribute("Count")?.Value));

            var ex = w.Element("ExplosiveDamage") ?? AddChild(w, new XElement("ExplosiveDamage"));
            if (W(name, e => e.Element("ExplosiveDamage")?.Element("Amount")?.Value.Trim()) is null)
                ex.Add(new XElement("Amount", perShot));
            ex.Add(new XElement("DamageRadius", radius));
            ex.Add(new XElement("SplashReduction", 0));
            projectiles.Add(projectile);
            n++;
        }

        var entities = AllEntities(ctx);
        int effects = 0;
        foreach (var p in projectiles)
        {
            if (!entities.TryGetValue(p, out var e) || Resolve(entities, p, x => x.Element("Projectile")?.Element("EffectOnEnd")?.Value.Trim()) is { Length: > 0 }) continue;
            var proj = e.Element("Projectile") ?? AddChild(e, new XElement("Projectile"));
            proj.Add(new XElement("EffectOnEnd", ExplosionEffect));
            effects++;
        }
        ctx.Report.Line($"{n} weapons explode on impact (radius {radius}); {effects} projectiles got an explosion effect.");
        ctx.Log($"Every shot explodes: {n} weapons, {effects} projectile effects.");
    }

    // ---- death blasts ------------------------------------------------------------------------------------------

    const string DeathBlast = "swh2_death_blast";

    /// <summary>Every bot (crew and enemies) explodes when scrapped — next to a crowd, that's a chain reaction.</summary>
    public static void DeathBlasts(ModContext ctx, int damage)
    {
        ctx.Report.Section("Bots explode when scrapped");
        var modifiers = ctx.Content.Xml("Definitions/modifiers.xml");
        if (modifiers.Definitions("Modifier").Any(m => m.Name() == DeathBlast))
            throw new InvalidOperationException("modifiers.xml already contains the death blast (built from a modded file?)");
        modifiers.Root.Add(new XElement("Modifier", new XAttribute("Name", DeathBlast),
            new XElement("Events",
                new XElement("Event", new XAttribute("On", "Death"), new XAttribute("Handler", "Explode"),
                    new XElement("Damage",
                        new XElement("Amount", damage),
                        new XElement("DamageRadius", 400),
                        new XElement("HitForce", new XAttribute("Constant", "4000"), new XAttribute("DamageFactor", "20"))),
                    new XElement("Effect", ExplosionEffect)))));

        // Innate modifier lists merge with the template's, so the root templates would be enough; entities with their
        // own list get it too in case a list replaces the template's (modifiers are unique, duplicates are harmless).
        int n = 0;
        foreach (var file in new[] { "Definitions/entities.crew.xml" }.Concat(ctx.Content.Find("Definitions", "entities.enemies*.xml")))
            foreach (var e in ctx.Content.Xml(file).Definitions("Entity"))
            {
                var own = e.Element("Modifiers")?.Element("InnateModifiers");
                if (own == null && e.Attribute("Template") != null) continue;
                var mods = e.Element("Modifiers") ?? AddChild(e, new XElement("Modifiers"));
                var list = mods.Element("InnateModifiers") ?? AddChild(mods, new XElement("InnateModifiers"));
                list.Add(new XElement("Modifier", DeathBlast));
                n++;
            }
        ctx.Report.Line($"{n} entity definitions explode on death ({damage} damage).");
        ctx.Log($"Death blasts: {n} entity definitions.");
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    static Dictionary<string, XElement> AllEntities(ModContext ctx)
    {
        var all = new Dictionary<string, XElement>();
        foreach (var file in ctx.Content.Find("Definitions", "entities*.xml"))
            foreach (var e in ctx.Content.Xml(file).Definitions("Entity"))
                all.TryAdd(e.Name(), e);
        return all;
    }

    /// <summary>First value along the Template chain.</summary>
    static string? Resolve(Dictionary<string, XElement> defs, string name, Func<XElement, string?> get)
    {
        for (int guard = 0; guard < 20 && defs.TryGetValue(name, out var e); guard++)
        {
            if (get(e) is { } v) return v;
            name = e.Attribute("Template")?.Value ?? "";
        }
        return null;
    }

    /// <summary>Edits the existing text node so the file is patched in place.</summary>
    static void SetText(XElement e, string value)
    {
        if (e.Nodes().Count() == 1 && e.FirstNode is XText t) t.Value = value;
        else e.Value = value;
    }

    static XElement AddChild(XElement parent, XElement child)
    {
        parent.Add(child);
        return child;
    }
}
