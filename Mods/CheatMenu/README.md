# Cheat Menu — SteamWorld Heist 2

Requires **SWH2 Mod Loader** 1.0+.

The game's own debug menu was removed from the release build. This mod brings cheats back for missions:

| Cheat | Hotkey |
|---|---|
| Win mission | Ctrl+Alt+1 |
| Heal crew | Ctrl+Alt+2 |
| Kill all enemies (may break scripted boss fights) | Ctrl+Alt+3 |
| Enemies to 1 HP | Ctrl+Alt+4 |
| Alarm off | Ctrl+Alt+5 |
| Invulnerable crew (toggle) | Ctrl+Alt+6 |
| Freeze enemies (toggle) | Ctrl+Alt+7 |
| Attacks don't end turn (toggle) | Ctrl+Alt+8 |
| Give any item | from the window |

## Use
1. Unpack into the game folder (`SteamWorld Heist 2\Mods\CheatMenu\mod.json`) or use Vortex.
2. In SWH2 Mod Loader tick *Cheat Menu* and click **Apply to game**.
3. Click **Cheats…** in the loader and keep the window open while you play (hotkeys work while the game has focus).
   Cheats work inside missions; the window shows whether a mission is running.

Command line: `swh2 cheat win`, `swh2 cheat god on`, `swh2 cheat give <item_id>`, `swh2 cheat status`.

## How it works
Lua can't read the keyboard or draw UI, so the window writes commands to
`%APPDATA%\Thunderful\SteamWorld Heist II\swh2_cheats_cmd.txt`; a small Lua module in the game reads them every
few frames during a mission. Toggles are kept in `swh2_cheats_state.txt` and survive restarts; results go to
`swh2_cheats.log`. Clicks made outside a mission expire after 10 seconds.

Works together with other mods: the loader merges every mod's Lua hooks.
