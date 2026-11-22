"""Builds the 'TestCampaign' loose-file mod: an alternative campaign on the original map (docs/MODDING.md, "Своя кампания").

How it replaces the story without breaking references:
  - every original quest stays defined (conversations, personas, cutscenes, worldmap.lua and missions refer to them by
    name) but gets UnlockedBy = its own hidden never-quest (mod_park_<name>) -> the original story never starts.
    Quests the start outset marks as completed stay completed (e.g. the first cordon stays open);
  - all original mission icons are removed from WorldMap.tmx (bars, shops and hat shops stay);
  - our missions (room layouts cloned from original missions, fresh GUIDs) get icons next to the Angler's Reef bar;
  - our quest chain: q1 -> q2 -> q3 -> end; hidden lock quests keep the next mission locked until the previous quest is
    done; map conversations on each step; the last quest runs a tactics script (finale line) and the end quest
    gives a reward;
  - every new game starts at Angler's Reef (NewGameOutset = new_game_skip_tutorial): the tutorial needs the story.
Strings go to Language/<lang>_campaign.csv. Conflicts with NewMissionTest (same files) — enable only one of them.

Usage: python tools/mods/make_test_campaign.py [--game DIR]
Then:  swh2 disable NewMissionTest && swh2 enable TestCampaign && swh2 install     (undo: swh2 restore)
Needs a NEW GAME (quest order must match the save).
"""
import argparse, os, re, shutil, uuid, zipfile

from make_new_mission_test import in_region

DEFAULT_GAME = r"C:\Program Files (x86)\Steam\steamapps\common\SteamWorld Heist 2"
TILE = 216
# Prefix of the per-quest never-quests. ONE shared never-quest does not work: completing a quest (start outset
# CompletedQuests, Lua CompleteQuest) also completes its UnlockedBy prerequisites, so the shared one got completed at
# new game and every original quest unlocked at once (tested in game 2026-11-22).
PARK = "mod_park_"

# The whole campaign in one place — the seed of a future campaign file format.
CAMPAIGN = {
    "anchor": "WEST_CARIBBEA_B",   # icons are placed relative to this map object (Angler's Reef bar)
    "region": "west_caribbea_b",
    "speaker": "captain",
    "missions": [
        {"name": "mod_camp_01", "clone": "west_caribbea_02", "shift": (3, 3), "crew": 2,
         "objective": "loot_master", "loot": "utility_grenade_01"},
        {"name": "mod_camp_02", "clone": "west_caribbea_23", "shift": (-3, 4), "crew": 2,
         "objective": "kill_all_enemies", "loot": "loot_random_weapon"},
        {"name": "mod_camp_03", "clone": "west_caribbea_09", "shift": (4, -3), "crew": 2,
         "objective": ("kill_named_enemy", "navy_leader_1"), "loot": "crossbow_03_rare"},
    ],
    "end_reward": [("loot_money", 500)],
    # Own start: a new bar (level copied from an original bar, its NPCs swapped for ours), an intro scene on the first
    # visit, new games start there and it becomes the home bar.
    "start": {
        "bar": "mod_camp_bar", "clone": "west_caribbea_a_bar", "shift": (-4, -4),
        "bartender": ("mod_camp_bartender", "bartender_wc_a"),  # (our entity, entity it replaces in the copied level)
        "npc": ("mod_camp_informant", "old_man_npc"),
        "intro": [  # (speaker, string key) — speakers must stand in the bar level (or be the captain)
            ("mod_camp_bartender", "mod_camp_intro_1"), ("captain", "mod_camp_intro_2"),
            ("mod_camp_informant", "mod_camp_intro_3"), ("captain", "mod_camp_intro_4"),
            ("mod_camp_informant", "mod_camp_intro_5"), ("mod_camp_bartender", "mod_camp_intro_6"),
        ],
    },
}

