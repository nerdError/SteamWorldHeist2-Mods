using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SWH2.Core;

/// <summary>
/// An XML/.de/.me file loaded for editing.
///
/// Saving patches the ORIGINAL text in place: only attribute values, text values and new
/// attributes that actually changed are rewritten, so the result differs from the game's file
/// byte-for-byte only where we changed something (formatting, comments, alignment all survive).
/// Structural edits (added/removed elements) fall back to a regular XmlWriter save.
/// </summary>
public sealed class XmlDoc
{
    readonly string _text;
    readonly int[] _lineStarts;
    readonly int _originalNodeCount;
    readonly bool _bom, _declaration, _crlf;

    XmlDoc(string path, string text, bool bom)
    {
        Path = path;
        _text = text;
        _bom = bom;
        _declaration = text.TrimStart().StartsWith("<?xml");
        _crlf = text.Contains("\r\n");
        Doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        _originalNodeCount = Doc.DescendantNodes().Count();

        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') starts.Add(i + 1);
        _lineStarts = [.. starts];

        Doc.Changed += (_, _) => Dirty = true;
    }

    public string Path { get; }
    public XDocument Doc { get; }
    public XElement Root => Doc.Root!;
    public bool Dirty { get; internal set; }

    /// <summary>True if the last Serialize() had to fall back to a full rewrite.</summary>
    public bool LastSaveWasFullRewrite { get; private set; }

    public static XmlDoc Parse(string path, byte[] data)
    {
        bool bom = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;
        return new XmlDoc(path, Encoding.UTF8.GetString(data, bom ? 3 : 0, data.Length - (bom ? 3 : 0)), bom);
    }

