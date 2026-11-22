"""Builds the 'RebelCampaign' loose-file mod: "Broken Chain", a story campaign in the Eastern Caribbea
(design: docs/CAMPAIGN_REBELS.md; mechanics: docs/MODDING.md, "Своя кампания").

Lord Admiral Piston has seized the East. The captain joins the outnumbered steambot rebels of the Broken Chain bar and
fights alongside them; in the finale their leader Vapora betrays them to Piston and the captain falls covering the
retreat. Six concept missions on original layouts with new goals and allies:
  1 intro (scripted beats, a hidden ally joins when reached)   2 escort (the VIP is a spawned crewmate)
  3 allies from the start (AI rebels, faction townsfolk)        4 free the captives (faction "" -> townsfolk)
  5 hold waves together, then fall back (gather zone)           6 finale: Piston + Vapora's betrayal (SetActorFaction)

Built like TestCampaign (whose helpers it reuses): every original quest is parked behind its own never-quest, original
mission icons are removed, the new game starts at the original East start (outset templated on east_caribbea_a) in our
bar, which replaces the Royal Blue on the map. Optional: the recruitable crew is re-skinned as rebel steambots
(models + names; portraits stay). Strings: Language/<lang>_rebels.csv. Exclusive with TestCampaign / NewMissionTest.

Usage: python tools/mods/make_rebel_campaign.py [--game DIR] [--out DIR]
Then:  swh2 disable TestCampaign NewMissionTest && swh2 enable RebelCampaign && swh2 install   (undo: swh2 restore)
Needs a NEW GAME.
"""
import argparse, os, re, shutil, uuid, zipfile

from make_new_mission_test import in_region
from make_test_campaign import DEFAULT_GAME, GUID, PARK, TILE, check_unlock_order, cutscenes_playable_at_start, \
    never_quest_xml, surface_open

NS = uuid.UUID("5f0c2a8e-3b7d-4c1e-9a6f-2d8b1e7c4a90")  # GUIDs derived from names -> same output every run
P = "reb_"  # prefix of every name this campaign adds

# ---- the campaign --------------------------------------------------------------------------------------------------
ALLIES = {  # combat rebels (faction townsfolk, AI): entity -> (template enemy, model or None, weapon or None, HP, hat)
    "reb_bolton_c": ("enemy_navy_seabot_machinegun", None, None, 10, "hat_revolution_beret"),
    "reb_rusty_c": ("enemy_navy_seabot", None, None, 7, "hat_revolution_beret"),
    "reb_lever_c": ("enemy_navy_seabot_shotgun", None, None, 8, "hat_revolution_beret"),
    "reb_spark_c": ("enemy_navy_seabot_sniper", None, None, 6, None),
    "reb_vapora_c": ("enemy_navy_seabot", "Characters/Navy/Commander/navy_commander.isc2", "navy_commander_pistol", 14,
                     "hat_navy_commander_fancy02"),
    "reb_rebel_rifle": ("enemy_navy_seabot", None, None, 6, "hat_revolution_beret"),
    "reb_rebel_shotgun": ("enemy_navy_seabot_shotgun", None, None, 6, "hat_revolution_beret"),
    "reb_rebel_sniper": ("enemy_navy_seabot_sniper", None, None, 5, "hat_revolution_beret"),
}
# bar NPCs: our entity -> (entity it is copied from, what it replaces in the copied level)
BAR_NPCS = {
    "reb_vapora": ("lady_npc", "hub_c_navy_hr_revolution"),
    "reb_bolton": ("hub_c_navy_hr_revolution", "rude_bar_commander_1"),
    "reb_gauge": ("revolutionary_1", "revolutionary_1"),
    "reb_rusty": ("revolutionary_2", "revolutionary_2"),
    "reb_lever": ("hub_c_navy_hr_revolution", "rude_bar_commander_2"),
}
BAR = {"bar": "reb_hq", "clone": "east_caribbea_a_bar", "anchor": "EAST_CARIBBEA_A", "region": "east_caribbea_a",
       "start": "east_caribbea_a_start", "outset_base": "east_caribbea_a",
       "bartender": ("reb_bartender", "bartender_east_caribbea_a"),
       "keep_layers": ("npcs_post_piston",),  # layers whose NPC slots stay (and are always shown)
       "drop_layers": ("npcs_pre_piston", "post_piston_party", "navy_deserter_npc", "optional_shady_navy_npc"),
       "host": "reb_vapora", "captain_offset": -480}  # captain stands next to the host for the intro
# VIP persona for the escort mission (spawned with SpawnCrewmatesAtEntrance; AI fallback = reb_spark_c)
VIP = {"persona": "reb_spark", "copy": "judy", "entity": "crew_reb_spark", "copy_entity": "crew_judy",
       "model": "Characters/Navy/Seabot/navy_seabot_sniper.isc2"}
# re-skin of the recruitable crew (entities.crew.xml): persona -> model
CREW_SKINS = {
    "daisy": "Characters/Navy/Seabot/navy_seabot_sniper.isc2",
    "wesley": "Characters/Navy/Seabot/navy_seabot.isc2",
    "judy": "Characters/Navy/Seabot/navy_seabot_machinegunner.isc2",
    "crow": "Characters/Navy/Recruit/navy_recruit.isc2",
    "diver": "Characters/Navy/Seabot/navy_seabot_machinegunner.isc2",
    "chimney": "Characters/Navy/Guard/navy_guard.isc2",
    "poe": "Characters/Navy/Seabot/navy_seabot_sniper.isc2",
    "cornelius": "Characters/Navy/Commander/navy_commander.isc2",
    "adventure_boy": "Characters/Navy/Seabot/navy_seabot_shotgunner.isc2",
    "cyclop": "Characters/Navy/Seabot/navy_seabot_swordsman.isc2",
}

# missions: clone = original mission whose layout/level is reused; allies = FixedSpawns (entity, marker, editor name);
# "start_marker" = marker added in the start room (index 0) for allies; objectives = rules groups;
# loot = master loot (item, count; 0 = the item's default)
MISSIONS = [
    {"name": "reb_01", "clone": "east_caribbea_02", "crew": 3, "spawn_table": "east_caribbea_navy_a",
     "keep_fixed": ("navy_leader_1",), "start_marker": "reb_start",
     "allies": [("reb_bolton_c", "reb_start", "reb_bolton"), ("reb_spark_c", "2", "reb_spark")],
     "objectives": [("manual", "objective_reb_01_find", "reb_spark"), ("kill_named_enemy", "objective_reb_01_officer", None)],
     "target": "navy_leader_1", "loot": ("utility_grenade_03_rare", 1)},
    {"name": "reb_02", "clone": "east_caribbea_05", "crew": 3, "spawn_table": "east_caribbea_navy_a",
     "keep_fixed": ("navy_leader_1",), "start_marker": None, "allies": [],
     "objectives": [("loot_master", "objective_reb_02_codes", None)], "loot": ("loot_fragments", 10)},
    {"name": "reb_03", "clone": "east_caribbea_06", "crew": 3, "spawn_table": None,
     "keep_fixed": ("navy_normal_1", "navy_normal_2", "navy_leader_1"), "start_marker": "reb_start",
     "allies": [("reb_bolton_c", "reb_start", "reb_bolton"), ("reb_rusty_c", "reb_start", "reb_rusty"),
                ("reb_lever_c", "reb_start", "reb_lever")],
     "objectives": [("kill_all_enemies", "objective_reb_03", None)], "loot": ("loot_random_weapon", 0)},
    {"name": "reb_04", "clone": "east_caribbea_03", "crew": 3, "spawn_table": None,
     "keep_fixed": ("navy_leader_1", "navy_leader_2", "com_1", "com_2", "drone1", "drone2"), "start_marker": "reb_start",
     "allies": [("reb_bolton_c", "reb_start", "reb_bolton"),
                ("reb_rebel_rifle", "rec1", "reb_captive_1"), ("reb_rebel_shotgun", "rec2", "reb_captive_2"),
                ("reb_rebel_sniper", "rec3", "reb_captive_3"), ("reb_rusty_c", "rec4", "reb_captive_4")],
     "objectives": [("manual", "objective_reb_04_free", "reb_captive_1,reb_captive_2,reb_captive_3,reb_captive_4"),
                    ("kill_named_enemy", "objective_reb_04_warden", None)],
     "target": "navy_leader_1", "loot": ("utility_armor_02", 1)},
    {"name": "reb_05", "clone": "east_caribbea_04", "crew": 4, "spawn_table": None, "keep_fixed": (), "start_marker": None,
     "allies": [("reb_bolton_c", "gz", "reb_bolton"), ("reb_lever_c", "gz", "reb_lever"),
                ("reb_rebel_rifle", "gz", "reb_rebel_1"), ("reb_rebel_sniper", "gz", "reb_rebel_2")],
     "gather": ("reb_fallback", "1"),
     "objectives": [("manual", "objective_reb_05_reach", "d1"), ("manual_1", "objective_reb_05_hold", None),
                    ("enter_gather", "objective_reb_05_fallback", "reb_fallback"), ("manual_2", "objective_reb_05_docks", None)],
     "loot": ("loot_random_weapon", 0)},
    {"name": "reb_06", "clone": "east_caribbea_boss_01", "crew": 4, "spawn_table": None, "keep_fixed": (), "start_marker": None,
     "allies": [], "target": "navy_boss",
     "objectives": [("manual", "objective_reb_06_piston", "navy_boss"), ("manual_1", "objective_reb_06_hold", None)],
     "loot": ("loot_fragments", 15)},  # the original drops a key item
]
# finale: ally spawn spots in the arena next to the rt2 relocation targets (6120..7080, floors y=2760/3720)
FINALE_ALLY_SPOTS = [("reb_vapora_c", "reb_vapora", 7320, 2760), ("reb_bolton_c", "reb_bolton", 6600, 2760),
                     ("reb_rebel_rifle", "reb_rebel_1", 5880, 3720), ("reb_rebel_sniper", "reb_rebel_2", 6600, 3720)]

