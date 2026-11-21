"""DEVELOPER TOOL. Players use the Turbo / Challenges mods in SWH2 Mod Loader (same bytes, ported to C#:
Mods/Turbo/Turbo.cs, Mods/Challenges/NoCheckpoints.cs). Use this only to prototype new exe patches; the
loader restores an exe patched by this script on its next Apply.

Experimental patches for SteamWorld Heist 2.exe (see docs/EXE_NOTES.md, "Скорость боя").

    python tools/re/exe_patch.py status
    python tools/re/exe_patch.py apply turbo --enemy 3 --intro 6
    python tools/re/exe_patch.py apply turbo no_checkpoints          (several patches at once)
    python tools/re/exe_patch.py apply tactics_speed --speed 3      (old test: whole battle x2/x3/x4)
    python tools/re/exe_patch.py restore

The original exe is copied to <game>/SWH2ModLoader/Backup/ before the first patch. Every apply builds the
patched exe from that pristine copy in memory (list ALL wanted patches in one apply; so re-applying with other values is fine) and writes it back
in place ("r+b" + truncate: opening the exe with "wb" fails in this setup). Original bytes are verified first.
"""
import argparse, os, shutil, struct, sys
import pefile, keystone

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\SteamWorld Heist 2"
EXE = os.path.join(GAME, "SteamWorld Heist 2.exe")
BACKUP = os.path.join(GAME, "SWH2ModLoader", "Backup", "SteamWorld Heist 2.exe")

# --- addresses (RVA) in the 2024-10-04 build ---
WORLD_UPDATE_SCALE_LOAD = 0x480b6f   # movss xmm1, [rip+1.0]   (no slow-mo -> default time scale)
WORLD_UPDATE_SCALE_STORE = 0x480b77  # movss [rdx+0x1b70], xmm1 (rdx = game state, rsi = tactics world)
SEQUENCE_SYSTEM_INDEX = 0xe9fbbc     # static int: slot of SequenceSimulator in world->systems[]
FLOAT_CONST = {1.0: 0xbd1214, 2.0: 0xb549a4, 3.0: 0xbd1368, 4.0: 0xbd13a8}
# vtables of imf::Sequence* classes
VT = {
    "EnemyIntro": 0xb9edc8, "TurretIntro": 0xba3388, "Reinforcements": 0xb9e870,
    "Conversation": 0xb802b0, "IntermissionCutscene": 0xb9e640, "PlayMovie": 0xb803b8,
}
SEQ_FLAG_CUTSCENE = 0x02 | 0x10      # CutsceneSkippable | Letterbox (Sequence+0xc)
# bool HasRestorePoint(state+0x4b8): its only callers are the restore itself (0xc6779), the pause menu
# "Load Checkpoint" button SetEnabled (0x1bd2d7) and the objective-fail popup (0x3ac704, picks the
# "no checkpoint" text). SaveRestorePoints=0 in game_constants.xml is NOT an option: the save code does
# `while (size >= SaveRestorePoints) pop_front()` (0xc6de6) and would pop an empty deque.
HAS_RESTORE_POINT = 0xc0f80
HAS_RESTORE_POINT_ORIG = bytes.fromhex("4883b9b000000000")   # cmp qword ptr [rcx+0xb0], 0
PATCHES = ("turbo", "tactics_speed", "no_checkpoints")

KS = keystone.Ks(keystone.KS_ARCH_X86, keystone.KS_MODE_64)


def asm(code, at):
    return bytes(KS.asm(code, at)[0])


def rip_movss_xmm1_load(at, target):
    return bytes.fromhex("f30f100d") + struct.pack("<i", target - (at + 8))


def find_vtable(pe, img, class_name):
    """MSVC RTTI: TypeDescriptor name '.?AV<cls>@imf@@' -> COL -> vtable."""
    import re
    base = pe.OPTIONAL_HEADER.ImageBase
    name = f".?AV{class_name}@imf@@".encode() + b"\0"
    td = img.find(name) - 0x10
    for m in re.finditer(re.escape(struct.pack("<I", td)), img):
        col = m.start() - 0x0C
        if struct.unpack_from("<I", img, col)[0] != 1:
            continue
        p = img.find(struct.pack("<Q", base + col))
        if p > 0:
            return p + 8
    raise LookupError(class_name)


