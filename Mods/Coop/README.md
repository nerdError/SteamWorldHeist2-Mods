# NE Co-op (network) — SteamWorld Heist 2

Requires **NE Mod Loader & Manager** 1.2+. **Experimental.**

Play missions together over the network: **2 players, each in their own copy of the game** (Steam or GOG, both
with this mod applied). One player hosts, the other joins by IP address.

- The crew is split: 1st crewmate host, 2nd guest, 3rd host… Each player can select only their own (Tab / clicks skip
  the others).
- The host's game decides everything (its moves, the enemies, the dice); the guest's game follows it live and sends
  the guest's moves to the host.
- In the mission: the owners' nicknames over the bots, the crew portraits in the owner's colour (host orange, guest
  blue) and two lines on the screen — the connection ("Host · Bob connected") and whose move it is.

## Playing together
1. Both: unpack into the game folder (`SteamWorld Heist 2\Mods\Coop\mod.json`) or use Vortex; tick
   *NE Co-op (network)* in the loader and click **Apply to game**.
2. Both: select the mod → **Network game…**, enter your **nickname**.
   - Host: **Host a game** (port 27015 unless you change it) → **Start the game**. The window shows your addresses:
     tell the other player one of them.
   - Guest: **Join a game**, enter the host's address (`192.168.1.20` or `26.1.2.3:27015`) → **Start the game**.
3. **The save**: the host starts first (and picks which slot to give, default the most recently saved one). The guest's
   loader then gets that save from the host's running game and puts it into the guest's slot chosen in the window
   (default 3; what was there is kept as a save bank), then starts the guest's game. Both load that slot and enter the
   same mission. The guest's game waits for the host's. The host saved later? The guest closes the game and joins again.

**Through Steam** (both play the Steam copy): the host keeps **Through Steam too** ticked and starts as usual. The
guest doesn't need the loader: in the Steam friends list right-click the host → **Join game** (or accept the host's
invite: the host presses **F8** in the game, or Shift+Tab → friends → Invite). Steam starts the guest's game, the game gets the host's save into slot 3 (what was
there goes to a save bank, `auto coop <time>`) and joins the host's battle. No address, no port, no VPN — Steam
relays the connection. A guest whose game already runs is restarted by it (not during a battle).

Without Steam (GOG copy) the guest has to reach the host's TCP port: forward it on the host's router, or both join a
virtual LAN (Radmin VPN, ZeroTier, Tailscale) and use its address. Windows asks the host once whether the game may
use the network — allow it.

Before anything else the guest's loader checks with the host's game that both have the same Co-op version and the
same game data (same mods with the same settings — Randomizer: the same seed). If not, it doesn't start and says
what differs (the Co-op dll's source version, which game file, both mod sets). Copy the same mod pack build to both
PCs: a Co-op you built yourself from other sources counts as another version. The window keeps your nickname, mode and
address for next time.

**The host goes into the battle first**, the guest joins once the host is in it: the guest's loader gets the host's
save from inside the battle, the guest clicks Continue and lands in the same battle — same crew, same gear, nothing
to pick. Until the guest's game is in the battle the host can't move (the screen says who it waits for). Next
mission: the guest closes the game and joins again (Network game… → Start the game) once the host is in it.

The connection broke (Wi-Fi, VPN)? Nothing to do: the guest's game connects again by itself (the screen shows
"waiting" meanwhile) and goes on where it stopped. Commands the guest gave while it was away are lost — just give
them again.