# conversations: name -> [(speaker, string key)]; bar NPC talk: npc -> [(quest that must be complete or None, key)]
CONV = {
    "con_reb_intro": [("reb_bolton", "reb_intro_1"), ("captain", "reb_intro_2"), ("reb_vapora", "reb_intro_3"),
                      ("reb_vapora", "reb_intro_4"), ("reb_bolton", "reb_intro_5"), ("captain", "reb_intro_6"),
                      ("reb_vapora", "reb_intro_7"), ("reb_bolton", "reb_intro_8")],
    "con_reb_01_start": [("reb_bolton", "reb_01_start_1"), ("captain", "reb_01_start_2")],
    "con_reb_01_found": [("reb_spark", "reb_01_found_1"), ("reb_bolton", "reb_01_found_2")],
    "con_reb_01_officer": [("reb_bolton", "reb_01_officer_1")],
    "con_reb_02_start": [("reb_spark", "reb_02_start_1"), ("captain", "reb_02_start_2")],
    "con_reb_02_codes": [("reb_spark", "reb_02_codes_1")],
    "con_reb_02_lost": [("captain", "reb_02_lost_1")],
    "con_reb_03_start": [("reb_bolton", "reb_03_start_1"), ("reb_rusty", "reb_03_start_2"), ("reb_lever", "reb_03_start_3")],
    "con_reb_03_end": [("reb_bolton", "reb_03_end_1")],
    "con_reb_04_start": [("reb_bolton", "reb_04_start_1"), ("captain", "reb_04_start_2")],
    "con_reb_04_all_free": [("reb_bolton", "reb_04_free_1")],
    "con_reb_05_start": [("reb_bolton", "reb_05_start_1"), ("captain", "reb_05_start_2")],
    "con_reb_05_hold": [("reb_bolton", "reb_05_hold_1")],
    "con_reb_05_fallback": [("reb_bolton", "reb_05_fallback_1"), ("captain", "reb_05_fallback_2")],
    "con_reb_05_docks": [("reb_bolton", "reb_05_docks_1")],
    "con_reb_05_end": [("reb_bolton", "reb_05_end_1")],
    "con_reb_06_start": [("reb_vapora", "reb_06_start_1"), ("reb_bolton", "reb_06_start_2"), ("captain", "reb_06_start_3")],
    "con_reb_06_piston": [("navy_boss", "reb_06_piston_1"), ("reb_vapora", "reb_06_piston_2")],
    "con_reb_06_betrayal": [("navy_boss", "reb_06_betrayal_1"), ("reb_vapora", "reb_06_betrayal_2"),
                            ("reb_bolton", "reb_06_betrayal_3"), ("reb_vapora", "reb_06_betrayal_4"),
                            ("captain", "reb_06_betrayal_5")],
    "con_reb_06_last": [("captain", "reb_06_last_1"), ("reb_bolton", "reb_06_last_2")],
    "con_reb_06_epilogue": [("reb_bolton", "reb_06_epilogue_1"), ("reb_bolton", "reb_06_epilogue_2")],
}
MAP_CONV = ["reb_map_q1", "reb_map_q2", "reb_map_q3", "reb_map_q4", "reb_map_q5", "reb_map_q6"]  # captain, on quest unlock
TALK = {
    "reb_vapora": [("reb_q5", "reb_talk_vapora_6"), ("reb_q4", "reb_talk_vapora_5"), ("reb_q3", "reb_talk_vapora_4"),
                   ("reb_q2", "reb_talk_vapora_3"), ("reb_q1", "reb_talk_vapora_2"), (None, "reb_talk_vapora_1")],
    "reb_bolton": [("reb_q4", "reb_talk_bolton_4"), ("reb_q2", "reb_talk_bolton_3"), ("reb_q1", "reb_talk_bolton_2"),
                   (None, "reb_talk_bolton_1")],
    "reb_gauge": [("reb_q2", "reb_talk_gauge_3"), ("reb_q1", "reb_talk_gauge_2"), (None, "reb_talk_gauge_1")],
    "reb_rusty": [("reb_q3", "reb_talk_rusty_2"), (None, "reb_talk_rusty_1")],
    "reb_lever": [("reb_q3", "reb_talk_lever_2"), (None, "reb_talk_lever_1")],
}

