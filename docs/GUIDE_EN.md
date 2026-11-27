NE MOD LOADER & MANAGER + MODS — GUIDE
======================================
(Русская инструкция — ниже, после английской.)

What's in this folder
---------------------
  SWH2ModLoader\SWH2ModLoader.exe   <- the ONLY thing you need to run (the mod manager window)
  SWH2ModLoader\swh2.exe            the same from the command line (optional)
  SWH2ModLoader\*.dll, *.config     loader files, don't touch
  Mods\Randomizer\                  randomizer: missions, rewards, shops, enemies, weapons, perks, dialogue
  Mods\Turbo\                       faster enemy turns
  Mods\Challenges\                  challenges: no grind, mutators, hardcore
  Mods\CheatMenu\                   cheat menu for missions
  Mods\MoreSlots\                   more save slots (4–10 instead of 3)
  README.txt                        this file

The mods themselves are not programs: there is nothing to run in their folders.
The loader finds them and applies them to the game.

Install (once)
--------------
1. Open the game folder: Steam -> right-click SteamWorld Heist 2 -> Manage -> Browse local files.
   Usually C:\Program Files (x86)\Steam\steamapps\common\SteamWorld Heist 2
2. Copy the SWH2ModLoader and Mods folders there (replace files). You should get:
     SteamWorld Heist 2\SteamWorld Heist 2.exe
     SteamWorld Heist 2\SWH2ModLoader\SWH2ModLoader.exe
     SteamWorld Heist 2\Mods\Randomizer\mod.json
     ...
   Copying alone doesn't change the game.

Nothing else to install: everything runs on .NET Framework 4.8, which is part of Windows 10/11.

How to use
----------
1. Close the game.
2. Run SteamWorld Heist 2\SWH2ModLoader\SWH2ModLoader.exe
3. On the left is the mod list: tick the mods you want.
   Select a mod in the list to see its settings on the right (seed, options etc.).
   Above the settings: search, "Fold"/"Unfold" (fold/unfold sections; clicking a section header folds
   just that one), "Descriptions" (help under the settings), "Defaults" (reset).
   Settings that differ from the default are highlighted in blue.
   The mouse wheel only scrolls — it can't change a setting by accident.
   The borders between the mod list, the settings and the log can be dragged.
4. Click "Apply to game". The loader backs up the original game files
   and builds the game with the selected mods. The log is at the bottom.
5. Start the game with the "▶ Play" button or from Steam as usual.

To change mods or settings: close the game, change the ticks, "Apply to game" again.
Every Apply rebuilds the game from the originals, so changes never stack on top of each other.

To get the original game back: "Restore original files" (or Steam -> Properties -> Verify integrity of game files).

