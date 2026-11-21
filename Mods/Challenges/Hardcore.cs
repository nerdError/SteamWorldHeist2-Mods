using System.Text;
using System.Xml.Linq;
using SWH2.Core;
using SWH2.Modding;

namespace SWH2.Mods.Challenges;

/// <summary>
/// Hardcore runs, both stored in the save through hidden quests:
/// - Permadeath: a scrapped crewmate completes quest swh2_scrapped_&lt;persona&gt;. Brought on a later mission,
///   that bot is scrapped right after deployment. The Captain is exempt (the story needs them).
/// - One life: losing a combat mission ends the run. There is no defeat callback, so a fresh mission start marks
///   the run "in_mission" and OnMissionVictory marks it "won"; a run still "in_mission" at the next mission start
///   lost (defeat, abort, quit). Then quest swh2_hardcore_lost unlocks swh2_hardcore_game_over, which locks every
///   combat mission for good. The run state lives in a file next to the saves, keyed by a run id that is written
///   into the save as completed quests swh2_run_bit_N (quests can only be completed, never reset, so they can't
///   hold the state themselves).
/// </summary>
public static class Hardcore
{
    const string Marker = "-- ==== SWH2 Challenges: hardcore ====";
    const string Module = "swh2_hardcore";
    const int IdBits = 20;
    const string QuestPrefix = "swh2_";

    // Scripts that leave a won mission without a victory (cutscene exits) would read as a loss.
    static readonly string[] NonVictoryExits = ["EndMissionInstant", "ExitMissionToWorldMap", "ExitMissionToHomeBase", "ChangeMission"];

    public static void Apply(ModContext ctx, MissionScripts missions, bool permadeath, bool oneLife)
    {
        ctx.Report.Section("Hardcore");
        var personas = ctx.Content.Xml("Definitions/personas.xml").Definitions("Persona").ToDictionary(p => p.Name());
        string? P(string name, Func<XElement, string?> get)
        {
            for (int guard = 0; guard < 20 && personas.TryGetValue(name, out var p); guard++)
            {
                if (get(p) is { } v) return v;
                name = p.Attribute("Template")?.Value ?? "";
            }
            return null;
        }
        // Recruitable crew only: the Captain is LoadoutUsage "captain", story-only bots aren't recruitable.
        var crew = personas.Values
            .Where(p => !p.IsAbstract() && P(p.Name(), e => e.Element("Recruitable")?.Value.Trim())?.ToLowerInvariant() is "true" or "1"
                && P(p.Name(), e => e.Element("LoadoutUsage")?.Value.Trim()) == "crew")
            .Select(p => (Name: p.Name(), Entity: P(p.Name(), e => e.Element("TacticsEntity")?.Value.Trim()) ?? ""))
            .OrderBy(p => p.Name, StringComparer.Ordinal).ToList();

        AddQuests(ctx, missions, crew.Select(c => c.Name), permadeath, oneLife);
        ctx.Lua.AddGlobalModule(Module, GlobalModule(crew));
        AddStrings(ctx);

        if (ctx.Lua.MissionScripts.Any(p => ctx.Content.ReadText(p).Contains(Marker)))
            throw new InvalidOperationException("Mission scripts already contain the hardcore hook (built from a modded file?)");
        var tracked = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        string Block(string script, string source)
        {
            bool combat = missions.Combat.Contains(script);
            bool track = oneLife && combat && !NonVictoryExits.Any(source.Contains);
            if (track) tracked.Add(script);
            return Hook.Replace("{SCRIPT}", script)
                .Replace("{COMBAT}", combat ? "true" : "false")
                .Replace("{TRACK}", track ? "true" : "false")
                .Replace("{PERMADEATH}", permadeath ? "true" : "false")
                .Replace("{BITS}", IdBits.ToString());
        }
        int n = ctx.Lua.AppendToMissionScripts(script =>
            Block(script, ctx.Content.ReadOriginal($"Lua/Missions/{script}.lua") is var b ? Encoding.UTF8.GetString(b) : ""));
        if (ctx.Content.Exists(MissionScripts.GenericPath))
        {
            MissionScripts.AppendToGeneric(ctx, Block(MissionScripts.GenericName, ""));
            n++;
        }

        if (permadeath)
            ctx.Report.Line($"Permadeath: {crew.Count} recruitable crewmates can be scrapped for good: {string.Join(", ", crew.Select(c => c.Name))}.");
        if (oneLife)
        {
            ctx.Report.Line($"One life: losing any of {tracked.Count} tracked combat mission scripts ends the run.");
            var skipped = missions.Combat.Where(s => !tracked.Contains(s)).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            if (skipped.Count > 0)
                ctx.Report.Line($"Not tracked (they leave the mission by script, not by a victory): {string.Join(", ", skipped)}.");
        }
        ctx.Log($"Hardcore: {n} mission scripts hooked{(permadeath ? ", permadeath" : "")}{(oneLife ? $", one life ({tracked.Count} tracked)" : "")}.");
    }