LOC = {
    "en": {
        "mission_mod_camp_01": "[MOD] The First Lead",
        "mission_mod_camp_01_desc": "Rumour has it a navy courier carries a map to a smugglers' stash. Grab it.",
        "objective_mod_camp_01": "Grab the courier's chest",
        "mod_camp_01_bark": "The map should be in that chest.",
        "mission_mod_camp_02": "[MOD] The Warehouse",
        "mission_mod_camp_02_desc": "The map leads to a smugglers' warehouse. Clear it out.",
        "objective_mod_camp_02": "Scrap every enemy",
        "mod_camp_02_bark": "Nobody leaves this warehouse.",
        "mission_mod_camp_03": "[MOD] The Smuggler Boss",
        "mission_mod_camp_03_desc": "The boss of the smugglers hides on a freighter. End this.",
        "objective_mod_camp_03": "Scrap the smuggler boss",
        "mod_camp_03_bark": "There's the boss. Let's finish it.",
        "quest_mod_camp_q1": "The Smugglers' Trail",
        "quest_mod_camp_q1_obj": "Complete \"The First Lead\"",
        "quest_mod_camp_q2": "The Warehouse",
        "quest_mod_camp_q2_obj": "Complete \"The Warehouse\"",
        "quest_mod_camp_q3": "The Smuggler Boss",
        "quest_mod_camp_q3_obj": "Complete \"The Smuggler Boss\"",
        "mod_camp_con_q1": "A new campaign! A navy courier near Angler's Reef carries a map. Let's take it.",
        "mod_camp_con_q2": "The map points to a warehouse right next to the bar. Time to pay a visit.",
        "mod_camp_con_q3": "The boss is hiding on a freighter. Last job, crew!",
        "mod_camp_con_finale": "That's the boss done. The test campaign is complete!",
        "mod_camp_con_end": "Campaign complete. Thanks for playing the test campaign!",
        "mission_mod_camp_bar": "The Rusty Anchor",
        "speaker_mod_camp_bartender": "Bolt",
        "speaker_mod_camp_informant": "Whisper",
        "mod_camp_banner": "The Rusty Anchor, Angler's Reef",
        "mod_camp_intro_1": "Welcome to the Rusty Anchor, Captain. Not many subs dock here these days.",
        "mod_camp_intro_2": "We're looking for work. The kind that pays.",
        "mod_camp_intro_3": "Then you're in luck. Smugglers have been running cargo right under the navy's nose.",
        "mod_camp_intro_4": "And where do we start?",
        "mod_camp_intro_5": "A navy courier carries their map. Take it, and the rest will follow.",
        "mod_camp_intro_6": "Rest here whenever you need. Good hunting, Captain.",
        "mod_camp_bartender_hello": "Back again, Captain? What'll it be?",
        "mod_camp_bartender_rest": "Let's call it a day.",
        "mod_camp_bartender_tired": "You look fine to me. Go make some money first!",
        "mod_camp_inf_hello": "Psst. Captain. Over here.",
        "mod_camp_inf_ask": "What do you want to know?",
        "mod_camp_inf_opt_who": "Who are you?",
        "mod_camp_inf_opt_job": "Any news about the smugglers?",
        "mod_camp_inf_opt_bye": "Nothing, thanks.",
        "mod_camp_inf_who": "Names are bad for business. Call me Whisper.",
        "mod_camp_inf_job_1": "Find the courier first. The map will tell you the rest.",
        "mod_camp_inf_job_2": "Their boss hides on a freighter. Don't let him get away.",
        "mod_camp_inf_job_3": "The smugglers are done. You've made a lot of friends here, Captain.",
        "mod_camp_inf_bye": "Stay quiet out there.",
    },
    "ru": {
        "mission_mod_camp_01": "[МОД] Первый след",
        "mission_mod_camp_01_desc": "Говорят, курьер флота везёт карту к тайнику контрабандистов. Заберите её.",
        "objective_mod_camp_01": "Забрать сундук курьера",
        "mod_camp_01_bark": "Карта должна быть в том сундуке.",
        "mission_mod_camp_02": "[МОД] Склад",
        "mission_mod_camp_02_desc": "Карта ведёт на склад контрабандистов. Зачистите его.",
        "objective_mod_camp_02": "Уничтожить всех врагов",
        "mod_camp_02_bark": "Со склада никто не уйдёт.",
        "mission_mod_camp_03": "[МОД] Главарь контрабандистов",
        "mission_mod_camp_03_desc": "Главарь контрабандистов прячется на грузовом судне. Пора с этим покончить.",
        "objective_mod_camp_03": "Уничтожить главаря",
        "mod_camp_03_bark": "Вот и главарь. Заканчиваем.",
        "quest_mod_camp_q1": "След контрабандистов",
        "quest_mod_camp_q1_obj": "Пройти «Первый след»",
        "quest_mod_camp_q2": "Склад",
        "quest_mod_camp_q2_obj": "Пройти «Склад»",
        "quest_mod_camp_q3": "Главарь контрабандистов",
        "quest_mod_camp_q3_obj": "Пройти «Главарь контрабандистов»",
        "mod_camp_con_q1": "Новая кампания! У Рифа Рыболова ходит курьер флота с картой. Заберём её.",
        "mod_camp_con_q2": "Карта указывает на склад прямо возле бара. Наведаемся.",
        "mod_camp_con_q3": "Главарь прячется на грузовом судне. Последнее дело, команда!",
        "mod_camp_con_finale": "С главарём покончено. Тестовая кампания пройдена!",
        "mod_camp_con_end": "Кампания пройдена. Спасибо, что сыграли в тестовую кампанию!",
        "mission_mod_camp_bar": "Ржавый якорь",
        "speaker_mod_camp_bartender": "Болт",
        "speaker_mod_camp_informant": "Шёпот",
        "mod_camp_banner": "«Ржавый якорь», Риф Рыболова",
        "mod_camp_intro_1": "Добро пожаловать в «Ржавый якорь», капитан. Нынче к нам редко кто заходит.",
        "mod_camp_intro_2": "Мы ищем работу. Такую, за которую хорошо платят.",
        "mod_camp_intro_3": "Тогда вам повезло. Контрабандисты возят грузы прямо под носом у флота.",
        "mod_camp_intro_4": "И с чего начать?",
        "mod_camp_intro_5": "У курьера флота есть их карта. Заберите её, а дальше всё само пойдёт.",
        "mod_camp_intro_6": "Отдыхайте здесь, когда нужно. Удачной охоты, капитан.",
        "mod_camp_bartender_hello": "Снова вы, капитан? Чем помочь?",
        "mod_camp_bartender_rest": "Пожалуй, на сегодня хватит.",
        "mod_camp_bartender_tired": "Вы ещё бодры. Сначала заработайте!",
        "mod_camp_inf_hello": "Псст. Капитан. Сюда.",
        "mod_camp_inf_ask": "Что хотите узнать?",
        "mod_camp_inf_opt_who": "Кто ты такой?",
        "mod_camp_inf_opt_job": "Что слышно о контрабандистах?",
        "mod_camp_inf_opt_bye": "Ничего, спасибо.",
        "mod_camp_inf_who": "Имена вредят делу. Зовите меня Шёпот.",
        "mod_camp_inf_job_1": "Сначала найдите курьера. Карта подскажет остальное.",
        "mod_camp_inf_job_2": "Их главарь прячется на грузовом судне. Не дайте ему уйти.",
        "mod_camp_inf_job_3": "С контрабандистами покончено. У вас тут теперь много друзей, капитан.",
        "mod_camp_inf_bye": "Не шумите там.",
    },
}

