-- SWH2 CheatMenu backend: runs in the GLOBAL Lua environment (loaded from system.lua via require_global).
-- Engine functions are not visible here; mission scripts run the actual cheats (see the appended hook).
Cheats = {}

local function datadir()
	local appdata = os and os.getenv and os.getenv("APPDATA")
	if appdata then return appdata .. "\\Thunderful\\SteamWorld Heist II\\" end
	return ""
end

local DIR = datadir()
local CMD = DIR .. "swh2_cheats_cmd.txt"
local WORK = CMD .. ".work"
local LOG = DIR .. "swh2_cheats.log"
local STATE = DIR .. "swh2_cheats_state.txt"
local STATUS = DIR .. "swh2_cheats_status.txt"
local TOGGLES = { god = true, freeze = true, spinup = true }
local MAX_AGE = 10

local flags = {}
local frame = 0
Cheats.script = "?"

function Cheats.log(msg)
	local f = io.open(LOG, "a")
	if f then f:write(os.date("%Y-%m-%d %H:%M:%S"), "  ", tostring(msg), "\n"); f:close() end
end

-- Mission sandboxes have no pcall: they call this instead. Errors are logged, never propagated.
function Cheats.safe(where, fn, ...)
	local ok, err = pcall(fn, ...)
	if not ok then Cheats.log("ERROR in " .. tostring(where) .. ": " .. tostring(err)) end
	return ok
end

function Cheats.flag(name) return flags[name] == true end

local function readFlags()
	flags = {}
	local f = io.open(STATE, "r")
	if not f then return end
	for line in f:lines() do
		local k, v = line:match("^(%w+)=(%d)")
		if k and TOGGLES[k] then flags[k] = v == "1" end
	end
	f:close()
end

local function writeFlags()
	local f = io.open(STATE, "w")
	if not f then return end
	for k in pairs(TOGGLES) do f:write(k, "=", flags[k] and "1" or "0", "\n") end
	f:close()
end

local function writeStatus()
	local f = io.open(STATUS, "w")
	if not f then return end
	f:write("script=", Cheats.script, "\n", "time=", tostring(os.time()), "\n")
	for k in pairs(TOGGLES) do f:write(k, "=", flags[k] and "1" or "0", "\n") end
	f:close()
end

function Cheats.report(id, cmd, result)
	Cheats.log(tostring(id) .. " " .. tostring(cmd) .. " " .. tostring(result) .. " (" .. Cheats.script .. ")")
end

-- Called once when a mission script loads.
function Cheats.Enter(script)
	Cheats.script = script
	frame = 0
	readFlags()
	writeStatus()
end

-- Called every frame from the mission OnUpdate. Returns the one-shot commands to run now, and whether the
-- toggle modifiers should be re-synced (periodically, to catch new spawns, and right after a toggle change).
function Cheats.Tick()
	frame = frame + 1
	local cmds, sync = {}, frame % 60 == 1
	if frame % 120 == 1 then writeStatus() end
	if frame % 15 ~= 1 then return cmds, sync end
	os.remove(WORK)
	if not os.rename(CMD, WORK) then return cmds, sync end
	local f = io.open(WORK, "r")
	if not f then return cmds, sync end
	local now, changed = os.time(), false
	for line in f:lines() do
		local id, t, cmd, arg = line:match("^(%S+)%s+(%d+)%s+(%S+)%s*(.-)%s*$")
		if id then
			if TOGGLES[cmd] then
				flags[cmd] = arg == "1"
				changed = true
				Cheats.report(id, cmd .. "=" .. (flags[cmd] and "1" or "0"), "ok")
			elseif now - tonumber(t) > MAX_AGE then
				Cheats.report(id, cmd, "expired")
			else
				cmds[#cmds + 1] = { id = id, cmd = cmd, arg = arg }
			end
		end
	end
	f:close()
	os.remove(WORK)
	if changed then writeFlags(); writeStatus(); sync = true end
	return cmds, sync
end

-- NB: never use io.popen/os.execute here: they flash a console window.
sandbox("Cheats")