LOC = {
    "ru": {
        "mission_reb_hq": "Разорванная цепь", "reb_banner": "«Разорванная цепь» — штаб повстанцев",
        "speaker_reb_vapora": "Вапора", "speaker_reb_vapora_c": "Вапора",
        "speaker_reb_bolton": "Сержант Болтон", "speaker_reb_bolton_c": "Сержант Болтон",
        "speaker_reb_spark": "Искра", "speaker_reb_spark_c": "Искра",
        "speaker_reb_gauge": "Манометр", "speaker_reb_rusty": "Ржавый", "speaker_reb_rusty_c": "Ржавый",
        "speaker_reb_lever": "Рычаг", "speaker_reb_lever_c": "Рычаг",
        "speaker_reb_bartender": "Старый Клапан",
        "reb_bolton_c": "Сержант Болтон", "reb_rusty_c": "Ржавый", "reb_lever_c": "Рычаг", "reb_spark_c": "Искра",
        "reb_vapora_c": "Вапора", "reb_rebel_rifle": "Повстанец", "reb_rebel_shotgun": "Повстанец",
        "reb_rebel_sniper": "Повстанец-стрелок",
        "persona_reb_spark": "Искра", "persona_reb_spark_full": "Искра",
        "persona_reb_spark_description": "Механик повстанцев. Взломает любой замок, но стреляет так себе.",
        # bar
        "reb_intro_1": "Капитан? Проходите, только тихо. Хвоста за вами не было?",
        "reb_intro_2": "Чисто. Мы получили ваш сигнал.",
        "reb_intro_3": "Добро пожаловать в «Разорванную цепь». Последнее место на Востоке, где ещё можно говорить свободно.",
        "reb_intro_4": "Лорд-адмирал Пистон захватил власть. Флот Востока теперь его, а кто не присягнул — на свалке.",
        "reb_intro_5": "Нас меньше, пушки у нас старые. Но мы ещё дымим.",
        "reb_intro_6": "Чем мы можем помочь?",
        "reb_intro_7": "Для начала — Искра. Наш механик застряла в доках, патрули её вот-вот найдут. Вытащите её.",
        "reb_intro_8": "Я пойду с вами. Покажу дорогу.",
        "reb_bartender_hello": "Капитан. Чего налить?",
        "reb_bartender_rest": "Пожалуй, на сегодня хватит.",
        "reb_bartender_tired": "Отдыхать рано, капитан. Восстание само себя не поднимет.",
        "reb_talk_vapora_1": "Искра в доках, капитан. Каждый час на счету.",
        "reb_talk_vapora_2": "Искра цела — благодаря вам. Теперь нам нужны шифровки флота.",
        "reb_talk_vapora_3": "С кодами патрулей мы видим каждый шаг Пистона. Пора ударить по его топливу.",
        "reb_talk_vapora_4": "Склад горит, а Пистон бесится. Хорошо. Теперь — наши пленные.",
        "reb_talk_vapora_5": "Дворец Пистона... Знаете, капитан, его трон смотрелся бы лучше без него.",
        "reb_talk_vapora_6": "Мы потеряли квартал, но не дух. Время идти на дворец. Всё или ничего.",
        "reb_talk_bolton_1": "Двадцать лет на флоте. А теперь стреляю по своим. Странная штука — совесть.",
        "reb_talk_bolton_2": "Искра — как дочь мне. Спасибо, капитан.",
        "reb_talk_bolton_3": "Ребята рвутся в бой. Возьмите нас в следующий раз — не пожалеете.",
        "reb_talk_bolton_4": "Не нравится мне, как Вапора смотрит на дворец. Будто уже прикидывает, куда поставит мебель.",
        "reb_talk_gauge_1": "Я Манометр, связист. Эфир кишит флотскими — Искру ищут по всем докам.",
        "reb_talk_gauge_2": "Искра уже копается в моём передатчике. Говорит, к флотским кодам нужен ключ — их сейф.",
        "reb_talk_gauge_3": "Коды работают! Теперь я слышу, как Пистон чихает.",
        "reb_talk_rusty_1": "Я Ржавый. Не из-за ржавчины — из-за характера.",
        "reb_talk_rusty_2": "Видели, как полыхнул склад? Красота!",
        "reb_talk_lever_1": "Рычаг. Был боцманом у Пистона. Был.",
        "reb_talk_lever_2": "Пистон ответит. Он всегда отвечает. Будьте готовы.",
        # map
        "reb_map_q1": "Искра ждёт в доках. Болтон пойдёт с нами.",
        "reb_map_q2": "Искра знает, где флот хранит шифровки. Доставим её к сейфу — живой.",
        "reb_map_q3": "Болтон собрал отряд. Бьём по складу топлива — вместе.",
        "reb_map_q4": "Пленных повстанцев держат на приёме у губернатора. Вытащим их.",
        "reb_map_q5": "Пистон ударил по кварталу повстанцев! Нужно прикрыть отход.",
        "reb_map_q6": "Вапора зовёт на последний штурм. Дворец Пистона.",
        "quest_reb_q1": "Искра", "quest_reb_q1_obj": "Пройти «Искра»",
        "quest_reb_q2": "Шифровки", "quest_reb_q2_obj": "Пройти «Шифровки»",
        "quest_reb_q3": "Плечом к плечу", "quest_reb_q3_obj": "Пройти «Плечом к плечу»",
        "quest_reb_q4": "Сломанные цепи", "quest_reb_q4_obj": "Пройти «Сломанные цепи»",
        "quest_reb_q5": "Последний рубеж", "quest_reb_q5_obj": "Пройти «Последний рубеж»",
        "quest_reb_q6": "Предательство", "quest_reb_q6_obj": "Пройти «Предательство»",
        # missions
        "mission_reb_01": "[Восстание] Искра",
        "mission_reb_01_desc": "Механик повстанцев Искра прячется в доках. Найдите её раньше флота.",
        "objective_reb_01_find": "Найти Искру", "objective_reb_01_officer": "Уничтожить лейтенанта",
        "reb_01_start_1": "Её сигнал шёл со склада за доками. Держитесь рядом.",
        "reb_01_start_2": "Вперёд. Тихо, пока можно.",
        "reb_01_found_1": "Наконец-то! Я уж думала, меня переплавят на пуговицы.",
        "reb_01_found_2": "Обниматься потом. Стреляй! Их лейтенант где-то рядом.",
        "reb_01_officer_1": "Лейтенант готов! Уходим, пока не прислали ещё.",
        "mission_reb_02": "[Восстание] Шифровки",
        "mission_reb_02_desc": "Искра вскроет сейф флота с шифровками. Доведите её туда и обратно. Если она погибнет — всё зря.",
        "objective_reb_02_codes": "Забрать шифровки (Искра должна выжить)",
        "reb_02_start_1": "Сейф в дальнем конце. Прикройте меня — я не боец.",
        "reb_02_start_2": "Держись за нами и не высовывайся.",
        "reb_02_codes_1": "Есть! Тут коды всех патрулей Пистона. Теперь — к выходу!",
        "reb_02_lost_1": "Искра! Нет... Без неё нам здесь делать нечего.",
        "mission_reb_03": "[Восстание] Плечом к плечу",
        "mission_reb_03_desc": "Склад топлива флота. Отряд Болтона идёт с вами — уничтожьте всех.",
        "objective_reb_03": "Уничтожить всех врагов вместе с отрядом",
        "reb_03_start_1": "Ржавый, Рычаг — со мной. Капитан, ваши берут фланг.",
        "reb_03_start_2": "Наконец-то настоящее дело!",
        "reb_03_start_3": "Меньше болтовни, Ржавый.",
        "reb_03_end_1": "Чисто! Теперь Пистон неделю будет ходить пешком.",
        "reb_ally_down": "Повстанец пал!",
        "mission_reb_04": "[Восстание] Сломанные цепи",
        "mission_reb_04_desc": "Флот держит наших пленных на приёме у губернатора. Подойдите к ним — и они снова в строю.",
        "objective_reb_04_free": "Освободить пленных (подойти к ним)", "objective_reb_04_warden": "Уничтожить начальника охраны",
        "reb_04_start_1": "Наши где-то в зале. Пробьёмся к ним — и у нас прибавится стволов.",
        "reb_04_start_2": "Тогда не будем заставлять их ждать.",
        "reb_04_freed": "Свобода! Дайте мне цель!",
        "reb_04_free_1": "Все наши на свободе! Теперь — начальник охраны.",
        "mission_reb_05": "[Восстание] Последний рубеж",
        "mission_reb_05_desc": "Пистон бросил флот на квартал повстанцев. Держите площадь вместе с отрядом, потом отступайте к докам.",
        "objective_reb_05_reach": "Пробиться к повстанцам на площади", "objective_reb_05_hold": "Держать площадь",
        "objective_reb_05_fallback": "Отступить к докам", "objective_reb_05_docks": "Удержать доки",
        "reb_05_start_1": "Наши держат площадь за той дверью. Долго они не протянут.",
        "reb_05_start_2": "Тогда идём к ним.",
        "reb_05_hold_1": "Капитан! Держим их, пока жители уходят. Ни шагу назад!",
        "reb_05_fallback_1": "Жители ушли! Всем к докам, отходим!",
        "reb_05_fallback_2": "Отходим! Прикрываем друг друга!",
        "reb_05_docks_1": "Ещё немного! Лодки вот-вот будут.",
        "reb_05_end_1": "Лодки здесь! Уходим, живо!",
        "mission_reb_06": "[Восстание] Предательство",
        "mission_reb_06_desc": "Всё или ничего: штурм дворца Пистона вместе с Вапорой и всеми, кто ещё держит оружие.",
        "objective_reb_06_piston": "Уничтожить лорда-адмирала Пистона", "objective_reb_06_hold": "Задержать врага, пока повстанцы отступают",
        "reb_06_start_1": "Сегодня Восток станет свободным. За мной!",
        "reb_06_start_2": "За «Разорванную цепь»!",
        "reb_06_start_3": "Пистон наш. Вперёд!",
        "reb_06_piston_1": "Горстка ржавых бунтарей у меня во дворце. Как трогательно.",
        "reb_06_piston_2": "Твой дворец ненадолго, Пистон.",
        "reb_06_betrayal_1": "Вапора, дорогая. Пора.",
        "reb_06_betrayal_2": "Прости, капитан. Это восстание было обречено с первого дня. А я не люблю проигрывать.",
        "reb_06_betrayal_3": "Ты... ты продала нас!",
        "reb_06_betrayal_4": "Пистон обещал мне Восток. Вам он обещает свалку.",
        "reb_06_betrayal_5": "Болтон, уводи людей! Я их задержу.",
        "reb_06_last_1": "Уходите... Восстание живо, пока жив хоть один из вас.",
        "reb_06_last_2": "Капитан!..",
        "reb_06_epilogue_1": "Он задержал их один. Мы ушли все до единого.",
        "reb_06_epilogue_2": "Вапора и Пистон ещё ответят. Весь Восток запомнит его имя.",
    },
    "en": {
        "mission_reb_hq": "The Broken Chain", "reb_banner": "The Broken Chain - rebel headquarters",
        "speaker_reb_vapora": "Vapora", "speaker_reb_vapora_c": "Vapora",
        "speaker_reb_bolton": "Sergeant Bolton", "speaker_reb_bolton_c": "Sergeant Bolton",
        "speaker_reb_spark": "Spark", "speaker_reb_spark_c": "Spark",
        "speaker_reb_gauge": "Gauge", "speaker_reb_rusty": "Rusty", "speaker_reb_rusty_c": "Rusty",
        "speaker_reb_lever": "Lever", "speaker_reb_lever_c": "Lever",
        "speaker_reb_bartender": "Old Valve",
        "reb_bolton_c": "Sergeant Bolton", "reb_rusty_c": "Rusty", "reb_lever_c": "Lever", "reb_spark_c": "Spark",
        "reb_vapora_c": "Vapora", "reb_rebel_rifle": "Rebel", "reb_rebel_shotgun": "Rebel", "reb_rebel_sniper": "Rebel Marksman",
        "persona_reb_spark": "Spark", "persona_reb_spark_full": "Spark",
        "persona_reb_spark_description": "The rebels' mechanic. Cracks any lock, shoots so-so.",
        "reb_intro_1": "Captain? Come in, quietly. Nobody followed you?",
        "reb_intro_2": "All clear. We got your signal.",
        "reb_intro_3": "Welcome to the Broken Chain. The last place in the East where you can still speak freely.",
        "reb_intro_4": "Lord Admiral Piston has seized power. The Eastern fleet is his, and whoever didn't swear is scrap.",
        "reb_intro_5": "We're outnumbered and our guns are old. But we're still steaming.",
        "reb_intro_6": "How can we help?",
        "reb_intro_7": "Start with Spark. Our mechanic is stuck at the docks and the patrols are closing in. Get her out.",
        "reb_intro_8": "I'll go with you. I know the way.",
        "reb_bartender_hello": "Captain. What'll it be?",
        "reb_bartender_rest": "Let's call it a day.",
        "reb_bartender_tired": "Too early to rest, Captain. The rebellion won't raise itself.",
        "reb_talk_vapora_1": "Spark is at the docks, Captain. Every hour counts.",
        "reb_talk_vapora_2": "Spark is safe, thanks to you. Now we need the fleet's codes.",
        "reb_talk_vapora_3": "With the patrol codes we see Piston's every move. Time to hit his fuel.",
        "reb_talk_vapora_4": "The depot burns and Piston rages. Good. Now, our prisoners.",
        "reb_talk_vapora_5": "Piston's palace... You know, Captain, his throne would look better without him on it.",
        "reb_talk_vapora_6": "We lost the quarter, not our spirit. Time to storm the palace. All or nothing.",
        "reb_talk_bolton_1": "Twenty years in the navy. Now I shoot at my own. Funny thing, conscience.",
        "reb_talk_bolton_2": "Spark is like a daughter to me. Thank you, Captain.",
        "reb_talk_bolton_3": "The lads want a fight. Take us along next time, you won't regret it.",
        "reb_talk_bolton_4": "I don't like how Vapora looks at that palace. Like she's already placing the furniture.",
        "reb_talk_gauge_1": "I'm Gauge, the radio man. The air is full of navy chatter - they're combing the docks for Spark.",
        "reb_talk_gauge_2": "Spark is already tinkering with my radio. Says the navy codes need a key - their safe.",
        "reb_talk_gauge_3": "The codes work! Now I can hear Piston sneeze.",
        "reb_talk_rusty_1": "I'm Rusty. Not the rust, the temper.",
        "reb_talk_rusty_2": "See that depot go up? Beautiful!",
        "reb_talk_lever_1": "Lever. Was Piston's bosun. Was.",
        "reb_talk_lever_2": "Piston will strike back. He always does. Be ready.",
        "reb_map_q1": "Spark is waiting at the docks. Bolton is coming with us.",
        "reb_map_q2": "Spark knows where the navy keeps its codes. We get her to the safe - alive.",
        "reb_map_q3": "Bolton has a squad ready. We hit the fuel depot - together.",
        "reb_map_q4": "The rebel prisoners are held at the governor's reception. Let's get them out.",
        "reb_map_q5": "Piston is attacking the rebel quarter! We have to cover the retreat.",
        "reb_map_q6": "Vapora calls for the final assault. Piston's palace.",
        "quest_reb_q1": "Spark", "quest_reb_q1_obj": "Complete \"Spark\"",
        "quest_reb_q2": "The Codes", "quest_reb_q2_obj": "Complete \"The Codes\"",
        "quest_reb_q3": "Shoulder to Shoulder", "quest_reb_q3_obj": "Complete \"Shoulder to Shoulder\"",
        "quest_reb_q4": "Broken Chains", "quest_reb_q4_obj": "Complete \"Broken Chains\"",
        "quest_reb_q5": "The Last Line", "quest_reb_q5_obj": "Complete \"The Last Line\"",
        "quest_reb_q6": "Betrayal", "quest_reb_q6_obj": "Complete \"Betrayal\"",
        "mission_reb_01": "[Rebellion] Spark",
        "mission_reb_01_desc": "The rebel mechanic Spark is hiding at the docks. Find her before the navy does.",
        "objective_reb_01_find": "Find Spark", "objective_reb_01_officer": "Scrap the lieutenant",
        "reb_01_start_1": "Her signal came from the warehouse behind the docks. Stay close.",
        "reb_01_start_2": "Move. Quietly, while we can.",
        "reb_01_found_1": "Finally! I thought they'd melt me into buttons.",
        "reb_01_found_2": "Hug later. Shoot! Their lieutenant is close.",
        "reb_01_officer_1": "The lieutenant is down! Let's go before they send more.",
        "mission_reb_02": "[Rebellion] The Codes",
        "mission_reb_02_desc": "Spark will crack the navy safe with the codes. Get her there and back. If she falls, it's all for nothing.",
        "objective_reb_02_codes": "Grab the codes (Spark must survive)",
        "reb_02_start_1": "The safe is at the far end. Cover me, I'm no fighter.",
        "reb_02_start_2": "Stay behind us and keep your head down.",
        "reb_02_codes_1": "Got it! Codes for every one of Piston's patrols. Now, to the exit!",
        "reb_02_lost_1": "Spark! No... Without her there's nothing for us here.",
        "mission_reb_03": "[Rebellion] Shoulder to Shoulder",
        "mission_reb_03_desc": "A navy fuel depot. Bolton's squad fights with you - scrap them all.",
        "objective_reb_03": "Scrap every enemy together with the squad",
        "reb_03_start_1": "Rusty, Lever - with me. Captain, your crew takes the flank.",
        "reb_03_start_2": "Finally, some real work!",
        "reb_03_start_3": "Less talk, Rusty.",
        "reb_03_end_1": "Clear! Piston will be walking for a week.",
        "reb_ally_down": "A rebel is down!",
        "mission_reb_04": "[Rebellion] Broken Chains",
        "mission_reb_04_desc": "The navy holds our prisoners at the governor's reception. Reach them and they're back in the fight.",
        "objective_reb_04_free": "Free the prisoners (get close to them)", "objective_reb_04_warden": "Scrap the warden",
        "reb_04_start_1": "Our people are somewhere in the hall. Reach them and we get more guns.",
        "reb_04_start_2": "Then let's not keep them waiting.",
        "reb_04_freed": "Free! Give me a target!",
        "reb_04_free_1": "Everyone's free! Now, the warden.",
        "mission_reb_05": "[Rebellion] The Last Line",
        "mission_reb_05_desc": "Piston throws the fleet at the rebel quarter. Hold the square with the squad, then fall back to the docks.",
        "objective_reb_05_reach": "Break through to the rebels on the square", "objective_reb_05_hold": "Hold the square",
        "objective_reb_05_fallback": "Fall back to the docks", "objective_reb_05_docks": "Hold the docks",
        "reb_05_start_1": "Our people hold the square behind that door. They won't last long.",
        "reb_05_start_2": "Then we go to them.",
        "reb_05_hold_1": "Captain! We hold them while the townsfolk get out. Not a step back!",
        "reb_05_fallback_1": "The townsfolk are out! Everyone to the docks, fall back!",
        "reb_05_fallback_2": "Fall back! Cover each other!",
        "reb_05_docks_1": "A little longer! The boats are almost here.",
        "reb_05_end_1": "The boats are here! Go, go!",
        "mission_reb_06": "[Rebellion] Betrayal",
        "mission_reb_06_desc": "All or nothing: storm Piston's palace with Vapora and everyone who can still hold a gun.",
        "objective_reb_06_piston": "Scrap Lord Admiral Piston", "objective_reb_06_hold": "Hold the enemy while the rebels retreat",
        "reb_06_start_1": "Today the East will be free. Follow me!",
        "reb_06_start_2": "For the Broken Chain!",
        "reb_06_start_3": "Piston is ours. Go!",
        "reb_06_piston_1": "A handful of rusty rebels in my palace. How touching.",
        "reb_06_piston_2": "Your palace won't be yours for long, Piston.",
        "reb_06_betrayal_1": "Vapora, my dear. It's time.",
        "reb_06_betrayal_2": "Sorry, Captain. This rebellion was doomed from day one. And I don't like to lose.",
        "reb_06_betrayal_3": "You... you sold us out!",
        "reb_06_betrayal_4": "Piston promised me the East. He promises you the scrapyard.",
        "reb_06_betrayal_5": "Bolton, get our people out! I'll hold them.",
        "reb_06_last_1": "Go... The rebellion lives as long as one of you does.",
        "reb_06_last_2": "Captain!..",
        "reb_06_epilogue_1": "He held them off alone. Every last one of us got out.",
        "reb_06_epilogue_2": "Vapora and Piston will answer for this. The whole East will remember his name.",
    },
}
REBEL_NAMES = {  # crew re-skin names: persona -> (en, ru)
    "daisy": ("Scope", "Прицел"), "wesley": ("Fuse", "Фитиль"), "judy": ("Anvil", "Наковальня"),
    "crow": ("Rook", "Грач"), "diver": ("Ballast", "Балласт"), "chimney": ("Boiler", "Котёл"),
    "poe": ("Quill", "Перо"), "cornelius": ("Brass", "Латунь"), "adventure_boy": ("Sprocket", "Шестерёнка"),
    "cyclop": ("Lens", "Линза"),
}
REBEL_DESC = ("A navy deserter who joined the rebellion.", "Дезертир флота, примкнувший к восстанию.")


