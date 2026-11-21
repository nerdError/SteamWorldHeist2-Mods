using System.Security.Cryptography;
using System.Text;

namespace SWH2.Compat;

// .NET Framework 4.8 lacks some newer BCL APIs; these stand-ins keep the code close to modern .NET.
// Imported everywhere (also in mods) via <Using Include="SWH2.Compat" /> in Directory.Build.props.

public static class Hex
{
    public static string ToHex(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 2);
        foreach (var b in data) sb.Append(b.ToString("X2"));
        return sb.ToString();
    }

    public static byte[] Parse(string hex)
    {
        if (hex.Length % 2 != 0) throw new FormatException("Odd number of hex digits.");
        var result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++) result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return result;
    }

    public static string Sha256(byte[] data)
    {
        using var sha = SHA256.Create();
        return ToHex(sha.ComputeHash(data));
    }

    public static string Sha256(Stream data)
    {
        using var sha = SHA256.Create();
        return ToHex(sha.ComputeHash(data));
    }
}

public static class PathEx
{
    /// <summary>Path.GetRelativePath for a file inside <paramref name="baseDir"/>.</summary>
    public static string GetRelativePath(string baseDir, string path)
    {
        var b = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var p = Path.GetFullPath(path);
        return p.StartsWith(b, StringComparison.OrdinalIgnoreCase) ? p.Substring(b.Length) : p;
    }

    public static void MoveOverwrite(string from, string to)
    {
        if (File.Exists(to)) File.Delete(to);
        File.Move(from, to);
    }
}

public static class CompatExtensions
{
    public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> kv, out TKey key, out TValue value)
    {
        key = kv.Key;
        value = kv.Value;
    }

    public static bool TryAdd<TKey, TValue>(this Dictionary<TKey, TValue> d, TKey key, TValue value)
    {
        if (d.ContainsKey(key)) return false;
        d.Add(key, value);
        return true;
    }

    public static TValue? GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> d, TKey key) =>
        d.TryGetValue(key, out var v) ? v : default;

    public static TValue? GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> d, TKey key) =>
        d.TryGetValue(key, out var v) ? v : default;

    public static TValue GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> d, TKey key, TValue fallback) =>
        d.TryGetValue(key, out var v) ? v : fallback;

    public static bool Remove<TKey, TValue>(this Dictionary<TKey, TValue> d, TKey key, out TValue value) =>
        d.TryGetValue(key, out value!) && d.Remove(key);

    public static bool Contains(this string s, char c) => s.IndexOf(c) >= 0;
    public static bool StartsWith(this string s, char c) => s.Length > 0 && s[0] == c;
    public static bool EndsWith(this string s, char c) => s.Length > 0 && s[s.Length - 1] == c;
    public static string[] SplitTrim(this string s, char separator) =>
        s.Split(separator).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();

    public static IEnumerable<T> TakeLast<T>(this IEnumerable<T> source, int count)
    {
        var list = source.ToList();
        return list.Skip(Math.Max(0, list.Count - count));
    }

    public static IOrderedEnumerable<T> Order<T>(this IEnumerable<T> source, IComparer<T>? comparer = null) =>
        source.OrderBy(x => x, comparer ?? Comparer<T>.Default);

    public static void Write(this Stream s, ReadOnlySpan<byte> data) => s.Write(data.ToArray(), 0, data.Length);

    public static void ReadExactly(this Stream s, Span<byte> buffer)
    {
        var tmp = new byte[buffer.Length];
        int read = 0;
        while (read < tmp.Length)
        {
            int n = s.Read(tmp, read, tmp.Length - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
        tmp.CopyTo(buffer);
    }

    public static string GetString(this Encoding e, ReadOnlySpan<byte> bytes) => e.GetString(bytes.ToArray());

    public static float ReadSingleLittleEndian(ReadOnlySpan<byte> b) => BitConverter.ToSingle(b.ToArray(), 0);
}

public static class Rng
{
    static readonly Random Shared = new();
    public static int Next(int min, int max) { lock (Shared) return Shared.Next(min, max); }
}
