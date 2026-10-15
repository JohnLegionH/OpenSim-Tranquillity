/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Xml;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Archive.Inspection;

internal sealed class RefInfo
{
    public UUID Id;
    public ReferenceKind Kind;
    public Certainty Certainty;
    public int? AssetType;
}

internal sealed class ParsedPart
{
    public PartRecord Part;
    public List<ItemRecord> Items = new List<ItemRecord>();
    public List<RefInfo> Refs = new List<RefInfo>();
    public List<UUID> ItemAssetRefs = new List<UUID>();
    public bool HasDynAttrs;
}

internal sealed class ParsedGroup
{
    public List<ParsedPart> Parts = new List<ParsedPart>();
    public UUID RootId;
    public string Name;
    public float X, Y, Z;
}

internal sealed class ParsedObject
{
    public string Format;
    public List<ParsedGroup> Groups = new List<ParsedGroup>();
    public int ScriptStates;
    public int PhloxScriptStates;
    public bool Coalesced;
}

/// <summary>
/// Reads the object XML forms (xml2 as written to an OAR, the original form used by object assets, and
/// coalesced objects) with one forward pass over an in-memory body. Only the fields the inspector reports are read.
/// </summary>
internal static class ObjectXmlParser
{
    private static readonly UUID InvalidCollisionSound = UUID.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    public static ParsedObject Parse(byte[] body)
    {
        ParsedObject result = new ParsedObject();
        using (XmlReader r = XmlRead.Create(body))
        {
            while (!r.EOF)
            {
                if (r.NodeType != XmlNodeType.Element)
                {
                    r.Read();
                    continue;
                }

                switch (r.Name)
                {
                    case "CoalescedObject":
                        result.Coalesced = true;
                        r.Read();
                        break;
                    case "SceneObjectGroup":
                        ReadGroup(r, result);
                        break;
                    case "SceneObjectPart":
                    {
                        ParsedGroup g = new ParsedGroup();
                        ParsedPart p = ReadPart(r);
                        p.Part.IsRoot = true;
                        g.Parts.Add(p);
                        Finish(g);
                        result.Groups.Add(g);
                        break;
                    }
                    default:
                        r.Skip();
                        break;
                }
            }
        }

        result.Format = result.Coalesced ? "coalesced" : "object";
        return result;
    }

    private static void ReadGroup(XmlReader r, ParsedObject result)
    {
        // r is on a SceneObjectGroup element; a coalesced file wraps one in another.
        ParsedGroup g = new ParsedGroup();
        bool rootSeen = false;

        if (r.IsEmptyElement)
        {
            r.Read();
            return;
        }

        int depth = r.Depth;
        r.Read();
        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
        {
            if (r.NodeType != XmlNodeType.Element)
            {
                r.Read();
                continue;
            }

            switch (r.Name)
            {
                case "SceneObjectGroup":
                    ReadGroup(r, result);
                    break;
                case "RootPart":
                    ReadPartsIn(r, g, true, ref rootSeen);
                    break;
                case "OtherParts":
                    ReadPartsIn(r, g, false, ref rootSeen);
                    break;
                case "SceneObjectPart":
                {
                    ParsedPart p = ReadPart(r);
                    p.Part.IsRoot = !rootSeen;
                    rootSeen = true;
                    g.Parts.Add(p);
                    break;
                }
                case "GroupScriptStates":
                    result.ScriptStates += CountChildren(r, "SavedScriptState");
                    break;
                case "PhloxGroupScriptStates":
                    result.PhloxScriptStates += Math.Max(1, CountChildren(r, "SavedScriptState"));
                    break;
                default:
                    r.Skip();
                    break;
            }
        }
        r.Read();

        if (g.Parts.Count > 0)
        {
            Finish(g);
            result.Groups.Add(g);
        }
    }

    private static int CountChildren(XmlReader r, string childName)
    {
        int count = 0;
        if (r.IsEmptyElement)
        {
            r.Read();
            return 0;
        }

        int depth = r.Depth;
        r.Read();
        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
        {
            if (r.NodeType == XmlNodeType.Element)
            {
                if (r.Name == childName)
                    count++;
                r.Skip();
            }
            else
                r.Read();
        }
        r.Read();
        return count;
    }

