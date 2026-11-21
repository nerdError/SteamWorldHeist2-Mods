using SWH2.Modding;

namespace SWH2.Mods.Randomizer;

/// <summary>Randomizer mod: deterministic (same seed + settings = same game) shuffles of missions, rewards, shops, enemies, stats and text.</summary>
public sealed class RandomizerMod : IModPlugin
{
    public void Apply(ModContext mod)
    {
        var options = RandomizerOptions.From(mod.Settings);
        var ctx = new RandomizerContext(mod, options);
        mod.ReportName = $"spoiler_{options.Seed}";
        mod.Summary = options.Summary();
        ctx.Spoiler.Line("SteamWorld Heist 2 Randomizer — spoiler log");
        ctx.Spoiler.Line(options.Summary());
        ctx.Log($"Randomizing with {options.Summary()}");

        // Missions first: the item logic needs to know which missions moved.
        if (options.ShuffleMissions) MissionShuffle.Apply(ctx);
        if (options.ShuffleItems) ItemShuffle.Apply(ctx);
        if (options.ShuffleShops) ShopShuffle.Apply(ctx);
        if (options.ShuffleEnemies) EnemyShuffle.Apply(ctx);
        if (options.ShuffleEnemyHats) LookShuffle.ApplyEnemyHats(ctx);
        if (options.RandomizeWeaponStats) StatRandomizer.ApplyWeaponStats(ctx);
        if (options.RandomizePrices) StatRandomizer.ApplyPrices(ctx);
        if (options.ShufflePerks) PerkShuffle.Apply(ctx);
        if (options.ShuffleDialogue) TextShuffle.ApplyDialogue(ctx);
        if (options.ShuffleBarks) TextShuffle.ApplyBarks(ctx);
        if (options.ShuffleNpcLooks) LookShuffle.ApplyNpcs(ctx);
    }
}