About the mods
--------------
Randomizer
  - Seed: any number/text. Empty — the loader picks a random one and saves it.
    The same seed + settings = the same game on any PC.
  - Start a NEW save (in an old one only places you haven't visited yet change).
  - Spoilers: SWH2ModLoader\Reports\Randomizer\spoiler_<seed>_<date>.txt
  - Preset Balanced — like the normal game; Chaos — everything mixed up, but beatable. Every part can be
    turned on/off on its own (missions, rewards, key items, shops, enemies, hats, weapons, prices, perks,
    utilities, abilities, crew stats, projectiles, dialogue, barks, names, music, lighting, weapon models,
    crew hats, NPC looks; since 1.4 also: enemy guns and AI, explosive cover, mutator roulette, chaos events,
    golden enemy, regional edicts, starting jobs, personal abilities, random start, sub weapons and upgrade
    costs, loot decks, sounds, effects, voices, mystery items; on the world map: whirlpools, floating loot,
    patrols and ambushes, mines and rocks).
  - The world map stays beatable: a whirlpool only leads to a region the story has already opened on the same
    depth, late-game ships don't show up in early regions, the passages between seas (the way to Atomic City)
    are not touched.
  - Mutator roulette, chaos events, the golden enemy and regional edicts work through Lua in missions:
    what each mission got is in the spoiler log; errors go to
    %APPDATA%\Thunderful\SteamWorld Heist II\swh2_randomizer.log and never break the mission.
  - Quick switches under the preset: "All modes" — Balanced/Chaos for every mode at once (doesn't turn
    anything on or off); "Main" / "Stats" / "Cosmetic" — turn a whole group on/off
    (main: missions, rewards, shops, enemies, enemy hats; stats: weapons, prices, perks, utilities,
    abilities, crew, projectiles, jobs, personal abilities, start, sub, loot decks;
    battles: enemy guns and AI, explosive cover, mutators, chaos events, golden enemy, edicts;
    world map: whirlpools, loot, patrols, hazards;
    cosmetic: All / Light — without dialogue and names / Off).
    If a group was set by hand, its switch shows Mixed.
  - Key items (in Chaos) only move to places the story reaches earlier — the game stays beatable.

Turbo
  - Speeds up enemy turns (default x3) and the enemy/reinforcement intros (x6). Multipliers are in the settings.
  - Patches the game exe, works only with the current Steam version (the loader checks it).

Challenges
  - Every challenge has its own checkbox in the mod's settings.
  - "No checkpoints" patches the game exe.
  - Whole-game mutators: crew have 1 HP, enemies have 1 HP (bosses keep theirs), every shot
    explodes, bots explode when scrapped.
  - Permadeath: a crewmate scrapped in a mission never comes back (the Captain is exempt).
  - One life: defeat, Abort or quitting a mission ends the run — every combat mission gets locked,
    you have to start a new game. A game crash mid-mission counts as a loss too.
    Best together with "No checkpoints" and "Quitting mid-mission forfeits it".

CheatMenu
  1. Enable the mod and click "Apply to game".
  2. A "Cheats…" button appears next to Apply — open the window and keep it open while you play.
  3. Hotkeys (work in a mission while the game has focus):
       Ctrl+Alt+1  win mission              Ctrl+Alt+5  alarm off
       Ctrl+Alt+2  heal crew                Ctrl+Alt+6  invulnerable crew (on/off)
       Ctrl+Alt+3  kill all enemies         Ctrl+Alt+7  freeze enemies (on/off)
       Ctrl+Alt+4  enemies to 1 HP          Ctrl+Alt+8  attacks don't end turn (on/off)
     Give any item — from the window.

More Save Slots
  - 4–10 save slots in the main menu instead of 3 (default 6). With 7+ the slot buttons get shorter
    to fit on the screen.
  - Patches the game exe, works only with the current Steam version.
  - Turned the mod off — saves in the extra slots don't go anywhere (savegame_003.dat and up),
    the game just doesn't show them until the mod is on again.

Save banks ("Save banks…" button)
---------------------------------
A bank is a copy of one save slot outside the game. This way you can keep any number of runs
(different randomizer seeds, hardcore etc.), even without the More Save Slots mod.
  - "Slot → new bank…" — copy the selected slot into a named bank. The slot in the game stays as it was;
    you can delete it in the game and start a new game there.
  - "◀ Bank → slot" — put a bank into the selected slot (the game must be closed). If the slot had
    something in it, that is first saved to an "auto …" bank (the last 20 are kept), so nothing is lost.
  - Banks are in %APPDATA%\SWH2ModLoader\SaveBanks\<steam id>\ — outside the game folder and Steam Cloud.

Folders the loader creates
--------------------------
  SWH2ModLoader\Backup\        original Game.pak, Game2.pak and exe — DON'T delete while mods are installed
  SWH2ModLoader\profile.json   which mods are enabled and their settings
  SWH2ModLoader\Reports\       mod logs (randomizer spoilers)

Full uninstall: "Restore original files", then delete the SWH2ModLoader and Mods folders from the game folder.

Command line (optional)
-----------------------
From the SWH2ModLoader folder:
  swh2 mods                                  list mods and settings
  swh2 enable Randomizer Turbo               enable (disable — turn off)
  swh2 set Randomizer seed=12345 damage=on   change settings
  swh2 install                               = "Apply to game"
  swh2 restore                               = "Restore original files"
  swh2 saves                                 save slots and banks
  swh2 saves store 2 "seed 123"              slot 2 -> bank (load "seed 123" 1 — bank -> slot 1)
  swh2 cheat win                             cheats (swh2 help — all commands)

If something goes wrong
-----------------------
  - "Apply" complains about files in use — close the game.
  - The game updated / broke — "Restore original files", then "Apply to game" again.
  - Totally broken — Steam -> Verify integrity of game files.
