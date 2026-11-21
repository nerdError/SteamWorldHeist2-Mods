# Challenge Manager — SteamWorld Heist 2

Requires **SWH2 Mod Loader** 1.0+. Works alone or together with the Randomizer.

Optional rules for harder runs; each one has its own switch.

**No grind** — everything farmable becomes a one-time reward:
- *Won missions can't be replayed.* Uses the game's own mission locks via hidden quests, so it's stored in your save.
  Story rematches (e.g. the Fabio fight) stay open until their quest is done.
- *Flagships/artifacts don't respawn* after you loot them, no "ghost" loot, no daily floating loot on the map.
- *World map ships drop nothing.*

**Hardcore**
- *No checkpoints*: "Load Checkpoint" is disabled in missions (pause menu and the objective-failed popup).
  Patches the game exe (current Steam version only; the loader checks it and keeps a backup).
- *Quitting mid-mission forfeits it*: the game saves missions about once per turn, so quitting and pressing
  Continue would rewind the turn. With this on, continuing a mission from a save sends you back to the world map,
  like Abort. A crash mid-mission costs the mission too. The ship, bars, shops, the tutorial and the prison break
  (which can't be aborted) are not affected. Pairs naturally with *No checkpoints*.

- *Permadeath*: a crewmate scrapped in a mission is gone for good. Bring them again and they are scrapped right
  after deployment. The Captain is exempt (the story needs them). Stored in your save; loading a checkpoint
  undoes a death, so pair it with *No checkpoints*.
- *One life*: losing a combat mission ends the run — defeat, Abort or quitting it all count (so does a crash
  mid-mission). The next mission you start shows GAME OVER, every combat mission gets locked and a "Game over"
  quest appears: start a new game. The run state is kept in `swh2_hardcore_runs.txt` next to your saves.

**Mutators** (whole game, every mission, including procedural ones)
- *Crew have 1 HP*: HP from level-ups, armor items and weapons is removed too. Armor still works.
- *Enemies have 1 HP*: bosses and enemies whose script phases depend on HP keep theirs.
- *Every shot explodes*: every gun, yours and the enemies', gets a small blast on impact (1 damage per bullet,
  radius in the settings).
- *Bots explode when scrapped*: crew and enemies alike — chain reactions included.

Changing no-grind settings mid-save is fine; missions you already won get locked as soon as the mod is installed.

## Install
Unpack into the game folder (`SteamWorld Heist 2\Mods\Challenges\mod.json`) or use Vortex, then enable it in
SWH2 Mod Loader, choose the challenges and click **Apply to game**.
