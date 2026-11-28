-- SWH2 Co-op backend: runs in the GLOBAL Lua environment (loaded from system.lua via require_global).
-- Engine functions are not visible here; the hook appended to mission scripts shows the labels and tells the
-- input lock dll whose device may play.
Coop = {}
local C = Coop

-- File in the data folder of this copy of the game: SWH2_INSTANCE_DIR ("instanceN\") is set by the Coop dll for the
-- 2nd+ copy running on one PC. Read on every use, in case it is set after this file is loaded.
local function file(name)
	local getenv = os and os.getenv
	return ((getenv and getenv("APPDATA")) or "") .. "\\Thunderful\\SteamWorld Heist II\\" ..
		((getenv and getenv("SWH2_INSTANCE_DIR")) or "") .. name
end
local OWNERS = "swh2_coop_owners.txt"
local LOG = "swh2_coop.log"
local INPUT = "swh2_coop_input.txt"      -- read by the input lock dll (Mods/Coop/swh2coop.dll)
local BLOCKED = "swh2_coop_blocked.txt"  -- written by it when it mutes a press
local NETMODE = "swh2_net_mode.txt"      -- netplay test (dll netplay.c): path of its log, absent = off

-- Filled in by the mod at install time.
C.players = {PLAYERS}
C.manual = { {MANUAL} }       -- persona -> player picked in the settings
C.order = { {ORDER} }         -- persona -> position in personas.xml (deal order for the rest)
C.entities = { {ENTITIES} }   -- tactics entity -> persona
C.labelTurn = {LABEL_TURN}
C.labelSelect = {LABEL_SELECT}
C.labelAlways = {LABEL_ALWAYS}  -- name tags over the crewmates all the time (C.netTags)

C.inputLock = {INPUT_LOCK}
C.devices = { {DEVICES} }     -- player -> input device ("kbm", "pad1".."pad4", "all")
C.netTest = {NET_TEST}        -- netplay test (developer): the dll's recorder is on

C.script = "?"
local owners = {}
local lastInput, inputAge, lastBlocked = nil, 0, nil

function C.log(msg)
	local f = io.open(file(LOG), "a")
	if f then f:write(os.date("%Y-%m-%d %H:%M:%S"), "  ", tostring(msg), "\n"); f:close() end
end

-- Mission sandboxes have no pcall: they call this instead. Errors are logged, never propagated.
function C.safe(where, fn, ...)
	local ok, err = pcall(fn, ...)
	if not ok then C.log("ERROR in " .. tostring(where) .. ": " .. tostring(err)) end
	return ok
end

-- Persona of a crewmate entity: crew entities are named after their persona; the definition is the fallback.
function C.persona(name, definition)
	if name and C.order[name] then return name end
	if definition then
		if C.entities[definition] then return C.entities[definition] end
		if string.sub(definition, 1, 12) == "crew_captain" then return "captain" end
	end
	return nil
end

-- In a network game (the dll reports a role, C.netStatus) the owners come from its split (C.netCrew).
local netOwners = {}
function C.owner(persona) return (C.netRole and netOwners[persona]) or owners[persona] end

local function save()
	local f = io.open(file(OWNERS), "w")
	if not f then return end
	f:write(C.script, "\n")
	for p, i in pairs(owners) do f:write(p, "=", i, "\n") end
	f:close()
end

-- Called once when a mission script loads (fresh start and resume).
function C.Enter(script)
	C.script = script
	owners = {}
	lastInput, inputAge = nil, 0
	local f = io.open(file(BLOCKED), "r")
	if f then lastBlocked = f:read("*l"); f:close() end
	C.netLog = nil
	C.netCleared = false   -- netplay status counters of this battle not removed yet
	C.netCrewList, C.netPersona, C.netRole = {}, {}, nil
	C.netEventSeq, C.focus, C.tags, C.netCam, C.moveSeq = nil, {}, {}, nil, nil   -- notifications / camera / name tags of this battle
	netOwners = {}
	if os and os.remove then os.remove(file("swh2_net_crew.txt")) end
	f = C.netTest and io.open(file(NETMODE), "r")
	if f then
		local path = f:read("*l")
		f:close()
		if path and path ~= "" then C.netLog = path end
	end
	C.netWrite("# mission script " .. tostring(script))
end

-- Netplay test: lines in the dll's log (record or replay), compared by the Coop "Netplay test…" window / swh2 nettest compare (NetTest.cs).
function C.netWrite(line)
	if not C.netLog then return end
	local f = io.open(C.netLog, "a")
	if f then f:write(line, "\n"); f:close() end
end

-- Netplay test, replay (= network guest): state correction. Before every recorded player command the dll asks for the
-- record's state just before it ("<n> <local ids by actor ordinal...>" in swh2_net_sync.txt, n = the n-th "state" line
-- of the record) and waits for n in swh2_net_synced.txt. Actors are matched by ordinal ("actor <id> o=<n>" lines; a
-- guest that joined inside the host's mission gets the host's whole table as one "ords <id> <id>..." line).
local SYNC, SYNCED, RECORD = "swh2_net_sync.txt", "swh2_net_synced.txt", "swh2_net_replay.log"
local rec, lastFix = nil, nil

