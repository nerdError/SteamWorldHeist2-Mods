using System.Reflection;
using System.Windows.Forms;
using SWH2.Modding;

namespace SWH2.Mods.CheatMenu;

/// <summary>
/// Cheat menu. The game's own debug menu was stripped from the release exe (docs/EXE_NOTES.md), and Lua can neither
/// read the keyboard nor draw UI, so the menu is a window of this mod in the loader (and <c>swh2 cheat ...</c>).
/// It appends commands to a file; the Lua side polls it from the mission OnUpdate and runs them with engine
/// functions (see <see cref="CheatClient"/> for the file protocol).
///
/// Installed into the game:
///  - Lua/Mod/cheats.lua: backend in the GLOBAL environment (file IO, pcall), loaded from system.lua;
///  - a hook appended to every mission script (runs in the mission SANDBOX: no pcall/io, always calls the
///    original callbacks);
///  - modifiers cheat_god / cheat_spinup / cheat_freeze built from engine StatusFlags, with names in &lt;lang&gt;_mod.csv.
/// </summary>
public sealed class CheatMenuMod : IModPlugin, IModTool, IModCommand
{
    const string Marker = "-- ==== SWH2 CheatMenu (auto-appended) ====";

    static readonly Dictionary<string, string> English = new()
    {
        ["cheat_god_name"] = "[Cheat] Invulnerable",
        ["cheat_spinup_name"] = "[Cheat] Attacks don't end turn",
        ["cheat_freeze_name"] = "[Cheat] Frozen",
    };

    static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Translations = new()
    {
        ["ru"] = new Dictionary<string, string>
        {
            ["cheat_god_name"] = "[Чит] Неуязвимость",
            ["cheat_spinup_name"] = "[Чит] Атака не завершает ход",
            ["cheat_freeze_name"] = "[Чит] Заморожен",
        },
    };

    public void Apply(ModContext ctx)
    {
        if (ctx.Content.ReadText("Lua/Missions/arctica_09.lua").Contains(Marker))
            throw new InvalidOperationException("mission scripts already contain the CheatMenu hook (an old loose-file CheatMenu is enabled too?).");

        ctx.Lua.AddGlobalModule("cheats", Resource("cheats.lua"));
        var hook = Resource("mission_hook.lua");
        int scripts = ctx.Lua.AppendToMissionScripts(script => hook.Replace("{SCRIPT}", script).TrimEnd());

        // Text insert before the closing tag keeps the rest of modifiers.xml byte-identical.
        const string file = "Definitions/modifiers.xml", close = "</ModifierDefinitions>";
        var xml = ctx.Content.ReadText(file);
        int at = xml.IndexOf(close, StringComparison.Ordinal);
        if (at < 0 || xml.IndexOf(close, at + 1, StringComparison.Ordinal) >= 0) throw new InvalidOperationException($"{file}: unexpected layout.");
        var modifiers = Resource("modifiers.xml.txt").TrimStart('\r', '\n');
        ctx.Content.WriteText(file, xml.Insert(at, modifiers), bom: ctx.Content.ReadOriginal(file) is [0xEF, 0xBB, 0xBF, ..]);

        ctx.Lua.AddStrings(English, Translations);
        ctx.Log($"CheatMenu: backend module, hooks in {scripts} mission scripts, 3 modifiers. Open it with the Cheats… button or `swh2 cheat`.");
    }

    /// <summary>Embedded Lua/XML source, stored with LF; the game's files use CRLF.</summary>
    static string Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                      ?? throw new InvalidOperationException($"missing resource {name}");
        return new StreamReader(s).ReadToEnd().Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    // ---- tool window + CLI ----------------------------------------------------------------

    static CheatForm? _form;

    public void OpenTool(ModToolContext ctx)
    {
        if (_form is null || _form.IsDisposed) _form = new CheatForm(ctx.Game);
        _form.Show(ctx.Owner as IWin32Window);
        _form.Activate();
    }

    public int RunCommand(IReadOnlyList<string> a, ModToolContext ctx)
    {
        if (a.Count == 0 || a[0] == "status")
        {
            var st = CheatClient.ReadStatus();
            ctx.Log(st == null ? "No status yet (CheatMenu not applied to the game, or no mission started)."
                : $"{(st.InMission ? "In mission" : "Last mission")}: {st.Script} (seen {st.LastSeen.ToLocalTime():HH:mm:ss}); "
                  + string.Join(", ", st.Toggles.Select(t => $"{t.Key}={(t.Value ? "on" : "off")}")));
            foreach (var line in CheatClient.ReadLogTail(5)) ctx.Log("  " + line);
            return 0;
        }
        try
        {
            string? arg = a.Count > 1 ? a[1] : null;
            if (CheatClient.Toggles.Contains(a[0]))
                arg = arg is "on" or "1" ? "1" : arg is "off" or "0" ? "0" : throw new ArgumentException($"{a[0]}: expected on|off");
            else if (a[0] == "give" && arg == null) throw new ArgumentException("give: ITEM required");
            ctx.Log($"Queued '{string.Join(" ", a)}' (id {CheatClient.Send(a[0], arg)}).");
            return 0;
        }
        catch (ArgumentException ex)
        {
            ctx.Log(ex.Message);
            return 2;
        }
    }
}
