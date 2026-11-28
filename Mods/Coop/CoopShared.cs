using System.Reflection;
using System.Xml.Linq;
using SWH2.Core;
using SWH2.Modding;

namespace SWH2.Mods.CoopCommon;

/// <summary>
/// Install parts shared by NE Co-op (Coop, network play) and NE Remote Play Coop (RemoteCoop, one game over Parsec):
/// both use the same Lua (Mods/Coop/Lua) and the same native dll (Mods/Coop/native) — the dll's network part runs only
/// when the exe marker says so (Coop). The two mods can't be on together (same mission hook, same exe patch).
/// </summary>
public static class CoopInstall
{
    public const string Marker = "-- ==== SWH2 Co-op (auto-appended) ====";
    public const int MaxPlayers = 4;

    // Dll loader: the one call of SDL_Init (`call [rip+IAT]`, before any input is read) goes to a stub that
    // LoadLibraryA's the dll first. The path is relative: the game runs with the game folder as working directory.
    // If the dll is missing, LoadLibraryA just fails and the game runs without it.
    // Steam-build RVAs (exe.Map translates them for GOG); the original call is `call [rip+rel]` = SDL_Init.
    const uint SdlInitCall = 0x7F8A7A, IatSdlInit = 0xB47870, IatLoadLibraryA = 0xB47180;

    /// <summary>Refuses a second co-op mod: both append the same hook to the mission scripts.</summary>
    public static void CheckAlone(ModContext ctx)
    {
        if (ctx.Lua.MissionScripts.Any(p => ctx.Content.ReadText(p).Contains(Marker)))
            throw new InvalidOperationException("NE Co-op and NE Remote Play Coop can't be on together (or the mission scripts were built from a modded file): untick one of them.");
    }

