# SteamWorld Heist 2 — полный индекс файлов (автогенерация)

> Сгенерировано `tools/gen_file_index.py`. Не редактировать вручную — перезапустите скрипт.
> Обзор «что за что отвечает» — в [FILE_MAP.md](FILE_MAP.md).

Источник: `pak` = Game.pak / Game2.pak, `loose` = отдельный файл в `Bundle/Core`.

## Сводка по папкам

| Папка | Файлов | Размер | Где лежит | Типы |
|---|---:|---:|---|---|
| `Atlases` | 304 | 2.4 GB | Game2.pak, loose | .txt×152, .ktx×152 |
| `Audio/Music` | 123 | 205 MB | loose | .ogg×123 |
| `Audio/Sounds` | 1976 | 547 MB | Game2.pak, loose | .wav×1976 |
| `Characters/Atomics` | 14 | 2 MB | Game2.pak, loose | .isc2×14 |
| `Characters/Crew` | 12 | 11 MB | loose | .isc2×12 |
| `Characters/Guns` | 101 | 48 KB | Game2.pak | .isc2×101 |
| `Characters/Hats` | 101 | 56 KB | Game2.pak | .isc2×101 |
| `Characters/NPC` | 46 | 6 MB | Game2.pak, loose | .isc2×46 |
| `Characters/Navy` | 23 | 6 MB | Game2.pak, loose | .isc2×23 |
| `Characters/Pirates` | 18 | 6 MB | Game2.pak, loose | .isc2×18 |
| `Characters/Sprites` | 56 | 508 KB | Game2.pak | .isc2×56 |
| `Characters/Temp` | 32 | 419 KB | Game2.pak, loose | .isc2×32 |
| `Controls` | 4 | 506 KB | Game.pak | .cfg×4 |
| `DecalSprites/Industrial` | 7 | 34 KB | Game2.pak | .isc2×7 |
| `Definitions` | 103 | 5 MB | Game.pak, Game2.pak | .xml×88, .de×14, .dat×1 |
| `Effects` | 15 | 15 MB | Game.pak | .pe×15 |
| `Effects/Textures` | 35 | 9 MB | Game2.pak, loose | .ktx×35 |
| `Fonts` | 22 | 294 KB | Game2.pak | .fgen×22 |
| `Fonts/ja` | 14 | 1 MB | Game2.pak | .fgen×14 |
| `Fonts/zh` | 14 | 3 MB | loose | .fgen×14 |
| `Language` | 37 | 13 MB | Game.pak | .csv×36, .xml×1 |
| `Lua/Intermissions` | 62 | 26 KB | Game.pak | .lua×62 |
| `Lua/Missions` | 115 | 250 KB | Game.pak | .lua×115 |
| `Lua/System` | 4 | 14 KB | Game.pak | .lua×4 |
| `Lua/WorldMap` | 1 | 24 KB | Game.pak | .lua×1 |
| `MouseCursors` | 10 | 69 KB | Game2.pak | .png×10 |
| `Movies` | 55 | 382 MB | Game.pak, loose | .srt×48, .webm×7 |
| `Patchsets` | 46 | 30 MB | Game.pak | .le×46 |
| `Patchsets/Atomic` | 4 | 6 MB | Game.pak | .le×4 |
| `Patchsets/Industrial` | 4 | 5 MB | Game.pak | .le×4 |
| `Patchsets/Navy` | 10 | 12 MB | Game.pak | .le×10 |
| `Patchsets/Pirate` | 5 | 14 MB | Game.pak | .le×5 |
| `Patchsets/Skybox` | 13 | 2 MB | Game.pak | .le×13 |
| `Patchsets/Trash` | 12 | 5 MB | Game.pak | .le×12 |
| `Shaders` | 1 | 4 KB | Game2.pak | .xml×1 |
| `Shaders/DX11` | 99 | 296 KB | Game2.pak | .dxbc×99 |
| `Shaders/Vulkan1_1` | 99 | 305 KB | Game2.pak | .spv×99 |
| `Textures` | 3 | 1 KB | Game2.pak | .ktx×3 |
| `Textures/Fills` | 5 | 270 KB | Game2.pak | .ktx×5 |
| `Textures/IngameMisc` | 6 | 2 MB | Game2.pak, loose | .ktx×6 |
| `Textures/Loading` | 1 | 19 KB | Game2.pak | .ktx×1 |
| `Textures/Minimap` | 4 | 16 KB | Game2.pak | .ktx×4 |
| `Textures/Ropes` | 4 | 16 KB | Game2.pak | .ktx×4 |
| `Textures/Skybox` | 10 | 3 MB | Game2.pak, loose | .ktx×10 |
| `Textures/Swag` | 6 | 541 KB | Game2.pak | .ktx×6 |
| `Textures/Temp` | 2 | 432 KB | Game2.pak, loose | .ktx×2 |
| `Textures/UI` | 1 | 2 KB | Game2.pak | .ktx×1 |
| `Textures/WorldMap` | 71 | 221 MB | Game2.pak, loose | .ktx×71 |
| `Tilesets` | 11 | 62 KB | Game.pak | .xml×11 |
| `UI` | 3 | 344 KB | Game.pak | .me×3 |
| `WorldMap` | 2 | 1 MB | Game2.pak, loose | .xml×1, .dat×1 |
| `WorldMap/Maps` | 2 | 9 MB | Game.pak, loose | .tmx×1, .dat×1 |
| `WorldMap/Templates` | 25 | 4 KB | Game.pak | .tx×25 |
| `WorldMap/Tiles` | 20 | 520 KB | Game.pak, Game2.pak | .tsx×11, .xml×9 |

## Файлы с данными (XML / .de / .me / Tiled)

Для каждого: корневой элемент, какие определения внутри, первая строка документации из шапки файла.


### `Definitions/`

