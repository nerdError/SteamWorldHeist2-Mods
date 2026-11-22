using System.Xml.Linq;

namespace SWH2.Mods.Randomizer;

/// <summary>A value somewhere in the game data that a shuffle may change (element text or attribute).</summary>
public sealed class Slot(string label, string group, Func<string> get, Action<string> set)
{
    public string Label { get; } = label;
    public string Group { get; } = group;
    public string Value { get => get(); set => set(value); }

    public static Slot Of(string label, string group, XElement e) =>
        new(label, group, () => e.Value.Trim(), v => e.Value = v);

    public static Slot Of(string label, string group, XAttribute a) =>
        new(label, group, () => a.Value, v => a.Value = v);
}

public static class Slots
{
    /// <summary>
    /// Shuffles the values of the slots inside each group (a permutation, so every value is still used as often as
    /// before) and writes "label: new (was old)" for every change to the spoiler log. Returns how many changed.
    /// </summary>
    public static int Shuffle(RandomizerContext ctx, Random rng, IEnumerable<Slot> slots, Func<string, string>? show = null)
    {
        show ??= v => v;
        int count = 0;
        foreach (var group in slots.GroupBy(s => s.Group).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var list = group.ToList();
            var values = list.Select(s => s.Value).ToList();
            rng.Shuffle(values);
            for (int i = 0; i < list.Count; i++)
            {
                var was = list[i].Value;
                if (was == values[i]) continue;
                list[i].Value = values[i];
                ctx.Spoiler.Line($"{list[i].Label}: {show(values[i])}    (was {show(was)})");
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Like <see cref="Shuffle"/>, but a value only moves to a slot at least as far into the story as the slot it came
    /// from (depth = when the story reaches it): late-game things never turn up early, early ones may turn up later.
    /// Slots are filled from the earliest on, each picking a random value still free that fits; its own value always
    /// fits, so this always succeeds.
    /// </summary>
    public static int ShuffleNoLater(RandomizerContext ctx, Random rng, IEnumerable<(Slot Slot, int Depth)> slots, Func<string, string>? show = null)
    {
        show ??= v => v;
        int count = 0;
        foreach (var group in slots.GroupBy(s => s.Slot.Group).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var list = group.ToList();
            var pool = list.Select(s => (Value: s.Slot.Value, s.Depth)).ToList();
            rng.Shuffle(list);
            foreach (var (slot, depth) in list.OrderBy(s => s.Depth).ToList())
            {
                var fits = Enumerable.Range(0, pool.Count).Where(i => pool[i].Depth <= depth).ToList();
                int pick = fits[rng.Next(fits.Count)];
                var value = pool[pick].Value;
                pool.RemoveAt(pick);
                var was = slot.Value;
                if (was == value) continue;
                slot.Value = value;
                ctx.Spoiler.Line($"{slot.Label}: {show(value)}    (was {show(was)})");
                count++;
            }
        }
        return count;
    }
}
