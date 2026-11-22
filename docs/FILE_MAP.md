# SteamWorld Heist 2 — карта файлов игры

Обзор «что где лежит и за что отвечает». Предназначен и для людей, и для нейросетей.
Полный автоматический список всех ~3800 файлов — в [FILE_INDEX.md](FILE_INDEX.md)
(перегенерировать: `python tools/gen_file_index.py`).

Версия игры, по которой составлено: Steam-сборка от 2025-08-25 (дата файлов `Game.pak`).

---

## 1. Папка игры

```
SteamWorld Heist 2/
├─ SteamWorld Heist 2.exe        движок Image & Form / Thunderful (C++, SDL2, OpenAL)
├─ SDL2.dll, OpenAL32.dll, steam_api64.dll
├─ Bundle/Core/                  ВЕСЬ контент игры
│  ├─ Game.pak       (9.7 MB)    ZIP: данные — XML-определения, Lua, уровни, локализация, UI
│  ├─ Game2.pak      (45 MB)     ZIP: часть ассетов — атласы, звуки, шейдеры, шрифты, текстуры
│  ├─ Atlases/ Audio/ Characters/ Effects/ Fonts/ Movies/ Textures/ WorldMap/
│  │                             «рыхлые» (loose) файлы: тяжёлые ассеты (музыка .ogg, .ktx, .isc2, .webm)
│  └─ GamePaks/                  НЕ игровая папка — ручные распаковки паков (созданы пользователем)
├─ Mods/                         моды (Mods/<Id>/mod.json …)
└─ SWH2ModLoader/                загрузчик: бэкапы, state/profile.json, отчёты (раньше SWH2Randomizer/)
```