def quest(i):
    return f"{P}q{i + 1}"


def guid(*parts):
    return str(uuid.uuid5(NS, ":".join(parts)))


# ---- missions ------------------------------------------------------------------------------------------------------
def clone_mission(text, m):
    """Copy of the original mission entry (layout, level, reinforcements) with our rules, script, spawns."""
    src = re.search(rf'\t<Mission Name="{m["clone"]}".*?</Mission>\r?\n', text, re.S)
    b = src.group(0)
    for g in sorted(set(re.findall(GUID, b))):
        b = b.replace(g, guid(m["name"], g))
    b = b.replace(f'Name="{m["clone"]}"', f'Name="{m["name"]}"', 1)
    b = re.sub(r"\t*<Parameters>.*?</Parameters>\r?\n", "", b, flags=re.S)
    b = re.sub(r"\t*<IntroConversation>.*?</IntroConversation>\r?\n", "", b)
    b = re.sub(r"\t*<QuestLocked\w*>[^<]*</QuestLocked\w*>\r?\n", "", b)
    b = re.sub(r"<Rules>.*?</Rules>", f"<Rules>{m['name']}</Rules>", b)
    b, n = re.subn(r"<Script>.*?</Script>", f"<Script>Lua\\\\Missions\\\\{m['name']}.lua</Script>", b)
    assert n == 1, m["clone"]
    b = re.sub(r"<CrewSize>\d+</CrewSize>", f"<CrewSize>{m['crew']}</CrewSize>", b)
    extra = []
    if m.get("target"):
        extra.append(f'<Parameters>\n\t\t\t<Parameter Name="Target" Value="{m["target"]}" />\n\t\t</Parameters>')
    if m["spawn_table"]:
        b, n = re.subn(r"<InitialSpawnTable>\w*</InitialSpawnTable>", f"<InitialSpawnTable>{m['spawn_table']}</InitialSpawnTable>", b)
        if not n:
            extra.append(f"<InitialSpawnTable>{m['spawn_table']}</InitialSpawnTable>")
    item, count = m["loot"]
    b, n = re.subn(r'(<MasterLoot[^>]*>\s*<Item Definition=")[^"]+(" Count=")\d+', rf"\g<1>{item}\g<2>{count}", b, count=1)
    assert n == 1, m["clone"]
    # spawn marker for allies in the start room: an existing one (allies spawn anywhere in its room), else marker 0
    # (start rooms have one)
    allies = m["allies"]
    if m["start_marker"]:
        room = re.search(r'<Entrance Room="([^"]+)" Entrance="\d+" Name="start"', b).group(1)
        old = re.search(rf'<SpawnMarker Room="{room}" SpawnMarker="\d+" Name="(\w+)"', b)
        if old:
            allies = [(e, old.group(1) if mk == m["start_marker"] else mk, n) for e, mk, n in allies]
        else:
            b, n = re.subn(r"(<SpawnMarkers>)", rf'\1\n\t\t\t<SpawnMarker Room="{room}" SpawnMarker="0" Name="{m["start_marker"]}" />', b, count=1)
            assert n == 1
    # fixed spawns: keep the listed originals (by editor name or marker), add the allies
    fixed = re.search(r"(\t*)<FixedSpawns[^>]*>(.*?)</FixedSpawns>\r?\n", b, re.S)
    keep = []
    if fixed:
        for e in re.findall(r"\t*<Entry [^>]*/>", fixed.group(2)):
            ed = re.search(r'EditorName="(\w*)"', e).group(1)
            mk = re.search(r'Marker="(\w*)"', e).group(1)
            if ed in m["keep_fixed"] or mk in m["keep_fixed"]:
                keep.append(e.strip())
        b = b[:fixed.start()] + b[fixed.end():]
    for ent, marker, name in allies:
        keep.append(f'<Entry Name="{ent}" Marker="{marker}" ScriptTrigger="" EditorName="{name}" RandomPositionInRoom="True" />')
    if keep:
        extra.append('<FixedSpawns Inherit="false">\n' + "".join(f"\t\t\t{e}\n" for e in keep) + "\t\t</FixedSpawns>")
    if m.get("gather"):
        z, mk = m["gather"]
        b = re.sub(r"\t*<GatherZones.*?</GatherZones>\r?\n", "", b, flags=re.S)
        extra.append(f'<GatherZones Inherit="false">\n\t\t\t<Entry ZoneName="{z}" MarkerName="{mk}" RequirementType="RemainingCrew" '
                     f'RequiredCrew="{m["crew"]}" ManualSize="0" />\n\t\t</GatherZones>')
    close = b.rfind("\t</Mission>")
    b = b[:close] + "".join(f"\t\t{x}\n" for x in extra) + b[close:]
    nl = "\r\n" if "\r\n" in text else "\n"
    return src.end(), b.replace("\r\n", "\n").replace("\n", nl)


def rules_xml(m, nl):
    groups = []
    for d, sid, ptr in m["objectives"]:
        attrs = f'Definition="{d}" DisplayStringId="{sid}"'
        if d == "kill_named_enemy":
            attrs += ' TargetName="$Target" Count="1"'
        elif d == "enter_gather":
            attrs += f' TargetName="{ptr}" PointerTargets="{ptr}"'
        elif ptr:
            attrs += f' PointerTargets="{ptr}" PointerInFOW="true"'
        groups.append(f"\t\t\t<Group>\n\t\t\t\t<Objective {attrs} />\n\t\t\t</Group>\n")
    params = ('\t\t<Parameters>\n\t\t\t<Parameter Name="Target" Default="" Type="String"/>\n\t\t</Parameters>\n'
              if m.get("target") else "")
    return (f'\t<MissionRules Name="{m["name"]}">\n{params}\t\t<Objectives>\n' + "".join(groups)
            + "\t\t</Objectives>\n\t</MissionRules>\n").replace("\n", nl)


