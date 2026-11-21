"""SUPERSEDED by the CheatMenu mod (Mods/CheatMenu, same output byte for byte). Kept only as a reference.

Builds the 'CheatMenu' loose-file mod: an in-mission cheat backend driven from outside the game.

The official cheat menu was stripped from the release exe (docs/EXE_NOTES.md), and Lua can neither read the
keyboard nor draw UI, so the menu itself lives in our app (GUI "Cheats…" window / `swh2 cheat ...`). The app
appends commands to a file; this mod polls it from the mission OnUpdate and runs them with engine functions.

Files in %APPDATA%/Thunderful/SteamWorld Heist II/:
  swh2_cheats_cmd.txt     commands from the app, one per line: "<id> <unixtime> <cmd> [arg]"
  swh2_cheats_state.txt   persistent toggles ("god=1"), owned by the mod
  swh2_cheats_status.txt  heartbeat: current mission script + time + toggles (rewritten every ~2 s)
  swh2_cheats.log         results ("<id> <cmd> ok|error|unknown|expired") and errors

Commands: win, heal, kill, weaken, alarm_off, give <item>; toggles god|freeze|spinup 0|1.
One-shot commands older than 10 s are dropped, so a click made outside a mission doesn't fire in the next one.

Usage: python tools/mods/make_cheat_menu.py [--game DIR]
Then:  swh2 enable CheatMenu && swh2 install      (don't combine with LuaProbe: both replace system.lua)
"""
import argparse, os, re, shutil, zipfile

DEFAULT_GAME = r"C:\Program Files (x86)\Steam\steamapps\common\SteamWorld Heist 2"
MARK = "-- ==== SWH2 CheatMenu (auto-appended) ===="

CHEATS_LUA = r'''-- SWH2 CheatMenu backend: runs in the GLOBAL Lua environment (loaded from system.lua via require_global).
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
'''

# Appended to every mission script. Runs in the mission's SANDBOX (no pcall/error/io/os/_G, see
# docs/EXE_NOTES.md): everything goes through Cheats.safe, and the original callbacks are always called.
MISSION_HOOK = MARK + r'''
do
	local cheatScript = "%(script)s"
	local C = Cheats
	local function run(fn, ...) if C and C.safe then return C.safe(cheatScript, fn, ...) end end
	local function each(list, fn) if list then for _, a in ipairs(list) do fn(a) end end end
	local function setMod(list, mod, on)
		each(list, function(a)
			local has = HasModifier(a, mod)
			if on and not has then AddModifier(a, mod) elseif has and not on then RemoveModifier(a, mod) end
		end)
	end
	local function sync()
		setMod(GetFriendlyActors(), "cheat_god", C.flag("god"))
		setMod(GetFriendlyActors(), "cheat_spinup", C.flag("spinup"))
		setMod(GetEnemyActors(), "cheat_freeze", C.flag("freeze"))
	end
	local actions = {
		win = function() EndMissionInVictory(1.5) end,
		heal = function() each(GetFriendlyActors(), function(a) SetActorHealth(a, GetActorMaxHealth(a)) end) end,
		kill = function() each(GetEnemyActors(), function(e) KillObject(e) end) end,
		weaken = function() each(GetEnemyActors(), function(e) SetActorHealth(e, 1) end) end,
		alarm_off = function() SetAlarmActive(false) end,
		give = function(item) GiveItem(item, true) end,
	}

	run(function() C.Enter(cheatScript) end)

	local origUpdate = OnUpdate
	function OnUpdate(...)
		local cmds, doSync = nil, false
		run(function() cmds, doSync = C.Tick() end)
		each(cmds, function(c)
			local fn = actions[c.cmd]
			if not fn then run(C.report, c.id, c.cmd, "unknown"); return end
			local ok = run(fn, c.arg)
			run(C.report, c.id, c.cmd, ok and "ok" or "error")
		end)
		if doSync then run(sync) end
		if origUpdate then return origUpdate(...) end
	end

	local origCrewDeployed = OnCrewDeployed
	function OnCrewDeployed(...)
		run(sync)
		if origCrewDeployed then return origCrewDeployed(...) end
	end

	local origTurnBegin = OnFactionTurnBegin
	function OnFactionTurnBegin(...)
		run(sync)
		if origTurnBegin then return origTurnBegin(...) end
	end
end
'''

