using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SWH2.Core;
using SWH2.Modding;

namespace SWH2.Mods.Randomizer;

/// <summary>
/// Things that happen inside missions, through Lua hooks in the mission scripts (the Lua mod recipe of
/// docs/MODDING.md): a mutator roulette (each mission gets its own modifiers), chaos events at the start of your turns,
/// a golden enemy that drops a prize, and regional edicts (the strongest enemy of a region's missions issues the
/// region's edict). What each mission gets is decided here from the seed (and written to the spoiler log); only the
/// targets of chaos events are picked while you play.
///
/// Procedural combat missions have no script of their own; they get one (Lua/Missions/swh2_rnd_&lt;mission&gt;.lua),
/// so each of them can have its own mutators. Missions sharing a script share its setup.
/// </summary>
public static class MissionChaos
{
    const string Marker = "-- ==== SWH2 Randomizer: mission chaos ====";
    const string Module = "swh2_randomizer";
    const string ModifiersFile = "Definitions/modifiers.xml";
    const string EdictsFile = "Definitions/edicts.xml";
    const string MapFile = "WorldMap/Maps/WorldMap.tmx";
    const string Prefix = "swh2_";
    static readonly string[] Factions = ["navy", "pirates", "atomics"];
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A mutator: modifier body, who gets it, whether Balanced may roll it, its texts.</summary>
    sealed record Mutator(string Id, string Target, bool Mild, string Icon, string Body, string En, string Ru, string EnText, string RuText)
    {
        public string Modifier => $"{Prefix}mut_{Id}";
    }

    const string Death = @"<Events><Event On=""Death"" Handler=""Explode""><Damage><Amount>2</Amount><DamageRadius>400</DamageRadius><HitForce Constant=""4000"" DamageFactor=""20""/></Damage><Effect>explosion_medium</Effect></Event></Events>";

    static readonly Mutator[] Mutators =
    [
        new("heavy_hitters", "all", true, "edict_deadly", "<Stats><Damage><Amount>1</Amount></Damage></Stats>",
            "Heavy hitters", "Тяжёлая рука", "Mutator: everyone deals +1 damage!", "Мутатор: все наносят +1 урона!"),
        new("thick_plating", "enemies", true, "edict_chunky", "<Stats><HitPoints>2</HitPoints></Stats>",
            "Thick plating", "Толстая обшивка", "Mutator: enemies have +2 health!", "Мутатор: у врагов +2 здоровья!"),
        new("armor_up", "enemies", true, "edict_armored", "<Stats><Armor>1</Armor></Stats>",
            "Armored", "Броня", "Mutator: enemies have +1 armor!", "Мутатор: у врагов +1 брони!"),
        new("sprinters", "all", true, "modifier_momentum", "<Stats><MoveDistance>1</MoveDistance></Stats>",
            "Sprinters", "Спринтеры", "Mutator: everyone moves further!", "Мутатор: все бегают дальше!"),
        new("slowpokes", "all", false, "modifier_hobbled", "<Stats><MoveDistance>-1</MoveDistance></Stats>",
            "Slowpokes", "Тихоходы", "Mutator: everyone moves less!", "Мутатор: все ходят меньше!"),
        new("no_crits", "all", true, "modifier_broken", "<StatusFlags>disable_critical_hits</StatusFlags>",
            "No crits", "Без критов", "Mutator: no critical hits!", "Мутатор: никаких критов!"),
        new("slippery", "enemies", false, "modifier_greased_up", "<Stats><EvadeChance>0.2</EvadeChance></Stats>",
            "Slippery", "Скользкие", "Mutator: enemies dodge 20% of shots!", "Мутатор: враги уворачиваются от 20% выстрелов!"),
        new("blast_shields", "all", true, "modifier_abs_of_steel", "<StatusFlags>blast_shield</StatusFlags>",
            "Blast shields", "Противовзрывные щиты", "Mutator: splash deals at most 1 damage!", "Мутатор: взрывы бьют не больше чем на 1!"),
        new("double_tap", "crew", false, "modifier_amped_up", "<Stats><ActionTags>primary_weapon</ActionTags><Salvos>1</Salvos></Stats>",
            "Double tap", "Двойной выстрел", "Mutator: your guns fire twice!", "Мутатор: ваше оружие стреляет дважды!"),
        new("enemy_double_tap", "enemies", false, "modifier_rage", "<Stats><ActionTags>primary_weapon</ActionTags><Salvos>1</Salvos></Stats>",
            "Trigger happy", "Нервный курок", "Mutator: enemy guns fire twice!", "Мутатор: враги стреляют дважды!"),
        new("piercing", "all", true, "modifier_focus", "<Stats><ShootDamage><Pierce>1</Pierce></ShootDamage></Stats>",
            "Piercing rounds", "Бронебойные пули", "Mutator: every shot pierces one target!", "Мутатор: каждый выстрел пробивает цель насквозь!"),
        new("big_booms", "all", true, "modifier_burning", "<Stats><AoERange>1</AoERange></Stats>",
            "Big booms", "Большие бабахи", "Mutator: bigger explosions!", "Мутатор: взрывы больше!"),
        new("quick_cooldowns", "crew", true, "modifier_focus_upgraded", "<Stats><CooldownReduction>1</CooldownReduction></Stats>",
            "Quick recharge", "Быстрая перезарядка", "Mutator: abilities recharge faster!", "Мутатор: способности перезаряжаются быстрее!"),
        new("extra_items", "crew", true, "modifier_morale_boost", "<Stats><ItemUsage>1</ItemUsage></Stats>",
            "Deep pockets", "Глубокие карманы", "Mutator: +1 use of every utility!", "Мутатор: +1 использование каждой утилиты!"),
        new("stubborn", "enemies", false, "modifier_payback", "<StatusFlags>resist_death</StatusFlags>",
            "Stubborn", "Упрямые", "Mutator: enemies survive one deadly hit!", "Мутатор: враги переживают смертельный удар!"),
        new("death_blasts", "all", false, "modifier_burning", Death,
            "Volatile", "Взрывоопасные", "Mutator: bots explode when scrapped!", "Мутатор: боты взрываются при гибели!"),
        new("armor_piercing", "crew", true, "modifier_warcry", "<Stats><Flags>PierceArmor</Flags></Stats>",
            "Armor piercing", "Пробитие брони", "Mutator: your attacks ignore armor!", "Мутатор: ваши атаки игнорируют броню!"),
        new("tough_crew", "crew", true, "edict_chunky", "<Stats><HitPoints>2</HitPoints></Stats>",
            "Tough crew", "Крепкая команда", "Mutator: your crew has +2 health!", "Мутатор: у команды +2 здоровья!"),
    ];

