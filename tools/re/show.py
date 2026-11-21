import re, sys
from pe import Exe
x = Exe()
def tgt(i):
    m = re.search(r'\[rip ([+-]) (0x[0-9a-f]+)\]', i.op_str)
    return i.address + i.size + int(m.group(2), 16) * (1 if m.group(1) == '+' else -1) if m else None
def show(a, n=40, end=None):
    for i in x.dis(a, n):
        if end and i.address >= end: break
        c = ''
        t = tgt(i)
        if t is not None:
            c = f'; {t:#x}'
            if 0xa00000 <= t < 0xd00000 and i.mnemonic == 'lea':
                s = x.cstr(t, 60)
                if s.isprintable() and len(s) > 2: c += ' ' + repr(s)
        print(f'{i.address:#x}  {i.mnemonic:6} {i.op_str} {c}')
def callers(f):
    return [(hex(c), x.func_start(c) and hex(x.func_start(c)[0])) for c in x.calls.get(f, [])]
if __name__ == '__main__':
    a = int(sys.argv[1], 16); n = int(sys.argv[2]) if len(sys.argv) > 2 else 40
    show(a, n)
