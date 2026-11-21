"""Extract Lua API bindings from SteamWorld Heist 2.exe.

Engine binding pattern (per function):
    lua_pushlstring(L, "Name", len)          ; call PUSHLSTRING, rdx = lea name
    ud = lua_newuserdatauv(L, 8, 1)          ; call NEWUD
    *ud = <C++ implementation>               ; lea rcx, impl ; mov [rax], rcx
    lua_pushcclosure(L, <trampoline>, 1)     ; call PUSHCCLOSURE, rdx = lea tramp
    lua_rawset/settable(L, -3)
"""
import re, sys, collections
from pe import Exe

PUSHLSTRING, NEWUD, PUSHCCLOSURE = 0x85a6a0, 0x85b620, 0x85a7f0

def lea_target(i):
    m = re.search(r'\[rip ([+-]) (0x[0-9a-f]+)\]', i.op_str)
    if not m: return None
    d = int(m.group(2), 16) * (1 if m.group(1) == '+' else -1)
    return i.address + i.size + d

def scan(x, start, end):
    ins = [i for i in x.dis(start, (end - start)) if i.address < end]
    out, cur = [], None
    for k, i in enumerate(ins):
        if i.mnemonic == 'call' and i.op_str == hex(PUSHLSTRING):
            # nearest preceding lea rdx
            for j in range(k - 1, max(k - 6, 0), -1):
                if ins[j].mnemonic == 'lea' and ins[j].op_str.startswith('rdx'):
                    cur = {'name': x.cstr(lea_target(ins[j])), 'impl': None, 'tramp': None, 'site': i.address}
                    break
        elif cur and i.mnemonic == 'lea' and i.op_str.startswith('rcx, [rip') and cur['impl'] is None:
            cur['impl'] = lea_target(i)
        elif cur and i.mnemonic == 'lea' and i.op_str.startswith('rdx, [rip'):
            cur['tramp'] = lea_target(i)
        elif cur and i.mnemonic == 'call' and i.op_str == hex(PUSHCCLOSURE):
            out.append(cur); cur = None
    return out

if __name__ == '__main__':
    x = Exe()
    funcs = collections.Counter()
    for site in x.calls.get(PUSHCCLOSURE, []):
        f = x.func_start(site)
        if f: funcs[f] += 1
    allb = []
    for (s, e), n in funcs.most_common():
        if n < 3: continue
        b = scan(x, s, e)
        print(f'# registrar {hex(s)}-{hex(e)}: {n} closures, {len(b)} named bindings', file=sys.stderr)
        for r in b: r['registrar'] = s
        allb += b
    for r in sorted(allb, key=lambda r: (r['registrar'], r['name'])):
        print(f"{hex(r['registrar'])}\t{r['name']}\t{hex(r['impl'] or 0)}\t{hex(r['tramp'] or 0)}")
