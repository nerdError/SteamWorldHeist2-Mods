using System.Text.RegularExpressions;
using SWH2.Core;
using SWH2.Modding;

namespace SWH2.Mods.Challenges;

/// <summary>
/// "Quitting forfeits the mission": the game saves missions about once per turn (each save also pushes a restore
/// point, docs/EXE_NOTES.md), so quit + Continue goes back to the start of the turn — the same rewind
/// "No checkpoints" removes. Loading a mission save calls the script's OnMissionResume; we then leave the mission
/// like Abort does. Only abortable combat missions are hooked: the ship, bars, shops, the tutorial and the
/// prison break resume as usual.
/// The exit runs a few frames later from OnUpdate, not inside the load callback.
/// </summary>
public static class QuitForfeits
{
    const string Marker = "-- ==== SWH2 Challenges: quitting mid-mission forfeits it ====";

    const string Hook = Marker + @"
do
	local exitIn = nil
	local origResume = OnMissionResume
	function OnMissionResume(...)
		exitIn = 10
		if origResume then return origResume(...) end
	end
	local origUpdate = OnUpdate
	function OnUpdate(...)
		if exitIn then
			exitIn = exitIn - 1
			if exitIn <= 0 then
				exitIn = nil
				ExitMissionToWorldMap()
			end
		end
		if origUpdate then return origUpdate(...) end
	end
end";

    public static void Apply(ModContext ctx)
    {
        // Script file name (without extension) of every mission that starts in combat.
        var missions = new Dictionary<string, System.Xml.Linq.XElement>();
        foreach (var file in ctx.Content.Find("Definitions", "missions*.de"))
            foreach (var m in ctx.Content.Xml(file).Definitions("Mission"))
                missions.TryAdd(m.Name(), m);
        string? Resolve(string name, string field)
        {
            for (int guard = 0; guard < 20 && missions.TryGetValue(name, out var m); guard++)
            {
                if (m.Element(field)?.Value.Trim() is { Length: > 0 } v) return v;
                name = m.Attribute("Template")?.Value ?? "";
            }
            return null;
        }
        var combatScripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipScripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in missions.Keys)
        {
            if (missions[name].IsAbstract() || Resolve(name, "Script") is not { } script) continue;
            var file = Regex.Replace(script, @"^.*[\\/]|\.lua$", "");
            // Only where the game itself allows Abort: the tutorial and the prison break can't be left.
            bool abortable = Resolve(name, "Abortable")?.ToLowerInvariant() is not ("false" or "0");
            (Resolve(name, "StartingMode") == "Combat" && abortable ? combatScripts : skipScripts).Add(file);
        }
        // A script shared with a non-combat or non-abortable mission is left alone.
        combatScripts.ExceptWith(skipScripts);

        if (ctx.Lua.MissionScripts.Any(p => ctx.Content.ReadText(p).Contains(Marker)))
            throw new InvalidOperationException("Mission scripts already contain the quit-forfeits hook (built from a modded file?)");
        int n = ctx.Lua.AppendToMissionScripts(script => combatScripts.Contains(script) ? Hook : null);

        ctx.Report.Section("Quitting forfeits the mission");
        ctx.Report.Line($"{n} combat mission scripts: continuing a mission from a save returns you to the world map.");
        ctx.Log($"Quitting forfeits the mission: {n} combat mission scripts hooked.");
    }
}
