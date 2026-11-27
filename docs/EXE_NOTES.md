# SteamWorld Heist 2.exe — заметки по реверсу

Сборка: `SteamWorld Heist 2.exe`, 15.7 MB, PE x64, MSVC, timestamp 2024-10-04. Игра больше не обновляется,
поэтому RVA ниже стабильны. Адрес в памяти = база модуля (по умолчанию `0x140000000`, но есть ASLR) + RVA.

Инструменты: `tools/re/` (Python + capstone + pefile, без IDA/Ghidra):
- `pe.py` — загрузка exe, поиск строк, xref'ы (RIP-relative, call, указатели), RTTI → vtable.
- `show.py <rva> [n]` — дизассемблер с подписями строк.
- `lua_bindings2.py` — извлекает таблицу Lua-функций → `lua_bindings.tsv` → `docs/LUA_API.md`.

## Общее
- **Нет DRM-обёртки** (нет секции `.bind`); только `steam_api64.dll`.
- **RTTI сохранён**: ~460 классов `imf::*` (движок Image & Form). Символов (pdb) нет.
- Пути исходников в строках: `c:\jh\ws\heist_2\heist_2_win_upload\framework\code\...`.
- **Lua 5.4 вкомпилирован статически.** `lua_pushlstring` = `0x85a6a0`, `lua_newuserdatauv` = `0x85b620`,
  `lua_pushcclosure` = `0x85a7f0`, `lua_settable/rawset` ≈ `0x85ae20`.

## Регистрация Lua API
Три функции-регистратора (вызываются при создании окружения скрипта):

| RVA | Контекст | Пример функций |
|---|---|---|
| `0x26f2f0` | общие | `StartConversation`, `FindEntityWithName`, `GiveItem`, `RemoveItem`, `SetCameraTarget` |
| `0x32a700` | тактика (миссии) | `SetActorHealth`, `AddModifier`, `EndMissionInVictory`, `SpawnActorsAtEntrance`, … (167 шт.) |
| `0x6735e0` | карта мира | `SpawnMission`, `GetEnemyShips`, `MoveWorldMapActorTo`, `SetHomeBar` |

Два шаблона регистрации: (1) `pushlstring(name)` → userdata с указателем на C++-функцию → `pushcclosure(трамплин)`;
(2) `string_view{name,len}` + `lea r8, impl` + вызов шаблонного хелпера (по одному на сигнатуру).
Итого 240 привязок (226 уникальных имён), полный список — `docs/LUA_API.md`. 41 функция в скриптах игры не используется.

Базовые функции (`0x5e8910`): `print`, `LogInfo/LogWarning/LogError`, `RandomInt`, `RandomFloat`, `Color`,
`proxy`, `sandbox`, `require`.

## Lua-окружение и песочница
- Lua-state создаётся в `0x274c50`. **Открываются ВСЕ стандартные библиотеки** (инлайновый `luaL_openlibs`,
  таблица `0xb7ae40`): `_G, package, coroutine, table, io, os, string, math, utf8, debug`.
- `Lua/System/system.lua` выполняется в **глобальном** окружении (там `io`/`os` доступны) и экспортирует имена в
  песочницу скриптов вызовом `sandbox("name")`. Скрипты миссий работают в песочнице.
- Свой `require("X")` (`0x5e8550`) грузит `Lua/X.lua` из бандла (т.е. из `Game.pak`). `require_global("X")` — то же,
  но в глобальном окружении (используется в system.lua: `require_global("System/vector")`).
- **Проверено в игре (мод LuaProbe, `tools/mods/make_lua_probe.py`):** свой `Lua/Mod/probe.lua`, подключённый из
  `system.lua` через `require_global`, грузится; в нём есть `io`/`os`/`debug`/`package`/`load`, запись в файлы
  работает, cwd = папка игры. `sandbox("ModProbe")` делает таблицу видимой в скриптах миссий.
  Обёртки колбэков, дописанные в конец скрипта миссии, вызываются (`OnMissionStart`, `OnUpdate`,
  `OnFactionTurnBegin/End(faction)`; faction игрока = `"cowbots"`).
- Lua-state пересоздаётся при запуске игры и при каждом входе в миссию/на базу (`system.lua` выполняется заново);
  глобальные переменные между миссиями НЕ сохраняются — состояние хранить в файле.
- Игровые функции (`SpawnWorldText`, `AddModifier`, …) в глобальном окружении **не видны** — только в песочнице
  скрипта. Глобальный модуль может получать их аргументом из скрипта миссии.
