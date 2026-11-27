# SWH2 Mod Loader — notes for AI assistants

Mod loader/manager for SteamWorld Heist 2 (C#, .NET Framework 4.8 — ships with Windows, players install nothing) plus
four mods published separately on Nexus: Randomizer, Turbo (exe), Challenges, CheatMenu (Lua + tool window + CLI command). See README.md for features, docs/MAKING_MODS.md for the mod API.

## Where to look first
- `docs/FILE_MAP.md` — what every game data file does and where content is defined. Read before touching game data.
- `docs/MODDING.md` — START HERE for gameplay mods / exe patches: current state, tools, proven recipes, pitfalls, next steps.
- `docs/EXE_NOTES.md` / `docs/LUA_API.md` — reverse-engineering notes on the exe and the full engine Lua API (tools in `tools/re/`).
- `docs/FILE_INDEX.md` — generated list of every file in the game (regenerate: `python tools/gen_file_index.py`).
- Game install: `C:\Program Files (x86)\Steam\steamapps\common\SteamWorld Heist 2`. Data is in `Bundle/Core/Game.pak` (ZIP).
  To read game files without touching the install: `swh2 extract <dir>` or read the paks with any zip library.

## Architecture
- `SWH2.Core/Core` — game I/O. `GameContent` is a virtual file system over the ORIGINAL paks with an in-memory
  overlay; mods edit `content.Xml("Definitions/x.xml")` (an `XmlDoc` wrapping XDocument) and the changes
  are collected and written by `GameFiles.InstallPak` (via `ZipRewriter`, raw-copying untouched entries).
  `GameFiles` also owns the exe (backup, `InstallExe`, restore); `ExeImage` = in-memory exe that mods patch.
- `XmlDoc.Serialize` patches the original text in place (only changed attribute/text values) — keep edits to
  attribute values, simple text values and new attributes so this stays lossless; adding/removing elements
  falls back to a full XmlWriter rewrite.
- `SWH2.Core/Modding` — mod API: `mod.json` (`ModManifest`, settings schema → GUI form), `IModPlugin.Apply(ModContext)`,
  `ModCatalog` (discover `<game>\Mods\*`, load plugin dlls into own ALC, install from zip), `ModProfile` (profile.json).
- `Pipeline.Install` — runs enabled mods in `order` (loose `files\` then plugin) on one `GameContent`/`ExeImage`,
  then writes paks + exe. Shared by the WinForms GUI (`SWH2.ModLoader`) and the `swh2` CLI.
- `Mods/<Id>/` — one project per mod (props in `Mods/Directory.Build.props`; output folder = ready `Mods\<Id>\`).
  Randomizer modules: `Apply(RandomizerContext)`, use `ctx.RngFor("<module>")` so modules don't affect each other's
  output for the same seed; spoiler lines go to `ctx.Spoiler` (= the mod's report).
- `build.cmd` / `tools/package.ps1` → `dist/SWH2-ModPack-<ver>/` (+ .zip; loader + all mods + `docs/GUIDE_EN.md` + `docs/GUIDE_RU.md` as
  README.txt) and `dist/nexus/*.zip` (archives rooted at the game folder). `build.cmd deploy` (or double-click `deploy.cmd`) copies into the game folder; `build.local.cmd` (git-ignored, from `build.local.example.cmd`) with `AUTO_DEPLOY=1` makes every `build.cmd` run deploy (`build.cmd nodeploy` to skip).
  When adding a mod, add it to `$mods` in package.ps1 and to GUIDE_EN.md + GUIDE_RU.md (all READMEs: English first, then Russian). Unchanged loader/mods are reused from
  `obj/package/<name>/` (source hash: project folder + SWH2.Core + Directory.Build.props); `build.cmd rebuild` ignores it.
  If a mod gets inputs outside its folder, add them to its sources list in package.ps1.
- Everything is `net48` + `LangVersion latest` + PolySharp (root `Directory.Build.props`). Missing BCL APIs: use
  `SWH2.Compat` (`SWH2.Core/Compat.cs`: `Hex`, `PathEx`, dictionary/string helpers) instead of .NET 5+ APIs.
  Plugins load via `Assembly.Load(bytes)` + `AssemblyResolve` (ModCatalog); optional `IModTool` (GUI button) / `IModCommand` (`swh2 <cmd>`).

## Rules
- Never modify the game folder in tests; use `swh2 install --dry-run <dir>` or copy the paks into a fake game folder.
- After changing XML handling run `swh2 roundtrip` (must report 0 different / 0 failed).
- Never move progression items (key items, items referenced by quest `<Parameter>`s or Lua strings) — except key items
  through the Randomizer's opt-in key item logic (`ItemShuffle.PlaceKeyItems`: only to places the `outsets.xml` story
  timeline reaches earlier). Never move missions the story needs (`Story.ProgressionMissions`).
- Lua mods: build from the ORIGINAL pak (backup), run `tools/mods/sim_sandbox.py` before installing; mission sandbox has no pcall.
- Exe patches for players go in mods via `ctx.Exe.Patch(rva, originalBytes, newBytes)` / `AddSection` (see Mods/Turbo).
  `tools/re/exe_patch.py` is only for prototyping; verify a port byte-for-byte against its output. Game must be closed for any install.
- After changing a mod, check determinism: same seed + settings must give byte-identical `--dry-run` output.
