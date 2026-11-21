using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SWH2.Core;

/// <summary>
/// Writes a copy of a zip (.pak) with some entries replaced or added.
/// Unchanged entries are copied as raw compressed bytes, so they stay bit-identical to the
/// game's originals; replaced and new entries are deflated (method 8, like every entry the game ships).
/// Supports plain (non-zip64) archives, which is what the game uses.
/// </summary>
public static class ZipRewriter
{
    const uint LocalSig = 0x04034b50, CentralSig = 0x02014b50, EndSig = 0x06054b50;

    sealed class Entry
    {
        public required byte[] Central;   // original central directory record (fixed part + name + extra + comment)
        public required string Name;
        public int NameLength => BinaryPrimitives.ReadUInt16LittleEndian(Central.AsSpan(28));
        public ushort Method => BinaryPrimitives.ReadUInt16LittleEndian(Central.AsSpan(10));
        public uint CompressedSize => BinaryPrimitives.ReadUInt32LittleEndian(Central.AsSpan(20));
        public uint LocalOffset => BinaryPrimitives.ReadUInt32LittleEndian(Central.AsSpan(42));
    }

    public static void Rewrite(string sourcePath, string destPath, IReadOnlyDictionary<string, byte[]> changes)
    {
        using var src = File.OpenRead(sourcePath);
        var entries = ReadCentralDirectory(src);
        var pending = changes.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        using var dst = File.Create(destPath);
        var central = new MemoryStream();
        int count = 0;

        foreach (var e in entries)
        {
            uint offset = (uint)dst.Position;
            var record = (byte[])e.Central.Clone();
            if (pending.Remove(e.Name, out var data))
            {
                var (packed, crc) = Deflate(data);
                Set16(record, 10, 8);
                Set32(record, 16, crc);
                Set32(record, 20, (uint)packed.Length);
                Set32(record, 24, (uint)data.Length);
                Set16(record, 8, (ushort)(Get16(record, 8) & ~0x8));
                WriteLocalHeader(dst, record, e.NameLength);
                dst.Write(packed);
            }
            else
            {
                Set16(record, 8, (ushort)(Get16(record, 8) & ~0x8)); // sizes are known: no data descriptor
                WriteLocalHeader(dst, record, e.NameLength);
                CopyRawData(src, dst, e);
            }
            Set32(record, 42, offset);
            central.Write(record);
            count++;
        }

        foreach (var (name, data) in pending.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            uint offset = (uint)dst.Position;
            var nameBytes = Encoding.UTF8.GetBytes(name);
            bool ascii = nameBytes.Length == name.Length;
            var (packed, crc) = Deflate(data);
            var (time, date) = DosTime(new DateTime(2025, 1, 1)); // fixed, so the same input always gives the same pak
            var record = new byte[46 + nameBytes.Length];
            Set32(record, 0, CentralSig);
            Set16(record, 4, 20);
            Set16(record, 6, 20);
            Set16(record, 8, (ushort)(ascii ? 0 : 0x800));
            Set16(record, 10, 8);
            Set16(record, 12, time);
            Set16(record, 14, date);
            Set32(record, 16, crc);
            Set32(record, 20, (uint)packed.Length);
            Set32(record, 24, (uint)data.Length);
            Set16(record, 28, (ushort)nameBytes.Length);
            Set32(record, 42, offset);
            nameBytes.CopyTo(record, 46);
            WriteLocalHeader(dst, record, nameBytes.Length);
            dst.Write(packed);
            central.Write(record);
            count++;
        }

        uint cdOffset = (uint)dst.Position;
        central.Position = 0;
        central.CopyTo(dst);
        var end = new byte[22];
        Set32(end, 0, EndSig);
        Set16(end, 8, (ushort)count);
        Set16(end, 10, (ushort)count);
        Set32(end, 12, (uint)central.Length);
        Set32(end, 16, cdOffset);
        dst.Write(end);
    }

    static List<Entry> ReadCentralDirectory(FileStream src)
    {
        int tail = (int)Math.Min(src.Length, 22 + 65535);
        var buf = new byte[tail];
        src.Position = src.Length - tail;
        src.ReadExactly(buf);
        int eocd = -1;
        for (int i = tail - 22; i >= 0; i--)
            if (Get32(buf, i) == EndSig) { eocd = i; break; }
        if (eocd < 0) throw new InvalidDataException("Not a zip file (end of central directory not found).");

        int total = Get16(buf, eocd + 10);
        uint cdSize = Get32(buf, eocd + 12), cdOffset = Get32(buf, eocd + 16);
        if (cdOffset == 0xFFFFFFFF || total == 0xFFFF) throw new NotSupportedException("Zip64 archives are not supported.");

        var cd = new byte[cdSize];
        src.Position = cdOffset;
        src.ReadExactly(cd);

        var list = new List<Entry>(total);
        int p = 0;
        for (int i = 0; i < total; i++)
        {
            if (Get32(cd, p) != CentralSig) throw new InvalidDataException("Corrupt central directory.");
            int n = Get16(cd, p + 28), x = Get16(cd, p + 30), c = Get16(cd, p + 32);
            int len = 46 + n + x + c;
            bool utf8 = (Get16(cd, p + 8) & 0x800) != 0;
            var name = (utf8 ? Encoding.UTF8 : Encoding.GetEncoding(28591)).GetString(cd, p + 46, n);
            list.Add(new Entry { Central = cd.AsSpan(p, len).ToArray(), Name = name });
            p += len;
        }
        return list;
    }

    static void WriteLocalHeader(Stream dst, byte[] central, int nameLength)
    {
        var h = new byte[30 + nameLength];
        Set32(h, 0, LocalSig);
        Array.Copy(central, 6, h, 4, 22); // version needed .. uncompressed size: central 6..27 -> local 4..25
        Set16(h, 26, (ushort)nameLength);
        Set16(h, 28, 0);
        Array.Copy(central, 46, h, 30, nameLength);
        dst.Write(h);
    }

    static void CopyRawData(FileStream src, Stream dst, Entry e)
    {
        var lh = new byte[30];
        src.Position = e.LocalOffset;
        src.ReadExactly(lh);
        if (Get32(lh, 0) != LocalSig) throw new InvalidDataException($"Corrupt local header for {e.Name}.");
        src.Position = e.LocalOffset + 30 + Get16(lh, 26) + Get16(lh, 28);
        var buffer = new byte[81920];
        long left = e.CompressedSize;
        while (left > 0)
        {
            int n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (n <= 0) throw new EndOfStreamException(e.Name);
            dst.Write(buffer, 0, n);
            left -= n;
        }
    }

    static (byte[] Data, uint Crc) Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return (ms.ToArray(), Crc32(data));
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    static (ushort Time, ushort Date) DosTime(DateTime t) =>
        ((ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2)),
         (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day));

    static ushort Get16(byte[] b, int i) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i));
    static uint Get32(byte[] b, int i) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i));
    static void Set16(byte[] b, int i, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i), v);
    static void Set32(byte[] b, int i, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i), v);
}
