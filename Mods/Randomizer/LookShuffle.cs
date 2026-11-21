using System.Text.RegularExpressions;
using System.Xml.Linq;
using SWH2.Core;

namespace SWH2.Mods.Randomizer;

/// <summary>Cosmetic shuffles: enemy hats and NPC models.</summary>
public static class LookShuffle
{
    const string NpcFile = "Definitions/entities.npc.xml";

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
}