    private static void ReadPartsIn(XmlReader r, ParsedGroup g, bool rootContainer, ref bool rootSeen)
    {
        if (r.IsEmptyElement)
        {
            r.Read();
            return;
        }

        int depth = r.Depth;
        r.Read();
        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
        {
            if (r.NodeType != XmlNodeType.Element)
            {
                r.Read();
                continue;
            }

            if (r.Name == "SceneObjectPart")
            {
                ParsedPart p = ReadPart(r);
                p.Part.IsRoot = rootContainer && !rootSeen;
                if (p.Part.IsRoot)
                    rootSeen = true;
                g.Parts.Add(p);
            }
            else if (r.Name == "Part")
            {
                bool dummy = true;
                ReadPartsIn(r, g, false, ref dummy);
            }
            else
                r.Skip();
        }
        r.Read();
    }

    private static void Finish(ParsedGroup g)
    {
        ParsedPart root = g.Parts.FirstOrDefault(p => p.Part.IsRoot) ?? g.Parts[0];
        root.Part.IsRoot = true;
        g.RootId = root.Part.PartId;
        g.Name = root.Part.Name;
        g.X = root.Part.X;
        g.Y = root.Part.Y;
        g.Z = root.Part.Z;
        foreach (ParsedPart p in g.Parts)
        {
            p.Part.LinksetId = g.RootId;
            p.Part.LinksetName = g.Name;
            foreach (ItemRecord i in p.Items)
            {
                i.LinksetId = g.RootId;
                i.PartId = p.Part.PartId;
                i.PartName = p.Part.Name;
            }
        }
    }

    private static ParsedPart ReadPart(XmlReader r)
    {
        ParsedPart pp = new ParsedPart { Part = new PartRecord() };
        PartRecord part = pp.Part;
        string creatorId = null, creatorData = null;

        if (r.IsEmptyElement)
        {
            r.Read();
            part.Creator = XmlRead.BuildCreator(null, null, null);
            return pp;
        }

        int depth = r.Depth;
        r.Read();
        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
        {
            if (r.NodeType != XmlNodeType.Element)
            {
                r.Read();
                continue;
            }

            switch (r.Name)
            {
                case "CreatorID": creatorId = XmlRead.Text(r); break;
                case "CreatorData": creatorData = XmlRead.Text(r); break;
                case "UUID": part.PartId = XmlRead.Uuid(r); break;
                case "Name": part.Name = XmlRead.Text(r); break;
                case "LinkNum": { XmlRead.TryInt(XmlRead.Text(r), out int ln); part.LinkNum = ln; break; }
                case "GroupPosition": { XmlRead.Vector(r, out float gx, out float gy, out float gz); part.X = gx; part.Y = gy; part.Z = gz; break; }
                case "CreationDate": part.CreationDate = XmlRead.Date(XmlRead.Text(r)); break;
                case "GroupID": part.GroupId = XmlRead.Uuid(r); break;
                case "OwnerID": part.OwnerId = XmlRead.Uuid(r); break;
                case "LastOwnerID": part.LastOwnerId = XmlRead.Uuid(r); break;
                case "RezzerID": part.RezzerId = XmlRead.Uuid(r); break;
                case "BaseMask": part.Masks.Base = XmlRead.Mask(XmlRead.Text(r)); break;
                case "OwnerMask": part.Masks.Owner = XmlRead.Mask(XmlRead.Text(r)); break;
                case "GroupMask": part.Masks.Group = XmlRead.Mask(XmlRead.Text(r)); break;
                case "EveryoneMask": part.Masks.Everyone = XmlRead.Mask(XmlRead.Text(r)); break;
                case "NextOwnerMask": part.Masks.NextOwner = XmlRead.Mask(XmlRead.Text(r)); break;
                case "TaskInventory": ReadTaskInventory(r, pp); break;
                case "Shape": ReadShape(r, pp); break;
                case "CollisionSound":
                {
                    UUID id = XmlRead.Uuid(r);
                    if (id != UUID.Zero && id != InvalidCollisionSound)
                        pp.Refs.Add(new RefInfo { Id = id, Kind = ReferenceKind.CollisionSound, Certainty = Certainty.Certain, AssetType = (int)AssetType.Sound });
                    break;
                }
                case "SoundID":
                {
                    UUID id = XmlRead.Uuid(r);
                    if (id != UUID.Zero)
                        pp.Refs.Add(new RefInfo { Id = id, Kind = ReferenceKind.Sound, Certainty = Certainty.Certain, AssetType = (int)AssetType.Sound });
                    break;
                }
                case "ParticleSystem": ReadParticle(XmlRead.Text(r), pp); break;
                case "SOPAnims": ReadAnims(XmlRead.Text(r), pp); break;
                case "DynAttrs": pp.HasDynAttrs = true; r.Skip(); break;
                default: r.Skip(); break;
            }
        }
        r.Read();

        part.Creator = XmlRead.BuildCreator(creatorId, null, creatorData);
        foreach (ItemRecord i in pp.Items)
        {
            i.PartId = part.PartId;
            i.PartName = part.Name;
        }
        return pp;
    }

