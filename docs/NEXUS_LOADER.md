# NE Mod Loader & Manager — SteamWorld Heist 2

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
- Mods can add their own windows (buttons next to *Apply*) and `swh2` commands — e.g. NE Cheat Menu.
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

---

# NE Mod Loader & Manager — по-русски

Менеджер и фреймворк модов для SteamWorld Heist 2. Безопасно ставит моды, позволяет нескольким модам работать
вместе и всегда может вернуть оригинальные файлы игры.

Весь контент игры лежит в `Bundle\Core\Game.pak` / `Game2.pak` (ZIP-архивы с XML, Lua и ассетами). Загрузчик хранит
бэкапы оригиналов и **при каждом Apply собирает файлы игры заново из оригиналов**, поэтому изменения никогда не
накладываются друг на друга, а сломанную установку можно откатить одной кнопкой.

## Возможности
- Список модов: включить/выключить, версия, автор, описание; перетащите `.zip` мода на список, чтобы установить или обновить его; удаление.
- Настройки каждого мода — форма (галочки, числа, выбор, сиды), которая строится по `mod.json` мода.
- Несколько модов могут править один XML — правки объединяются. Если мод заменяет файл, который менял другой мод, в логе будет предупреждение.
- Exe-моды: патчи `SteamWorld Heist 2.exe` проверяются по известной сборке игры и по исходным байтам;
  два мода, патчащих один и тот же код, — это ошибка конфликта, а не падение игры.
- **Restore original files** — паки и exe возвращаются к оригиналу. Steam → *Проверить целостность файлов* тоже работает.
- Моды могут добавлять свои окна (кнопки рядом с *Apply*) и команды `swh2` — например, NE Cheat Menu.
- **Save banks…** — копии слотов сохранения вне игры: прохождений можно держать больше, чем слотов.
- Консольная утилита `swh2` с теми же возможностями.

## Требования
- SteamWorld Heist 2 (Steam, Windows 10 1903+ или Windows 11).
- Больше ничего: загрузчик работает на .NET Framework 4.8, он входит в Windows.

## Установка
1. Распакуйте архив в папку игры (Steam → ПКМ по игре → Управление → Просмотреть локальные файлы).
   Получится `SteamWorld Heist 2\SWH2ModLoader\SWH2ModLoader.exe` и папка `Mods`.
2. Моды тоже распакуйте в папку игры (`SteamWorld Heist 2\Mods\<Мод>\mod.json`) или перетащите их `.zip` на список модов.
3. Запустите `SWH2ModLoader.exe`, отметьте моды, настройте их, нажмите **Apply to game**. Игра должна быть закрыта.

Чтобы увидеть все изменения контентных модов (рандомайзер и т.п.), начните **новое сохранение**; места, где вы уже были, сохраняют своё состояние.

Удалить всё: **Restore original files**, затем удалите папки `SWH2ModLoader` и `Mods`.

## Папки
| Путь | Что |
|---|---|
| `Mods\<Id>\` | установленные моды |
| `SWH2ModLoader\Backup\` | оригинальные `Game.pak`, `Game2.pak`, exe — не удаляйте, пока стоят моды |
| `SWH2ModLoader\profile.json` | включённые моды и их настройки |
| `SWH2ModLoader\Reports\<Мод>\` | логи модов (например, спойлеры рандомайзера) |

## Командная строка
```
swh2 mods                               список модов и настроек
swh2 enable Randomizer Turbo            (disable ...)
swh2 set Randomizer seed=123 damage=on
swh2 install                            применить профиль
swh2 install --dry-run out              записать результат в папку, а не в игру
swh2 restore
```

## Как делать моды
См. `MAKING_MODS.md` (в этой папке / на GitHub). Мод может быть просто изменёнными файлами игры (без кода) или
небольшим плагином на C#, который правит данные игры или патчит exe; настройки загрузчик покажет сам.