    static void AddQuests(ModContext ctx, MissionScripts missions, IEnumerable<string> crew, bool permadeath, bool oneLife)
    {
        var quests = ctx.Content.Xml("Definitions/quests.xml");
        var root = quests.Root;
        if (root.Elements("Quest").Any(q => q.Name().StartsWith(QuestPrefix)))
            throw new InvalidOperationException("quests.xml already contains hardcore quests (built from a modded file?)");

        // Hidden (no LocalizedStringId), never progress on their own — completed by the Lua hooks.
        XElement Hidden(string name) => new("Quest", new XAttribute("Name", name), new XAttribute("Template", "BASE"),
            new XElement("Objectives", new XElement("Objective", new XElement("Condition", "none"))));

        for (int i = 0; i < IdBits; i++) root.Add(Hidden($"{QuestPrefix}run_bit_{i}"));
        if (permadeath)
            foreach (var p in crew) root.Add(Hidden($"{QuestPrefix}scrapped_{p}"));
        if (oneLife)
        {
            root.Add(Hidden($"{QuestPrefix}hardcore_lost"));
            root.Add(new XElement("Quest", new XAttribute("Name", $"{QuestPrefix}hardcore_game_over"), new XAttribute("Template", "BASE"),
                new XElement("UnlockedBy", $"{QuestPrefix}hardcore_lost"),
                new XElement("Objectives", new XElement("Objective",
                    new XElement("LocalizedStringId", "swh2_hc_game_over_objective"),
                    new XElement("Condition", "none"))),
                new XElement("LockMissions", missions.CombatMissions.Select(m => new XElement("Mission", m)))));
        }
    }

    static void AddStrings(ModContext ctx) => ctx.Lua.AddStrings(new Dictionary<string, string>
    {
        ["swh2_hc_scrapped"] = "Scrapped for good!",
        ["swh2_hc_gone"] = "Already scrapped for good...",
        ["swh2_hc_game_over"] = "GAME OVER! A mission was lost - start a new game.",
        ["quest_swh2_hardcore_game_over"] = "Game over",
        ["swh2_hc_game_over_objective"] = "A mission was lost. This run is over - start a new game.",
    }, new Dictionary<string, IReadOnlyDictionary<string, string>>
    {
        ["ru"] = new Dictionary<string, string>
        {
            ["swh2_hc_scrapped"] = "Уничтожен навсегда!",
            ["swh2_hc_gone"] = "Этот бот уже уничтожен навсегда...",
            ["swh2_hc_game_over"] = "ИГРА ОКОНЧЕНА! Миссия проиграна - начните новую игру.",
            ["quest_swh2_hardcore_game_over"] = "Игра окончена",
            ["swh2_hc_game_over_objective"] = "Миссия проиграна. Это прохождение окончено - начните новую игру.",
        },
    });

    // ---- Lua -----------------------------------------------------------------------------------------------------