    private static void ReadTaskInventory(XmlReader r, ParsedPart pp)
    {
        if (r.IsEmptyElement)
        {
            r.Read();
            return;
        }

        int depth = r.Depth;
        r.Read();
        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
        {
            if (r.NodeType == XmlNodeType.Element && r.Name == "TaskInventoryItem")
                ReadTaskItem(r, pp);
            else if (r.NodeType == XmlNodeType.Element)
                r.Skip();
            else
                r.Read();
        }
        r.Read();
    }

    private static void ReadTaskItem(XmlReader r, ParsedPart pp)
    {
        ItemRecord item = new ItemRecord { Source = ItemSource.PrimInventory, HasLastOwner = true };
        string creatorId = null, creatorData = null;

        if (r.IsEmptyElement)
        {
            r.Read();
            return;
        }

        int depth = r.Depth;
        r.Read();
        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
        {
            if (r.NodeType != XmlNodeType.Element)
            {
                r.Read();
                continue;
            }

            switch (r.Name)
            {
                case "AssetID": item.AssetId = XmlRead.Uuid(r); break;
                case "BasePermissions": item.Masks.Base = XmlRead.Mask(XmlRead.Text(r)); break;
                case "CurrentPermissions": item.Masks.Owner = XmlRead.Mask(XmlRead.Text(r)); break;
                case "GroupPermissions": item.Masks.Group = XmlRead.Mask(XmlRead.Text(r)); break;
                case "EveryonePermissions": item.Masks.Everyone = XmlRead.Mask(XmlRead.Text(r)); break;
                case "NextPermissions": item.Masks.NextOwner = XmlRead.Mask(XmlRead.Text(r)); break;
                case "CreationDate": item.CreationDate = XmlRead.Date(XmlRead.Text(r)); break;
                case "CreatorID": creatorId = XmlRead.Text(r); break;
                case "CreatorData": creatorData = XmlRead.Text(r); break;
                case "GroupID": item.GroupId = XmlRead.Uuid(r); break;
                case "ItemID": item.ItemId = XmlRead.Uuid(r); break;
                case "LastOwnerID": item.LastOwnerId = XmlRead.Uuid(r); break;
                case "OwnerID": item.OwnerId = XmlRead.Uuid(r); break;
                case "Name": item.Name = XmlRead.Text(r); break;
                case "InvType": { if (XmlRead.TryInt(XmlRead.Text(r), out int it)) item.InvType = it; break; }
                case "Type": { if (XmlRead.TryInt(XmlRead.Text(r), out int t)) item.AssetType = t; break; }
                default: r.Skip(); break;
            }
        }
        r.Read();

        item.Creator = XmlRead.BuildCreator(creatorId, null, creatorData);
        pp.Items.Add(item);
        if (item.AssetId != UUID.Zero)
            pp.ItemAssetRefs.Add(item.AssetId);
    }

    private static void ReadShape(XmlReader r, ParsedPart pp)
    {
        if (r.IsEmptyElement)
        {
            r.Read();
            return;
        }

        int depth = r.Depth;
        r.Read();
        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
        {
            if (r.NodeType != XmlNodeType.Element)
            {
                r.Read();
                continue;
            }

            switch (r.Name)
            {
                case "TextureEntry": ReadTextureEntry(XmlRead.Text(r), pp); break;
                case "ExtraParams": ReadExtraParams(XmlRead.Text(r), pp); break;
                case "SculptTexture":
                {
                    UUID id = XmlRead.Uuid(r);
                    if (id != UUID.Zero)
                        pp.Refs.Add(new RefInfo { Id = id, Kind = ReferenceKind.Sculpt, Certainty = Certainty.Certain, AssetType = (int)AssetType.Texture });
                    break;
                }
                case "MatOvrd":
                {
                    byte[] data = TryBase64(XmlRead.Text(r));
                    if (data != null && data.Length > 36)
                        foreach (UUID id in TextScan.EmbeddedUuids(data))
                            pp.Refs.Add(new RefInfo { Id = id, Kind = ReferenceKind.MaterialOverride, Certainty = Certainty.Uncertain });
                    break;
                }
                default: r.Skip(); break;
            }
        }
        r.Read();
    }

