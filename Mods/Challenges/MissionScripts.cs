using System.Text.RegularExpressions;
using System.Xml.Linq;
using SWH2.Core;
using SWH2.Modding;

namespace SWH2.Mods.Challenges;

/// <summary>
/// Mission definitions as the Lua challenges see them: which scripts belong to combat missions, plus a shared stub
/// script for the procedural combat missions that have none (otherwise mission hooks never run there).
/// </summary>
public sealed class MissionScripts
{
    public const string GenericName = "swh2_generic";
    public const string GenericPath = "Lua/Missions/" + GenericName + ".lua";

    readonly Dictionary<string, XElement> _missions = new();

    /// <summary>Script file names (no extension) used only by combat missions.</summary>
    public HashSet<string> Combat { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Subset of <see cref="Combat"/>: only missions the game lets you abort.</summary>
    public HashSet<string> Abortable { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Names of all combat missions.</summary>
    public SortedSet<string> CombatMissions { get; } = new(StringComparer.Ordinal);
    /// <summary>Procedural combat missions that got the generic script.</summary>
    public int GenericCount { get; private set; }

    public MissionScripts(ModContext ctx, bool addGeneric)
    {
        foreach (var file in ctx.Content.Find("Definitions", "missions*.de"))
            foreach (var m in ctx.Content.Xml(file).Definitions("Mission"))
                _missions.TryAdd(m.Name(), m);

        if (addGeneric) AddGeneric(ctx);

        var other = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var notAbortable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, m) in _missions)
        {
            if (m.IsAbstract()) continue;
            bool combat = Resolve(name, "StartingMode") == "Combat";
            if (combat) CombatMissions.Add(name);
            if (Resolve(name, "Script") is not { } script) continue;
            var file = ScriptName(script);
            // The tutorial and the prison break can't be aborted.
            bool abortable = Resolve(name, "Abortable")?.ToLowerInvariant() is not ("false" or "0");
            (combat ? Combat : other).Add(file);
            if (!abortable) notAbortable.Add(file);
        }
        // A script shared with a non-combat mission is left alone.
        Combat.ExceptWith(other);
        Abortable.UnionWith(Combat.Where(s => !notAbortable.Contains(s)));
    }

    public static string ScriptName(string path) => Regex.Replace(path, @"^.*[\\/]|\.lua$", "");

    string? Resolve(string name, string field)
    {
        for (int guard = 0; guard < 20 && _missions.TryGetValue(name, out var m); guard++)
        {
            if (m.Element(field)?.Value.Trim() is { Length: > 0 } v) return v;
            name = m.Attribute("Template")?.Value ?? "";
        }
        return null;
    }

    void AddGeneric(ModContext ctx)
    {
        if (ctx.Content.Exists(GenericPath)) throw new InvalidOperationException($"{GenericPath} already exists (built from a modded file?)");
        foreach (var (name, m) in _missions)
        {
            if (m.IsAbstract() || Resolve(name, "StartingMode") != "Combat" || Resolve(name, "Script") != null) continue;
            if (m.Element("Script") is { } own) own.Value = $@"Lua\Missions\{GenericName}.lua";
            else m.Add(new XElement("Script", $@"Lua\Missions\{GenericName}.lua"));
            GenericCount++;
        }
        ctx.Content.WriteText(GenericPath, "-- SWH2 Challenges: shared script for procedural combat missions (they have none of their own).\r\n");
        ctx.Log($"Mission hooks: {GenericCount} procedural combat missions get the generic script.");
    }

    /// <summary>Appends a hook block to the generic script (it is not an original file, so LuaPatcher skips it).</summary>
    public static void AppendToGeneric(ModContext ctx, string block)
    {
        if (!ctx.Content.Exists(GenericPath)) return;
        ctx.Content.WriteText(GenericPath, ctx.Content.ReadText(GenericPath).TrimEnd() + "\r\n\r\n"
            + block.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n");
    }
}
