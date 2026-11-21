# SWH2 Mod Loader + моды для SteamWorld Heist 2

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

## Как это устроено

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

## Использование

### GUI (`SWH2ModLoader.exe`)
Список модов (галочка = включён, версия, автор, пометка `exe`), справа — настройки выбранного мода.
**Install .zip…** или перетащить архив мода на список — установка/обновление; **Remove** — удалить мод.
**Apply to game** — собрать и установить; **Restore original files** — вернуть оригиналы.
Справа от кнопок — окна включённых модов (например, **Cheats…** у Cheat Menu).

### CLI (`swh2.exe`)
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

## Сборка и выпуск

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

## Структура проекта

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

## Идеи на будущее

- Перемешивание шляп, стартового экипажа/профессий, стартового оружия; `loot_tables.xml`, цены найма, апгрейды.
- Челленджи: лимит ходов, permadeath, мутаторы на миссию (см. MODDING.md).
- Логика доступности (как в SWD2Randomizer) для перемешивания ключевых предметов.
