"""Tiny static-analysis helpers for SteamWorld Heist 2.exe (no IDA/Ghidra needed)."""
import re, struct, functools
import pefile, capstone

EXE = r"C:\Program Files (x86)\Steam\steamapps\common\SteamWorld Heist 2\SteamWorld Heist 2.exe"

class Exe:
    def __init__(self, path=EXE):
        self.pe = pefile.PE(path, fast_load=True)
        self.base = self.pe.OPTIONAL_HEADER.ImageBase
        self.img = self.pe.get_memory_mapped_image()
        self.secs = {s.Name.rstrip(b'\0').decode(): s for s in self.pe.sections}
        t = self.secs['.text']
        self.text_rva, self.text = t.VirtualAddress, self.img[t.VirtualAddress:t.VirtualAddress + t.Misc_VirtualSize]
        self.cs = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        self.cs.detail = False

    def find_str(self, s):
        b = s.encode() + b'\0'
        return [m.start() for m in re.finditer(re.escape(b), self.img) if self.img[m.start()-1:m.start()] == b'\0']

    def cstr(self, rva, n=200):
        e = self.img.find(b'\0', rva, rva + n)
        return self.img[rva:e].decode('latin1')

    @functools.cached_property
    def rip_refs(self):
        """target_rva -> [instr_rva] for lea/mov reg,[rip+disp32] (REX.W 8D/8B) and call/jmp rel32."""
        refs = {}
        t, base = self.text, self.text_rva
        for m in re.finditer(rb'[\x48\x4c][\x8d\x8b](?=[\x05\x0d\x15\x1d\x25\x2d\x35\x3d])', t):
            i = m.start()
            disp = struct.unpack_from('<i', t, i + 3)[0]
            refs.setdefault(base + i + 7 + disp, []).append(base + i)
        return refs

    @functools.cached_property
    def calls(self):
        refs = {}
        t, base = self.text, self.text_rva
        for m in re.finditer(rb'\xe8', t):
            i = m.start()
            tgt = base + i + 5 + struct.unpack_from('<i', t, i + 1)[0]
            if base <= tgt < base + len(t):
                refs.setdefault(tgt, []).append(base + i)
        return refs

    def ptr_refs(self, rva):
        va = struct.pack('<Q', self.base + rva)
        return [m.start() for m in re.finditer(re.escape(va), self.img)]

    def dis(self, rva, n=40):
        out = []
        for ins in self.cs.disasm(self.img[rva:rva + n * 15], rva):
            out.append(ins); 
            if len(out) >= n: break
        return out

    @functools.cached_property
    def _pdata(self):
        self.pe.parse_data_directories([pefile.DIRECTORY_ENTRY['IMAGE_DIRECTORY_ENTRY_EXCEPTION']])
        ranges = sorted((e.struct.BeginAddress, e.struct.EndAddress) for e in getattr(self.pe, 'DIRECTORY_ENTRY_EXCEPTION', []))
        return ranges, [b for b, _ in ranges]

    def func_start(self, rva):
        """Find containing function via .pdata (x64 unwind table): (begin, end) or None."""
        import bisect
        ranges, begins = self._pdata
        i = bisect.bisect_right(begins, rva) - 1
        return ranges[i] if i >= 0 and ranges[i][0] <= rva < ranges[i][1] else None

def xrefs_any(x, target, slack=(0, 1, 4)):
    """Brute-force RIP-relative references: disp32 at offset j with j+4+imm+disp == target.
    Returns candidate disp offsets (rva); verify with disassembly."""
    import numpy as np
    t = np.frombuffer(x.text, dtype=np.uint8)
    d = (t[:-3].astype(np.int64) | (t[1:-2].astype(np.int64) << 8) | (t[2:-1].astype(np.int64) << 16) | (t[3:].astype(np.int64) << 24))
    d = np.where(d >= 2**31, d - 2**32, d)
    pos = np.arange(len(d), dtype=np.int64) + x.text_rva
    out = []
    for s in slack:
        out += (pos[(pos + 4 + s + d) == target]).tolist()
    return sorted(set(out))

def verify_xrefs(x, target, back=12):
    """Disassemble backwards candidates to find the actual instructions referencing target."""
    res = []
    for j in xrefs_any(x, target):
        for k in range(1, back):
            ins = list(x.cs.disasm(x.img[j - k:j + 16], j - k))
            if ins and ins[0].address + ins[0].size > j and 'rip' in ins[0].op_str:
                i = ins[0]
                import re
                m = re.search(r'\[rip ([+-]) (0x[0-9a-f]+)\]', i.op_str)
                if m and i.address + i.size + int(m.group(2), 16) * (1 if m.group(1) == '+' else -1) == target:
                    res.append(i); break
    return res

def vtables_for(x, rtti_name_rva):
    """MSVC x64 RTTI: TypeDescriptor = name-0x10; COL has TD rva at +0x0C; vtable[-1] = &COL."""
    import struct, re
    td = rtti_name_rva - 0x10
    out = []
    for m in re.finditer(re.escape(struct.pack('<I', td)), x.img):
        col = m.start() - 0x0C
        sig, off = struct.unpack_from('<II', x.img, col)
        if sig != 1: continue
        for p in x.ptr_refs(col):
            out.append((p + 8, off))
    return out

def class_of_vtable(x, vt):
    import struct
    col = struct.unpack_from('<Q', x.img, vt - 8)[0] - x.base
    td = struct.unpack_from('<I', x.img, col + 12)[0]
    return x.cstr(td + 0x10, 300)