    /// <summary>Chaos event modifiers (applied for a round by the Lua hook) and texts of the other events.</summary>
    static readonly (string Id, string Body, string En, string Ru)[] Events =
    [
        ("adrenaline", "<RoundsActive>1</RoundsActive><HealthBarIcon>Textures/UI/HealthBar/modifier_amped_up</HealthBarIcon><StatusFlags>spin_up</StatusFlags>",
            "Adrenaline! Attacks don't end the turn", "Адреналин! Атака не завершает ход"),
        ("shield", "<RoundsActive>1</RoundsActive><HealthBarIcon>Textures/UI/HealthBar/edict_vectron_shield</HealthBarIcon><StatusFlags>invulnerable</StatusFlags>",
            "Lucky charm! Invulnerable this round", "Талисман! Неуязвим до следующего хода"),
        ("stun", "<RoundsActive>2</RoundsActive><HealthBarIcon>Textures/UI/HealthBar/modifier_stun</HealthBarIcon><StatusFlags>disabled</StatusFlags>",
            "Short circuit! Stunned", "Короткое замыкание! Оглушён"),
        ("rooted", "<RoundsActive>2</RoundsActive><HealthBarIcon>Textures/UI/HealthBar/modifier_hobbled</HealthBarIcon><StatusFlags>movement_disabled</StatusFlags>",
            "Rusted joints! Can't move", "Заржавели суставы! Не может ходить"),
        ("overcharge", "<RoundsActive>1</RoundsActive><HealthBarIcon>Textures/UI/HealthBar/edict_deadly</HealthBarIcon><Stats><Damage><Amount>2</Amount></Damage></Stats>",
            "Overcharge! +2 damage this turn", "Перегрузка! +2 урона в этот ход"),
        ("bomb", "<HealthBarIcon>Textures/UI/HealthBar/modifier_burning</HealthBarIcon>" + Death,
            "Ticking... explodes when scrapped!", "Тикает... взорвётся при гибели!"),
        ("heal", "", "Spare parts! Fully repaired", "Запчасти! Полностью починен"),
        ("mutiny", "", "Mutiny! Switches sides", "Бунт! Переходит на другую сторону"),
        ("swap", "", "Teleporter glitch! Swapped places", "Сбой телепорта! Поменялись местами"),
        ("alarm", "", "Somebody tripped the alarm!", "Кто-то включил тревогу!"),
    ];

