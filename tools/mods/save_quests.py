"""Lists the quest state stored in a save slot: completed and active quests (for debugging quest mods).

Save format (docs/MODDING.md): 'Qest' records = tag + header + name; a 9-byte header means completed, 17 = active
(inferred by comparing saves; other header sizes are printed as 'other').

Usage: python tools/mods/save_quests.py [SAVE.dat]      (default: the newest savegame_*.dat of the last Steam account)
       add --all to print every name instead of a summary.
"""
import collections, glob, os, re, sys

ROOT = os.path.join(os.environ.get("APPDATA", ""), "Thunderful", "SteamWorld Heist II")
KIND = {9: "completed", 17: "active"}


def newest_save():
    files = glob.glob(os.path.join(ROOT, "*", "Save*", "savegame_*.dat"))
    return max(files, key=os.path.getmtime) if files else None


def quests(data):
    out = collections.defaultdict(list)
    for m in re.finditer(rb"Qest", data):
        mm = re.match(rb"([\x00-\xff]{0,24}?)([a-z][a-z0-9_]{2,80})(?![a-z0-9_])", data[m.end():m.end() + 120])
        if mm:
            out[KIND.get(len(mm.group(1)), "other")].append(mm.group(2).decode())
    return out


def main():
    args = [a for a in sys.argv[1:] if a != "--all"]
    path = args[0] if args else newest_save()
    if not path:
        sys.exit("no save found")
    q = quests(open(path, "rb").read())
    print(path)
    for kind in ("active", "completed", "other"):
        names = sorted(q.get(kind, []))
        mods = [n for n in names if n.startswith("mod_")]
        print(f"{kind}: {len(names)} (mod_*: {len(mods)})")
        shown = names if "--all" in sys.argv else [n for n in names if not n.startswith("mod_park_")][:40]
        for n in shown:
            print("   ", n)


if __name__ == "__main__":
    main()
