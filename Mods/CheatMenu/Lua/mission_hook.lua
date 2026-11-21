-- ==== SWH2 CheatMenu (auto-appended) ====
do
	local cheatScript = "{SCRIPT}"
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