def turbo_code(at, enemy, intro, vt):
    """Runs instead of `movss [rdx+0x1b70], xmm1` in the tactics world update.
    In: rdx = game state, rsi = tactics world, xmm1 = time scale the game computed (1.0 / slow-mo / 0 = pause).
    Clobbers only rax/rcx/flags (both are overwritten right after the original store)."""
    ret = WORLD_UPDATE_SCALE_STORE + 8
    same = "\n".join(f"lea rcx, [rip + {v - 0:#x} - ({{here}})]" for v in [])  # placeholder (unused)
    lines = [
        # current sequence = front of SequenceSimulator's queue
        f"movsxd rax, dword ptr [rip + {SEQUENCE_SYSTEM_INDEX - (at + 7):#x}]",
        "mov rax, qword ptr [rsi + rax*8 + 8]",
        "test rax, rax", "jz faction",
        "mov rax, qword ptr [rax + 0x10]",
        "test rax, rax", "jz faction",
        "mov rcx, qword ptr [rax]",
        "cmp rcx, qword ptr [rax + 8]", "je faction",
        "mov rax, qword ptr [rcx]",
        "test rax, rax", "jz faction",
        f"test dword ptr [rax + 0xc], {SEQ_FLAG_CUTSCENE:#x}", "jnz done",      # cutscene-like -> x1
        "mov rax, qword ptr [rax]",                                             # vtable
    ]
    for name, label in [("EnemyIntro", "intro"), ("TurretIntro", "intro"), ("Reinforcements", "intro"),
                        ("Conversation", "done"), ("IntermissionCutscene", "done"), ("PlayMovie", "done"),
                        ("Victory", "done"), ("Defeat", "done")]:
        lines += [f"lea rcx, [rip + VT_{name}]", "cmp rax, rcx", f"je {label}"]
    lines += [
        "faction:",
        "movsxd rax, dword ptr [rdx + 0x1ef8]",
        "test rax, rax", "js done",
        "mov rcx, qword ptr [rdx + 0x1ee8]",
        "sub rcx, qword ptr [rdx + 0x1ee0]",
        "sar rcx, 5",
        "cmp rax, rcx", "jae done",
        "shl rax, 5",
        "add rax, qword ptr [rdx + 0x1ee0]",
        "cmp qword ptr [rax + 0x10], 7", "jne enemy",                          # std::string size
        "cmp qword ptr [rax + 0x18], 0x10", "jb inline",                       # SSO?
        "mov rax, qword ptr [rax]",
        "inline:",
        "mov rcx, qword ptr [rax]",
        "shl rcx, 8",
        "mov rax, 0x73746f62776f6300",                                          # "cowbots" << 8
        "cmp rcx, rax", "je done",
        "enemy:",
        "mulss xmm1, dword ptr [rip + k_enemy]",
        "jmp done",
        "intro:",
        "mulss xmm1, dword ptr [rip + k_intro]",
        "done:",
        "movss dword ptr [rdx + 0x1b70], xmm1",
        f"jmp {ret:#x}",
    ]
    # two 4-byte placeholders after the code become the multipliers
    src = "\n".join(lines + ["k_enemy:", "nop; nop; nop; nop", "k_intro:", "nop; nop; nop; nop"])
    return assemble_with_absolute(src, at, vt)[:-8] + struct.pack("<ff", enemy, intro)


def assemble_with_absolute(src, at, vt):
    """Assemble `src` where `[rip + VT_<name>]` refers to absolute RVA vt[name]. Each such lea is 7 bytes,
    so: assemble with placeholder disp 0, find each lea's address, then patch its disp32."""
    names = []
    s2 = src
    for name in vt:
        tag = f"[rip + VT_{name}]"
        if tag in s2:
            s2 = s2.replace(tag, "[rip + 0x7fff0000]")      # force disp32 encoding
            names.append(name)
    code = bytearray(asm(s2, at))
    import capstone
    cs = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    order = [n for line in src.split("\n") for n in vt if f"[rip + VT_{n}]" in line]
    k = 0
    for ins in cs.disasm(bytes(code), at):
        if ins.mnemonic == "lea" and "0x7fff0000" in ins.op_str:
            name = order[k]; k += 1
            o = ins.address - at + ins.size - 4
            code[o:o + 4] = struct.pack("<i", vt[name] - (ins.address + ins.size))
    assert k == len(order), (k, len(order))
    return bytes(code)


def build(orig, names, a):
    """Return the patched exe bytes built from the pristine `orig` with all `names` applied."""
    pe = pefile.PE(data=orig, fast_load=True)
    data = bytearray(orig)

    def put(off, old, new):
        assert data[off:off + len(old)] == old, f"unexpected original bytes at {off:#x}"
        data[off:off + len(new)] = new

    for which in names:
        if which == "tactics_speed":
            s = WORLD_UPDATE_SCALE_LOAD
            put(pe.get_offset_from_rva(s), rip_movss_xmm1_load(s, FLOAT_CONST[1.0]),
                rip_movss_xmm1_load(s, FLOAT_CONST[a.speed]))
        elif which == "no_checkpoints":
            # challenge: HasRestorePoint() always false -> no "Load Checkpoint" in missions
            h = HAS_RESTORE_POINT
            put(pe.get_offset_from_rva(h), HAS_RESTORE_POINT_ORIG, asm("xor al, al; ret", h))
            print(f"  HasRestorePoint @ {h:#x} -> false")
        else:
            assert which == "turbo"
            add_turbo(pe, data, put, a)
    return bytes(data)