    private static byte[] TryBase64(string s)
    {
        if (string.IsNullOrEmpty(s))
            return null;
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }

    private static void AddTexture(ParsedPart pp, Primitive.TextureEntryFace face)
    {
        UUID tid = face.TextureID;
        if (tid != UUID.Zero && !WellKnownAssets.IsWellKnown(tid))
            pp.Refs.Add(new RefInfo { Id = tid, Kind = ReferenceKind.FaceTexture, Certainty = Certainty.Certain, AssetType = (int)AssetType.Texture });
        if (face.MaterialID != UUID.Zero)
            pp.Refs.Add(new RefInfo { Id = face.MaterialID, Kind = ReferenceKind.Material, Certainty = Certainty.Certain });
    }

    private static void ReadTextureEntry(string text, ParsedPart pp)
    {
        byte[] data = TryBase64(text);
        if (data == null || data.Length <= 16)
            return;
        try
        {
            Primitive.TextureEntry te = new Primitive.TextureEntry(data, 0, data.Length);
            if (te.DefaultTexture != null)
                AddTexture(pp, te.DefaultTexture);
            if (te.FaceTextures != null)
                foreach (Primitive.TextureEntryFace face in te.FaceTextures)
                    if (face != null)
                        AddTexture(pp, face);
        }
        catch (Exception)
        {
            // a damaged texture entry names nothing; the part is still reported
        }
    }

    private static void ReadExtraParams(string text, ParsedPart pp)
    {
        byte[] data = TryBase64(text);
        if (data == null || data.Length <= 16)
            return;
        try
        {
            PrimitiveBaseShape shape = new PrimitiveBaseShape();
            shape.ReadInExtraParamsBytes(data);
            if (shape.ProjectionTextureUUID != UUID.Zero)
                pp.Refs.Add(new RefInfo { Id = shape.ProjectionTextureUUID, Kind = ReferenceKind.ProjectionTexture, Certainty = Certainty.Certain, AssetType = (int)AssetType.Texture });
            if (shape.RenderMaterials?.entries != null)
                foreach (var e in shape.RenderMaterials.entries)
                    if (e.id != UUID.Zero)
                        pp.Refs.Add(new RefInfo { Id = e.id, Kind = ReferenceKind.Material, Certainty = Certainty.Certain, AssetType = (int)AssetType.Material });
        }
        catch (Exception)
        {
        }
    }

    private static void ReadParticle(string text, ParsedPart pp)
    {
        byte[] data = TryBase64(text);
        if (data == null || data.Length <= 16)
            return;
        try
        {
            Primitive.ParticleSystem ps = new Primitive.ParticleSystem(data, 0);
            if (ps.Texture != UUID.Zero)
                pp.Refs.Add(new RefInfo { Id = ps.Texture, Kind = ReferenceKind.ParticleTexture, Certainty = Certainty.Certain, AssetType = (int)AssetType.Texture });
        }
        catch (Exception)
        {
        }
    }

    private static void ReadAnims(string text, ParsedPart pp)
    {
        byte[] data = TryBase64(text);
        if (data == null || data.Length < 18)
            return;
        try
        {
            int count = data[0] | (data[1] << 8);
            int pos = 2;
            while (--count >= 0 && pos + 17 <= data.Length)
            {
                UUID id = new UUID(data, pos);
                pos += 16;
                int len = data[pos++];
                pos += len;
                if (id == UUID.Zero)
                    break;
                if (!WellKnownAssets.IsWellKnown(id))
                    pp.Refs.Add(new RefInfo { Id = id, Kind = ReferenceKind.Animation, Certainty = Certainty.Certain, AssetType = (int)AssetType.Animation });
            }
        }
        catch (Exception)
        {
        }
    }
}
