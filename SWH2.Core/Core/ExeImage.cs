using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SWH2.Core;

/// <summary>
/// An in-memory copy of SteamWorld Heist 2.exe that mods patch. It always starts from the pristine original,
/// every patch checks the original bytes, and two mods touching the same bytes is reported as a conflict.
/// Mods write addresses as RVAs of the Steam build (the game is final, so hard-coded addresses are fine) and pass
/// each one through <see cref="Map"/>, which translates it for the build being patched (<see cref="GameBuild"/>).
/// </summary>
public sealed class ExeImage
{
    public const uint SectionCode = 0x60000020; // code | execute | read

    readonly byte[] _original;
    byte[] _data;
    readonly List<(int Start, int End, string Owner)> _patched = [];
    readonly List<(uint Start, uint End, string Owner)> _added = [];

    /// <param name="original">The pristine exe.</param>
    /// <param name="build">Its build; null = identify it by hash (Steam if unknown).</param>
    public ExeImage(byte[] original, GameBuild? build = null)
    {
        _original = original;
        _data = (byte[])original.Clone();
        Build = build ?? Identify(original) ?? GameBuild.Steam;
    }

    /// <summary>The known build an original exe is, or null (another version, or already patched).</summary>
    public static GameBuild? Identify(byte[] exe) => GameBuild.FromSha256(Hex.Sha256(exe));
    public static bool IsSupported(byte[] exe) => Identify(exe) != null;

    /// <summary>The game release being patched.</summary>
    public GameBuild Build { get; }

    /// <summary>
    /// The RVA in this exe of an address of the Steam build. Throws when this build has no known counterpart,
    /// so a mod fails cleanly instead of patching the wrong place.
    /// </summary>
    public uint Map(uint steamRva) => Build.TryMap(steamRva)
        ?? throw new NotSupportedException($"{CurrentOwner}: address {steamRva:X} (Steam build) has no known counterpart in the {Build.Name} exe.");

    /// <summary>Name shown in conflict messages; set by the loader before each mod runs.</summary>
    public string CurrentOwner { get; set; } = "?";
    public bool Modified => _patched.Count > 0 || _added.Count > 0;
    public byte[] ToArray() => (byte[])_data.Clone();
    public IEnumerable<string> Owners => _patched.Select(p => p.Owner).Concat(_added.Select(a => a.Owner)).Distinct();

    // ---- PE layout ----------------------------------------------------------------------

