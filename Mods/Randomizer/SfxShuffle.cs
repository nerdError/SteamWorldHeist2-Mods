using System.Xml.Linq;
using SWH2.Core;

namespace SWH2.Mods.Randomizer;

/// <summary>How battles sound and look, and how people talk: weapon sounds, visual effects, speaker voices and portraits.</summary>
public static class SfxShuffle
{
    const string ProjectilesFile = "Definitions/entities.projectiles.xml";
    const string SpeakersFile = "Definitions/speakers.xml";
    const string PlayerWeaponsFile = "Definitions/weapons.xml";

    /// <summary>
    /// Weapon sounds (weapons*.xml ShootSound, AimSound, CancelAimSound, ReloadSound) and the hurt sounds of bots
    /// (entities.enemies*.xml / entities.crew.xml HurtSound). Only a definition's own values move, every sound is
    /// still used as often as before. Balanced: player weapons inside their class and enemy weapons among themselves,
    /// crew among crew, enemies among enemies. Chaos: everything of a kind together (a sniper may go "pew").
    /// </summary>
    public static void ApplySounds(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var rng = ctx.RngFor("sounds");
        bool balanced = ctx.Options.SoundMode == Mode.Balanced;
        var jobs = JobsOfPlayerWeapons(c);

        var slots = new List<Slot>();
        foreach (var file in c.Find("Definitions", "weapons*.xml"))
            foreach (var w in c.Xml(file).Definitions("Weapon"))
                foreach (var tag in new[] { "ShootSound", "AimSound", "CancelAimSound", "ReloadSound" })
                    if (w.Element(tag) is { } e && e.Value.Trim().Length > 0 && !e.HasElements)
                    {
                        var side = file == PlayerWeaponsFile ? "player " + jobs.GetValueOrDefault(w.Name(), "") : "enemy";
                        slots.Add(Slot.Of($"{w.Name()} {tag}", tag + (balanced ? " " + side : ""), e));
                    }
        foreach (var file in EnemyShuffle.EnemyFiles.Append("Definitions/entities.crew.xml"))
            foreach (var en in c.Xml(file).Definitions("Entity"))
                if (en.Element("Actor")?.Element("HurtSound") is { } e && e.Value.Trim().Length > 0)
                    slots.Add(Slot.Of($"{en.Name()} HurtSound", "HurtSound" + (balanced ? " " + (file.Contains("crew") ? "crew" : "enemy") : ""), e));

        ctx.Spoiler.Section($"Sounds ({ctx.Options.SoundMode})");
        int n = Slots.Shuffle(ctx, rng, slots);
        ctx.Log($"Sounds: {n} of {slots.Count} sounds changed.");
    }

    /// <summary>
    /// Visual effects: projectile trails, muzzle flashes and impacts (entities.projectiles.xml ProjectileEffect,
    /// MuzzleEffect, EffectOnEnd), weapon shoot effects (weapons*.xml ShootEffects/Effect@Name) and enemy death
    /// effects (DeathEffect). Damage, range and sounds stay. Aim previews ("predicted") stay. Balanced: trails with
    /// trails, muzzles with muzzles, impacts with impacts. Chaos: a bullet trail may become an explosion and back.
    /// </summary>
    public static void ApplyEffects(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var rng = ctx.RngFor("effects");
        bool balanced = ctx.Options.EffectMode == Mode.Balanced;
        string Group(string kind) => balanced ? kind : "effect";

        var slots = new List<Slot>();
        foreach (var p in c.Xml(ProjectilesFile).Definitions("Entity"))
        {
            if (p.Name().Contains("predicted") || p.Element("Projectile") is not { } proj) continue;
            foreach (var tag in new[] { "ProjectileEffect", "MuzzleEffect", "EffectOnEnd" })
                if (proj.Element(tag) is { } e && e.Value.Trim().Length > 0)
                    slots.Add(Slot.Of($"{p.Name()} {tag}", Group(tag), e));
        }
        foreach (var file in c.Find("Definitions", "weapons*.xml"))
            foreach (var w in c.Xml(file).Definitions("Weapon"))
                foreach (var e in w.Element("ShootEffects")?.Elements("Effect") ?? [])
                    if (e.Attribute("Name") is { } a && a.Value.Length > 0 && !a.Value.Contains("sound"))
                        slots.Add(Slot.Of($"{w.Name()} shoot effect", Group("MuzzleEffect"), a));
        foreach (var file in EnemyShuffle.EnemyFiles)
            foreach (var en in c.Xml(file).Definitions("Entity"))
                if (en.Element("DeathEffect") is { } e && e.Value.Trim().Length > 0)
                    slots.Add(Slot.Of($"{en.Name()} DeathEffect", Group("DeathEffect"), e));

        ctx.Spoiler.Section($"Effects ({ctx.Options.EffectMode})");
        int n = Slots.Shuffle(ctx, rng, slots);
        ctx.Log($"Effects: {n} of {slots.Count} effects changed.");
    }