    public byte[] Serialize()
    {
        var patched = TryPatchOriginal();
        LastSaveWasFullRewrite = patched == null;
        if (patched != null) return Encode(patched);

        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(_bom),
            OmitXmlDeclaration = !_declaration,
            Indent = false,
            NewLineHandling = _crlf ? NewLineHandling.Replace : NewLineHandling.None,
            NewLineChars = _crlf ? "\r\n" : "\n",
        };
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, settings)) Doc.Save(w);
        return ms.ToArray();
    }

    byte[] Encode(string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        return _bom ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }

    // ---- in-place patching -------------------------------------------------------------

    int Offset(IXmlLineInfo li) => _lineStarts[li.LineNumber - 1] + li.LinePosition - 1;

    string? TryPatchOriginal()
    {
        // New definitions (root children without line info) are inserted as text before the closing root tag.
        var added = Doc.Root!.Elements().Where(e => !((IXmlLineInfo)e).HasLineInfo()).ToList();
        if (Doc.DescendantNodes().Count() - added.Sum(e => e.DescendantNodesAndSelf().Count()) != _originalNodeCount) return null;
        var edits = new List<(int Start, int Length, string Text)>();
        if (added.Count > 0)
        {
            if (AppendText(added) is not { } append) return null;
            edits.Add(append);
        }

        var addedSet = new HashSet<XElement>(added);
        foreach (var el in Doc.Root.Elements().Where(e => !addedSet.Contains(e)).SelectMany(e => e.DescendantsAndSelf()).Prepend(Doc.Root))
        {
            IXmlLineInfo eli = el;
            if (!eli.HasLineInfo()) return null;
            int elPos = Offset(eli);
            if (string.CompareOrdinal(_text, elPos, el.Name.LocalName, 0, el.Name.LocalName.Length) != 0) return null;

            foreach (var a in el.Attributes())
            {
                IXmlLineInfo ali = a;
                if (!ali.HasLineInfo())
                {
                    edits.Add((AttributeInsertPoint(elPos), 0, $" {a.Name.LocalName}=\"{EscapeAttr(a.Value, '"')}\""));
                    continue;
                }
                if (!TryAttrValueSpan(Offset(ali), out int vs, out int ve, out char quote)) return null;
                if (DecodeAttr(_text[vs..ve]) != a.Value) edits.Add((vs, ve - vs, EscapeAttr(a.Value, quote)));
            }

            foreach (var node in el.Nodes())
            {
                if (node is XCData || node is not XText t) continue;
                IXmlLineInfo tli = t;
                if (tli.HasLineInfo())
                {
                    int ts = Offset(tli), te = _text.IndexOf('<', ts);
                    if (te < 0) return null;
                    if (DecodeText(_text[ts..te]) != t.Value) edits.Add((ts, te - ts, EscapeText(t.Value)));
                }
                else
                {
                    // XElement.Value = "..." replaces the text node; supported for simple <A>text</A>.
                    if (el.Nodes().Count() != 1) return null;
                    int gt = StartTagEnd(elPos, out bool selfClosing, out _);
                    if (selfClosing) return null;
                    int cs = gt + 1, ce = _text.IndexOf('<', cs);
                    if (ce < 0 || string.CompareOrdinal(_text, ce, "</", 0, 2) != 0) return null;
                    edits.Add((cs, ce - cs, EscapeText(t.Value)));
                }
            }
        }

        if (edits.Count == 0) return _text;
        var sb = new StringBuilder(_text);
        foreach (var (start, length, text) in edits.OrderByDescending(e => e.Start))
            sb.Remove(start, length).Insert(start, text);
        return sb.ToString();
    }

    (int Start, int Length, string Text)? AppendText(List<XElement> added)
    {
        string nl = _crlf ? "\r\n" : "\n";
        int close = _text.LastIndexOf("</" + Doc.Root!.Name.LocalName, StringComparison.Ordinal);
        if (close < 0) return null;
        var sb = new StringBuilder();
        if (close > 0 && _text[close - 1] != '\n') sb.Append(nl);
        var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true, IndentChars = "\t", NewLineChars = nl, ConformanceLevel = ConformanceLevel.Fragment };
        foreach (var e in added)
        {
            var w = new StringWriter();
            using (var xw = XmlWriter.Create(w, settings)) e.WriteTo(xw);
            sb.Append(nl).Append('\t').Append(w.ToString().Replace(nl, nl + "\t")).Append(nl);
        }
        return (close, 0, sb.ToString());
    }

    /// <summary>Index of the start tag's closing '&gt;'; also reports where the last attribute ends.</summary>
    int StartTagEnd(int namePos, out bool selfClosing, out int afterLastAttr)
    {
        int i = namePos;
        while (i < _text.Length && !char.IsWhiteSpace(_text[i]) && _text[i] != '>' && _text[i] != '/') i++;
        afterLastAttr = i;
        for (; i < _text.Length; i++)
        {
            char ch = _text[i];
            if (ch == '"' || ch == '\'')
            {
                i = _text.IndexOf(ch, i + 1);
                afterLastAttr = i + 1;
            }
            else if (ch == '>')
            {
                selfClosing = _text[i - 1] == '/';
                return i;
            }
        }
        throw new FormatException($"Unterminated start tag in {Path}");
    }

    int AttributeInsertPoint(int elementNamePos)
    {
        StartTagEnd(elementNamePos, out _, out int afterLastAttr);
        return afterLastAttr;
    }

    bool TryAttrValueSpan(int namePos, out int start, out int end, out char quote)
    {
        start = end = 0;
        quote = '"';
        int eq = _text.IndexOf('=', namePos);
        if (eq < 0) return false;
        int q = eq + 1;
        while (q < _text.Length && char.IsWhiteSpace(_text[q])) q++;
        if (q >= _text.Length || (_text[q] != '"' && _text[q] != '\'')) return false;
        quote = _text[q];
        start = q + 1;
        end = _text.IndexOf(quote, start);
        return end >= 0;
    }

    static string DecodeAttr(string raw) =>
        WebUtility.HtmlDecode(raw.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' '));

    static string DecodeText(string raw) => WebUtility.HtmlDecode(raw.Replace("\r\n", "\n"));

    static string EscapeAttr(string s, char quote) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(quote == '"' ? "\"" : "'", quote == '"' ? "&quot;" : "&apos;");

    static string EscapeText(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

public static class XmlExtensions
{
    /// <summary>Definition elements: direct children of the root with a Name attribute.</summary>
    public static IEnumerable<XElement> Definitions(this XmlDoc doc, string? tag = null) =>
        doc.Root.Elements().Where(e => (tag == null || e.Name.LocalName == tag) && e.Attribute("Name") != null);

    public static string Name(this XElement e) => e.Attribute("Name")?.Value ?? "";

    public static bool IsAbstract(this XElement e) =>
        e.Attribute("Abstract")?.Value.ToLowerInvariant() is "1" or "true";

    public static bool AttrIsTrue(this XElement e, string attr) =>
        e.Attribute(attr)?.Value.ToLowerInvariant() is "1" or "true";
}