| Файл | Источник | Размер | Корень | Содержимое | Комментарий в шапке |
|---|---|---:|---|---|---|
| `actions.xml` | Game.pak | 99 KB | `Actions` | Action×183 — e.g. move, shifty, skip_recast, wait, open_door, use_switch … |  |
| `ai.xml` | Game.pak | 54 KB | `AIDefinitions` | AI×49 — e.g. ai_basic_range, ai_machinegunner, ai_shotgunner, ai_melee, ai_sniper, ai_fabio01 … | OrderCalculators: |
| `animations.xml` | Game.pak | 68 KB | `Animations` | Animation×30 — e.g. actor_default, actor_berserker, actor_trophy_vent, actor_trophy_orb, actor_kraken_blob, actor_kraken_final … | 0.25 |
| `assetsets.xml` | Game.pak | 11 KB | `AssetSets` | AssetSet×20 — e.g. movie_player, system, common, menus, ingame, environment_west_caribbea … | Loaded synchronously on startup, keep minimal |
| `banners.xml` | Game.pak | 2 KB | `Banners` | Banner×9 — e.g. alarm_activated, alarm_new_tier, new_day, timed_loot_timeout, timed_loot_success, worldmap_enter_region … | BannerLayer Layer{}; |
| `barks.xml` | Game.pak | 163 KB | `Barks` | Bark×42 — e.g. player_generic, wm_navy_enemy, wm_rattler_enemy, wm_rattler_boss_enemy, wm_atomic_enemy, wm_player … | Each <Bark> definition is a set of barks that can be linked to from an actor entity. Each bark definition contains a number of events it |
| `behaviors.xml` | Game.pak | 41 KB | `Behaviors` | Behavior×11 — e.g. behavior_idle, behavior_ship, behavior_corvette, behavior_centipede, behavior_turret, behavior_mine … |  |
| `bodyparts.xml` | Game.pak | 892 B | `BodyPartDefinitions` | BodyPart×9 — e.g. object, actor, head, thigh, calf, foot … | Used by fallbacks when not hitting a tagged body part |
| `brushes.xml` | Game.pak | 4 KB | `BrushDefinitions` | Brush×10, EditorBrush×1 — e.g. brush_a, brush_b, brush_navy, brush_LightingTest, brush_industrial, brush_pirate … |  |
| `conversations.xml` | Game.pak | 1008 KB | `Conversations` | Conversation×699 — e.g. test, test_queen, con_04_tutorial, con_01_tutorial, con_07_secret_tutorial, con_09_bossintro_tutorial … |  |
| `counters.xml` | Game.pak | 8 KB | `Counters` | Counter×32 — e.g. alarm_parent, alarm, alarm_tier, alarm_next_tier, turret_activation, timed_loot … | PanelTemplate:    Template that need "effect", "icon", "header", and "counter" labels. The remaining count will be shown on "counter" as the |
| `difficulty_presets.xml` | Game.pak | 3 KB | `DifficultyPresets` | DifficultyPreset×5 — e.g. casual, easy, medium, hard, veryhard |  |
| `dlc.xml` | Game.pak | 260 B | `DLCs` |  | <DLC Name="DLC_Test01"> |
| `edicts.xml` | Game.pak | 7 KB | `Edicts` | Edict×10 — e.g. armored_single, shield_single, hitpoints, damage, armored, shield_all … | Faction:               Which faction owns this edict. Used to determine the meaning of other fields. |
| `entities.crew.xml` | Game.pak | 17 KB | `EntityDefinitions` | Entity×15 — e.g. crew_captain, crew_captain_rearmed, crew_captain_rearmed_combat, crew_captain_final_boss, crew_daisy, crew_wesley … | Make sure active crew is on top of other things |
| `entities.cutscene.xml` | Game.pak | 4 KB | `EntityDefinitions` | Entity×6 — e.g. cutscene_captain, cutscene_daisy, cutscene_wesley, cutscene_crew_member, cutscene_npc |  |
| `entities.editor.xml` | Game.pak | 38 KB | `EntityDefinitions` | Entity×54 — e.g. particle_effect, ambient_sound, ambient_sound_area, explored_fow, light_sphere, light_backlight_test … |  |
| `entities.enemies.atomics.xml` | Game.pak | 31 KB | `EntityDefinitions` | Entity×23 — e.g. enemy_atomic_reviver, enemy_atomic_reviver_easy, enemy_atomic_reviver_elite, enemy_atomic_reviver_flying, enemy_atomic_walking_bomb, enemy_atomic_mimic … | GENERIC - START |
| `entities.enemies.navy.xml` | Game.pak | 56 KB | `EntityDefinitions` | Entity×79 — e.g. enemy_navy_seabot, enemy_navy_seabot_weak, enemy_navy_seabot_elite, enemy_navy_seabot_fire, enemy_navy_seabot_fire_elite, enemy_navy_seabot_machinegun … | SEABOT RANGED VARIANTS |
| `entities.enemies.pirates.xml` | Game.pak | 44 KB | `EntityDefinitions` | Entity×71 — e.g. enemy_swab_grenade, enemy_pirate_swab_weak, enemy_pirate_swab, enemy_pirate_swab_elite, enemy_pirate_swab_icegun, enemy_pirate_swab_icegun_elite … | GENERIC - START |
| `entities.enemies.xml` | Game.pak | 10 KB | `EntityDefinitions` | Entity×5 | Used to calculate the area this enemy is covering for the spawn system |
| `entities.npc.xml` | Game.pak | 60 KB | `EntityDefinitions` | Entity×91 — e.g. bartender_wc_a, bartender_wc_b, bartender_wc_hub_c, bartender_arctica_a, bartender_arctica_b, bartender_arctica_c … | Actors seem to be designed for a walk speed of 792 units/sec / 1584. Note: In blender it's 660 due to the characters being scaled 1.2. 792 / |
| `entities.pickups.xml` | Game.pak | 6 KB | `EntityDefinitions` | Entity×10 — e.g. pickup_loot, pickup_loot_special, pickup_master_loot, pickup_master_loot_no_reputation, pickup_master_loot_glowrod, pickup_timed_loot … | Item: The item to pickup |
| `entities.projectiles.xml` | Game.pak | 28 KB | `EntityDefinitions` | Entity×71 — e.g. projectile_dummy, projectile_bullet, projectile_bullet_enemy, projectile_bullet_enemy_ice, projectile_bullet_enemy_fire, projectile_bullet_enemy_atomic … | Projectile values: |
| `entities.rigids.xml` | Game.pak | 10 KB | `EntityDefinitions` | Entity×11 — e.g. test_rigid, test_rigid_hanging, navy_light_hanging_generic, debris_body_part, debris_body_part_shield, debris_attachment_Hat … | What collision type we are |
| `entities.worldmap.xml` | Game.pak | 151 KB | `EntityDefinitions` | Entity×195 — e.g. wm_player_ship_noram, wm_player_ship_ram, wmse_player_ship_surface_noram, wmse_player_ship_surface_ram, wmse_player_ship_submerged_noram, wmse_player_ship_submerged_ram … | If true is not allowed on surface, if spawning on depth 0 it ends up on depth 1. |
| `entities.worldtexts.xml` | Game.pak | 9 KB | `EntityDefinitions` | Entity×39 — e.g. worldtext_actor_bonus, worldtext_loot, worldtext_map_resource, worldtext_map_bounty, worldtext_edict_issued, worldtext_speak_bark … | Available settings w/ defaults: TODO: Also explain |
| `entities.xml` | Game.pak | 106 KB | `EntityDefinitions` | Entity×159 — e.g. special_effect, special_effect_persistent, cover_weak_low_industrial, cover_weak_low_navy, cover_weak_low_pirate, cover_weak_low_navy_elite … | DirectKillFlags: |
| `environments.xml` | Game.pak | 59 KB | `EnvironmentDefinitions` | Environment×42 — e.g. industrial_caribbea, carribea_open_sea, industrial_arctica, navy_caribbea, navy_arctica, pirate_caribbea … | Light, can be specifed multiple times. Settings are interpolated between based on depth. Attributes: |
| `factions.xml` | Game.pak | 1 KB | `Factions` | Faction×6 — e.g. cowbots, townsfolk, navy, pirates, atomics, hazards | Used for mines on the World Map |
| `forge_recipes.xml` | Game.pak | 1 KB | `ForgeRecipeDefinitions` | ForgeRecipe×6 — e.g. sniper, assault, handgun, rocketlauncher, hammer, shotgun |  |
| `framework_constants.platform.xml` | Game.pak | 188 B | `FrameworkConstants` | Settings×1 — e.g. default |  |
| `framework_constants.xml` | Game.pak | 521 B | `FrameworkConstants` | Settings×1 — e.g. default |  |
| `fringesets.xml` | Game.pak | 31 KB | `FringeSets` | FringeSet×12 — e.g. cowbot, LightingTest, navy, navy_exterior, pirate, pirate_exterior … | Values: |
| `game_constants.xml` | Game.pak | 42 KB | `GameConstants` | GameConstants×1 — e.g. default | CAMERA SETTINGS |
| `hats.xml` | Game.pak | 24 KB | `HatDefinitions` | Hat×102 — e.g. hat_captain, hat_daisy, hat_wesley, hat_cornelius, hat_poe, hat_judy … | Properties: |
| `hulldecals.xml` | Game.pak | 36 KB | `HullDecals` | HullDecal×79, HullDecalSet×13 — e.g. ind_stilt01, ind_opticsensor01, ind_opticsensor02, ind_antenna01, ind_antenna02, ind_antenna01_roof … | Decal default values with comments |
| `indicators.xml` | Game.pak | 16 KB | `IndicatorSets` | IndicatorSet×15 — e.g. unknown, mouse_and_keyboard, touchscreen, gamepad_xbox360, gamepad_xboxone, gamepad_nx_joycon … | Kinda dubious, ported over from Heist 1 |
| `intermission_cutscenes.xml` | Game.pak | 25 KB | `IntermissionCutscenes` | IntermissionCutscene×35 — e.g. pilot_reunion, build_ship_weapon, trophy_scuttles, trophy_crew_scuttle, trophy_makes_a_sound, wc_c_bar_intro … | Mission:          which mission to load for this cutscene |
| `job_upgrades.personal.xml` | Game.pak | 4 KB | `JobUpgradeDefinitions` | JobUpgrade×20 — e.g. pu_wesley1, pu_wesley2, pu_daisy1, pu_daisy2, pu_cyclop1, pu_cyclop2 … | Parameters: |
| `job_upgrades.xml` | Game.pak | 23 KB | `JobUpgradeDefinitions` | JobUpgrade×98 — e.g. plus_health_1_tank_a, plus_health_1_boomer_a, plus_health_1_boomer_b, plus_health_1_boomer_c, plus_health_1_engineer_a, plus_health_1_sniper_a … | Parameters: |
| `jobs.xml` | Game.pak | 5 KB | `JobDefinitions` | Job×7 — e.g. tank, boomer, engineer, sniper, hunter, flanker | Accumulated experience needed to level up |
| `key_items.xml` | Game.pak | 9 KB | `KeyItems` | KeyItem×30 — e.g. steel_plates_west_caribbea_c, ship_booster, keyitem_ship_shield, atomic_engine, keyitem_ship_ram, keyitem_glow_rod_01 … | Generic item properties: |
| `ladders.xml` | Game.pak | 1 KB | `Ladders` | Ladder×4 — e.g. industrial, navy, pirate, atomic |  |
| `languages.xml` | Game.pak | 5 KB | `LanguageDefinitions` | Language×12 — e.g. en, es, fr, de, it, pl … | File names should be standardized naming as in code language detection (en, es, pt, de, fr, ...) |
| `loot_tables.xml` | Game.pak | 6 KB | `LootTables` | LootGroup×11 — e.g. loot_group_tactics, loot_group_tactics_limited, loot_group_tactics_atomics, loot_group_world_loot, loot_group_world_loot_fragments, loot_group_world_loot_big … | Controls the distribution of loot, for example by guaranteeing that you |
| `materials.xml` | Game.pak | 28 KB | `MaterialDefinitions` | Material×10 — e.g. metal, metal_platform, atomic, ice, wood, test … |  |
| `mission_objectives.xml` | Game.pak | 19 KB | `MissionObjectives` | MissionObjective×143 — e.g. BASE, evacuate, kill_enemies, kill_all_enemies, destroy_generators, kill_named_enemy … | Settings: |
| `mission_rules.de` | Game.pak | 37 KB | `MissionRules` | MissionRules×84, Project×1 — e.g. west_caribbea_01, west_caribbea_02, west_caribbea_03, west_caribbea_04, west_caribbea_05, west_caribbea_08 … | MissionRules options |
| `missions.arctica.de` | Game.pak | 299 KB | `Missions` | Mission×33, Project×1, DisplayName×1 — e.g. test_atomic_art_prototype, arctica_boss_01, arctica_parley, arctica_heist, arctica_a_bar, arctica_b_bar … |  |
| `missions.atomics.de` | Game.pak | 117 KB | `Missions` | Mission×23, Project×1, DisplayName×1 — e.g. Mission1, atomic_city_bar, atomic_city_shop_01, test_atomic_rooms, atomic_09, atomic_08 … |  |
| `missions.de` | Game.pak | 8 KB | `Missions` | Mission×3, Project×1, DisplayName×1 — e.g. home | Random="1" |
| `missions.east_caribbea.de` | Game.pak | 81 KB | `Missions` | Mission×12, Project×1, DisplayName×1 — e.g. east_caribbea_a_bar, east_caribbea_02, east_caribbea_03, east_caribbea_04, east_caribbea_05, east_caribbea_06 … |  |
| `missions.industrial.de` | Game.pak | 89 KB | `Missions` | Mission×36, Project×1, DisplayName×1 — e.g. east_caribbea_01, test_christoffer, test_harald, east_caribbea_roaming_01, west_caribbea_roaming_01, west_caribbea_roaming_02 … |  |
| `missions.west_caribbea.de` | Game.pak | 257 KB | `Missions` | Mission×44, Project×1, DisplayName×1 — e.g. west_caribbea_a_bar, west_caribbea_b_bar, west_caribbea_b_mechanic_shop, marooned, marooned_mother, west_caribbea_b_dark_dummy_mission … |  |
| `modifiers.xml` | Game.pak | 163 KB | `ModifierDefinitions` | Modifier×339 — e.g. cover_immunities, stun, stun_permanent, stun_immune, stun_permanent_immune, stun_immune_announce … |  |
| `mousecursors.xml` | Game.pak | 1 KB | `MouseCursors` | MouseCursor×3 — e.g. default, hover, press |  |
| `music_lists.xml` | Game.pak | 6 KB | `MusicLists` | MusicList×9 — e.g. music_list_west_caribbea_bar_b, music_list_arctica_bar_a, music_list_arctica_bar_b, music_list_arctica_bar_c, music_list_arctica_bar_d, music_list_east_caribbea_bar_a … | Defines music lists that can be used instead of a music cue in a missions Music definition. |
| `outsets.xml` | Game.pak | 71 KB | `Outsets` | Outset×35 — e.g. new_game, new_game_skip_tutorial, trophy_intro, save_mechanic, west_caribbea_c, west_caribbea_c_heist … | Legend |
| `personas.xml` | Game.pak | 17 KB | `PersonaDefinitions` | Persona×18 — e.g. unassigned, player_ship, captain_rearmed, crew_captain_rearmed_combat, crew_captain_final_boss, daisy … | LocalizedNameId: Defaults to "persona_<Name>". Refers to the language files. |
| `platforms.xml` | Game.pak | 2 KB | `Platforms` | Platform×5 — e.g. industrial, navy, pirate, atomic |  |
| `pointsofinterest.xml` | Game.pak | 14 KB | `PointOfInterestDefinitions` | PointOfInterest×33 — e.g. generic, player_ship, player_ship_overview, enemy_ship, worldmap_bar, worldmap_mission_radar … | Scale when the pointer appears, scaled down to regular scale over the Time in seconds, using the Interpolation method |
| `quests.xml` | Game.pak | 113 KB | `Quests` | Quest×223 — e.g. tutorial_mission, tutorial_goto_bar_a, tutorial_goto_bar_a_hidden, hub_a_go_north_main, hub_a_go_north_hidden, arctica_roaming_01_hidden … |  |
| `rooms.atomic.de` | Game.pak | 48 KB | `Rooms` | Room×43, Project×1 — e.g. fill_01_atomic, roof_atomic_fill, roof_atomic_flat, roof_atomic_flatchimney, roof_atomic_diagL, roof_atomic_diagLchimney … |  |
| `rooms.de` | Game.pak | 366 B | `Rooms` | Project×1, Room×1 |  |
| `rooms.industrial.de` | Game.pak | 55 KB | `Rooms` | Room×58, Project×1 — e.g. fill_industrial, roof_industrial_fill, roof_industrial_flat, roof_industrial_flatchimney, roof_industrial_diagL, roof_industrial_diagLchimney … |  |
| `rooms.navy.de` | Game.pak | 78 KB | `Rooms` | Room×71, Project×1 — e.g. fill, roof_navy_fill, roof_navy_flat, roof_navy_flatchimney, roof_navy_diagL, roof_navy_diagLchimney … |  |
| `rooms.pirate.de` | Game.pak | 78 KB | `Rooms` | Room×76, Project×1 — e.g. fill_01_pirate, roof_pirate_fill, roof_pirate_flat, roof_pirate_flatchimney, roof_pirate_diagL, roof_pirate_diagLchimney … | TODO: REMOVE - also remove the test patch in pirate_rooms.de |
| `rumbles.xml` | Game.pak | 75 KB | `RumbleDefinitions` | Rumble×546 — e.g. victory_popup, mission_complete_show_line_1, mission_complete_show_line_2, mission_complete_show_line_3, mission_complete_count_up, unlock_new_ability … | Available values: |
| `screenshakes.xml` | Game.pak | 6 KB | `ScreenShakes` | ScreenShake×34 — e.g. heavy_footstep, heavy_landing, explode_cover_ability, explode_mech_rocket, explode_mech_engine_impact, huge_explosion … |  |
| `settings.platform.xml` | Game.pak | 625 B | `Settings` | Setting×3 |  |
| `settings.xml` | Game.pak | 10 KB | `Settings` | Setting×55 | Key: Name of variable |
| `ship_equipment.xml` | Game.pak | 21 KB | `ShipEquipment` | ShipEquipment×49 — e.g. ship_equipment_torpedo_01, ship_equipment_torpedo_02, ship_equipment_torpedo_02_rare, ship_equipment_torpedo_03, ship_equipment_machinegun_01, ship_equipment_machinegun_02 … | Generic item properties: |
| `ship_upgrades.xml` | Game.pak | 18 KB | `ShipUpgrades` | ShipUpgrade×62 — e.g. ship_boost_00, dive_00, dive_02, geiger_counter_00, geiger_counter_01, equip_00 … | Available settings: |
| `ship_weapons.enemies.xml` | Game.pak | 14 KB | `ShipWeapons` | ShipWeapon×39 — e.g. navy_submarine_torpedo_triple, navy_submarine_torpedo_triple_elite, navy_corvette_machinegun, navy_frigate_torpedo, navy_frigate_flagship_01_torpedo, navy_frigate_flagship_02_torpedo … | See ship_weapons.xml for BASE template and description of all attributes |
| `ship_weapons.xml` | Game.pak | 19 KB | `ShipWeapons` | ShipWeapon×35 — e.g. PLAYER_MACHINEGUN, player_machinegun_01, player_machinegun_02, player_machinegun_03, PLAYER_MACHINEGUN_HEAVY, player_machinegun_heavy_01 … | Weapon damage. This is a ScalingValue, where x = naval combat difficulty setting. |
| `sounds.xml` | Game.pak | 189 KB | `Sounds` | Groups×1, DecayGroups×1, AudioCues×1, NumMusicPlayers×1 |  |
| `spawn_groups.de` | Game.pak | 6 KB | `SpawnGroups` | SpawnGroup×21 — e.g. navy_group_guardseabot, navy_group_guardcommander, navy_group_west_caribbea_reinforcement_tier1, navy_group_recruit, pirate_group_skelebot, pirate_group_skelebot_ice … | Defines spawn groups that can be referenced in spawn tables. |
| `spawn_tables.de` | Game.pak | 38 KB | `SpawnTables` | SpawnTable×71 — e.g. empty, west_caribbea_navy_a, east_caribbea_navy_a, west_caribbea_navy_reinforcements_tier0, west_caribbea_navy_reinforcements_tier1, west_caribbea_navy_reinforcements_tier2 … | Defines spawn tables which are used to spawn enemy units in a mission, e.g. for initial spawns and reinforcements. |
| `speakers.xml` | Game.pak | 35 KB | `SpeakerDefinitions` | Speaker×143 — e.g. captain, captain_radio, daisy, wesley, judy, cornelius … | NameStringId, automatically set to speaker_%Name%, so don't normally specify |
| `special_items.xml` | Game.pak | 4 KB | `SpecialItemDefinitions` | SpecialItem×15 — e.g. loot_nothing, loot_random_dummy, loot_fragments, loot_money, loot_container_normal, loot_container_master … | Settings on top of generic items: |
| `specialeffects.xml` | Game.pak | 78 KB | `SpecialEffects` | SpecialEffect×410 — e.g. test_bullet_bounce, test_bullet_impact, metal_hit, metal_bounce, sand_hit, sand_bounce … | Persistent: Whether the effect should be serialized for saving. NOTE: This should almost always be left at the default false. |
| `textitems.xml` | Game.pak | 1 KB | `TextItemDefinitions` | TextItem×12 — e.g. cooldown_turns, exhausted, money, fragments, cog, cog_tintable … |  |
| `textsettings.xml` | Game.pak | 12 KB | `TextSettingsDefinitions` | TextSettings×56 — e.g. default, test, screen_header, popup_header, label_generic, label_generic_drop … | Allows you to specify text layout settings, such as the font and text scale. Contrary to text styles, |
| `textstyles.xml` | Game.pak | 4 KB | `TextStyleDefinitions` | TextStyleContext×1 — e.g. default | example_text	This is how you add [style your_style_name]style[style] in a text.	*EXAMPLE |
| `treasure.xml` | Game.pak | 624 B | `TreasureDefinitions` |  | old example |
| `tutorials.xml` | Game.pak | 3 KB | `Tutorials` | Tutorial×13 — e.g. camera, basicmove, take_cover, skipturn, opendoor, shoot … |  |
| `tutorials_popup.xml` | Game.pak | 18 KB | `Tutorials` | Tutorial×43 — e.g. difficulty_setting, actions, crits, armor, timed_loot, hats … | Tutorial properties: |
| `utilities.xml` | Game.pak | 33 KB | `UtilityDefinitions` | Utility×68 — e.g. utility_repair_01, utility_repair_01_rare, utility_repair_02, utility_repair_02_rare, utility_repair_03, utility_repair_03_rare … | Generic item properties: |
| `weapon_barrages.xml` | Game.pak | 6 KB | `WeaponBarrages` | Barrage×12 — e.g. barrage_kraken_sequence_slow, barrage_kraken_sequence_fast, barrage_kraken_duo_slow, barrage_kraken_all_slow, barrage_kraken_all_fast, barrage_kraken_all_multiple … | Sequences of attacks used for the Kraken boss fight. |
| `weapon_parts.xml` | Game.pak | 281 B | `WeaponPartDefinitions` | WeaponPart×1 |  |
| `weapons.atomics.xml` | Game.pak | 4 KB | `WeaponDefinitions` | Weapon×9 — e.g. atomic_reviver_rifle, atomic_reviver_rifle_elite, atomic_flying_reviver_rifle, atomic_totem_gun, atomic_commander_pistol, atomic_sphere_laser_single … |  |
| `weapons.navy.xml` | Game.pak | 9 KB | `WeaponDefinitions` | Weapon×24 — e.g. navy_seabot_rifle, navy_seabot_rifle_elite, navy_seabot_rifle_fire, navy_seabot_sniper, navy_seabot_sniper_2shot, navy_seabot_sniper_elite … | NAVY WEAPONS |
| `weapons.pirates.xml` | Game.pak | 7 KB | `WeaponDefinitions` | Weapon×14 — e.g. pirate_swab_carbine_2h, pirate_swab_shotgun_2h, pirate_swab_grenade_launcher_2h, pirate_swab_ice_launcher_2h, pirate_swab_icegun, pirate_berserker_rifle … | <RapidFire Count="2" Time="0.15" AngleOffsetMin="-0.16" AngleOffsetMax="-0.10" AngleReset="false"/> |
| `weapons.xml` | Game.pak | 62 KB | `WeaponDefinitions` | Weapon×103 — e.g. sniper_00, sniper_01, sniper_01_rare, sniper_02, sniper_02_rare, sniper_03 … |  |
| `wedges.ground_status.xml` | Game.pak | 6 KB | `Wedges` | Wedge×26, WedgeSet×4 — e.g. ice_0, ice_l14, ice_r14, ice_l26, ice_r26, ice_ground_status … | Short hand templates for ground status textures |
| `wedges.xml` | Game.pak | 9 KB | `Wedges` | Wedge×39, WedgeSet×5 — e.g. ind_lg14, ind_rg14, ind_lg26, ind_rg26, ind_lp26, ind_rp26 … | Entity definition to spawn for this wedge. This is required |
| `windobjects.xml` | Game.pak | 5 KB | `WindObjectDefinitions` | WindObject×24 — e.g. Textures/Skybox/hill03_d.png, Decals/Caribbea/tree_palm02, Decals/Caribbea/tree_palm01, Decals/Caribbea/tree01_d, Decals/Caribbea/tree02_d, Decals/Caribbea/tree03 … | Available settings: |
| `worldmap_regions.xml` | Game.pak | 22 KB | `WorldMapRegions` | Region×17 — e.g. west_caribbea_a, west_caribbea_b, west_caribbea_c, arctica_a, arctica_b, arctica_c … | LocalizedStringId: Display name of hub, defaults to "region_<Name>", ie for west_caribbea_a its "region_west_caribbea_a" |
| `worldmap_shipwakes.xml` | Game.pak | 4 KB | `ShipWakes` | ShipWake×6 — e.g. side, mid, side_medium, mid_medium, side_big, mid_big | Life: Life time of the trail in seconds. It fades out over this time. |
| `worldmap_spawns.xml` | Game.pak | 9 KB | `WorldMapSpawns` | Spawn×36 — e.g. west_caribbea_b_bottom, west_caribbea_b_top, west_caribbea_c_top, west_caribbea_c_bottom, arctica_a, arctica_b … | List of spawn entries, with names and weights |
| `worldmap_water.xml` | Game.pak | 6 KB | `WorldMapWaterDefinitions` | WorldMapWater×2 — e.g. default, arctica_a_test | World map surface water settings. |