    /// <summary>
    /// Personas the player can field (Captain first, then personas.xml order) and tactics entity → persona. Every
    /// Captain variant (rearmed, final boss fight) counts as "captain", the name of the Captain's entity in missions.
    /// </summary>
    public static (List<string> Personas, SortedDictionary<string, string> Entities) Crew(ModContext ctx)
    {
        var all = ctx.Content.Xml("Definitions/personas.xml").Definitions("Persona").ToList();
        var byName = all.GroupBy(p => p.Name()).ToDictionary(g => g.Key, g => g.First());
        string? Field(XElement p, string field)
        {
            for (int guard = 0; guard < 20 && p != null; guard++)
            {
                if (p.Element(field)?.Value.Trim() is { Length: > 0 } v) return v;
                p = byName.GetValueOrDefault(p.Attribute("Template")?.Value ?? "")!;
            }
            return null;
        }

        var personas = new List<string> { "captain" };
        var entities = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in all.Where(p => !p.IsAbstract()))
        {
            var usage = Field(p, "LoadoutUsage");
            if (usage is not ("crew" or "captain")) continue;
            var persona = usage == "captain" ? "captain" : p.Name();
            if (!personas.Contains(persona)) personas.Add(persona);
            if (Field(p, "TacticsEntity") is { } entity) entities[entity] = persona;
        }
        return (personas, entities);
    }

    /// <param name="dllPath">Relative to the game folder, e.g. <c>Mods\Coop\swh2coop.dll</c>.</param>
    /// <param name="netplay">Marks the section ("NET1" at +0x60) so the dll turns on its network part (native/netplay.c).</param>
    public static void AddDllLoader(ModContext ctx, string dllPath, bool netplay)
    {
        var exe = ctx.Exe;
        uint sdlInitCall = exe.Map(SdlInitCall), iatSdlInit = exe.Map(IatSdlInit), iatLoadLibraryA = exe.Map(IatLoadLibraryA);
        uint va = exe.AddSection(".coop", 0x100);
        var path = System.Text.Encoding.ASCII.GetBytes(dllPath + "\0");
        if (path.Length > 0x40) throw new InvalidOperationException("dll path too long");
        var code = new List<byte>();
        void Add(string hex) => code.AddRange(Hex.Parse(hex));
        void Rel(uint target) => code.AddRange(BitConverter.GetBytes(checked((int)((long)target - (va + code.Count + 4)))));
        Add("51");                  // push rcx            (SDL_Init flags; also aligns the stack for the call)
        Add("4883EC20");            // sub rsp, 0x20       (shadow space)
        Add("488D0D"); Rel(va + 0x20);           // lea rcx, [path]
        Add("FF15"); Rel(iatLoadLibraryA);       // call [LoadLibraryA]
        Add("4883C420");            // add rsp, 0x20
        Add("59");                  // pop rcx
        Add("FF25"); Rel(iatSdlInit);            // jmp [SDL_Init]  (returns straight to the game)
        while (code.Count < 0x20) code.Add(0xCC);
        code.AddRange(path);
        if (netplay)
        {
            while (code.Count < 0x60) code.Add(0);
            code.AddRange(System.Text.Encoding.ASCII.GetBytes("NET1"));
        }
        exe.Write(va, code.ToArray());
        var call = new byte[6];
        call[0] = 0xE8;             // call stub; nop
        BitConverter.GetBytes(checked((int)((long)va - (sdlInitCall + 5)))).CopyTo(call, 1);
        call[5] = 0x90;
        exe.Patch(sdlInitCall, ExeImage.CallIndirect(sdlInitCall, iatSdlInit), call);
    }

    /// <summary>Modifiers coop_owner_1..4: name only (the owner in the bot's info), no effect.</summary>
    public static void AddModifiers(ModContext ctx)
    {
        // Text insert before the closing tag keeps the rest of modifiers.xml byte-identical.
        const string file = "Definitions/modifiers.xml", close = "</ModifierDefinitions>";
        var xml = ctx.Content.ReadText(file);
        int at = xml.IndexOf(close, StringComparison.Ordinal);
        if (at < 0 || xml.IndexOf(close, at + 1, StringComparison.Ordinal) >= 0) throw new InvalidOperationException($"{file}: unexpected layout.");
        var modifiers = "\t<!-- SWH2 Co-op: owner of a crewmate (name only, no effect) -->\r\n" + string.Concat(Enumerable.Range(1, MaxPlayers).Select(i =>
            $"\t<Modifier Name=\"coop_owner_{i}\">\r\n" +
            $"\t\t<LocalizedNameId>coop_owner_{i}</LocalizedNameId>\r\n" +
            "\t\t<RemoveFromPlayerOnMissionEnd>true</RemoveFromPlayerOnMissionEnd>\r\n" +
            "\t</Modifier>\r\n"));
        ctx.Content.WriteText(file, xml.Insert(at, modifiers), bom: ctx.Content.ReadOriginal(file) is [0xEF, 0xBB, 0xBF, ..]);
    }

    /// <summary>A name the CSV and the game's text can carry: one line, no tabs, not empty.</summary>
    public static string CleanName(string name, string fallback)
    {
        var clean = string.Join(" ", name.Split(['\t', '\r', '\n', '"'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (clean.Length > 24) clean = clean.Substring(0, 24).TrimEnd();
        return clean.Length > 0 ? clean : fallback;
    }

    /// <summary>coop.lua with the install-time values filled in.</summary>
    public static string CoopLua(int players, IEnumerable<KeyValuePair<string, int>> manual, List<string> personas,
        SortedDictionary<string, string> entities, bool labelTurn, bool labelSelect, bool inputLock, bool netplay, IEnumerable<string> devices,
        bool labelAlways = false) =>
        Resource("coop.lua")
            .Replace("{PLAYERS}", players.ToString())
            .Replace("{MANUAL}", string.Join(", ", manual.Select(kv => $"[\"{kv.Key}\"] = {kv.Value}")))
            .Replace("{ORDER}", string.Join(", ", personas.Select((p, i) => $"[\"{p}\"] = {i + 1}")))
            .Replace("{ENTITIES}", string.Join(", ", entities.Select(kv => $"[\"{kv.Key}\"] = \"{kv.Value}\"")))
            .Replace("{LABEL_TURN}", labelTurn ? "true" : "false")
            .Replace("{LABEL_SELECT}", labelSelect ? "true" : "false")
            .Replace("{LABEL_ALWAYS}", labelAlways ? "true" : "false")

            .Replace("{INPUT_LOCK}", inputLock ? "true" : "false")
            .Replace("{NET_TEST}", netplay ? "true" : "false")
            .Replace("{DEVICES}", string.Join(", ", devices.Select(d => $"\"{d}\"")));

    /// <summary>The mission hook (Lua/mission_hook.lua) appended to every combat mission script.</summary>
    public static int HookMissions(ModContext ctx)
    {
        // Procedural combat missions have no script; they share the generic one (made here or by Challenges).
        var missions = new SWH2.Mods.Challenges.MissionScripts(ctx, addGeneric: true);
        var hook = Resource("mission_hook.lua");
        return ctx.Lua.AppendToMissionScripts(script => missions.Combat.Contains(script) ? hook.Replace("{SCRIPT}", script).TrimEnd() : null);
    }

    /// <summary>Embedded Lua source (the calling mod embeds Mods/Coop/Lua/*.lua), stored with LF; the game's files use CRLF.</summary>
    public static string Resource(string name)
    {
        using var s = Assembly.GetCallingAssembly().GetManifestResourceStream(name) ?? typeof(CoopInstall).Assembly.GetManifestResourceStream(name)
                      ?? throw new InvalidOperationException($"missing resource {name}");
        return new StreamReader(s).ReadToEnd().Replace("\r\n", "\n").Replace("\n", "\r\n");
    }
}
