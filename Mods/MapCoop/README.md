# NE Map Co-op (network, experimental) — SteamWorld Heist 2

Requires **NE Mod Loader & Manager** 1.2+ and the current Steam or GOG version of the game.

**Work in progress, not playable yet.** The goal: the second player watches the host's world map over the network
(each player runs their own copy of the game), later maybe sails their own ship. Network play in missions is
**NE Co-op (network)**; this mod is separate for now and works next to it.

## How it works
Patches `SteamWorld Heist 2.exe` so the game loads `Mods\MapCoop\swh2map.dll`. The loader keeps a backup of the
original exe, checks the exe version before patching and rebuilds from the backup on every install. If the game is a
different build, the mod refuses to install instead of breaking anything.

Uninstall: untick it and click Apply, or *Restore original files*.

## Install
Unpack into the game folder (`SteamWorld Heist 2\Mods\MapCoop\mod.json`) or use Vortex, then enable it in NE Mod
Loader & Manager and click **Apply to game**.

---

# NE Map Co-op (сеть, экспериментальный) — по-русски

Нужен **NE Mod Loader & Manager** 1.2+ и текущая Steam- или GOG-версия игры.

**В разработке, играть пока нельзя.** Цель: второй игрок по сети видит карту мира хоста (у каждого своя копия игры),
позже, возможно, плавает на своём корабле. Сетевая игра в миссиях — мод **NE Co-op (network)**; этот мод пока
отдельный и работает рядом с ним.

## Как это работает
Патчит `SteamWorld Heist 2.exe`, чтобы игра загружала `Mods\MapCoop\swh2map.dll`. Загрузчик хранит бэкап
оригинального exe, проверяет версию перед патчем и каждый раз собирает из бэкапа. Если версия игры другая — мод не
установится, а не сломает игру.

Удаление: снять галочку и нажать Apply или *Restore original files*.

## Установка
Распаковать в папку игры (`SteamWorld Heist 2\Mods\MapCoop\mod.json`) или через Vortex, затем включить в NE Mod
Loader & Manager и нажать **Apply to game**.
