# NE Remote Play Coop — SteamWorld Heist 2

Requires **NE Mod Loader & Manager** 1.1+.

Play missions together with 2–4 friends. The game stays single-player: one person (the host) runs it and shares it
over **Parsec**; everyone plays from their own gamepad (or one player from the keyboard + mouse). The mod splits the
crew between the players:
- after deployment, at the start of every player turn and when a bot is selected, the bot shows its owner's name;
- **input lock**: while a bot is selected, only its owner's device controls it. Everyone can still switch bots
  (LB/RB, Q/E/Tab, 1–6), move the camera (right stick, arrows), pause (Start, Esc) and open the map. A press on someone
  else's bot shows whose it is. Cutscenes, dialogue, menus (also the pause menu), tutorial popups and the world map are open to everyone.

So in a turn: whoever's bot is selected plays it; to play yours, switch to it with LB/RB (or Q/E/Tab).

## Settings
- **Number of players** and their **names** (shown in the game).
- **Player N crew** (optional): comma-separated crewmates for that player, e.g. `captain, daisy`. Names:
  captain, daisy, wesley, judy, crow, diver, poe, cornelius, adventure_boy, chimney, cyclop.
  Bots not listed are dealt out in turn, starting with the player who has the fewest. The owners chosen in a mission
  stay until it ends, also after quitting and continuing it.
- **Player N plays with**: keyboard + mouse, gamepad 1–4 or any device. Gamepads are numbered in the order they were
  connected: the host's own gamepad (if plugged in) is gamepad 1, then the guests' in the order they joined Parsec.
  `%APPDATA%\Thunderful\SteamWorld Heist II\swh2_coop_input.log` lists every gamepad the game sees with its number.
- The input lock patches the game exe (current Steam and GOG versions); turn it off to only get the name labels.

## Playing together
1. Host: unpack into the game folder (`SteamWorld Heist 2\Mods\RemoteCoop\mod.json`) or use Vortex; tick
   *NE Remote Play Coop* in the loader, enter the names and devices and click **Apply to game**.
2. Everyone installs **Parsec** (free, parsec.app). The host starts the game from Steam and shares the computer
   (Parsec → Share, or add friends); guests connect. In the guest's permissions allow **gamepad** (keyboard/mouse are
   not needed: the host's keyboard and mouse belong to the keyboard + mouse player).
3. Only the host needs the game and the mods.

Steam Remote Play Together works the same way if Steam ever offers it for this game (it doesn't now).

Works with the other NE mods (Randomizer, Challenges, Turbo, CheatMenu…). The ship, bars and shops aren't affected.
Logs: `swh2_coop.log` (owners, Lua errors) and `swh2_coop_input.log` (input lock) in
`%APPDATA%\Thunderful\SteamWorld Heist II\`.


For real network play (everyone runs their own game) use **NE Co-op** (mod Coop) instead; the two can't be on together.

---

# NE Remote Play Coop — по-русски

Нужен **NE Mod Loader & Manager** 1.1+.

Миссии вместе с 2–4 друзьями. Игра остаётся одиночной: один игрок (хост) запускает её и показывает остальным через
**Parsec**, а каждый играет со своего геймпада (или один — с клавиатуры и мыши). Мод делит команду между игроками:
- после высадки, в начале каждого хода игрока и при выборе бота над ним появляется имя владельца;
- **блокировка ввода**: пока бот выбран, им управляет только устройство его владельца. Переключать ботов
  (LB/RB, Q/E/Tab, 1–6), двигать камеру (правый стик, стрелки), ставить паузу (Start, Esc) и открывать карту могут
  все. Нажатие на чужом боте показывает, чей он. Катсцены, диалоги, меню (и меню паузы), окна обучения и карта мира открыты всем.

То есть в ходе: чей бот выбран, тот им и ходит; чтобы походить своим — переключитесь на него LB/RB (или Q/E/Tab).

## Настройки
- **Number of players** — число игроков, и их **имена** (видны в игре).
- **Player N crew** (необязательно) — боты этого игрока через запятую, например `captain, daisy`. Имена:
  captain, daisy, wesley, judy, crow, diver, poe, cornelius, adventure_boy, chimney, cyclop.
  Остальные раздаются по очереди, начиная с игрока, у которого их меньше. Владельцы в миссии не меняются до её
  конца — и после выхода с продолжением.
- **Player N plays with** — клавиатура + мышь, геймпад 1–4 или любое устройство. Геймпады нумеруются в порядке
  подключения: геймпад хоста (если подключён) — 1, дальше геймпады гостей в порядке входа в Parsec.
  В `%APPDATA%\Thunderful\SteamWorld Heist II\swh2_coop_input.log` записан каждый геймпад, который видит игра, с номером.
- Блокировка ввода патчит exe игры (текущие Steam- и GOG-версии); выключите её, если нужны только подписи.

## Как играть вместе
1. Хост: распакуйте в папку игры (`SteamWorld Heist 2\Mods\RemoteCoop\mod.json`) или через Vortex; отметьте
   *NE Remote Play Coop* в загрузчике, впишите имена и устройства и нажмите **Apply to game**.
2. Все ставят **Parsec** (бесплатно, parsec.app). Хост запускает игру из Steam и открывает доступ к компьютеру
   (Parsec → Share или через друзей), гости подключаются. В правах гостя разрешите **геймпад** (клавиатура и мышь
   не нужны: клавиатура и мышь хоста принадлежат игроку «клавиатура + мышь»).
3. Игра и моды нужны только хосту.

Steam Remote Play Together работает так же, если Steam когда-нибудь включит его для этой игры (сейчас его нет).

Совместим с другими модами NE (Randomizer, Challenges, Turbo, CheatMenu…). Корабль, бары и магазины не затрагиваются.
Логи: `swh2_coop.log` (владельцы, ошибки Lua) и `swh2_coop_input.log` (блокировка ввода) в
`%APPDATA%\Thunderful\SteamWorld Heist II\`.

Для настоящей сетевой игры (у каждого своя копия игры) — мод **NE Co-op** (Coop); вместе с этим модом его включить нельзя.