    static readonly string[] MildEdicts = ["hitpoints", "damage", "armored_single", "shield_single"];
    static readonly string[] AllEdicts = ["hitpoints", "damage", "armored_single", "shield_single", "armored", "shield_all"];
    static readonly Dictionary<string, (string En, string Ru)> EdictTexts = new()
    {
        ["hitpoints"] = ("[e]All[] enemies have more [e]Health[].", "[e]Все[] враги получают больше [e]здоровья[]."),
        ["damage"] = ("[e]All[] enemies deal more [e]Damage[].", "[e]Все[] враги наносят больше [e]урона[]."),
        ["armored_single"] = ("Each turn, one enemy gets [e]+1 Armor[].", "Каждый ход один враг получает [e]+1 брони[]."),
        ["shield_single"] = ("Each turn, one enemy becomes [e]Immune[].", "Каждый ход один враг становится [e]неуязвимым[]."),
        ["armored"] = ("[e]All[] enemies have [e]+1 Armor[].", "[e]Все[] враги получают [e]+1 брони[]."),
        ["shield_all"] = ("[e]All[] enemies are shielded [e]every other turn[].", "[e]Все[] враги под щитом [e]через ход[]."),
    };

    /// <summary>What a mission script gets.</summary>
    sealed class Setup
    {
        public List<Mutator> Mutators = [];
        public string? Golden;
        public string? Edict;
        public string? Region;
    }

    public static void Apply(RandomizerContext ctx, ModContext mod)
    {
        var o = ctx.Options;
        var c = ctx.Content;
        if (c.Exists($"Lua/Mod/{Module}.lua") || c.Xml(ModifiersFile).Definitions("Modifier").Any(m => m.Name().StartsWith(Prefix + "mut_", StringComparison.Ordinal)))
            throw new InvalidOperationException("The game files already contain the randomizer's mission chaos (built from a modded file?)");

        var scripts = CombatScripts(ctx);
        var regions = o.RegionEdicts ? MissionRegions(c) : new Dictionary<string, string>(StringComparer.Ordinal);
        var setups = new SortedDictionary<string, Setup>(StringComparer.Ordinal);
        foreach (var s in scripts.Keys) setups[s] = new Setup();

        if (o.MutatorRoulette) RollMutators(ctx, setups);
        if (o.GoldenEnemy) RollGolden(ctx, setups, scripts);
        var edicts = o.RegionEdicts ? RollEdicts(ctx, setups, scripts, regions) : [];

        AddModifiers(c, edicts);
        AddEdicts(c, edicts);
        AddStrings(mod, edicts);
        mod.Lua.AddGlobalModule(Module, GlobalModule);
        int hooked = mod.Lua.AppendToMissionScripts(script => setups.TryGetValue(script, out var s) ? Hook(script, s, o.ChaosEvents ? o.ChaosEventChance : 0) : null);

        ctx.Spoiler.Section("Mission chaos");
        if (o.ChaosEvents) ctx.Spoiler.Line($"Chaos events: {o.ChaosEventChance}% chance at the start of each of your turns (from the second one).");
        foreach (var (script, s) in setups)
        {
            var parts = new List<string>();
            if (s.Mutators.Count > 0) parts.Add("mutators: " + string.Join(", ", s.Mutators.Select(m => m.En)));
            if (s.Golden != null) parts.Add("golden enemy drops " + ctx.Items.Display(s.Golden));
            if (s.Edict != null) parts.Add($"edict of {s.Region}: {s.Edict}");
            if (parts.Count > 0) ctx.Spoiler.Line($"{string.Join(", ", scripts[script])}: {string.Join("; ", parts)}");
        }
        ctx.Log($"Mission chaos: {hooked} mission scripts hooked, {setups.Values.Count(s => s.Mutators.Count > 0)} with mutators, " +
                $"{setups.Values.Count(s => s.Golden != null)} with a golden enemy, {setups.Values.Count(s => s.Edict != null)} with a regional edict.");
    }

    // ---- which missions -------------------------------------------------------------------------------------------

