"""Second binding pattern: string_view{name,len} on stack + `lea r8, impl` + call <templated helper>.
Finds all helpers by looking at known registrars, then every call site of those helpers anywhere."""
import re, sys, collections
from pe import Exe
from lua_bindings import lea_target, scan

x = Exe()
regs = [(0x32a700, 0x32e65f), (0x6735e0, 0x674901), (0x26f2f0, 0x270329)]

def pattern2(ins):
    out = []
    for k, i in enumerate(ins):
        if i.mnemonic != 'call': continue
        win = ins[max(0, k - 9):k]
        name = impl = None
        for j in win:
            if j.mnemonic == 'lea' and j.op_str.startswith('rax, [rip'):
                t = lea_target(j)
                if t and t >= 0xa00000: name = x.cstr(t)
            if j.mnemonic == 'lea' and j.op_str.startswith('r8, [rip'):
                t = lea_target(j)
                if t and t < 0xa00000: impl = t
            if j.mnemonic == 'mov' and re.match(r'qword ptr \[\w+ [+-] 0x[0-9a-f]+\], (0x[0-9a-f]+)$', j.op_str):
                ln = int(j.op_str.rsplit(', ', 1)[1], 16)
                if name and ln != len(name): name = None
        if name and impl and re.fullmatch(r'[A-Za-z_]\w*', name):
            out.append({'name': name, 'impl': impl, 'helper': int(i.op_str, 16), 'site': i.address})
    return out

helpers = collections.Counter()
for s, e in regs:
    for b in pattern2([i for i in x.dis(s, e - s) if i.address < e]):
        helpers[b['helper']] += 1
print('helpers', {hex(k): v for k, v in helpers.items()}, file=sys.stderr)

# every function that calls any helper
funcs = set()
for h in helpers:
    for site in x.calls.get(h, []):
        f = x.func_start(site)
        if f: funcs.add(f)
rows = {}
for s, e in sorted(funcs):
    ins = [i for i in x.dis(s, e - s) if i.address < e]
    for b in pattern2(ins):
        rows[(s, b['name'])] = (b['impl'], b['helper'])
    for b in scan(x, s, e):
        rows[(s, b['name'])] = (b['impl'] or 0, b['tramp'] or 0)
print('registrars', len(funcs), 'bindings', len(rows), file=sys.stderr)
for (s, n), (impl, h) in sorted(rows.items()):
    print(f'{s:#x}\t{n}\t{impl:#x}\t{h:#x}')
