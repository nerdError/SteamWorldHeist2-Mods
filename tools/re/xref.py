"""Cross-reference queries over the ORIGINAL exe (backup), used for the multiplayer research (docs/MULTIPLAYER.md).

  python tools/re/xref.py str "Pre-simulating outcome of action \"{}\"..."   # code referencing a string (+ function)
  python tools/re/xref.py strings 0x391e30                                   # strings referenced inside a function
  python tools/re/xref.py callers 0x38b9b0                                   # direct callers (+ their functions)
  python tools/re/xref.py chain 0x391e30 3                                   # caller tree
  python tools/re/xref.py imports SDL_PollEvent SDL_Init                     # call sites of imported functions
  python tools/re/xref.py show 0x7f4d18 60                                   # disassembly (show.py)
"""
import re, struct, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pe
BACKUP = r"C:\Program Files (x86)\Steam\steamapps\common\SteamWorld Heist 2\SWH2ModLoader\Backup\SteamWorld Heist 2.exe"
X = pe.Exe(BACKUP if os.path.exists(BACKUP) else pe.EXE)
import show
show.x = X

def fn(rva):
    f = X.func_start(rva)
    return hex(f[0]) if f else None

def strrefs(s):
    """Code that loads the string with lea/mov rip-relative (strings in tables are referenced by pointer: ptr_refs)."""
    return [(hex(r), fn(r)) for a in X.find_str(s) for r in X.rip_refs.get(a, [])]

def strings_in(f):
    lo, hi = X.func_start(f)
    out = []
    for t, rs in X.rip_refs.items():
        if not 0xb47000 <= t < 0xe66000: continue
        s = X.cstr(t, 100)
        if len(s) > 3 and s.isprintable():
            out += [(hex(r), s) for r in rs if lo <= r < hi]
    return sorted(out)

def callers(f):
    return [(hex(c), fn(c)) for c in X.calls.get(f, [])]

def chain(f, depth=3, seen=None, ind=0):
    seen = set() if seen is None else seen
    fs = X.func_start(f); f0 = fs[0] if fs else f
    if f0 in seen or depth < 0: return
    seen.add(f0)
    cs = X.calls.get(f0, [])
    print("  " * ind + f"{f0:#x}  callers={len(cs)} lea-refs={[hex(l) for l in X.rip_refs.get(f0, [])][:4]} ptr-refs={[hex(p) for p in X.ptr_refs(f0)][:4]}")
    for c in cs[:8]: chain(c, depth - 1, seen, ind + 1)

def imports(names):
    X.pe.parse_data_directories()
    iat = {i.address - X.base: i.name.decode() for e in X.pe.DIRECTORY_ENTRY_IMPORT for i in e.imports if i.name}
    t, base = X.text, X.text_rva
    uses = {}
    for m in re.finditer(rb'\xff[\x15\x25]', t):
        i = m.start(); tgt = base + i + 6 + struct.unpack_from('<i', t, i + 2)[0]
        if tgt in iat: uses.setdefault(iat[tgt], []).append(base + i)
    for n in names:
        slot = [hex(a) for a, v in iat.items() if v == n]
        print(n, "IAT", slot, [(hex(u), fn(u)) for u in uses.get(n, [])])

if __name__ == "__main__":
    cmd, args = sys.argv[1], sys.argv[2:]
    if cmd == "str":
        for s in args: print(s, strrefs(s))
    elif cmd == "strings":
        for r, s in strings_in(int(args[0], 16)): print(r, s)
    elif cmd == "callers":
        for a in args: print(a, callers(int(a, 16)))
    elif cmd == "chain": chain(int(args[0], 16), int(args[1]) if len(args) > 1 else 3)
    elif cmd == "imports": imports(args)
    elif cmd == "show": show.show(int(args[0], 16), int(args[1]) if len(args) > 1 else 40)
