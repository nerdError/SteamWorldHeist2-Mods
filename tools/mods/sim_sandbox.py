"""Offline check of a loose-file Lua mod against a model of the game's script sandbox (needs `pip install lupa`).
Global env runs system.lua-side code (every Lua/Mod/*.lua) with full stdlib; mission scripts run in an env holding
ONLY the engine's whitelist (see docs/EXE_NOTES.md) + stubbed engine functions.

Usage: python tools/mods/sim_sandbox.py MODDIR MISSION [--frames N] [--resume] [--file NAME=TEXT ...]
  --frames N        call OnUpdate N times (default 130)
  --resume          continue a saved mission: only OnMissionResume, then OnUpdate
  --file NAME=TEXT  pre-create a file in the fake %APPDATA%/Thunderful/SteamWorld Heist II/ (\\n = newline,
                    {now} = current unix time), e.g. --file "swh2_cheats_cmd.txt=1 {now} win\\n2 {now} god 1"
Prints the engine calls made (with arguments) and every file the mod wrote."""
import argparse, glob, os, tempfile, time
from lupa import LuaRuntime

ap = argparse.ArgumentParser()
ap.add_argument("mod")
ap.add_argument("mission")
ap.add_argument("--frames", type=int, default=130)
ap.add_argument("--resume", action="store_true")
ap.add_argument("--calls", type=int, default=80, help="how many engine calls to print")
ap.add_argument("--turns", type=int, default=2, help="player turns to simulate (each followed by an enemy turn)")
ap.add_argument("--file", action="append", default=[])
a = ap.parse_args()

tmp = tempfile.mkdtemp()
data = os.path.join(tmp, "Thunderful", "SteamWorld Heist II")
os.makedirs(data, exist_ok=True)
for spec in a.file:
    name, text = spec.split("=", 1)
    with open(os.path.join(data, name), "w", encoding="utf-8") as f:
        f.write(text.replace("\\n", "\n").replace("{now}", str(int(time.time()))) + "\n")

L = LuaRuntime(unpack_returned_tuples=True)
L.execute('''
SANDBOX_EXPORTS = {}
function sandbox(name) SANDBOX_EXPORTS[name] = true end
function require_global() end
local realgetenv = os.getenv
os.getenv = function(k) if k == "APPDATA" then return TMPDIR end return realgetenv(k) end
''')
L.globals().TMPDIR = tmp
L.globals().FRAME = 0
for f in sorted(glob.glob(os.path.join(a.mod, "Lua", "Mod", "*.lua"))):
    L.execute(open(f, encoding="utf-8").read())
L.execute(r'''
function make_env()
  local env = {}
  for _, k in ipairs({"tostring","tonumber","pairs","ipairs","next","select","type"}) do env[k] = _G[k] end
  env.table = {insert=table.insert, remove=table.remove, sort=table.sort, move=table.move, concat=table.concat, pack=table.pack, unpack=table.unpack}
  env.math = {}; for k, v in pairs(math) do if k ~= "random" and k ~= "randomseed" then env.math[k] = v end end
  env.string = {}; for k, v in pairs(string) do if k ~= "dump" then env.string[k] = v end end
  env.coroutine = coroutine
  env.print = function(...) end
  for name in pairs(SANDBOX_EXPORTS) do env[name] = _G[name] end
  CALLS = {}
  local function rec(k, ...)
    local args = {}
    for i = 1, select("#", ...) do args[#args + 1] = tostring((select(i, ...))) end
    table.insert(CALLS, k .. "(" .. table.concat(args, ", ") .. ")")
  end
  -- engine stubs with plausible results; anything else = recorded no-op returning nil
  local stubs = {
    GetFriendlyActors = function() return {101, 102} end,
    GetEnemyActors = function() return {201} end,
    GetAllActors = function() return {101, 102, 201} end,
    -- an action is in progress during frames 10..19 of every 40
    IsActionsOngoing = function() return FRAME % 40 >= 10 and FRAME % 40 < 20 end,
    GetActorMaxHealth = function() return 10 end,
    GetActorHealth = function() return 5 end,
    HasModifier = function() return false end,
    GetPlayerFaction = function() return "cowbots" end,
    GetEntityDefinition = function(e) return e >= 200 and "enemy_navy_seabot" or "crew_daisy" end,
    GetActorFaction = function(e) return e >= 200 and "navy" or "cowbots" end,
    GetAlertActors = function() return {201} end,
    IsActorHostile = function(e) return e >= 200 end,
    GetEntityPosition = function(e) return {x = e, y = 0} end,
    GetEntityName = function(e) return ({[101] = "captain", [102] = "wesley"})[e] or ("entity_" .. e) end,
    GetCurrentFaction = function() return "cowbots" end,
    -- the player clicks through the crew: a different bot every 40 frames (see OnUpdate loop)
    GetSelectedActor = function() return ({101, 102})[math.floor(FRAME / 40) % 2 + 1] end,
  }
  -- Vector(x, y) and the constants Vector.Left/Right/Up/Down
  env.Vector = setmetatable({Left = {x = -1, y = 0}, Right = {x = 1, y = 0}, Up = {x = 0, y = -1}, Down = {x = 0, y = 1}},
    {__call = function(_, x, y) return {x = x, y = y} end})
  setmetatable(env, {__index = function(t, k)
    if type(k) == "string" and k:match("^%u") then
      return function(...) rec(k, ...); if stubs[k] then return stubs[k](...) end end
    end
  end})
  return env
end
''')
env = L.eval("make_env()")
src = open(os.path.join(a.mod, "Lua", "Missions", a.mission + ".lua"), encoding="utf-8-sig").read()
f, err = L.eval("function(s, e) local f, err = load(s, 'mission', 't', e); return f, tostring(err) end")(src, env)
assert f, err
f()
start = [("OnMissionStart", ()), ("OnCrewDeployed", ())]
for _ in range(a.turns): start += [("OnFactionTurnBegin", ("cowbots",)), ("OnFactionTurnBegin", ("navy",))]
start += [("OnEntityKilled", (201,))]
for cb, args in ([("OnMissionResume", ())] if a.resume else start):
    fn = env[cb]
    if fn: fn(*args)
upd = env["OnUpdate"]
if upd:
    for i in range(a.frames):
        L.globals().FRAME = i
        upd(1 / 60)
calls = list(L.eval("CALLS").values())
print(f"engine calls ({len(calls)}):", ", ".join(calls[:a.calls]) + (" …" if len(calls) > a.calls else ""))
for p in sorted(glob.glob(os.path.join(data, "*"))):
    print(f"---- {os.path.basename(p)}")
    print(open(p, encoding="utf-8").read().rstrip())
