# NE Turbo Battles — SteamWorld Heist 2

Requires **NE Mod Loader & Manager** 1.3+ and the current Steam or GOG version of the game.

Cuts the waiting in battles:
- **Enemy turns** run faster (default ×3).
- The camera **introducing enemies, turrets and reinforcements** runs faster (default ×6).
- **Your own turn** can run faster too (setting "Your turn speed", default ×1 = normal).
- Cutscenes, dialogue and victory/defeat always stay at normal speed.

All multipliers are settings in the loader, and also sliders in the game itself: **Options → Game**, at the bottom
(loader setting "Sliders in the game's Options menu", on by default). The loader's values are the starting values;
once you move a slider in the game, the game keeps its own value. Changes apply right away, even mid-battle.

Known limitation: during enemy turns all animations speed up together (hats, idle animations) — the engine has one clock.

## How it works
Patches `SteamWorld Heist 2.exe` (the battle time scale). The loader keeps a backup of the original exe,
checks the exe version before patching and rebuilds from the backup on every install, so it never stacks.
If the game is a different build, the mod refuses to install instead of breaking anything.

Uninstall: untick it and click Apply, or *Restore original files*. Steam "Verify integrity of game files" also restores the exe.

## Install
Unpack into the game folder (`SteamWorld Heist 2\Mods\Turbo\mod.json`) or use Vortex, then enable it in NE Mod Loader & Manager and click **Apply to game**.

---

# NE Turbo Battles — по-русски

Нужен **NE Mod Loader & Manager** 1.3+ и текущая Steam- или GOG-версия игры.

Меньше ожидания в боях:
- **Ходы врагов** идут быстрее (по умолчанию ×3).
- Камера, **показывающая врагов, турели и подкрепления**, работает быстрее (по умолчанию ×6).
- **Свой ход** тоже можно ускорить (настройка «Your turn speed», по умолчанию ×1 — обычная скорость).
- Катсцены, диалоги и победа/поражение всегда идут с обычной скоростью.

Все множители — в настройках загрузчика и ползунками в самой игре: **Настройки → Игра**, в конце списка
(настройка «Sliders in the game's Options menu», по умолчанию включена). Значения из загрузчика — стартовые;
если подвинуть ползунок в игре, игра дальше хранит своё значение. Меняется сразу, даже посреди боя.

Известное ограничение: во время хода врагов ускоряются все анимации сразу (шапки, анимации ожидания) — у движка одни часы.

## Как это работает
Патчит `SteamWorld Heist 2.exe` (масштаб времени в бою). Загрузчик хранит бэкап оригинального exe, проверяет версию
перед патчем и при каждой установке собирает заново из бэкапа, так что патчи не накладываются.
Если сборка игры другая, мод откажется устанавливаться, а не сломает игру.

Удаление: снимите галочку и нажмите Apply или *Restore original files*. Steam → «Проверить целостность файлов» тоже восстановит exe.

## Установка
Распакуйте в папку игры (`SteamWorld Heist 2\Mods\Turbo\mod.json`) или через Vortex, включите мод в NE Mod Loader & Manager и нажмите **Apply to game**.
