namespace SWH2.Core;

/// <summary>
/// Display strings from Language/&lt;lang&gt;.csv (tab-separated: id, text).
/// Used only to make spoiler logs readable.
/// </summary>
public sealed class Localization
{
    readonly Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);

    public static Localization Load(GameContent content, string language = "en")
    {
        var loc = new Localization();
        var path = $"Language/{language}.csv";
        if (!content.ExistsInOriginal(path)) return loc;
        foreach (var line in content.ReadText(path).Split('\n'))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length >= 2 && parts[0].Length > 0) loc._strings.TryAdd(parts[0], parts[1]);
        }
        return loc;
    }

    public string? Get(string id) => _strings.TryGetValue(id, out var s) && s.Length > 0 ? s : null;
}