local function loadRecord()
	rec = { states = {}, ord = {}, born = {} }
	local f = io.open(file(RECORD), "r")
	if not f then return end
	for line in f:lines() do
		if line:sub(1, 6) == "state " then rec.states[#rec.states + 1] = line:sub(7)
		else
			local id, o = line:match("^actor (%-?%d+) o=(%d+)")
			if id then rec.ord[tonumber(id)] = tonumber(o); rec.born[tonumber(id)] = #rec.states
			elseif line:sub(1, 5) == "ords " then
				local i = 0
				for v in line:sub(6):gmatch("%-?%d+") do rec.ord[tonumber(v)] = i; i = i + 1 end
			end
		end
	end
	f:close()
end

-- E = engine functions from the mission hook (not visible in this global module).
function C.netFix(E)
	if not C.netLog or not C.netLog:find("replayed", 1, true) then return end
	local f = io.open(file(SYNC), "r")
	if not f then return end
	local line = f:read("*l")
	f:close()
	if not line or line == lastFix then return end
	if E.IsActionsOngoing() then return end
	local nums = {}
	for v in line:gmatch("%-?%d+") do nums[#nums + 1] = tonumber(v) end
	local n = nums[1]
	if not n then return end
	-- A live guest's record grows while the battle runs (the host streams it): read it again for a newer state.
	if not rec or not rec.states[n] then loadRecord() end
	local want = rec.states[n]
	local moved, healed, killed, missing, lost, turned = 0, 0, 0, 0, nil, 0
	if want then
		local localOf = {}   -- record id -> local id
		for id, o in pairs(rec.ord) do if nums[o + 2] then localOf[id] = nums[o + 2] end end
		local alive = {}
		for _, a in ipairs(E.GetAllActors() or {}) do alive[tonumber(tostring(a))] = a end
		local keep = {}
		for part in want:gmatch("%S+") do
			local id, x, y, hp = part:match("^(%-?%d+)@([%-%d%.]+),([%-%d%.]+)#(%-?%d+)")
			id = tonumber(id)
			local lid = id and localOf[id]
			local a = lid and alive[lid]
			if not a then
				missing = missing + 1
				C.netWrite("# FIX " .. n .. ": actor " .. tostring(id) .. " (ordinal " .. tostring(rec.ord[id]) .. ") not here")
			else
				keep[lid] = true
				x, y, hp = tonumber(x), tonumber(y), tonumber(hp)
				local p = E.GetEntityPosition(a)
				local px, py = p and (p.x or p[1]), p and (p.y or p[2])
				if px and py and (math.abs(px - x) > 1 or math.abs(py - y) > 1) then
					C.netWrite(string.format("# FIX %d: actor %d moved %.2f,%.2f -> %.2f,%.2f", n, lid, px, py, x, y))
					E.SetEntityPosition(a, E.Vector(x, y), false)
					moved = moved + 1
				end
				local h = E.GetActorHealth(a)
				if h ~= hp then
					C.netWrite("# FIX " .. n .. ": actor " .. lid .. " health " .. tostring(h) .. " -> " .. hp)
					E.SetActorHealth(a, hp)
					healed = healed + 1
				end
				-- Facing (">L"/">R" after the health): a crewmate aimed at the guest turns there, the host's doesn't —
				-- the shot then leaves from the other side of the gun and hits someone else.
				local want = part:match(">([LR])$")
				if want and E.GetEntityDirection and E.SetEntityDirection then
					local d = E.GetEntityDirection(a)
					local x = d and (d.x or d[1])
					if type(x) == "number" and (x < 0 and "L" or "R") ~= want then
						C.netWrite("# FIX " .. n .. ": actor " .. lid .. " facing " .. (x < 0 and "L" or "R") .. " -> " .. want)
						E.SetEntityDirection(a, want == "L" and E.Vector.Left or E.Vector.Right)
						turned = turned + 1
					end
				end
			end
		end
		-- Nothing matched at all: the actor tables don't fit (not a real difference) — correcting would kill everyone.
		if next(keep) == nil then
			C.netWrite("# FIX " .. n .. ": no actor of the record found here, nothing corrected")
			alive = {}
			lost = 0
		end
		local recordOf = {}   -- local id -> record id
		for id, l in pairs(localOf) do recordOf[l] = id end
		for lid, a in pairs(alive) do
			local rid = recordOf[lid]
			if not keep[lid] and rid and rec.born[rid] and rec.born[rid] >= n then
				-- Born at the host after that state (a reinforcement): this game is just ahead, not wrong. Killing it
				-- mid-entrance froze the guest's game (2026-11-28, laptop).
				C.netWrite("# FIX " .. n .. ": actor " .. lid .. " came after this state at the host too, kept")
				keep[lid] = true
			end
			if not keep[lid] then
				C.netWrite("# FIX " .. n .. ": actor " .. lid .. " is not in the record's state, killed")
				E.KillObject(a)
				killed = killed + 1
			end
		end
	else
		C.netWrite("# FIX " .. n .. ": the record has no such state line")
	end
	C.netWrite(string.format("fix %d moved=%d health=%d killed=%d missing=%d turned=%d", n, moved, healed, killed, missing, turned))
	lastFix = line
	-- "<state> <missing>": actors alive at the host but gone here can't be brought back — the dll rejoins the host's
	-- battle from its save (hard correction). Third: how many things were corrected (0 = the dll goes on right away).
	f = io.open(file(SYNCED), "w")
	if f then f:write(n, " ", lost or missing, " ", moved + healed + killed + turned, "\n"); f:close() end
end

-- Netplay test: "a dialogue / camera sequence is on" for the dll autopilot (swh2coop.c "cutscenes on" presses Return
-- while it is 1), written when it changes.
-- Netplay: the crew in the order it came onto the field (entity ids, new ones appended in id order, the dead kept),
-- for the dll's owner split (1st = host, 2nd = guest, 3rd = host...; netplay.c "Owners"). Written when it grows.
local CREW = "swh2_net_crew.txt"
C.netCrewList, C.netPersona = {}, {}
function C.netCrew(list)
	local ids = {}
	for _, c in ipairs(list) do ids[#ids + 1] = c.id; C.netPersona[c.id] = c.persona end
	local known, fresh = {}, {}
	for _, id in ipairs(C.netCrewList) do known[id] = true end
	for _, id in ipairs(ids) do if not known[id] then fresh[#fresh + 1] = id; known[id] = true end end
	if #fresh == 0 then return end
	table.sort(fresh)
	for _, id in ipairs(fresh) do C.netCrewList[#C.netCrewList + 1] = id end
	for i, id in ipairs(C.netCrewList) do
		local p = C.netPersona[id]
		if p and not netOwners[p] then netOwners[p] = (i % 2 == 1) and 1 or 2 end
	end
	C.netWrite("# crew " .. table.concat(C.netCrewList, " "))
	local f = io.open(file(CREW), "w")
	local parts = {}   -- "id=persona": the dll needs the persona for the portrait slots
	for i, id in ipairs(C.netCrewList) do parts[i] = id .. "=" .. tostring(C.netPersona[id] or "?") end
	if f then f:write(table.concat(parts, " "), "\n"); f:close() end
end

-- Netplay HUD: two counters (counters.xml coop_net_*, added by the mod): the network status and whose move it is.
-- The dll writes swh2_net_status.txt twice a second: "<host|guest> <waiting|connected|closed|failed> <host's crewmates
-- with action points> <guest's>". E = { AddManualCounter, RemoveCounter, player = is it the player's turn }.
local STATUS, shown = "swh2_net_status.txt", {}
-- Manual counters are saved with the mission: a guest that loaded the host's in-battle save got the host's status line
-- too. The first update in a battle removes every netplay counter (removing a missing one does nothing).
local NET_COUNTERS = { "host_waiting", "host_connected", "host_closed", "host_failed", "host_joining", "guest_waiting",
	"guest_connected", "guest_closed", "guest_failed", "turn_both", "turn_p1", "turn_p2", "turn_enemy" }
local function showCounter(slot, name, E)
	if shown[slot] == name then return end
	if shown[slot] then E.RemoveCounter(shown[slot]) end
	shown[slot] = name
	if name then E.AddManualCounter(name, 0) end
end
-- Not a network game: the netplay counters of a save from one are removed once.
function C.netClearStatus(RemoveCounter)
	if C.netCleared then return end
	C.netCleared = true
	for _, n in ipairs(NET_COUNTERS) do RemoveCounter("coop_net_" .. n) end
end

-- Notifications from the dll (the last one rides on the status line: "<seq> <name> <actor id>"): a player left /
-- came back / lost the connection = the game's popup window (tutorials_popup.xml coop_net_<name>, forced: shown every
-- time); a refused command = a text over that crewmate. The one already there when the battle starts is old news.
local function netEvent(seq, name, actor, E)
	seq = tonumber(seq)
	if not seq or seq == C.netEventSeq then return end
	local first = C.netEventSeq == nil
	C.netEventSeq = seq
	if first or not name or name == "-" then return end
	C.netWrite("# shown: " .. name)
	if name:sub(1, 8) == "refused_" then
		local a = E.actorById and E.actorById(tonumber(actor))
		if a then E.SpawnWorldText(a, "worldtext_speak_important", "coop_net_" .. name) end
	elseif E.TutorialPopup then
		E.TutorialPopup("coop_net_" .. name, true)
	end
end

function C.netStatus(E)
	local f = io.open(file(STATUS), "r")
	if not f then return end
	local line = f:read("*l")
	f:close()
	local role, conn, hostLeft, guestLeft = (line or ""):match("^(%a+) (%a+) (%d+) (%d+)")
	if not role then return end
	local seq, ev, evActor, follow, own = line:match("^%a+ %a+ %d+ %d+ (%d+) (%S+) (%-?%d+) ?(%-?%d*) ?(%-?%d*)")
	if seq then netEvent(seq, ev, evActor, E) end
	local h, g = tonumber(hostLeft), tonumber(guestLeft)
	C.netCam = { player = E.player, mine = role == "host" and h or g, other = role == "host" and g or h,
		follow = tonumber(follow) or -1, own = tonumber(own) or -1 }
	if not C.netCleared then
		C.netCleared = true
		for _, n in ipairs(NET_COUNTERS) do E.RemoveCounter("coop_net_" .. n) end
	end
	if C.netRole ~= role then
		C.netRole = role
		C.netWrite("# network role " .. role .. " (owners from the split)")
	end
	showCounter("status", "coop_net_" .. role .. "_" .. conn, E)
	hostLeft, guestLeft = tonumber(hostLeft), tonumber(guestLeft)
	local turn
	if not E.player then turn = "coop_net_turn_enemy"
	elseif hostLeft > 0 and guestLeft > 0 then turn = "coop_net_turn_both"
	elseif hostLeft > 0 then turn = "coop_net_turn_p1"
	elseif guestLeft > 0 then turn = "coop_net_turn_p2"
	else turn = shown.turn end
	showCounter("turn", turn, E)
end

-- Camera (2026-11-28, laptop test 5, the user's call): the game's own camera, plus one pan when the crew's turn starts
-- to this player's first crewmate with action points. No more following the other player's crewmate (it held the
-- camera so it couldn't be moved, and came while this player still had an action). The host's AI on the guest (the
-- guest's game runs no enemy turn simulator, which moves the camera at the host): the dll writes "<seq> <actor id>"
-- to swh2_net_focus.txt per AI command, panned to the same way. The pans are made like the game's own (no letterbox:
-- the dll's CameraPan hook knows them by the hold time PAN_HOLD). Returns true while a pan of ours is on (its camera
-- sequence is not a mission cutscene). C.Enter resets it.
local FOCUS = "swh2_net_focus.txt"
local PAN_HOLD = 0.3071   -- = COOP_PAN_HOLD in netplay.c
C.focus = {}
function C.netFocus(frameNo, E)
	local F, S = C.focus, C.netCam
	if frameNo % 6 == 0 then
		local f = io.open(file(FOCUS), "r")
		local line = f and f:read("*l")
		if f then f:close() end
		local seq, id = (line or ""):match("^(%d+) (%-?%d+)")
		if not seq and F.seq == nil then F.seq = "none" end   -- no focus yet: the first one that comes is new
		if seq and seq ~= F.seq then
			local first = F.seq == nil
			F.seq = seq
			if not first then F.enemy = tonumber(id) end
		end
	end
	if not S then return false end
	local function pan(a)
		E.CameraPanToEntity(a, PAN_HOLD)
		F.panUntil = frameNo + 60
	end
	if S.player then F.enemy = nil
	elseif F.enemy then
		local a = E.actorById(F.enemy)
		F.enemy = nil
		if a then pan(a) end
	end
	if S.player and not F.wasPlayer then F.startAt = frameNo + 20 end
	F.wasPlayer = S.player
	if F.startAt and frameNo >= F.startAt and not E.IsActionsOngoing() then
		F.startAt = nil
		local a = S.mine > 0 and S.own >= 0 and E.actorById(S.own)
		if a then pan(a) end
	end
	return F.panUntil ~= nil and frameNo < F.panUntil
end

-- Name tags: the owner's name over every crewmate all the time, in the owner's colour (entities.worldtexts.xml
-- coop_tag_N: attached to the crewmate, no drift, fades after 4 s) — spawned again every ~2.5 s (150 frames, or 3 s
-- of clock at a low frame rate). Short-lived (was 12 s): a tag that fell behind its crewmate is soon replaced, and a
-- dead crewmate's (no longer in the list) is gone within seconds (2026-11-28, laptop test 5). list = crew().
C.tags = {}
function C.netTags(list, frameNo, E)
	local now = (os and os.time and os.time()) or math.floor(frameNo / 60)
	for _, c in ipairs(list) do
		local owner = C.owner(c.persona)
		local id = tostring(c.actor)
		local t = C.tags[id]
		if owner and (not t or t.owner ~= owner or frameNo - t.frame >= 150 or frameNo < t.frame or now - t.at >= 3 or now < t.at) then
			E.SpawnWorldText(c.actor, "coop_tag_" .. owner, "coop_owner_" .. owner)
			C.tags[id] = { owner = owner, at = now, frame = frameNo }
		end
	end
end

local CUTSCENE, cutscene = "swh2_net_cutscene.txt", nil

function C.netCutscene(on, why)
	if on == cutscene then return end
	cutscene = on
	C.netWrite("# cutscene " .. (on and "on" or "off") .. (why and (" (" .. why .. ")") or ""))
	local f = io.open(file(CUTSCENE), "w")
	if f then f:write(on and ("1 " .. (why or "")) or "0", "\n"); f:close() end
end

-- Guest: the host won the battle (the dll writes swh2_net_won.txt "1" once this game has played everything before
-- the host's win): ended here too, with this game's own victory screen. A guest whose mission script didn't end it
-- went into an enemy turn no one plays and froze (2026-11-28, laptop test 5). E = { EndMissionInVictory }.
local WON = "swh2_net_won.txt"
function C.netWon(frameNo, E)
	if frameNo % 15 ~= 0 then return end
	local f = io.open(file(WON), "r")
	if not f then return end
	local v = f:read("*l")
	f:close()
	if v ~= "1" then return end
	if os and os.remove then os.remove(file(WON)) end
	C.netWrite("# the host won: this mission ends in victory too")
	E.EndMissionInVictory(1.0)
end

-- The other player's move target: the dll writes swh2_net_move.txt "<seq> <actor id> <owner> <x> <y>" while that
-- player points at a tile for its selected crewmate, "<seq> off" when not (netplay.c MoveDraw). Shown as an anchor
-- entity on that tile (coop_move_anchor: the game's pointer over it, seen on stand screenshots). A WORLDTEXT made by
-- SpawnEntity was never drawn (2.6.0-2.6.2), nor one attached to the anchor by SpawnWorldText (only actors carry texts).
-- E = { SpawnEntity, DestroyEntity, FindEntityWithName, Vector }.
local MOVE, MARKER = "swh2_net_move.txt", "coop_move_marker"
C.moveSeq = nil
function C.netMove(frameNo, E)
	if frameNo % 3 ~= 0 then return end
	local f = io.open(file(MOVE), "r")
	local line = f and f:read("*l")
	if f then f:close() end
	local seq = line and line:match("^(%d+)")
	if not seq or seq == C.moveSeq then return end
	C.moveSeq = seq
	local old = E.FindEntityWithName(MARKER)
	if old then E.DestroyEntity(old) end
	local owner, x, y = line:match("^%d+ %-?%d+ (%d) ([%-%d%.]+) ([%-%d%.]+)")
	owner, x, y = tonumber(owner), tonumber(x), tonumber(y)
	if not owner or owner < 1 or owner > 2 or not x or not y then return end
	E.SpawnEntity("coop_move_anchor", E.Vector(x, y), E.Vector.Right, MARKER)
	if not C.moveLogged then
		C.moveLogged = true
		C.log("move marker: first at " .. x .. "," .. y .. (E.FindEntityWithName(MARKER) and "" or " (anchor NOT found)"))
	end
end

-- Guest: its battle is won and the host picks the next one. On the world map (map_hook.lua, OnEnterMap) the game's
-- popup says so (coop_net_wait_host): the host's next battle is joined by itself (the dll: main menu, Continue).
-- Marked by the mission hook's OnMissionVictory; a mark older than 15 min is old news.
local MAPWAIT = "swh2_net_mapwait.txt"
function C.netMapWaitMark()
	local f = io.open(file(MAPWAIT), "w")
	if f then f:write(tostring(os.time()), "\n"); f:close() end
end
function C.netMapWait(TutorialPopup)
	local f = io.open(file(MAPWAIT), "r")
	if not f then return end
	local at = tonumber(f:read("*l") or "")
	f:close()
	if os and os.remove then os.remove(file(MAPWAIT)) end
	if at and os.time() - at < 900 and TutorialPopup then TutorialPopup("coop_net_wait_host", true) end
end

local function num(v)
	if type(v) == "number" then return string.format("%.2f", v) end
	return tostring(v)
end

local function pos(p)
	if type(p) == "number" then return num(p) end
	local ok, s = pcall(function()
		local x, y = p.x, p.y
		if x == nil then x, y = p[1], p[2] end
		if x ~= nil then return num(x) .. "," .. num(y) end
		return tostring(p)
	end)
	return ok and s or tostring(p)
end

-- Facing as ">L" / ">R" (GetEntityDirection: Vector.Left / Vector.Right), "" when unreadable.
local function side(d)
	local ok, x = pcall(function() return d.x or d[1] end)
	if not ok or type(x) ~= "number" then return "" end
	return x < 0 and ">L" or ">R"
end

-- One "state" line: every actor as id@x,y#health>side, sorted by id. actors = { {actor, position, health}, ... }.
function C.netState(actors)
	if not C.netLog then return end
	local parts = {}
	for _, a in ipairs(actors) do parts[#parts + 1] = tostring(a[1]) .. "@" .. pos(a[2]) .. "#" .. tostring(a[3]) .. side(a[4]) end
	table.sort(parts, function(x, y)
		local a, b = tonumber(x:match("^(%-?%d+)")), tonumber(y:match("^(%-?%d+)"))
		if a and b and a ~= b then return a < b end
		return x < y
	end)
	C.netWrite("state " .. table.concat(parts, " "))
end

-- Called every frame: which device may play now (the owner of the selected bot; nil = everyone). Rewritten twice
-- a second even when unchanged: the dll treats a file older than 2 s as "everyone" (map, menus, mission left).
function C.input(owner)
	if not C.inputLock then return end
	local dev = owner and C.devices[owner] or "all"
	inputAge = inputAge + 1
	if dev == lastInput and inputAge < 30 then return end
	lastInput, inputAge = dev, 0
	local f = io.open(file(INPUT), "w")
	if f then f:write(dev, "\n"); f:close() end
end

-- True once per press the input lock muted since the last call.
function C.blockedPress()
	if not C.inputLock then return false end
	local f = io.open(file(BLOCKED), "r")
	if not f then return false end
	local s = f:read("*l")
	f:close()
	if s == lastBlocked then return false end
	lastBlocked = s
	return true
end

-- A mission continued from a save keeps the owners it had (bots scrapped since then would shift the deal).
function C.Resume()
	local f = io.open(file(OWNERS), "r")
	if not f then return end
	if f:read("*l") == C.script then
		for line in f:lines() do
			local p, i = line:match("^([%w_]+)=(%d+)$")
			if p and tonumber(i) <= C.players then owners[p] = tonumber(i) end
		end
	end
	f:close()
end

-- Gives every persona on the field an owner; earlier assignments stay (reinforcements get dealt in later).
function C.assign(personas)
	local count, fresh = {}, {}
	for i = 1, C.players do count[i] = 0 end
	for _, i in pairs(owners) do count[i] = count[i] + 1 end
	for _, p in ipairs(personas) do
		if not owners[p] then
			local m = C.manual[p]
			if m and m <= C.players then
				owners[p] = m
				count[m] = count[m] + 1
			end
			fresh[#fresh + 1] = p
		end
	end
	local rest = {}
	for _, p in ipairs(fresh) do if not owners[p] then rest[#rest + 1] = p end end
	table.sort(rest, function(a, b)
		local x, y = C.order[a] or 99, C.order[b] or 99
		if x ~= y then return x < y end
		return a < b
	end)
	for _, p in ipairs(rest) do
		local best = 1
		for i = 2, C.players do if count[i] < count[best] then best = i end end
		owners[p] = best
		count[best] = count[best] + 1
	end
	if #fresh > 0 then
		save()
		local parts = {}
		for _, p in ipairs(fresh) do parts[#parts + 1] = p .. "=" .. owners[p] end
		C.log(C.script .. ": " .. table.concat(parts, ", "))
	end
end

-- NB: never use io.popen/os.execute here: they flash a console window.
sandbox("Coop")
