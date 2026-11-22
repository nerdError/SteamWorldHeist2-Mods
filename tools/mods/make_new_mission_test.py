"""Builds the 'NewMissionTest' loose-file mod: smoke test for adding NEW content (step 1 of the content plan).

What it adds (nothing existing is changed or removed, all edits are text inserts):
  - mission `mod_test_01` in missions.west_caribbea.de: a copy of west_caribbea_02's room layout (fresh GUIDs),
    own rules, own Lua script, own master loot (crossbow_03_rare);
  - rules `mod_test_01` in mission_rules.de (objective: grab the master loot);
  - a map icon in WorldMap.tmx, 3+3 tiles from the Angler's Reef bar (open water, region west_caribbea_b);
  - strings in Language/<lang>_newmission.csv (the loader reads every <lang>_*.csv, docs/EXE_NOTES.md),
    so it can be installed together with CheatMenu (which uses <lang>_mod.csv).
In game: a new mission "[MOD] Test Run" next to the Angler's Reef bar; on deploy the first crewmate says a line.
Questions it answers: new mission name/GUID accepted? new TMX object read? new script + rules + strings work?
How does the save react (reputation, completion)?

Usage: python tools/mods/make_new_mission_test.py [--game DIR]
Then:  swh2 enable CheatMenu NewMissionTest && swh2 install     (undo: swh2 restore)
"""
import argparse, os, re, shutil, uuid, zipfile

DEFAULT_GAME = r"C:\Program Files (x86)\Steam\steamapps\common\SteamWorld Heist 2"
NAME = "mod_test_01"
CLONE_OF = "west_caribbea_02"
LOOT = "crossbow_03_rare"
TILE = 216  # tileheight of the isometric map; object x/y are in these units per tile
# Test missions go next to the Angler's Reef bar: the user skips the tutorial and starts there (new game option).
ANCHOR = "WEST_CARIBBEA_B"  # map object (bar) to place the icon next to
REGION = "west_caribbea_b"  # Angler's Reef; the icon must be inside this region polygon
SHIFT = (3, 3)  # tiles from the anchor: open water (surface layers 0/1 are empty there)

LOC = {
    "en": {
        f"mission_{NAME}": "[MOD] Test Run",
        f"mission_{NAME}_desc": "A mission that is not in the original game. If you can read this, new content works.",
        f"objective_{NAME}": "Grab the modded loot",
        f"{NAME}_bark": "This ship wasn't here yesterday...",
    },
    "ru": {
        f"mission_{NAME}": "[МОД] Пробный заход",
        f"mission_{NAME}_desc": "Миссии нет в оригинальной игре. Если вы это читаете, новый контент работает.",
        f"objective_{NAME}": "Забрать модовый трофей",
        f"{NAME}_bark": "Вчера этого корабля здесь не было...",
    },
}

SCRIPT = f'''-- [MOD] Test Run: new mission added by tools/mods/make_new_mission_test.py

function OnCrewDeployed()
	local crew = GetFriendlyActors()
	if crew and crew[1] then SpawnWorldText(crew[1], "worldtext_speak_important", "{NAME}_bark") end
end
'''

RULES = f'''	<MissionRules Name="{NAME}">
		<Objectives>
			<Group>
				<Objective Definition="loot_master" DisplayStringId="objective_{NAME}" />
			</Group>
		</Objectives>
	</MissionRules>
'''


