namespace SWH2.Core;

/// <summary>
/// Shared helpers for Lua mods (proven recipe, docs/MODDING.md "Как устроен Lua-мод"). Changes stack, so several
/// mods can each add a module, mission hooks and strings without overwriting each other's system.lua / csv.
/// </summary>
public sealed class LuaPatcher(GameContent content)
{
    const string SystemLua = "Lua/System/system.lua";
    const string Anchor = "require_global(\"System/vector\")";

    /// <summary>
    /// Adds Lua/Mod/&lt;name&gt;.lua and loads it from system.lua. It runs in the GLOBAL environment (io, os, pcall,
    /// but no engine functions); end it with <c>sandbox("Name")</c> to make its table visible to mission scripts.
    /// </summary>
    public void AddGlobalModule(string name, string source)
    {
        content.WriteText($"Lua/Mod/{name}.lua", source);
        var sys = content.ReadText(SystemLua);
        var line = $"require_global(\"Mod/{name}\")";
        if (sys.Contains(line)) return;
        int at = sys.IndexOf(Anchor, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException($"{SystemLua}: anchor {Anchor} not found.");
        at += Anchor.Length;
        // Keep mods' requires together, in the order the mods ran.
        const string next = "\r\nrequire_global(\"Mod/";
        while (string.CompareOrdinal(sys, at, next, 0, next.Length) == 0)
            at = sys.IndexOf('\n', at + 2) is var nl and >= 0 ? nl - 1 : sys.Length;
        content.WriteText(SystemLua, sys.Insert(at, "\r\n" + line), bom: HasBom(SystemLua));
    }

    /// <summary>Mission scripts (Lua/Missions/*.lua). 60 procedural missions have no script and get no hooks.</summary>
    public IEnumerable<string> MissionScripts => content.Find("Lua/Missions", "*.lua");

    /// <summary>
    /// Appends a block to every mission script. The block runs in the mission SANDBOX (no pcall/error/io/os/_G):
    /// wrap callbacks — save the original in a local, call your code via a pcall from a global module, then
    /// ALWAYS call the original.
    /// </summary>
    public int AppendToMissionScripts(Func<string, string?> blockForScript)
    {
        int n = 0;
        foreach (var path in MissionScripts)
        {
            var script = Path.GetFileNameWithoutExtension(path);
            if (blockForScript(script) is not { Length: > 0 } block) continue;
            content.WriteText(path, content.ReadText(path).TrimEnd() + "\r\n\r\n" + block.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n", bom: HasBom(path));
            n++;
        }
        return n;
    }

    /// <summary>
    /// Adds strings (localization key → text) to Language/&lt;lang&gt;_mod.csv for every game language (proven to load,
    /// docs/EXE_NOTES.md); languages without a translation get <paramref name="fallback"/> (usually English).
    /// Several mods append to the same files. BOM and the trailing TAB of non-English rows follow the game's own file.
    /// </summary>
    public void AddStrings(IReadOnlyDictionary<string, string> fallback, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? translations = null)
    {
        foreach (var csv in content.Find("Language", "*.csv"))
        {
            var lang = Path.GetFileNameWithoutExtension(csv);
            if (!System.Text.RegularExpressions.Regex.IsMatch(lang, "^[a-z-]+$")) continue; // skips *_mod.csv
            var strings = translations?.GetValueOrDefault(lang) ?? fallback;
            var file = $"Language/{lang}_mod.csv";
            var source = content.ReadText(csv);
            int eol = source.IndexOf("\r\n", StringComparison.Ordinal);
            var tab = (eol >= 0 ? source.Substring(0, eol) : source).EndsWith("\t") ? "\t" : "";
            var text = content.Exists(file) ? content.ReadText(file) : "";
            text += string.Concat(strings.Select(kv => $"{kv.Key}\t{kv.Value}{tab}\r\n"));
            content.WriteText(file, text, bom: HasBom(csv));
        }
    }

    bool HasBom(string path) => content.ExistsInOriginal(path) && content.ReadOriginal(path) is [0xEF, 0xBB, 0xBF, ..];
}