GUID = r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"


def quest_name(i):
    return f"mod_camp_q{i + 1}"


def clone_mission(text, m):
    src = re.search(rf'\t<Mission Name="{m["clone"]}".*?</Mission>\r?\n', text, re.S)
    block = src.group(0)
    for g in set(re.findall(GUID, block)):
        block = block.replace(g, str(uuid.uuid4()))
    block = block.replace(f'Name="{m["clone"]}"', f'Name="{m["name"]}"', 1)
    named = isinstance(m["objective"], tuple)
    if not named:  # Parameters only feed the original script/rules
        block = re.sub(r"\t*<Parameters>.*?</Parameters>\r?\n", "", block, flags=re.S)
    else:
        block = re.sub(r'(<Parameter Name="Target" Value=")\w*', rf"\g<1>{m['objective'][1]}", block)
    block = re.sub(r"\t*<IntroConversation>.*?</IntroConversation>\r?\n", "", block)
    block = re.sub(r"<Rules>.*?</Rules>", f"<Rules>{m['name']}</Rules>", block)
    block = re.sub(r"<Script>.*?</Script>", f"<Script>Lua\\\\Missions\\\\{m['name']}.lua</Script>", block)
    block = re.sub(r"<CrewSize>\d+</CrewSize>", f"<CrewSize>{m['crew']}</CrewSize>", block)
    block, n = re.subn(r'(<MasterLoot[^>]*>\s*<Item Definition=")[^"]+', rf"\g<1>{m['loot']}", block, count=1)
    assert n == 1, m["clone"]
    return src.end(), block


def rules_for(m, nl):
    obj = m["objective"]
    if isinstance(obj, tuple):
        body = (f'\t\t<Parameters>\n\t\t\t<Parameter Name="Target" Default="" Type="String"/>\n\t\t</Parameters>\n'
                f'\t\t<Objectives>\n\t\t\t<Group>\n\t\t\t\t<Objective Definition="{obj[0]}" TargetName="$Target" Count="1" '
                f'DisplayStringId="objective_{m["name"]}"/>\n\t\t\t</Group>\n\t\t</Objectives>\n')
    else:
        body = (f'\t\t<Objectives>\n\t\t\t<Group>\n\t\t\t\t<Objective Definition="{obj}" DisplayStringId="objective_{m["name"]}" />\n'
                f'\t\t\t</Group>\n\t\t</Objectives>\n')
    return f'\t<MissionRules Name="{m["name"]}">\n{body}\t</MissionRules>\n'.replace("\n", nl)


def mission_script(m, last):
    s = (f'-- {m["name"]}: TestCampaign mission (tools/mods/make_test_campaign.py)\n\n'
         'function OnCrewDeployed()\n\tlocal crew = GetFriendlyActors()\n'
         f'\tif crew and crew[1] then SpawnWorldText(crew[1], "worldtext_speak_important", "{m["name"]}_bark") end\nend\n')
    if last:  # called by the last quest's OnCompleted TacticsScript
        s += '\nfunction ModCampaignFinale()\n\tStartConversation("con_mod_camp_finale")\nend\n'
    return s.replace("\n", "\r\n")


def never_quest_xml(name, nl):
    return (f'\r\n\t<Quest Name="{name}" Template="BASE">\n\t\t<Objectives>\n\t\t\t<Objective>\n\t\t\t\t<Condition>none</Condition>\n'
            '\t\t\t</Objective>\n\t\t</Objectives>\n\t</Quest>').replace("\r\n", "\n").replace("\n", nl)


def check_unlock_order(text):
    """Keep every UnlockedBy name defined above its user: the parser warns otherwise ('Quest {} is unlocked by
    non-existent quest {} (or the latter is further down in the file)'), and the skippable check (0x11ca50) and the
    unlock check skip dependencies without a definition -> such a quest would unlock at game start."""
    seen = set()
    for name, body in re.findall(r'<Quest Name="(\w+)"[^>]*>(.*?)</Quest>', text, re.S):
        for u in re.findall(r"<UnlockedBy>([^<]*)</UnlockedBy>", body):
            for dep in filter(None, (d.strip() for d in u.split(","))):
                assert dep in seen, f"quest {name}: UnlockedBy {dep} is not defined above it"
        seen.add(name)


