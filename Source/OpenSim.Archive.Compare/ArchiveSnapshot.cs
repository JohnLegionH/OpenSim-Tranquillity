/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using OpenSim.Archive.Inspection;
using OpenSim.Framework.Serialization;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Archive.Compare;

/// <summary>The archive could not be read, or a plan could not be used. The comparison itself did not run.</summary>
public sealed class CompareException : Exception
{
    public CompareException(string message, Exception inner = null) : base(message, inner) { }
}

/// <summary>One prim inventory item of a part, keyed by its item id.</summary>
public sealed class ItemSnapshot
{
    public Guid Id;
    public string Name;
    public SortedDictionary<string, string> Fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// One part as the object file stores it, reduced to the values that are compared. Each value group maps a key to a text
/// value; a group with a single value uses the empty key.
/// </summary>
public sealed class PartSnapshot
{
    public Guid Id;
    public Guid RootId;
    public string Entry;
    public string BuildLabel;
    public string Name;
    public int LinkNum;
    public Dictionary<string, SortedDictionary<string, string>> Groups = new Dictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
    public Dictionary<Guid, ItemSnapshot> Items = new Dictionary<Guid, ItemSnapshot>();

    public string Single(string group) =>
        Groups.TryGetValue(group, out SortedDictionary<string, string> g) && g.TryGetValue(string.Empty, out string v) ? v : null;
}

/// <summary>
/// An OAR read without loading it: the parts by part id and a hash for every entry that is not an object file. Object files
/// are read for their parts and not kept.
/// </summary>
public sealed class ArchiveSnapshot
{
    public string Path { get; private set; }
    public Dictionary<string, string> Entries { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public Dictionary<Guid, PartSnapshot> Parts { get; } = new Dictionary<Guid, PartSnapshot>();
    public int ObjectFiles { get; private set; }

    public static ArchiveSnapshot Read(string path)
    {
        ArchiveSnapshot snap = new ArchiveSnapshot { Path = path };
        try
        {
            using FileStream fs = File.OpenRead(path);
            TarStreamReader tar = new TarStreamReader(Decompressed(fs));
            TarEntryHeader h;
            while ((h = tar.ReadNext()) != null)
            {
                string name = h.Name;
                for (int n = 2; snap.Entries.ContainsKey(name); n++)
                    name = h.Name + " [duplicate " + n + "]";

                if (h.IsDirectory)
                {
                    snap.Entries[name] = "(directory)";
                    continue;
                }

                byte[] body = tar.ReadBody();
                if (h.Name.StartsWith(ArchiveConstants.OBJECTS_PATH, StringComparison.Ordinal) && h.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    snap.ObjectFiles++;
                    snap.ReadObjectFile(h.Name, body);
                }
                else
                    snap.Entries[name] = Convert.ToHexString(SHA256.HashData(body));
            }
        }
        catch (Exception e) when (e is IOException || e is InvalidDataException || e is TarFormatException || e is ArchiveLimitException || e is UnauthorizedAccessException)
        {
            throw new CompareException("cannot read '" + path + "': " + e.Message, e);
        }
        return snap;
    }

    /// <summary>
    /// The same archive with each part and item keyed by what survives a load, for comparing an archive with the one a region
    /// saves after loading it (the loader gives every part and item a new id, and rewrites creator and owner fields that name
    /// accounts it does not know). A part is keyed by its build (the root part's name and position, to two decimals), its link
    /// number and its name; an item by its name, type and asset id. Creator, owner, last owner and group are left out of the
    /// comparison. Parts that share a key (identical prims stacked on one spot) are put in order of their own contents and
    /// told apart by their place in that order; the keys are returned in <paramref name="shared"/> so a report can say so.
    /// </summary>
    public ArchiveSnapshot ByPosition(out List<string> shared)
    {
        shared = new List<string>();
        ArchiveSnapshot copy = new ArchiveSnapshot { Path = Path, ObjectFiles = ObjectFiles };
        foreach (KeyValuePair<string, string> e in Entries)
            copy.Entries[e.Key] = e.Value;

        Dictionary<string, List<PartSnapshot>> byKey = new Dictionary<string, List<PartSnapshot>>(StringComparer.Ordinal);
        foreach (PartSnapshot p in Parts.Values)
        {
            string key = PositionKey(p);
            if (!byKey.TryGetValue(key, out List<PartSnapshot> list))
                byKey[key] = list = new List<PartSnapshot>();
            list.Add(p);
        }

        foreach (KeyValuePair<string, List<PartSnapshot>> group in byKey)
        for (int ordinal = 0; ordinal < group.Value.Count; ordinal++)
        {
            string key = group.Value.Count > 1 ? group.Key + "#" + (ordinal + 1) : group.Key;
            if (group.Value.Count > 1 && ordinal == 0)
            {
                shared.Add("'" + group.Key + "' names " + group.Value.Count + " parts");
                group.Value.Sort((x, y) => string.CompareOrdinal(Signature(x), Signature(y)));
            }
            PartSnapshot p = group.Value[ordinal];
            Parts.TryGetValue(p.RootId, out PartSnapshot root);
            root ??= p;
            PartSnapshot q = new PartSnapshot
            {
                Id = KeyGuid(key),
                RootId = KeyGuid(PositionKey(root)),
                Entry = p.Entry, BuildLabel = p.BuildLabel, Name = p.Name, LinkNum = p.LinkNum
            };
            foreach (KeyValuePair<string, SortedDictionary<string, string>> g in p.Groups)
                if (g.Key != "creator" && g.Key != "owner")
                    q.Groups[g.Key] = g.Key == "shape" ? WithoutDefaultsTheServerAdds(g.Value) : g.Value;
            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (ItemSnapshot it in p.Items.Values.OrderBy(i => i.Name, StringComparer.Ordinal).ThenBy(i => string.Join(";", i.Fields.Select(f => f.Value)), StringComparer.Ordinal))
            {
                it.Fields.TryGetValue("Type", out string type);
                it.Fields.TryGetValue("AssetID", out string asset);
                string ik = it.Name + "|" + type + "|" + asset;
                seen[ik] = seen.TryGetValue(ik, out int n) ? n + 1 : 1;
                ItemSnapshot copyItem = new ItemSnapshot { Id = KeyGuid(key + "#" + ik + "#" + seen[ik]), Name = it.Name };
                foreach (KeyValuePair<string, string> f in it.Fields)
                    if (f.Key != "CreatorID" && f.Key != "CreatorData" && f.Key != "OwnerID" && f.Key != "LastOwnerID" && f.Key != "GroupID")
                        copyItem.Fields[f.Key] = f.Value;
                q.Items[copyItem.Id] = copyItem;
            }
            copy.Parts[q.Id] = q;
        }
        return copy;
    }

    /// <summary>
    /// The server writes the flexible-path and light settings of every shape and an empty extra-parameter block as one zero
    /// byte; an archive from another writer may leave them out. They count only when the flexible path or light is on, and an empty block counts as none.
    /// </summary>
    private static SortedDictionary<string, string> WithoutDefaultsTheServerAdds(SortedDictionary<string, string> shape)
    {
        SortedDictionary<string, string> kept = new SortedDictionary<string, string>(shape, StringComparer.Ordinal);
        foreach (string prefix in new[] { "Flexi", "Light" })
            if (!shape.TryGetValue(prefix + "Entry", out string on) || !string.Equals(on, "true", StringComparison.OrdinalIgnoreCase))
                foreach (string key in shape.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                    kept.Remove(key);
        if (kept.TryGetValue("ExtraParams", out string extra) && (extra.Length == 0 || extra == "AA=="))
            kept.Remove("ExtraParams");
        return kept;
    }

    // A part's own values with no ids, to put identical-key parts in the same order in both archives.
    private static string Signature(PartSnapshot p) =>
        string.Join("|", p.Groups.Where(g => g.Key != "creator" && g.Key != "owner").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key + "=" + string.Join(";", g.Value.Select(v => v.Key + ":" + v.Value))))
        + "|" + string.Join("|", p.Items.Values.Select(i => i.Name + ":" + string.Join(",", i.Fields.Where(f => f.Key != "CreatorID" && f.Key != "CreatorData" && f.Key != "OwnerID" && f.Key != "LastOwnerID" && f.Key != "GroupID").Select(f => f.Value))).OrderBy(x => x, StringComparer.Ordinal));

    private string PositionKey(PartSnapshot p)
    {
        Parts.TryGetValue(p.RootId, out PartSnapshot root);
        root ??= p;
        return root.Name + "@" + RoundedPosition(root.Single("groupPosition")) + "|" + p.LinkNum.ToString(CultureInfo.InvariantCulture) + "|" + p.Name;
    }

    private static string RoundedPosition(string vector)
    {
        if (vector == null)
            return "?";
        string[] n = vector.Trim('<', '>').Split(',', StringSplitOptions.TrimEntries);
        return "<" + string.Join(", ", n.Select(x => float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? Math.Round(f, 2).ToString("0.##", CultureInfo.InvariantCulture) : x)) + ">";
    }

    private static Guid KeyGuid(string key) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>A gzip file starts 1f 8b; anything else is taken to be a plain tar.</summary>
    private static Stream Decompressed(Stream raw)
    {
        int b0 = raw.ReadByte(), b1 = raw.ReadByte();
        if (b0 < 0)
            throw new InvalidDataException("the archive is empty");
        raw.Seek(0, SeekOrigin.Begin);
        return b0 == 0x1f && b1 == 0x8b ? new GZipStream(new BufferedStream(raw, 1 << 16), CompressionMode.Decompress) : new BufferedStream(raw, 1 << 16);
    }

    private void ReadObjectFile(string entry, byte[] body)
    {
        XDocument doc;
        try
        {
            using XmlReader r = XmlReader.Create(new MemoryStream(body, false), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            doc = XDocument.Load(r);
        }
        catch (XmlException e)
        {
            throw new CompareException("object file '" + entry + "' is not well-formed XML: " + e.Message, e);
        }

        foreach (XElement part in doc.Descendants("SceneObjectPart"))
        {
            // The first part of a group element is its root; a bare part in a file is its own root.
            XElement group = part.Ancestors("SceneObjectGroup").FirstOrDefault();
            XElement root = group?.Descendants("SceneObjectPart").FirstOrDefault() ?? part;

            PartSnapshot p = ReadPart(entry, part);
            if (p == null)
                continue;
            PartSnapshot rootSnap = root == part ? p : ReadPartHeader(root);
            p.RootId = rootSnap.Id;
            p.BuildLabel = Label(rootSnap);
            if (root == part && group != null)
                p.Groups["scriptStates"] = One(ScriptStates(group));

            if (Parts.ContainsKey(p.Id))
                throw new CompareException("part " + p.Id + " is in more than one place in '" + Path + "'");
            Parts[p.Id] = p;
        }
    }

    private static string ScriptStates(XElement group)
    {
        StringBuilder sb = new StringBuilder();
        foreach (XElement s in group.Elements().Where(e => e.Name.LocalName == "GroupScriptStates" || e.Name.LocalName == "PhloxGroupScriptStates"))
            sb.Append(s.Name.LocalName).Append('=').Append(s.ToString(SaveOptions.DisableFormatting));
        return sb.Length == 0 ? "(none)" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string Label(PartSnapshot root)
    {
        string pos = root.Single("groupPosition") ?? "?";
        return root.Name + " @" + pos + " [" + root.Id.ToString("N").Substring(0, 8) + "]";
    }

    private static PartSnapshot ReadPartHeader(XElement part)
    {
        PartSnapshot p = new PartSnapshot { Id = PartId(part) ?? Guid.Empty, Name = Text(part, "Name") };
        p.Groups["groupPosition"] = One(Vector(part.Element("GroupPosition")));
        return p;
    }

    private static Guid? PartId(XElement part)
    {
        XElement u = part.Element("UUID");
        string text = u == null ? null : (u.Element("UUID") ?? u.Element("Guid"))?.Value.Trim();
        return Guid.TryParse(text, out Guid id) ? id : null;
    }

    private static string Text(XElement e, string child) => e.Element(child)?.Value.Trim() ?? string.Empty;

    private static SortedDictionary<string, string> One(string value) =>
        new SortedDictionary<string, string>(StringComparer.Ordinal) { [string.Empty] = value };

    /// <summary>Numbers are compared as numbers, so "1" and "1.0" are the same value; text that is not a number is compared as text.</summary>
    private static string Number(string text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f.ToString("R", CultureInfo.InvariantCulture) : text;

    private static string Vector(XElement v)
    {
        if (v == null)
            return "(absent)";
        return "<" + string.Join(", ", new[] { "X", "Y", "Z", "W" }.Where(n => v.Element(n) != null).Select(n => Number(v.Element(n).Value.Trim()))) + ">";
    }

    /// <summary>A leaf element's text, or its children as name:value pairs, so layout and nesting do not matter.</summary>
    private static string Flat(XElement e)
    {
        if (!e.HasElements)
            return e.Value.Trim();
        return string.Join(",", e.Elements().Select(c => c.Name.LocalName + ":" + Flat(c)));
    }

    private PartSnapshot ReadPart(string entry, XElement part)
    {
        Guid? id = PartId(part);
        if (id == null)
            return null;

        PartSnapshot p = new PartSnapshot { Id = id.Value, Entry = entry, Name = Text(part, "Name") };
        int.TryParse(Text(part, "LinkNum"), NumberStyles.Integer, CultureInfo.InvariantCulture, out p.LinkNum);

        p.Groups["name"] = One(p.Name);
        p.Groups["description"] = One(Text(part, "Description"));
        p.Groups["linkNum"] = One(p.LinkNum.ToString(CultureInfo.InvariantCulture));
        p.Groups["position"] = One(Vector(part.Element("OffsetPosition")));
        p.Groups["groupPosition"] = One(Vector(part.Element("GroupPosition")));
        p.Groups["rotation"] = One(Vector(part.Element("RotationOffset")));
        p.Groups["scale"] = One(Vector(part.Element("Scale")));

        SortedDictionary<string, string> shape = new SortedDictionary<string, string>(StringComparer.Ordinal);
        XElement shapeEl = part.Element("Shape");
        if (shapeEl != null)
            foreach (XElement c in shapeEl.Elements().Where(c => c.Name.LocalName != "TextureEntry"))
                shape[c.Name.LocalName] = Flat(c);
        p.Groups["shape"] = shape;
        p.Groups["textureEntry"] = One(Regex.Replace(shapeEl?.Element("TextureEntry")?.Value ?? string.Empty, @"\s+", string.Empty));
        p.Groups["faces"] = One(Faces(part));

        p.Groups["creator"] = Pick(part, "CreatorID", "CreatorData");
        p.Groups["owner"] = Pick(part, "OwnerID", "LastOwnerID", "GroupID");
        p.Groups["permissions"] = Pick(part, "BaseMask", "OwnerMask", "GroupMask", "EveryoneMask", "NextOwnerMask");
        p.Groups["physics"] = Pick(part, "PhysicsShapeType");

        XElement inventory = part.Element("TaskInventory");
        if (inventory != null)
            foreach (XElement item in inventory.Elements("TaskInventoryItem"))
            {
                // An id is stored as <ItemID><UUID>..</UUID></ItemID> (older archives: <Guid>).
                XElement idEl = item.Element("ItemID")?.Descendants().FirstOrDefault(d => !d.HasElements) ?? item.Element("ItemID");
                if (!Guid.TryParse(idEl?.Value.Trim(), out Guid iid))
                    iid = Guid.Empty;
                ItemSnapshot it = new ItemSnapshot { Id = iid, Name = Text(item, "Name") };
                foreach (string f in new[] { "Name", "Type", "InvType", "AssetID", "CreatorID", "CreatorData", "OwnerID", "LastOwnerID", "GroupID", "BasePermissions", "CurrentPermissions", "GroupPermissions", "EveryonePermissions", "NextPermissions", "Flags" })
                    if (item.Element(f) != null)
                        it.Fields[f] = Flat(item.Element(f));
                p.Items[iid] = it;
            }
        return p;
    }

    private static SortedDictionary<string, string> Pick(XElement part, params string[] names)
    {
        SortedDictionary<string, string> d = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string n in names)
            if (part.Element(n) != null)
                d[n] = Flat(part.Element(n));
        return d;
    }

    /// <summary>
    /// The number of faces, by the region server's own rule: the part is read with the server's object reader and
    /// SceneObjectPart.GetNumberOfSides is asked. No scene is needed.
    /// </summary>
    private static string Faces(XElement part)
    {
        try
        {
            using XmlReader r = XmlReader.Create(new StringReader(part.ToString(SaveOptions.DisableFormatting)), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            r.MoveToContent();
            return SceneObjectPart.FromXml(r).GetNumberOfSides().ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception e)
        {
            return "unreadable (" + e.GetType().Name + ")";
        }
    }
}
