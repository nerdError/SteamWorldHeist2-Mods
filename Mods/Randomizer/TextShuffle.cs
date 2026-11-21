using System.Xml.Linq;
using SWH2.Core;

namespace SWH2.Mods.Randomizer;

/// <summary>
/// Shuffles which text is shown, by swapping string ids (the texts themselves are in Language/*.csv, so this works for
/// every language at once):
///  - dialogue: Speak@Text in conversations.xml (bars, cutscenes, mission talk). Answer choices (Ask/Item) and the
///    tutorial conversations stay, so the game can still be followed and played.
///  - barks: String in barks.xml (what crew and enemies shout in battle).
/// </summary>
public static class TextShuffle
{
    const string ConversationsFile = "Definitions/conversations.xml";
    const string BarksFile = "Definitions/barks.xml";

    public static void ApplyDialogue(RandomizerContext ctx)
    {
        var rng = ctx.RngFor("dialogue");
        var lines = ctx.Content.Xml(ConversationsFile).Definitions("Conversation")
            .Where(conv => !conv.Name().Contains("tutorial", StringComparison.OrdinalIgnoreCase))
            .SelectMany(conv => conv.Descendants("Speak").Select(s => (Conversation: conv.Name(), Text: s.Attribute("Text"))))
            .Where(x => x.Text != null && x.Text.Value.Length > 0)
            .ToList();

        var groups = ctx.Options.DialogueWithinConversation ? lines.GroupBy(x => x.Conversation) : lines.GroupBy(_ => "");
        int count = 0;
        foreach (var group in groups)
            count += Shuffle(rng, group.Select(x => x.Text!).ToList(), a => a.Value, (a, v) => a.Value = v);

        ctx.Spoiler.Section("Dialogue");
        ctx.Spoiler.Line($"{count} of {lines.Count} lines moved ({(ctx.Options.DialogueWithinConversation ? "inside each conversation" : "across the whole game")}).");
        ctx.Log($"Dialogue: {count} lines moved.");
    }

    public static void ApplyBarks(RandomizerContext ctx)
    {
        var rng = ctx.RngFor("barks");
        var strings = ctx.Content.Xml(BarksFile).Root.Descendants("String").Where(s => s.Value.Trim().Length > 0).ToList();
        int count = Shuffle(rng, strings, s => s.Value, (s, v) => s.Value = v);
        ctx.Spoiler.Section("Barks");
        ctx.Spoiler.Line($"{count} of {strings.Count} barks moved.");
        ctx.Log($"Barks: {count} moved.");
    }

    /// <summary>Permutes the values in place; returns how many changed.</summary>
    static int Shuffle<T>(Random rng, List<T> slots, Func<T, string> get, Action<T, string> set)
    {
        var values = slots.Select(get).ToList();
        rng.Shuffle(values);
        int changed = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            if (get(slots[i]) == values[i]) continue;
            set(slots[i], values[i]);
            changed++;
        }
        return changed;
    }
}
