using SWH2.Modding;

namespace SWH2.Mods.Randomizer;

public enum ItemShuffleMode
{
    /// <summary>Items only swap with items of a similar tier (and money with similar amounts), so progression feels like vanilla.</summary>
    Balanced,
    /// <summary>Anything can end up anywhere.</summary>
    Chaos,
}

public enum EnemyStats
{
    /// <summary>The new enemy gets the health, damage and spawn cost of the enemy it replaces.</summary>
    Balanced,
    /// <summary>The new enemy keeps its own stats (within <see cref="EnemyLimit"/>).</summary>
    Chaos,
}

/// <summary>How much stronger than the replaced enemy a chaos enemy may be.</summary>
public sealed record EnemyLimit(string Id, double Health, double Damage)
{
    public static readonly EnemyLimit Fair = new("fair", 2, 1.5);
    public static readonly EnemyLimit Loose = new("loose", 3, 2);
    public static readonly EnemyLimit Off = new("off", double.PositiveInfinity, double.PositiveInfinity);
    public bool IsOff => double.IsPositiveInfinity(Health);
    public static EnemyLimit From(string id) => id switch { "loose" => Loose, "off" => Off, _ => Fair };
}

/// <summary>The randomizer's settings (ids as in mod.json). The preset only fills these in (done by the loader).</summary>
public sealed class RandomizerOptions
{
    public string Seed { get; set; } = "";
    public string Preset { get; set; } = "balanced";

    // Fixed item rewards: mission master loot, region bounties, world map artifacts, quest rewards.
    public bool ShuffleItems { get; set; } = true;
    public ItemShuffleMode ItemMode { get; set; } = ItemShuffleMode.Balanced;
    public bool IncludeResources { get; set; } = true;

    // Enemies listed in spawn tables (initial spawns and reinforcements).
    public bool ShuffleEnemies { get; set; } = true;
    public EnemyStats EnemyStats { get; set; } = EnemyStats.Balanced;
    /// <summary>True: a replacement fights for the faction of the enemy it replaces. False: its own (factions fight each other).</summary>
    public bool EnemiesKeepSlotFaction { get; set; } = true;
    public EnemyLimit EnemyLimit { get; set; } = EnemyLimit.Fair;

    public bool RandomizeWeaponDamage { get; set; }
    public int WeaponDamageVariance { get; set; } = 30;

    public bool RandomizePrices { get; set; }
    public int PriceVariance { get; set; } = 30;

    public static RandomizerOptions From(ModSettings s) => new()
    {
        Seed = s.String("seed"),
        Preset = s.String("preset"),
        ShuffleItems = s.Bool("items"),
        ItemMode = s.String("itemMode") == "chaos" ? ItemShuffleMode.Chaos : ItemShuffleMode.Balanced,
        IncludeResources = s.Bool("resources"),
        ShuffleEnemies = s.Bool("enemies"),
        EnemyStats = s.String("enemyMode") == "chaos" ? EnemyStats.Chaos : EnemyStats.Balanced,
        EnemiesKeepSlotFaction = s.String("enemyFaction") != "own",
        EnemyLimit = EnemyLimit.From(s.String("enemyLimit")),
        RandomizeWeaponDamage = s.Bool("damage"),
        WeaponDamageVariance = s.Int("damagePct"),
        RandomizePrices = s.Bool("prices"),
        PriceVariance = s.Int("pricesPct"),
    };

    public string Summary()
    {
        var parts = new List<string> { Preset };
        if (ShuffleItems) parts.Add($"items:{ItemMode.ToString().ToLowerInvariant()}{(IncludeResources ? "+res" : "")}");
        if (ShuffleEnemies)
            parts.Add($"enemies:{EnemyStats.ToString().ToLowerInvariant()}" +
                      $"{(EnemyStats == EnemyStats.Chaos ? "/" + EnemyLimit.Id : "")}+{(EnemiesKeepSlotFaction ? "slot" : "own")}-faction");
        if (RandomizeWeaponDamage) parts.Add($"damage±{WeaponDamageVariance}%");
        if (RandomizePrices) parts.Add($"prices±{PriceVariance}%");
        return $"seed {Seed} ({string.Join(", ", parts)})";
    }
}