    /// <summary>
    /// Speakers (speakers.xml): talking voices (Voice@Cue with its Speed) swap. Chaos also swaps the default talk
    /// portraits and the radio portraits, so the captain may talk with the bartender's face.
    /// </summary>
    public static void ApplyVoices(RandomizerContext ctx)
    {
        var rng = ctx.RngFor("voices");
        var speakers = ctx.Content.Xml(SpeakersFile).Definitions("Speaker").Where(s => !s.IsAbstract()).ToList();

        // Cue and speed move together: a slot holds "cue|speed".
        var voices = speakers
            .Where(s => s.Element("Voice") is { } v && v.Attribute("Cue")?.Value is { Length: > 0 })
            .Select(s =>
            {
                var v = s.Element("Voice")!;
                return new Slot(s.Name() + " voice", "voice",
                    () => v.Attribute("Cue")!.Value + "|" + (v.Attribute("Speed")?.Value ?? ""),
                    x =>
                    {
                        var parts = x.Split('|');
                        v.SetAttributeValue("Cue", parts[0]);
                        if (parts[1].Length > 0) v.SetAttributeValue("Speed", parts[1]);
                    });
            }).ToList();

        ctx.Spoiler.Section($"Voices{(ctx.Options.VoiceMode == Mode.Chaos ? " and portraits" : "")}");
        int n = Slots.Shuffle(ctx, rng, voices, x => x.Split('|')[0]);
        if (ctx.Options.VoiceMode == Mode.Chaos)
        {
            var faces = speakers
                .SelectMany(s => (s.Element("Portraits")?.Elements("Portrait") ?? [])
                    .Where(p => p.Attribute("Type")?.Value == "default" && p.Value.Trim().Length > 0)
                    .Select(p => Slot.Of(s.Name() + " portrait", "portrait", p))
                    .Concat(s.Element("RadioPortrait") is { } r && r.Value.Trim().Length > 0 ? [Slot.Of(s.Name() + " radio portrait", "radio", r)] : []))
                .ToList();
            n += Slots.Shuffle(ctx, rng, faces, Path.GetFileName);
        }
        ctx.Log($"Voices: {n} changed.");
    }

    /// <summary>Weapon class (Job, through Template) of every weapon in weapons.xml.</summary>
    internal static Dictionary<string, string> JobsOfPlayerWeapons(GameContent c)
    {
        var defs = c.Xml(PlayerWeaponsFile).Definitions("Weapon").ToDictionary(e => e.Name(), StringComparer.Ordinal);
        var jobs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, w) in defs)
        {
            XElement? e = w;
            for (int i = 0; i < 16 && e != null; i++)
            {
                if (e.Element("Job")?.Value.Trim() is { Length: > 0 } job) { jobs[name] = job; break; }
                e = e.Attribute("Template")?.Value is { } t && defs.TryGetValue(t, out var next) ? next : null;
            }
        }
        return jobs;
    }
}