### `Language/`

| Файл | Источник | Размер | Корень | Содержимое | Комментарий в шапке |
|---|---|---:|---|---|---|
| `credits.en.xml` | Game.pak | 23 KB | `Credits` | Group×110 | Main developers |

### `Shaders/`

| Файл | Источник | Размер | Корень | Содержимое | Комментарий в шапке |
|---|---|---:|---|---|---|
| `shaders.xml` | Game2.pak | 4 KB | `Shaders` | Shader×49 — e.g. BasicColor, BasicColorTexture, BlurFlat, BlurGuassian, BlurCircular, ColorTextureBloomBlend … | Legend |

### `Tilesets/`

| Файл | Источник | Размер | Корень | Содержимое | Комментарий в шапке |
|---|---|---:|---|---|---|
| `LightingTest_foreground.xml` | Game.pak | 3 KB | `Tileset` | TileSize×1, TileDirectory×1, TileScale×1, HatchPlatform×1 |  |
| `atomic_foreground.xml` | Game.pak | 3 KB | `Tileset` | TileSize×1, TileDirectory×1, TileScale×1, HatchPlatform×1 |  |
| `atomic_midground.xml` | Game.pak | 6 KB | `Tileset` | TileSize×1, TileDirectory×1, TileScale×1, MultiTileTextures×1 |  |
| `bar_caribbea_foreground.xml` | Game.pak | 4 KB | `Tileset` | TileSize×1, TileDirectory×1, TileScale×1, HatchPlatform×1 |  |
| `home.xml` | Game.pak | 3 KB | `Tileset` | TileSize×1, TileDirectory×1, TileScale×1, FringeSet×1 |  |
| `industrial_foreground.xml` | Game.pak | 6 KB | `Tileset` | TileSize×1, TileDirectory×1, TileScale×1, HatchPlatform×1 |  |
| `industrial_midground.xml` | Game.pak | 16 KB | `Tileset` | TileSize×1, TileDirectory×1, TileScale×1, MultiTileTextures×1 | tiles 2.0 |
| `navy_foreground.xml` | Game.pak | 4 KB | `Tileset` | TileSize×1, TileDirectory×1, TileScale×1, HatchPlatform×1 |  |
| `navy_midground.xml` | Game.pak | 7 KB | `Tileset` | TileSize×1, TileDirectory×1, TileScale×1, MultiTileTextures×1 | Prototype 2 assets |
| `pirate_foreground.xml` | Game.pak | 3 KB | `Tileset` | TileSize×1, TileDirectory×1, TileScale×1, HatchPlatform×1 |  |
| `pirate_midground.xml` | Game.pak | 8 KB | `Tileset` | TileSize×1, TileDirectory×1, TileScale×1, MultiTileTextures×1 |  |