# Own modifiers built from the engine's status flags (documented at the top of modifiers.xml).
MODIFIERS = '''
	<!-- SWH2 CheatMenu -->
	<Modifier Name="cheat_god">
		<LocalizedNameId>cheat_god_name</LocalizedNameId>
		<StatusFlags>invulnerable, buddha</StatusFlags>
	</Modifier>
	<Modifier Name="cheat_spinup">
		<LocalizedNameId>cheat_spinup_name</LocalizedNameId>
		<StatusFlags>spin_up</StatusFlags>
	</Modifier>
	<Modifier Name="cheat_freeze">
		<LocalizedNameId>cheat_freeze_name</LocalizedNameId>
		<StatusFlags>ai_disabled</StatusFlags>
	</Modifier>
'''

LOC = {
    "en": {"cheat_god_name": "[Cheat] Invulnerable", "cheat_spinup_name": "[Cheat] Attacks don't end turn",
           "cheat_freeze_name": "[Cheat] Frozen"},
    "ru": {"cheat_god_name": "[Чит] Неуязвимость", "cheat_spinup_name": "[Чит] Атака не завершает ход",
           "cheat_freeze_name": "[Чит] Заморожен"},
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=DEFAULT_GAME)
    a = ap.parse_args()
    # Always build from the ORIGINAL pak: the installed one may already contain a mod.
    backup = os.path.join(a.game, "SWH2ModLoader", "Backup", "Game.pak")
    pak = zipfile.ZipFile(backup if os.path.exists(backup) else os.path.join(a.game, "Bundle", "Core", "Game.pak"))
    assert b"(auto-appended) ====" not in pak.read("Lua/Missions/arctica_09.lua"), "source pak is already modded"
    out = os.path.join(a.game, "Mods", "CheatMenu")
    if os.path.isdir(out):
        shutil.rmtree(out)

    def write(rel, data: bytes):
        p = os.path.join(out, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "wb") as f:
            f.write(data)

    def text(rel):
        return pak.read(rel).decode("utf-8-sig")

    # 1. our module, loaded from system.lua (global env) right after vector.lua
    write("Lua/Mod/cheats.lua", CHEATS_LUA.replace("\n", "\r\n").encode())
    sysl = text("Lua/System/system.lua")
    anchor = 'require_global("System/vector")'
    assert anchor in sysl
    write("Lua/System/system.lua", sysl.replace(anchor, anchor + '\r\nrequire_global("Mod/cheats")', 1).encode())

    # 2. hook every mission script
    n = 0
    for name in pak.namelist():
        if name.startswith("Lua/Missions/") and name.endswith(".lua"):
            script = name[len("Lua/Missions/"):-4]
            src = text(name).rstrip() + "\r\n\r\n" + (MISSION_HOOK % {"script": script}).replace("\n", "\r\n")
            write(name, src.encode())
            n += 1

    # 3. modifiers (text insert before the closing tag keeps the rest of the file byte-identical)
    raw = pak.read("Definitions/modifiers.xml")
    mods = raw.decode("utf-8-sig")
    close = "</ModifierDefinitions>"
    assert mods.count(close) == 1
    mods = mods.replace(close, MODIFIERS.replace("\n", "\r\n").lstrip("\r\n") + close)
    write("Definitions/modifiers.xml", (b"\xef\xbb\xbf" if raw.startswith(b"\xef\xbb\xbf") else b"") + mods.encode())

    # 4. modifier names: new <lang>_mod.csv files (proven to load, docs/EXE_NOTES.md)
    langs = 0
    for name in pak.namelist():
        m = re.fullmatch(r"Language/([a-z\-]+)\.csv", name)
        if m:
            lang = m.group(1)
            src = pak.read(name)
            bom = b"\xef\xbb\xbf" if src.startswith(b"\xef\xbb\xbf") else b""
            tab = "\t" if src.decode("utf-8-sig").split("\r\n", 1)[0].endswith("\t") else ""
            rows = "".join(f"{k}\t{v}{tab}\r\n" for k, v in LOC.get(lang, LOC["en"]).items())
            write(f"Language/{lang}_mod.csv", bom + rows.encode())
            langs += 1

    with open(os.path.join(out, "README.txt"), "w", encoding="utf-8") as f:
        f.write("CheatMenu — generated by tools/mods/make_cheat_menu.py. Control it from the GUI (Cheats…) or `swh2 cheat`.\n")
    print(f"CheatMenu written to {out}: cheats.lua, system.lua, {n} mission scripts, modifiers.xml, {langs} language files")


if __name__ == "__main__":
    main()