def quests_xml(c, nl):
    ms = c["missions"]
    # hidden intro quest: completed by the intro scene in our bar; the first quest waits for it
    out = ['\t<Quest Name="mod_camp_intro" Template="BASE">\n\t\t<Objectives>\n\t\t\t<Objective>\n\t\t\t\t<Condition>none</Condition>\n'
           '\t\t\t</Objective>\n\t\t</Objectives>\n\t</Quest>\n']
    for i, m in enumerate(ms):
        q = quest_name(i)
        unlock = f"\t\t<UnlockedBy>{quest_name(i - 1) if i else 'mod_camp_intro'}</UnlockedBy>\n"
        home = ' MapScript="ModCampSetHome"' if i == 0 else ""
        done = ' TacticsScript="ModCampaignFinale"' if i == len(ms) - 1 else ""
        out.append(f'\t<Quest Name="{q}" Template="BASE">\n{unlock}\t\t<OnUnlocked MapConversation="con_mod_camp_q{i + 1}"{home} />\n'
                   + (f'\t\t<OnCompleted{done} />\n' if done else "")
                   + f'\t\t<Objectives>\n\t\t\t<Objective>\n\t\t\t\t<LocalizedStringId>quest_{q}_obj</LocalizedStringId>\n'
                   f'\t\t\t\t<Condition>mission</Condition>\n\t\t\t\t<Parameter>{m["name"]}</Parameter>\n'
                   '\t\t\t</Objective>\n\t\t</Objectives>\n\t</Quest>\n')
        if i:  # hidden lock: mission i stays locked until quest i-1 is complete
            out.append(f'\t<Quest Name="mod_camp_lock_{i + 1}" Template="BASE">\n\t\t<LockMissions>\n\t\t\t<Mission>{m["name"]}</Mission>\n'
                       f'\t\t</LockMissions>\n\t\t<Objectives>\n\t\t\t<Objective>\n\t\t\t\t<Condition>quest</Condition>\n'
                       f'\t\t\t\t<Parameter>{quest_name(i - 1)}</Parameter>\n\t\t\t</Objective>\n\t\t</Objectives>\n\t</Quest>\n')
    items = "".join(f'\t\t\t<Item Name="{n}" Count="{k}"/>\n' for n, k in c["end_reward"])
    out.append(f'\t<Quest Name="mod_camp_end" Template="BASE">\n\t\t<UnlockedBy>{quest_name(len(ms) - 1)}</UnlockedBy>\n'
               '\t\t<OnUnlocked MapConversation="con_mod_camp_end" />\n'
               f'\t\t<ItemRewards ShowLootUI="true">\n{items}\t\t</ItemRewards>\n\t</Quest>\n')
    return "".join(out).replace("\n", nl)


def conversations_xml(c, nl):
    names = [f"q{i + 1}" for i in range(len(c["missions"]))] + ["finale", "end"]
    out = []
    for k, n in enumerate(names):
        out.append(f'\t<Conversation Name="con_mod_camp_{n}">\n\t\t<Speak Speaker="{c["speaker"]}" Text="mod_camp_con_{n}" '
                   f'Id="mdc{k:03d}" />\n\t</Conversation>\n')
    return "".join(out).replace("\n", nl)


def clone_bar(text, s):
    src = re.search(rf'\t<Mission Name="{s["clone"]}".*?</Mission>\r?\n', text, re.S)
    block = src.group(0)
    for g in set(re.findall(GUID, block)):
        block = block.replace(g, str(uuid.uuid4()))
    block = block.replace(f'Name="{s["clone"]}"', f'Name="{s["bar"]}"', 1)
    block, n1 = re.subn(r"<Patchset>[^<]*</Patchset>", f"<Patchset>Patchsets/{s['bar']}</Patchset>", block)
    block, n2 = re.subn(r"<Script>[^<]*</Script>", f"<Script>Lua\\\\Missions\\\\{s['bar']}.lua</Script>", block)
    assert n1 == n2 == 1
    return src.end(), block


def clone_level(le, s):
    """Copy of the original bar level: own name/GUID, the original bartender and NPC replaced by ours."""
    le = le.replace(f"<Name>{s['clone']}</Name>", f"<Name>{s['bar']}</Name>", 1)
    le = le.replace(f"<FileName>{s['clone']}.le</FileName>", f"<FileName>{s['bar']}.le</FileName>", 1)
    le = re.sub(r"<GuidIdentifier>[^<]*</GuidIdentifier>", f"<GuidIdentifier>{uuid.uuid4()}</GuidIdentifier>", le, count=1)
    for new, old in (s["bartender"], s["npc"]):
        for tag in ("Name", "Definition"):
            le, n = re.subn(rf"<{tag}>{old}</{tag}>", f"<{tag}>{new}</{tag}>", le)
            assert n == 1, f"{old}: {n} <{tag}> in the level"
    return le


