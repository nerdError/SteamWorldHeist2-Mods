using SWH2.Core;
using SWH2.Modding;

namespace SWH2.Mods.Randomizer;

/// <summary>What the randomizer modules work with: the loader's <see cref="ModContext"/> plus the parsed options.</summary>
public sealed class RandomizerContext(ModContext mod, RandomizerOptions options)
{
    public GameContent Content => mod.Content;
    public RandomizerOptions Options { get; } = options;
    public Action<string> Log => mod.Log;
    public Localization Loc => mod.Loc;
    public ItemCatalog Items => mod.Items;
    /// <summary>The spoiler log (saved as the mod's report).</summary>
    public ReportLog Spoiler => mod.Report;

    /// <summary>
    /// Each module gets its own generator derived from the seed, so turning one option on or off
    /// doesn't change what the other modules produce for the same seed.
    /// </summary>
    public Random RngFor(string module) => new(ModContext.StableHash(Options.Seed + "/" + module));
}

public static class RandomExtensions
{
    public static void Shuffle<T>(this Random rng, IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    public static T Pick<T>(this Random rng, IReadOnlyList<T> list) => list[rng.Next(list.Count)];
}