    /// <summary>
    /// Combat mission scripts (file name without extension) and the missions using them. Procedural combat missions
    /// without a script get their own one. The tutorial is left alone.
    /// </summary>
    static Dictionary<string, List<string>> CombatScripts(RandomizerContext ctx)
    {
        var c = ctx.Content;
        var defs = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var f in c.Find("Definitions", "missions*.de"))
            foreach (var m in c.Xml(f).Definitions("Mission"))
                defs.TryAdd(m.Name(), m);
        string? Resolve(string name, string field)
        {
            for (int i = 0; i < 20 && defs.TryGetValue(name, out var m); i++)
            {
                if (m.Element(field)?.Value.Trim() is { Length: > 0 } v) return v;
                name = m.Attribute("Template")?.Value ?? "";
            }
            return null;
        }

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var nonCombat = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (var (name, m) in defs.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            if (m.IsAbstract()) continue;
            bool combat = Resolve(name, "StartingMode") == "Combat" && !name.Contains("tutorial");
            var script = Resolve(name, "Script");
            if (script == null)
            {
                if (!combat) continue;
                var file = $"{Prefix}rnd_{name}";
                c.WriteText($"Lua/Missions/{file}.lua", $"-- SWH2 Randomizer: script of the procedural mission {name} (it has none of its own).\r\n");
                if (m.Element("Script") is { } own) own.Value = $@"Lua\Missions\{file}.lua";
                else m.Add(new XElement("Script", $@"Lua\Missions\{file}.lua"));
                script = file;
                added++;
            }
            else script = Regex.Replace(script, @"^.*[\\/]|\.lua$", "");
            // A mission may name a script the game doesn't ship (old_heist): nothing to hook there.
            if (!c.Exists($"Lua/Missions/{script}.lua")) continue;
            if (!combat) { nonCombat.Add(script); continue; }
            (result.TryGetValue(script, out var l) ? l : result[script] = []).Add(name);
        }
        // A script shared with a non-combat mission (bars, the ship) is left alone.
        foreach (var s in nonCombat) result.Remove(s);
        ctx.Log($"Mission chaos: {result.Count} combat mission scripts ({added} added for procedural missions).");
        return result;
    }

    /// <summary>Region of each mission on the world map: the Region polygon (WorldMap.tmx) its marker stands in.</summary>
    static Dictionary<string, string> MissionRegions(GameContent c)
    {
        var map = XDocument.Parse(c.ReadText(MapFile));
        static string? Prop(XElement o, string name) =>
            o.Element("properties")?.Elements("property").FirstOrDefault(p => p.Attribute("name")?.Value == name)?.Attribute("value")?.Value;
        static double D(XElement o, string a) => double.TryParse(o.Attribute(a)?.Value, NumberStyles.Float, Inv, out var v) ? v : 0;

        var polygons = new List<(string Region, List<(double X, double Y)> Points)>();
        foreach (var o in map.Descendants("object").Where(o => o.Attribute("type")?.Value == "Region"))
            if (Prop(o, "WorldMapRegion") is { Length: > 0 } region && o.Element("polygon")?.Attribute("points")?.Value is { } pts)
            {
                double x = D(o, "x"), y = D(o, "y");
                polygons.Add((region, pts.Split(' ').Select(p => p.Split(','))
                    .Select(p => (x + double.Parse(p[0], Inv), y + double.Parse(p[1], Inv))).ToList()));
            }

        static bool Inside(List<(double X, double Y)> poly, double x, double y)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                if ((poly[i].Y > y) != (poly[j].Y > y) &&
                    x < (poly[j].X - poly[i].X) * (y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                    inside = !inside;
            return inside;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var o in map.Descendants("object"))
            if (Prop(o, "Mission") is { Length: > 0 } mission && !result.ContainsKey(mission))
            {
                double x = D(o, "x"), y = D(o, "y");
                if (polygons.FirstOrDefault(p => Inside(p.Points, x, y)) is { Region: { } r }) result[mission] = r;
            }
        return result;
    }

    // ---- rolls ----------------------------------------------------------------------------------------------------

    static void RollMutators(RandomizerContext ctx, SortedDictionary<string, Setup> setups)
    {
        var rng = ctx.RngFor("mutators");
        bool balanced = ctx.Options.MutatorMode == Mode.Balanced;
        var pool = Mutators.Where(m => !balanced || m.Mild).ToList();
        foreach (var s in setups.Values)
        {
            // Balanced: half of the missions get one mild mutator. Chaos: every mission gets one to three.
            int count = balanced ? (rng.NextDouble() < 0.5 ? 1 : 0) : 1 + rng.Next(3);
            var options = pool.ToList();
            for (int i = 0; i < count && options.Count > 0; i++)
            {
                var m = rng.Pick(options);
                s.Mutators.Add(m);
                options.Remove(m);
                // Opposites cancel out: never roll both.
                options.RemoveAll(x => (m.Id, x.Id) is ("sprinters", "slowpokes") or ("slowpokes", "sprinters"));
            }
        }
    }

    static void RollGolden(RandomizerContext ctx, SortedDictionary<string, Setup> setups, Dictionary<string, List<string>> scripts)
    {
        var c = ctx.Content;
        var rng = ctx.RngFor("golden");
        bool balanced = ctx.Options.GoldenMode == Mode.Balanced;
        var prizes = ctx.Items.All
            .Where(i => !i.Abstract && i.Tier is > 0 && i.RarityType is "common" or "rare" &&
                        (i.Kind == "Utility" || (i.Kind == "Weapon" && i.File == "Definitions/weapons.xml")))
            .OrderBy(i => i.Name, StringComparer.Ordinal).ToList();

        // Tier of a mission: its master loot, else its sea (the mission file).
        var tiers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in c.Find("Definitions", "missions*.de"))
        {
            int sea = f.Contains("west_caribbea") ? 2 : f.Contains("east_caribbea") ? 3 : f.Contains("arctica") ? 4 : f.Contains("atomics") ? 6 : 3;
            foreach (var m in c.Xml(f).Definitions("Mission"))
            {
                var loot = m.Element("MasterLoot")?.Elements("Item").Select(i => i.Attribute("Definition")?.Value).FirstOrDefault(d => d != null);
                tiers[m.Name()] = loot != null && ctx.Items.Get(loot)?.Tier is { } t and > 0 ? t : sea;
            }
        }

        foreach (var (script, s) in setups)
        {
            if (balanced && rng.NextDouble() >= 0.5) continue;
            int tier = scripts[script].Select(m => tiers.GetValueOrDefault(m, 3)).Max();
            var fits = balanced ? prizes.Where(p => Math.Abs(p.Tier!.Value - tier) <= 1 && p.RarityType == "rare").ToList() : prizes;
            if (fits.Count == 0) fits = prizes;
            s.Golden = rng.Pick(fits).Name;
        }
    }

    static List<string> RollEdicts(RandomizerContext ctx, SortedDictionary<string, Setup> setups, Dictionary<string, List<string>> scripts, Dictionary<string, string> regions)
    {
        var rng = ctx.RngFor("edicts");
        bool balanced = ctx.Options.EdictMode == Mode.Balanced;
        var pool = balanced ? MildEdicts : AllEdicts;
        var byRegion = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var region in regions.Values.Distinct().OrderBy(r => r, StringComparer.Ordinal))
            byRegion[region] = !balanced || rng.NextDouble() < 0.5 ? rng.Pick(pool) : null;

        ctx.Spoiler.Section($"Regional edicts ({ctx.Options.EdictMode})");
        foreach (var (region, edict) in byRegion)
            ctx.Spoiler.Line($"{region}: {edict ?? "none"}");
        foreach (var (script, s) in setups)
        {
            var region = scripts[script].Select(m => regions.GetValueOrDefault(m)).FirstOrDefault(r => r != null);
            if (region != null && byRegion[region] is { } e) (s.Region, s.Edict) = (region, e);
        }
        return setups.Values.Select(s => s.Edict).Where(e => e != null).Select(e => e!).Distinct().OrderBy(e => e, StringComparer.Ordinal).ToList();
    }

    // ---- game data ------------------------------------------------------------------------------------------------

    static void AddModifiers(GameContent c, List<string> edicts)
    {
        var root = c.Xml(ModifiersFile).Root;
        XElement Parse(string name, string body) => XElement.Parse($"<Modifier Name=\"{name}\">{body}</Modifier>");
        foreach (var m in Mutators)
            root.Add(Parse(m.Modifier, $"<LocalizedNameId>{m.Modifier}</LocalizedNameId><HealthBarIcon>Textures/UI/HealthBar/{m.Icon}</HealthBarIcon>{m.Body}"));
        foreach (var (id, body, _, _) in Events.Where(e => e.Body.Length > 0))
            root.Add(Parse($"{Prefix}evt_{id}", $"<LocalizedNameId>{Prefix}evt_{id}</LocalizedNameId>{body}"));
        root.Add(Parse($"{Prefix}golden",
            "<LocalizedNameId>swh2_golden</LocalizedNameId><HealthBarIcon>Textures/UI/HealthBar/edict_geared_up</HealthBarIcon>" +
            "<ApplyEffect>edict_buffed</ApplyEffect><WorldTextDefinition>worldtext_modifier_positive</WorldTextDefinition>" +
            "<Stats><HitPoints>3</HitPoints><Armor>1</Armor></Stats>"));
        // Like the navy commanders' issue_edict_passive_*: the edict is active while the issuer is alert and alive.
        foreach (var e in edicts)
            foreach (var f in Factions)
                root.Add(Parse($"{Prefix}issue_{e}_{f}",
                    $"<Events><Event On=\"Alerted\" Handler=\"IssueEdict\"><Edict>{Prefix}{e}_{f}</Edict></Event>" +
                    "<Event On=\"Removed\" Handler=\"RemoveEdict\" /><Event On=\"Death\" Handler=\"RemoveEdict\" /></Events>"));
    }

    /// <summary>Copies of the edicts for each enemy faction (the originals belong to the navy or the atomics).</summary>
    static void AddEdicts(GameContent c, List<string> edicts)
    {
        var root = c.Xml(EdictsFile).Root;
        foreach (var e in edicts)
            foreach (var f in Factions)
                root.Add(new XElement("Edict", new XAttribute("Name", $"{Prefix}{e}_{f}"), new XAttribute("Template", e),
                    new XElement("Faction", f),
                    new XElement("NameStringId", $"edict_{e}"),
                    new XElement("DescriptionStringId", $"{Prefix}edict_{e}_description")));
    }

    static void AddStrings(ModContext mod, List<string> edicts)
    {
        var en = new Dictionary<string, string>();
        var ru = new Dictionary<string, string>();
        foreach (var m in Mutators)
        {
            en[m.Modifier] = m.En; ru[m.Modifier] = m.Ru;
            en[m.Modifier + "_text"] = m.EnText; ru[m.Modifier + "_text"] = m.RuText;
        }
        foreach (var (id, _, enText, ruText) in Events)
        {
            en[$"{Prefix}evt_{id}"] = enText; ru[$"{Prefix}evt_{id}"] = ruText;
        }
        en["swh2_golden"] = "Golden"; ru["swh2_golden"] = "Золотой";
        en["swh2_golden_text"] = "A golden enemy! Scrap it for a prize."; ru["swh2_golden_text"] = "Золотой враг! Уничтожьте его ради приза.";
        en["swh2_golden_loot"] = "The golden enemy dropped a prize!"; ru["swh2_golden_loot"] = "Золотой враг оставил приз!";
        foreach (var e in edicts)
        {
            en[$"{Prefix}edict_{e}_description"] = EdictTexts[e].En;
            ru[$"{Prefix}edict_{e}_description"] = EdictTexts[e].Ru;
        }
        mod.Lua.AddStrings(en, new Dictionary<string, IReadOnlyDictionary<string, string>> { ["ru"] = ru });
    }

    // ---- Lua ------------------------------------------------------------------------------------------------------

    static string Lua(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    static string Hook(string script, Setup s, int eventChance)
    {
        if (s.Mutators.Count == 0 && s.Golden == null && s.Edict == null && eventChance == 0) return "";
        var mutators = "{ " + string.Join(", ", s.Mutators.Select(m => $"{{ {Lua(m.Modifier)}, {Lua(m.Target)} }}")) + " }";
        return HookTemplate
            .Replace("{SCRIPT}", script)
            .Replace("{MUTATORS}", mutators)
            .Replace("{EVENTS}", eventChance.ToString(Inv))
            .Replace("{GOLDEN}", s.Golden != null ? Lua(s.Golden) : "nil")
            .Replace("{EDICT}", s.Edict != null ? Lua(s.Edict) : "nil");
    }

    /// <summary>Global environment: pcall, math.random and a log for the sandboxed hooks.</summary>
    const string GlobalModule = @"-- SWH2 Randomizer mission chaos backend: runs in the GLOBAL Lua environment (loaded from system.lua).
-- Mission hooks (sandboxed: no pcall/io/os/math.random) call into this table.
SWH2Rnd = {}
local R = SWH2Rnd

-- File in the data folder of this copy of the game: SWH2_INSTANCE_DIR (""instanceN\"") is set by the Coop dll for the
-- 2nd+ copy running on one PC. Read on every use, in case it is set after this file is loaded.
local function file(name)
	local getenv = os and os.getenv
	return ((getenv and getenv(""APPDATA"")) or """") .. ""\\Thunderful\\SteamWorld Heist II\\"" ..
		((getenv and getenv(""SWH2_INSTANCE_DIR"")) or """") .. name
end
local LOG = ""swh2_randomizer.log""
math.randomseed(os.time() + math.floor((os.clock() * 1000) % 100000))

function R.log(msg)
	local f = io.open(file(LOG), ""a"")
	if f then f:write(os.date(""%Y-%m-%d %H:%M:%S""), ""  "", tostring(msg), ""\n""); f:close() end
end

function R.safe(where, fn, ...)
	local ok, err = pcall(fn, ...)
	if not ok then R.log(""ERROR in "" .. tostring(where) .. "": "" .. tostring(err)) end
	return ok
end

function R.random(n) return math.random(n) end

function R.pick(t)
	if t and #t > 0 then return t[math.random(#t)] end
end

function R.filter(t, f)
	local r = {}
	for _, v in ipairs(t or {}) do if f(v) then r[#r + 1] = v end end
	return r
end

R.factions = { navy = true, pirates = true, atomics = true }

-- Bosses and machines are never golden, commanders, mutineers or swapped.
local SPECIAL = { ""boss"", ""kraken"", ""mimic"", ""turret"", ""totem"", ""sphere"", ""mech"", ""fabio"", ""drone"", ""bomb"",
	""prison"", ""keyholder"", ""medal"", ""big_steve"", ""heist"", ""tutorial"", ""deployer"", ""eye"" }

function R.ordinary(definition)
	local d = string.lower(tostring(definition or """"))
	if d == """" then return false end
	for _, p in ipairs(SPECIAL) do
		if string.find(d, p, 1, true) then return false end
	end
	return true
end

sandbox(""SWH2Rnd"")
";

    /// <summary>Appended to mission scripts (sandbox). Always calls the script's own callbacks.</summary>
    const string HookTemplate = Marker + @"
do
	local R = SWH2Rnd
	local SCRIPT = ""{SCRIPT}""
	local MUTATORS = {MUTATORS}
	local EVENT_CHANCE = {EVENTS}
	local GOLDEN_ITEM = {GOLDEN}
	local EDICT = {EDICT}
	local function run(fn, ...) if R and R.safe then return R.safe(SCRIPT, fn, ...) end end
	local setupIn, resumeIn, textIn, texts = nil, nil, nil, {}
	local golden, goldenDone, edictDone, turns = nil, false, false, 0

	local function crew() return GetFriendlyActors() or {} end
	local function enemies() return GetEnemyActors() or {} end
	local function ordinary(e) return R.ordinary(GetEntityDefinition(e)) end
	local function say(e, key) if e then SpawnWorldText(e, ""worldtext_speak_important"", key) end end

	local function give(actors, modifier)
		for _, a in ipairs(actors) do
			if not HasModifier(a, modifier) then AddModifier(a, modifier) end
		end
	end

	-- Also catches reinforcements: called again at the start of every turn.
	local function applyMutators()
		for _, m in ipairs(MUTATORS) do
			if m[2] ~= ""enemies"" then give(crew(), m[1]) end
			if m[2] ~= ""crew"" then give(enemies(), m[1]) end
		end
	end

	local function pickGolden()
		if not GOLDEN_ITEM or golden or goldenDone then return end
		golden = R.pick(R.filter(enemies(), ordinary))
		if golden then
			AddModifier(golden, ""swh2_golden"")
			texts[#texts + 1] = { golden, ""swh2_golden_text"" }
		end
	end

	-- The strongest ordinary enemy issues the region's edict once it's alert.
	local function pickCommander()
		if not EDICT or edictDone then return end
		local boss, hp = nil, -1
		for _, e in ipairs(enemies()) do
			if ordinary(e) then
				local h = GetActorMaxHealth(e) or 0
				if h > hp then boss, hp = e, h end
			end
		end
		if not boss then return end
		edictDone = true
		local faction = tostring(GetActorFaction(boss))
		if R.factions[faction] then AddModifier(boss, ""swh2_issue_"" .. EDICT .. ""_"" .. faction) end
	end

	local function setup()
		applyMutators()
		local first = crew()[1]
		for _, m in ipairs(MUTATORS) do texts[#texts + 1] = { first, m[1] .. ""_text"" } end
		pickGolden()
		pickCommander()
		textIn = 30
	end

	local function resume()
		applyMutators()
		edictDone = true
		for _, e in ipairs(enemies()) do
			if HasModifier(e, ""swh2_golden"") then golden = e end
		end
		if GOLDEN_ITEM and not golden then goldenDone = true end
	end

	-- Chaos events: each returns who to show the text over and the text, or nil when it can't happen now.
	local function otherFaction(f)
		local options = {}
		for name in pairs(R.factions) do if name ~= f then options[#options + 1] = name end end
		table.sort(options)
		return R.pick(options)
	end
	local EVENTS = {
		function() local a = R.pick(crew()); if a then AddModifier(a, ""swh2_evt_adrenaline""); return a, ""swh2_evt_adrenaline"" end end,
		function() local a = R.pick(crew()); if a then AddModifier(a, ""swh2_evt_shield""); return a, ""swh2_evt_shield"" end end,
		function() local e = R.pick(R.filter(enemies(), ordinary)); if e then AddModifier(e, ""swh2_evt_stun""); return e, ""swh2_evt_stun"" end end,
		function() local e = R.pick(enemies()); if e then AddModifier(e, ""swh2_evt_rooted""); return e, ""swh2_evt_rooted"" end end,
		function() local a = R.pick(crew()); if a then AddModifier(a, ""swh2_evt_overcharge""); return a, ""swh2_evt_overcharge"" end end,
		function() local e = R.pick(R.filter(enemies(), ordinary)); if e then AddModifier(e, ""swh2_evt_bomb""); return e, ""swh2_evt_bomb"" end end,
		function()
			local hurt = R.filter(crew(), function(a) return (GetActorHealth(a) or 0) < (GetActorMaxHealth(a) or 0) end)
			local a = R.pick(hurt)
			if a then SetActorHealth(a, GetActorMaxHealth(a)); return a, ""swh2_evt_heal"" end
		end,
		function()
			local e = R.pick(R.filter(enemies(), ordinary))
			if not e then return end
			local to = otherFaction(tostring(GetActorFaction(e)))
			if to then SetActorFaction(e, to); return e, ""swh2_evt_mutiny"" end
		end,
		function()
			local a, e = R.pick(crew()), R.pick(R.filter(GetAlertActors() or {}, function(x) return IsActorHostile(x) and ordinary(x) end))
			if not a or not e then return end
			local pa, pe = GetEntityPosition(a), GetEntityPosition(e)
			SetEntityPosition(a, pe, false)
			SetEntityPosition(e, pa, false)
			return a, ""swh2_evt_swap""
		end,
		function() local a = crew()[1]; if a then SetAlarmActive(true); return a, ""swh2_evt_alarm"" end end,
	}

	local function chaosEvent()
		for _ = 1, 3 do
			local who, key = EVENTS[R.random(#EVENTS)]()
			if who then say(who, key); return end
		end
	end

	local origDeployed = OnCrewDeployed
	function OnCrewDeployed(...)
		setupIn = 15
		if origDeployed then return origDeployed(...) end
	end

	local origResume = OnMissionResume
	function OnMissionResume(...)
		resumeIn = 15
		if origResume then return origResume(...) end
	end

	local origTurn = OnFactionTurnBegin
	function OnFactionTurnBegin(faction, ...)
		run(function()
			applyMutators()
			pickGolden()
			pickCommander()
			if tostring(faction) == tostring(GetPlayerFaction()) then
				turns = turns + 1
				if EVENT_CHANCE > 0 and turns > 1 and R.random(100) <= EVENT_CHANCE then chaosEvent() end
			end
		end)
		if origTurn then return origTurn(faction, ...) end
	end

	local origKilled = OnEntityKilled
	function OnEntityKilled(entity, ...)
		if golden and not goldenDone and entity == golden then
			run(function()
				goldenDone = true
				GiveItem(GOLDEN_ITEM, true)
				say(crew()[1], ""swh2_golden_loot"")
				R.log(""golden enemy dropped "" .. GOLDEN_ITEM .. "" ("" .. SCRIPT .. "")"")
			end)
		end
		if origKilled then return origKilled(entity, ...) end
	end

	local origUpdate = OnUpdate
	function OnUpdate(...)
		if setupIn then
			setupIn = setupIn - 1
			if setupIn <= 0 then setupIn = nil; run(setup) end
		end
		if resumeIn then
			resumeIn = resumeIn - 1
			if resumeIn <= 0 then resumeIn = nil; run(resume) end
		end
		if textIn then
			textIn = textIn - 1
			if textIn <= 0 then
				textIn = nil
				local t = table.remove(texts, 1)
				if t then
					run(function() say(t[1] or crew()[1], t[2]) end)
					textIn = 150
				end
			end
		end
		if origUpdate then return origUpdate(...) end
	end
end";
}
