/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using System.Xml;

namespace OpenSim.Archive.Rewrite;

/// <summary>
/// Finds parts inside an object file and edits chosen elements in place. The file is never re-serialised: the
/// positions of the elements are read from the text, and only the bytes of the element contents that an edit
/// replaces are changed. Every other byte of the file, including whitespace, quoting, element order and elements
/// this code does not know, stays as it was.
/// </summary>
internal static class ObjectXml
{
    private sealed class Span
    {
        public string Name;
        public int TagStart;      // offset of '<' of the start tag
        public int TagEnd;        // offset just after '>' of the start tag
        public bool SelfClosing;
        public int InnerEnd;      // offset of '<' of the end tag (TagEnd when self-closing)
    }

    private sealed class PartSpans
    {
        public Guid Id;
        public Dictionary<string, List<Span>> ByPath = new Dictionary<string, List<Span>>();
    }

    /// <summary>The bytes decoded to text; the BOM, if any, is set aside so offsets match the text that is parsed.</summary>
    internal sealed class Decoded
    {
        public byte[] Bom = Array.Empty<byte>();
        public string Text;

        public byte[] Encode(string text)
        {
            byte[] body = new UTF8Encoding(false).GetBytes(text);
            byte[] all = new byte[Bom.Length + body.Length];
            Buffer.BlockCopy(Bom, 0, all, 0, Bom.Length);
            Buffer.BlockCopy(body, 0, all, Bom.Length, body.Length);
            return all;
        }
    }

