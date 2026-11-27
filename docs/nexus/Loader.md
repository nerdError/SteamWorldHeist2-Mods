# NE Mod Loader & Manager

## Name
```text
NE Mod Loader & Manager
```

## Description (plain text, max 350 characters)
```text
Safe mod manager for SteamWorld Heist 2, shipped with all my mods: Randomizer, Turbo Battles, Challenge Manager, Cheat Menu, More Save Slots. Tick mods, set options, click Apply. Game files are rebuilt from backed-up originals every time, so vanilla is one click away. Save banks and Play button included.
```

## Tags
- Utilities for Players
- Utilities for Modders
- Modder's Resource
- Quality of Life
- Saved games
- English

## Full description (BBCode)
```text
[size=5][b]Description[/b][/size]
NE Mod Loader & Manager installs and manages mods for [b]SteamWorld Heist 2[/b]. It installs mods safely, lets several mods work together and can always put the original game files back.

The game keeps all of its content in two archives ([i]Game.pak[/i] and [i]Game2.pak[/i]). The loader backs up the originals and [b]rebuilds the game files from those originals on every Apply[/b] — changes never stack on top of an earlier install, and a broken install is one click away from vanilla.

[b]The main file already includes all my SteamWorld Heist 2 mods[/b] — NE Randomizer, NE Turbo Battles, NE Challenge Manager, NE Cheat Menu and NE More Save Slots. They are off until you tick them, so you only get what you choose. Each mod also has its own page, for updates and comments.

[size=5][b]Installation instructions[/b][/size]
[list=1]
[*]Open the game folder: Steam → right-click SteamWorld Heist 2 → Manage → Browse local files.
[*]Unpack the [b]main file[/b] (loader + all mods) there. You get [i]SteamWorld Heist 2\SWH2ModLoader\SWH2ModLoader.exe[/i] and a [i]Mods[/i] folder with the mods. (Vortex works too.) The optional file is the loader alone, for those who want to pick mods one by one.
[*]Other mods: unpack them into the game folder as well, or drag their .zip onto the loader's mod list.
[*]Close the game, run [b]SWH2ModLoader.exe[/b], tick the mods, adjust their settings and click [b]Apply to game[/b].
[/list]
Start a [b]new save[/b] to see every change of content mods such as the Randomizer — places you have already visited keep their state.

[b]Uninstall:[/b] click [b]Restore original files[/b], then delete the [i]SWH2ModLoader[/i] and [i]Mods[/i] folders. Steam's [i]Verify integrity of game files[/i] also brings the originals back.

[size=5][b]Main features[/b][/size]
[list]
[*][b]Mod list[/b] — enable/disable, version, author, description; drag & drop a mod .zip to install or update it.
[*][b]Settings form for every mod[/b] — checkboxes, numbers, choices and seeds, with search, foldable sections and a Defaults button.
[*][b]Mods work together[/b] — several mods can edit the same game file and their edits are combined; the log warns when one mod overwrites another's changes.
[*][b]Safe exe patches[/b] — patches to the game exe are checked against the known game build and the original bytes; two mods patching the same code are reported as a conflict instead of crashing the game.
[*][b]Restore original files[/b] — game archives and exe back to vanilla in one click.
[*][b]Save banks[/b] — store any save slot as a named copy outside the game and put it back into any slot later: keep more runs than the game has slots.
[*][b]▶ Play[/b] button to start the game right from the loader.
[*][b]Mod windows[/b] — mods can add their own tool windows (e.g. the Cheats… window of NE Cheat Menu).
[*][b]swh2 command-line tool[/b] with the same features, for scripts and power users.
[*][b]For modders[/b] — a mod can be plain edited game files (no code) or a small C# plugin that edits game data, adds Lua hooks or patches the exe; the loader builds its settings form automatically. See MAKING_MODS.md in the loader folder.
[/list]

[size=5][b]Requirements[/b][/size]
[list]
[*]SteamWorld Heist 2 (Steam version), Windows 10 (1903 or newer) or Windows 11.
[*]Nothing else: the loader runs on .NET Framework 4.8, which is already part of Windows.
[/list]
Source code: [url=https://github.com/nerdError/SteamWorldHeist2-Mods]GitHub[/url] (MIT license). Unofficial fan project, not affiliated with Thunderful or Image & Form.

[size=5][b]Shout outs[/b][/size]
A huge thank you to the original developers of SteamWorld Heist 2 — the team at [b]Image & Form[/b], who were unfairly let go after the game's release. I truly love everything they have made, and this is my tiny contribution to the world they built. Thank you for these games.
```