A network game is for that one start of the game: a normal start (Steam, the loader's Play) plays single-player.

For playing over **Parsec** on one copy of the game (friends without the game) use **NE Remote Play Coop**
(mod RemoteCoop) instead — the two can't be on together.

Logs (`%APPDATA%\Thunderful\SteamWorld Heist II\`): `swh2_net_record.log` (host), `swh2_net_replayed.log` (guest),
`swh2_coop.log`. Developer tests: **Network game… → Developer tests…** or `swh2 nettest` (see `docs/MULTIPLAYER.md`
in the source repository).

In the battle: the owner's name hangs over every crewmate in the owner's colour (setting "Name tags"). When your
crew is done for the turn, the camera goes to the other player's actions (and to the enemies' on the guest). A move
into a cell the other player's crewmate is already going to is refused (the reason shows over your crewmate). One
player leaving the battle or losing the connection is shown in the game's popup window: the host then controls the
guest's crewmates until the guest is back; the guest goes to the main menu and joins the host's next battle by itself.
A guest that joins inside the battle fast-forwards through what the host did meanwhile. While the other player
aims, you see it: their crewmate raises the weapon and shows the sight line (with the same weapon and aim as theirs).
The guest's battle runs in step with the host's (the same time passes between two moves, animations included), so a
shot ends the same in both games; while the host's battle is paused, the guest's waits too.

Not done yet: more than 2 players, Steam invites (instead of an IP address).

---

# NE Co-op (network) — по-русски

Нужен **NE Mod Loader & Manager** 1.2+. **Экспериментальный.**

Миссии вместе по сети: **2 игрока, у каждого своя копия игры** (Steam или GOG, у обоих применён этот мод). Один
создаёт игру (хост), второй подключается по IP-адресу.

- Команда делится: 1-й боец хосту, 2-й гостю, 3-й хосту… Выбрать можно только своих (Tab и щелчки чужих пропускают).
- Всё решает игра хоста (её ходы, враги, кубики); игра гостя повторяет её вживую и отправляет хосту ходы гостя.
- В миссии: ники владельцев над ботами, портреты команды в цвете владельца (хост — оранжевый, гость — синий) и две
  строки на экране — связь («Хост · на связи: Bob») и чей ход.

## Как играть вместе
1. Оба: распакуйте в папку игры (`SteamWorld Heist 2\Mods\Coop\mod.json`) или через Vortex; отметьте
   *NE Co-op (network)* в загрузчике и нажмите **Apply to game**.
2. Оба: выберите мод → **Network game…**, впишите **ник**.
   - Хост: **Host a game** (порт 27015, если не менять) → **Start the game**. В окне — ваши адреса: скажите один
     из них второму игроку.
   - Гость: **Join a game**, адрес хоста (`192.168.1.20` или `26.1.2.3:27015`) → **Start the game**.
3. **Сейв**: хост запускается первым (и выбирает, какой слот отдать; по умолчанию — последний сохранённый). Загрузчик
   гостя забирает этот сейв из запущенной игры хоста, кладёт в выбранный в окне слот гостя (по умолчанию 3; что там
   было — сохраняется в банк) и запускает игру гостя. Оба загружают этот слот и входят в одну миссию. Игра гостя ждёт
   игру хоста. Хост потом сохранился? Гость закрывает игру и подключается заново.

**Через Steam** (у обоих копия из Steam): хост оставляет галочку **Through Steam too** и запускает как обычно.
Гостю загрузчик не нужен: в списке друзей Steam правый клик на хоста → **Присоединиться к игре** (или принять
приглашение хоста: хост жмёт в игре **F8** или Shift+Tab → друзья → Пригласить). Steam запускает игру гостя, игра кладёт сейв хоста в слот 3 (что там было — в
банк сохранений `auto coop <время>`) и входит в бой хоста. Ни адреса, ни порта, ни VPN — связь идёт через Steam.
Уже запущенную игру гостя это перезапустит (не во время боя).

Без Steam (копия GOG) гость должен достучаться до TCP-порта хоста: пробросьте его на роутере хоста или оба зайдите в
виртуальную сеть (Radmin VPN, ZeroTier, Tailscale) и используйте её адрес. Windows один раз спросит хоста, можно ли
игре в сеть, — разрешите.

Сначала загрузчик гостя сверяется с игрой хоста: одна ли версия Co-op и одинаковые ли данные игры (те же моды с теми
же настройками; Randomizer — тот же сид). Если нет — игра не запускается, а окно пишет, что именно отличается
(версия исходников dll Co-op, какой файл игры, наборы модов обоих). Ставьте на оба ПК одну и ту же сборку мод-пака:
Co-op, собранный из других исходников, считается другой версией. Ник, режим и адрес окно запоминает.

**Хост входит в бой первым**, гость подключается, когда хост уже в нём: загрузчик гостя забирает сейв хоста прямо из
боя, гость жмёт «Продолжить» и оказывается в том же бою — та же команда, то же снаряжение, ничего выбирать не надо.
Пока игра гостя не в бою, хост не может ходить (на экране написано, кого ждём). Следующая миссия: гость закрывает
игру и подключается снова (Network game… → Start the game), когда хост уже в ней.

Связь оборвалась (Wi-Fi, VPN)? Ничего делать не нужно: игра гостя сама подключится снова (пока что на экране —
«ожидание») и продолжит с того места. Команды, которые гость дал без связи, теряются — просто дайте их ещё раз.

Сетевая игра — на один запуск игры: обычный запуск (Steam, Play в загрузчике) — одиночная игра.

Для игры через **Parsec** на одной копии игры (у друзей нет игры) — мод **NE Remote Play Coop** (RemoteCoop);
вместе их включить нельзя.

Логи (`%APPDATA%\Thunderful\SteamWorld Heist II\`): `swh2_net_record.log` (хост), `swh2_net_replayed.log` (гость),
`swh2_coop.log`. Тесты разработчика: **Network game… → Developer tests…** или `swh2 nettest` (см. `docs/MULTIPLAYER.md`
в репозитории).

В бою: над каждым бойцом всё время висит имя владельца его цветом (настройка «Name tags»). Когда твоя команда
уже походила, камера показывает действия другого игрока (а у гостя — и врагов). Ход в клетку, куда уже идёт боец
другого игрока, отклоняется (причина — над твоим бойцом). Выход игрока из боя или потеря связи показываются окном
игры: хост управляет бойцами гостя, пока тот не вернётся; гость выходит в главное меню и сам входит в следующий бой
хоста. Гость, вошедший посреди боя, проматывает сделанное хостом ускоренно. Когда другой игрок целится, это видно: его
боец поднимает оружие и показывает линию прицела (как у него самого).
Бой гостя идёт в ногу с боем хоста (между ходами проходит столько же времени, анимации тоже), поэтому выстрел кончается
одинаково в обеих играх; пока бой хоста на паузе, бой гостя ждёт.

Пока нет: больше 2 игроков, приглашений через Steam (вместо IP-адреса).