LUA_COMMON = '''-- RebelCampaign (tools/mods/make_rebel_campaign.py)

-- player crew only (GetFriendlyActors may include the allied townsfolk)
function RebCrew()
	return GetActorsWithFaction("cowbots") or {}
end

function RebNear(a, b, r)
	local p, q = GetEntityPosition(a), GetEntityPosition(b)
	local dx, dy = p.x - q.x, p.y - q.y
	return dx * dx + dy * dy <= r * r
end

function RebNearCrew(e, r)
	for _, c in ipairs(RebCrew()) do
		if IsActor(c) and RebNear(c, e, r) then return true end
	end
	return false
end

-- allies spawn unalerted like enemies: wake them up
function RebWakeAllies(names)
	for _, n in ipairs(names) do
		local a = FindEntityWithName(n)
		if a and IsActor(a) then SetActorAlert(a, true) end
	end
end

function RebBark(e, key)
	if e and IsActor(e) then SpawnWorldText(e, "worldtext_speak_important", key) end
end

-- captive / hidden ally: no faction (nobody acts on it, nobody targets it), untouchable until freed
function RebHold(name)
	local a = FindEntityWithName(name)
	if a then
		SetActorFaction(a, "")
		AddModifier(a, "reb_captive")
	end
	return a
end

function RebFree(a)
	RemoveModifier(a, "reb_captive")
	SetActorFaction(a, "townsfolk")
	SetActorAlert(a, true)
end
'''

LUA = {}
LUA["reb_01"] = '''
held = false
found = false

function OnMissionStart()
	held = RebHold("reb_spark") ~= nil
end

function OnCrewDeployed()
	if not held then held = RebHold("reb_spark") ~= nil end
	RebWakeAllies({"reb_bolton"})
	StartConversation("con_reb_01_start")
end

function OnActionFinished()
	if found then return end
	local spark = FindEntityWithName("reb_spark")
	if spark and RebNearCrew(spark, 720) then
		found = true
		RebFree(spark)
		CameraPanToEntity(spark)
		StartConversation("con_reb_01_found")
		CompleteNamedObjective("manual")
		ClearCameraTarget()
	end
end

function OnEntityKilled(entity)
	if EntityIsNamed(entity, "navy_leader_1") then
		StartConversation("con_reb_01_officer")
	elseif EntityIsNamed(entity, "reb_bolton") or EntityIsNamed(entity, "reb_spark") then
		RebBark(RebCrew()[1], "reb_ally_down")
	end
end
'''
LUA["reb_02"] = '''
vip = nil
lost = false

function OnCrewDeployed()
	SpawnCrewmatesAtEntrance({"reb_spark"}, "start")
	vip = FindEntityWithName("reb_spark")
	if vip == nil then  -- fallback: Spark as an AI ally if a non-recruited persona can't be spawned
		SpawnActorsAtEntrance({"reb_spark_c"}, {"reb_spark"}, "start")
		vip = FindEntityWithName("reb_spark")
		if vip then SetActorAlert(vip, true) end
	end
	StartConversation("con_reb_02_start")
end

function OnEntityPickedUp(entityName)
	if entityName == "pickup_master_loot" then StartConversation("con_reb_02_codes") end
end

function OnEntityKilled(entity)
	if not lost and EntityIsNamed(entity, "reb_spark") then
		lost = true
		StartConversation("con_reb_02_lost")
		EndMissionInDefeat()
	end
end
'''
LUA["reb_03"] = '''
allies = {"reb_bolton", "reb_rusty", "reb_lever"}

function OnCrewDeployed()
	RebWakeAllies(allies)
	StartConversation("con_reb_03_start")
end

function OnMissionResume()
	RebWakeAllies(allies)
end

function OnEntityKilled(entity)
	for _, n in ipairs(allies) do
		if EntityIsNamed(entity, n) then RebBark(RebCrew()[1], "reb_ally_down") end
	end
	if GetActorFaction(entity) == "navy" and #(GetEnemyActors() or {}) == 0 then
		StartConversation("con_reb_03_end")
	end
end
'''
LUA["reb_04"] = '''
captives = {"reb_captive_1", "reb_captive_2", "reb_captive_3", "reb_captive_4"}
freed = {}
held = false
done = false

function HoldAll()
	if held then return end
	for _, n in ipairs(captives) do
		if RebHold(n) then held = true end
	end
end

function OnMissionStart()
	HoldAll()
end

function OnCrewDeployed()
	HoldAll()
	RebWakeAllies({"reb_bolton"})
	StartConversation("con_reb_04_start")
end

function OnActionFinished()
	if done then return end
	local left = 0
	for _, n in ipairs(captives) do
		local a = FindEntityWithName(n)
		if a and IsActor(a) and not freed[n] then
			if RebNearCrew(a, 720) then
				freed[n] = true
				RebFree(a)
				RebBark(a, "reb_04_freed")
			else
				left = left + 1
			end
		end
	end
	if left == 0 then
		done = true
		StartConversation("con_reb_04_all_free")
		CompleteNamedObjective("manual")
	end
end
'''
LUA["reb_05"] = '''
allies = {"reb_bolton", "reb_lever", "reb_rebel_1", "reb_rebel_2"}
phase = 0  -- 0 docks, 1 holding the square, 2 falling back, 3 holding the docks, 4 done

function OnCrewDeployed()
	RebWakeAllies(allies)
	StartConversation("con_reb_05_start")
end

function OnMissionResume()
	RebWakeAllies(allies)
end

function OnDoorOpened(door)
	if phase == 0 and EntityIsNamed(door, "d1") then
		phase = 1
		CompleteNamedObjective("manual")
		StartConversation("con_reb_05_hold")
		AddTimerCounter("reb_hold", 5, 0)
		TriggerReinforcements("0")
	end
end

function OnCounterChanged(counterName, count, countMax, complete)
	if counterName == "reb_hold" then
		if count == 4 then TriggerReinforcements("2")
		elseif count == 3 then TriggerReinforcements("3")
		elseif count == 2 then TriggerReinforcements("5")
		elseif count == 1 then TriggerReinforcements("4") TriggerReinforcements("6") end
	elseif counterName == "reb_docks" then
		if count == 2 then TriggerReinforcements("8") TriggerReinforcements("9")
		elseif count == 1 then TriggerReinforcements("10") end
	end
end

function OnCounterRemoved(counterName)
	if counterName == "reb_hold" and phase == 1 then
		phase = 2
		CompleteNamedObjective("manual_1")
		StartConversation("con_reb_05_fallback")
		EnableGatherZone("reb_fallback", true)
		TriggerReinforcements("7")
	elseif counterName == "reb_docks" and phase == 3 then
		phase = 4
		StartConversation("con_reb_05_end")
		CompleteNamedObjective("manual_2")
	end
end

function OnZoneGathered(zone)
	if zone == "reb_fallback" and phase == 2 then
		phase = 3
		EnableGatherZone("reb_fallback", false)
		StartConversation("con_reb_05_docks")
		AddTimerCounter("reb_docks", 3, 0)
	end
end

function OnEntityKilled(entity)
	for _, n in ipairs(allies) do
		if EntityIsNamed(entity, n) then RebBark(RebCrew()[1], "reb_ally_down") end
	end
end
'''
LUA["reb_06"] = '''
entityBoss = nil
phase = 0  -- 0 fight together, 1 betrayal queued, 2 last stand, 3 ending
allies = {@ALLY_NAMES@}

function OnCrewDeployed()
	entityBoss = FindEntityWithName("navy_boss")
	RegisterHealthPointTrigger("RebQueueBetrayal", entityBoss, 40, false)
	PlayMusic("caribbea_roaming")
	-- the level's own rebels belong to the original story
	for _, n in ipairs({"hub_c_navy_hr_revolution", "revolutionary_1", "revolutionary_2", "revolutionary_doorman_1"}) do
		local e = FindEntityWithName(n)
		if e then DestroyEntity(e) end
	end
	RelocateTeam("rt1")
	SetCameraTargetEntity(entityBoss)
	PlayMusicOnce("boss_mech_intro_drone")
	Wait(1.0)
	StartConversation("con_reb_06_piston")
	MoveActorToNamed(entityBoss, "pos_boss_intro_target_1")
	Wait(1.0)
	SetEntityPosition(entityBoss, GetNamedPoint("pos_boss_intro_target_2"), true)
	SnapActorToGround(entityBoss)
	SetEntityDirection(entityBoss, Vector.Right)
	local door1 = FindEntityWithName("ph_door_into_arena_beach")
	local door2 = FindEntityWithName("ph_door_into_arena_balcony")
	OpenDoorImmediate(door1)
	ClearCameraTarget()
	CameraPanToNamed("pos_cam_intro_target")
	HoldCamera()
	RelocateTeam("rt2")
	WaitForActionsToFinish()
	CloseDoorImmediate(door1)
	LockDoorImmediate(door1)
	CloseDoorImmediate(door2)
	LockDoorImmediate(door2)
	OpenNamedDoorImmediate("ph_trapdoor")
	HideFOWByMarker("marker_outside")
	-- the rebels storm in with us
@SPAWN_ALLIES@
	RebWakeAllies(allies)
	ClearCameraTarget()
	StartConversation("con_reb_06_start")
	SetActorAlert(entityBoss, true)
	SetAlarmActive(true)
	EnableNamedTurret("ph_turret_phase1", true)
	SpawnActorsAtEntrance({"enemy_navy_guard_elite"}, {"enemy_navy_guard_elite"}, "entrance_top_right", "pos_guard_start")
	SpawnActorsAtEntrance({"enemy_navy_seabot_elite"}, {"enemy_navy_seabot_elite"}, "entrance_top_left")
	SetAlarmTier(0, 2)
	PlayMusic("boss_mech", 0)
	SaveCheckpoint()
end

function OnMissionResume()
	if phase < 2 then RebWakeAllies(allies) end
end

function RebQueueBetrayal()
	if phase == 0 then phase = 1 end
end

function RebBetrayal()
	phase = 2
	local vapora = FindEntityWithName("reb_vapora")
	if entityBoss and IsActor(entityBoss) then
		CameraPanToEntity(entityBoss)
		AddModifier(entityBoss, "reb_shielded")
	end
	StartConversation("con_reb_06_betrayal")
	if vapora and IsActor(vapora) then
		SetActorFaction(vapora, "navy")
		SetActorAlert(vapora, true)
	end
	ClearCameraTarget()
	CompleteNamedObjective("manual")
	ChangeAlarmTier(2)
	EnableNamedTurret("ph_turret_phase2", true)
	AddTimerCounter("reb_last_stand", 4, 0)
	SaveCheckpoint()
end

function OnFactionTurnBegin(faction)
	if faction == "navy" and phase == 1 then RebBetrayal() end
end

function OnEntityKilled(entity)
	if entity == entityBoss and phase < 2 then
		-- Piston fell early: Vapora takes his place anyway
		RebBetrayal()
	end
end

function OnCounterChanged(counterName, count, countMax, complete)
	if counterName == "reb_last_stand" and count == 2 then
		SpawnActorsAtEntrance({"enemy_navy_guard_elite"}, {"enemy_navy_guard_elite"}, "entrance_top_right")
		SpawnActorsAtEntrance({"enemy_navy_seabot_elite"}, {"enemy_navy_seabot_elite"}, "entrance_top_left")
	end
end

function OnCounterRemoved(counterName)
	if counterName == "reb_last_stand" and phase == 2 then
		phase = 3
		RebEnding()
	end
end

function RebEnding()
	local crew = RebCrew()
	local captain = crew[1]
	for _, c in ipairs(crew) do
		if EntityHasDefinition(c, "crew_captain") then captain = c end
	end
	SetAlarmActive(false)
	if captain then CameraPanToEntity(captain) end
	StartConversation("con_reb_06_last")
	-- quest first: if the captain is the last one standing, his fall ends the mission
	CompleteNamedObjective("manual_1")
	CompleteQuest("@LAST_QUEST@")
	if captain and IsActor(captain) then KillObject(captain) end
	Wait(2.0)
	ClearCameraTarget()
	PlayLingeringHeroMusicOnce("spg_dancing_in_our_graves_short_2min")
	StartConversation("con_reb_06_epilogue")
	FadeOut(2.0)
	EndCredits()
	EndMissionInVictory(1.0)
end
'''


