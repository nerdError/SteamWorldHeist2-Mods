"""Address map from the Steam exe (the build all exe mods are written for) to another build of the game (GOG).

Both builds come from the same source, so functions are the same code, only some of them sit at other addresses.
  1. Functions = .pdata entries. Each one is disassembled and normalised (rip-relative / branch rel32 operands
     zeroed); the two lists are aligned on those signatures (difflib), so a matched pair is the same function.
  2. Code ranges: each matched function (plus the padding up to the next one) maps with its own delta.
  3. Data ranges: in matched functions every rip-relative operand / branch target gives a pair
     (steam target -> other target); the import table adds a pair per imported function. Consecutive targets with
     the same delta are merged into one range; targets whose pairs disagree are dropped.
Only addresses inside a range can be mapped; anything else is "not available in this build".

  python tools/re/build_map.py <steam exe> <other exe> <name>
writes SWH2.Core/Maps/<name>.map (used by ExeImage.Map) and Mods/Coop/native/map_<name>.h (used by the Coop dll).
"""
import bisect, collections, difflib, hashlib, os, struct, sys
import pefile, capstone

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


class Build:
    def __init__(self, path):
        self.pe = pefile.PE(path)
        self.base = self.pe.OPTIONAL_HEADER.ImageBase
        self.img = self.pe.get_memory_mapped_image()
        self.raw = open(path, 'rb').read()
        self.sha = hashlib.sha256(self.raw).hexdigest().upper()
        self.stamp = self.pe.FILE_HEADER.TimeDateStamp
        self.size = self.pe.OPTIONAL_HEADER.SizeOfImage
        self.funcs = sorted({(e.struct.BeginAddress, e.struct.EndAddress) for e in self.pe.DIRECTORY_ENTRY_EXCEPTION})
        self.cs = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        self.cs.detail = True
        self.iat = {}
        for d in self.pe.DIRECTORY_ENTRY_IMPORT:
            for i in d.imports:
                key = (d.dll.lower(), i.name or i.ordinal)
                self.iat[key] = i.address - self.base

    def analyse(self, begin, end):
        """(signature, [(offset in function, absolute target)]) of a function."""
        code = bytearray(self.img[begin:end])
        refs = []
        for ins in self.cs.disasm(bytes(code), begin):
            # rel32 of a branch, or disp32 of a rip-relative operand: zero it in the signature, remember its target
            off = ins.address - begin
            target = None
            if ins.disp_offset and any(op.type == capstone.x86.X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP
                                       for op in ins.operands):
                field = off + ins.disp_offset
                target = ins.address + ins.size + ins.disp
            elif ins.imm_offset and ins.imm_size == 4 and ins.group(capstone.CS_GRP_BRANCH_RELATIVE):
                field = off + ins.imm_offset
                target = ins.operands[0].imm
            elif ins.disp_offset and ins.disp_size == 4 and 0x100000 <= ins.disp < self.size:
                # image-relative disp32, e.g. jump tables: mov r8, [r14 + rax*8 + table_rva] with r14 = image base
                field = off + ins.disp_offset
                target = ins.disp
            if target is None: continue
            code[field:field + 4] = b'\0\0\0\0'
            refs.append((off, target))
        return hashlib.md5(bytes(code)).digest() + struct.pack('<I', end - begin), refs


def build(steam_path, other_path):
    s, o = Build(steam_path), Build(other_path)
    sa = [s.analyse(b, e) for b, e in s.funcs]
    oa = [o.analyse(b, e) for b, e in o.funcs]
    sm = difflib.SequenceMatcher(None, [x[0] for x in sa], [x[0] for x in oa], autojunk=False)
    pairs = []  # (steam function index, other function index)
    for blk in sm.get_matching_blocks():
        pairs += [(blk.a + k, blk.b + k) for k in range(blk.size)]

    code = []  # (steam start, steam end, delta)
    votes = collections.defaultdict(collections.Counter)
    for i, j in pairs:
        (sb, se), (ob, oe) = s.funcs[i], o.funcs[j]
        # include the padding up to the next function when it is the same size in both builds
        sn = s.funcs[i + 1][0] if i + 1 < len(s.funcs) else se
        on = o.funcs[j + 1][0] if j + 1 < len(o.funcs) else oe
        end = sn if sn - sb == on - ob else se
        code.append((sb, end, ob - sb))
        for (_, st), (_, ot) in zip(sa[i][1], oa[j][1]):
            if not sb <= st < se: votes[st][ot - st] += 1
    for key, rva in s.iat.items():
        if key in o.iat: votes[rva][o.iat[key] - rva] += 1000
    code_starts = [c[0] for c in code]

    def in_code(rva):
        k = bisect.bisect_right(code_starts, rva) - 1
        return k >= 0 and code[k][0] <= rva < code[k][1]

    text = next(x for x in s.pe.sections if x.Name.rstrip(b'\0') == b'.text')
    text_end = text.VirtualAddress + text.Misc_VirtualSize
    # code outside matched functions stays unmapped; data needs pairs that all agree
    points = sorted((t, c.most_common(1)[0][0]) for t, c in votes.items()
                    if len(c) == 1 and not in_code(t) and text_end <= t < s.size)
    data = []
    for t, d in points:
        if data and data[-1][2] == d: data[-1][1] = t + 8  # an 8-byte slot (pointer / IAT entry) past the last target
        else: data.append([t, t + 8, d])

    ranges = sorted([tuple(c) for c in code] + [tuple(r) for r in data])
    merged = []
    for a, b, d in ranges:
        if merged and merged[-1][2] == d and a <= merged[-1][1]:
            merged[-1][1] = max(merged[-1][1], b)
        elif merged and a < merged[-1][1]:
            merged[-1][1] = a  # overlap with another delta: the later range wins from its start
            merged.append([a, b, d])
        else:
            merged.append([a, b, d])
    stats = f"{len(pairs)}/{len(s.funcs)} functions matched, {len(points)} data targets, {len(merged)} ranges"
    return s, o, merged, stats


def main():
    if len(sys.argv) != 4: sys.exit(__doc__)
    s, o, ranges, stats = build(sys.argv[1], sys.argv[2])
    name = sys.argv[3]
    print(stats)
    head = (f"steam {s.sha}\n{name} {o.sha}\nstamp {o.stamp}\n"
            f"# start end delta (Steam RVAs, hex): {stats}. Generated by tools/re/build_map.py, do not edit.\n")
    path = os.path.join(ROOT, 'SWH2.Core', 'Maps', f'{name}.map')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', newline='\n') as f:
        f.write(head)
        for a, b, d in ranges: f.write(f"{a:x} {b:x} {d:x}\n" if d >= 0 else f"{a:x} {b:x} -{-d:x}\n")
    hdr = os.path.join(ROOT, 'Mods', 'Coop', 'native', f'map_{name}.h')
    with open(hdr, 'w', newline='\n') as f:
        f.write(f"/* Steam -> {name} exe address map ({stats}). Generated by tools/re/build_map.py, do not edit. */\n")
        f.write(f"#define MAP_{name.upper()}_STAMP {o.stamp}u\n")
        f.write(f"static const MapRange map_{name}[] = {{\n")
        for a, b, d in ranges: f.write(f"    {{0x{a:x}, 0x{b:x}, {d}}},\n")
        f.write("};\n")
    print("wrote", path, "and", hdr)


if __name__ == '__main__':
    main()
