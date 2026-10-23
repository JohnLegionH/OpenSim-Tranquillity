/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using System.Xml;
using OpenMetaverse;
using OpenSim.Framework.Serialization;

namespace OpenSim.Archive.Inspection;

/// <summary>Reading helpers shared by the parsers. XML from an archive is untrusted: no DTD, no resolver.</summary>
internal static class XmlRead
{
    /// <summary>
    /// Opens an XML body. The bytes are decoded as UTF-8 first (the savers write UTF-8 under a declaration
    /// that says utf-16, so the declared encoding must be ignored), and illegal characters are let through so
    /// they can be removed when printed.
    /// </summary>
    public static XmlReader Create(byte[] data)
    {
        // object assets saved by viewers and by some forks end with a NUL; the repository's readers drop it too
        int length = data.Length;
        while (length > 0 && data[length - 1] == 0)
            length--;
        string text = Encoding.UTF8.GetString(data, 0, length);
        if (text.Length > 0 && text[0] == (char)0xFEFF)
            text = text.Substring(1);
        text = DropIllegalXmlChars(text);

        XmlReaderSettings settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CheckCharacters = false,
            ConformanceLevel = ConformanceLevel.Auto
        };
        return XmlReader.Create(new StringReader(text), settings);
    }

    /// <summary>
    /// Characters XML 1.0 does not allow (most control characters). The reader refuses them even with character
    /// checking off, and they have no place in a name, so they are dropped before parsing.
    /// </summary>
    private static string DropIllegalXmlChars(string text)
    {
        int bad = 0;
        foreach (char c in text)
            if (IsIllegal(c)) bad++;
        if (bad == 0)
            return text;

        StringBuilder sb = new StringBuilder(text.Length - bad);
        foreach (char c in text)
            if (!IsIllegal(c))
                sb.Append(c);
        return sb.ToString();
    }

    private static bool IsIllegal(char c) => (c < ' ' && c != (char)9 && c != (char)10 && c != (char)13) || c == (char)0xFFFE || c == (char)0xFFFF;

    /// <summary>Text of the element the reader is on, all nested text joined; moves the reader past the element.</summary>
    public static string Text(XmlReader r)
    {
        if (r.IsEmptyElement)
        {
            r.Read();
            return string.Empty;
        }

        StringBuilder sb = new StringBuilder();
        using (XmlReader sub = r.ReadSubtree())
        {
            while (sub.Read())
            {
                if (sub.NodeType == XmlNodeType.Text || sub.NodeType == XmlNodeType.CDATA || sub.NodeType == XmlNodeType.SignificantWhitespace)
                    sb.Append(sub.Value);
            }
        }
        r.Read();
        return sb.ToString().Trim();
    }

    public static UUID Uuid(XmlReader r)
    {
        UUID.TryParse(Text(r), out UUID id);
        return id;
    }

    public static bool TryInt(string s, out int v) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

    /// <summary>Parses a mask or flags value the way the serializers store them (a signed or unsigned 32-bit number).</summary>
    public static uint? Mask(string s)
    {
        if (uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint u))
            return u;
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
            return unchecked((uint)i);
        return null;
    }

    public static int? Date(string s)
    {
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
            return i;
        if (uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint u))
            return unchecked((int)u);
        return null;
    }

    public static float Float(string s)
    {
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f);
        return f;
    }

    /// <summary>Reads the X, Y, Z and W children of the element the reader is on; an empty element reads as the identity.</summary>
    public static Quaternion Quaternion(XmlReader r)
    {
        float x = 0, y = 0, z = 0, w = 1;
        if (r.IsEmptyElement)
        {
            r.Read();
            return new Quaternion(0, 0, 0, 1);
        }

        int depth = r.Depth;
        r.Read();
        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
        {
            if (r.NodeType == XmlNodeType.Element)
            {
                string n = r.Name;
                string t = Text(r);
                if (n == "X" || n == "x") x = Float(t);
                else if (n == "Y" || n == "y") y = Float(t);
                else if (n == "Z" || n == "z") z = Float(t);
                else if (n == "W" || n == "w") w = Float(t);
            }
            else
                r.Read();
        }
        r.Read();
        return new Quaternion(x, y, z, w);
    }

    /// <summary>Reads X, Y, Z children of the element the reader is on.</summary>
    public static void Vector(XmlReader r, out float x, out float y, out float z)
    {
        x = y = z = 0;
        if (r.IsEmptyElement)
        {
            r.Read();
            return;
        }

        int depth = r.Depth;
        r.Read();
        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
        {
            if (r.NodeType == XmlNodeType.Element)
            {
                string n = r.Name;
                string t = Text(r);
                if (n == "X" || n == "x") x = Float(t);
                else if (n == "Y" || n == "y") y = Float(t);
                else if (n == "Z" || n == "z") z = Float(t);
            }
            else
                r.Read();
        }
        r.Read();
    }

    /// <summary>
    /// Splits creator data into home URL and name. Parts and prim items store "home;First Last", IAR items
    /// store "home/uuid;First Last" (OpenSim.Framework.Serialization ExternalRepresentationUtils.CalcCreatorData).
    /// </summary>
    public static void SplitCreatorData(string data, out string home, out string name, out UUID idInHome)
    {
        home = string.Empty;
        name = string.Empty;
        idInHome = UUID.Zero;
        if (string.IsNullOrEmpty(data))
            return;

        int semi = data.IndexOf(';');
        if (semi < 0)
        {
            name = data.Trim();
            return;
        }

        home = data.Substring(0, semi).Trim();
        name = data.Substring(semi + 1).Trim();

        int slash = home.LastIndexOf('/');
        if (slash > 0 && slash < home.Length - 1 && UUID.TryParse(home.Substring(slash + 1), out UUID id))
        {
            idInHome = id;
            home = home.Substring(0, slash);
        }
    }

    /// <summary>Name inside an OSPA string "ospa:n=First Last", or empty when the text is not one.</summary>
    public static string OspaName(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.StartsWith(OspResolver.OSPA_PREFIX, StringComparison.Ordinal))
            return string.Empty;

        foreach (string tuple in text.Substring(OspResolver.OSPA_PREFIX.Length).Split(OspResolver.OSPA_TUPLE_SEPARATOR_ARRAY))
        {
            int eq = tuple.IndexOf(OspResolver.OSPA_PAIR_SEPARATOR, StringComparison.Ordinal);
            if (eq < 0)
                continue;
            if (tuple.Substring(0, eq).Trim() == OspResolver.OSPA_NAME_KEY)
                return tuple.Substring(eq + 1).Trim();
        }
        return string.Empty;
    }

    /// <summary>Builds the creator reference from the fields an archive can hold.</summary>
    public static CreatorRef BuildCreator(string idText, string altText, string creatorData)
    {
        CreatorRef c = new CreatorRef { Data = creatorData ?? string.Empty };

        UUID id = UUID.Zero;
        bool haveId = !string.IsNullOrEmpty(idText) && UUID.TryParse(idText, out id);
        if (!haveId && !string.IsNullOrEmpty(altText))
        {
            haveId = UUID.TryParse(altText, out id);
            if (!haveId)
                c.Raw = altText;
        }
        else if (haveId && !string.IsNullOrEmpty(altText) && !UUID.TryParse(altText, out _))
            c.Raw = altText;
        else if (!haveId && !string.IsNullOrEmpty(idText))
            c.Raw = idText;

        SplitCreatorData(c.Data, out string home, out string name, out UUID idInHome);
        c.Home = home;
        c.Name = name;
        if (c.Name.Length == 0)
            c.Name = OspaName(c.Raw);
        if (!haveId && idInHome != UUID.Zero)
            id = idInHome;
        c.Id = id;
        return c;
    }
}