def npc_entities(ents, s, nl):
    """Our bartender/NPC: copies of the replaced entities (same model) talking with our conversations."""
    out = []
    for new, old in (s["bartender"], s["npc"]):
        b = re.search(rf'\t<Entity Name="{old}".*?</Entity>', ents, re.S).group(0)
        b = b.replace(f'Name="{old}"', f'Name="{new}"', 1)
        b, n = re.subn(r"<Conversation>[^<]*</Conversation>", f"<Conversation>{new}</Conversation>", b)
        assert n == 1, old
        b = re.sub(r"<ConversationCannotEndDayFallback>[^<]*</ConversationCannotEndDayFallback>",
                   f"<ConversationCannotEndDayFallback>{new}</ConversationCannotEndDayFallback>", b)
        out.append(b.replace("\r\n", "\n").replace("\n", nl) + nl + nl)
    return "".join(out)


def start_conversations_xml(c, nl):
    s = c["start"]
    bt, inf = s["bartender"][0], s["npc"][0]
    ids = (f"mds{k:03d}" for k in range(1000))
    sp = lambda who, key, extra="": f'<Speak Speaker="{who}" Text="{key}"{extra} Id="{next(ids)}" />'
    intro = "".join(f"\t\t{sp(w, k)}\n" for w, k in s["intro"])
    q = [quest_name(i) for i in range(len(c["missions"]))]
    x = f'''	<Conversation Name="con_mod_camp_intro">
{intro}	</Conversation>
	<Conversation Name="{bt}">
		<Ask Label="questions" Speaker="{bt}" Text="mod_camp_bartender_hello" Cancelable="True" Id="{next(ids)}">
			<Options>
				<Item Text="dialogue_option_end_day" AlwaysEmphasized="True" />
				<Item Text="dialogue_option_farewell" AlwaysEmphasized="True" />
			</Options>
		</Ask>
		<Case If="answer:0" Id="{next(ids)}">
			<Children>
				<Case If="can_end_day" Id="{next(ids)}">
					<Children>
						{sp("captain", "mod_camp_bartender_rest", f' SpeakTo="{bt}"')}
						<Jump TargetConversation="{bt}" TargetLabel="end_day" Id="{next(ids)}" />
					</Children>
				</Case>
				{sp(bt, "mod_camp_bartender_tired")}
				<Jump TargetConversation="{bt}" TargetLabel="bar_end" Id="{next(ids)}" />
			</Children>
		</Case>
		<Case Id="{next(ids)}">
			<Children>
				<Jump TargetConversation="{bt}" TargetLabel="bar_end" Id="{next(ids)}" />
			</Children>
		</Case>
		<EndDay Label="end_day" Id="{next(ids)}" />
		<Wait Label="bar_end" Id="{next(ids)}" />
	</Conversation>
	<Conversation Name="{inf}">
		<Case If="first_time" Id="{next(ids)}">
			<Children>
				{sp(inf, "mod_camp_inf_hello", ' SpeakTo="captain" IsAdvertised="True"')}
			</Children>
		</Case>
		<Ask Label="ask" Speaker="{inf}" Text="mod_camp_inf_ask" Cancelable="True" Id="{next(ids)}">
			<Options>
				<Item Text="mod_camp_inf_opt_who" />
				<Item Text="mod_camp_inf_opt_job" />
				<Item Text="mod_camp_inf_opt_bye" AlwaysEmphasized="True" />
			</Options>
		</Ask>
		<Case If="answer:0" Id="{next(ids)}">
			<Children>
				{sp(inf, "mod_camp_inf_who", ' SpeakTo="captain"')}
				<Jump TargetConversation="{inf}" TargetLabel="ask" Id="{next(ids)}" />
			</Children>
		</Case>
		<Case If="answer:1" Id="{next(ids)}">
			<Children>
				<Case If="quest_complete:{q[-1]}" Id="{next(ids)}">
					<Children>
						{sp(inf, "mod_camp_inf_job_3", ' SpeakTo="captain"')}
						<Jump TargetConversation="{inf}" TargetLabel="ask" Id="{next(ids)}" />
					</Children>
				</Case>
				<Case If="quest_complete:{q[0]}" Id="{next(ids)}">
					<Children>
						{sp(inf, "mod_camp_inf_job_2", ' SpeakTo="captain"')}
						<Jump TargetConversation="{inf}" TargetLabel="ask" Id="{next(ids)}" />
					</Children>
				</Case>
				{sp(inf, "mod_camp_inf_job_1", ' SpeakTo="captain"')}
				<Jump TargetConversation="{inf}" TargetLabel="ask" Id="{next(ids)}" />
			</Children>
		</Case>
		<Case Id="{next(ids)}">
			<Children>
				{sp(inf, "mod_camp_inf_bye")}
			</Children>
		</Case>
	</Conversation>
'''
    return x.replace("\n", nl)


