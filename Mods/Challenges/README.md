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

Changing no-grind settings mid-save is fine; missions you already won get locked as soon as the mod is installed.

## Install
Unpack into the game folder (`SteamWorld Heist 2\Mods\Challenges\mod.json`) or use Vortex, then enable it in
SWH2 Mod Loader, choose the challenges and click **Apply to game**.