    /// <summary>Global environment: file I/O, random run ids and pcall for the sandboxed hooks.</summary>
    static string GlobalModule(IEnumerable<(string Name, string Entity)> crew)
    {
        var personas = string.Join(", ", crew.Select(c => $"[\"{c.Name}\"] = true"));
        var entities = string.Join(", ", crew.Where(c => c.Entity != "").Select(c => $"[\"{c.Entity}\"] = \"{c.Name}\""));
        return @"-- SWH2 Challenges hardcore backend: runs in the GLOBAL Lua environment (loaded from system.lua).
-- Mission hooks (sandboxed: no pcall/io/os) call into this table.
SWH2Hardcore = {}
local H = SWH2Hardcore

local DIR = ((os and os.getenv and os.getenv(""APPDATA"")) or """") .. ""\\Thunderful\\SteamWorld Heist II\\""
local RUNS = DIR .. ""swh2_hardcore_runs.txt""
local LOG = DIR .. ""swh2_hardcore.log""

H.personas = { " + personas + @" }
H.entities = { " + entities + @" }

function H.log(msg)
	local f = io.open(LOG, ""a"")
	if f then f:write(os.date(""%Y-%m-%d %H:%M:%S""), ""  "", tostring(msg), ""\n""); f:close() end
end

function H.safe(where, fn, ...)
	local ok, err = pcall(fn, ...)
	if not ok then H.log(""ERROR in "" .. tostring(where) .. "": "" .. tostring(err)) end
	return ok
end

-- Persona of a crewmate entity: crew entities are named after their persona; the definition is the fallback.
function H.persona(name, definition)
	if name and H.personas[name] then return name end
	return definition and H.entities[definition]
end

-- Run ids: a random number written into the save as completed quests (one per set bit).
function H.newId(bits)
	math.randomseed(os.time() + math.floor((os.clock() * 1000) % 100000))
	return math.random(1, 2 ^ bits - 1)
end

function H.hasBit(id, i) return math.floor(id / 2 ^ i) % 2 == 1 end

local function key(id) return string.format(""%d"", id) end

local function readRuns()
	local runs = {}
	local f = io.open(RUNS, ""r"")
	if not f then return runs end
	for line in f:lines() do
		local id, state = line:match(""^(%d+)%s+(%a[%w_]*)"")
		if id then runs[id] = state end
	end
	f:close()
	return runs
end

function H.state(id) return readRuns()[key(id)] end

function H.setState(id, state)
	local runs = readRuns()
	runs[key(id)] = state
	local f = io.open(RUNS, ""w"")
	if not f then H.log(""cannot write "" .. RUNS); return end
	for k, v in pairs(runs) do f:write(k, "" "", v, ""\n"") end
	f:close()
	H.log(""run "" .. key(id) .. "": "" .. state)
end

-- NB: never use io.popen/os.execute here: they flash a console window.
sandbox(""SWH2Hardcore"")
";
    }

    /// <summary>Appended to mission scripts (sandbox). Always calls the script's own callbacks.</summary>
    const string Hook = Marker + @"
do
	local H = SWH2Hardcore
	local script = ""{SCRIPT}""
	local COMBAT, TRACK, PERMADEATH, BITS = {COMBAT}, {TRACK}, {PERMADEATH}, {BITS}
	local function run(fn, ...) if H and H.safe then return H.safe(script, fn, ...) end end
	local resumed, startIn, purgeIn, exitIn, over = false, nil, nil, nil, false

	local function runId(create)
		local id, bit = 0, 1
		for i = 0, BITS - 1 do
			if IsQuestCompleted(""swh2_run_bit_"" .. i) then id = id + bit end
			bit = bit * 2
		end
		if id == 0 and create then
			id = H.newId(BITS)
			for i = 0, BITS - 1 do
				if H.hasBit(id, i) then CompleteQuest(""swh2_run_bit_"" .. i) end
			end
			H.log(""new run "" .. id .. "" ("" .. script .. "")"")
		end
		return id
	end

	local function personaOf(e) return H.persona(GetEntityName(e), GetEntityDefinition(e)) end
	local function say(e, key) if e then SpawnWorldText(e, ""worldtext_speak_important"", key) end end

	local function gameOver(id)
		over = true
		H.setState(id, ""lost"")
		if not IsQuestCompleted(""swh2_hardcore_lost"") then CompleteQuest(""swh2_hardcore_lost"") end
		local crew = GetFriendlyActors()
		say(crew and crew[1], ""swh2_hc_game_over"")
		exitIn = 180
	end

	-- A fresh start (not a resume from a mission save).
	local function onStart()
		local id = runId(true)
		if not TRACK then return end
		local state = H.state(id)
		if IsQuestCompleted(""swh2_hardcore_lost"") or state == ""in_mission"" or state == ""lost"" then
			gameOver(id)
		else
			H.setState(id, ""in_mission"")
		end
	end

	-- Crewmates scrapped for good are scrapped again right after deployment.
	local function purge()
		local crew = GetFriendlyActors()
		if not crew then return end
		for _, a in ipairs(crew) do
			local p = personaOf(a)
			if p and IsQuestCompleted(""swh2_scrapped_"" .. p) then
				say(a, ""swh2_hc_gone"")
				KillObject(a)
			end
		end
	end

	local origStart = OnMissionStart
	function OnMissionStart(...)
		startIn = 5
		if origStart then return origStart(...) end
	end

	local origResume = OnMissionResume
	function OnMissionResume(...)
		resumed = true
		if origResume then return origResume(...) end
	end

	local origDeployed = OnCrewDeployed
	function OnCrewDeployed(...)
		if PERMADEATH and COMBAT then purgeIn = 10 end
		if origDeployed then return origDeployed(...) end
	end

	local origKilled = OnEntityKilled
	function OnEntityKilled(entity, ...)
		if PERMADEATH then
			run(function()
				local p = personaOf(entity)
				if p and GetActorFaction(entity) == GetPlayerFaction() and not IsQuestCompleted(""swh2_scrapped_"" .. p) then
					CompleteQuest(""swh2_scrapped_"" .. p)
					say(entity, ""swh2_hc_scrapped"")
					H.log(""scrapped for good: "" .. p .. "" ("" .. script .. "")"")
				end
			end)
		end
		if origKilled then return origKilled(entity, ...) end
	end

	local origVictory = OnMissionVictory
	function OnMissionVictory(...)
		if TRACK and not over then
			run(function()
				local id = runId(false)
				if id ~= 0 then H.setState(id, ""won"") end
			end)
		end
		if origVictory then return origVictory(...) end
	end

	local origUpdate = OnUpdate
	function OnUpdate(...)
		if startIn then
			startIn = startIn - 1
			if startIn <= 0 then
				startIn = nil
				if not resumed then run(onStart) end
			end
		end
		if purgeIn and not over then
			purgeIn = purgeIn - 1
			if purgeIn <= 0 then purgeIn = nil; run(purge) end
		end
		if exitIn then
			exitIn = exitIn - 1
			if exitIn <= 0 then exitIn = nil; ExitMissionToWorldMap() end
		end
		if origUpdate then return origUpdate(...) end
	end
end";
}
