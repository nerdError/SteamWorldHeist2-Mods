using SWH2.Modding;

namespace SWH2.Mods.Randomizer;

/// <summary>Balanced keeps the game's progression and economy close to vanilla; Chaos lets anything go anywhere.</summary>
public enum Mode
{
    Balanced,
    Chaos,
}

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

    // Which missions the world map markers start.
    public bool ShuffleMissions { get; set; }
    public Mode MissionMode { get; set; } = Mode.Balanced;

    // Fixed item rewards: mission master loot, region bounties, world map artifacts, quest rewards.
    public bool ShuffleItems { get; set; } = true;
    public ItemShuffleMode ItemMode { get; set; } = ItemShuffleMode.Balanced;
    public bool IncludeResources { get; set; } = true;
    /// <summary>Story key items move too, but only to places the game reaches earlier than their own (see <see cref="ItemShuffle"/>).</summary>
    public bool ShuffleKeyItems { get; set; }

    public bool ShuffleShops { get; set; }
    public Mode ShopMode { get; set; } = Mode.Balanced;

    // Enemies listed in spawn tables (initial spawns and reinforcements).
    public bool ShuffleEnemies { get; set; } = true;
    public EnemyStats EnemyStats { get; set; } = EnemyStats.Balanced;
    /// <summary>True: a replacement fights for the faction of the enemy it replaces. False: its own (factions fight each other).</summary>
    public bool EnemiesKeepSlotFaction { get; set; } = true;
    public EnemyLimit EnemyLimit { get; set; } = EnemyLimit.Fair;
    public bool ShuffleEnemyHats { get; set; }

    public bool RandomizeWeaponStats { get; set; }
    public Mode WeaponMode { get; set; } = Mode.Balanced;
    public int WeaponVariance { get; set; } = 30;

    public bool RandomizePrices { get; set; }
    public int PriceVariance { get; set; } = 30;

    public bool ShufflePerks { get; set; }
    public Mode PerkMode { get; set; } = Mode.Balanced;

    public bool RandomizeUtilities { get; set; }
    public Mode UtilityMode { get; set; } = Mode.Balanced;
    /// <summary>Variance for utility numbers (chaos abilities use it too).</summary>
    public int UtilityVariance { get; set; } = 30;
    public bool RandomizeAbilities { get; set; }
    public Mode AbilityMode { get; set; } = Mode.Balanced;

    public bool RandomizeCrewStats { get; set; }
    public Mode CrewStatsMode { get; set; } = Mode.Balanced;

    /// <summary>What player weapons shoot (also changes range and arcs). Balanced: inside the weapon class.</summary>
    public bool ShuffleProjectiles { get; set; }
    public Mode ProjectileMode { get; set; } = Mode.Balanced;

    // Cosmetic.
    public bool ShuffleDialogue { get; set; }
    /// <summary>True: lines only move inside their own conversation. False: across the whole game.</summary>
    public bool DialogueWithinConversation { get; set; } = true;
    public bool ShuffleBarks { get; set; }
    public bool ShuffleNpcLooks { get; set; }
    public bool ShuffleCrewHats { get; set; }
    public bool ShuffleWeaponLooks { get; set; }
    public Mode WeaponLookMode { get; set; } = Mode.Balanced;
    public bool ShuffleMusic { get; set; }
    public bool ShuffleEnvironments { get; set; }
    public Mode EnvironmentMode { get; set; } = Mode.Balanced;
    public bool ShuffleNames { get; set; }
    public Mode NameMode { get; set; } = Mode.Balanced;
    public bool ShuffleSounds { get; set; }
    public Mode SoundMode { get; set; } = Mode.Balanced;
    public bool ShuffleEffects { get; set; }
    public Mode EffectMode { get; set; } = Mode.Balanced;
    public bool ShuffleVoices { get; set; }
    public Mode VoiceMode { get; set; } = Mode.Balanced;
    public bool MysteryItems { get; set; }

    // Battles.
    public bool ShuffleEnemyWeapons { get; set; }
    public Mode EnemyWeaponMode { get; set; } = Mode.Balanced;
    public bool ShuffleEnemyAi { get; set; }
    public Mode EnemyAiMode { get; set; } = Mode.Balanced;
    public bool ExplosiveCover { get; set; }
    public Mode CoverMode { get; set; } = Mode.Balanced;
    public bool MutatorRoulette { get; set; }
    public Mode MutatorMode { get; set; } = Mode.Balanced;
    public bool ChaosEvents { get; set; }
    public int ChaosEventChance { get; set; } = 25;
    public bool GoldenEnemy { get; set; }
    public Mode GoldenMode { get; set; } = Mode.Balanced;
    public bool RegionEdicts { get; set; }
    public Mode EdictMode { get; set; } = Mode.Balanced;
    /// <summary>Any option that needs the Lua mission hooks (<see cref="MissionChaos"/>).</summary>
    public bool MissionHooks => MutatorRoulette || ChaosEvents || GoldenEnemy || RegionEdicts;

    // Crew, start and sub.
    public bool ShuffleCrewJobs { get; set; }
    public Mode CrewJobMode { get; set; } = Mode.Balanced;
    public bool ShufflePersonal { get; set; }
    public Mode PersonalMode { get; set; } = Mode.Balanced;
    public bool RandomStart { get; set; }
    public Mode StartMode { get; set; } = Mode.Balanced;
    public bool ShuffleSubWeapons { get; set; }
    public Mode SubMode { get; set; } = Mode.Balanced;
    public bool ShuffleSubCosts { get; set; }
    public bool RandomizeLootDecks { get; set; }
    public Mode LootDeckMode { get; set; } = Mode.Balanced;

    // World map.
    public bool ShuffleWhirlpools { get; set; }
    public Mode WhirlpoolMode { get; set; } = Mode.Balanced;
    public bool ShuffleMapChests { get; set; }
    public Mode ChestMode { get; set; } = Mode.Balanced;
    public bool ShuffleMapPatrols { get; set; }
    public Mode PatrolMode { get; set; } = Mode.Balanced;
    public bool ShuffleMapHazards { get; set; }
    public Mode HazardMode { get; set; } = Mode.Balanced;

    static Mode ModeOf(ModSettings s, string id) => s.String(id) == "chaos" ? Mode.Chaos : Mode.Balanced;

    public static RandomizerOptions From(ModSettings s) => new()
    {
        Seed = s.String("seed"),
        Preset = s.String("preset"),
        ShuffleMissions = s.Bool("missions"),
        MissionMode = ModeOf(s, "missionMode"),
        ShuffleItems = s.Bool("items"),
        ItemMode = s.String("itemMode") == "chaos" ? ItemShuffleMode.Chaos : ItemShuffleMode.Balanced,
        IncludeResources = s.Bool("resources"),
        ShuffleKeyItems = s.Bool("keyItems"),
        ShuffleShops = s.Bool("shops"),
        ShopMode = ModeOf(s, "shopMode"),
        ShuffleEnemies = s.Bool("enemies"),
        EnemyStats = s.String("enemyMode") == "chaos" ? EnemyStats.Chaos : EnemyStats.Balanced,
        EnemiesKeepSlotFaction = s.String("enemyFaction") != "own",
        EnemyLimit = EnemyLimit.From(s.String("enemyLimit")),
        ShuffleEnemyHats = s.Bool("hats"),
        RandomizeWeaponStats = s.Bool("damage"),
        WeaponMode = ModeOf(s, "damageMode"),
        WeaponVariance = s.Int("damagePct"),
        RandomizePrices = s.Bool("prices"),
        PriceVariance = s.Int("pricesPct"),
        ShufflePerks = s.Bool("perks"),
        PerkMode = ModeOf(s, "perkMode"),
        ShuffleDialogue = s.Bool("dialogue"),
        DialogueWithinConversation = s.String("dialogueMode") != "all",
        ShuffleBarks = s.Bool("barks"),
        ShuffleNpcLooks = s.Bool("npcs"),
        RandomizeUtilities = s.Bool("utilities"),
        UtilityMode = ModeOf(s, "utilityMode"),
        UtilityVariance = s.Int("utilityPct"),
        RandomizeAbilities = s.Bool("abilities"),
        AbilityMode = ModeOf(s, "abilityMode"),
        RandomizeCrewStats = s.Bool("crewStats"),
        CrewStatsMode = ModeOf(s, "crewStatsMode"),
        ShuffleProjectiles = s.Bool("projectiles"),
        ProjectileMode = ModeOf(s, "projectileMode"),
        ShuffleCrewHats = s.Bool("crewHats"),
        ShuffleWeaponLooks = s.Bool("weaponLooks"),
        WeaponLookMode = ModeOf(s, "weaponLookMode"),
        ShuffleMusic = s.Bool("music"),
        ShuffleEnvironments = s.Bool("environments"),
        EnvironmentMode = ModeOf(s, "environmentMode"),
        ShuffleNames = s.Bool("names"),
        NameMode = ModeOf(s, "nameMode"),
        ShuffleSounds = s.Bool("sounds"),
        SoundMode = ModeOf(s, "soundMode"),
        ShuffleEffects = s.Bool("effects"),
        EffectMode = ModeOf(s, "effectMode"),
        ShuffleVoices = s.Bool("voices"),
        VoiceMode = ModeOf(s, "voiceMode"),
        MysteryItems = s.Bool("mystery"),
        ShuffleEnemyWeapons = s.Bool("enemyWeapons"),
        EnemyWeaponMode = ModeOf(s, "enemyWeaponMode"),
        ShuffleEnemyAi = s.Bool("enemyAi"),
        EnemyAiMode = ModeOf(s, "enemyAiMode"),
        ExplosiveCover = s.Bool("explosiveCover"),
        CoverMode = ModeOf(s, "coverMode"),
        MutatorRoulette = s.Bool("mutators"),
        MutatorMode = ModeOf(s, "mutatorMode"),
        ChaosEvents = s.Bool("chaosEvents"),
        ChaosEventChance = s.Int("chaosEventPct"),
        GoldenEnemy = s.Bool("golden"),
        GoldenMode = ModeOf(s, "goldenMode"),
        RegionEdicts = s.Bool("regionEdicts"),
        EdictMode = ModeOf(s, "edictMode"),
        ShuffleCrewJobs = s.Bool("crewJobs"),
        CrewJobMode = ModeOf(s, "crewJobMode"),
        ShufflePersonal = s.Bool("personal"),
        PersonalMode = ModeOf(s, "personalMode"),
        RandomStart = s.Bool("start"),
        StartMode = ModeOf(s, "startMode"),
        ShuffleSubWeapons = s.Bool("subWeapons"),
        SubMode = ModeOf(s, "subMode"),
        ShuffleSubCosts = s.Bool("subCosts"),
        RandomizeLootDecks = s.Bool("lootDecks"),
        LootDeckMode = ModeOf(s, "lootDeckMode"),
        ShuffleWhirlpools = s.Bool("whirlpools"),
        WhirlpoolMode = ModeOf(s, "whirlpoolMode"),
        ShuffleMapChests = s.Bool("mapChests"),
        ChestMode = ModeOf(s, "chestMode"),
        ShuffleMapPatrols = s.Bool("mapPatrols"),
        PatrolMode = ModeOf(s, "patrolMode"),
        ShuffleMapHazards = s.Bool("mapHazards"),
        HazardMode = ModeOf(s, "hazardMode"),
    };

    static string Lower(Mode m) => m.ToString().ToLowerInvariant();

    public string Summary()
    {
        var parts = new List<string> { Preset };
        if (ShuffleMissions) parts.Add($"missions:{Lower(MissionMode)}");
        if (ShuffleItems) parts.Add($"items:{ItemMode.ToString().ToLowerInvariant()}{(IncludeResources ? "+res" : "")}{(ShuffleKeyItems ? "+key" : "")}");
        if (ShuffleShops) parts.Add($"shops:{Lower(ShopMode)}");
        if (ShuffleEnemies)
            parts.Add($"enemies:{EnemyStats.ToString().ToLowerInvariant()}" +
                      $"{(EnemyStats == EnemyStats.Chaos ? "/" + EnemyLimit.Id : "")}+{(EnemiesKeepSlotFaction ? "slot" : "own")}-faction");
        if (ShuffleEnemyHats) parts.Add("hats");
        if (RandomizeWeaponStats) parts.Add($"weapons:{Lower(WeaponMode)}±{WeaponVariance}%");
        if (RandomizePrices) parts.Add($"prices±{PriceVariance}%");
        if (ShufflePerks) parts.Add($"perks:{Lower(PerkMode)}");
        if (RandomizeUtilities) parts.Add($"utilities:{Lower(UtilityMode)}±{UtilityVariance}%");
        if (RandomizeAbilities) parts.Add($"abilities:{Lower(AbilityMode)}");
        if (RandomizeCrewStats) parts.Add($"crew:{Lower(CrewStatsMode)}");
        if (ShuffleProjectiles) parts.Add($"projectiles:{Lower(ProjectileMode)}");
        if (ShuffleDialogue) parts.Add($"dialogue:{(DialogueWithinConversation ? "conversation" : "all")}");
        if (ShuffleBarks) parts.Add("barks");
        if (ShuffleNpcLooks) parts.Add("npcs");
        if (ShuffleCrewHats) parts.Add("crewhats");
        if (ShuffleWeaponLooks) parts.Add($"weaponlooks:{Lower(WeaponLookMode)}");
        if (ShuffleMusic) parts.Add("music");
        if (ShuffleEnvironments) parts.Add($"environments:{Lower(EnvironmentMode)}");
        if (ShuffleNames) parts.Add($"names:{Lower(NameMode)}");
        if (ShuffleSounds) parts.Add($"sounds:{Lower(SoundMode)}");
        if (ShuffleEffects) parts.Add($"effects:{Lower(EffectMode)}");
        if (ShuffleVoices) parts.Add($"voices:{Lower(VoiceMode)}");
        if (MysteryItems) parts.Add("mystery");
        if (ShuffleEnemyWeapons) parts.Add($"enemyweapons:{Lower(EnemyWeaponMode)}");
        if (ShuffleEnemyAi) parts.Add($"enemyai:{Lower(EnemyAiMode)}");
        if (ExplosiveCover) parts.Add($"cover:{Lower(CoverMode)}");
        if (MutatorRoulette) parts.Add($"mutators:{Lower(MutatorMode)}");
        if (ChaosEvents) parts.Add($"events{ChaosEventChance}%");
        if (GoldenEnemy) parts.Add($"golden:{Lower(GoldenMode)}");
        if (RegionEdicts) parts.Add($"edicts:{Lower(EdictMode)}");
        if (ShuffleCrewJobs) parts.Add($"jobs:{Lower(CrewJobMode)}");
        if (ShufflePersonal) parts.Add($"personal:{Lower(PersonalMode)}");
        if (RandomStart) parts.Add($"start:{Lower(StartMode)}");
        if (ShuffleSubWeapons) parts.Add($"sub:{Lower(SubMode)}");
        if (ShuffleSubCosts) parts.Add("subcosts");
        if (RandomizeLootDecks) parts.Add($"lootdecks:{Lower(LootDeckMode)}");
        if (ShuffleWhirlpools) parts.Add($"whirlpools:{Lower(WhirlpoolMode)}");
        if (ShuffleMapChests) parts.Add($"maploot:{Lower(ChestMode)}");
        if (ShuffleMapPatrols) parts.Add($"patrols:{Lower(PatrolMode)}");
        if (ShuffleMapHazards) parts.Add($"hazards:{Lower(HazardMode)}");
        return $"seed {Seed} ({string.Join(", ", parts)})";
    }
}