def intro_cutscene_xml(c, nl):
    bar = c["start"]["bar"]
    return f'''	<IntermissionCutscene Name="mod_camp_intro" Template="base">
		<Mission>{bar}</Mission>
		<ScriptFile>Lua/Intermissions/mod_camp_intro.lua</ScriptFile>
		<NextMission>{bar}</NextMission>
		<Priority>10</Priority>
		<Trigger Type="EnterRoamingMission" Value="{bar}"/>
		<Filters>
			<Filter Type="QuestActive" Value="mod_camp_intro"/>
		</Filters>
		<StartingBannerTextId>mod_camp_banner</StartingBannerTextId>
	</IntermissionCutscene>
'''.replace("\n", nl)


# The captain enters at the bar door; for the intro he is moved next to the NPCs (the copied level's
# cutscene_captain spot: between the NPC at x-480 and the bartender), same calls as the original cutscenes.
PLACE_CAPTAIN_LUA = '''
function ModCampPlaceCaptain()
	local captain = FindEntityWithName("captain")
	local npc = FindEntityWithName("%(npc)s")
	if captain and npc then
		local p = GetEntityPosition(npc)
		SetEntityPosition(captain, Vector(p.x + 480, p.y), true)
		SnapActorToGround(captain)
		SetEntityDirection(captain, Vector.Left)
	end
end
'''

INTRO_LUA = '''-- TestCampaign intro scene (intermission_cutscenes.xml: mod_camp_intro), tools/mods/make_test_campaign.py
%(place)s
-- runs before the fade-in
function Init()
	ModCampPlaceCaptain()
end

function Run()
	Wait(1.0)
	StartConversation("con_mod_camp_intro")
	CompleteQuest("mod_camp_intro")
end
'''

# Fallback in case the cutscene does not trigger when a new game starts inside the bar.
BAR_LUA = '''-- TestCampaign bar, tools/mods/make_test_campaign.py
%(place)s
function OnMissionStart()
	if IsQuestActive("mod_camp_intro") then
		ModCampPlaceCaptain()
		StartConversation("con_mod_camp_intro")
		CompleteQuest("mod_camp_intro")
	end
end
'''

WORLDMAP_LUA = '''

-- TestCampaign (tools/mods/make_test_campaign.py): our bar is the home bar. Run by quest mod_camp_q1 OnUnlocked.
function ModCampSetHome()
	SetHomeBar("%s", false, true)
end
'''


def cutscenes_playable_at_start(ic, outset):
    """Original intermission cutscenes whose filters already pass at the start (only quests in the outset are
    completed, original quests never become active) -> they would play out of context. Unknown filter types count as
    passing, so the list errs on the side of blocking."""
    done = set(re.findall(r"<Quest>(\w+)</Quest>", outset))
    cut_done = set(re.findall(r"<Cutscene>(\w+)</Cutscene>", outset))
    mis_done = set(re.findall(r"<Mission>(\w+)</Mission>", outset))
    out = []
    for m in re.finditer(r'<IntermissionCutscene Name="(\w+)"([^>]*)>(.*?)</IntermissionCutscene>', ic, re.S):
        if "Abstract" in m.group(2) or m.group(1) in cut_done:
            continue
        ok = True
        for f in re.findall(r"<Filter [^>]*>", m.group(3)):
            t = re.search(r'Type="(\w+)"', f).group(1)
            v = (re.search(r'Value="([^"]*)"', f) or [None, ""])[1]
            ok &= {"QuestCompleted": v in done, "QuestActive": False, "CutscenePlayed": v in cut_done,
                   "CutsceneNotPlayed": v not in cut_done, "MissionCompleted": v in mis_done}.get(t, True)
        if ok:
            out.append(m.group(1))
    return out