def finale_lua():
    spawn = "".join(f'\tSpawnEntity("{e}", Vector({x}, {y}), Vector.Left, "{n}")\n'
                    f'\tSnapActorToGround(FindEntityWithName("{n}"))\n' for e, n, x, y in FINALE_ALLY_SPOTS)
    names = ", ".join(f'"{n}"' for _, n, _, _ in FINALE_ALLY_SPOTS)
    return LUA["reb_06"].replace("@SPAWN_ALLIES@", spawn.rstrip("\n")).replace("@ALLY_NAMES@", names) \
        .replace("@LAST_QUEST@", quest(len(MISSIONS) - 1))


def mission_lua(m):
    body = finale_lua() if m["name"] == "reb_06" else LUA[m["name"]]
    return (LUA_COMMON + body).replace("\n", "\r\n")


# ---- entities, personas, modifiers ---------------------------------------------------------------------------------
def ally_entities(nl):
    out = []
    for name, (tpl, model, weapon, hp, hat) in ALLIES.items():
        actor = f"\t\t\t<Faction>townsfolk</Faction>\n\t\t\t<NameStringId>{name}</NameStringId>\n\t\t\t<SpawnCost>0</SpawnCost>\n"
        if hat:
            actor += f"\t\t\t<Hat>{hat}</Hat>\n\t\t\t<RareHat Chance=\"0\">{hat}</RareHat>\n"
        if weapon:
            actor += f"\t\t\t<Weapon>{weapon}</Weapon>\n"
        skin = f"\t\t<SkinnedCharacter>\n\t\t\t<File>{model}</File>\n\t\t</SkinnedCharacter>\n" if model else ""
        out.append(f'\t<Entity Name="{name}" Template="{tpl}">\n\t\t<MinimapIcon>\n\t\t\t<Icon>Textures/Minimap/icon_npc</Icon>\n'
                   f'\t\t</MinimapIcon>\n\t\t<Health>\n\t\t\t<HitPoints>{hp}</HitPoints>\n\t\t</Health>\n'
                   f"\t\t<Actor>\n{actor}\t\t</Actor>\n{skin}\t</Entity>\n\n")
    return "<!-- RebelCampaign allies (tools/mods/make_rebel_campaign.py) -->\n" + "".join(out).replace("\n", nl)


def copy_entity(ents, old, new, conv=None):
    b = re.search(rf'\t<Entity Name="{old}".*?</Entity>', ents, re.S).group(0)
    b = b.replace(f'Name="{old}"', f'Name="{new}"', 1)
    if conv:
        if "<Conversation>" in b:
            b = re.sub(r"<Conversation>[^<]*</Conversation>", f"<Conversation>{conv}</Conversation>", b)
        else:
            b, n = re.subn(r"(<Actor>)", rf"\1\n\t\t\t<Conversation>{conv}</Conversation>", b, count=1)
            if not n:
                b = b.replace("</Entity>", f"\t<Actor>\n\t\t\t<Conversation>{conv}</Conversation>\n\t\t</Actor>\n\t</Entity>")
        b = re.sub(r"<ConversationCannotEndDayFallback>[^<]*</ConversationCannotEndDayFallback>",
                   f"<ConversationCannotEndDayFallback>{conv}</ConversationCannotEndDayFallback>", b)
    b = re.sub(r"[ \t]*<Hat>hat_shop_top</Hat>", "\t\t\t<Hat>hat_revolution_beret</Hat>", b)
    return b


def npc_entities(ents, nl):
    out = [copy_entity(ents, old, new, new) for new, (old, _) in BAR_NPCS.items()]
    bt, old = BAR["bartender"]
    out.append(copy_entity(ents, old, bt, bt))
    return "".join(x.replace("\r\n", "\n").replace("\n", nl) + nl + nl for x in out)


MODIFIERS = '''	<!-- RebelCampaign (tools/mods/make_rebel_campaign.py): captive / hidden ally until freed; Piston after the betrayal -->
	<Modifier Name="reb_captive">
		<StatusFlags>invulnerable, hide_health_bar, harmless, immaterial</StatusFlags>
		<AnimationInput>emptyhanded</AnimationInput>
	</Modifier>
	<Modifier Name="reb_shielded">
		<StatusFlags>invulnerable</StatusFlags>
	</Modifier>
'''


def vip_persona(personas, nl):
    b = re.search(rf'\t<Persona Name="{VIP["copy"]}".*?</Persona>', personas, re.S).group(0)
    b = b.replace(f'Name="{VIP["copy"]}"', f'Name="{VIP["persona"]}"', 1)
    b = re.sub(r"<TacticsEntity>[^<]*</TacticsEntity>", f"<TacticsEntity>{VIP['entity']}</TacticsEntity>", b)
    b = re.sub(r"\s*<PersonalConversations>.*?</PersonalConversations>", "", b, flags=re.S)
    b = re.sub(r"\s*<RecruitConversation [^>]*/>", "", b)
    return b.replace("\r\n", "\n").replace("\n", nl) + nl


def vip_entity(crew, nl):
    b = re.search(rf'\t<Entity Name="{VIP["copy_entity"]}".*?</Entity>', crew, re.S).group(0)
    b = b.replace(f'Name="{VIP["copy_entity"]}"', f'Name="{VIP["entity"]}"', 1)
    b = re.sub(r"<File>[^<]*</File>", f"<File>{VIP['model']}</File>", b, count=1)
    b = re.sub(r"\s*<Conversation>[^<]*</Conversation>", "", b)
    return b.replace("\r\n", "\n").replace("\n", nl) + nl + nl


def reskin_crew(crew):
    for persona, model in CREW_SKINS.items():
        m = re.search(rf'\t<Entity Name="crew_{persona}".*?</Entity>', crew, re.S)
        b, n = re.subn(r"<File>Characters/Crew/[^<]*</File>", f"<File>{model}</File>", m.group(0), count=1)
        assert n == 1, persona
        crew = crew[:m.start()] + b + crew[m.end():]
    return crew


# ---- quests, conversations -----------------------------------------------------------------------------------------
def quests_xml(nl):
    out = [f'\t<Quest Name="{P}intro" Template="BASE">\n\t\t<Objectives>\n\t\t\t<Objective>\n\t\t\t\t<Condition>none</Condition>\n'
           '\t\t\t</Objective>\n\t\t</Objectives>\n\t</Quest>\n']
    for i, m in enumerate(MISSIONS):
        q = quest(i)
        unlock = quest(i - 1) if i else f"{P}intro"
        home = ' MapScript="RebSetHome"' if i == 0 else ""
        out.append(f'\t<Quest Name="{q}" Template="BASE">\n\t\t<UnlockedBy>{unlock}</UnlockedBy>\n'
                   f'\t\t<OnUnlocked MapConversation="con_{MAP_CONV[i]}"{home} />\n'
                   f'\t\t<Objectives>\n\t\t\t<Objective>\n\t\t\t\t<LocalizedStringId>quest_{q}_obj</LocalizedStringId>\n'
                   f'\t\t\t\t<Condition>mission</Condition>\n\t\t\t\t<Parameter>{m["name"]}</Parameter>\n'
                   '\t\t\t</Objective>\n\t\t</Objectives>\n\t</Quest>\n')
        if i:  # hidden lock: mission i stays locked until quest i-1 is complete
            out.append(f'\t<Quest Name="{P}lock_{i + 1}" Template="BASE">\n\t\t<LockMissions>\n\t\t\t<Mission>{m["name"]}</Mission>\n'
                       f'\t\t</LockMissions>\n\t\t<Objectives>\n\t\t\t<Objective>\n\t\t\t\t<Condition>quest</Condition>\n'
                       f'\t\t\t\t<Parameter>{quest(i - 1)}</Parameter>\n\t\t\t</Objective>\n\t\t</Objectives>\n\t</Quest>\n')
    return "".join(out).replace("\n", nl)


class Ids:
    def __init__(self):
        self.k = 0

    def __call__(self):
        self.k += 1
        return f"rb{self.k:04d}"


