"""
Generates docs/FILE_INDEX.md: a complete, machine-made index of every file
SteamWorld Heist 2 ships in Bundle/Core (Game.pak, Game2.pak and loose folders).

For XML-like files (.xml, .de, .me, .tmx, .tsx, .tx) it also records the root
element, which definition elements the file contains (with counts) and the
first line of the leading doc comment, if any.

Usage:
    python tools/gen_file_index.py [path-to-game-folder]

The hand-written overview lives in docs/FILE_MAP.md; this file is the raw index.
"""
import collections
import os
import re
import sys
import zipfile
import xml.etree.ElementTree as ET

DEFAULT_GAME = r"C:\Program Files (x86)\Steam\steamapps\common\SteamWorld Heist 2"
XML_EXT = {".xml", ".de", ".me", ".tmx", ".tsx", ".tx"}
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def human(n):
    for unit in ("B", "KB", "MB"):
        if n < 1024:
            return f"{n:.0f} {unit}"
        n /= 1024
    return f"{n:.1f} GB"


def describe_xml(data):
    text = data.decode("utf-8-sig", errors="replace")
    comment = ""
    m = re.search(r"<!--(.*?)-->", text[:6000], re.S)
    root_pos = re.search(r"<[A-Za-z]", text)
    if m and root_pos and m.start() < text.find(">", root_pos.end()) + 2000:
        for line in m.group(1).strip().splitlines():
            line = line.strip(" \t-*=")
            if line:
                comment = line[:140]
                break
    try:
        root = ET.fromstring(text.encode("utf-8"))
    except ET.ParseError:
        return "?", "", comment
    kids = collections.Counter(c.tag for c in root)
    names = [c.get("Name") for c in root if c.get("Name") and c.get("Abstract") not in ("1", "true", "True")]
    kinds = ", ".join(f"{t}×{n}" for t, n in kids.most_common(4))
    if names:
        sample = ", ".join(names[:6]) + (" …" if len(names) > 6 else "")
        kinds += f" — e.g. {sample}"
    return root.tag, kinds, comment


def collect(game):
    core = os.path.join(game, "Bundle", "Core")
    sources = {}
    for pak in ("Game.pak", "Game2.pak"):
        z = zipfile.ZipFile(os.path.join(core, pak))
        for info in z.infolist():
            if not info.is_dir():
                sources[info.filename] = (pak, info.file_size, lambda z=z, n=info.filename: z.read(n))
    for top in sorted(os.listdir(core)):
        full_top = os.path.join(core, top)
        if not os.path.isdir(full_top) or top == "GamePaks":
            continue
        for d, _, files in os.walk(full_top):
            for f in files:
                full = os.path.join(d, f)
                rel = os.path.relpath(full, core).replace(os.sep, "/")
                sources[rel] = ("loose", os.path.getsize(full), lambda p=full: open(p, "rb").read())
    return sources


def main():
    game = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_GAME
    sources = collect(game)

    by_dir = collections.defaultdict(list)
    for path in sources:
        by_dir[os.path.dirname(path)].append(path)

    out = []
    out.append("# SteamWorld Heist 2 — полный индекс файлов (автогенерация)\n")
    out.append("> Сгенерировано `tools/gen_file_index.py`. Не редактировать вручную — перезапустите скрипт.")
    out.append("> Обзор «что за что отвечает» — в [FILE_MAP.md](FILE_MAP.md).\n")
    out.append("Источник: `pak` = Game.pak / Game2.pak, `loose` = отдельный файл в `Bundle/Core`.\n")

    out.append("## Сводка по папкам\n")
    out.append("| Папка | Файлов | Размер | Где лежит | Типы |")
    out.append("|---|---:|---:|---|---|")
    by_top = collections.defaultdict(list)
    for path in sources:
        by_top["/".join(path.split("/")[:2]) if path.count("/") >= 2 else os.path.dirname(path)].append(path)
    for d in sorted(by_top):
        files = by_top[d]
        size = sum(sources[f][1] for f in files)
        where = ", ".join(sorted({sources[f][0] for f in files}))
        exts = collections.Counter(os.path.splitext(f)[1] or "(none)" for f in files)
        ext_s = ", ".join(f"{e}×{n}" for e, n in exts.most_common(4))
        out.append(f"| `{d or '.'}` | {len(files)} | {human(size)} | {where} | {ext_s} |")

    out.append("\n## Файлы с данными (XML / .de / .me / Tiled)\n")
    out.append("Для каждого: корневой элемент, какие определения внутри, первая строка документации из шапки файла.\n")
    for d in sorted(by_dir):
        xml_files = [f for f in sorted(by_dir[d]) if os.path.splitext(f)[1].lower() in XML_EXT]
        if not xml_files:
            continue
        if len(xml_files) > 40 and d != "Definitions":
            out.append(f"\n### `{d}/` — {len(xml_files)} файлов (свёрнуто)\n")
            out.append(", ".join(f"`{os.path.basename(f)}`" for f in xml_files))
            continue
        out.append(f"\n### `{d}/`\n")
        out.append("| Файл | Источник | Размер | Корень | Содержимое | Комментарий в шапке |")
        out.append("|---|---|---:|---|---|---|")
        for f in xml_files:
            src, size, read = sources[f]
            root, kinds, comment = describe_xml(read())
            esc = lambda s: s.replace("|", "\\|")
            out.append(f"| `{os.path.basename(f)}` | {src} | {human(size)} | `{root}` | {esc(kinds)} | {esc(comment)} |")

    out.append("\n## Прочие файлы (Lua, ассеты)\n")
    for d in sorted(by_dir):
        other = [f for f in sorted(by_dir[d]) if os.path.splitext(f)[1].lower() not in XML_EXT]
        if not other:
            continue
        src = ", ".join(sorted({sources[f][0] for f in other}))
        if len(other) > 30:
            exts = collections.Counter(os.path.splitext(f)[1] for f in other)
            out.append(f"- `{d}/` ({src}) — {len(other)} файлов: " + ", ".join(f"{e}×{n}" for e, n in exts.most_common()))
        else:
            out.append(f"- `{d}/` ({src}): " + ", ".join(f"`{os.path.basename(f)}`" for f in other))

    dst = os.path.join(REPO, "docs", "FILE_INDEX.md")
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    with open(dst, "w", encoding="utf-8") as fh:
        fh.write("\n".join(out) + "\n")
    print(f"Wrote {dst} ({len(sources)} files indexed)")


if __name__ == "__main__":
    main()
