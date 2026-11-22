# More Save Slots — SteamWorld Heist 2

Requires **SWH2 Mod Loader** 1.0+ and the current Steam version of the game.

The main menu shows more save slots: 4–10 instead of 3 (setting in the loader, default 6).
With more than 6 the slot buttons get shorter so they fit on the screen.

Turning the mod off (or lowering the number) doesn't delete anything: saves in the extra slots
(`savegame_003.dat` and up in `%APPDATA%\Thunderful\SteamWorld Heist II\<id>\Save000\`) stay on disk,
the game just doesn't show them until the mod is back on.

## How it works
Patches `SteamWorld Heist 2.exe`: the size of the slot list, the three loops over it and the limit on the current slot number. The loader keeps a
backup of the original exe, checks the exe version before patching and rebuilds from the backup on every install.
If the game is a different build, the mod refuses to install instead of breaking anything.

Uninstall: untick it and click Apply, or *Restore original files*. Steam "Verify integrity of game files" also restores the exe.

## Install
Unpack into the game folder (`SteamWorld Heist 2\Mods\MoreSlots\mod.json`) or use Vortex, then enable it in SWH2 Mod Loader and click **Apply to game**.
