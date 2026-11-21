using SWH2.Modding;

namespace SWH2.Mods.Randomizer;

/// <summary>Randomizer mod: deterministic (same seed + settings = same game) shuffles of rewards, enemies and stats.</summary>
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

        if (options.ShuffleItems) ItemShuffle.Apply(ctx);
        if (options.ShuffleEnemies) EnemyShuffle.Apply(ctx);
        if (options.RandomizeWeaponDamage) StatRandomizer.ApplyWeaponDamage(ctx);
        if (options.RandomizePrices) StatRandomizer.ApplyPrices(ctx);
    }
}
