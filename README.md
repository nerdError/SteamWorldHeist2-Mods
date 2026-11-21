# SWH2 Mod Loader + mods for SteamWorld Heist 2

[![build](https://github.com/nerdError/SteamWorldHeist2-Mods/actions/workflows/build.yml/badge.svg)](https://github.com/nerdError/SteamWorldHeist2-Mods/actions/workflows/build.yml)

**[English](#english) · [Русский](#русский)**

> Unofficial fan project, not affiliated with Thunderful / Image & Form. No game files are included —
> you need your own copy of the game.

## English

A mod manager / modding framework for **SteamWorld Heist 2** and four mods built on it. Everything targets
**.NET Framework 4.8**, which ships with Windows 10 (1903+) and 11 — players don't need to install anything.

| What | Folder | Nexus archive |
|---|---|---|
| **SWH2 Mod Loader** — the base: mod manager (GUI + `swh2` CLI) and the mod API | `SWH2.Core`, `SWH2.ModLoader`, `SWH2.Cli` | `SWH2ModLoader-<ver>.zip` |
| **Randomizer** — shuffles rewards, enemies, damage, prices (seeded) | `Mods/Randomizer` | `Randomizer-<ver>.zip` |
| **Turbo Battles** — speeds up enemy turns and enemy/reinforcement reveals | `Mods/Turbo` | `Turbo-<ver>.zip` |
| **Challenge Manager** — challenges: "no grind", "no checkpoints" | `Mods/Challenges` | `Challenges-<ver>.zip` |
| **Cheat Menu** — in-mission cheats: a loader window + hotkeys, `swh2 cheat` | `Mods/CheatMenu` | `CheatMenu-<ver>.zip` |

Nexus page texts: [docs/NEXUS_LOADER.md](docs/NEXUS_LOADER.md) and `Mods/*/README.md`.
How to make mods: [docs/MAKING_MODS.md](docs/MAKING_MODS.md).

### How it works

All game content lives in `Bundle/Core/Game.pak` and `Game2.pak` — plain ZIP archives with XML, Lua and assets.
The loader keeps the originals in `SWH2ModLoader\Backup`, and on every **Apply** it rebuilds the game files
**from the originals**, running the enabled mods in order — changes never stack on top of previous installs.

A mod is a folder `<game>\Mods\<Id>\` with a `mod.json` and:
- `files\` — ready-made game files in the pak layout (a code-free mod), and/or
- a plugin (DLL, `IModPlugin`) — edits XML in place, generates content, patches the exe, adds Lua hooks
  (`ctx.Lua`; edits from several mods are merged). A plugin can also add a **window** (a button in the loader,
  `IModTool`) and a **CLI command** (`IModCommand`) — that's how the cheat menu works.

`mod.json` declares the mod's settings (bool / int / float / choice / string / seed / header) — the GUI builds a
form from them, the CLI accepts `swh2 set <mod> key=value`. Enabled mods and their settings are stored in
`SWH2ModLoader\profile.json` (shared by GUI and CLI).

**Safety**
- Untouched files inside a pak are copied byte-for-byte; changed XML is patched in place.
- Several mods can edit the same XML — the edits are merged; if a mod replaces a whole file over other mods'
  edits, a warning is logged. Nothing is written to the game until every mod has run without errors.
- Exe patches apply only to a known build (SHA-256) and verify the original bytes; two mods patching the same
  spot is a conflict error, not a game crash. An exe patched by the old `tools/re/exe_patch.py` is detected and
  replaced (it never overwrites the backup).
- If Steam updates the paks, the backup is refreshed. If all else fails — Steam → *Verify integrity of game files*.
- The old layout (`SWH2Randomizer\`, `SWH2Randomizer\Mods\`) is migrated automatically on the first Apply/Restore.

### Usage

#### GUI (`SWH2ModLoader.exe`)
Mod list (checkbox = enabled, version, author, `exe` marker), the selected mod's settings on the right.
**Install .zip…** or drag a mod archive onto the list to install/update; **Remove** deletes a mod.
**Apply to game** builds and installs; **Restore original files** brings back the originals.
Next to the buttons are the enabled mods' windows (e.g. **Cheats…** for Cheat Menu).

#### CLI (`swh2.exe`)
```bash
swh2 status
swh2 mods                                   # mods, enabled or not, settings
swh2 add Randomizer-1.0.0.zip               # install/update a mod from an archive
swh2 enable Randomizer Challenges           # disable ...
swh2 set Randomizer seed=12345 damage=on damagePct=20
swh2 install                                # install the profile (the game must be closed)
swh2 install --only Randomizer --set Randomizer.seed=1 --dry-run out   # one-off, not saved; installs nothing
swh2 restore
swh2 remove Turbo
swh2 extract C:\SWH2_extracted [--assets]
swh2 roundtrip                              # self-test: XML survives a lossless round trip
swh2 cheat win|heal|...                     # Cheat Menu command (swh2 help lists all mod commands)
```
Game path: the folder the loader sits in (`<game>\SWH2ModLoader\`), else Steam libraries, else `--game DIR`.

### Building and releasing

Requires the .NET SDK (8+; tested on 10) — it also builds the `net48` projects (reference assemblies come from NuGet).
**To build everything, double-click `build.cmd`** (or `build.cmd deploy` to build and copy into the game folder;
`build.cmd deploy "D:\...\SteamWorld Heist 2"` for another folder). Output goes to `dist\`:

| What | Why |
|---|---|
| `dist\SWH2-ModPack-<ver>\` | **ready build**: `SWH2ModLoader\` + `Mods\` (all 4 mods) + `README.txt` (guide, [docs/GUIDE_RU.md](docs/GUIDE_RU.md)). Copy its contents into the game folder |
| `dist\SWH2-ModPack-<ver>.zip` | the same as one archive |
| `dist\nexus\*.zip` | separate archives of the loader and each mod for Nexus |

Only `SWH2ModLoader\SWH2ModLoader.exe` is meant to be run; `deploy` doesn't touch `Backup\`/`profile.json` or the game
files (those change only on **Apply**). Exes in the projects' `bin\` folders are intermediate — ignore them.
```bash
dotnet build SWH2ModLoader.slnx -c Release                    # build only (for development)
powershell -ExecutionPolicy Bypass -File tools/package.ps1    # same as build.cmd
```
All projects are `net48` with `LangVersion=latest` (root `Directory.Build.props`): modern C# works via PolySharp
(compile-time polyfills), missing BCL methods are in `SWH2.Core/Compat.cs` (global `using SWH2.Compat`).
System.Text.Json and its dependencies ship next to the loader (+ binding redirects in `*.exe.config`).
Each mod builds into `Mods/<Id>/bin/Release/<Id>/` — a ready `Mods\<Id>\` folder. Every archive is rooted at the
game folder (`SWH2ModLoader\...`, `Mods\<Id>\...`) — fine for manual extraction and Vortex.
Versions: loader — `<Version>` in `SWH2.Core.csproj`; mod — `version` in its `mod.json`.

### Project layout

| Path | What |
|---|---|
| `SWH2.Core/Core/` | game I/O: `GameInstall` (paths, migration), `GameFiles` (backup/install/restore of paks and exe), `ZipRewriter`, `GameContent` (virtual FS over the paks + tracking which mod changed what), `XmlDoc`, `ExeImage` (exe patches), `LuaPatcher`, `ItemCatalog`, `Localization` |
| `SWH2.Core/Compat.cs` | stand-ins for APIs missing in .NET Framework (`Hex`, `PathEx`, `TryAdd`, `GetValueOrDefault`…) |
| `SWH2.Core/Modding/` | mod API: `ModManifest` (mod.json), `ModSettings`, `IModPlugin`/`ModContext`, `IModTool`/`IModCommand`/`ModToolContext`, `ModCatalog` (discovery, plugin loading, install from zip), `ModProfile` |
| `SWH2.Core/Pipeline.cs` | install / restore / extract / roundtrip — shared by GUI and CLI |
| `SWH2.ModLoader/` | WinForms GUI (mod manager) |
| `SWH2.Cli/` | the `swh2` console tool |
| `Mods/Randomizer/` | `ItemShuffle`, `EnemyShuffle`, `StatRandomizer` |
| `Mods/Turbo/` | battle time-scale patch (code from `exe_patch.py`, ported as bytes + a relocation table) |
| `Mods/Challenges/` | `NoGrind` (XML), `NoCheckpoints` (exe) |
| `Mods/CheatMenu/` | Lua backend and mission hook (`Lua/*.lua`, embedded in the dll), modifiers, `CheatForm` window, `CheatClient` (file-based protocol) |
| `tools/package.ps1` | builds the Nexus archives |
| `tools/re/`, `tools/mods/` | developer tools (exe RE, Lua mod generators, sandbox simulator) |
| `docs/` | `MODDING.md` (work status), `FILE_MAP.md`, `EXE_NOTES.md`, `LUA_API.md`, `MAKING_MODS.md`, `NEXUS_LOADER.md` |

### Ideas for the future

- Shuffle hats, starting crew/jobs, starting weapons; `loot_tables.xml`, hiring prices, upgrades.
- Challenges: turn limit, permadeath, per-mission mutators (see MODDING.md).
- Logic-aware placement (like SWD2Randomizer) so key items can be shuffled too.

### License

[MIT](LICENSE). SteamWorld Heist 2 and all its assets belong to their respective owners; this repository contains no game files.

---

## Русский

Менеджер модов / фреймворк для модов **SteamWorld Heist 2** и четыре мода на нём. Всё на **.NET Framework 4.8** —
он встроен в Windows 10 (1903+) и 11, игрокам ничего не нужно докачивать.

| Что | Папка | Архив для Nexus |
|---|---|---|
| **SWH2 Mod Loader** — основа: менеджер модов (GUI + CLI `swh2`), API для модов | `SWH2.Core`, `SWH2.ModLoader`, `SWH2.Cli` | `SWH2ModLoader-<ver>.zip` |
| **Randomizer** — перемешивание наград, врагов, урона, цен (по сиду) | `Mods/Randomizer` | `Randomizer-<ver>.zip` |
| **Turbo Battles** — ускорение ходов врагов и показа врагов/подкреплений | `Mods/Turbo` | `Turbo-<ver>.zip` |
| **Challenge Manager** — челленджи: «без гринда», «без чекпоинтов» | `Mods/Challenges` | `Challenges-<ver>.zip` |
| **Cheat Menu** — читы в миссиях: окно в загрузчике + горячие клавиши, `swh2 cheat` | `Mods/CheatMenu` | `CheatMenu-<ver>.zip` |

Тексты для страниц Nexus: [docs/NEXUS_LOADER.md](docs/NEXUS_LOADER.md) и `Mods/*/README.md`.
Как делать моды: [docs/MAKING_MODS.md](docs/MAKING_MODS.md).

### Как это устроено

Весь контент игры лежит в `Bundle/Core/Game.pak` и `Game2.pak` — обычные ZIP-архивы с XML, Lua и ассетами.
Загрузчик держит оригиналы в `SWH2ModLoader\Backup` и при каждом **Apply** собирает файлы игры **заново из
оригиналов**, прогоняя включённые моды по порядку — изменения никогда не накладываются друг на друга.

Мод — папка `<игра>\Mods\<Id>\` с `mod.json` и:
- `files\` — готовые файлы игры в структуре пака (мод без кода), и/или
- плагин (DLL, `IModPlugin`) — правит XML на месте, генерирует контент, патчит exe, добавляет Lua-хуки
  (`ctx.Lua`, правки нескольких модов складываются). Плагин может добавить и **окно** (кнопка в загрузчике,
  `IModTool`) и **команду CLI** (`IModCommand`) — так устроено чит-меню.

`mod.json` описывает настройки мода (bool / int / float / choice / string / seed / header) — GUI сам строит по
ним форму, CLI принимает `swh2 set <мод> key=value`. Включённые моды и их настройки — в
`SWH2ModLoader\profile.json` (общий для GUI и CLI).

**Безопасность**
- Неизменённые файлы внутри пака копируются бит-в-бит, изменённые XML правятся точечно.
- Несколько модов могут править один XML — правки объединяются; если мод заменяет файл целиком поверх чужих
  правок — предупреждение в логе. Ничего не пишется в игру, пока все моды не отработали без ошибок.
- Патчи exe: только для известной сборки (SHA-256), с проверкой исходных байтов; два мода на одно место —
  ошибка конфликта, а не падение игры. Exe, пропатченный старым `tools/re/exe_patch.py`, распознаётся и
  заменяется (бэкап не перезаписывается им).
- Если Steam обновил паки, бэкап обновится. Совсем сломалось — Steam → *Проверить целостность файлов*.
- Старая раскладка (`SWH2Randomizer\`, `SWH2Randomizer\Mods\`) переносится автоматически при первом Apply/Restore.

### Использование

#### GUI (`SWH2ModLoader.exe`)
Список модов (галочка = включён, версия, автор, пометка `exe`), справа — настройки выбранного мода.
**Install .zip…** или перетащить архив мода на список — установка/обновление; **Remove** — удалить мод.
**Apply to game** — собрать и установить; **Restore original files** — вернуть оригиналы.
Справа от кнопок — окна включённых модов (например, **Cheats…** у Cheat Menu).

#### CLI (`swh2.exe`)
```bash
swh2 status
swh2 mods                                   # моды, включены ли, настройки
swh2 add Randomizer-1.0.0.zip               # установить/обновить мод из архива
swh2 enable Randomizer Challenges           # disable ...
swh2 set Randomizer seed=12345 damage=on damagePct=20
swh2 install                                # установить профиль (игра должна быть закрыта)
swh2 install --only Randomizer --set Randomizer.seed=1 --dry-run out   # разово, без сохранения; ничего не ставит
swh2 restore
swh2 remove Turbo
swh2 extract C:\SWH2_extracted [--assets]
swh2 roundtrip                              # самопроверка: XML сохраняется без потерь
swh2 cheat win|heal|...                     # команда мода Cheat Menu (swh2 help — все команды модов)
```
Путь к игре: папка, где лежит загрузчик (`<игра>\SWH2ModLoader\`), иначе Steam-библиотеки, иначе `--game DIR`.

### Сборка и выпуск

Требуется .NET SDK (8+; проверено на 10) — он собирает и проекты под `net48` (референс-сборки тянутся из NuGet).
**Просто собрать всё — двойной клик по `build.cmd`** (или `build.cmd deploy` — собрать и сразу скопировать в папку игры;
`build.cmd deploy "D:\...\SteamWorld Heist 2"` — другая папка). Результат в `dist\`:

| Что | Зачем |
|---|---|
| `dist\SWH2-ModPack-<ver>\` | **готовая сборка**: `SWH2ModLoader\` + `Mods\` (все 4 мода) + `README.txt` (инструкция, [docs/GUIDE_RU.md](docs/GUIDE_RU.md)). Содержимое копируется в папку игры |
| `dist\SWH2-ModPack-<ver>.zip` | то же одним архивом |
| `dist\nexus\*.zip` | отдельные архивы загрузчика и каждого мода для Nexus |

Запускать нужно только `SWH2ModLoader\SWH2ModLoader.exe`; `deploy` не трогает `Backup\`/`profile.json` и файлы игры
(они меняются только по **Apply**). Exe в `bin\` папках проектов — промежуточные, их можно игнорировать.
```bash
dotnet build SWH2ModLoader.slnx -c Release                    # только сборка (для разработки)
powershell -ExecutionPolicy Bypass -File tools/package.ps1    # то же, что build.cmd
```
Все проекты — `net48` с `LangVersion=latest` (корневой `Directory.Build.props`): современный C# работает через
PolySharp (полифиллы на этапе компиляции), недостающие методы BCL — в `SWH2.Core/Compat.cs` (глобальный
`using SWH2.Compat`). Рядом с загрузчиком лежат System.Text.Json и его зависимости (+ binding redirects в `*.exe.config`).
Каждый мод собирается в `Mods/<Id>/bin/Release/<Id>/` — это готовая папка `Mods\<Id>\`. Все архивы
имеют корень = папка игры (`SWH2ModLoader\...`, `Mods\<Id>\...`) — подходят для ручной распаковки и Vortex.
Версии: загрузчик — `<Version>` в `SWH2.Core.csproj`; мод — `version` в его `mod.json`.

### Структура проекта

| Путь | Что |
|---|---|
| `SWH2.Core/Core/` | работа с игрой: `GameInstall` (пути, миграция), `GameFiles` (бэкапы/установка/откат паков и exe), `ZipRewriter`, `GameContent` (виртуальная ФС поверх паков + учёт, какой мод что менял), `XmlDoc`, `ExeImage` (патчи exe), `LuaPatcher`, `ItemCatalog`, `Localization` |
| `SWH2.Core/Compat.cs` | замены API, которых нет в .NET Framework (`Hex`, `PathEx`, `TryAdd`, `GetValueOrDefault`…) |
| `SWH2.Core/Modding/` | API модов: `ModManifest` (mod.json), `ModSettings`, `IModPlugin`/`ModContext`, `IModTool`/`IModCommand`/`ModToolContext`, `ModCatalog` (поиск, загрузка плагинов, установка из zip), `ModProfile` |
| `SWH2.Core/Pipeline.cs` | install / restore / extract / roundtrip — общие для GUI и CLI |
| `SWH2.ModLoader/` | WinForms GUI (менеджер модов) |
| `SWH2.Cli/` | консольная утилита `swh2` |
| `Mods/Randomizer/` | `ItemShuffle`, `EnemyShuffle`, `StatRandomizer` |
| `Mods/Turbo/` | патч масштаба времени боя (код из `exe_patch.py`, перенесён байтами + таблица релокаций) |
| `Mods/Challenges/` | `NoGrind` (XML), `NoCheckpoints` (exe) |
| `Mods/CheatMenu/` | Lua-бэкенд и хук миссий (`Lua/*.lua`, встроены в dll), модификаторы, окно `CheatForm`, `CheatClient` (файловый протокол) |
| `tools/package.ps1` | сборка архивов для Nexus |
| `tools/re/`, `tools/mods/` | инструменты разработчика (RE exe, генераторы Lua-модов, симулятор песочницы) |
| `docs/` | `MODDING.md` (состояние работ), `FILE_MAP.md`, `EXE_NOTES.md`, `LUA_API.md`, `MAKING_MODS.md`, `NEXUS_LOADER.md` |

### Идеи на будущее

- Перемешивание шляп, стартового экипажа/профессий, стартового оружия; `loot_tables.xml`, цены найма, апгрейды.
- Челленджи: лимит ходов, permadeath, мутаторы на миссию (см. MODDING.md).
- Логика доступности (как в SWD2Randomizer) для перемешивания ключевых предметов.

### Лицензия

[MIT](LICENSE). SteamWorld Heist 2 и все её ресурсы принадлежат правообладателям; репозиторий не содержит файлов игры.
