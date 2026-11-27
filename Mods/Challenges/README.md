# NE Challenge Manager — SteamWorld Heist 2

Requires **NE Mod Loader & Manager** 1.1+. Works alone or together with the Randomizer.

Optional rules for harder runs; each one has its own switch.

**No grind** — everything farmable becomes a one-time reward:
- *Won missions can't be replayed.* Uses the game's own mission locks via hidden quests, so it's stored in your save.
  Story rematches (e.g. the Fabio fight) stay open until their quest is done.
- *Flagships/artifacts don't respawn* after you loot them, no "ghost" loot, no daily floating loot on the map.
- *World map ships drop nothing.*

**Hardcore**
- *No checkpoints*: "Load Checkpoint" is disabled in missions (pause menu and the objective-failed popup).
  Patches the game exe (current Steam and GOG versions; the loader checks it and keeps a backup).
- *Quitting mid-mission forfeits it*: the game saves missions about once per turn, so quitting and pressing
  Continue would rewind the turn. With this on, continuing a mission from a save sends you back to the world map,
  like Abort. A crash mid-mission costs the mission too. The ship, bars, shops, the tutorial and the prison break
  (which can't be aborted) are not affected. Pairs naturally with *No checkpoints*.

- *Permadeath*: a crewmate scrapped in a mission is gone for good. Bring them again and they are scrapped right
  after deployment. The Captain is exempt (the story needs them). Stored in your save; loading a checkpoint
  undoes a death, so pair it with *No checkpoints*.
- *One life*: losing a combat mission ends the run — defeat, Abort or quitting it all count (so does a crash
  mid-mission). The next mission you start shows GAME OVER, every combat mission gets locked and a "Game over"
  quest appears: start a new game. The run state is kept in `swh2_hardcore_runs.txt` next to your saves.

**Mutators** (whole game, every mission, including procedural ones)
- *Crew have 1 HP*: HP from level-ups, armor items and weapons is removed too. Armor still works.
- *Enemies have 1 HP*: bosses and enemies whose script phases depend on HP keep theirs.
- *Every shot explodes*: every gun, yours and the enemies', gets a small blast on impact (1 damage per bullet,
  radius in the settings).
- *Bots explode when scrapped*: crew and enemies alike — chain reactions included.

Changing no-grind settings mid-save is fine; missions you already won get locked as soon as the mod is installed.

## Install
Unpack into the game folder (`SteamWorld Heist 2\Mods\Challenges\mod.json`) or use Vortex, then enable it in
NE Mod Loader & Manager, choose the challenges and click **Apply to game**.

---

# NE Challenge Manager — по-русски

Нужен **NE Mod Loader & Manager** 1.1+. Работает сам по себе или вместе с рандомайзером.

Необязательные правила для более сложного прохождения; у каждого своя галочка.

**Без гринда** — всё, что можно фармить, становится одноразовой наградой:
- *Пройденные миссии нельзя переиграть.* Используются собственные блокировки миссий игры через скрытые квесты,
  поэтому это хранится в сохранении. Сюжетные реванши (например, бой с Фабио) открыты, пока их квест не выполнен.
- *Флагманы и артефакты не возрождаются* после того, как вы их обобрали; нет «призрачного» лута и ежедневного плавающего лута на карте.
- *Корабли на карте мира ничего не роняют.*

**Хардкор**
- *Без чекпоинтов*: «Load Checkpoint» в миссиях отключён (в меню паузы и в окне проваленной цели).
  Патчит exe игры (текущие Steam- и GOG-версии; загрузчик её проверяет и хранит бэкап).
- *Выход из миссии = отступление*: игра сохраняет миссию примерно раз в ход, поэтому выход и «Продолжить»
  отматывали бы ход. С этой опцией продолжение миссии из сохранения возвращает на карту мира, как Abort.
  Вылет посреди миссии тоже стоит миссии. Корабль, бары, магазины, обучение и побег из тюрьмы
  (из него нельзя отступить) не затрагиваются. Хорошо сочетается с *Без чекпоинтов*.
- *Permadeath*: член экипажа, уничтоженный в миссии, пропадает навсегда. Если взять его снова, он будет уничтожен сразу
  после высадки. Капитан — исключение (он нужен сюжету). Хранится в сохранении; загрузка чекпоинта
  отменяет смерть, поэтому сочетайте с *Без чекпоинтов*.
- *Одна жизнь*: проигрыш боевой миссии заканчивает прохождение — считаются поражение, Abort и выход (и вылет посреди
  миссии тоже). Следующая начатая миссия покажет GAME OVER, все боевые миссии закроются и появится квест
  «Game over»: начните новую игру. Состояние забега хранится в `swh2_hardcore_runs.txt` рядом с сохранениями.

**Мутаторы** (вся игра, каждая миссия, включая процедурные)
- *У экипажа 1 HP*: HP от уровней, брони и оружия тоже убирается. Броня по-прежнему работает.
- *У врагов 1 HP*: боссы и враги, у которых фазы скрипта зависят от HP, сохраняют своё.
- *Каждый выстрел взрывается*: любое оружие, ваше и вражеское, даёт небольшой взрыв при попадании (1 урона за пулю,
  радиус — в настройках).
- *Боты взрываются при уничтожении*: и экипаж, и враги — с цепными реакциями.

Настройки «без гринда» можно менять посреди прохождения; уже пройденные миссии закроются сразу после установки мода.

## Установка
Распакуйте в папку игры (`SteamWorld Heist 2\Mods\Challenges\mod.json`) или через Vortex, включите мод в
NE Mod Loader & Manager, выберите челленджи и нажмите **Apply to game**.
