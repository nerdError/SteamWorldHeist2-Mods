-- ==== SWH2 Co-op (auto-appended) ====
do
	local coopScript = "{SCRIPT}"
	local C = Coop
	local function run(fn, ...) if C and C.safe then return C.safe(coopScript, fn, ...) end end
	local showIn = nil      -- frames until the owner labels of the whole crew are shown (counts once the camera is free)
	local lastSelected = nil

	local function busy() return IsCameraSequenceActive() or IsActionsOngoing() end
	local function playerTurn() return GetCurrentFaction() == GetPlayerFaction() end

	local function crew()
		local list, names = {}, {}
		local friends = GetFriendlyActors()
		if friends then
			for _, a in ipairs(friends) do
				local p = C.persona(GetEntityName(a), GetEntityDefinition(a))
				if p then
					list[#list + 1] = { actor = a, persona = p }
					names[#names + 1] = p
				end
			end
		end
		C.assign(names)
		return list
	end

	-- Owner modifier (its name shows the player in the bot's info) + assignment of new crewmates.
	local function refresh()
		local list = crew()
		for _, c in ipairs(list) do
			local owner = C.owner(c.persona)
			for i = 1, C.players do
				local mod = "coop_owner_" .. i
				local has = HasModifier(c.actor, mod)
				if i == owner and not has then AddModifier(c.actor, mod) elseif i ~= owner and has then RemoveModifier(c.actor, mod) end
			end
		end
		return list
	end

	local function label(actor, persona)
		if C.labelAlways then return end   -- the name tags are there all the time
		local owner = C.owner(persona)
		if owner then SpawnWorldText(actor, "worldtext_speak_important", "coop_owner_" .. owner) end
	end

	local function showAll()
		local list = refresh()
		if C.labelTurn then for _, c in ipairs(list) do label(c.actor, c.persona) end end
		lastSelected = GetSelectedActor()
	end

	local function selection()
		local sel = GetSelectedActor()
		if sel == lastSelected then return end
		lastSelected = sel
		if not sel then return end
		local p = C.persona(GetEntityName(sel), GetEntityDefinition(sel))
		if not p then return end
		if not C.owner(p) then refresh() end
		label(sel, p)
	end

	run(function() C.Enter(coopScript) end)

	local origCrewDeployed = OnCrewDeployed
	function OnCrewDeployed(...)
		showIn = 30
		if origCrewDeployed then return origCrewDeployed(...) end
	end

	local origResume = OnMissionResume
	function OnMissionResume(...)
		run(C.Resume)
		showIn = 30
		if origResume then return origResume(...) end
	end

	local origTurnBegin = OnFactionTurnBegin
	function OnFactionTurnBegin(faction, ...)
		if faction == GetPlayerFaction() then showIn = 20 end
		run(function() C.netWrite("turn " .. tostring(faction) .. (faction == GetPlayerFaction() and " player" or "")) end)
		if origTurnBegin then return origTurnBegin(faction, ...) end
	end

	-- Input lock: only the device of the selected bot's owner may play (everyone during cutscenes/dialogue and
	-- enemy turns). A muted press shows whose bot it is.
	local frame, blockedShown, over = 0, -1000, false
	local function lockInput()
		local owner, sel = nil, nil
		if not over and playerTurn() and not IsCameraSequenceActive() then
			sel = GetSelectedActor()
			local p = sel and C.persona(GetEntityName(sel), GetEntityDefinition(sel))
			if p and not C.owner(p) then refresh() end
			owner = p and C.owner(p)
		end
		C.input(owner)
		frame = frame + 1
		if frame % 15 == 0 and C.blockedPress() and owner and frame - blockedShown > 90 then
			blockedShown = frame
			SpawnWorldText(sel, "worldtext_speak_important", "coop_owner_" .. owner)
		end
	end

	-- Netplay test: the state of every actor whenever the actions in progress are over.
	local netBusy = false
	local function netWatch()
		if not C.netLog then return end
		if IsActionsOngoing() then netBusy = true; return end
		if not netBusy then return end
		netBusy = false
		local list = {}
		for _, a in ipairs(GetAllActors() or {}) do list[#list + 1] = { a, GetEntityPosition(a), GetActorHealth(a), GetEntityDirection(a) } end
		C.netState(list)
	end

	local function actorById(id)
		if not id then return nil end
		for _, a in ipairs(GetAllActors() or {}) do if tonumber(tostring(a)) == id then return a end end
		return nil
	end

	local netApi = { IsActionsOngoing = IsActionsOngoing, GetAllActors = GetAllActors, GetEntityPosition = GetEntityPosition,
		GetEntityDirection = GetEntityDirection, SetEntityDirection = SetEntityDirection,
		SetEntityPosition = SetEntityPosition, GetActorHealth = GetActorHealth, SetActorHealth = SetActorHealth,
		KillObject = KillObject, Vector = Vector, CameraPanToEntity = CameraPanToEntity,
		SetCameraTargetEntity = SetCameraTargetEntity, ClearCameraTarget = ClearCameraTarget, actorById = actorById }

	-- Netplay test: dialogue for the autopilot (it clicks through while this says so).
	local convoAt, frameNo = nil, 0
	if C and C.netTest and StartConversation then
		local origConversation = StartConversation
		function StartConversation(name, ...)
			convoAt = frameNo
			return origConversation(name, ...)
		end
	end
	local netOver, crewSeen = false, false
	local function netOverCheck()   -- crew seen alive, now none left: the mission is lost (no Lua callback for that)
		if netOver then return end
		local crew = GetFriendlyActors()
		if crew and #crew > 0 then crewSeen = true; return end
		if not crewSeen then return end
		netOver = true
		C.netWrite("# mission over: crew lost")
	end

	local function netCutscene()
		frameNo = frameNo + 1
		-- A pan of our own to the other player's action is no mission cutscene (the replay would wait for it).
		local ourPan = C.netFocus(frameNo, netApi)
		local camera = IsCameraSequenceActive() and not ourPan
		local convo = convoAt ~= nil and frameNo - convoAt < 120
		C.netCutscene(camera or convo, camera and "camera" or (convo and "conversation" or nil))
	end

	-- Netplay: the crew for the owner split and the HUD status, twice a second.
	local function netCrew()
		if frameNo % 30 ~= 0 then return end
		local list, all = {}, crew()
		for _, c in ipairs(all) do list[#list + 1] = { id = tonumber(tostring(c.actor)), persona = c.persona } end
		C.netCrew(list)
		C.netStatus({ AddManualCounter = AddManualCounter, RemoveCounter = RemoveCounter, player = playerTurn(),
			SpawnWorldText = SpawnWorldText, TutorialPopup = TutorialPopup, actorById = actorById })
		if C.labelAlways and C.netRole and not over then C.netTags(all, frameNo, { SpawnWorldText = SpawnWorldText }) end
	end


	local origVictory = OnMissionVictory
	function OnMissionVictory(...)
		over = true
		run(C.input, nil)
		if C.netLog and not netOver then netOver = true; run(C.netWrite, "# mission over: victory") end
		if C.netLog then run(C.netWrite, "won") end   -- host: the guest's game ends its battle too (netplay.c FollowVictory)
		if C.netRole == "guest" then run(C.netMapWaitMark) end   -- the world map then says it waits for the host
		if origVictory then return origVictory(...) end
	end

	local origUpdate = OnUpdate
	function OnUpdate(...)
		run(lockInput)
		run(netWatch)
		if C.netLog then
			run(C.netFix, netApi); run(netCutscene); run(netOverCheck); run(netCrew)
			if not over then run(C.netWon, frameNo, { EndMissionInVictory = EndMissionInVictory }) end
			run(C.netMove, frameNo, { SpawnEntity = SpawnEntity, DestroyEntity = DestroyEntity, SpawnWorldText = SpawnWorldText,
				FindEntityWithName = FindEntityWithName, Vector = Vector })
		else run(C.netClearStatus, RemoveCounter) end   -- a save from a network game keeps its status lines
		run(function()
			if busy() or not playerTurn() then return end
			if showIn then
				showIn = showIn - 1
				if showIn <= 0 then
					showIn = nil
					showAll()
				end
			elseif C.labelSelect then
				selection()
			end
		end)
		if origUpdate then return origUpdate(...) end
	end
end
