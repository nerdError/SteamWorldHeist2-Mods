using System.IO.Compression;

namespace SWH2.Core;

/// <summary>
/// A virtual view of the game's files across Game.pak and Game2.pak, with an editable overlay.
/// Reads come from the original paks; writes are kept in memory until collected for installing.
/// Paths use the in-pak form, e.g. "Definitions/weapons.xml".
/// </summary>
public sealed class GameContent : IDisposable
{
    readonly List<ZipArchive> _archives = [];
    readonly Dictionary<string, (string Pak, ZipArchiveEntry Entry)> _index = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, byte[]> _changes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, XmlDoc> _xml = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<string>> _owners = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="paks">Pairs of (pak name, file to read it from).</param>
    public GameContent(IEnumerable<(string Pak, string File)> paks)
    {
        foreach (var (pak, file) in paks)
        {
            var zip = ZipFile.OpenRead(file);
            _archives.Add(zip);
            foreach (var e in zip.Entries)
                if (!e.FullName.EndsWith('/'))
                    _index.TryAdd(e.FullName, (pak, e));
        }
    }

    public static GameContent OpenOriginal(GameFiles paks) =>
        new(GameInstall.PakNames.Select(p => (p, paks.OriginalPakPath(p))));

    public IEnumerable<string> OriginalFiles => _index.Values.Select(v => v.Entry.FullName);
    public int ChangedFileCount => _changes.Count + _xml.Values.Count(d => d.Dirty);

    public static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    public bool ExistsInOriginal(string path) => _index.ContainsKey(Normalize(path));

    /// <summary>In the original paks or added by a mod.</summary>
    public bool Exists(string path) => ExistsInOriginal(path) || _changes.ContainsKey(Normalize(path));

    /// <summary>Which pak a file belongs to; new files go to Game.pak (where all definitions live).</summary>
    public string PakOf(string path) => _index.TryGetValue(Normalize(path), out var x) ? x.Pak : GameInstall.PakNames[0];

    string Canonical(string path)
    {
        path = Normalize(path);
        return _index.TryGetValue(path, out var x) ? x.Entry.FullName : path;
    }

    public byte[] ReadOriginal(string path)
    {
        if (!_index.TryGetValue(Normalize(path), out var x)) throw new FileNotFoundException($"Not in game paks: {path}");
        using var s = x.Entry.Open();
        using var ms = new MemoryStream((int)x.Entry.Length);
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Current content: edited version if any, else original.</summary>
    public byte[] Read(string path)
    {
        var p = Canonical(path);
        if (_xml.TryGetValue(p, out var doc) && doc.Dirty) return doc.Serialize();
        return _changes.TryGetValue(p, out var data) ? data : ReadOriginal(p);
    }

    public string ReadText(string path) => System.Text.Encoding.UTF8.GetString(Read(path)).TrimStart('﻿');

    /// <summary>Name of the mod currently making changes; used to report which mods touched which files.</summary>
    public string? CurrentOwner { get; set; }

    /// <summary>Called when a mod replaces a whole file another mod had already changed (its changes are lost).</summary>
    public event Action<string, IReadOnlyList<string>>? Overridden;

    /// <summary>Files changed by more than one mod, with the mods in the order they ran.</summary>
    public IEnumerable<(string Path, IReadOnlyList<string> Owners)> SharedFiles =>
        _owners.Where(o => o.Value.Count > 1).Select(o => (o.Key, (IReadOnlyList<string>)o.Value));

    void Touch(string canonical)
    {
        if (CurrentOwner == null) return;
        var list = _owners.TryGetValue(canonical, out var l) ? l : _owners[canonical] = [];
        if (!list.Contains(CurrentOwner)) list.Add(CurrentOwner);
    }

    /// <summary>Replaces a file. Writing bytes identical to the original drops the change.</summary>
    public void Write(string path, byte[] data)
    {
        var p = Canonical(path);
        if (CurrentOwner != null && _owners.TryGetValue(p, out var before) && before.Any(o => o != CurrentOwner)
            && !Read(p).AsSpan().SequenceEqual(data))
            Overridden?.Invoke(p, before.Where(o => o != CurrentOwner).ToList());
        _xml.Remove(p);
        SetChange(p, data);
        Touch(p);
    }

    public void WriteText(string path, string text, bool bom = false) =>
        Write(path, [.. (bom ? (byte[])[0xEF, 0xBB, 0xBF] : []), .. System.Text.Encoding.UTF8.GetBytes(text)]);

    void SetChange(string canonical, byte[] data)
    {
        if (_index.ContainsKey(canonical) && ReadOriginal(canonical).AsSpan().SequenceEqual(data))
            _changes.Remove(canonical);
        else
            _changes[canonical] = data;
    }

    /// <summary>Loads (once) an XML file for editing. Changes are picked up automatically.</summary>
    public XmlDoc Xml(string path)
    {
        var p = Canonical(path);
        if (!_xml.TryGetValue(p, out var doc))
        {
            _xml[p] = doc = XmlDoc.Parse(p, Read(p));
            doc.Doc.Changed += (_, _) => Touch(p);
        }
        return doc;
    }

    /// <summary>All files matching a directory + wildcard pattern, e.g. ("Definitions", "missions*.de").</summary>
    public IEnumerable<string> Find(string dir, string pattern)
    {
        var rx = new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(Normalize(dir).TrimEnd('/') + "/" + pattern)
                .Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return OriginalFiles.Where(f => rx.IsMatch(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>All edits grouped by the pak they belong to.</summary>
    public Dictionary<string, Dictionary<string, byte[]>> CollectChanges()
    {
        foreach (var doc in _xml.Values.Where(d => d.Dirty))
        {
            SetChange(doc.Path, doc.Serialize());
            doc.Dirty = false;
        }
        var result = GameInstall.PakNames.ToDictionary(p => p, _ => new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase));
        foreach (var (path, data) in _changes)
            result[PakOf(path)][path] = data;
        return result;
    }

    public void Dispose()
    {
        foreach (var a in _archives) a.Dispose();
    }
}
