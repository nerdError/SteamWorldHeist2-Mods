# NE More Save Slots — SteamWorld Heist 2

Requires **NE Mod Loader & Manager** 1.2+ and the current Steam or GOG version of the game.

The main menu shows more save slots: 4–10 instead of 3 (setting in the loader, default 6).
With more than 6 the slot buttons get shorter so they fit on the screen.

Turning the mod off (or lowering the number) doesn't delete anything: saves in the extra slots
(`savegame_003.dat` and up in `%APPDATA%\Thunderful\SteamWorld Heist II\<id>\Save000\`) stay on disk,
the game just doesn't show them until the mod is back on.

## How it works
Patches `SteamWorld Heist 2.exe`: the size of the slot list, the three loops over it and the limit on the current slot number. The loader keeps a
backup of the original exe, checks the exe version before patching and rebuilds from the backup on every install.
If the game is a different build, the mod refuses to install instead of breaking anything.

Uninstall: untick it and click Apply, or *Restore original files*. Steam "Verify integrity of game files" also restores the exe.

## Install
Unpack into the game folder (`SteamWorld Heist 2\Mods\MoreSlots\mod.json`) or use Vortex, then enable it in NE Mod Loader & Manager and click **Apply to game**.

---

# NE More Save Slots — по-русски

Нужен **NE Mod Loader & Manager** 1.2+ и текущая Steam- или GOG-версия игры.

В главном меню больше слотов сохранения: 4–10 вместо 3 (настройка в загрузчике, по умолчанию 6).
При количестве больше 6 кнопки слотов становятся ниже, чтобы поместиться на экране.

Выключение мода (или уменьшение числа) ничего не удаляет: сохранения в лишних слотах
(`savegame_003.dat` и дальше в `%APPDATA%\Thunderful\SteamWorld Heist II\<id>\Save000\`) остаются на диске,
игра просто не показывает их, пока мод снова не включён.

## Как это работает
Патчит `SteamWorld Heist 2.exe`: размер списка слотов, три цикла по нему и ограничение на номер текущего слота. Загрузчик хранит
бэкап оригинального exe, проверяет версию перед патчем и при каждой установке собирает заново из бэкапа.
Если сборка игры другая, мод откажется устанавливаться, а не сломает игру.

Удаление: снимите галочку и нажмите Apply или *Restore original files*. Steam → «Проверить целостность файлов» тоже восстановит exe.

## Установка
Распакуйте в папку игры (`SteamWorld Heist 2\Mods\MoreSlots\mod.json`) или через Vortex, включите мод в NE Mod Loader & Manager и нажмите **Apply to game**.