    public static Decoded Decode(byte[] data)
    {
        Decoded d = new Decoded();
        int skip = 0;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            d.Bom = new byte[] { 0xEF, 0xBB, 0xBF };
            skip = 3;
        }
        d.Text = new UTF8Encoding(false, true).GetString(data, skip, data.Length - skip);
        // The text must encode back to exactly the original bytes, or an edit would change more than it names.
        if (!d.Encode(d.Text).AsSpan().SequenceEqual(data))
            throw new InvalidDataException("the object file does not survive a text round trip, so it cannot be patched in place");
        return d;
    }

    /// <summary>The ids of the parts in the file that are in <paramref name="wanted"/>.</summary>
    public static HashSet<Guid> FindParts(Decoded d, ISet<Guid> wanted)
    {
        HashSet<Guid> found = new HashSet<Guid>();
        foreach (PartSpans p in Scan(d.Text, wanted))
            found.Add(p.Id);
        return found;
    }

    /// <summary>Applies the edits and returns the new bytes of the file.</summary>
    public static byte[] Apply(Decoded d, IReadOnlyDictionary<Guid, PartPatch> patches, string entryName)
    {
        string text = d.Text;
        List<(int start, int end, string with)> cuts = new List<(int, int, string)>();
        HashSet<Guid> done = new HashSet<Guid>();

        foreach (PartSpans part in Scan(text, new HashSet<Guid>(patches.Keys)))
        {
            if (!done.Add(part.Id))
                throw new InvalidDataException("part " + part.Id + " appears twice in '" + entryName + "'");
            foreach (ElementEdit edit in patches[part.Id].Edits)
            {
                if (!part.ByPath.TryGetValue(edit.Path, out List<Span> spans) || spans.Count == 0)
                    throw new InvalidDataException("part " + part.Id + " in '" + entryName + "' has no element '" + edit.Path + "'");
                if (spans.Count > 1)
                    throw new InvalidDataException("part " + part.Id + " in '" + entryName + "' has " + spans.Count + " elements '" + edit.Path + "'; the path is ambiguous");

                Span s = spans[0];
                string content = edit.IsMarkup ? edit.Value : Escape(edit.Value);
                if (!s.SelfClosing)
                    cuts.Add((s.TagEnd, s.InnerEnd, content));
                else
                {
                    // <Name /> becomes <Name>content</Name>: cut the "/>" (and blanks before it) and write the new tail.
                    int cutFrom = s.TagEnd - 2;
                    while (cutFrom > s.TagStart && char.IsWhiteSpace(text[cutFrom - 1]))
                        cutFrom--;
                    cuts.Add((cutFrom, s.TagEnd, ">" + content + "</" + s.Name + ">"));
                }
            }
        }

        foreach (Guid id in patches.Keys)
            if (!done.Contains(id))
                throw new InvalidDataException("part " + id + " is not in '" + entryName + "'");

        cuts.Sort((a, b) => a.start.CompareTo(b.start));
        for (int i = 1; i < cuts.Count; i++)
            if (cuts[i].start < cuts[i - 1].end)
                throw new InvalidDataException("two edits to '" + entryName + "' overlap; an element and one inside it were both edited");

        StringBuilder sb = new StringBuilder(text.Length + 64);
        int at = 0;
        foreach ((int start, int end, string with) in cuts)
        {
            sb.Append(text, at, start - at);
            sb.Append(with);
            at = end;
        }
        sb.Append(text, at, text.Length - at);
        string result = sb.ToString();

        // The result must still be well formed.
        using (XmlReader r = XmlReader.Create(new StringReader(result), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore }))
            while (r.Read()) { }

        return d.Encode(result);
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static bool TryPartId(PartSpans part, string text, out Guid id)
    {
        id = Guid.Empty;
        foreach (string path in new[] { "UUID/UUID", "UUID/Guid" })
            if (part.ByPath.TryGetValue(path, out List<Span> spans) && spans.Count == 1
                && Guid.TryParse(text.Substring(spans[0].TagEnd, spans[0].InnerEnd - spans[0].TagEnd).Trim(), out id))
                return true;
        return false;
    }

    /// <summary>Reads the file and returns the elements inside each SceneObjectPart whose id is wanted.</summary>
    private static List<PartSpans> Scan(string text, ISet<Guid> wanted)
    {
        // Line starts as the XML reader counts them: \n, \r\n and a lone \r each end a line.
        List<int> lineStart = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n' || (c == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
                lineStart.Add(i + 1);
        }

        List<PartSpans> result = new List<PartSpans>();
        PartSpans current = null;       // the part being read
        int partDepth = -1;
        List<string> pathStack = new List<string>();
        Stack<Span> open = new Stack<Span>();

        using XmlReader reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        IXmlLineInfo li = (IXmlLineInfo)reader;
        int Offset() => lineStart[li.LineNumber - 1] + li.LinePosition - 1;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                int nameAt = Offset();   // the position is the first character of the element name
                Span span = new Span { Name = reader.Name, TagStart = nameAt - 1 };

                // Find the '>' that closes the start tag, skipping quoted attribute values.
                int p = nameAt;
                char quote = '\0';
                for (; p < text.Length; p++)
                {
                    char c = text[p];
                    if (quote != '\0') { if (c == quote) quote = '\0'; }
                    else if (c == '"' || c == '\'') quote = c;
                    else if (c == '>') break;
                }
                span.TagEnd = p + 1;
                span.SelfClosing = reader.IsEmptyElement;
                span.InnerEnd = span.TagEnd;

                if (current == null)
                {
                    if (reader.Name == "SceneObjectPart")
                    {
                        current = new PartSpans();
                        partDepth = reader.Depth;
                        pathStack.Clear();
                        if (span.SelfClosing)
                            current = null;   // an empty part has no id to match
                    }
                }
                else
                {
                    pathStack.Add(reader.Name);
                    string path = string.Join("/", pathStack);
                    if (!current.ByPath.TryGetValue(path, out List<Span> list))
                        current.ByPath[path] = list = new List<Span>();
                    list.Add(span);
                    if (span.SelfClosing)
                        pathStack.RemoveAt(pathStack.Count - 1);
                }

                if (!span.SelfClosing)
                    open.Push(span);
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                Span span = open.Pop();
                span.InnerEnd = Offset() - 2;   // the position is just after "</"

                if (current != null)
                {
                    if (reader.Depth == partDepth)
                    {
                        // The part's own id is the UUID element's child: <UUID><UUID>..</UUID></UUID> as this server writes it,
                        // <UUID><Guid>..</Guid></UUID> in older archives.
                        if (TryPartId(current, text, out Guid id))
                        {
                            current.Id = id;
                            if (wanted.Contains(id))
                                result.Add(current);
                        }
                        current = null;
                        pathStack.Clear();
                    }
                    else
                        pathStack.RemoveAt(pathStack.Count - 1);
                }
            }
        }
        return result;
    }
}
