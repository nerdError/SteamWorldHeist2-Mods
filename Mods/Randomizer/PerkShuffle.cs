using SWH2.Core;

namespace SWH2.Mods.Randomizer;

/// <summary>
/// Class perks: the level-up upgrades of the six jobs (Definitions/jobs.xml, Job/Upgrades/Upgrade) swap between jobs.
/// Level 0 (the starting ability that defines a class, e.g. power_shot, which the tutorial uses) stays.
/// Every perk still exists exactly once, so no class gets the same perk twice.
///  - Balanced: only perks of the same level swap, abilities with abilities and stat bonuses (plus_*/minus_*) with stat bonuses.
///  - Chaos: any perk from level 1 up can land on any job and level.
/// </summary>
public static class PerkShuffle
{
    const string JobsFile = "Definitions/jobs.xml";

    public static void Apply(RandomizerContext ctx)
    {
        var rng = ctx.RngFor("perks");
        bool balanced = ctx.Options.PerkMode == Mode.Balanced;
        var slots = ctx.Content.Xml(JobsFile).Definitions("Job")
            .Where(j => !j.IsAbstract())
            .SelectMany(j => (j.Element("Upgrades")?.Elements("Upgrade") ?? []).Select(u => (Job: j.Name(), Upgrade: u, Level: u.Attribute("Level")?.Value ?? "")))
            .Where(x => x.Level != "0" && x.Upgrade.Value.Trim().Length > 0)
            .ToList();
        var before = slots.Select(x => x.Upgrade.Value.Trim()).ToList();

        foreach (var group in slots.GroupBy(x => balanced ? x.Level + (IsStat(x.Upgrade.Value) ? " stat" : " ability") : ""))
        {
            var list = group.ToList();
            var perks = list.Select(x => x.Upgrade.Value.Trim()).ToList();
            rng.Shuffle(perks);
            for (int i = 0; i < list.Count; i++) list[i].Upgrade.Value = perks[i];
        }

        ctx.Spoiler.Section($"Class perks ({ctx.Options.PerkMode})");
        int moved = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            var now = slots[i].Upgrade.Value.Trim();
            if (now != before[i]) moved++;
            ctx.Spoiler.Line($"{slots[i].Job} level {slots[i].Level}: {Display(ctx, now)}    (was {Display(ctx, before[i])})");
        }
        ctx.Log($"Perks: {moved} of {slots.Count} moved.");
    }

    static bool IsStat(string perk) => perk.Trim().StartsWith("plus_", StringComparison.Ordinal) || perk.Trim().StartsWith("minus_", StringComparison.Ordinal);

    static string Display(RandomizerContext ctx, string perk) =>
        ctx.Loc.Get("jobupgrade_" + perk) is { } name ? $"{name} [{perk}]" : perk;
}
