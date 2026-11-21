# SWH2 Mod Loader — SteamWorld Heist 2

A mod manager and modding framework for SteamWorld Heist 2. It installs mods safely, lets several mods work
together, and can always put the original game files back.

The game keeps all its content in `Bundle\Core\Game.pak` / `Game2.pak` (ZIP archives with XML, Lua and assets).
The loader keeps backups of the originals and **rebuilds the game files from those originals on every Apply**, so
changes never stack and a broken install is one click away from vanilla.

## Features
- Mod list: enable/disable, version, author, description; drag & drop a mod `.zip` to install or update it; remove.
- Per-mod settings, shown as a form (checkboxes, numbers, choices, seeds) built from each mod's `mod.json`.
- Several mods can edit the same XML file: edits are combined. The log warns when one mod replaces a file another changed.
- Exe mods: patches to `SteamWorld Heist 2.exe` are checked against the known game build and the original bytes;
  two mods patching the same code is reported as a conflict instead of crashing the game.
- **Restore original files**: paks and exe back to vanilla. Steam's *Verify integrity of game files* also works.
- Mods can add their own windows (buttons next to *Apply*) and `swh2` commands — e.g. the Cheat Menu.
- `swh2` command-line tool with the same features (for scripts and power users).

## Requirements
- SteamWorld Heist 2 (Steam, Windows 10 1903+ or Windows 11).
- Nothing else: the loader runs on .NET Framework 4.8, which is part of Windows.

## Install
1. Unpack the archive into the game folder (Steam → right-click the game → Manage → Browse local files).
   You get `SteamWorld Heist 2\SWH2ModLoader\SWH2ModLoader.exe` and a `Mods` folder.
2. Download mods and unpack them into the game folder too (`SteamWorld Heist 2\Mods\<Mod>\mod.json`),
   or drop their `.zip` on the mod list.
3. Run `SWH2ModLoader.exe`, tick the mods, adjust settings, click **Apply to game**. The game must be closed.

Start a **new save** to see every change of content mods (randomizers etc.); places you already visited keep their state.

To uninstall everything: **Restore original files**, then delete `SWH2ModLoader` and `Mods`.

## Folders
| Path | What |
|---|---|
| `Mods\<Id>\` | installed mods |
| `SWH2ModLoader\Backup\` | original `Game.pak`, `Game2.pak`, exe — don't delete while mods are installed |
| `SWH2ModLoader\profile.json` | enabled mods and their settings |
| `SWH2ModLoader\Reports\<Mod>\` | logs written by mods (e.g. randomizer spoilers) |

## Command line
```
swh2 mods                               list mods and settings
swh2 enable Randomizer Turbo            (disable ...)
swh2 set Randomizer seed=123 damage=on
swh2 install                            apply the profile
swh2 install --dry-run out              write the result to a folder instead of the game
swh2 restore
```

## Making mods
See `MAKING_MODS.md` (in this folder / on GitHub). A mod can be plain edited game files (no code), or a small
C# plugin that edits the game data or patches the exe, with settings the loader shows automatically.
