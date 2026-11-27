# Nexus Mods page texts

One file per Nexus page. Each has:
- **Name** — the mod name for the page (every name starts with **NE**, from nerdError).
- **Description** — the short plain-text field (no markup, max 350 characters).
- **Tags** — picked from Nexus' own tag list (fetched from the Nexus API, `legacyTags`), names exactly as on the site.
- **Full description (BBCode)** — ready to paste into the full description editor. It follows Nexus' template
  (Description / Installation instructions / Main features / Requirements / Shout outs).

| Page | File | Archive (`dist/nexus/`) |
|---|---|---|
| NE Mod Loader & Manager | [Loader.md](Loader.md) | `SWH2-ModPack-<ver>.zip` (main file: loader + all mods), `SWH2ModLoader-<ver>.zip` (loader only) |
| NE Randomizer | [Randomizer.md](Randomizer.md) | `Randomizer-<ver>.zip` |
| NE Turbo Battles | [Turbo.md](Turbo.md) | `Turbo-<ver>.zip` |
| NE Challenge Manager | [Challenges.md](Challenges.md) | `Challenges-<ver>.zip` |
| NE Cheat Menu | [CheatMenu.md](CheatMenu.md) | `CheatMenu-<ver>.zip` |
| NE More Save Slots | [MoreSlots.md](MoreSlots.md) | `MoreSlots-<ver>.zip` |

## Permissions → Third-party content
No game files are shipped: every mod edits the player's own game files at install time.
- **NE Mod Loader & Manager** (pack and loader-only file): **yes** — Microsoft's .NET libraries ship next to the loader
  (`System.Text.Json`, `System.Memory`, `System.Buffers`, `Microsoft.Bcl.AsyncInterfaces` etc., MIT license,
  redistribution allowed). Credits:
  `System.Text.Json and its dependency libraries — Microsoft / .NET Foundation (MIT license). PolySharp polyfills — Sergio Pedri (MIT license).`
- **Every mod page**: **no** third-party content. Credits (optional): Randomizer —
  `Key item logic idea: SteamWorld Dig 2 Randomizer (SWD2Randomizer).`

## Requirements (Nexus "Requirements" step, *File-to-file requirements*)
- **NE Mod Loader & Manager** page: none, on both files (it only needs the game; .NET Framework 4.8 is part of Windows).
- **Every mod page**: the mod's file requires the loader-only file `SWH2ModLoader-<ver>.zip` (the optional file),
  not the pack — the pack already contains every mod and could put an older copy over the newer one.
  Minimum loader: 1.1 for NE Randomizer and NE Challenge Manager, 1.0 for the rest.
- When a new loader file is uploaded, update the requirement on the mod files to point to it.
