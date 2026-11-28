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
            s.Bool("labelTurn"), s.Bool("labelSelect"), inputLock: false, netplay: true, ["all", "all", "all", "all"], s.Bool("labelAlways")));
        int scripts = CoopInstall.HookMissions(ctx);
        HookWorldMap(ctx);
        CoopInstall.AddModifiers(ctx);
        AddStrings(ctx);
        AddCounters(ctx);
        AddPartySlotOwner(ctx);
        FitSlotName(ctx);
        AddPopups(ctx);
        AddNameTags(ctx);

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
        "coop_owner_1", "coop_owner_2", "coop_move_1", "coop_move_2",
        "coop_net_host_waiting", "coop_net_host_connected", "coop_net_host_closed", "coop_net_host_failed", "coop_net_host_joining",
        "coop_net_guest_waiting", "coop_net_guest_connected", "coop_net_guest_closed", "coop_net_guest_failed",
        "coop_net_turn_both", "coop_net_turn_p1", "coop_net_turn_p2", "coop_net_turn_enemy",
        "tutorial_coop_net_guest_left", "tutorial_coop_net_guest_left_page1", "tutorial_coop_net_guest_back", "tutorial_coop_net_guest_back_page1",
        "tutorial_coop_net_guest_lost", "tutorial_coop_net_guest_lost_page1", "tutorial_coop_net_host_left", "tutorial_coop_net_host_left_page1",
        "tutorial_coop_net_host_lost", "tutorial_coop_net_host_lost_page1",
        "coop_net_refused_cell", "coop_net_refused_gone", "coop_net_refused_notyours", "coop_net_refused_bad",
        "tutorial_coop_net_version", "tutorial_coop_net_version_page1", "tutorial_coop_net_wait_host", "tutorial_coop_net_wait_host_page1",
    ];

    /// <summary>Notifications in the game's popup window (the dll's events, coop.lua netEvent): tutorials_popup.xml.</summary>
    static readonly string[] Popups = ["guest_left", "guest_back", "guest_lost", "host_left", "host_lost", "version", "wait_host"];

    static void AddStrings(ModContext ctx)
    {
        var en = new Dictionary<string, string>
        {
            ["coop_net_default_1"] = "Host",
            ["coop_net_default_2"] = "Guest",
            ["coop_owner_1"] = "{1}",
            ["coop_owner_2"] = "{2}",
            ["coop_move_1"] = "[ {1} ]",
            ["coop_move_2"] = "[ {2} ]",
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
            ["tutorial_coop_net_guest_left"] = "{2} left the battle",
            ["tutorial_coop_net_guest_left_page1"] = "{2} is in the main menu. {2}'s crewmates are yours until {2} comes back to the battle.",
            ["tutorial_coop_net_guest_back"] = "{2} is back",
            ["tutorial_coop_net_guest_back_page1"] = "{2} is in the battle again and controls their own crewmates.",
            ["tutorial_coop_net_guest_lost"] = "Connection to {2} lost",
            ["tutorial_coop_net_guest_lost_page1"] = "{2}'s game doesn't answer. {2}'s crewmates are yours; when {2} is back, they join the battle by themselves.",
            ["tutorial_coop_net_host_left"] = "{1} left the battle",
            ["tutorial_coop_net_host_left_page1"] = "The host is in the main menu. Your game goes to the main menu too; when {1} enters a battle again, you join it by yourself.",
            ["tutorial_coop_net_host_lost"] = "Connection to {1} lost",
            ["tutorial_coop_net_host_lost_page1"] = "The host's game doesn't answer. Your game goes to the main menu; when {1} is in a battle again, you join it by yourself.",
            ["coop_net_refused_cell"] = "{1}'s crewmate is going there",
            ["coop_net_refused_gone"] = "Not accepted by {1}",
            ["coop_net_refused_notyours"] = "Not accepted by {1}",
            ["coop_net_refused_bad"] = "Not accepted by {1}",
            ["tutorial_coop_net_version"] = "Another NE Co-op version than {1}'s",
            ["tutorial_coop_net_wait_host"] = "Waiting for {1}",
            ["tutorial_coop_net_wait_host_page1"] = "The battle is won. When {1} starts the next mission, your game goes there by itself (through the main menu, a few seconds). Until then you can look around the map.",
            ["tutorial_coop_net_version_page1"] = "You and {1} have different versions of NE Co-op. The battle may go differently in the two games: install the same version on both PCs (the loader of the newer one, Apply to game).",
        };
        var ru = new Dictionary<string, string>
        {
            ["coop_net_default_1"] = "Хост",
            ["coop_net_default_2"] = "Гость",
            ["coop_owner_1"] = "{1}",
            ["coop_owner_2"] = "{2}",
            ["coop_move_1"] = "[ {1} ]",
            ["coop_move_2"] = "[ {2} ]",
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
            ["tutorial_coop_net_guest_left"] = "{2}: выход из боя",
            ["tutorial_coop_net_guest_left_page1"] = "{2} в главном меню. Бойцы игрока {2} под твоим управлением, пока {2} не вернётся в бой.",
            ["tutorial_coop_net_guest_back"] = "{2} снова в бою",
            ["tutorial_coop_net_guest_back_page1"] = "{2} снова в бою и управляет своими бойцами.",
            ["tutorial_coop_net_guest_lost"] = "Связь с {2} потеряна",
            ["tutorial_coop_net_guest_lost_page1"] = "Игра {2} не отвечает. Бойцы игрока {2} под твоим управлением; когда связь вернётся, {2} сам(а) войдёт в бой.",
            ["tutorial_coop_net_host_left"] = "{1}: выход из боя",
            ["tutorial_coop_net_host_left_page1"] = "Хост в главном меню. Твоя игра тоже выйдет в главное меню; когда {1} снова войдёт в бой, ты присоединишься сам(а).",
            ["tutorial_coop_net_host_lost"] = "Связь с {1} потеряна",
            ["tutorial_coop_net_host_lost_page1"] = "Игра хоста не отвечает. Твоя игра выйдет в главное меню; когда {1} снова будет в бою, ты присоединишься сам(а).",
            ["coop_net_refused_cell"] = "Туда уже идёт боец игрока {1}",
            ["coop_net_refused_gone"] = "{1}: команда не принята",
            ["coop_net_refused_notyours"] = "{1}: команда не принята",
            ["coop_net_refused_bad"] = "{1}: команда не принята",
            ["tutorial_coop_net_version"] = "Версия NE Co-op не как у {1}",
            ["tutorial_coop_net_wait_host"] = "Ждём {1}",
            ["tutorial_coop_net_wait_host_page1"] = "Бой выигран. Когда {1} начнёт следующую миссию, твоя игра перейдёт туда сама (через главное меню, несколько секунд). А пока можно осмотреть карту.",
            ["tutorial_coop_net_version_page1"] = "У тебя и у {1} разные версии NE Co-op. Бой в двух играх может пойти по-разному: поставьте одну версию на оба ПК (загрузчик новой версии, Apply to game).",
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
        var counters = "\t<!-- SWH2 Co-op: network status (text only) -->\r\n" + string.Concat(NameKeys.Where(k => k.StartsWith("coop_net_", StringComparison.Ordinal) && !k.StartsWith("coop_net_refused_", StringComparison.Ordinal)).Select(k =>
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

    /// <summary>
    /// The dll's notifications in the game's own popup window (TutorialPopup with force, so it shows every time): one
    /// tutorial per event, one page, title tutorial_&lt;name&gt; (the game's rule), texts with the nicknames.
    /// </summary>
    /// <summary>The world map hook (Lua/map_hook.lua, OnEnterMap): a guest back from a won battle is told it waits for the host.</summary>
    static void HookWorldMap(ModContext ctx)
    {
        const string file = "Lua/WorldMap/worldmap.lua";
        var hook = CoopInstall.Resource("map_hook.lua").TrimEnd();
        ctx.Content.WriteText(file, ctx.Content.ReadText(file).TrimEnd() + "\r\n\r\n" + hook + "\r\n", bom: ctx.Content.ReadOriginal(file) is [0xEF, 0xBB, 0xBF, ..]);
    }

    static void AddPopups(ModContext ctx)
    {
        const string file = "Definitions/tutorials_popup.xml", close = "</Tutorials>";
        var xml = ctx.Content.ReadText(file);
        int at = xml.LastIndexOf(close, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException($"{file}: unexpected layout.");
        var popups = "\t<!-- SWH2 Co-op: network notifications -->\r\n" + string.Concat(Popups.Select(p =>
            $"\t<Tutorial Name=\"coop_net_{p}\" Template=\"BASE\">\r\n" +
            "\t\t<Optional>false</Optional>\r\n" +
            $"\t\t<Page>\r\n\t\t\t<Text>tutorial_coop_net_{p}_page1</Text>\r\n\t\t</Page>\r\n" +
            "\t</Tutorial>\r\n")) + "\r\n";
        ctx.Content.WriteText(file, xml.Insert(at, popups), bom: ctx.Content.ReadOriginal(file) is [0xEF, 0xBB, 0xBF, ..]);
    }

    /// <summary>Name tag colours (host, guest): bright versions of the portrait colours.</summary>
    static readonly string[] TagColors = ["255,170,60,255", "90,195,255,255"];

    /// <summary>
    /// World texts for the name tags over the crewmates (coop.lua C.netTags): attached to the crewmate, no drift, in
    /// the owner's colour, gone after 4 s (the Lua spawns them again every ~2.5 s), hidden in cutscenes.
    /// </summary>
    static void AddNameTags(ModContext ctx)
    {
        const string file = "Definitions/entities.worldtexts.xml", close = "</EntityDefinitions>";
        var xml = ctx.Content.ReadText(file);
        int at = xml.LastIndexOf(close, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException($"{file}: unexpected layout.");
        var tags = "\t<!-- SWH2 Co-op: name tags over the crewmates (1 = host, 2 = guest) -->\r\n" + string.Concat(Enumerable.Range(1, 2).Select(i =>
            $"\t<Entity Name=\"coop_tag_{i}\" Template=\"WORLDTEXT\">\r\n" +
            "\t\t<WorldText>\r\n" +
            "\t\t\t<AttachToEntity>true</AttachToEntity>\r\n" +
            "\t\t\t<Velocity>0,0</Velocity>\r\n" +
            $"\t\t\t<Color>{TagColors[i - 1]}</Color>\r\n" +
            "\t\t\t<Scale Start=\"1\" Time=\"0\">1.1</Scale>\r\n" +
            "\t\t\t<Fade Delay=\"4\">0.25</Fade>\r\n" +
            "\t\t\t<SpawnDelay IgnoreWhenOver=\"0\">0</SpawnDelay>\r\n" +
            "\t\t\t<Offset>0,-40</Offset>\r\n" +
            "\t\t\t<HideInCutscenes>true</HideInCutscenes>\r\n" +
            "\t\t</WorldText>\r\n" +
            "\t</Entity>\r\n")) +
            // The other player's move target (coop.lua C.netMove): an anchor entity on the tile (a game pointer over it)
            // with "[ nick ]" attached by SpawnWorldText. A WORLDTEXT made by SpawnEntity was never drawn (2.6.0-2.6.2).
            "\t<!-- SWH2 Co-op: the tile the other player points at for a move -->\r\n" + string.Concat(Enumerable.Range(1, 2).Select(i =>
            $"\t<Entity Name=\"coop_move_{i}\" Template=\"WORLDTEXT\">\r\n" +
            "\t\t<WorldText>\r\n" +
            "\t\t\t<AttachToEntity>true</AttachToEntity>\r\n" +
            "\t\t\t<Velocity>0,0</Velocity>\r\n" +
            $"\t\t\t<Color>{TagColors[i - 1]}</Color>\r\n" +
            "\t\t\t<Scale Start=\"1.6\" Time=\"0.15\">1.2</Scale>\r\n" +
            "\t\t\t<Fade Delay=\"3600\">0.2</Fade>\r\n" +
            "\t\t\t<SpawnDelay IgnoreWhenOver=\"0\">0</SpawnDelay>\r\n" +
            "\t\t\t<Offset>0,-30</Offset>\r\n" +
            "\t\t\t<HideInCutscenes>true</HideInCutscenes>\r\n" +
            "\t\t</WorldText>\r\n" +
            "\t</Entity>\r\n")) +
            "\t<Entity Name=\"coop_move_anchor\">\r\n\t\t<Position/>\r\n\t\t<PointOfInterest Definition=\"hunters_mark_inactive\"/>\r\n\t</Entity>\r\n";
        ctx.Content.WriteText(file, xml.Insert(at, tags), bom: ctx.Content.ReadOriginal(file) is [0xEF, 0xBB, 0xBF, ..]);
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

    /// <summary>
    /// The main menu slot name (ui.me template main_menu_profile_slot, label "name"), where the Coop dll writes
    /// "Co-op: &lt;nick&gt;": a fixed width with MinScale, so a long name gets smaller instead of cut ("Game 1" stays as it is).
    /// </summary>
    static void FitSlotName(ModContext ctx)
    {
        const string file = "UI/ui.me";
        const string label = "Name=\"name\" Style=\"MainMenuButtonLabel\"";
        var xml = ctx.Content.ReadText(file);
        int template = xml.IndexOf("Name=\"main_menu_profile_slot\"", StringComparison.Ordinal);
        int at = template < 0 ? -1 : xml.IndexOf(label, template, StringComparison.Ordinal);
        if (at < 0 || at - template > 1000) throw new InvalidOperationException($"{file}: unexpected profile slot layout.");
        xml = xml.Insert(at + label.Length, " Width=\"640\" MinScale=\"0.6\"");
        ctx.Content.WriteText(file, xml, bom: ctx.Content.ReadOriginal(file) is [0xEF, 0xBB, 0xBF, ..]);
    }
}