def conversations_xml(nl):
    nid = Ids()
    sp = lambda who, key, extra="": f'<Speak Speaker="{who}" Text="{key}"{extra} Id="{nid()}" />'
    out = []
    for name, lines in CONV.items():
        out.append(f'\t<Conversation Name="{name}">\n' + "".join(f"\t\t{sp(w, k)}\n" for w, k in lines) + "\t</Conversation>\n")
    for key in MAP_CONV:
        out.append(f'\t<Conversation Name="con_{key}">\n\t\t{sp("captain", key)}\n\t</Conversation>\n')
    to_captain = ' SpeakTo="captain"'
    for npc, stages in TALK.items():
        body = ""
        for q, key in stages:
            if q:
                body += (f'\t\t<Case If="quest_complete:{q}" Id="{nid()}">\n\t\t\t<Children>\n'
                         f'\t\t\t\t{sp(npc, key, to_captain)}\n'
                         f'\t\t\t\t<Jump TargetConversation="{npc}" TargetLabel="end" Id="{nid()}" />\n'
                         '\t\t\t</Children>\n\t\t</Case>\n')
            else:
                body += f'\t\t{sp(npc, key, to_captain)}\n'
        out.append(f'\t<Conversation Name="{npc}">\n{body}\t\t<Wait Label="end" Id="{nid()}" />\n\t</Conversation>\n')
    bt = BAR["bartender"][0]
    out.append(f'''	<Conversation Name="{bt}">
		<Ask Label="questions" Speaker="{bt}" Text="reb_bartender_hello" Cancelable="True" Id="{nid()}">
			<Options>
				<Item Text="dialogue_option_end_day" AlwaysEmphasized="True" />
				<Item Text="dialogue_option_farewell" AlwaysEmphasized="True" />
			</Options>
		</Ask>
		<Case If="answer:0" Id="{nid()}">
			<Children>
				<Case If="can_end_day" Id="{nid()}">
					<Children>
						{sp("captain", "reb_bartender_rest", f' SpeakTo="{bt}"')}
						<Jump TargetConversation="{bt}" TargetLabel="end_day" Id="{nid()}" />
					</Children>
				</Case>
				{sp(bt, "reb_bartender_tired")}
				<Jump TargetConversation="{bt}" TargetLabel="bar_end" Id="{nid()}" />
			</Children>
		</Case>
		<Case Id="{nid()}">
			<Children>
				<Jump TargetConversation="{bt}" TargetLabel="bar_end" Id="{nid()}" />
			</Children>
		</Case>
		<EndDay Label="end_day" Id="{nid()}" />
		<Wait Label="bar_end" Id="{nid()}" />
	</Conversation>
''')
    return "".join(out).replace("\n", nl)


def speakers_xml(speakers, nl):
    out = []
    for new, (old, _) in BAR_NPCS.items():
        tpl = old if f'<Speaker Name="{old}"' in speakers else "DEFAULT_NPC"
        out.append(f'\t<Speaker Name="{new}" Template="{tpl}" />\n\t<Speaker Name="{new}_c" Template="{tpl}" />\n')
    bt, old = BAR["bartender"]
    out.append(f'\t<Speaker Name="{bt}" Template="{old}" />\n')
    return "".join(out).replace("\n", nl)


# ---- bar -----------------------------------------------------------------------------------------------------------
def clone_bar(text):
    src = re.search(rf'\t<Mission Name="{BAR["clone"]}".*?</Mission>\r?\n', text, re.S)
    b = src.group(0)
    for g in sorted(set(re.findall(GUID, b))):
        b = b.replace(g, guid(BAR["bar"], g))
    b = b.replace(f'Name="{BAR["clone"]}"', f'Name="{BAR["bar"]}"', 1)
    b, n = re.subn(r"<Patchset>[^<]*</Patchset>", f"<Patchset>Patchsets/{BAR['bar']}</Patchset>", b)
    assert n == 1
    b = re.sub(r"\t*<ToggleLayers.*?</ToggleLayers>\r?\n", "", b, flags=re.S)  # the level copy decides who stands there
    assert "<Script>" not in b
    close = b.rfind("\t</Mission>")
    nl = "\r\n" if "\r\n" in text else "\n"
    b = b[:close] + f"\t\t<Script>Lua\\Missions\\{BAR['bar']}.lua</Script>{nl}" + b[close:]
    return src.end(), b


def clone_level(le):
    """Copy of the East bar level: one NPC layer kept (always shown, NPCs swapped for ours), story layers emptied."""
    le = le.replace(f"<Name>{BAR['clone']}</Name>", f"<Name>{BAR['bar']}</Name>", 1)
    le = le.replace(f"<FileName>{BAR['clone']}.le</FileName>", f"<FileName>{BAR['bar']}.le</FileName>", 1)
    le = re.sub(r"<GuidIdentifier>[^<]*</GuidIdentifier>", f"<GuidIdentifier>{guid(BAR['bar'], 'level')}</GuidIdentifier>", le, count=1)
    swap = {old_in_level: new for new, (_, old_in_level) in BAR_NPCS.items()}
    placed = []

    def layer(m):
        ents, props = m.group(1), m.group(2)
        h = re.search(r"(<Name>HideLayerName</Name>\s*<Type>\w+</Type>\s*<Value>)([^<]*)(</Value>)", props)
        name = h.group(2) if h else ""
        if name in BAR["drop_layers"]:
            ents = re.sub(r"\s*<CustomEntity>(?:(?!</CustomEntity>).)*?<Definition>(?:npc_slot|cutscene_npc)</Definition>\s*</CustomEntity>",
                          "", ents, flags=re.S)
        elif name in BAR["keep_layers"]:
            def npc(e):
                v = re.search(r"(<Name>NPCDefinition</Name>\s*<Type>\w+</Type>\s*<Value>)(\w+)(</Value>)", e.group(0))
                if not v or v.group(2) not in swap:
                    return ""  # nobody else stands in the HQ
                new = swap[v.group(2)]
                placed.append(new)
                x = e.group(0).replace(v.group(0), v.group(1) + new + v.group(3))
                return re.sub(r"<Name>npc_slot\w*</Name>", f"<Name>{new}</Name>", x, count=1)
            def slot(e):
                x = npc(e)
                return x and "\n\t\t\t\t\t\t" + x.strip()
            ents = re.sub(r"\s*<CustomEntity>(?:(?!</CustomEntity>).)*?<Definition>npc_slot</Definition>\s*</CustomEntity>",
                          slot, ents, flags=re.S)
            props = props.replace(h.group(0), h.group(1) + h.group(3))  # always shown
        return f"<Entities>{ents}</Entities>\n\t\t\t\t\t<Properties>{props}</Properties>"

    le = re.sub(r"<Entities>(.*?)</Entities>\s*<Properties>(.*?)</Properties>", layer, le, flags=re.S)
    placed = sorted(set(placed))
    assert placed == sorted(BAR_NPCS), f"bar NPCs placed: {placed}"
    bt, old = BAR["bartender"]
    for tag in ("Name", "Definition"):
        le, n = re.subn(rf"<{tag}>{old}</{tag}>", f"<{tag}>{bt}</{tag}>", le)
        assert n == 1, f"{old}: {n} <{tag}>"
    return le


INTRO_LUA = '''-- RebelCampaign intro scene (intermission_cutscenes.xml: reb_intro), tools/mods/make_rebel_campaign.py
%(place)s
function Init()
	RebPlaceCaptain()
end

function Run()
	Wait(1.0)
	StartConversation("con_reb_intro")
	CompleteQuest("reb_intro")
end
'''
BAR_LUA = '''-- RebelCampaign HQ, tools/mods/make_rebel_campaign.py
%(place)s
-- fallback in case the intro cutscene does not trigger when a new game starts inside the bar
function OnMissionStart()
	if IsQuestActive("reb_intro") then
		RebPlaceCaptain()
		StartConversation("con_reb_intro")
		CompleteQuest("reb_intro")
	end
end
'''
PLACE_CAPTAIN_LUA = '''
function RebPlaceCaptain()
	local captain = FindEntityWithName("captain")
	local host = FindEntityWithName("%(host)s")
	if captain and host then
		local p = GetEntityPosition(host)
		SetEntityPosition(captain, Vector(p.x + (%(dx)d), p.y), true)
		SnapActorToGround(captain)
		SetEntityDirection(captain, %(dir)s)
	end
end
'''
WORLDMAP_LUA = '''

-- RebelCampaign (tools/mods/make_rebel_campaign.py): the rebel HQ is the home bar. Run by quest reb_q1 OnUnlocked.
function RebSetHome()
	SetHomeBar("%s", false, true)
end
'''


def intro_cutscene_xml(nl):
    bar = BAR["bar"]
    return f'''	<IntermissionCutscene Name="{P}intro" Template="base">
		<Mission>{bar}</Mission>
		<ScriptFile>Lua/Intermissions/{P}intro.lua</ScriptFile>
		<NextMission>{bar}</NextMission>
		<Priority>10</Priority>
		<Trigger Type="EnterRoamingMission" Value="{bar}"/>
		<Filters>
			<Filter Type="QuestActive" Value="{P}intro"/>
		</Filters>
		<StartingBannerTextId>reb_banner</StartingBannerTextId>
	</IntermissionCutscene>
'''.replace("\n", nl)


# ---- outset --------------------------------------------------------------------------------------------------------
def outset_state(text, name):
    """Completed quests/cutscenes/missions of an outset including its templates (children merge unless Inherit="false")."""
    chain = []
    while name:
        m = re.search(rf'<Outset Name="{name}"([^>]*)>(.*?)</Outset>', text, re.S)
        chain.append(m.group(2))
        t = re.search(r'Template="(\w+)"', m.group(1))
        name = t.group(1) if t else None
    out = ""
    for tag in ("CompletedQuests", "CompletedCutscenes", "CompletedMissions"):
        parts = []
        for body in chain:
            m = re.search(rf"<{tag}(\s+Inherit=\"(\w+)\")?\s*>(.*?)</{tag}>", body, re.S)
            if m:
                parts.append(m.group(3))
                if (m.group(2) or "").lower() == "false":
                    break
        out += f"<{tag}>{''.join(parts)}</{tag}>"
    return out


def outset_xml(blocked, nl):
    cut = "".join(f"\n\t\t\t<Cutscene>{c}</Cutscene>" for c in blocked)
    return (f'\t<Outset Name="{P}new_game" Template="{BAR["outset_base"]}">\n'
            f'\t\t<ShipStartPosition>{BAR["start"]}</ShipStartPosition>\n\t\t<Mission>{BAR["bar"]}</Mission>\n'
            f'\t\t<MissionRegion>{BAR["region"]}</MissionRegion>\n\t\t<CompletedCutscenes>{cut}\n\t\t</CompletedCutscenes>\n'
            '\t</Outset>\n').replace("\n", nl)


