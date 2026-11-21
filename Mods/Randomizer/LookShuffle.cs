using System.Text.RegularExpressions;
using System.Xml.Linq;
using SWH2.Core;

namespace SWH2.Mods.Randomizer;

/// <summary>Looks: enemy and crew hats, NPC models, weapon models and projectiles.</summary>
public static class LookShuffle
{
    const string NpcFile = "Definitions/entities.npc.xml";
    const string CrewFile = "Definitions/entities.crew.xml";
    const string WeaponsFile = "Definitions/weapons.xml";

    /// <summary>
    /// Enemy hats (Actor/Hat and Actor/RareHat in entities.enemies.*.xml) swap between enemies. Hats are what enemies
    /// drop when their hat is shot off, so it's a permutation: every hat still drops from some enemy.
    /// </summary>
    public static void ApplyEnemyHats(RandomizerContext ctx)
    {
        var rng = ctx.RngFor("hats");
        ctx.Spoiler.Section("Enemy hats");
        int count = 0;
        foreach (var tag in new[] { "Hat", "RareHat" })
        {
            var slots = EnemyShuffle.EnemyFiles
                .SelectMany(f => ctx.Content.Xml(f).Definitions("Entity"))
                .Select(e => (Enemy: e.Name(), Hat: e.Element("Actor")?.Element(tag)))
                .Where(x => x.Hat != null && x.Hat.Value.Trim().Length > 0 && ctx.Items.Exists(x.Hat.Value.Trim()))
                .ToList();
            var hats = slots.Select(x => x.Hat!.Value.Trim()).ToList();
            rng.Shuffle(hats);
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].Hat!.Value.Trim() == hats[i]) continue;
                ctx.Spoiler.Line($"{slots[i].Enemy} {(tag == "Hat" ? "hat" : "rare hat")}: {ctx.Items.Display(hats[i])}    (was {ctx.Items.Display(slots[i].Hat!.Value.Trim())})");
                slots[i].Hat!.Value = hats[i];
                count++;
            }
        }
        ctx.Log($"Enemy hats: {count} changed.");
    }

    /// <summary>
    /// NPCs in bars, shops and on locations (entities.npc.xml) swap models (SkinnedCharacter/File). NPCs that scripts
    /// refer to by name (cutscene actors, who get animations played on them) keep theirs, and models only swap between
    /// NPCs that use the same idle animation setting, so nobody ends up without animations.
    /// </summary>
    public static void ApplyNpcs(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var rng = ctx.RngFor("npcs");
        var scripted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lua in c.OriginalFiles.Where(f => f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)))
            foreach (Match m in Regex.Matches(c.ReadText(lua), "[A-Za-z0-9_]+"))
                scripted.Add(m.Value);

        var defs = c.Xml(NpcFile).Definitions("Entity").ToDictionary(e => e.Name(), StringComparer.Ordinal);
        string? Animation(XElement e)
        {
            for (int i = 0; i < 16 && e != null; i++)
            {
                if (e.Element("SkinnedCharacter")?.Element("Animation")?.Value.Trim() is { Length: > 0 } a) return a;
                if (e.Attribute("Template")?.Value is not { } t || !defs.TryGetValue(t, out e!)) break;
            }
            return null;
        }

        var npcs = defs.Values
            .Where(e => !e.IsAbstract() && !scripted.Contains(e.Name()) && e.Element("SkinnedCharacter")?.Element("File") is { } f && f.Value.Trim().Length > 0)
            .Select(e => (Npc: e.Name(), File: e.Element("SkinnedCharacter")!.Element("File")!, Animation: Animation(e)))
            .Where(x => x.Animation is null or "Idle")
            .ToList();

        ctx.Spoiler.Section("NPC looks");
        int count = 0;
        foreach (var group in npcs.GroupBy(x => x.Animation ?? ""))
        {
            var list = group.ToList();
            var models = list.Select(x => x.File.Value.Trim()).ToList();
            rng.Shuffle(models);
            for (int i = 0; i < list.Count; i++)
            {
                var was = list[i].File.Value.Trim();
                if (was == models[i]) continue;
                list[i].File.Value = models[i];
                ctx.Spoiler.Line($"{list[i].Npc}: {Path.GetFileNameWithoutExtension(models[i])}    (was {Path.GetFileNameWithoutExtension(was)})");
                count++;
            }
        }
        ctx.Log($"NPC looks: {count} of {npcs.Count} NPCs changed.");
    }

    /// <summary>
    /// Crew hats: each crew member's own hat (entities.crew.xml crew_*/Actor/Hat) goes to another crew member. The
    /// captain keeps his. Starting outsets (outsets.xml Crew/Hat) follow, so Daisy and Wesley start with their new hats.
    /// </summary>
    public static void ApplyCrewHats(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var rng = ctx.RngFor("crewhats");
        var crew = c.Xml(CrewFile).Definitions("Entity")
            .Where(e => !e.IsAbstract() && e.Name().StartsWith("crew_", StringComparison.Ordinal) && !e.Name().Contains("captain"))
            .Select(e => (Entity: e.Name(), Hat: e.Element("Actor")?.Element("Hat")))
            .Where(x => x.Hat != null && x.Hat.Value.Trim().Length > 0)
            .ToList();
        var hats = crew.Select(x => x.Hat!.Value.Trim()).ToList();
        var before = hats.ToList();
        rng.Shuffle(hats);

        var byEntity = new Dictionary<string, (string Was, string Now)>(StringComparer.Ordinal);
        ctx.Spoiler.Section("Crew hats");
        for (int i = 0; i < crew.Count; i++)
        {
            crew[i].Hat!.Value = hats[i];
            byEntity[crew[i].Entity] = (before[i], hats[i]);
            if (before[i] != hats[i]) ctx.Spoiler.Line($"{crew[i].Entity}: {ctx.Items.Display(hats[i])}    (was {ctx.Items.Display(before[i])})");
        }

        var entityOf = c.Xml("Definitions/personas.xml").Definitions("Persona")
            .Where(p => p.Element("TacticsEntity") != null)
            .ToDictionary(p => p.Name(), p => p.Element("TacticsEntity")!.Value.Trim(), StringComparer.Ordinal);
        foreach (var member in c.Xml("Definitions/outsets.xml").Root.Descendants("Crew").Where(e => e.Element("CrewName") != null))
            if (member.Element("Hat") is { } hat && entityOf.TryGetValue(member.Element("CrewName")!.Value.Trim(), out var entity) &&
                byEntity.TryGetValue(entity, out var swap) && hat.Value.Trim() == swap.Was)
                hat.Value = swap.Now;
        ctx.Log($"Crew hats: {before.Where((h, i) => h != hats[i]).Count()} of {crew.Count} crew members changed.");
    }

    /// <summary>Player weapons (weapons.xml, with a Rarity) and their class (Job, through Template).</summary>
    static List<(XElement Weapon, string Job)> PlayerWeapons(GameContent c)
    {
        var defs = c.Xml(WeaponsFile).Definitions("Weapon").ToDictionary(e => e.Name(), StringComparer.Ordinal);
        string Job(XElement e)
        {
            for (int i = 0; i < 16 && e != null; i++)
            {
                if (e.Element("Job")?.Value.Trim() is { Length: > 0 } job) return job;
                if (e.Attribute("Template")?.Value is not { } t || !defs.TryGetValue(t, out e!)) break;
            }
            return "";
        }
        return defs.Values.Where(w => !w.IsAbstract() && w.Element("Rarity") != null).Select(w => (w, Job(w))).ToList();
    }

    /// <summary>
    /// Weapon looks: model (Character) and icon (Portrait) swap together between player weapons; stats, sounds and
    /// projectiles stay with the weapon. Balanced: inside the weapon class (so it's still held the right way).
    /// </summary>
    public static void ApplyWeaponLooks(RandomizerContext ctx)
    {
        var rng = ctx.RngFor("weaponlooks");
        bool balanced = ctx.Options.WeaponLookMode == Mode.Balanced;
        var slots = PlayerWeapons(ctx.Content)
            .Select(x => (x.Weapon, x.Job, Model: x.Weapon.Element("Character"), Icon: x.Weapon.Element("Portrait")))
            .Where(x => x.Model != null && x.Icon != null && x.Model.Value.Trim().Length > 0 && x.Icon.Value.Trim().Length > 0)
            .ToList();

        ctx.Spoiler.Section($"Weapon looks ({(balanced ? "inside each class" : "any weapon")})");
        int count = 0;
        foreach (var group in slots.GroupBy(x => balanced ? x.Job : ""))
        {
            var list = group.ToList();
            var looks = list.Select(x => (Name: x.Weapon.Name(), Model: x.Model!.Value.Trim(), Icon: x.Icon!.Value.Trim())).ToList();
            rng.Shuffle(looks);
            for (int i = 0; i < list.Count; i++)
            {
                if (looks[i].Name == list[i].Weapon.Name()) continue;
                list[i].Model!.Value = looks[i].Model;
                list[i].Icon!.Value = looks[i].Icon;
                ctx.Spoiler.Line($"{ctx.Items.Display(list[i].Weapon.Name())} looks like {ctx.Items.Display(looks[i].Name)}");
                count++;
            }
        }
        ctx.Log($"Weapon looks: {count} of {slots.Count} weapons changed.");
    }

    /// <summary>
    /// Projectiles of player weapons (weapons.xml Weapon/Projectile, the weapon's own one; class defaults stay). A
    /// projectile brings its speed, range and arc (e.g. the banjo sniper's slow arcing shot), damage stays with the weapon.
    /// Thrown and "predicted" projectiles (grenade throws, aim previews) stay. Balanced: inside the weapon class.
    /// </summary>
    public static void ApplyProjectiles(RandomizerContext ctx)
    {
        var rng = ctx.RngFor("projectiles");
        bool balanced = ctx.Options.ProjectileMode == Mode.Balanced;
        var slots = PlayerWeapons(ctx.Content)
            .Select(x => (x.Weapon, x.Job, Projectile: x.Weapon.Element("Projectile")))
            .Where(x => x.Projectile != null && x.Projectile.Value.Trim() is { Length: > 0 } p && !p.Contains("predicted") && !p.Contains("throw"))
            .ToList();

        ctx.Spoiler.Section($"Projectiles ({ctx.Options.ProjectileMode})");
        int count = 0;
        foreach (var group in slots.GroupBy(x => balanced ? x.Job : ""))
        {
            var list = group.ToList();
            var values = list.Select(x => x.Projectile!.Value.Trim()).ToList();
            rng.Shuffle(values);
            for (int i = 0; i < list.Count; i++)
            {
                var was = list[i].Projectile!.Value.Trim();
                if (was == values[i]) continue;
                list[i].Projectile!.Value = values[i];
                ctx.Spoiler.Line($"{ctx.Items.Display(list[i].Weapon.Name())}: {values[i]}    (was {was})");
                count++;
            }
        }
        ctx.Log($"Projectiles: {count} of {slots.Count} weapons changed.");
    }
}