    int PeOffset => BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(0x3C));
    int FileHeader => PeOffset + 4;
    int OptionalHeader => FileHeader + 20;
    int SectionCount => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(FileHeader + 2));
    int SectionTable => OptionalHeader + BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(FileHeader + 16));
    uint SectionAlignment => U32(OptionalHeader + 32);
    uint FileAlignment => U32(OptionalHeader + 36);
    uint SizeOfHeaders => U32(OptionalHeader + 60);
    uint U32(int off) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off));

    record struct Section(string Name, uint VirtualSize, uint Rva, uint RawSize, uint RawPointer);

    IEnumerable<Section> Sections()
    {
        for (int i = 0; i < SectionCount; i++)
        {
            int h = SectionTable + i * 40;
            yield return new(Encoding.ASCII.GetString(_data, h, 8).TrimEnd('\0'), U32(h + 8), U32(h + 12), U32(h + 16), U32(h + 20));
        }
    }

    public bool HasSection(string name) => Sections().Any(s => s.Name == name);

    /// <summary>File offset of an RVA.</summary>
    public int OffsetOf(uint rva)
    {
        foreach (var s in Sections())
            if (rva >= s.Rva && rva < s.Rva + Math.Max(s.VirtualSize, s.RawSize))
            {
                if (rva - s.Rva >= s.RawSize) break;
                return checked((int)(rva - s.Rva + s.RawPointer));
            }
        throw new ArgumentOutOfRangeException(nameof(rva), $"RVA {rva:X} is not backed by file data.");
    }

    // ---- patching -------------------------------------------------------------------------

    /// <summary>Replaces bytes of the original exe. <paramref name="expected"/> must match the original exactly.</summary>
    public void Patch(uint rva, ReadOnlySpan<byte> expected, ReadOnlySpan<byte> replacement)
    {
        if (replacement.Length > expected.Length)
            throw new ArgumentException("Patch must not be longer than the checked original bytes.");
        int off = OffsetOf(rva);
        if (!_original.AsSpan(off, expected.Length).SequenceEqual(expected))
            throw new InvalidOperationException($"{CurrentOwner}: unexpected original bytes at RVA {rva:X} — unsupported game version?");
        foreach (var (start, end, owner) in _patched)
            if (off < end && start < off + expected.Length)
                throw new InvalidOperationException($"Exe conflict: {CurrentOwner} and {owner} both patch RVA {rva:X}.");
        replacement.CopyTo(_data.AsSpan(off));
        _patched.Add((off, off + expected.Length, CurrentOwner));
    }

    /// <summary>
    /// Appends a new zero-filled section (for code caves) and returns its RVA. Content goes in with <see cref="Write"/>.
    /// </summary>
    public uint AddSection(string name, int size, uint characteristics = SectionCode)
    {
        if (Encoding.ASCII.GetByteCount(name) > 8) throw new ArgumentException("Section names are at most 8 characters.");
        var last = Sections().Last();
        int header = SectionTable + SectionCount * 40;
        if (header + 40 > SizeOfHeaders || _data.Skip(header).Take(40).Any(b => b != 0))
            throw new InvalidOperationException("No room for another section header in the exe.");
        uint raw = (uint)_data.Length;
        if (raw % FileAlignment != 0 || raw != last.RawPointer + last.RawSize)
            throw new InvalidOperationException("The exe has trailing data after its last section; can't add a section.");

        uint vsize = Align((uint)size, SectionAlignment), rawSize = Align((uint)size, FileAlignment);
        uint rva = Align(last.Rva + last.VirtualSize, SectionAlignment);

        var h = _data.AsSpan(header, 40);
        Encoding.ASCII.GetBytes(name).CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], rva);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], rawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], raw);
        BinaryPrimitives.WriteUInt32LittleEndian(h[36..], characteristics);
        BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(FileHeader + 2), (ushort)(SectionCount + 1));
        BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(OptionalHeader + 56), rva + vsize); // SizeOfImage

        Array.Resize(ref _data, _data.Length + (int)rawSize);
        _added.Add((rva, rva + (uint)size, CurrentOwner));
        return rva;
    }

    /// <summary>Writes into a section this mod added with <see cref="AddSection"/>.</summary>
    public void Write(uint rva, ReadOnlySpan<byte> bytes)
    {
        uint end = rva + (uint)bytes.Length;
        if (!_added.Any(a => a.Owner == CurrentOwner && rva >= a.Start && end <= a.End))
            throw new InvalidOperationException($"{CurrentOwner}: Write is only allowed inside a section the mod added; use Patch for original code.");
        bytes.CopyTo(_data.AsSpan(OffsetOf(rva)));
    }

    /// <summary>
    /// Moves position-dependent machine code: <paramref name="rel32Offsets"/> are the offsets of rel32 fields
    /// (rip-relative operands, jmp/call targets) that point outside the blob and must keep their absolute target.
    /// </summary>
    public static byte[] Relocate(ReadOnlySpan<byte> code, uint assembledAt, uint newRva, IEnumerable<int> rel32Offsets)
    {
        var result = code.ToArray();
        foreach (var off in rel32Offsets)
        {
            long target = assembledAt + off + 4 + BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(off));
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(off), checked((int)(target - (newRva + off + 4))));
        }
        return result;
    }

    /// <summary>
    /// <see cref="Relocate(ReadOnlySpan{byte}, uint, uint, IEnumerable{int})"/> for code assembled against the Steam
    /// build: the rel32 targets (game code / data) are translated with <see cref="Map"/>.
    /// </summary>
    public byte[] RelocateMapped(ReadOnlySpan<byte> code, uint assembledAt, uint newRva, IEnumerable<int> rel32Offsets)
    {
        var result = code.ToArray();
        foreach (var off in rel32Offsets)
        {
            long target = assembledAt + off + 4 + BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(off));
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(off), checked((int)(Map(checked((uint)target)) - (long)(newRva + off + 4))));
        }
        return result;
    }

    /// <summary>6-byte <c>call qword ptr [rip+rel32]</c> at <paramref name="at"/> through the pointer at <paramref name="slot"/>.</summary>
    public static byte[] CallIndirect(uint at, uint slot)
    {
        var b = new byte[6];
        b[0] = 0xFF;
        b[1] = 0x15;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(2), checked((int)((long)slot - (at + 6))));
        return b;
    }

    /// <summary>5-byte <c>jmp rel32</c> from <paramref name="at"/> to <paramref name="target"/>.</summary>
    public static byte[] Jmp(uint at, uint target)
    {
        var b = new byte[5];
        b[0] = 0xE9;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(1), checked((int)((long)target - (at + 5))));
        return b;
    }

    static uint Align(uint v, uint a) => (v + a - 1) / a * a;
}
