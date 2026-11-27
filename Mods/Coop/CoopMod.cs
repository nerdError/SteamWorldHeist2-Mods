using SWH2.Modding;
using SWH2.Mods.CoopCommon;

namespace SWH2.Mods.Coop;

/// <summary>
/// NE Co-op: network play in missions — every player runs their own copy of the game (docs/MULTIPLAYER.md). The host's
/// game decides (its player commands, the AI, the dice); the guest's game plays the host's commands live over TCP and
/// sends its own player's commands to the host. The crew is dealt alternately (1st host, 2nd guest, 3rd host…), each
/// player can select only their own. Screen-sharing co-op (Parsec) is the separate mod RemoteCoop.
///
/// Installed into the game:
///  - an exe patch that loads Mods\Coop\swh2coop.dll right before SDL_Init, marked for the network part
///    (native/netplay.c, nettcp.c). It does nothing until a network game is started from the loader window
///    (CoopTools / NetPlayForm: swh2_net_session.txt) — a normal start of the game plays single-player;
///  - Lua/Mod/coop.lua + a hook in every combat mission script (shared with RemoteCoop): owner labels, the crew list
///    for the split, the HUD status;
///  - modifiers coop_owner_1/2, strings (nicknames: the dll puts the players' nicknames into them at run time),
///    HUD counters (network status, whose move), owner colour + name on the crew portraits (ui.me).
/// </summary>
public sealed partial class CoopMod : IModPlugin
{
    const string DllPath = @"Mods\Coop\swh2coop.dll";

    public void Apply(ModContext ctx)
    {
        var s = ctx.Settings;
        ctx.ReportName = "coop";
        CoopInstall.CheckAlone(ctx);
        var crew = CoopInstall.Crew(ctx);

        CoopInstall.AddDllLoader(ctx, DllPath, netplay: true);
        ctx.Lua.AddGlobalModule("coop", CoopInstall.CoopLua(2, [], crew.Personas, crew.Entities,
            s.Bool("labelTurn"), s.Bool("labelSelect"), inputLock: false, netplay: true, ["all", "all", "all", "all"]));
        int scripts = CoopInstall.HookMissions(ctx);
        CoopInstall.AddModifiers(ctx);
        AddStrings(ctx);
        AddCounters(ctx);
        AddPartySlotOwner(ctx);

        ctx.Report.Section("Co-op (network)");
        ctx.Report.Line("Start a network game from the loader: select NE Co-op → Network game… (host or join, nickname).");
        ctx.Report.Line("The crew is dealt alternately: 1st crewmate host, 2nd guest, 3rd host…");
        ctx.Report.Line($"{scripts} combat mission scripts hooked.");
        ctx.Log($"Co-op: network play, hooks in {scripts} combat mission scripts.");
        ctx.Summary = "network play (host / join in the Network game window)";
    }

    /// <summary>
    /// Texts with the players' nicknames. The game's text table is filled from the CSVs at start, the nicknames are
    /// known only in a network game, so: every text <c>K</c> here also has a template <c>coop_tpl_K</c> with {1} = host,
    /// {2} = guest; the dll (netplay.c ApplyNames) writes the template with the nicknames into <c>K</c> in the game's
    /// table whenever they change. Until then <c>K</c> shows the template with the default names "Host" / "Guest".
    /// The dll has the same list of keys (NameKeys) — keep them in step.
    /// </summary>
    static readonly string[] NameKeys =
    [
        "coop_owner_1", "coop_owner_2",
        "coop_net_host_waiting", "coop_net_host_connected", "coop_net_host_closed", "coop_net_host_failed", "coop_net_host_joining",
        "coop_net_guest_waiting", "coop_net_guest_connected", "coop_net_guest_closed", "coop_net_guest_failed",
        "coop_net_turn_both", "coop_net_turn_p1", "coop_net_turn_p2", "coop_net_turn_enemy",
    ];

