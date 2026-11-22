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
        if (options.ShuffleWhirlpools) MapShuffle.ApplyWhirlpools(ctx);
        if (options.ShuffleMapChests) MapShuffle.ApplyChests(ctx);
        if (options.ShuffleMapPatrols) MapShuffle.ApplyPatrols(ctx);
        if (options.ShuffleMapHazards) MapShuffle.ApplyHazards(ctx);
        // Enemy guns first, so the enemy shuffle's stats scaling sees the new guns.
        if (options.ShuffleEnemyWeapons) BattleShuffle.ApplyEnemyWeapons(ctx);
        if (options.ShuffleEnemies) EnemyShuffle.Apply(ctx);
        if (options.ShuffleEnemyAi) BattleShuffle.ApplyEnemyAi(ctx);
        if (options.ExplosiveCover) BattleShuffle.ApplyExplosiveCover(ctx);
        if (options.ShuffleEnemyHats) LookShuffle.ApplyEnemyHats(ctx);
        if (options.RandomizeWeaponStats) StatRandomizer.ApplyWeaponStats(ctx);
        if (options.RandomizePrices) StatRandomizer.ApplyPrices(ctx);
        if (options.ShufflePerks) PerkShuffle.Apply(ctx);
        if (options.RandomizeUtilities) AbilityStats.ApplyUtilities(ctx);
        if (options.RandomizeAbilities) AbilityStats.ApplyAbilities(ctx);
        if (options.RandomizeCrewStats) StatRandomizer.ApplyCrewStats(ctx);
        if (options.ShuffleProjectiles) LookShuffle.ApplyProjectiles(ctx);
        if (options.ShuffleCrewJobs) CrewShuffle.ApplyJobs(ctx);
        if (options.ShufflePersonal) CrewShuffle.ApplyPersonalAbilities(ctx);
        if (options.RandomStart) CrewShuffle.ApplyStart(ctx);
        if (options.ShuffleSubWeapons) SubShuffle.ApplyWeapons(ctx);
        if (options.ShuffleSubCosts) SubShuffle.ApplyUpgradeCosts(ctx);
        if (options.RandomizeLootDecks) SubShuffle.ApplyLootDecks(ctx);
        // After the missions moved (regions follow the markers) and the items are known (golden prizes).
        if (options.MissionHooks) MissionChaos.Apply(ctx, mod);
        if (options.ShuffleDialogue) TextShuffle.ApplyDialogue(ctx);
        if (options.ShuffleBarks) TextShuffle.ApplyBarks(ctx);
        if (options.ShuffleNpcLooks) LookShuffle.ApplyNpcs(ctx);
        if (options.ShuffleCrewHats) LookShuffle.ApplyCrewHats(ctx);
        if (options.ShuffleWeaponLooks) LookShuffle.ApplyWeaponLooks(ctx);
        if (options.ShuffleMusic) FunShuffle.ApplyMusic(ctx);
        if (options.ShuffleEnvironments) FunShuffle.ApplyEnvironments(ctx);
        if (options.ShuffleSounds) SfxShuffle.ApplySounds(ctx);
        if (options.ShuffleEffects) SfxShuffle.ApplyEffects(ctx);
        if (options.ShuffleVoices) SfxShuffle.ApplyVoices(ctx);
        // Last: it rewrites the language files the other modules read names from.
        if (options.ShuffleNames) FunShuffle.ApplyNames(ctx);
        if (options.MysteryItems) FunShuffle.ApplyMystery(ctx);
    }
}