def add_turbo(pe, data, put, a):
    img = pe.get_memory_mapped_image()
    vt = dict(VT)
    vt["Victory"] = find_vtable(pe, img, "SequenceVictory")
    vt["Defeat"] = find_vtable(pe, img, "SequenceDefeat")

    # new executable section ".swh2" (4 KB) appended after the last one
    oh, fh = pe.OPTIONAL_HEADER, pe.FILE_HEADER
    last = pe.sections[-1]
    hdr_off = last.get_file_offset() + 40
    assert hdr_off + 40 <= oh.SizeOfHeaders and not any(data[hdr_off:hdr_off + 40])
    va = (last.VirtualAddress + last.Misc_VirtualSize + oh.SectionAlignment - 1) // oh.SectionAlignment * oh.SectionAlignment
    raw = len(data)
    assert raw % oh.FileAlignment == 0 and raw == last.PointerToRawData + last.SizeOfRawData, "exe has an overlay"
    size = 0x1000
    sec = struct.pack("<8sIIIIIIHHI", b".swh2", size, va, size, raw, 0, 0, 0, 0, 0x60000020)  # code|exec|read
    put(hdr_off, bytes(40), sec)
    put(fh.get_file_offset() + 2, struct.pack("<H", fh.NumberOfSections), struct.pack("<H", fh.NumberOfSections + 1))
    soi = oh.get_file_offset() + 56
    put(soi, struct.pack("<I", oh.SizeOfImage), struct.pack("<I", va + size))

    code = turbo_code(va, a.enemy, a.intro, vt)
    assert len(code) <= size
    data += code + bytes(size - len(code))

    st = WORLD_UPDATE_SCALE_STORE
    put(pe.get_offset_from_rva(st), bytes.fromhex("f30f118a701b0000"), asm(f"jmp {va:#x}", st) + b"\x90" * 3)
    print(f"  .swh2 section @ {va:#x}, code {len(code)} bytes; hook @ {st:#x}")


def write_exe(content):
    with open(EXE, "r+b") as f:
        f.seek(0)
        f.write(content)
        f.truncate()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=["status", "apply", "restore"])
    ap.add_argument("names", nargs="*", metavar="name", help="apply: " + ", ".join(PATCHES))
    ap.add_argument("--speed", type=float, default=3.0, help="tactics_speed: 2, 3 or 4")
    ap.add_argument("--enemy", type=float, default=3.0, help="turbo: time scale during non-player turns")
    ap.add_argument("--intro", type=float, default=6.0, help="turbo: time scale while enemies/turrets/reinforcements are introduced")
    a = ap.parse_args()

    if not os.path.exists(BACKUP):
        if a.cmd != "apply":
            sys.exit("no backup yet (nothing was patched)")
        os.makedirs(os.path.dirname(BACKUP), exist_ok=True)
        shutil.copy2(EXE, BACKUP)
        print("backed up original exe")
    orig = open(BACKUP, "rb").read()
    cur = open(EXE, "rb").read()

    if a.cmd == "status":
        if cur == orig:
            print("exe: original")
        else:
            pe = pefile.PE(data=cur, fast_load=True)
            found = []
            if ".swh2" in [s.Name.rstrip(b"\0").decode() for s in pe.sections]:
                found.append("turbo")
            o = pe.get_offset_from_rva(WORLD_UPDATE_SCALE_LOAD)
            if cur[o:o + 8] != orig[o:o + 8]:
                found.append("tactics_speed")
            o = pe.get_offset_from_rva(HAS_RESTORE_POINT)
            if cur[o:o + 3] == bytes.fromhex("30c0c3"):
                found.append("no_checkpoints")
            print("exe: patched (" + (", ".join(found) or "unknown patch") + ")")
        return
    if a.cmd == "restore":
        write_exe(orig)
        print("exe restored from backup")
        return
    names = list(dict.fromkeys(a.names))
    if not names or any(n not in PATCHES for n in names):
        sys.exit("unknown or missing patch; available: " + ", ".join(PATCHES))
    if "turbo" in names and "tactics_speed" in names:
        sys.exit("turbo and tactics_speed both change the battle time scale; pick one")
    if "tactics_speed" in names and a.speed not in FLOAT_CONST:
        sys.exit(f"--speed must be one of {sorted(FLOAT_CONST)}")
    write_exe(build(orig, names, a))
    info = {"turbo": f"turbo (enemy x{a.enemy}, intro x{a.intro})", "tactics_speed": f"tactics_speed (x{a.speed})"}
    print("applied " + ", ".join(info.get(n, n) for n in names))


if __name__ == "__main__":
    main()