    static void AddStrings(ModContext ctx)
    {
        var en = new Dictionary<string, string>
        {
            ["coop_net_default_1"] = "Host",
            ["coop_net_default_2"] = "Guest",
            ["coop_owner_1"] = "{1}",
            ["coop_owner_2"] = "{2}",
            ["coop_net_host_waiting"] = "Host · waiting for a player",
            ["coop_net_host_connected"] = "Host · {2} connected",
            ["coop_net_host_closed"] = "Host · {2} left",
            ["coop_net_host_failed"] = "Host · port busy, no one can join",
            ["coop_net_host_joining"] = "Host · waiting for {2} to enter the battle",
            ["coop_net_guest_waiting"] = "Guest · connecting…",
            ["coop_net_guest_connected"] = "Guest · connected to {1}",
            ["coop_net_guest_closed"] = "Guest · connection to {1} lost",
            ["coop_net_guest_failed"] = "Guest · connection to {1} lost",
            ["coop_net_turn_both"] = "Crew's turn: {1} and {2}",
            ["coop_net_turn_p1"] = "{1}'s move",
            ["coop_net_turn_p2"] = "{2}'s move",
            ["coop_net_turn_enemy"] = "Enemy turn",
        };
        var ru = new Dictionary<string, string>
        {
            ["coop_net_default_1"] = "Хост",
            ["coop_net_default_2"] = "Гость",
            ["coop_owner_1"] = "{1}",
            ["coop_owner_2"] = "{2}",
            ["coop_net_host_waiting"] = "Хост · ждём второго игрока",
            ["coop_net_host_connected"] = "Хост · на связи: {2}",
            ["coop_net_host_closed"] = "Хост · отключился: {2}",
            ["coop_net_host_failed"] = "Хост · порт занят, подключиться нельзя",
            ["coop_net_host_joining"] = "Хост · ждём, пока {2} войдёт в бой",
            ["coop_net_guest_waiting"] = "Гость · подключаемся…",
            ["coop_net_guest_connected"] = "Гость · связь с: {1}",
            ["coop_net_guest_closed"] = "Гость · связь потеряна: {1}",
            ["coop_net_guest_failed"] = "Гость · связь потеряна: {1}",
            ["coop_net_turn_both"] = "Ход экипажа: {1} и {2}",
            ["coop_net_turn_p1"] = "Ходит: {1}",
            ["coop_net_turn_p2"] = "Ходит: {2}",
            ["coop_net_turn_enemy"] = "Ход врагов",
        };
        Dictionary<string, string> Full(Dictionary<string, string> t)
        {
            var d = new Dictionary<string, string>(t);
            foreach (var k in NameKeys)
            {
                d["coop_tpl_" + k] = t[k];
                d[k] = t[k].Replace("{1}", t["coop_net_default_1"]).Replace("{2}", t["coop_net_default_2"]);
            }
            d["coop_net_empty"] = " ";
            return d;
        }
        ctx.Lua.AddStrings(Full(en), new Dictionary<string, IReadOnlyDictionary<string, string>> { ["ru"] = Full(ru) });
    }