- **Белый список песочницы** (функция `0x7e8e0`): `tostring tonumber pairs ipairs next select type`,
  `table.{insert remove sort move concat pack unpack}`, `math.*` (без `random`/`randomseed`), `string.*`
  (без `dump`), `coroutine.*`; плюс от движка `print LogInfo LogWarning LogError RandomInt RandomFloat Color`
  и всё, что экспортировано через `sandbox()`. **Нет `pcall`, `error`, `setmetatable`, `io`, `os`, `_G`.**
  Ошибка в обёртке ломает родной колбэк миссии — весь код мода в миссии гнать через `pcall` глобального модуля
  (`ModProbe.safe`), а родной колбэк вызывать безусловно.
- `io.popen`/`os.execute` открывают окно консоли — не использовать.

## Локализация
- Загрузчик (`0x729bc0`) читает `Language/<lang>.csv`, затем все `Language/<lang>_*.csv`. Таблица строк — глобал
  `0xe9fbe8`, поиск ключа `0x729970`. Язык игры — `settings.xml` → `Language.Custom`.
- **Проверено в игре:** новый файл `Language/<lang>_mod.csv` подхватывается — лучший способ добавлять свои
  строки, не трогая файлы игры. Ключ, вставленный в начало `<lang>.csv`, тоже работает; ключ, дописанный
  в САМЫЙ КОНЕЦ `en.csv`, не нашёлся (причина не выяснена).
- `SpawnWorldText(entity, "worldtext_speak_important", key)` — реплика над персонажем; `key` — ключ локализации,
  при отсутствии показывается `!missing line` (сырой текст не выводится).

## Отладочное меню / читы
- Конфиг `Controls/default_controls.cfg` содержит бинды `cheat_menu = Key.Tilde`, `cheat_log = F11`, `cheat_hud = F10`,
  `cheat_noclip = F9`, `cheat_reload_assets = F8`, `cheat_speed_up = Z`, …
- В exe строки `cheat_*` используются **только** статическими инициализаторами глобальных строк (`0x90640…0x907c0`)
  и их деструкторами — код обработки этих действий **вырезан из релиза**. Строк самих читов (god mode, win mission,
  money…) в exe нет.
- Осталась инфраструктура: объект отладки (ctor `0x717fb0`, грузит `UI/debug.me`, шрифт `debug_mono`) создаётся
  всегда (`0x7e1d1c`, хранится во framework `+0x68`); базовый класс `imf::DebugMenu` (vtable `0xbc19d8`)
  используется только `imf::DLCDebugMenu` (vtable `0xbc13b8`).
- Вывод: «включить скрытое чит-меню» нельзя — его нет. Вместо него сделан свой: мод CheatMenu + окно в GUI
  (см. MODDING.md → «Чит-меню»).

## Система модов/DLC
- `imf::ModHandler` → `imf::DesktopModHandler` (создаётся в `0x7f84a0` при монтировании `Bundle/Core`).
- Моды создаются из `Definitions/dlc.xml` (`0x719d00`: «Created mod(s) from {} DLC definitions»). Формат
  (закомментированный пример в файле): `<DLC Name=…><ModId>GUID</ModId><NativeDLCId>…</NativeDLCId>
  <StrictSaveCompatibility>true</StrictSaveCompatibility></DLC>`.
