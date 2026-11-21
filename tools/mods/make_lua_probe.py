"""Builds the 'LuaProbe' loose-file mod: an in-game test of the modding hypotheses from docs/EXE_NOTES.md.

What it checks (results go to %APPDATA%/Thunderful/SteamWorld Heist II/swh2_mod_probe.log):
  1. system.lua can load our own file from the pak via require_global("Mod/probe").
  2. The global Lua environment has io/os (file I/O -> persistent state between missions and game sessions).
  3. sandbox("ModProbe") exposes our table to mission scripts.
  4. Wrapping mission callbacks (OnCrewDeployed, OnFactionTurnBegin) works, including in missions that
     did not define them.
In game: a bark "[MOD] ..." over the first crewmate when the crew is deployed and on each player turn.

Usage: python tools/mods/make_lua_probe.py [--game DIR]
Then:  swh2 enable LuaProbe && swh2 install      (undo: swh2 restore)
"""
import argparse, os, re, shutil, zipfile

DEFAULT_GAME = r"C:\Program Files (x86)\Steam\steamapps\common\SteamWorld Heist 2"
MARK = "-- ==== SWH2 LuaProbe (auto-appended) ===="

PROBE_LUA = r'''-- SWH2 LuaProbe: runs in the GLOBAL Lua environment (loaded from system.lua via require_global).
ModProbe = {}

local function logdir()
	local appdata = os and os.getenv and os.getenv("APPDATA")
	if appdata then return appdata .. "\\Thunderful\\SteamWorld Heist II\\" end
	return ""
end

local LOG = logdir() .. "swh2_mod_probe.log"
local STATE = logdir() .. "swh2_mod_probe_state.txt"

function ModProbe.log(msg)
	local line = (os and os.date and os.date("%Y-%m-%d %H:%M:%S") or "?") .. "  " .. tostring(msg)
	print("[LuaProbe] " .. tostring(msg))
	if io and io.open then
		local f = io.open(LOG, "a")
		if f then f:write(line, "\n"); f:close() end
	end
end

-- Persistent counter (survives missions and game restarts) -> proof that run state can be kept.
local function readCount()
	if not (io and io.open) then return -1 end
	local f = io.open(STATE, "r")
	if not f then return 0 end
	local n = tonumber(f:read("l") or "0") or 0
	f:close()
	return n
end

local function writeCount(n)
	if not (io and io.open) then return end
	local f = io.open(STATE, "w")
	if f then f:write(tostring(n), "\n"); f:close() end
end

-- Mission sandboxes have no pcall: they call this instead. Errors are logged, never propagated.
function ModProbe.safe(where, fn, ...)
	local ok, err = pcall(fn, ...)
	if not ok then ModProbe.log("ERROR in " .. tostring(where) .. ": " .. tostring(err)) end
	return ok
end

-- Called from mission scripts. Returns a short text to show in game.
function ModProbe.CrewDeployed(script)
	local n = readCount() + 1
	writeCount(n)
	ModProbe.turn = 0
	ModProbe.script = script
	ModProbe.log(string.format("CrewDeployed in %s, missions so far: %d", script, n))
	return n
end

function ModProbe.PlayerTurn(script)
	ModProbe.turn = (ModProbe.turn or 0) + 1
	ModProbe.log(string.format("Player turn %d in %s", ModProbe.turn, script))
	return ModProbe.turn
end

-- One-time environment report.
local function t(x) return type(x) end
ModProbe.log("==== LuaProbe loaded (" .. tostring(_VERSION) .. ") ====")
ModProbe.log("libs: io=" .. t(io) .. " os=" .. t(os) .. " debug=" .. t(debug) .. " package=" .. t(package)
	.. " load=" .. t(load) .. " loadfile=" .. t(loadfile) .. " dofile=" .. t(dofile) .. " require=" .. t(require))
ModProbe.log("engine fns visible in global env: SpawnWorldText=" .. t(SpawnWorldText) .. " GetFriendlyActors=" .. t(GetFriendlyActors)
	.. " AddModifier=" .. t(AddModifier) .. " GiveItem=" .. t(GiveItem) .. " sandbox=" .. t(sandbox))
-- NB: never use io.popen/os.execute here: they flash a console window.

sandbox("ModProbe")
'''