def surface_open(tmx, x, y, r=1):
    """True if the surface tile layers (groups '0' and '1', except Ground = sea floor) are empty around (x, y)."""
    tx, ty = int(x // TILE), int(y // TILE)
    stack, checked = [], 0
    for m in re.finditer(r'<group [^>]*name="([^"]*)"[^>]*>|</group>|<layer id="\d+" name="([^"]*)" width="(\d+)" height="(\d+)"[^>]*>\s*<data encoding="csv">(.*?)</data>', tmx, re.S):
        tok = m.group(0)
        if tok == "</group>":
            stack.pop()
        elif tok.startswith("<group"):
            stack.append(m.group(1))
        elif stack and stack[-1] in ("0", "1") and m.group(2) != "Ground":
            W, H = int(m.group(3)), int(m.group(4))
            v = m.group(5).replace("\n", "").split(",")
            assert len(v) == W * H and 0 < tx < W - 1 and 0 < ty < H - 1, f"layer {m.group(2)}: unexpected size"
            checked += 1
            if any(v[(ty + b) * W + tx + a].strip() != "0" for a in range(-r, r + 1) for b in range(-r, r + 1)):
                return False
    assert checked, "no surface layers found"
    return True


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=DEFAULT_GAME)
    a = ap.parse_args()
    backup = os.path.join(a.game, "SWH2ModLoader", "Backup", "Game.pak")
    pak = zipfile.ZipFile(backup if os.path.exists(backup) else os.path.join(a.game, "Bundle", "Core", "Game.pak"))
    out = os.path.join(a.game, "Mods", "TestCampaign")
    if os.path.isdir(out):
        shutil.rmtree(out)
    c, ms = CAMPAIGN, CAMPAIGN["missions"]

    def write(rel, data):
        path = os.path.join(out, *rel.split("/"))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as f:
            f.write(data)

    def read(rel):
        raw = pak.read(rel)
        return raw.decode("utf-8-sig"), (b"\xef\xbb\xbf" if raw.startswith(b"\xef\xbb\xbf") else b"")

    def insert_before_close(text, block):
        close = re.search(r"</\w+>\s*$", text)
        nl = "\r\n" if "\r\n" in text else "\n"
        return text[:close.start()] + block(nl) + text[close.start():]

    # 1. missions (all in the west_caribbea file: they inherit its templates)
    rel = "Definitions/missions.west_caribbea.de"
    text, bom = read(rel)
    for m in ms:
        end, block = clone_mission(text, m)
        text = text[:end] + block + text[end:]
    s = c["start"]
    end, block = clone_bar(text, s)
    text = text[:end] + block + text[end:]
    write(rel, bom + text.encode())

    # 1b. our bar: level copy, NPC entities, scripts, intro cutscene
    le, bom = read(f"Patchsets/{s['clone']}.le")
    write(f"Patchsets/{s['bar']}.le", bom + clone_level(le, s).encode())
    place = PLACE_CAPTAIN_LUA % {"npc": s["npc"][0]}
    write(f"Lua/Missions/{s['bar']}.lua", (BAR_LUA % {"place": place}).replace("\n", "\r\n").encode())
    write("Lua/Intermissions/mod_camp_intro.lua", (INTRO_LUA % {"place": place}).replace("\n", "\r\n").encode())

    rel = "Definitions/entities.npc.xml"
    text, bom = read(rel)
    text = insert_before_close(text, lambda nl: npc_entities(text, s, nl))
    write(rel, bom + text.encode())

    rel = "Definitions/intermission_cutscenes.xml"
    text, bom = read(rel)
    ic_text = text
    text = insert_before_close(text, lambda nl: intro_cutscene_xml(c, nl))
    write(rel, bom + text.encode())

    rel = "Lua/WorldMap/worldmap.lua"
    text, bom = read(rel)
    write(rel, bom + (text.rstrip() + WORLDMAP_LUA % s["bar"]).replace("\r\n", "\n").replace("\n", "\r\n").encode())

    # new games start in our bar; original cutscenes that could already play are marked as seen
    rel = "Definitions/outsets.xml"
    text, bom = read(rel)
    om = re.search(r'<Outset Name="new_game_skip_tutorial".*?</Outset>', text, re.S)
    outset = om.group(0)
    blocked = cutscenes_playable_at_start(ic_text, outset)
    outset = re.sub(r"<ShipStartPosition>[^<]*</ShipStartPosition>", f"<ShipStartPosition>{s['bar']}_start</ShipStartPosition>", outset, count=1)
    outset = re.sub(r"<Mission>[^<]*</Mission>", f"<Mission>{s['bar']}</Mission>", outset, count=1)  # first = start mission
    outset, n = re.subn(r"(<CompletedCutscenes>)", r"\1" + "".join(f"\r\n\t\t\t<Cutscene>{b}</Cutscene>" for b in blocked), outset, count=1)
    assert n == 1
    text = text[:om.start()] + outset + text[om.end():]
    write(rel, bom + text.encode())

    rel = "Definitions/mission_rules.de"
    text, bom = read(rel)
    text = insert_before_close(text, lambda nl: "".join(rules_for(m, nl) for m in ms))
    write(rel, bom + text.encode())

    for i, m in enumerate(ms):
        write(f"Lua/Missions/{m['name']}.lua", mission_script(m, i == len(ms) - 1).encode())

    # 2. quests: park every original quest behind its OWN never-quest, then append ours
    rel = "Definitions/quests.xml"
    text, bom = read(rel)
    parked = []

    def park(mq):
        head, name, body = mq.group(1), mq.group(2), mq.group(3)
        before = text[:mq.start()]
        if "Abstract" in head or before.rfind("<!--") > before.rfind("-->"):  # template, or a commented-out quest
            return mq.group(0)
        parked.append(name)
        body = re.sub(r"[ \t]*<UnlockedBy>[^<]*</UnlockedBy>[ \t]*\r?\n", "", body)
        return f"{head}\r\n\t\t<UnlockedBy>{PARK}{name}</UnlockedBy>{body}"

    text = re.sub(r'(<Quest Name="(\w+)"[^>]*>)(.*?</Quest>)', park, text, flags=re.S)
    # never-quests right after the BASE template, i.e. above every quest that names them (check_unlock_order)
    base = re.search(r'<Quest Name="BASE"[^>]*>.*?</Quest>', text, re.S)
    nl = "\r\n" if "\r\n" in text else "\n"
    text = text[:base.end()] + "".join(never_quest_xml(PARK + n, nl) for n in parked) + text[base.end():]
    text = insert_before_close(text, lambda nl: quests_xml(c, nl))
    check_unlock_order(text)
    write(rel, bom + text.encode())

    rel = "Definitions/conversations.xml"
    text, bom = read(rel)
    text = insert_before_close(text, lambda nl: conversations_xml(c, nl) + start_conversations_xml(c, nl))
    write(rel, bom + text.encode())

    # 3. every new game starts at Angler's Reef
    rel = "Definitions/game_constants.xml"
    text, bom = read(rel)
    text, n = re.subn(r"<NewGameOutset>new_game</NewGameOutset>", "<NewGameOutset>new_game_skip_tutorial</NewGameOutset>", text)
    assert n == 1
    write(rel, bom + text.encode())

    # 4. map: drop original mission icons (keep shops), add ours next to the anchor
    rel = "WorldMap/Maps/WorldMap.tmx"
    text, bom = read(rel)
    removed = []

    def drop(mo):
        if "shop" in mo.group(1):
            return mo.group(0)
        removed.append(mo.group(1))
        return ""

    # one non-self-closing <object> whose body has a Mission property (the body must not run into the next object)
    text = re.sub(r' *<object id="\d+"[^>]*[^/]>(?:(?!</object>|<object ).)*?<property name="Mission" value="(\w+)"/>'
                  r'(?:(?!</object>|<object ).)*?</object>\r?\n', drop, text, flags=re.S)
    nxt = re.search(r'nextobjectid="(\d+)"', text)
    oid = int(nxt.group(1))
    am = re.search(rf'<object id="\d+" name="{c["anchor"]}"[^>]*? x="([\d.]+)" y="([\d.]+)"[^>]*>.*?</object>\r?\n', text, re.S)
    icons = [(float(x), float(y)) for x, y in re.findall(r'<object id="\d+" name="[^"]*" gid="[35]" x="([\d.]+)" y="([\d.]+)"', text)]
    new = []
    for i, m in enumerate(ms):
        x = float(am.group(1)) + m["shift"][0] * TILE
        y = float(am.group(2)) + m["shift"][1] * TILE
        assert in_region(text, c["region"], x, y), f"{m['name']}: outside {c['region']}"
        assert surface_open(text, x, y), f"{m['name']}: not open water"
        for ix, iy in icons:
            assert abs(ix - x) + abs(iy - y) >= 2 * TILE, f"{m['name']}: too close to the icon at {ix:g},{iy:g}"
        icons.append((x, y))
        # missions behind a lock quest are hidden until unlocked (same property the original map uses)
        hide = '     <property name="Hide If Locked By Quest" type="bool" value="true"/>\r\n' if i else ""
        new.append(f'   <object id="{oid + i}" name="{m["name"]}" gid="5" x="{x:g}" y="{y:g}" width="170" height="167">\r\n'
                   f'    <properties>\r\n{hide}     <property name="Mission" value="{m["name"]}"/>\r\n    </properties>\r\n   </object>\r\n')
    # our bar (hub icon) and the new-game ship start point at the same spot, like the original bars
    x = float(am.group(1)) + s["shift"][0] * TILE
    y = float(am.group(2)) + s["shift"][1] * TILE
    assert in_region(text, c["region"], x, y), f"{s['bar']}: outside {c['region']}"
    assert surface_open(text, x, y), f"{s['bar']}: not open water"
    for ix, iy in icons:
        assert abs(ix - x) + abs(iy - y) >= 2 * TILE, f"{s['bar']}: too close to the icon at {ix:g},{iy:g}"
    k = oid + len(ms)
    new.append(f'   <object id="{k}" name="{s["bar"].upper()}" gid="3" x="{x:g}" y="{y:g}" width="170" height="167">\r\n'
               f'    <properties>\r\n     <property name="BarMission" value="{s["bar"]}"/>\r\n    </properties>\r\n   </object>\r\n'
               f'   <object id="{k + 1}" name="{s["bar"]}_start" gid="10" x="{x:g}" y="{y:g}" width="170" height="167"/>\r\n')
    text = text[:am.end()] + "".join(new) + text[am.end():]
    text = re.sub(r'nextobjectid="\d+"', f'nextobjectid="{k + 2}"', text, count=1)
    write(rel, bom + text.encode())

    # 5. strings
    langs = 0
    for name in pak.namelist():
        lm = re.fullmatch(r"Language/([a-z\-]+)\.csv", name)
        if lm:
            src, bom = read(name)
            tab = "\t" if src.split("\r\n", 1)[0].endswith("\t") else ""
            rows = "".join(f"{k}\t{v}{tab}\r\n" for k, v in LOC.get(lm.group(1), LOC["en"]).items())
            write(f"Language/{lm.group(1)}_campaign.csv", bom + rows.encode())
            langs += 1

    with open(os.path.join(out, "README.txt"), "w", encoding="utf-8") as f:
        f.write("TestCampaign — generated by tools/mods/make_test_campaign.py. Replaces the story with a 3-mission test "
                "campaign at Angler's Reef. Start a NEW game. Do not combine with NewMissionTest.\n")
    print(f"TestCampaign written to {out}: {len(ms)} missions, {len(parked)} original quests parked, "
          f"{len(removed)} original mission icons removed, {langs} language files")


if __name__ == "__main__":
    main()
