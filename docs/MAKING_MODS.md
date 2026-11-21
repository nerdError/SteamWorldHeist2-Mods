# Making mods for SWH2 Mod Loader

A mod is a folder `Mods\<Id>\` with a `mod.json` and any of:
- `files\` — loose game files in pak layout (`files\Definitions\weapons.xml`, `files\Lua\Missions\x.lua`, new files too).
  Only files that differ from the originals are used, so `files\` may be a full extraction with a few edits.
- a **plugin** dll — C# code that edits content in place, generates content, or patches the exe.

On every Apply the loader starts from the ORIGINAL game files and runs the enabled mods in `order`:
for each mod first its `files\`, then its plugin. Nothing is written to the game unless every mod succeeds.

To see the game data: `swh2 extract C:\SWH2_extracted` (or "Extract game files…" in the GUI).
Data reference for this game: `docs/FILE_MAP.md`, Lua API: `docs/LUA_API.md`, exe notes: `docs/EXE_NOTES.md`.

## mod.json
```json
{
  "id": "MyMod",                       // = folder name; letters/digits, no spaces
  "name": "My Mod",
  "version": "1.0.0",
  "author": "you",
  "description": "Shown in the loader.",
  "url": "https://www.nexusmods.com/...",
  "plugin": "MyMod.dll",               // optional
  "files": "files",                    // optional, default "files"
  "order": 0,                          // data mods ~0, generators ~100, rule changes ~200, exe ~300
  "minLoaderVersion": "1.0.0",
  "patchesExe": false,                 // true if the plugin uses ctx.Exe (shown in the UI)
  "conflicts": ["OtherModId"],
  "settings": [
    { "id": "h1", "type": "header", "label": "Group title" },
    { "id": "enabled", "type": "bool", "label": "Do the thing", "default": true, "help": "Tooltip / hint text" },
    { "id": "amount", "type": "int", "label": "Amount", "default": 30, "min": 5, "max": 90, "step": 5, "enabledBy": "enabled" },
    { "id": "speed", "type": "float", "label": "Speed ×", "default": 1.5, "min": 1, "max": 4, "step": 0.5 },
    { "id": "mode", "type": "choice", "default": "a", "choices": [ { "value": "a", "label": "Mode A" }, { "value": "b", "label": "Mode B" } ] },
    { "id": "name", "type": "string", "default": "" },
    { "id": "seed", "type": "seed", "label": "Seed" }
  ]
}
```
(Comments are allowed.) `enabledBy` greys a setting out while that bool is off. An empty `seed` is filled with a
random one at install time and saved. Values are stored in `SWH2ModLoader\profile.json`.

**Presets**: a `choice` whose options have `"sets": { "otherId": value, ... }` is a preset. Picking such an option
(in the GUI or `swh2 set`) writes those values into the other settings; changing one of them by hand afterwards
switches the preset to its first option without `sets` (e.g. `{ "value": "custom", "label": "Custom" }`). The plugin
just reads the individual settings. Example: Mods/Randomizer/mod.json.

A folder **without** mod.json still works: the whole folder is treated as pak layout (old-style mod).

## Plugin
Class library for **.NET Framework 4.8** (`net48`), referencing `SWH2.Core.dll` from the loader folder **without
copying it** (the loader provides it; a second copy would break type identity). Modern C# works with
`LangVersion latest`; add PolySharp for features like `init`/`required`/ranges. WinForms/Drawing are part of the
framework, so a plugin can show windows without shipping anything:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="SWH2.Core" HintPath="path\to\SWH2ModLoader\SWH2.Core.dll" Private="false" />
    <PackageReference Include="PolySharp" Version="1.15.0" PrivateAssets="all" />
  </ItemGroup>
</Project>
```
(In this repository mods use `Mods/Directory.Build.props`, which does the same with a project reference.)
`SWH2.Compat` (in SWH2.Core) has stand-ins for APIs .NET Framework lacks: `Hex.Parse/ToHex/Sha256`,
`PathEx.GetRelativePath`, `dict.GetValueOrDefault`, `dict.TryAdd`, `str.SplitTrim(',')`.

```csharp
using SWH2.Core;
using SWH2.Modding;

public sealed class MyMod : IModPlugin
{
    public void Apply(ModContext ctx)
    {
        if (!ctx.Settings.Bool("enabled")) return;
        int pct = ctx.Settings.Int("amount");

        // Edit XML in place (only changed values are rewritten; formatting and comments survive).
        foreach (var w in ctx.Content.Xml("Definitions/weapons.xml").Definitions("Weapon"))
            if (w.Element("ShootDamage")?.Element("Amount") is { } a && int.TryParse(a.Value, out var dmg))
                a.Value = (dmg * (100 + pct) / 100).ToString();

        ctx.Report.Line("written to SWH2ModLoader\\Reports\\MyMod\\");
        ctx.Log("Weapons buffed.");
    }
}
```

`ModContext`:
| Member | |
|---|---|
| `Settings` | `Bool/Int/Float/Double/String(id)`, `IsActive(id)` |
| `Content` | the game files: `Xml(path)` (editable `XmlDoc`), `Read/ReadText/Write/WriteText`, `Find(dir, "pattern*.xml")`, `ExistsInOriginal`, `ReadOriginal` |
| `Lua` | `AddGlobalModule(name, source)`, `AppendToMissionScripts(script => block)`, `AddStrings(...)` — stack across mods (see docs/MODDING.md for the sandbox rules: no `pcall` in mission scripts, always call the original callbacks) |
| `Exe` | `Patch(rva, expectedOriginalBytes, newBytes)`, `AddSection(name, size)` + `Write(rva, bytes)`, `ExeImage.Jmp`, `ExeImage.Relocate`. Only the supported Steam build; throws otherwise |
| `Loc`, `Items` | English strings; item catalog (names, kinds, tiers, key items) |
| `Report`, `ReportName` | text saved as a report after install |
| `Summary` | short text shown in the install status |
| `ModContext.StableHash(s)` | deterministic seed hashing (same on every machine) |

### Tool windows and CLI commands (optional)
The plugin class may also implement:
- `IModTool.OpenTool(ModToolContext ctx)` — with `"tool": "Button text"` in mod.json the loader shows a button next to
  *Apply* while the mod is enabled. `ctx.Owner` is the main window (`IWin32Window`), `ctx.Game` the game folder.
  The window may stay open while the game runs (it can't change game files then — talk to your Lua side through files,
  see Mods/CheatMenu).
- `IModCommand.RunCommand(args, ctx)` — with `"command": "name"` (+ `"commandHelp"`) users get `swh2 name ...`;
  print with `ctx.Log`, return the exit code.

Rules of thumb:
- Never move progression items (key items, items named in quest `<Parameter>`s or Lua strings).
- Keep XML edits to attribute values, simple text values and new attributes — adding/removing elements
  falls back to rewriting the whole file (still correct, but noisier).
- Throw an exception with a clear message to abort the install.
- Test without touching the game: `swh2 install --dry-run out` writes the changed files (and the patched exe) to `out`.

## Publishing
Zip with the **game folder as root**: `Mods\MyMod\mod.json`, `Mods\MyMod\MyMod.dll`, `Mods\MyMod\files\...`.
Users unpack into the game folder, install with Vortex, or drop the zip on the loader's mod list.
Mark *SWH2 Mod Loader* as a requirement on the Nexus page.