# Appended to every mission script. Runs in the mission's SANDBOX environment, which has NO pcall/error/
# setmetatable/io/os/_G (whitelist: docs/EXE_NOTES.md). Anything that may fail goes through ModProbe.safe,
# which runs pcall in the global environment. If ModProbe is missing, our code is skipped entirely and the
# original mission callbacks are always called.
MISSION_HOOK = MARK + r'''
do
	local probeScript = "%(script)s"
	local P = ModProbe
	local function run(fn, ...) if P and P.safe then P.safe(probeScript, fn, ...) end end
	local function bark(textId)
		local crew = GetFriendlyActors()
		if crew and crew[1] then SpawnWorldText(crew[1], "worldtext_speak_important", textId) end
	end

	run(function()
		P.log("script loaded: " .. probeScript .. "  pcall=" .. type(pcall) .. " print=" .. type(print)
			.. " GetPlayerFaction()=" .. tostring(GetPlayerFaction()))
	end)

	local origMissionStart = OnMissionStart
	function OnMissionStart(...)
		run(function() P.log("OnMissionStart in " .. probeScript) end)
		if origMissionStart then return origMissionStart(...) end
	end

	local origCrewDeployed = OnCrewDeployed
	function OnCrewDeployed(...)
		run(function()
			P.CrewDeployed(probeScript)
			bark("mod_probe_deployed")
		end)
		if origCrewDeployed then return origCrewDeployed(...) end
	end

	local origTurnBegin = OnFactionTurnBegin
	function OnFactionTurnBegin(faction, ...)
		run(function()
			P.log("OnFactionTurnBegin(" .. tostring(faction) .. ") in " .. probeScript)
			if faction == GetPlayerFaction() then
				P.PlayerTurn(probeScript)
				bark("mod_probe_turn")
			end
		end)
		if origTurnBegin then return origTurnBegin(faction, ...) end
	end
end
'''

LOC = {"en": "[MOD] A: key in en.csv works!", "ru": "[МОД] A: ключ в ru.csv работает!"}
LOC_TURN = {"en": "[MOD] B: en_mod.csv works!", "ru": "[МОД] B: ru_mod.csv работает!"}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=DEFAULT_GAME)
    a = ap.parse_args()
    # Always build from the ORIGINAL pak: the installed one may already contain this mod.
    backup = os.path.join(a.game, "SWH2ModLoader", "Backup", "Game.pak")
    pak = zipfile.ZipFile(backup if os.path.exists(backup) else os.path.join(a.game, "Bundle", "Core", "Game.pak"))
    assert MARK.encode() not in pak.read("Lua/Missions/" + "arctica_09.lua"), "source pak is already modded"
    out = os.path.join(a.game, "Mods", "LuaProbe")
    if os.path.isdir(out):
        shutil.rmtree(out)

    def write(rel, data: bytes):
        p = os.path.join(out, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "wb") as f:
            f.write(data)

    def text(rel):
        return pak.read(rel).decode("utf-8-sig")

    # 1. our module
    write("Lua/Mod/probe.lua", PROBE_LUA.replace("\n", "\r\n").encode())

    # 2. load it from system.lua (global env), right after vector.lua
    sysl = text("Lua/System/system.lua")
    anchor = 'require_global("System/vector")'
    assert anchor in sysl
    sysl = sysl.replace(anchor, anchor + '\r\nrequire_global("Mod/probe")', 1)
    write("Lua/System/system.lua", sysl.encode())

    # 3. hook every mission script
    n = 0
    for name in pak.namelist():
        if name.startswith("Lua/Missions/") and name.endswith(".lua"):
            src = text(name)
            script = name[len("Lua/Missions/"):-4]
            write(name, (src.rstrip() + "\r\n\r\n" + (MISSION_HOOK % {"script": script}).replace("\n", "\r\n")).encode())
            n += 1

    # 4. localization experiment: a key appended at the END of <lang>.csv was NOT found in game (v3).
    #    A: deploy key inserted right after the first line of <lang>.csv
    #    B: turn key in a new file <lang>_mod.csv (the loader also reads <lang>_*.csv)
    langs = 0
    for name in pak.namelist():
        m = re.fullmatch(r"Language/([a-z\-]+)\.csv", name)
        if m:
            lang = m.group(1)
            src = pak.read(name)
            bom = b"\xef\xbb\xbf" if src.startswith(b"\xef\xbb\xbf") else b""
            body = src.decode("utf-8-sig")
            first, rest = body.split("\r\n", 1)
            tab = "\t" if first.endswith("\t") else ""   # most non-en files end every line with a tab
            body = first + "\r\nmod_probe_deployed\t" + LOC.get(lang, LOC["en"]) + tab + "\r\n" + rest
            write(name, bom + body.encode())
            write(f"Language/{lang}_mod.csv", bom + f"mod_probe_turn\t{LOC_TURN.get(lang, LOC_TURN['en'])}{tab}\r\n".encode())
            langs += 1

    readme = "LuaProbe — test mod generated by tools/mods/make_lua_probe.py. Log: %APPDATA%\\Thunderful\\SteamWorld Heist II\\swh2_mod_probe.log\n"
    with open(os.path.join(out, "README.txt"), "w", encoding="utf-8") as f:
        f.write(readme)
    print(f"LuaProbe written to {out}: probe.lua, system.lua, {n} mission scripts, {langs} language files")


if __name__ == "__main__":
    main()