### `UI/`

| Файл | Источник | Размер | Корень | Содержимое | Комментарий в шапке |
|---|---|---:|---|---|---|
| `debug.me` | Game.pak | 4 KB | `UI` | Project×1, Styles×1 |  |
| `debug_character_browser.me` | Game.pak | 5 KB | `UI` | Project×1, Version×1, Styles×1, Animations×1 |  |
| `ui.me` | Game.pak | 334 KB | `UI` | Project×1, Version×1, Styles×1, Animations×1 |  |

### `WorldMap/`

| Файл | Источник | Размер | Корень | Содержимое | Комментарий в шапке |
|---|---|---:|---|---|---|
| `WorldMap.xml` | Game2.pak | 24 KB | `Object` | Array×4, Value×3 — e.g. automappingRulesFile, commands, compatibilityVersion, extensionsPath, folders, properties … |  |

### `WorldMap/Maps/`

| Файл | Источник | Размер | Корень | Содержимое | Комментарий в шапке |
|---|---|---:|---|---|---|
| `WorldMap.tmx` | Game.pak | 8 MB | `map` | tileset×11, group×10, editorsettings×1 |  |

### `WorldMap/Templates/TileCollision/`

| Файл | Источник | Размер | Корень | Содержимое | Комментарий в шапке |
|---|---|---:|---|---|---|
| `Bottom.tx` | Game.pak | 137 B | `template` | object×1 |  |
| `BottomHalf.tx` | Game.pak | 139 B | `template` | object×1 |  |
| `BottomQuad.tx` | Game.pak | 146 B | `template` | object×1 |  |
| `Default.tx` | Game.pak | 146 B | `template` | object×1 |  |
| `Left.tx` | Game.pak | 138 B | `template` | object×1 |  |
| `LeftBottom.tx` | Game.pak | 148 B | `template` | object×1 |  |
| `LeftHalf.tx` | Game.pak | 137 B | `template` | object×1 |  |
| `LeftQuad.tx` | Game.pak | 147 B | `template` | object×1 |  |
| `LeftRightBottom.tx` | Game.pak | 157 B | `template` | object×1 |  |
| `LeftRightBottomQuads.tx` | Game.pak | 166 B | `template` | object×1 |  |
| `Right.tx` | Game.pak | 138 B | `template` | object×1 |  |
| `RightBottom.tx` | Game.pak | 146 B | `template` | object×1 |  |
| `RightHalf.tx` | Game.pak | 136 B | `template` | object×1 |  |
| `RightQuad.tx` | Game.pak | 147 B | `template` | object×1 |  |
| `Top.tx` | Game.pak | 136 B | `template` | object×1 |  |
| `TopHalf.tx` | Game.pak | 137 B | `template` | object×1 |  |
| `TopLeft.tx` | Game.pak | 145 B | `template` | object×1 |  |
| `TopLeftBottom.tx` | Game.pak | 153 B | `template` | object×1 |  |
| `TopLeftBottomQuads.tx` | Game.pak | 162 B | `template` | object×1 |  |
| `TopLeftRight.tx` | Game.pak | 154 B | `template` | object×1 |  |
| `TopLeftRightQuads.tx` | Game.pak | 163 B | `template` | object×1 |  |
| `TopQuad.tx` | Game.pak | 145 B | `template` | object×1 |  |
| `TopRight.tx` | Game.pak | 145 B | `template` | object×1 |  |
| `TopRightBottom.tx` | Game.pak | 153 B | `template` | object×1 |  |
| `TopRightBottomQuads.tx` | Game.pak | 162 B | `template` | object×1 |  |

