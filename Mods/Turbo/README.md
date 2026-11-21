# Turbo Battles — SteamWorld Heist 2

Requires **SWH2 Mod Loader** 1.0+ and the current Steam version of the game.

Cuts the waiting in battles:
- **Enemy turns** run faster (default ×3).
- The camera **introducing enemies, turrets and reinforcements** runs faster (default ×6).
- Cutscenes, dialogue, victory/defeat and **your own turn stay at normal speed**.

Both multipliers are settings in the loader.

Known limitation: during enemy turns all animations speed up together (hats, idle animations) — the engine has one clock.

## How it works
Patches `SteamWorld Heist 2.exe` (the battle time scale). The loader keeps a backup of the original exe,
checks the exe version before patching and rebuilds from the backup on every install, so it never stacks.
If the game is a different build, the mod refuses to install instead of breaking anything.

Uninstall: untick it and click Apply, or *Restore original files*. Steam "Verify integrity of game files" also restores the exe.

## Install
Unpack into the game folder (`SteamWorld Heist 2\Mods\Turbo\mod.json`) or use Vortex, then enable it in SWH2 Mod Loader and click **Apply to game**.