# ---- map -----------------------------------------------------------------------------------------------------------
def free_spots(text, ax, ay, icons, count):
    """Deterministic search for icon spots around the anchor: inside the region, open water, >= 3 tiles apart."""
    found = []
    for r in range(3, 16):
        ring = [(dx, dy) for dx in range(-r, r + 1) for dy in range(-r, r + 1) if max(abs(dx), abs(dy)) == r]
        for dx, dy in sorted(ring, key=lambda d: (d[1], d[0])):
            x, y = ax + dx * TILE, ay + dy * TILE
            if not in_region(text, BAR["region"], x, y) or not surface_open(text, x, y):
                continue
            if all(abs(ix - x) + abs(iy - y) >= 3 * TILE for ix, iy in icons + found):
                found.append((x, y))
                if len(found) == count:
                    return found
    raise AssertionError(f"only {len(found)} icon spots found")


# ---- main ----------------------------------------------------------------------------------------------------------
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=DEFAULT_GAME)
    ap.add_argument("--out", help="output folder (default <game>/Mods/RebelCampaign)")
    ap.add_argument("--no-crew-skins", action="store_true", help="keep the original crew looks and names")
    a = ap.parse_args()
    backup = os.path.join(a.game, "SWH2ModLoader", "Backup", "Game.pak")
    pak = zipfile.ZipFile(backup if os.path.exists(backup) else os.path.join(a.game, "Bundle", "Core", "Game.pak"))
    out = a.out or os.path.join(a.game, "Mods", "RebelCampaign")
    if os.path.isdir(out):
        shutil.rmtree(out)

    def write(rel, data):
        path = os.path.join(out, *rel.split("/"))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as f:
            f.write(data)

    def read(rel):
        raw = pak.read(rel)
        return raw.decode("utf-8-sig"), (b"\xef\xbb\xbf" if raw.startswith(b"\xef\xbb\xbf") else b"")

    def nl_of(text):
        return "\r\n" if "\r\n" in text else "\n"

    def append(rel, block):
        text, bom = read(rel)
        close = re.search(r"</\w+>\s*$", text)
        text = text[:close.start()] + block(nl_of(text), text) + text[close.start():]
        write(rel, bom + text.encode())
        return text

    # 1. missions: our six + the HQ bar (all in the East file: they inherit its templates)
    rel = "Definitions/missions.east_caribbea.de"
    text, bom = read(rel)
    for m in MISSIONS:
        end, block = clone_mission(text, m)
        text = text[:end] + block + text[end:]
    end, block = clone_bar(text)
    text = text[:end] + block + text[end:]
    write(rel, bom + text.encode())
    append("Definitions/mission_rules.de", lambda nl, t: "".join(rules_xml(m, nl) for m in MISSIONS))
    for m in MISSIONS:
        write(f"Lua/Missions/{m['name']}.lua", mission_lua(m).encode())

    # 2. HQ: level copy, NPCs, scripts, intro cutscene, speakers
    le, bom = read(f"Patchsets/{BAR['clone']}.le")
    write(f"Patchsets/{BAR['bar']}.le", bom + clone_level(le).encode())
    place = PLACE_CAPTAIN_LUA % {"host": BAR["host"], "dx": BAR["captain_offset"],
                                 "dir": "Vector.Right" if BAR["captain_offset"] < 0 else "Vector.Left"}
    write(f"Lua/Missions/{BAR['bar']}.lua", (BAR_LUA % {"place": place}).replace("\n", "\r\n").encode())
    write(f"Lua/Intermissions/{P}intro.lua", (INTRO_LUA % {"place": place}).replace("\n", "\r\n").encode())
    append("Definitions/entities.npc.xml", lambda nl, t: npc_entities(t, nl))
    append("Definitions/entities.enemies.navy.xml", lambda nl, t: ally_entities(nl))
    append("Definitions/modifiers.xml", lambda nl, t: MODIFIERS.replace("\n", nl))
    append("Definitions/speakers.xml", lambda nl, t: speakers_xml(t, nl))
    ic_text = append("Definitions/intermission_cutscenes.xml", lambda nl, t: intro_cutscene_xml(nl))
    text, bom = read("Lua/WorldMap/worldmap.lua")
    write("Lua/WorldMap/worldmap.lua", bom + (text.rstrip() + WORLDMAP_LUA % BAR["bar"]).replace("\r\n", "\n").replace("\n", "\r\n").encode())

    # 3. crew: the escort VIP persona, optional rebel re-skin of the recruitable crew
    append("Definitions/personas.xml", lambda nl, t: vip_persona(t, nl))
    rel = "Definitions/entities.crew.xml"
    text, bom = read(rel)
    if not a.no_crew_skins:
        text = reskin_crew(text)
    close = re.search(r"</\w+>\s*$", text)
    text = text[:close.start()] + vip_entity(text, nl_of(text)) + text[close.start():]
    write(rel, bom + text.encode())

    # 4. new games start in the HQ (East start position); original cutscenes that could already play are marked seen
    rel = "Definitions/outsets.xml"
    text, bom = read(rel)
    blocked = cutscenes_playable_at_start(ic_text, outset_state(text, BAR["outset_base"]))
    append(rel, lambda nl, t: outset_xml(blocked, nl))
    rel = "Definitions/game_constants.xml"
    text, bom = read(rel)
    for tag in ("NewGameOutset", "NewGameOutsetSkipTutorial"):
        text, n = re.subn(rf"<{tag}>\w+</{tag}>", f"<{tag}>{P}new_game</{tag}>", text)
        assert n == 1, tag
    write(rel, bom + text.encode())

    # 5. quests: park every original quest behind its OWN never-quest (see make_test_campaign.py), then ours
    rel = "Definitions/quests.xml"
    text, bom = read(rel)
    parked = []

    def park(mq):
        head, name, body = mq.group(1), mq.group(2), mq.group(3)
        before = text[:mq.start()]
        if "Abstract" in head or before.rfind("<!--") > before.rfind("-->"):
            return mq.group(0)
        parked.append(name)
        body = re.sub(r"[ \t]*<UnlockedBy>[^<]*</UnlockedBy>[ \t]*\r?\n", "", body)
        return f"{head}\r\n\t\t<UnlockedBy>{PARK}{name}</UnlockedBy>{body}"

    text = re.sub(r'(<Quest Name="(\w+)"[^>]*>)(.*?</Quest>)', park, text, flags=re.S)
    base = re.search(r'<Quest Name="BASE"[^>]*>.*?</Quest>', text, re.S)
    nl = nl_of(text)
    text = text[:base.end()] + "".join(never_quest_xml(PARK + n, nl) for n in parked) + text[base.end():]
    close = re.search(r"</\w+>\s*$", text)
    text = text[:close.start()] + quests_xml(nl) + text[close.start():]
    check_unlock_order(text)
    write(rel, bom + text.encode())
    append("Definitions/conversations.xml", lambda nl, t: conversations_xml(nl))

    # 6. map: original mission icons out (shops stay), the Royal Blue icon now opens our HQ, our missions around it
    rel = "WorldMap/Maps/WorldMap.tmx"
    text, bom = read(rel)
    removed = []

    def drop(mo):
        if "shop" in mo.group(1):
            return mo.group(0)
        removed.append(mo.group(1))
        return ""

    text = re.sub(r' *<object id="\d+"[^>]*[^/]>(?:(?!</object>|<object ).)*?<property name="Mission" value="(\w+)"/>'
                  r'(?:(?!</object>|<object ).)*?</object>\r?\n', drop, text, flags=re.S)
    text, n = re.subn(rf'(<property name="BarMission" value="){BAR["clone"]}"', rf'\g<1>{BAR["bar"]}"', text)
    assert n == 1
    am = re.search(rf'<object id="\d+" name="{BAR["anchor"]}"[^>]*? x="([\d.]+)" y="([\d.]+)"[^>]*>.*?</object>\r?\n', text, re.S)
    icons = [(float(x), float(y)) for x, y in re.findall(r'<object id="\d+" name="[^"]*" gid="[35]" x="([\d.]+)" y="([\d.]+)"', text)]
    spots = free_spots(text, float(am.group(1)), float(am.group(2)), icons, len(MISSIONS))
    oid = int(re.search(r'nextobjectid="(\d+)"', text).group(1))
    new = []
    for i, (m, (x, y)) in enumerate(zip(MISSIONS, spots)):
        hide = '     <property name="Hide If Locked By Quest" type="bool" value="true"/>\r\n' if i else ""
        new.append(f'   <object id="{oid + i}" name="{m["name"]}" gid="5" x="{x:g}" y="{y:g}" width="170" height="167">\r\n'
                   f'    <properties>\r\n{hide}     <property name="Mission" value="{m["name"]}"/>\r\n    </properties>\r\n   </object>\r\n')
    text = text[:am.end()] + "".join(new) + text[am.end():]
    text = re.sub(r'nextobjectid="\d+"', f'nextobjectid="{oid + len(MISSIONS)}"', text, count=1)
    write(rel, bom + text.encode())

    # 7. strings
    langs = 0
    for name in pak.namelist():
        lm = re.fullmatch(r"Language/([a-z\-]+)\.csv", name)
        if not lm:
            continue
        lang = lm.group(1)
        src, bom = read(name)
        tab = "\t" if src.split("\r\n", 1)[0].endswith("\t") else ""
        loc = dict(LOC["en"], **LOC.get(lang, {}))
        if not a.no_crew_skins:
            k = 1 if lang == "ru" else 0
            for persona, names in REBEL_NAMES.items():
                loc[f"persona_{persona}"] = loc[f"persona_{persona}_full"] = names[k]
                loc[f"persona_{persona}_description"] = REBEL_DESC[k]
        rows = "".join(f"{key}\t{v}{tab}\r\n" for key, v in loc.items())
        write(f"Language/{lang}_rebels.csv", bom + rows.encode())
        langs += 1

    with open(os.path.join(out, "README.txt"), "w", encoding="utf-8") as f:
        f.write("RebelCampaign - generated by tools/mods/make_rebel_campaign.py. 'Broken Chain': a 6-mission rebel "
                "campaign in the Eastern Caribbea. Start a NEW game. Do not combine with TestCampaign/NewMissionTest.\n")
    print(f"RebelCampaign written to {out}: {len(MISSIONS)} missions, {len(parked)} original quests parked, "
          f"{len(removed)} mission icons removed, {len(blocked)} cutscenes blocked, {langs} language files, "
          f"icons at {[(int(x // TILE), int(y // TILE)) for x, y in spots]}")


if __name__ == "__main__":
    main()