### `WorldMap/Tiles/`

| Файл | Источник | Размер | Корень | Содержимое | Комментарий в шапке |
|---|---|---:|---|---|---|
| `Entities.tsx` | Game.pak | 11 KB | `tileset` | tile×63, grid×1, properties×1 |  |
| `Enviorment_Decals_Arctica.Geometry.xml` | Game2.pak | 41 KB | `TileGeometry` | Tile×139 |  |
| `Enviorment_Decals_Arctica.tsx` | Game.pak | 32 KB | `tileset` | tile×139, grid×1 |  |
| `Environment_Decals_Caribbea.Geometry.xml` | Game2.pak | 26 KB | `TileGeometry` | Tile×82 |  |
| `Environment_Decals_Caribbea.tsx` | Game.pak | 26 KB | `tileset` | tile×82, grid×1 |  |
| `Environment_Decals_Underwater.Geometry.xml` | Game2.pak | 23 KB | `TileGeometry` | Tile×79 |  |
| `Environment_Decals_Underwater.tsx` | Game.pak | 20 KB | `tileset` | tile×79, grid×1 |  |
| `Great_Barrier.Geometry.xml` | Game2.pak | 15 KB | `TileGeometry` | Tile×60 |  |
| `Great_Barrier.tsx` | Game.pak | 22 KB | `tileset` | tile×60, grid×1 |  |
| `Surface Decals.Geometry.xml` | Game2.pak | 16 KB | `TileGeometry` | Tile×52 |  |
| `Surface Decals.tsx` | Game.pak | 14 KB | `tileset` | tile×52, grid×1 |  |
| `Surface.Geometry.xml` | Game2.pak | 668 B | `TileGeometry` | Tile×2 |  |
| `Surface.tsx` | Game.pak | 655 B | `tileset` | tile×2, grid×1 |  |
| `Surface_Caribbea.Geometry.xml` | Game2.pak | 15 KB | `TileGeometry` | Tile×58 |  |
| `Surface_Caribbea.tsx` | Game.pak | 24 KB | `tileset` | tile×58, grid×1, wangsets×1 |  |
| `Surface_S_Arctica.Geometry.xml` | Game2.pak | 76 KB | `TileGeometry` | Tile×292 |  |
| `Surface_S_Arctica.tsx` | Game.pak | 98 KB | `tileset` | tile×292, grid×1, wangsets×1 |  |
| `Texture Lights.tsx` | Game.pak | 624 B | `tileset` | tile×2, grid×1 |  |
| `Underwater.Geometry.xml` | Game2.pak | 25 KB | `TileGeometry` | Tile×95 |  |
| `Underwater.tsx` | Game.pak | 35 KB | `tileset` | tile×95, grid×1, wangsets×1 |  |

## Прочие файлы (Lua, ассеты)