Сохранения и настройки: `%APPDATA%\Thunderful\SteamWorld Heist II\<SteamID>\`.

### Как движок грузит файлы

- При старте монтируются **все `*.pak` в `Bundle/Core`** (в exe строки `*.pak`, `Loaded {} zip entries from {}`).
  `.pak` — обычный ZIP (deflate, без шифрования). Внутри пути вида `Definitions/weapons.xml`.
- Рыхлые папки в `Bundle/Core` **не пересекаются** с содержимым паков (проверено: 0 общих путей из 1502).
  Это отдельные ассеты, а не распакованные копии.
- Поэтому мод = **пересобрать `Game.pak`** с изменёнными файлами (наш инструмент так и делает,
  храня оригинал в `SWH2ModLoader/Backup`).
- В exe есть система модов/DLC (`DLC/Mods`, `Mounting mod {} at location {} as bundle {}`,
  `Definitions/dlc.xml`, «Override definitions…»), но она завязана на DLC-определения Steam
  (`dlc.xml` пуст) — для пользовательских модов не годится.
- Пути в коде: `Core:Lua/System/system.lua` — префикс `Core:` = бандл `Bundle/Core`.

---

## 2. Форматы файлов

| Расширение | Что это | Чем открыть |
|---|---|---|
| `.xml` | Определения игровых сущностей (основной объект моддинга) | любой текстовый редактор |
| `.de` | Тоже XML, но редактируется во внутреннем «Definition Editor» (`missions*.de`, `rooms*.de`, `spawn_*.de`, `mission_rules.de`) | текстовый редактор |
| `.le` | XML уровня из Level Editor (`Patchsets/**`): комнаты, объекты, маркеры | текстовый редактор |
| `.me` | XML интерфейса из Menu Editor (`UI/ui.me` — весь UI) | текстовый редактор |
| `.pe` | XML частиц/эффектов (`Effects/*.pe`) | текстовый редактор |
| `.tmx` / `.tsx` / `.tx` | Карта мира в формате **Tiled** (`WorldMap/Maps/WorldMap.tmx`, 8.6 MB) + тайлсеты/шаблоны | [Tiled](https://www.mapeditor.org/) |
| `.lua` | Скрипты миссий, интерлюдий, карты мира | текстовый редактор |
| `.csv` | Локализация: `ключ<TAB>текст` | текстовый редактор / таблица |
| `.cfg` | Управление по умолчанию (`Controls/`) | текстовый редактор |
| `.isc2` | Бинарная скелетная модель персонажа/оружия/шляпы (сигнатура `ISC`) | — |
| `.ktx` | Текстуры (атласы `_d` diffuse, `_n` normal, `_s` specular) | KTX-тулзы |
| `Atlases/*.txt` | Разметка атласов (какой спрайт где) | текстовый редактор |
| `.wav` / `.ogg` | Звуки (`Audio/Sounds`) / музыка (`Audio/Music`, только loose) | аудиоредактор |
| `.webm` + `.srt` | Ролики и субтитры (`Movies/`) | плеер |
| `.dxbc` / `.spv` | Скомпилированные шейдеры DX11 / Vulkan | — |
| `.fgen` | Описания генерации шрифтов (`Fonts/`) | — |

---

## 3. Общие правила XML-определений

Почти все файлы в `Definitions/` устроены одинаково:

```xml
<WeaponDefinitions>                       <!-- корень = таблица определений -->
  <Weapon Name="SNIPER" Template="PLAYER_WEAPON_BASE" Abstract="1"> … </Weapon>
  <Weapon Name="sniper_01" Template="SNIPER"> … </Weapon>
</WeaponDefinitions>
```

- **`Name`** — ID определения, на него ссылаются другие файлы и Lua.
- **`Template="X"`** — наследование: берутся все поля X, перекрываются своими.
- **`Abstract="1"`** — шаблон, сам в игре не появляется (обычно ИМЕНА_КАПСОМ или `BASE`).
- **`Inherit="false"`** на списке (`<MasterLoot Inherit="false">`) — не дополнять, а заменить список шаблона.
- **Формулы с `x`**: `0.04*x`, `1.15 + 0.05*x + 0.012*x^2` — `x` = уровень угрозы/тир/день (по контексту).
- **Списки по уровням**: `<HitPoints>2,3,3,3,3,4</HitPoints>` — значение для каждой ступени угрозы.
- **Документация в шапке**: многие файлы начинаются с большого `<!-- … -->`, описывающего все поля
  (`weapons.xml`, `outsets.xml`, `worldmap_regions.xml`, `spawn_tables.de`, `key_items.xml`,
  `personas.xml`, `special_items.xml`, `loot_tables.xml` …). **Читайте шапку первой.**
- **Локализация**: имя предмета = ключ из `Language/<язык>.csv`. По умолчанию
  оружие `weapon_<Name>`, остальное `<Name>`, миссия `mission_<Name>`, регион `region_<Name>`,
  персонаж `persona_<Name>`. Явно задаётся `<LocalizedNameId>`.

---

## 4. `Definitions/` — по темам

### Предметы и лут

| Файл | Что внутри | Ключевые поля |
|---|---|---|
| `weapons.xml` | 103 оружия игрока (sniper, smg/crossbow, shotgun, handgun, hammer, rpg/launcher…). В шапке — подробное описание механики выстрела | `Job`, `ShootDamage/Amount`, `CritAmount`, `ShotCount`, `RapidFire`, `Spread`, `Rarity Tier/Type`, `LootCost`, `LootTables` |
| `weapons.navy.xml` / `.pirates.xml` / `.atomics.xml` | оружие врагов | то же |
| `utilities.xml` | 68 утилит (ремкомплекты, гранаты, sidearm, амулеты, шарф…) | `Action`, `Rarity`, `LootCost`, `LootTables` |
| `hats.xml` | 102 шляпы (косметика) | |
| `ship_equipment.xml` | 49 модулей подлодки (торпеды, пулемёты, лазеры, броня, ускорители) | `WeaponMounts`, `Rarity`, `LootTables` |
| `ship_weapons.xml` / `ship_weapons.enemies.xml` | оружие подлодок (игрока / врагов) на карте мира | |
| `ship_upgrades.xml` | 62 улучшения подлодки (дерево апгрейдов), часть требует `KeyItem` | `KeyItem`, стоимость |
| `key_items.xml` | 30 сюжетных ключевых предметов (таран, щит, бустер, светящиеся стержни, шестерни, ключи хейстов) | не продаются, не занимают место |
| `special_items.xml` | виртуальные предметы: `loot_money` (галлоны), `loot_fragments`, `loot_random_weapon[_tierN]`, контейнеры | `SpecialItemType` |
| `loot_tables.xml` | «колоды» случайного лута (`LootGroup` → `Deck` → `Entry`): что падает в бою, на карте, в наградах. **Изменение сбрасывает колоды в сейвах** | |
| `forge_recipes.xml` | рецепты кузницы | |
| `treasure.xml` | пусто (старый пример) | |

Связь «предмет ↔ таблица»: сам предмет перечисляет `<LootTables><Table Name="combat_utility" TierMin="2"/>`;
таблицы используются группами из `loot_tables.xml`, а магазины тянут ассортимент из тех же таблиц
(с учётом `RequiredReputation`).

### Миссии и уровни

| Файл | Что внутри |
|---|---|
| `missions.de`, `missions.west_caribbea.de`, `missions.east_caribbea.de`, `missions.arctica.de`, `missions.atomics.de`, `missions.industrial.de` | ~150 миссий: шаблон (`base`), уровень (`Patchset` → `Patchsets/*.le`), Lua-скрипт (`Script`), окружение, **фиксированная награда `MasterLoot/Item@Definition`**, множители лута/опыта, спаун врагов, музыка, слои по квестам (`ToggleLayers`) |
| `mission_rules.de` | правила/цели конкретных миссий (`MissionObjective`) |
| `mission_objectives.xml` | типы целей (kill_all_enemies, evacuate, destroy_generators …) |
| `rooms.*.de` | процедурные «комнаты» по стилям (atomic/industrial/navy/pirate): порталы-двери, крыши, заполнители |
| `spawn_tables.de` | **какие враги спаунятся** (начальные + подкрепления по тирам): `Entry Name ThreatMin/Max Deck DeckCount RequireSlot` |
| `spawn_groups.de` | группы врагов (`Entries` спаунятся все, из `Deck` — один) |
| `environments.xml` | освещение/фон/небо для окружений миссий |
| `tutorials.xml`, `tutorials_popup.xml` | обучение |

Уровни сами по себе — в `Patchsets/<Стиль>/*.le` (Level Editor), миссия ссылается на них через `<Patchset>`.

### Враги, экипаж, ИИ, бой

| Файл | Что внутри |
|---|---|
| `entities.enemies.xml` | базовые абстрактные шаблоны врагов (`ENEMY_BASE`, `ENEMY_RANGED`, `ENEMY_BASE_FLYING`, `ENEMY_TURRET`) |
| `entities.enemies.navy.xml` / `.pirates.xml` / `.atomics.xml` | 170+ врагов по фракциям, включая боссов (`*_boss`, мехи) | 
| `entities.crew.xml` | сущности членов экипажа в бою |
| `personas.xml` | **персонажи экипажа**: найм (`Recruit Cost`), базовые статы, личные апгрейды, личные диалоги |
| `jobs.xml` | 6 профессий (tank, boomer, engineer, sniper, hunter, flanker) и опыт до уровня |
| `job_upgrades.xml`, `job_upgrades.personal.xml` | перки профессий / личные перки |
| `actions.xml` | 183 действия (ход, выстрел, способности, использование утилит) |
| `modifiers.xml` | 339 баффов/дебаффов/статусов |
| `ai.xml`, `behaviors.xml` | ИИ врагов в бою / поведение кораблей на карте |
| `edicts.xml` | «эдикты» (глобальные модификаторы врагов) |
| `difficulty_presets.xml` | casual … veryhard |
| `entities.projectiles.xml`, `materials.xml` | снаряды; реакция материалов на попадания |
| `entities.pickups.xml` | подбираемые объекты (`pickup_master_loot`, `pickup_timed_loot` …) |
| `entities.xml`, `entities.rigids.xml`, `entities.editor.xml` | укрытия, двери, физобъекты, служебные объекты редактора |
| `factions.xml` | фракции (cowbots = игрок, navy, pirates, atomics …) |

### Карта мира, регионы, прогрессия

| Файл | Что внутри |
|---|---|
| `worldmap_regions.xml` | 17 регионов: угроза, **таблица наград за репутацию (`Bounties/Bounty Cost Item Count`)**, ежедневные спауны с предметами (`ReputationSpawns/Spawn/Item`, артефакты и флагманы), музыка |
| `worldmap_spawns.xml` | случайные встречи на карте |
| `entities.worldmap.xml` | 195 объектов карты мира: подлодка игрока, враги, магазины (`wm_*_shop`), бары, лут |
| `pointsofinterest.xml` | метки/указатели на карте |
| `worldmap_water.xml`, `worldmap_shipwakes.xml` | вода, следы |
| `WorldMap/Maps/WorldMap.tmx` | сама карта (Tiled): тайлы, объекты с свойствами `Mission`, `Item`, `DialogTrigger`, `ScriptTrigger` … |
| `outsets.xml` | **стартовые состояния**: `new_game` (обычный старт), `new_game_skip_tutorial` и ~30 отладочных точек сюжета — деньги, предметы, экипаж, пройденные миссии |
| `game_constants.xml` | глобальные константы (камера, бой, экономика, `NewGameOutset`) |

### Сюжет, квесты, диалоги

| Файл | Что внутри |
|---|---|
| `quests.xml` | 223 квеста: условия (`Condition` + `Parameter`), цепочки (`UnlockedBy`), **награды `ItemRewards/Item`** |
| `conversations.xml` | 699 диалогов (1 MB) — реплики ссылаются на ключи в `Language/*_conversations.csv` |
| `speakers.xml` | говорящие персонажи (портреты, голоса) |
| `barks.xml` | короткие реплики в бою/на карте |
| `intermission_cutscenes.xml` | интерлюдии между миссиями (+ скрипт в `Lua/Intermissions/`) |
| `counters.xml`, `banners.xml` | счётчики (тревога, таймер лута) и баннеры UI |

### Звук, визуал, UI, движок

`sounds.xml` (все аудио-события), `music_lists.xml`, `specialeffects.xml` (410 эффектов → `Effects/*.pe`),
`screenshakes.xml`, `rumbles.xml` (вибрация), `animations.xml`, `assetsets.xml` (что грузить при старте/в каком окружении),
`hulldecals.xml`, `fringesets.xml`, `wedges*.xml`, `platforms.xml`, `ladders.xml`, `brushes.xml`, `windobjects.xml`
(геометрия/декор уровней), `textsettings.xml`, `textstyles.xml`, `textitems.xml`, `indicators.xml` (иконки кнопок),
`mousecursors.xml`, `languages.xml`, `settings*.xml` (опции меню), `framework_constants*.xml`, `achievements.dat` (в Game2.pak).

---

## 5. Остальное содержимое паков

| Путь | Где | Что |
|---|---|---|
| `Lua/System/` | Game.pak | базовая библиотека: `system.lua` (классы), `tactics_common.lua`, `worldmap_common.lua`, `vector.lua` |
| `Lua/Missions/` | Game.pak | 115 скриптов миссий (`<Script>` в миссии) |
| `Lua/Intermissions/` | Game.pak | 62 скрипта интерлюдий — тут выдаются сюжетные предметы (`GiveItem("keyitem_ship_ram")`) |
| `Lua/WorldMap/worldmap.lua` | Game.pak | логика карты мира |
| `Language/` | Game.pak | 12 языков × 3 файла: `<lang>.csv` (UI/предметы), `<lang>_conversations.csv`, `<lang>_speakers.csv`; `credits.en.xml` |
| `Patchsets/` | Game.pak | уровни Level Editor `.le` по стилям: Atomic, Industrial, Navy, Pirate, Skybox, Trash |
| `Tilesets/` | Game.pak | тайлсеты уровней |
| `UI/ui.me` | Game.pak | весь интерфейс; `debug*.me` — отладочные меню |
| `Effects/*.pe` | Game.pak | частицы |
| `Controls/*.cfg` | Game.pak | раскладки управления |
| `Movies/*.srt` | Game.pak | субтитры роликов (сами ролики `.webm` — loose) |
| `WorldMap/` | оба | карта (Game.pak) + геометрия тайлсетов `*.Geometry.xml` (Game2.pak) |
| `Atlases/`, `Characters/`, `Audio/Sounds/`, `Fonts/`, `Shaders/`, `Textures/`, `MouseCursors/`, `DecalSprites/` | Game2.pak | ассеты |

### Lua: что доступно скриптам

Колбэки (движок вызывает): `Init`, `Run`, `OnMissionStart`, `OnCrewDeployed`, `OnFactionTurnBegin/End(faction)`,
`OnEntityKilled(entity, context)`, `OnActorAlerted(actor)`, `OnDoorOpened`, `OnEntityPickedUp`,
`OnZoneGathered`, `OnCounterChanged/Removed`, `OnObjectiveComplete`, `OnActionFinished`, `OnMissionVictory`.

Частые функции API: `StartConversation`, `TutorialPopup`, `GiveItem`, `RemoveItem`, `CompleteQuest`,
`SpawnEntity`, `DestroyEntity`, `FindEntityWithName`, `EntityIsNamed`, `IsActorHostile`, `TriggerReinforcements`,
`EnableEntranceReinforcements`, `SetAlarmActive`, `OpenNamedDoor`/`LockNamedDoor`/`UnlockNamedDoor`,
`NewObjective`, `CompleteNamedObjective`, `ProgressNamedObjective`, `AddModifier`/`RemoveModifier`,
`CameraPanToNamed`, `HoldCamera`, `PlayMusic`, `Wait`, `MoveActorToNamed`, `RelocateTeam`, `MoveWorldMapActorTo`.

---

## 6. «Где поменять X?» — шпаргалка

| Хочу изменить | Файл → место |
|---|---|
| урон оружия | `weapons.xml` → `<Weapon>` → `ShootDamage/Amount` (+ `ExplosiveDamage`, `CritAmount`) |
| цену предмета | `LootCost` в weapons/utilities/ship_equipment/hats.xml; продажа — `SellValueFactor` |
| что лежит в миссии как главный приз | `missions*.de` → `<Mission>` → `<MasterLoot><Item Definition=…>` |
| награды за репутацию региона | `worldmap_regions.xml` → `<Region>` → `<Bounties><Bounty Cost Item Count>` |
| артефакты/флагманы на карте | `worldmap_regions.xml` → `<ReputationSpawns><Spawn><Item Definition>` |
| награду за квест | `quests.xml` → `<Quest>` → `<ItemRewards><Item Name Count>` |
| состав случайного лута / магазинов | `loot_tables.xml` + `LootTables` у предметов |
| врагов в миссиях | `spawn_tables.de` / `spawn_groups.de`; миссия выбирает таблицу; размещённые вручную — в `Patchsets/*.le` |
| HP/характеристики врага | `entities.enemies.<фракция>.xml` → `Health/HitPoints`, `Actor/Weapon`, `SpawnCost` |
| стартовые деньги/экипаж/предметы | `outsets.xml` → `new_game` (`NewGameOutset` в `game_constants.xml`) |
| цену найма и статы персонажей | `personas.xml` |
| перки профессий | `job_upgrades.xml` |
| улучшения подлодки | `ship_upgrades.xml` |
| тексты/названия | `Language/<lang>.csv` |
| сюжетную выдачу предметов | `Lua/Intermissions/*.lua` (`GiveItem`) и `quests.xml` |

---

## 7. Что трогает рандомизатор

| Модуль | Файлы |
|---|---|
| Миссии на карте | `WorldMap/Maps/WorldMap.tmx` — значение свойства `Mission` у объектов-меток (сами метки, их имя и слой не трогаются) |
| Перемешивание фиксированных наград (+ логика ключевых предметов) | `missions*.de` (MasterLoot), `worldmap_regions.xml` (Bounties, ReputationSpawns), `quests.xml` (ItemRewards); таймлайн сюжета — из `outsets.xml` |
| Магазины | `Patchsets/*.le` — `CustomEntity` с `<Definition>itemshop</Definition>`, свойства `Item`/`OneTime` (подиумы в барах, на лодках-магазинах, в Atomic City) |
| Враги | `spawn_tables.de` |
| Шапки врагов | `entities.enemies.*.xml` → `Actor/Hat`, `Actor/RareHat` |
| Характеристики оружия | `weapons.xml` (`ShootDamage`/`ExplosiveDamage`: `Amount`, `CritAmount`, `AmountRandom`, `DamageRadius`) |
| Перки классов | `jobs.xml` → `Job/Upgrades/Upgrade` (уровень 0 не трогается) |
| Реплики / выкрики | `conversations.xml` (`Speak@Text`), `barks.xml` (`String`) — меняются ключи строк, тексты в `Language/*.csv` не трогаются |
| Внешность NPC | `entities.npc.xml` → `SkinnedCharacter/File` |
| Характеристики утилит | `utilities.xml` → `Stats/*` (пассивные) и их `Action` в `actions.xml` (`ExplosiveDamage/Amount`, `DamageRadius`, `Property@HealAmount`, `StartingCharges`, `Cooldown`); balanced меняет и `LootCost` |
| Характеристики способностей | `job_upgrades.xml`, `job_upgrades.personal.xml` → `CogCost` и их `Action` в `actions.xml` (`Cooldown`, `StartingCharges`, урон, `NavRange`, `Property@HealAmount/Points`); действия, которые есть у врагов, не трогаются |
| Шапки экипажа | `entities.crew.xml` → `crew_*/Actor/Hat`, синхронно `outsets.xml` → `Crew/Hat` |
| Характеристики экипажа | `personas.xml` → `BaseStats` (`HitPoints`, `CogCapacity`; у всех наследуются от `CREW`), `entities.crew.xml` → `Actor/MoveDistance` (база 6 в сущности `CREW`) |
| Модели оружия / снаряды | `weapons.xml` → `Character` + `Portrait` / `Projectile` (снаряд = скорость, дальность, дуга из `entities.projectiles.xml`) |
| Музыка | `missions*.de` → `Music`, `worldmap_regions.xml` → `Region/Music`, `music_lists.xml` → `Entry@MusicCue` (песни бар-групп) |
| Освещение миссий | `missions*.de` → `Environment` (см. `environments.xml`) |
| Названия | `Language/<lang>.csv` — меняются местами тексты ключей названий (`weapon_<имя>` / `LocalizedNameId` / имя предмета, `NameStringId` врагов) |
| Цены | `weapons.xml`, `utilities.xml`, `ship_equipment.xml`, `hats.xml`, `special_items.xml`, `key_items.xml` |
| Оружие / ИИ врагов | `entities.enemies*.xml` → `Actor/Weapon` / `Actor/AI` (боссы и «машины» не трогаются) |
| Взрывные укрытия | `missions*.de` → `Placeholders/Placeholder@ReplaceWith` (`ph_explosive_cover*`, `ph_hanging_explosive`, `ph_cover_weak_low`, `ph_hanging_cover`) |
| Звуки / эффекты / голоса | `weapons*.xml` (`ShootSound`, `AimSound`, `CancelAimSound`, `ReloadSound`, `ShootEffects/Effect@Name`), `HurtSound` / `DeathEffect` врагов и экипажа, `entities.projectiles.xml` (`ProjectileEffect`, `MuzzleEffect`, `EffectOnEnd`), `speakers.xml` (`Voice@Cue/Speed`, `Portrait`, `RadioPortrait`) |
| Профессии / личные способности | `personas.xml` → `DefaultWeapon` (+ `outsets.xml` `Crew/Jobs/Job/JobName`), `PersonalUpgrade1/2`, `ReplacePersonalUpgrade` |
| Случайный старт | `outsets.xml` → `new_game`, `new_game_skip_tutorial` (`Resources`, `Items`) |
| Подлодка / колоды лута | `ship_equipment.xml` → `WeaponMounts/Mount@Weapon`, `entities.worldmap.xml` → `wm_enemy_*` `WeaponMounts`, `ship_upgrades.xml` → `CostFragments`; `loot_tables.xml` → `Entry@Count` |
| Карта мира | `WorldMap.tmx` (только слои `Entities`): водовороты — свойство `Destination`; лут (`wm_loot`, `wm_loot_big`, `wm_loot_fragments`) — `x`/`y`; враги и мины (`wm_enemyspawn`, вид = тайл `gid` → `Entities.tsx` `EnemyDefinition`) и скалы (`wm_spike_rocks`) — `gid` + `width`/`height` (масштаб сохраняется). Слои — группы `0`..`9` (0 — поверхность, 6–9 — Atomic City); регион — полигон `Region` того же слоя; порядок регионов — по `outsets.xml` (`VisitedRegions`/`MissionRegion`). Бродячие спауны — `worldmap_spawns.xml` → `SpawnEntry@Name` |
| Загадочные предметы | `Language/<lang>.csv` — имя, `_description`, `_flavor` оружия и утилит → `???` |
| Хаос в миссиях (Lua) | `Lua/Mod/swh2_randomizer.lua` + хук в конце `Lua/Missions/*.lua`; процедурным боевым миссиям — `<Script>` `Lua/Missions/swh2_rnd_<миссия>.lua`; свои `modifiers.xml` (`swh2_mut_*`, `swh2_evt_*`, `swh2_golden`, `swh2_issue_*`), `edicts.xml` (`swh2_<эдикт>_<фракция>`), строки в `Language/<lang>_mod.csv`; регион миссии — полигон `Region` в `WorldMap.tmx`, где стоит её метка |
| Челлендж «без гринда» | `quests.xml` (+2 скрытых квеста на миссию: `nogrind_won_*` → `nogrind_lock_*` с `LockMissions`), `worldmap_regions.xml` (`RespawnIfAchievedReputation`, лут `worldmap_ghost`), `worldmap_spawns.xml` (спауны `wm_loot`), `entities.worldmap.xml` (`LootEntity` кораблей) |

Не перемещаются: ключевые предметы (`key_items.xml`), предметы, упомянутые в условиях квестов
(`quests.xml` `<Parameter>`) и в строках Lua — от них зависит сюжет. Тестовые и ни откуда не
используемые миссии исключены.

Опция «ключевые предметы»: ключевой предмет переносится только в место, которое сюжет (по `outsets.xml` —
отладочные точки старта, цепочка через `Template`, со списками пройденных квестов/миссий и предметов) проходит
РАНЬШЕ, чем предмет впервые появляется. Предметы, которые выдаёт Lua (`GiveItem`) или которые лежат ещё где-то
(`.le`, `.tmx`, `entities.worldmap.xml`), и места с неизвестным временем (bounty, спауны карты, перемещённые миссии,
всё пройденное в старте «skip tutorial») не участвуют.

Важно для миссий: условие квеста `mission` засчитывается, только если квест активен в момент победы
(`mission_static` — в любое время). Поэтому миссии, нужные сюжету, не перемещаются (`Story.ProgressionMissions`).
