using System.Buffers.Binary;

namespace SWH2.Core;

/// <summary>
/// Tiny x64 emitter for code generated at install time (stubs whose content depends on the mods, e.g. GameOptions).
/// Only the handful of instruction forms the stubs use. Code is emitted for a fixed RVA (<see cref="Origin"/>);
/// rel32 operands go to absolute RVAs (game code, already mapped) or to labels in the same blob.
/// </summary>
public sealed class X64(uint origin)
{
    readonly List<byte> _b = [];
    readonly Dictionary<string, int> _labels = [];
    readonly List<(int Field, int End, string Label)> _fixups = [];

    public uint Origin { get; } = origin;
    public int Length => _b.Count;
    public uint Here => Origin + (uint)_b.Count;

    public X64 Bytes(params byte[] b) { _b.AddRange(b); return this; }
    public X64 Label(string name) { _labels.Add(name, _b.Count); return this; }
    public uint Address(string label) => Origin + (uint)_labels[label];

    X64 I32(int v) { _b.AddRange(BitConverter.GetBytes(v)); return this; }
    /// <summary>rel32 to an absolute RVA; <paramref name="tail"/> = bytes of the instruction after the field (imm8 etc.).</summary>
    X64 Rel(uint target, int tail = 0) => I32(checked((int)((long)target - (Here + 4 + tail))));
    X64 Rel(string label, int tail = 0) { _fixups.Add((_b.Count, _b.Count + 4 + tail, label)); return I32(0); }

    public X64 Call(uint target) => Bytes(0xE8).Rel(target);
    public X64 Jmp(uint target) => Bytes(0xE9).Rel(target);
    public X64 Jmp(string label) => Bytes(0xE9).Rel(label);
    public X64 Je(string label) => Bytes(0x0F, 0x84).Rel(label);
    public X64 Jne(string label) => Bytes(0x0F, 0x85).Rel(label);

    /// <summary>lea rax, [rip+label]</summary>
    public X64 LeaRax(string label) => Bytes(0x48, 0x8D, 0x05).Rel(label);
    /// <summary>mov qword [rsp+disp], rax</summary>
    public X64 MovRspRax(byte disp) => Bytes(0x48, 0x89).RspDisp(0, Disp8(disp));
    /// <summary>mov qword [rsp+disp], imm32 (sign-extended)</summary>
    public X64 MovRspImm(byte disp, int imm) => Bytes(0x48, 0xC7).RspDisp(0, Disp8(disp)).I32(imm);
    /// <summary>mov rax, imm64</summary>
    public X64 MovRaxImm64(ulong imm) => Bytes(0x48, 0xB8).Bytes(BitConverter.GetBytes(imm));
    /// <summary>lea rax/rcx/rdx/r8/r9, [rsp+disp]</summary>
    public X64 LeaRsp(Reg r, byte disp) => Bytes(r >= Reg.R8 ? (byte)0x4C : (byte)0x48, 0x8D).RspDisp((int)r & 7, Disp8(disp));
    static int Disp8(byte disp) => disp < 0x80 ? disp : throw new ArgumentOutOfRangeException(nameof(disp), "Use an offset below 0x80.");
    /// <summary>mov rcx, [rip+target] (a pointer global of the game)</summary>
    public X64 MovRcxRipQword(uint target) => Bytes(0x48, 0x8B, 0x0D).Rel(target);
    public X64 XorEdxEdx() => Bytes(0x31, 0xD2);
    public X64 MovRcxRsi() => Bytes(0x48, 0x89, 0xF1);
    /// <summary>cmp byte [rip+label], imm8</summary>
    public X64 CmpByte(string label, byte imm) => Bytes(0x80, 0x3D).Rel(label, 1).Bytes(imm);
    /// <summary>mov byte [rip+label], imm8</summary>
    public X64 MovByte(string label, byte imm) => Bytes(0xC6, 0x05).Rel(label, 1).Bytes(imm);
    /// <summary>sete byte [rip+label]</summary>
    public X64 SeteByte(string label) => Bytes(0x0F, 0x94, 0x05).Rel(label);
    public X64 SubRsp(int n) => Bytes(0x48, 0x81, 0xEC).I32(n);
    public X64 AddRsp(int n) => Bytes(0x48, 0x81, 0xC4).I32(n);
    /// <summary>movups [rsp+disp], xmmN / movups xmmN, [rsp+disp] (N &lt; 8)</summary>
    public X64 SaveXmm(int n, int disp) => Bytes(0x0F, 0x11).RspDisp(n, disp);
    public X64 LoadXmm(int n, int disp) => Bytes(0x0F, 0x10).RspDisp(n, disp);
    /// <summary>ModRM + SIB for [rsp+disp]: disp8 below 0x80 (it is signed), else disp32.</summary>
    X64 RspDisp(int reg, int disp) => disp < 0x80 ? Bytes((byte)(0x44 | reg << 3), 0x24, (byte)disp) : Bytes((byte)(0x84 | reg << 3), 0x24).I32(disp);
    /// <summary>mulss / addss xmm0, [rip+label]; movss [rip+label], xmm0</summary>
    public X64 MulssXmm0(string label) => Bytes(0xF3, 0x0F, 0x59, 0x05).Rel(label);
    public X64 AddssXmm0(string label) => Bytes(0xF3, 0x0F, 0x58, 0x05).Rel(label);
    public X64 StoreXmm0(string label) => Bytes(0xF3, 0x0F, 0x11, 0x05).Rel(label);
    public X64 StoreXmm0(uint target) => Bytes(0xF3, 0x0F, 0x11, 0x05).Rel(target);
    /// <summary>push / pop of the volatile general registers rax rcx rdx r8 r9 r10 r11 (7 pushes).</summary>
    public X64 PushVolatile() => Bytes(0x50, 0x51, 0x52, 0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53);
    public X64 PopVolatile() => Bytes(0x41, 0x5B, 0x41, 0x5A, 0x41, 0x59, 0x41, 0x58, 0x5A, 0x59, 0x58);

    public X64 Float(float f) => Bytes(BitConverter.GetBytes(f));
    public X64 Ascii(string s) => Bytes([.. System.Text.Encoding.UTF8.GetBytes(s), 0]);
    public X64 Align(int n) { while (_b.Count % n != 0) _b.Add(0xCC); return this; }

    public byte[] ToArray()
    {
        var r = _b.ToArray();
        foreach (var (field, end, label) in _fixups)
        {
            if (!_labels.TryGetValue(label, out var at)) throw new InvalidOperationException($"X64: label {label} is not defined.");
            BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(field), at - end);
        }
        return r;
    }

    public enum Reg { Rax = 0, Rcx = 1, Rdx = 2, R8 = 8, R9 = 9 }
}