- `Atlases/` (Game2.pak, loose) — 304 файлов: .ktx×152, .txt×152
- `Audio/Music/` (loose) — 123 файлов: .ogg×123
- `Audio/Sounds/ambient/` (loose) — 45 файлов: .wav×45
- `Audio/Sounds/effects/` (Game2.pak, loose) — 394 файлов: .wav×394
- `Audio/Sounds/move/` (Game2.pak, loose) — 159 файлов: .wav×159
- `Audio/Sounds/rumbles/` (Game2.pak): `quick_boom.wav`, `rumble_ship_laser_loop.wav`
- `Audio/Sounds/talk/` (Game2.pak, loose) — 632 файлов: .wav×632
- `Audio/Sounds/ui/` (Game2.pak, loose) — 67 файлов: .wav×67
- `Audio/Sounds/weapon/` (Game2.pak, loose) — 402 файлов: .wav×402
- `Audio/Sounds/worldmap/` (Game2.pak, loose) — 275 файлов: .wav×275
- `Characters/Atomics/AtomicMechOperator/` (loose): `atomic_mech_operator.isc2`, `atomic_mech_operator_kraken.isc2`
- `Characters/Atomics/AtomicWaterScientist/` (loose): `atomic_water_scientist.isc2`
- `Characters/Atomics/FlyingReviver/` (Game2.pak): `atomic_flying_reviver.isc2`
- `Characters/Atomics/Hydrobot/` (Game2.pak): `atomics_hydrobot.isc2`
- `Characters/Atomics/Kraken/` (Game2.pak): `kraken_base.isc2`, `kraken_eye.isc2`, `kraken_necronomicog.isc2`
- `Characters/Atomics/Mimic/` (loose): `mimic.isc2`
- `Characters/Atomics/Reviver/` (loose): `atomic_reviver.isc2`
- `Characters/Atomics/Sphere/` (Game2.pak): `atomic_sphere_core.isc2`, `atomic_sphere_mid.isc2`, `atomic_sphere_outer.isc2`
- `Characters/Atomics/WalkingBomb/` (loose): `atomic_walking_bomb.isc2`
- `Characters/Crew/AdventureBoy/` (loose): `adventure_boy.isc2`
- `Characters/Crew/Captain/` (loose): `captain.isc2`, `captain_motherarm.isc2`
- `Characters/Crew/Chimney/` (loose): `chimney.isc2`
- `Characters/Crew/Crow/` (loose): `crow.isc2`
- `Characters/Crew/Cyclop/` (loose): `cyclop.isc2`
- `Characters/Crew/Daisy/` (loose): `daisy.isc2`
- `Characters/Crew/Diver/` (loose): `diver.isc2`
- `Characters/Crew/GentleGiant/` (loose): `gentle_giant.isc2`
- `Characters/Crew/Judy/` (loose): `judy.isc2`
- `Characters/Crew/Poe/` (loose): `poe.isc2`
- `Characters/Crew/Wesley/` (loose): `wesley.isc2`
- `Characters/Guns/Atomics/` (Game2.pak): `flying_reviver_rifle.isc2`, `kraken_eye_gun.isc2`, `reviver_rifle.isc2`, `reviver_turret_gun.isc2`
- `Characters/Guns/Crew/` (Game2.pak) — 76 файлов: .isc2×76
- `Characters/Guns/Navy/` (Game2.pak): `navy_commander_gun.isc2`, `navy_drone_gun.isc2`, `navy_fabio_gun.isc2`, `navy_mech_machinegun.isc2`, `navy_mech_top_cannon.isc2`, `navy_seabot_machinegun.isc2`, `navy_seabot_rifle.isc2`, `navy_seabot_shotgun.isc2`, `navy_seabot_sniper.isc2`, `navy_shotgun.isc2`, `navy_turret_gun.isc2`, `navy_turret_megaphone.isc2`
- `Characters/Guns/Pirates/` (Game2.pak): `pirate_berserker_rifle.isc2`, `pirate_swab_carbine.isc2`, `pirate_swab_carbine_ice.isc2`, `pirate_swab_grenade_launcher.isc2`, `pirate_swab_grenade_launcher_ice.isc2`, `pirate_swab_shotgun.isc2`, `pirate_totembearer_gun.isc2`, `pirate_turret_cannon.isc2`, `rattler_king_blunderbuss.isc2`
- `Characters/Hats/Atomics/` (Game2.pak): `atomic_flying_reviver_hat.isc2`, `atomic_flying_reviver_rare01_hat.isc2`, `atomic_mech_operator_hat.isc2`, `atomic_mimic_hat.isc2`, `atomic_reviver_hat.isc2`, `atomic_reviver_king_hat.isc2`, `atomic_reviver_rare01_hat.isc2`, `atomic_reviver_rare02_hat.isc2`, `atomic_walking_bomb_hat.isc2`, `atomic_walking_bomb_rare01_hat.isc2`, `atomic_water_scientist_hat.isc2`
- `Characters/Hats/Crew/` (Game2.pak): `chimney_hat.isc2`, `crew_adventure_boy_hat.isc2`, `crew_captain_hat.isc2`, `crew_cornelius_hat.isc2`, `crew_crow_hat.isc2`, `crew_cyclop_hat.isc2`, `crew_daisy_hat.isc2`, `crew_diver_hat.isc2`, `crew_judy_hat.isc2`, `crew_poe_hat.isc2`, `crew_wesley_hat.isc2`
- `Characters/Hats/NPC/` (Game2.pak): `mother_hat.isc2`, `piper_hat.isc2`, `revolution_beret_hat.isc2`
- `Characters/Hats/Navy/` (Game2.pak) — 38 файлов: .isc2×38
- `Characters/Hats/Pirates/` (Game2.pak): `pirate_berserker_hat.isc2`, `pirate_berserker_ice_hat.isc2`, `pirate_berserker_parley_hat.isc2`, `pirate_berserker_rare01_hat.isc2`, `pirate_berserker_rare02_hat.isc2`, `pirate_ice_bomb_hat.isc2`, `pirate_morgan_hat.isc2`, `pirate_skelebot_hat.isc2`, `pirate_swab_elite_hat.isc2`, `pirate_swab_hat.isc2`, `pirate_swab_ice_hat.isc2`, `pirate_swab_rare01_hat.isc2`, `pirate_swab_rare02_hat.isc2`, `pirate_swab_rare03_hat.isc2`, `pirate_swab_tough_hat.isc2`, `pirate_totem_bearer_bone_hat.isc2`, `pirate_totem_bearer_hat.isc2`, `pirate_totem_bearer_ice_hat.isc2`, `pirate_totem_bearer_lightning_hat.isc2`, `pirate_totem_bearer_pain_hat.isc2`, `pirate_totem_bearer_rare01_hat.isc2`, `pirate_totem_bearer_retribution_hat.isc2`
- `Characters/Hats/Shop/Arctica/` (Game2.pak): `shop_fish_hat.isc2`, `shop_fur_hat.isc2`, `shop_ice_hat.isc2`, `shop_kanga_hat.isc2`, `shop_rain_hat.isc2`, `shop_santa_hat.isc2`, `shop_ushanka_hat.isc2`, `shop_valkyrie_hat.isc2`
- `Characters/Hats/Shop/Caribbea/` (Game2.pak): `shop_bandana_hat.isc2`, `shop_fruit_hat.isc2`, `shop_icecream_hat.isc2`, `shop_screen_hat.isc2`, `shop_snorkel_hat.isc2`, `shop_straw_hat.isc2`, `shop_top_hat.isc2`, `shop_wickedshades_hat.isc2`
- `Characters/NPC/Arctica/BarNPC_a/` (Game2.pak): `npc_generic_bar_a.isc2`
- `Characters/NPC/Arctica/BarNPC_b/` (Game2.pak): `npc_generic_bar_b.isc2`
- `Characters/NPC/Arctica/Bartender/` (loose): `arctica_bartender.isc2`
- `Characters/NPC/Arctica/BartenderBolla/` (loose): `arctica_bartender_bolla.isc2`
- `Characters/NPC/Arctica/BartenderFrola/` (loose): `arctica_bartender_frola.isc2`
- `Characters/NPC/Arctica/BartenderKastrolla/` (loose): `arctica_bartender_kastrolla.isc2`
- `Characters/NPC/Arctica/BotsonHopper/` (Game2.pak): `npc_botson_hopper.isc2`
- `Characters/NPC/Arctica/LadySmog/` (Game2.pak): `npc_lady_smog.isc2`
- `Characters/NPC/Arctica/MrCrankshaft/` (Game2.pak): `npc_mr_crankshaft.isc2`
- `Characters/NPC/Arctica/MrsCrankshaft/` (Game2.pak): `npc_mrs_crankshaft.isc2`
- `Characters/NPC/Arctica/YeppersonYep/` (Game2.pak): `npc_yep.isc2`
- `Characters/NPC/AtomicCity/Bartender/` (loose): `bartender_atomic.isc2`
- `Characters/NPC/AtomicCity/Oppenheimer/` (loose): `oppenheimer.isc2`
- `Characters/NPC/EastCaribbea/BartenderCarnelian/` (loose): `ec_hc_bartender.isc2`
- `Characters/NPC/Generic/` (Game2.pak): `g_npc_1.isc2`, `g_npc_2.isc2`, `g_npc_3.isc2`, `g_npc_4.isc2`
- `Characters/NPC/Generic/Doorbot/` (Game2.pak): `npc_doorbot.isc2`
- `Characters/NPC/Generic/Nintendoguru/` (Game2.pak): `npc_nintendo_guru.isc2`
- `Characters/NPC/Generic/Revolutionary/` (Game2.pak): `npc_revolutionary.isc2`
- `Characters/NPC/SPG/Rabbit/` (loose): `spg_rabbit.isc2`
- `Characters/NPC/SPG/TheSpine/` (loose): `spg_spine.isc2`
- `Characters/NPC/SPG/Zero/` (loose): `spg_zero.isc2`
- `Characters/NPC/Shopkeeper/` (loose): `shopkeeper.isc2`
- `Characters/NPC/Story/Fen/` (Game2.pak, loose): `fen.isc2`, `fen_cloaked.isc2`
- `Characters/NPC/Story/Mechanic/` (loose): `mechanic.isc2`
- `Characters/NPC/Story/Mother/` (loose): `mother.isc2`, `mother_withoutarm.isc2`
- `Characters/NPC/Story/Piper/` (Game2.pak, loose): `piper.isc2`, `piper_cloaked.isc2`
- `Characters/NPC/Story/Stanwinch/` (loose): `stanwinch.isc2`
- `Characters/NPC/Story/Trophy/` (Game2.pak, loose): `trophy.isc2`, `trophy_kraken.isc2`, `trophy_orb.isc2`, `trophy_vent.isc2`
- `Characters/NPC/Story/WaterScientist/` (loose): `water_scientist.isc2`
- `Characters/NPC/WestCaribbea/BarNPC_c/` (Game2.pak): `npc_generic_bar_c.isc2`
- `Characters/NPC/WestCaribbea/BarNPC_d/` (Game2.pak): `npc_generic_bar_d.isc2`
- `Characters/NPC/WestCaribbea/Bartender/` (loose): `wc_hc_bartender.isc2`
- `Characters/NPC/WestCaribbea/BartenderCrank/` (Game2.pak): `wc_bartender_crank.isc2`
- `Characters/NPC/WestCaribbea/BartenderGorkin/` (Game2.pak): `wc_bartender_gorkin.isc2`
- `Characters/NPC/WestCaribbea/BuddyClampford/` (Game2.pak): `npc_buddy_clampford.isc2`
- `Characters/NPC/WestCaribbea/MrsClampford/` (Game2.pak): `npc_mrs_clampford.isc2`
- `Characters/NPC/WestCaribbea/OldHobHobson/` (Game2.pak): `npc_hobson.isc2`
- `Characters/Navy/Bombs/` (Game2.pak): `navy_fire_bomb.isc2`, `pirate_ice_bomb.isc2`
- `Characters/Navy/Commander/` (loose): `navy_commander.isc2`
- `Characters/Navy/Drone/` (Game2.pak): `navy_drone.isc2`
- `Characters/Navy/Fabio/` (loose): `fabio.isc2`
- `Characters/Navy/Guard/` (loose): `navy_guard.isc2`
- `Characters/Navy/Mech/` (loose): `navy_mech.isc2`, `navy_mech_sub_back.isc2`, `navy_mech_sub_cockpit.isc2`, `navy_mech_sub_fueltank.isc2`, `navy_mech_sub_loudspeaker.isc2`, `navy_mech_sub_operator.isc2`, `navy_mech_sub_operator_atomic.isc2`, `navy_mech_sub_scientist_atomic.isc2`, `navy_mech_sub_topcannon.isc2`
- `Characters/Navy/MechOperator/` (loose): `mech_operator.isc2`
- `Characters/Navy/Recruit/` (Game2.pak, loose): `navy_recruit.isc2`, `oddbot.isc2`
- `Characters/Navy/Seabot/` (loose): `navy_seabot.isc2`, `navy_seabot_machinegunner.isc2`, `navy_seabot_shotgunner.isc2`, `navy_seabot_sniper.isc2`, `navy_seabot_swordsman.isc2`
- `Characters/Pirates/Berserker/` (loose): `pirate_berserker.isc2`
- `Characters/Pirates/Morgan/` (loose): `morgan.isc2`, `pirate_boss_totem_1.isc2`, `pirate_boss_totem_2.isc2`
- `Characters/Pirates/Skelebot/` (loose): `pirate_skelebot.isc2`
- `Characters/Pirates/Swab/` (loose): `pirate_swab.isc2`, `pirate_swab_grenade.isc2`, `pirate_swab_grenade_ice.isc2`, `pirate_swab_ice.isc2`, `pirate_swab_machinegun.isc2`, `pirate_swab_shotgun.isc2`
- `Characters/Pirates/TotemBearer/` (loose): `totem_bearer.isc2`
- `Characters/Pirates/Totems/BoneTotem/` (Game2.pak): `bone_totem.isc2`
- `Characters/Pirates/Totems/FreezingTotem/` (Game2.pak): `freezing_totem.isc2`
- `Characters/Pirates/Totems/LightningTotem/` (Game2.pak): `lightning_totem.isc2`
- `Characters/Pirates/Totems/MalfunctionTotem/` (Game2.pak): `malfunction_totem.isc2`
- `Characters/Pirates/Totems/RetributionTotem/` (Game2.pak): `retribution_totem.isc2`
- `Characters/Pirates/Totems/RustTotem/` (Game2.pak): `rust_totem.isc2`
- `Characters/Sprites/Atomics/AtomicCoverExplosive/` (Game2.pak): `atomic_explosive_cover.isc2`
- `Characters/Sprites/Atomics/AtomicCoverReviveExplosive/` (Game2.pak): `atomic_explosive_reviving_cover.isc2`
- `Characters/Sprites/Atomics/AtomicCoverReviveHigh/` (Game2.pak): `atomic_reviving_cover.isc2`
- `Characters/Sprites/Atomics/AtomicCoverStrongHigh/` (Game2.pak): `atomic_high_strong_cover.isc2`
- `Characters/Sprites/Atomics/AtomicCoverWeakMid/` (Game2.pak): `atomic_mid_weak_cover.isc2`
- `Characters/Sprites/Atomics/AtomicCoverWeakSmall/` (Game2.pak): `atomic_low_weak_cover.isc2`
- `Characters/Sprites/Atomics/AtomicsDoor/` (Game2.pak): `atomics_door.isc2`, `atomics_door_tall.isc2`
- `Characters/Sprites/Atomics/AtomicsEntrance/` (Game2.pak): `atomic_entrance.isc2`
- `Characters/Sprites/Atomics/AtomicsHatch/` (Game2.pak): `atomic_hatch.isc2`
- `Characters/Sprites/Atomics/AtomicsPopupCover/` (Game2.pak): `atomic_popup_cover.isc2`
- `Characters/Sprites/Atomics/AtomicsReviverTurret/` (Game2.pak): `atomic_reviver_turret.isc2`
- `Characters/Sprites/Atomics/AtomicsVaultDoor/` (Game2.pak): `atomics_vault_door.isc2`, `atomics_vault_door_tall.isc2`
- `Characters/Sprites/BullseyeSwitch/` (Game2.pak): `bullseye_switch.isc2`
- `Characters/Sprites/Industrial/Consoles/MapConsole/` (Game2.pak): `playership_map_console.isc2`
- `Characters/Sprites/Industrial/Consoles/PlayerConsoles/` (Game2.pak): `ship_upgrade_console_guildhall.isc2`, `ship_upgrade_console_gym.isc2`, `ship_upgrade_console_main.isc2`
- `Characters/Sprites/Industrial/Consoles/ShipConsole/` (Game2.pak): `ship_equipment_console.isc2`
- `Characters/Sprites/Industrial/EngineerCover/` (Game2.pak): `engineer_cover.isc2`
- `Characters/Sprites/Industrial/IndustrialDoor/` (Game2.pak): `industrial_door.isc2`
- `Characters/Sprites/Industrial/IndustrialEntrance/` (Game2.pak): `industrial_entrance.isc2`, `industrial_entrance_particles.isc2`
- `Characters/Sprites/Industrial/IndustrialHatch/` (Game2.pak): `industrial_hatch.isc2`
- `Characters/Sprites/Industrial/IndustrialLowWeakCover/` (Game2.pak): `industrial_low_weak_cover.isc2`
- `Characters/Sprites/Industrial/IndustrialMidWeakCover/` (Game2.pak): `industrial_mid_weak_cover.isc2`
- `Characters/Sprites/Industrial/IndustrialPopupCover/` (Game2.pak): `popup_cover_industrial.isc2`
- `Characters/Sprites/Industrial/IndustrialStrongCover/` (Game2.pak): `industrial_strong_cover.isc2`
- `Characters/Sprites/Industrial/PlayerShipEntrance/` (Game2.pak): `playersub_entrance.isc2`
- `Characters/Sprites/Industrial/ShopPodium/` (Game2.pak): `shop_podium.isc2`
- `Characters/Sprites/Industrial/ShopPodiumBuyback/` (Game2.pak): `shop_podium_buyback.isc2`
- `Characters/Sprites/Navy/NavyDoor/` (Game2.pak): `navy_door.isc2`
- `Characters/Sprites/Navy/NavyDroneDeployer/` (Game2.pak): `navy_drone_deployer.isc2`
- `Characters/Sprites/Navy/NavyEntrance/` (Game2.pak): `navy_entrance.isc2`
- `Characters/Sprites/Navy/NavyHatch/` (Game2.pak): `navy_hatch.isc2`
- `Characters/Sprites/Navy/NavyLowWeakCover/` (Game2.pak): `navy_low_weak_cover.isc2`
- `Characters/Sprites/Navy/NavyMidWeakCover/` (Game2.pak): `navy_mid_weak_cover.isc2`
- `Characters/Sprites/Navy/NavyPopupCover/` (Game2.pak): `navy_popup_cover.isc2`
- `Characters/Sprites/Navy/NavyStrongCover/` (Game2.pak): `nv_strong_cover.isc2`
- `Characters/Sprites/Navy/NavyTrapDoor/` (Game2.pak): `navy_trapdoor.isc2`
- `Characters/Sprites/Navy/NavyTurret/` (Game2.pak): `navy_turret.isc2`
- `Characters/Sprites/Navy/NavyVaultDoor/` (Game2.pak): `navy_vault_door.isc2`
- `Characters/Sprites/Pirates/IceBlock/` (Game2.pak): `pirate_iceblock.isc2`
- `Characters/Sprites/Pirates/IceCover/` (Game2.pak): `pirate_icecover.isc2`
- `Characters/Sprites/Pirates/PirateDoor/` (Game2.pak): `pirate_door.isc2`
- `Characters/Sprites/Pirates/PirateEntrance/` (Game2.pak): `pirate_entrance.isc2`
- `Characters/Sprites/Pirates/PirateHatch/` (Game2.pak): `pirate_hatch.isc2`
- `Characters/Sprites/Pirates/PiratePopupCover/` (Game2.pak): `pirate_popup_cover.isc2`
- `Characters/Sprites/Pirates/PirateStrongCover/` (Game2.pak): `pirate_strong_cover.isc2`
- `Characters/Sprites/Pirates/PirateTurret/` (Game2.pak): `pirate_turret.isc2`
- `Characters/Sprites/Pirates/PirateTurretCannon/` (Game2.pak): `pirate_turret.isc2`
- `Characters/Sprites/Pirates/PirateVaultDoor/` (Game2.pak): `pirate_vault_door.isc2`
- `Characters/Sprites/Pirates/PirateWeakMediumCover/` (Game2.pak): `pirate_mid_weak_cover.isc2`
- `Characters/Sprites/Pirates/PirateWeakSmallCover/` (Game2.pak): `pirate_low_weak_cover.isc2`
- `Characters/Sprites/TriggerLight/` (Game2.pak): `trigger_light.isc2`
- `Characters/Temp/Atomic/` (Game2.pak): `atomic_guard.isc2`
- `Characters/Temp/BarNPCs/` (Game2.pak): `doorbot_npc.isc2`, `fisherwoman_npc.isc2`, `guru_bobby_npc.isc2`, `lady_npc.isc2`, `mrhusband_npc.isc2`, `mrswife_npc.isc2`, `nephbot_npc.isc2`, `old_man_npc.isc2`, `rattler_recruit_npc.isc2`, `revolution_npc.isc2`, `yep_npc.isc2`
- `Characters/Temp/BarNPCs/ShadyBarNavy/` (Game2.pak): `navy_recruit.isc2`
- `Characters/Temp/Hats/` (Game2.pak): `ArmouredTophat.isc2`, `ArmyHelmet.isc2`, `CaptainsHat.isc2`, `PirateHat1.isc2`, `RoyalCrown.isc2`, `RoyalistSoldierHat.isc2`, `crew_daisy_concept_hat.isc2`, `navy_guard_hat.isc2`, `swab_hat.isc2`
- `Characters/Temp/Scrapper_Swab/` (loose): `Scrapper_Swab.isc2`, `Scrapper_Swab_skelebot_a.isc2`
- `Characters/Temp/ShadyCharacters/` (Game2.pak): `shady_characters.isc2`
- `Characters/Temp/ShadyCharacters/Fen/` (Game2.pak): `shady_character_fen.isc2`
- `Characters/Temp/ShadyCharacters/Piper/` (Game2.pak): `shady_character_piper.isc2`
- `Characters/Temp/Trophy/` (Game2.pak): `trophy.isc2`, `trophy_kraken.isc2`, `trophy_vent.isc2`
- `Characters/Temp/WaterScientist/` (Game2.pak): `water_scientist.isc2`, `water_scientist_atomic.isc2`
- `Controls/` (Game.pak): `default_controls.cfg`, `default_controls_sensitivity.cfg`, `default_gamepads.cfg`, `default_gamepads_flavors.cfg`
- `DecalSprites/Industrial/Light_Hanging_1/` (Game2.pak): `hanging_light_flickering_1.isc2`, `hanging_light_spotlight.isc2`, `hanging_light_spotlight_2.isc2`, `hanging_light_spotlight_static.isc2`, `hanging_light_spotlight_steam.isc2`
- `DecalSprites/Industrial/PlayerShipMachinery/` (Game2.pak): `boiler_engine.isc2`
- `DecalSprites/Industrial/ShipWheel/` (Game2.pak): `ship_wheel.isc2`
- `Definitions/` (Game2.pak): `achievements.dat`
- `Effects/` (Game.pak): `attachements.pe`, `character.pe`, `edicts.pe`, `environments.pe`, `explosions.pe`, `hits.pe`, `icons.pe`, `lights.pe`, `modifiers.pe`, `projectiles.pe`, `ui.pe`, `wm_environments.pe`, `wm_fighting.pe`, `wm_icons.pe`, `wm_water_surface.pe`
- `Effects/Textures/OverlayTextures/` (Game2.pak, loose): `Anisotopic_noise_contrast.ktx`, `Cracks 2 - 512x512.ktx`, `Gabor 1 - 512x512.ktx`, `Manifold 13 - 512x512.ktx`, `Melt 14 - 512x512.ktx`, `Milky 10 - 512x512.ktx`, `Perlin 9 - 512x512.ktx`, `Perlin_9_contrast_512x512.ktx`, `Shine_gradient.ktx`, `Super Perlin 11 - 512x512.ktx`, `Super Perlin 12 - 512x512.ktx`, `Swirl 11 - 512x512.ktx`, `Turbulence 1 - 512x512.ktx`, `anisotopic_noise.ktx`, `atomic_reviver_turret_glass_mask.ktx`, `cloudnoise.ktx`, `cloudnoise2.ktx`, `cloudnoise3.ktx`, `cloudnoise4.ktx`, `cloudnoise_128x256_00000.ktx`, `noise_flames.ktx`, `reviver_cover_triangle.ktx`, `scanlines_1.ktx`, `swirlnoise.ktx`
- `Effects/Textures/WorldMapSurface/` (Game2.pak, loose): `SharpTurn_Wave.ktx`, `circle_wave2_50.ktx`, `foam_blob.ktx`, `foam_chaos_shape.ktx`, `hat_shop_boat_wave.ktx`, `line_wave.ktx`, `mission_wave.ktx`, `object_impact_wave.ktx`, `torpedotrail_wave.ktx`, `whirlpool_2_wave.ktx`, `whirlpool_wave.ktx`
- `Fonts/` (Game2.pak): `action_cooldown.fgen`, `body_large.fgen`, `body_large_bold.fgen`, `body_medium.fgen`, `body_medium_bold.fgen`, `body_medium_drop.fgen`, `body_small.fgen`, `body_small_drop.fgen`, `body_small_italic.fgen`, `conversation.fgen`, `conversation_nametag.fgen`, `critdamage.fgen`, `damage.fgen`, `debug_hud.fgen`, `debug_mono.fgen`, `debug_small.fgen`, `header_description.fgen`, `header_medium_drop.fgen`, `header_numbers.fgen`, `healthbar.fgen`, `indicator.fgen`, `tactics_healthbar.fgen`
- `Fonts/ja/` (Game2.pak): `body_large.fgen`, `body_large_bold.fgen`, `body_medium.fgen`, `body_medium_bold.fgen`, `body_medium_drop.fgen`, `body_small.fgen`, `body_small_drop.fgen`, `body_small_italic.fgen`, `conversation.fgen`, `conversation_nametag.fgen`, `debug_mono.fgen`, `header_description.fgen`, `header_medium_drop.fgen`, `indicator.fgen`
- `Fonts/zh/` (loose): `body_large.fgen`, `body_large_bold.fgen`, `body_medium.fgen`, `body_medium_bold.fgen`, `body_medium_drop.fgen`, `body_small.fgen`, `body_small_drop.fgen`, `body_small_italic.fgen`, `conversation.fgen`, `conversation_nametag.fgen`, `debug_mono.fgen`, `header_description.fgen`, `header_medium_drop.fgen`, `indicator.fgen`
- `Language/` (Game.pak) — 36 файлов: .csv×36
- `Lua/Intermissions/` (Game.pak) — 62 файлов: .lua×62
- `Lua/Missions/` (Game.pak) — 115 файлов: .lua×115
- `Lua/System/` (Game.pak): `system.lua`, `tactics_common.lua`, `vector.lua`, `worldmap_common.lua`
- `Lua/WorldMap/` (Game.pak): `worldmap.lua`
- `MouseCursors/` (Game2.pak): `aim.png`, `hover.png`, `hover_extralarge.png`, `hover_large.png`, `normal.png`, `normal_extralarge.png`, `normal_large.png`, `press.png`, `press_extralarge.png`, `press_large.png`
- `Movies/` (Game.pak, loose) — 55 файлов: .srt×48, .webm×7
- `Patchsets/` (Game.pak) — 46 файлов: .le×46
- `Patchsets/Atomic/` (Game.pak): `atomic_exteriors.le`, `atomic_rooms.le`, `atomic_rooms_decorated.le`, `atomic_rooms_iris.le`
- `Patchsets/Industrial/` (Game.pak): `alex_industrial_rooms.le`, `industrial_exteriors.le`, `industrial_rooms.le`, `martin_industrial_rooms.le`
- `Patchsets/Navy/` (Game.pak): `navy_exteriors.le`, `navy_rooms.le`, `navy_rooms_aldo.le`, `navy_rooms_alexander.le`, `navy_rooms_decorated.le`, `navy_rooms_emil.le`, `navy_rooms_fancy.le`, `navy_rooms_fancy_emil.le`, `navy_rooms_glue.le`, `navy_rooms_martin.le`
- `Patchsets/Pirate/` (Game.pak): `pirate_exteriors.le`, `pirate_rooms.le`, `pirate_rooms_alex.le`, `pirate_rooms_emil.le`, `pirate_rooms_martin.le`
- `Patchsets/Skybox/` (Game.pak): `skybox_arctica.le`, `skybox_atomiccity.le`, `skybox_caribbea.le`, `skybox_caribbea_open_sea.le`, `skybox_caribbea_stilts.le`, `skybox_deep.le`, `skybox_home_shallows.le`, `skybox_mainmenu.le`, `skybox_marooned.le`, `skybox_navyunderwater.le`, `skybox_pirateunderwater.le`, `skybox_test.le`, `skybox_tutorial.le`
- `Patchsets/Trash/` (Game.pak): `arctica_bar_art_prototype.le`, `atomic_art_prototype.le`, `home_art_prototype.le`, `navy_art_prototype_02.le`, `pirate_art_prototype.le`, `test_atomic.le`, `test_christoffer.le`, `test_emil.le`, `test_navigation.le`, `test_olle.le`, `test_robert.le`, `trash_aldo.le`
- `Shaders/DX11/` (Game2.pak) — 99 файлов: .dxbc×99
- `Shaders/Vulkan1_1/` (Game2.pak) — 99 файлов: .spv×99
- `Textures/` (Game2.pak): `default_d.ktx`, `default_n.ktx`, `default_s.ktx`
- `Textures/Fills/` (Game2.pak): `fow.ktx`, `jungle_ground01.ktx`, `terrain_underwater.ktx`, `test.ktx`, `test2.ktx`
- `Textures/IngameMisc/` (Game2.pak, loose): `blobsqalpha.ktx`, `explosion_radius.ktx`, `explosion_radius_invalid.ktx`, `floor_shadow.ktx`, `laser_smoke.ktx`, `vignette.ktx`
- `Textures/Loading/` (Game2.pak): `loading_temp.ktx`
- `Textures/Minimap/` (Game2.pak): `fill.ktx`, `ladder.ktx`, `minimap_grid.ktx`, `platform.ktx`
- `Textures/Ropes/` (Game2.pak): `nv_chain_01_D.ktx`, `pr_chain_01_D.ktx`, `wire_D.ktx`, `wire_N.ktx`
- `Textures/Skybox/` (loose): `water_norm_001.ktx`, `water_norm_002.ktx`
- `Textures/Skybox/Backdrops/Gradients/` (Game2.pak): `Arctica_Sky.ktx`, `Arctica_Underwater.ktx`, `AtomicCity.ktx`, `Caribbiean_Sky.ktx`, `Caribbiean_Underwater.ktx`, `Caribbiean_Underwater_Example.ktx`, `Deep.ktx`
- `Textures/Skybox/SurfaceLine/` (Game2.pak): `liquid_water_top.ktx`
- `Textures/Swag/` (Game2.pak): `loot_master.ktx`, `loot_master_ghost.ktx`, `pickup_loot_medal_D.ktx`, `swag_navy_D.ktx`, `swag_navy_N.ktx`, `swag_navy_S.ktx`
- `Textures/Temp/` (Game2.pak, loose): `Chandelier.ktx`, `ChandelierDebris.ktx`
- `Textures/UI/Targeting/` (Game2.pak): `line_overlay_passive.ktx`
- `Textures/WorldMap/` (Game2.pak, loose): `fow_surface_cloud.ktx`, `region_border.ktx`, `region_border_highlighted.ktx`
- `Textures/WorldMap/Backdrops/` (Game2.pak, loose): `underwater.ktx`, `underwater_parallax_bg.ktx`, `underwater_parallax_bg_bright.ktx`, `underwater_parallax_bg_green.ktx`
- `Textures/WorldMap/Caustics/` (loose): `f0.ktx`, `f1.ktx`, `f2.ktx`, `f3.ktx`, `f4.ktx`, `f5.ktx`, `f6.ktx`, `f7.ktx`, `g0.ktx`, `g1.ktx`, `g2.ktx`, `g3.ktx`, `g4.ktx`, `g5.ktx`, `g6.ktx`, `g7.ktx`
- `Textures/WorldMap/DistanceMaps/` (Game2.pak, loose): `vd0.ktx`, `vd1.ktx`, `vd2.ktx`, `vd3.ktx`, `vd4.ktx`, `vd5.ktx`, `vd6.ktx`, `vd7.ktx`, `vd8.ktx`, `vid0.ktx`, `vid1.ktx`, `vid2.ktx`, `vid3.ktx`, `vid4.ktx`, `vid5.ktx`, `vid6.ktx`, `vid7.ktx`, `vid8.ktx`
- `Textures/WorldMap/Vessels/` (Game2.pak, loose): `atomicCentipedeHead_844x692.ktx`, `atomicCentipedeTail_508x312.ktx`, `atomicSwarmCarrier_2000x1456_4row.ktx`, `krakenBody_32_5600x2432.ktx`, `krakenBody_broken_32_5600x2432.ktx`, `krakenHead_Invulnerable_32_6144x6240.ktx`, `krakenHead_Vulnerable_32_6144x6240.ktx`, `krakenTail_32_4264_6320.ktx`, `navyBattleship1_2400x1624.ktx`, `navyBattleship2_592x496.ktx`, `navyCorvette_980x600.ktx`, `navyFlagship_1136x916.ktx`, `navyFrigate_1136x916.ktx`, `navySub_1480x1104.ktx`, `playersub_surface_noram_1216x408.ktx`, `playersub_surface_ram_1456x428.ktx`, `playersub_underwater_noram_1136x428.ktx`, `playersub_underwater_ram_1442x452.ktx`, `rattlerBossShip_5200x1300_2row.ktx`, `rattlerCannon_2400x150.ktx`, `rattlerDreadnought_8000x500.ktx`, `rattlerFlagship_2000x2000_4row.ktx`, `rattlerSpeedboat1_560x392.ktx`, `rattlerSpeedboat2_400x224.ktx`, `rattlerSub_676x532.ktx`
- `Textures/WorldMap/WaveTextures/` (loose): `foam.ktx`, `roughness_noise.ktx`, `water_norm_001.ktx`, `water_norm_001b.ktx`, `waves_shore1.ktx`
- `WorldMap/` (loose): `collision.dat`
- `WorldMap/Maps/` (loose): `WorldMap.StaticOcclusion.dat`