- `0x7e8710` — активация мода («Can't activate non-existent mod {}!»), `0x71a670` — монтирование
  («Mounting mod {} at location {} as bundle {}...»). Как именно проверяется владение DLC и откуда берётся
  location — не исследовано. Для наших целей не нужно: мы и так пересобираем `Game.pak`.

## Скорость боя (патч `turbo`, `tools/re/exe_patch.py`)
- `0x480a10` — обновление тактического мира за кадр (`rcx`→`rsi` = мир, `rdx` = состояние игры, `r8` = &dt).
  Считает масштаб времени (1.0; при slow-mo — интерполяция к `Multiplier`), пишет его в `game+0x1b70`
  (`0x480b77`), при паузе — 0; затем `dt * game[0x1b70]` идёт во всю симуляцию (анимации, движение, снаряды).
  Ещё читают `+0x1b70`: `0xc26c8` (главный цикл), `0xc3b0f`, `0xc8c7f`.
- `imf::TimeSimulator` (0x20 байт): `+8` мир, `+0x10` множитель slow-mo, `+0x14` длительность, `+0x18` прошло.
  Кэш в мире: `world+0x4b0`. Включение slow-mo: `0x151ed0(sim, mult, duration)` (смерть — `0x430600`,
  шляпа — `0x4647a0`).
- Мир: системы в `world+8+idx*8`; индекс камеры — static `0xea18e0` (задаётся в конструкторе мира `0x47e720`).
  `IsCameraSequenceActive` = `camera+0xac || camera+0xdc`.
- Состояние игры: текущая фракция `game+0x1ef8` (int, индекс), имена фракций `game+0x1ee0..0x1ee8`
  (`std::string` по 32 байта); игрок — `"cowbots"`. `IsCutsceneSkipRequested` = `game+0x1ce8`.
- Последовательности (`imf::Sequence*`, 34 класса): `SequenceSimulator` (0x38 байт, индекс системы — static
  `0xe9fbbc`, update `0x57bb10`) держит в `+0x10` указатель на vector<unique_ptr<Sequence>>; за кадр
  обрабатывается только ПЕРВАЯ (текущая). `Sequence`: `+8` Segment, `+0xc` Flags (`DisallowGameActions=1`,
  `CutsceneSkippable=2`, `SingleSkippable=8`, `Letterbox=0x10`, `CameraTargetPanCentered=0x20`,
  `CameraTargetPanOnScreen=0x40`, …, `Done=0x40000`); vtable[1] = GetName. `SequenceWait`: таймер `+0x68`.
  vtables: EnemyIntro `0xb9edc8`, TurretIntro `0xba3388`, Reinforcements `0xb9e870`, Conversation `0xb802b0`,
  IntermissionCutscene `0xb9e640`, PlayMovie `0xb803b8`, Victory `0xb9e8e0`, Defeat `0xb9e558`,
  Projectile `0xb9e7e0`, CameraPan `0xb80218`, ScriptedCameraPan `0xb80378`, Generic `0xb802f8`, Wait `0xb943a8`.
- Флаг «показ камеры» (`camera+0xac/+0xdc`) включён и в катсценах, и при показе результата своего выстрела —
  для выбора скорости не годится (v1 патча ускорял их ×6).
- Тайминги в данных (`game_constants.xml`): `IntroCameraLingerTime` 0.4 / `IntroCameraLingerTimeFinal` 0.5
  (задержка камеры на новых врагах), `ProjectileCameraLingerTime` 1.0, `ProjectileCameraPanTime` 0.3.
- Патч `turbo` (v2): новая секция `.swh2` (RX, 4 КБ, VA `0xf2a000`, дописана в конец файла; NumberOfSections
  и SizeOfImage обновлены), перехват записи `0x480b77` → `jmp` туда. Логика: текущая последовательность —
  катсцена (флаги `0x12`) / Conversation / IntermissionCutscene / PlayMovie / Victory / Defeat → ×1;
  EnemyIntro / TurretIntro / Reinforcements → `×intro`; иначе ход не-`"cowbots"` → `×enemy`; иначе ×1.

## Быстрый старт (мод QuickStart)
- Ролик-логотип: `LogosSystemState` (vtable `0xb948a0`), Update `0x279840` (слот 4) играет ролики из таблицы на 1
  элемент (`0xb77420` → `Movies/logo_thunderful`, сам файл — loose `Bundle/Core/Movies/*.webm`); когда индекс
  `[this+0x18]` дошёл до числа — `0x2799c3`: `new MainMenuState` (0x1d8 байт, конструктор `0x5ecab0`, 4-й аргумент
  r9b → `[this+0xa1]`, отсюда 0) и смена состояния. Патч: `cmp rax, 1` (`0x279894`) → `cmp rax, 0`. **Работает.**
- Автозагрузка (НЕ сделана): `MainMenuState` (vtable `0xbb5588`), метод 8 = `0x5eddd0`; в конце:
  `if ([this+0xa0] || (byte [0xea52a7] && ![this+0xa1])) [this+0xa0] = LoadingGame(this)` (`0x5eebe0`, лог
  «Loading game»: профиль `Session.CurrentProfile` (settings.xml) = последний слот, проверка состояния профиля
  `[0xea5178 + idx*0x108] == 1`, ставит задачу через глобальный объект `[0xea1ba8]`->vt[1] и `[this+0xa2] = 1`).
  Флаг `0xea52a7` в релизе никто не пишет (единственный читатель — эта проверка). Проверено в игре: метод 8
  вызывается ОДИН раз при входе в меню; вызов загрузки там → вечный чёрный экран; счётчик «на N-м вызове» не
  срабатывает. Нужна точка, которая вызывается каждый кадр главного меню (его Update), или вызов загрузки позже.

## Точки отката «Load Checkpoint» (патч `no_checkpoints`)
- `GameConstants.SaveRestorePoints` (`game_constants.xml`, по умолчанию 2) парсится в `0x5b3808` → поле `+0x84`
  глобальных констант (`[0xea19d0]`). Сохранение точки (`0xc6c50`) перед добавлением делает
  `while (deque.size() >= SaveRestorePoints) pop_front()` для нескольких очередей (`state+0x580/0x5d8/…`) —
  **значение 0 приведёт к pop из пустой очереди**, минимум через XML — 1 (откат к началу хода остаётся).
- `bool HasRestorePoint(state+0x4b8)` = `0xc0f80`. Вызовы: сама загрузка (`0xc64e0`, режим 3 = restore point),
  меню паузы `system_load_restore_point` (`0x1bd2d7`: `button.SetEnabled(HasRestorePoint())`, кнопка — `menu+0xe8`),
  попап провала цели (`0x3ac704`: false → текст `popup_objective_fail_cannot_load_restore_point`, только «на карту»).
- Патч: `0xc0f80` → `xor al, al; ret` (3 байта). Кнопка серая, попап без отката, загрузка невозможна.
- Сохранение в миссии: `0xccc00` (виртуальный метод состояния игры; «Not safe to save right now, postponing…»)
  вызывает `0xc6c50` — т.е. **каждое сохранение = новая точка отката**; запрос сохранения — `0xcc640`
  («saving is disabled… disableSave cheat»). Lua `SaveCheckpoint()` (тактика, `0x3273f0`) — другое: запрос
  в `state+0xf08` → `0xcfb10`; такие чекпоинты попап провала грузит сам (`0x3ac69d`, `+0xf68`).

## Слоты сохранений (3, зашито в коде)
- Файлы: `%APPDATA%\Thunderful\SteamWorld Heist II\<steamid>\Save000\savegame_{:03d}.dat` (+ `.backup`) — имя
  форматируется по индексу, сам формат на число слотов не завязан.
- Глобальный указатель на массив профилей слотов `0xea5178`, элемент `0x108` байт; выделяется в `0x563760`
  (`new 0x318` = 3×0x108, `mov edx, 3` — конструктор элементов).
- Литерал 3 в циклах: загрузка слотов `0x561ae0` (`cmp r14d, 3` @ `0x561d20`), построение меню `0x5f0370`
  (`cmp ebx, 3` @ `0x5f0d4e`, на каждый слот `0x5f12d0(this, &slots[i], i)` — ищет виджет `main_menu_profile_slot`),
  `0x5ed2e0` («есть ли хоть одно сохранение», `cmp rcx, 3` @ `0x5ed3cc`). Остальные `cmp [..], 3` / `mov edx, 3`
  в `0x5626f0`/`0x562a20`/`0x5eebe0` — проверки уровня лога (`gs:[0x58]`) и аргументы логгера, не число слотов.
  Остальной код берёт слот по индексу (`Session.CurrentProfile`, геттер `0x561e10`, сеттер `0x561d70`).
  **Но геттер зажимает индекс в 0..2** (`mov eax, 2; cmp edi, eax; cmovl` @ `0x561f23`): без этого патча слоты 4+
  создаются, но все сохранения пишутся в слот 3 (найдено в игре).
- Меню: `UI/ui.me`, панель `save_slots_list` (flow-vertical); три `Instance` в разметке — только превью редактора,
  код очищает панель (`0x74d400`) и создаёт по шаблону `main_menu_profile_slot` кнопку на каждый слот
  (кнопка 140 + отступ 50, экран 2160 строк).
- Реализовано в `Mods/MoreSlots` (7 патчей; при 7+ слотах кнопки 100 + 20).

## Мультиплеер: внутренности тактики (2026-11-27)
Подробно и с планом — [MULTIPLAYER.md](MULTIPLAYER.md). Кратко:
- `ActorSimulator` (`actorsimulator.cpp`): `RequestAction(sim, actor, actionIndex, Target*, flag)` `0x38b9b0` (20 мест
  вызова: игрок, ИИ, Lua; `Target` = `std::variant` из 6 простых типов — MULTIPLAYER.md, «Команда»), выполнение
  `0x391e30` (событие ActionStarted + «Pre-simulating outcome»), обновление очереди `0x3896d0` из `0x480a10`.
- `PlayerTurnSimulator` (`playerturnsimulator.cpp`, `0x455eb0`…): выбор бота/действия, подтверждение по типу цели `0x458780`.
- ИИ: исполнитель приказов `0x43be40`. `OutcomeSimulator` (`outcomesimulator.cpp`): `0x455060` крутит `0x480a10`
  с фиксированным шагом `System.UpdatesPerSecond` (1/60) на копии состояния.
- ГСЧ — MWC Марсальи, один на игру: `[0xea1b98]` → `{z, w}`, засев от времени при запуске (`0x7e11a0`).
- Колбэки Lua: таблица имён `0xb785d8`, подписка на события мира — `0x470ec0`.
- Ввод: геймпады опрашиваются все в одном цикле `0x7f4d18`, события SDL — `0x7f9520` (мод Coop, `native/swh2coop.c`).