    /// <summary>
    /// HUD (coop.lua C.netStatus): counters with a text header and no number, one per network status and per "whose
    /// move" state; Lua swaps them. No effects or sounds (they change during play).
    /// </summary>
    static void AddCounters(ModContext ctx)
    {
        const string file = "Definitions/counters.xml", close = "</Counters>";
        var xml = ctx.Content.ReadText(file);
        int at = xml.LastIndexOf(close, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException($"{file}: unexpected layout.");
        var counters = "\t<!-- SWH2 Co-op: network status (text only) -->\r\n" + string.Concat(NameKeys.Where(k => k.StartsWith("coop_net_", StringComparison.Ordinal)).Select(k =>
            $"\t<Counter Name=\"{k}\">\r\n" +
            "\t\t<PanelTemplate>counter_generic_template</PanelTemplate>\r\n" +
            $"\t\t<HeaderStringId>{k}</HeaderStringId>\r\n" +
            "\t\t<CounterStringId>coop_net_empty</CounterStringId>\r\n" +
            "\t\t<HiddenWhenZero>false</HiddenWhenZero>\r\n" +
            "\t\t<ShowOneNumber>true</ShowOneNumber>\r\n" +
            "\t\t<AutoComplete>false</AutoComplete>\r\n" +
            "\t\t<RemoveDelay>0</RemoveDelay>\r\n" +
            "\t\t<Priority>-10</Priority>\r\n" +
            "\t</Counter>\r\n"));
        ctx.Content.WriteText(file, xml.Insert(at, counters), bom: ctx.Content.ReadOriginal(file) is [0xEF, 0xBB, 0xBF, ..]);
    }

    /// <summary>Owner colours of the crew portraits at the bottom of the tactics HUD (player 1 = host, 2 = guest).</summary>
    static readonly string[] OwnerColors = ["255, 150, 40, 150", "60, 170, 255, 150"];

    /// <summary>
    /// The crew portrait slot (ui.me template tactics_partyslot_template) gets per player a tinted slot background behind
    /// the portrait (coop_owner_frame_N) and the player's name on top (coop_owner_label_N), all hidden; the Coop dll shows
    /// the owner's after the game fills a slot (netplay.c, "party slots"). Text inserts keep the rest of ui.me as it is.
    /// </summary>
    static void AddPartySlotOwner(ModContext ctx)
    {
        const string file = "UI/ui.me";
        var xml = ctx.Content.ReadText(file);
        int template = xml.IndexOf("Name=\"tactics_partyslot_template\"", StringComparison.Ordinal);
        int button = template < 0 ? -1 : xml.IndexOf("<Button Style=\"CombatPartySlot\"", template, StringComparison.Ordinal);
        int open = button < 0 ? -1 : xml.IndexOf('>', button) + 1;
        int close = button < 0 ? -1 : xml.IndexOf("</Button>", button, StringComparison.Ordinal);
        int styles = xml.IndexOf("<Style Name=\"CombatPartySlotAP\">", StringComparison.Ordinal);
        if (open <= 0 || close < open || styles < 0 || styles > template) throw new InvalidOperationException($"{file}: unexpected party slot layout.");
        string indent = "\r\n\t\t\t\t";
        var frames = string.Concat(Enumerable.Range(1, 2).Select(i =>
            $"{indent}<Panel Name=\"coop_owner_frame_{i}\" Width=\"100%\" Height=\"100%\" Anchor=\"Center\" Style=\"CoopOwnerFrame{i}\" Hidden=\"True\" />"));
        var labels = string.Concat(Enumerable.Range(1, 2).Select(i =>
            $"\t<Label Name=\"coop_owner_label_{i}\" Text=\"#coop_owner_{i}\" Style=\"LabelTacticsPartySlotHealth\" Anchor=\"North\" Width=\"100%\" Height=\"40\" Hidden=\"True\" />\r\n\t\t\t"));
        var style = string.Concat(Enumerable.Range(1, 2).Select(i =>
            $"<Style Name=\"CoopOwnerFrame{i}\">\r\n\t\t\t<Background>\r\n\t\t\t\t<Texture>Textures/UI/Combat/party_slot_selected.png</Texture>\r\n" +
            $"\t\t\t\t<TextureColor>{OwnerColors[i - 1]}</TextureColor>\r\n\t\t\t</Background>\r\n\t\t</Style>\r\n\t\t"));
        xml = xml.Insert(close, labels).Insert(open, frames).Insert(styles, style);   // back to front: offsets stay valid
        ctx.Content.WriteText(file, xml, bom: ctx.Content.ReadOriginal(file) is [0xEF, 0xBB, 0xBF, ..]);
    }
}
