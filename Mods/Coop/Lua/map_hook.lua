-- ==== SWH2 Co-op (auto-appended) ====
-- World map: a network guest back from a won battle is told it waits for the host's next mission (coop.lua
-- C.netMapWait; it joins that one by itself). Calls the original first, always.
do
	local origEnterMap = OnEnterMap
	function OnEnterMap(...)
		local r = nil
		if origEnterMap then r = origEnterMap(...) end
		if Coop and Coop.safe and Coop.netMapWait then Coop.safe("worldmap", Coop.netMapWait, TutorialPopup) end
		return r
	end
end