def in_region(tmx, region, x, y):
    """Point-in-polygon test against the region's polygon in the Regions layer."""
    m = re.search(rf'<object id="\d+" name="{region}" type="Region" x="([\d.-]+)" y="([\d.-]+)">.*?points="([^"]+)"', tmx, re.S)
    ox, oy = float(m.group(1)), float(m.group(2))
    poly = [(ox + float(a), oy + float(b)) for a, b in (p.split(",") for p in m.group(3).split())]
    inside = False
    for (x1, y1), (x2, y2) in zip(poly, poly[-1:] + poly[:-1]):
        if (y1 > y) != (y2 > y) and x < (x2 - x1) * (y - y1) / (y2 - y1) + x1:
            inside = not inside
    return inside


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=DEFAULT_GAME)
    a = ap.parse_args()
    backup = os.path.join(a.game, "SWH2ModLoader", "Backup", "Game.pak")
    pak = zipfile.ZipFile(backup if os.path.exists(backup) else os.path.join(a.game, "Bundle", "Core", "Game.pak"))
    out = os.path.join(a.game, "Mods", "NewMissionTest")
    if os.path.isdir(out):
        shutil.rmtree(out)

    def write(rel, data):
        path = os.path.join(out, *rel.split("/"))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as f:
            f.write(data)

    def read(rel):
        raw = pak.read(rel)
        bom = raw.startswith(b"\xef\xbb\xbf")
        return raw.decode("utf-8-sig"), (b"\xef\xbb\xbf" if bom else b"")

    def crlf(s):
        return s.replace("\r\n", "\n").replace("\n", "\r\n")

    # 1. mission definition: clone the source block, fresh GUIDs (rooms are referenced by Id inside the block)
    rel = "Definitions/missions.west_caribbea.de"
    text, bom = read(rel)
    assert f'Name="{NAME}"' not in text
    m = re.search(rf'\t<Mission Name="{CLONE_OF}".*?</Mission>\r?\n', text, re.S)
    block = m.group(0)
    ids = {g: str(uuid.uuid4()) for g in set(re.findall(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", block))}
    for old, new in ids.items():
        block = block.replace(old, new)
    block = block.replace(f'Name="{CLONE_OF}"', f'Name="{NAME}"', 1)
    for tag in ("Parameters", "ToggleLayers"):  # only used by the original's script / map layers
        block = re.sub(rf"\t*<{tag}[ >].*?</{tag}>\r?\n", "", block, flags=re.S)
    block = re.sub(r"\t*<IntroConversation>.*?</IntroConversation>\r?\n", "", block)
    block = re.sub(r"<Rules>.*?</Rules>", f"<Rules>{NAME}</Rules>", block)
    block = re.sub(r"<Script>.*?</Script>", f"<Script>Lua\\\\Missions\\\\{NAME}.lua</Script>", block)
    block, n = re.subn(r'(<MasterLoot[^>]*>\s*<Item Definition=")[^"]+', rf"\g<1>{LOOT}", block)
    assert n == 1
    text = text[:m.end()] + block + text[m.end():]
    write(rel, bom + text.encode())

    # 2. rules
    rel = "Definitions/mission_rules.de"
    text, bom = read(rel)
    close = re.search(r"</\w+>\s*$", text)
    nl = "\r\n" if "\r\n" in text else "\n"
    text = text[:close.start()] + RULES.replace("\n", nl) + text[close.start():]
    write(rel, bom + text.encode())

    # 3. script
    write(f"Lua/Missions/{NAME}.lua", crlf(SCRIPT).encode())

    # 4. map icon: copy of the source mission's object, placed SHIFT tiles from the ANCHOR object
    rel = "WorldMap/Maps/WorldMap.tmx"
    text, bom = read(rel)
    m = re.search(rf'( *)<object id="(\d+)" name="{CLONE_OF}[^"]*"[^>]*>.*?</object>\r?\n', text, re.S)
    obj = m.group(0)
    nxt = re.search(r'nextobjectid="(\d+)"', text)
    oid = int(nxt.group(1))
    am = re.search(rf'<object id="\d+" name="{ANCHOR}"[^>]* x="([\d.]+)" y="([\d.]+)"', text)
    x = float(am.group(1)) + SHIFT[0] * TILE
    y = float(am.group(2)) + SHIFT[1] * TILE
    assert in_region(text, REGION, x, y), f"({x:g},{y:g}) is outside region {REGION}"
    for ix, iy in re.findall(r'<object id="\d+" name="[^"]*" gid="[35]" x="([\d.]+)" y="([\d.]+)"', text):
        assert abs(float(ix) - x) + abs(float(iy) - y) >= 2 * TILE, f"too close to the icon at {ix},{iy}"
    obj = re.sub(r'<object id="\d+" name="[^"]*"', f'<object id="{oid}" name="{NAME} (MOD Test Run)"', obj, count=1)
    obj = re.sub(r' x="[\d.]+"', f' x="{x:g}"', obj, count=1)
    obj = re.sub(r' y="[\d.]+"', f' y="{y:g}"', obj, count=1)
    obj = obj.replace(f'value="{CLONE_OF}"', f'value="{NAME}"')
    text = text[:m.end()] + obj + text[m.end():]
    text = text[:nxt.start()] + f'nextobjectid="{oid + 1}"' + text[nxt.end():]
    write(rel, bom + text.encode())

    # 5. strings: own <lang>_newmission.csv per language (en text where no translation)
    langs = 0
    for name in pak.namelist():
        lm = re.fullmatch(r"Language/([a-z\-]+)\.csv", name)
        if lm:
            lang = lm.group(1)
            src, bom = read(name)
            tab = "\t" if src.split("\r\n", 1)[0].endswith("\t") else ""
            rows = "".join(f"{k}\t{v}{tab}\r\n" for k, v in LOC.get(lang, LOC["en"]).items())
            write(f"Language/{lang}_newmission.csv", bom + rows.encode())
            langs += 1

    with open(os.path.join(out, "README.txt"), "w", encoding="utf-8") as f:
        f.write("NewMissionTest — generated by tools/mods/make_new_mission_test.py. Adds mission mod_test_01 next to the Angler's Reef bar.\n")
    print(f"NewMissionTest written to {out}: mission {NAME} (map object {oid} at {x:g},{y:g}), rules, script, {langs} language files")


if __name__ == "__main__":
    main()
