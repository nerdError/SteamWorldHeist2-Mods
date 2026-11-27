using SWH2.Modding;
using SWH2.Mods.CoopCommon;

namespace SWH2.Mods.RemoteCoop;

/// <summary>
/// Co-op missions for screen-sharing play (Parsec, Steam Remote Play Together): the game itself stays single-player,
/// the mod splits the crew between 2–4 players and shows each bot's owner. With the input lock every player has an
/// input device (keyboard+mouse or a gamepad) and only the owner's device controls the selected bot. No networking
/// (that is NE Co-op, mod Coop; the Lua and the dll are shared with it: Mods/Coop/Lua, Mods/Coop/native).
///
/// Installed into the game:
///  - Lua/Mod/coop.lua: backend in the GLOBAL environment (settings, owner assignment, file IO, pcall);
///  - a hook appended to every combat mission script (procedural ones get the shared generic script): owner labels
///    (SpawnWorldText) after deployment, at the start of every player turn and when a bot gets selected;
///  - modifiers coop_owner_1..4 (name only) and strings coop_owner_N = player name in every language;
///  - input lock (optional): an exe patch that loads Mods\RemoteCoop\swh2coop.dll (native/swh2coop.c) right before
///    SDL_Init; the dll filters the SDL input functions by device, the mission hook tells it whose device may play.
/// </summary>
public sealed class RemoteCoopMod : IModPlugin
{
    const string DllPath = @"Mods\RemoteCoop\swh2coop.dll";

    public void Apply(ModContext ctx)
    {
        var s = ctx.Settings;
        ctx.ReportName = "coop";
        CoopInstall.CheckAlone(ctx);

        int max = CoopInstall.MaxPlayers;
        int players = Math.Max(2, Math.Min(max, s.Int("players")));
        var names = Enumerable.Range(1, max).Select(i => CoopInstall.CleanName(s.String($"name{i}"), $"Player {i}")).ToList();
        var crew = CoopInstall.Crew(ctx);

        var manual = new SortedDictionary<string, int>(StringComparer.Ordinal);
        for (int i = 1; i <= players; i++)
            foreach (var raw in s.String($"crew{i}").Split(',', ';'))
            {
                var p = raw.Trim().ToLowerInvariant();
                if (p == "") continue;
                if (!crew.Personas.Contains(p))
                    throw new InvalidOperationException($"Player {i} crew: unknown crewmate '{raw.Trim()}'. Known: {string.Join(", ", crew.Personas)}.");
                if (manual.TryGetValue(p, out var other) && other != i)
                    throw new InvalidOperationException($"'{p}' is listed for both player {other} and player {i}.");
                manual[p] = i;
            }

        bool inputLock = s.Bool("inputLock");
        var devices = Enumerable.Range(1, max).Select(i => s.String($"device{i}")).ToList();
        if (inputLock)
        {
            var used = devices.Take(players).Where(d => d != "all").GroupBy(d => d).FirstOrDefault(g => g.Count() > 1);
            if (used != null) throw new InvalidOperationException($"Input lock: two players use the same device '{used.Key}'.");
            CoopInstall.AddDllLoader(ctx, DllPath, netplay: false);
        }

        ctx.Lua.AddGlobalModule("coop", CoopInstall.CoopLua(players, manual, crew.Personas, crew.Entities,
            s.Bool("labelTurn"), s.Bool("labelSelect"), inputLock, netplay: false, devices));
        int scripts = CoopInstall.HookMissions(ctx);

        CoopInstall.AddModifiers(ctx);
        // Player names are the same in every language.
        ctx.Lua.AddStrings(Enumerable.Range(1, max).ToDictionary(i => $"coop_owner_{i}", i => names[i - 1]));

        ctx.Report.Section("Remote Play Coop");
        for (int i = 1; i <= players; i++)
            ctx.Report.Line($"Player {i}: {names[i - 1]}" + (manual.Any(kv => kv.Value == i) ? " — " + string.Join(", ", manual.Where(kv => kv.Value == i).Select(kv => kv.Key)) : ""));
        if (inputLock)
            ctx.Report.Line("Input lock: " + string.Join(", ", Enumerable.Range(1, players).Select(i => $"{names[i - 1]} = {DeviceName(devices[i - 1])}")) + ".");
        ctx.Report.Line($"Other crewmates are dealt out in this order: {string.Join(", ", crew.Personas.Where(p => !manual.ContainsKey(p)))}.");
        ctx.Report.Line($"{scripts} combat mission scripts hooked.");
        ctx.Log($"Remote Play Coop: {players} players, hooks in {scripts} combat mission scripts.");
        ctx.Summary = $"{players} players: {string.Join(", ", names.Take(players))}" + (inputLock ? ", input lock" : "");
    }

    static string DeviceName(string device) => device switch
    {
        "kbm" => "keyboard + mouse",
        "all" => "any device",
        _ => "gamepad " + device.Substring(3),
    };
}
