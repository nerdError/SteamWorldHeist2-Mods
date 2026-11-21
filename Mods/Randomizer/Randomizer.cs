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
        _ = ctx.Items; // load names before the names module swaps them, so the spoiler log shows the real ones

        // Missions first: the item logic needs to know which missions moved.
        if (options.ShuffleMissions) MissionShuffle.Apply(ctx);
        if (options.ShuffleItems) ItemShuffle.Apply(ctx);
        if (options.ShuffleShops) ShopShuffle.Apply(ctx);
        if (options.ShuffleEnemies) EnemyShuffle.Apply(ctx);
        if (options.ShuffleEnemyHats) LookShuffle.ApplyEnemyHats(ctx);
        if (options.RandomizeWeaponStats) StatRandomizer.ApplyWeaponStats(ctx);
        if (options.RandomizePrices) StatRandomizer.ApplyPrices(ctx);
        if (options.ShufflePerks) PerkShuffle.Apply(ctx);
        if (options.RandomizeUtilities) AbilityStats.ApplyUtilities(ctx);
        if (options.RandomizeAbilities) AbilityStats.ApplyAbilities(ctx);
        if (options.RandomizeCrewStats) StatRandomizer.ApplyCrewStats(ctx);
        if (options.ShuffleProjectiles) LookShuffle.ApplyProjectiles(ctx);
        if (options.ShuffleDialogue) TextShuffle.ApplyDialogue(ctx);
        if (options.ShuffleBarks) TextShuffle.ApplyBarks(ctx);
        if (options.ShuffleNpcLooks) LookShuffle.ApplyNpcs(ctx);
        if (options.ShuffleCrewHats) LookShuffle.ApplyCrewHats(ctx);
        if (options.ShuffleWeaponLooks) LookShuffle.ApplyWeaponLooks(ctx);
        if (options.ShuffleMusic) FunShuffle.ApplyMusic(ctx);
        if (options.ShuffleEnvironments) FunShuffle.ApplyEnvironments(ctx);
        // Last: it rewrites the language files the other modules read names from.
        if (options.ShuffleNames) FunShuffle.ApplyNames(ctx);
    }
}
