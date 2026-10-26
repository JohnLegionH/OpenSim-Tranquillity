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

/// <summary>The raw shape fields of a part, as the object XML stores them.</summary>
public sealed class ShapeData
{
    public int ProfileCurve;
    public int PathCurve;
    public int PathBegin, PathEnd;
    public int PathScaleX, PathScaleY;
    public int PathShearX, PathShearY;
    public int PathTwist, PathTwistBegin;
    public int PathRadiusOffset, PathRevolutions;
    public int PathTaperX, PathTaperY;
    public int PathSkew;
    public int ProfileBegin, ProfileEnd, ProfileHollow;
    public int PCode;
    /// <summary>The shape state byte; for a built-in tree it is the species.</summary>
    public int State;
    public int SculptType;
    public UUID SculptTexture;
    public bool SculptEntry;
    /// <summary>The text forms of the profile and hollow shape, when the object XML carries them (they override ProfileCurve, as the loader does).</summary>
    public string ProfileShapeName, HollowShapeName;

    /// <summary>A copy that can be changed without touching this one.</summary>
    public ShapeData Clone() => (ShapeData)MemberwiseClone();
}

/// <summary>One part as read from an object body: the record the inspector reports plus the fields a geometry export needs.</summary>
public sealed class ParsedPart
{
    public PartRecord Part;
    public List<ItemRecord> Items = new List<ItemRecord>();
    internal List<RefInfo> Refs = new List<RefInfo>();
    internal List<UUID> ItemAssetRefs = new List<UUID>();
    internal bool HasDynAttrs;
    public string Description = string.Empty;
    /// <summary>Position inside the group: relative to the root part for a child, the world position for the root.</summary>
    public Vector3 OffsetPosition;
    /// <summary>Rotation inside the group: relative to the root part for a child, the world rotation for the root.</summary>
    public Quaternion RotationOffset = Quaternion.Identity;
    public Vector3 Scale = Vector3.One;
    public ShapeData Shape;
    /// <summary>The texture entry block as stored (empty when absent).</summary>
    public byte[] TextureEntry = Array.Empty<byte>();
    /// <summary>The shape's extra parameters block as stored (flexible path, light, sculpt, render materials; empty when absent).</summary>
    public byte[] ExtraParams = Array.Empty<byte>();
    /// <summary>The shape's per-face material override block as stored (empty when absent).</summary>
    public byte[] MaterialOverrides = Array.Empty<byte>();
    /// <summary>The part's texture animation block as stored (empty when absent).</summary>
    public byte[] TextureAnimation = Array.Empty<byte>();
    /// <summary>True when the part's Flags include Physics (the part is a physical object).</summary>
    public bool PhysicsFlag;
    /// <summary>The part's PhysicsShapeType element (0 prim, 1 none, 2 convex), -1 when the element is absent, -2 when it is not a number.</summary>
    public int PhysicsShapeType = -1;
    /// <summary>True when the part carries a Vehicle element (parameters set by llSetVehicle* calls).</summary>
    public bool HasVehicleParams;
    /// <summary>The vehicle type stored in the Vehicle element (0 when none or unreadable).</summary>
    public int VehicleType;
    /// <summary>True when the shape is flexible: the extra parameters hold a flexible-path block or FlexiEntry is true.</summary>
    public bool Flexible;
    /// <summary>True when the part is a point light (the LightEntry element, or a light block in the extra parameters).</summary>
    public bool Light;
    /// <summary>True when the part has a sit target (a non-zero sit target position).</summary>
    public bool SitTarget;
    /// <summary>True when the part has hover text.</summary>
    public bool HoverText;
    /// <summary>The text shown on the pie menu's sit entry, and on its touch entry; empty when the part has none.</summary>
    public string SitText = string.Empty, TouchText = string.Empty;
    /// <summary>The part's click action, object sale type and sale price as stored (0 when absent).</summary>
    public int ClickAction, SaleType, SalePrice;
    /// <summary>The colour of the hover text packed as alpha, red, green, blue bytes; opaque black, the region's default, when absent.</summary>
    public uint HoverTextColor = 0xFF000000;
    /// <summary>True when any of the part's five pay prices is not the default of -2.</summary>
    public bool PayPriceChanged;
    /// <summary>The part's surface material (wood, metal ...) as stored; 0 when absent.</summary>
    public int SurfaceMaterial;
    /// <summary>True when the part has a particle system.</summary>
    public bool HasParticles;
    /// <summary>True when the part has an attached sound.</summary>
    public bool HasSound;
    /// <summary>True when the part's Flags include Phantom.</summary>
    public bool Phantom;
    /// <summary>True when the part is a volume detector (the region stores that as the JointLP2P flag, SceneObjectPart.primFlagVolumeDetect).</summary>
    public bool VolumeDetect;
}

/// <summary>One linkset as read from an object body.</summary>
public sealed class ParsedGroup
{
    public List<ParsedPart> Parts = new List<ParsedPart>();
    public UUID RootId;
    public string Name;
    public float X, Y, Z;
}

/// <summary>The linksets found in one object body.</summary>
public sealed class ParsedObject
{
    public string Format;
    public List<ParsedGroup> Groups = new List<ParsedGroup>();
    public int ScriptStates;
    public int PhloxScriptStates;
    public bool Coalesced;
    /// <summary>SHA-256 of the object file's bytes (hex), set only when <see cref="InspectorOptions.HashObjectBodies"/> is on.</summary>
    public string BodyHash;
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
                case "Description": pp.Description = XmlRead.Text(r); break;
                case "OffsetPosition": { XmlRead.Vector(r, out float ox, out float oy, out float oz); pp.OffsetPosition = new Vector3(ox, oy, oz); break; }
                case "RotationOffset": pp.RotationOffset = XmlRead.Quaternion(r); break;
                case "Scale": { XmlRead.Vector(r, out float sx, out float sy, out float sz); pp.Scale = new Vector3(sx, sy, sz); break; }
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
                        pp.HasSound = true;
                    if (id != UUID.Zero)
                        pp.Refs.Add(new RefInfo { Id = id, Kind = ReferenceKind.Sound, Certainty = Certainty.Certain, AssetType = (int)AssetType.Sound });
                    break;
                }
                case "ParticleSystem":
                {
                    string particleText = XmlRead.Text(r);
                    pp.HasParticles = (TryBase64(particleText)?.Length ?? 0) > 0;
                    ReadParticle(particleText, pp);
                    break;
                }
                case "SitTargetPosition":
                case "SitTargetPositionLL":
                {
                    XmlRead.Vector(r, out float sx, out float sy, out float sz);
                    if (sx != 0 || sy != 0 || sz != 0)
                        pp.SitTarget = true;
                    break;
                }
                case "Text": pp.HoverText = !string.IsNullOrEmpty(XmlRead.Text(r)); break;
                case "SitName": pp.SitText = XmlRead.Text(r); break;
                case "TouchName": pp.TouchText = XmlRead.Text(r); break;
                case "ClickAction": { XmlRead.TryInt(XmlRead.Text(r), out int ca); pp.ClickAction = ca; break; }
                case "ObjectSaleType": { XmlRead.TryInt(XmlRead.Text(r), out int st); pp.SaleType = st; break; }
                case "SalePrice": { XmlRead.TryInt(XmlRead.Text(r), out int sp); pp.SalePrice = sp; break; }
                case "PayPrice0":
                case "PayPrice1":
                case "PayPrice2":
                case "PayPrice3":
                case "PayPrice4":
                    if (!XmlRead.TryInt(XmlRead.Text(r), out int pay) || pay != -2)
                        pp.PayPriceChanged = true;
                    break;
                case "Color": pp.HoverTextColor = ReadPackedColor(r); break;
                case "Material": { XmlRead.TryInt(XmlRead.Text(r), out int sm); pp.SurfaceMaterial = sm; break; }
                case "SOPAnims": ReadAnims(XmlRead.Text(r), pp); break;
                case "Flags":
                {
                    string flagsText = XmlRead.Text(r);
                    pp.PhysicsFlag = HasPhysicsFlag(flagsText);
                    pp.Phantom = HasFlag(flagsText, "Phantom", (int)PrimFlags.Phantom);
                    pp.VolumeDetect = HasFlag(flagsText, "JointLP2P", (int)PrimFlags.JointLP2P);
                    break;
                }
                case "PhysicsShapeType": pp.PhysicsShapeType = int.TryParse(XmlRead.Text(r)?.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int pst) ? pst : -2; break;
                case "Vehicle": ReadVehicle(r, pp); break;
                case "TextureAnimation": pp.TextureAnimation = TryBase64(XmlRead.Text(r)) ?? Array.Empty<byte>(); break;
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

    /// <summary>Reads the part's Color element (R, G, B, A bytes) as the region packs it: alpha, red, green, blue.</summary>
    private static uint ReadPackedColor(XmlReader r)
    {
        int red = 0, green = 0, blue = 0, alpha = 0;
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
                string name = r.Name;
                XmlRead.TryInt(XmlRead.Text(r), out int value);
                if (name == "R") red = value;
                else if (name == "G") green = value;
                else if (name == "B") blue = value;
                else if (name == "A") alpha = value;
            }
            else
                r.Read();
        }
        r.Read();
        return unchecked((uint)(((alpha & 0xFF) << 24) | ((red & 0xFF) << 16) | ((green & 0xFF) << 8) | (blue & 0xFF)));
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
        ShapeData sd = pp.Shape = new ShapeData();
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
                case "PCode": { if (XmlRead.TryInt(XmlRead.Text(r), out int pc)) { pp.Part.PCode = pc; sd.PCode = pc; } break; }
                case "State": sd.State = ShapeInt(r); break;
                case "ProfileCurve": sd.ProfileCurve = ShapeInt(r); break;
                case "ProfileShape": sd.ProfileShapeName = XmlRead.Text(r); break;
                case "HollowShape": sd.HollowShapeName = XmlRead.Text(r); break;
                case "PathCurve": sd.PathCurve = ShapeInt(r); break;
                case "PathBegin": sd.PathBegin = ShapeInt(r); break;
                case "PathEnd": sd.PathEnd = ShapeInt(r); break;
                case "PathScaleX": sd.PathScaleX = ShapeInt(r); break;
                case "PathScaleY": sd.PathScaleY = ShapeInt(r); break;
                case "PathShearX": sd.PathShearX = ShapeInt(r); break;
                case "PathShearY": sd.PathShearY = ShapeInt(r); break;
                case "PathTwist": sd.PathTwist = ShapeInt(r); break;
                case "PathTwistBegin": sd.PathTwistBegin = ShapeInt(r); break;
                case "PathRadiusOffset": sd.PathRadiusOffset = ShapeInt(r); break;
                case "PathRevolutions": sd.PathRevolutions = ShapeInt(r); break;
                case "PathTaperX": sd.PathTaperX = ShapeInt(r); break;
                case "PathTaperY": sd.PathTaperY = ShapeInt(r); break;
                case "PathSkew": sd.PathSkew = ShapeInt(r); break;
                case "ProfileBegin": sd.ProfileBegin = ShapeInt(r); break;
                case "ProfileEnd": sd.ProfileEnd = ShapeInt(r); break;
                case "ProfileHollow": sd.ProfileHollow = ShapeInt(r); break;
                case "SculptType": sd.SculptType = ShapeInt(r); break;
                case "FlexiEntry": if (string.Equals(XmlRead.Text(r), "true", StringComparison.OrdinalIgnoreCase)) pp.Flexible = true; break;
                case "LightEntry": if (string.Equals(XmlRead.Text(r), "true", StringComparison.OrdinalIgnoreCase)) pp.Light = true; break;
                case "SculptEntry": sd.SculptEntry = string.Equals(XmlRead.Text(r), "true", StringComparison.OrdinalIgnoreCase); break;
                case "TextureEntry":
                {
                    string teText = XmlRead.Text(r);
                    pp.TextureEntry = TryBase64(teText) ?? Array.Empty<byte>();
                    ReadTextureEntry(teText, pp);
                    break;
                }
                case "ExtraParams":
                {
                    string extraText = XmlRead.Text(r);
                    pp.ExtraParams = TryBase64(extraText) ?? Array.Empty<byte>();
                    ReadExtraParams(extraText, pp);
                    break;
                }
                case "SculptTexture":
                {
                    UUID id = XmlRead.Uuid(r);
                    sd.SculptTexture = id;
                    if (id != UUID.Zero)
                        pp.Refs.Add(new RefInfo { Id = id, Kind = ReferenceKind.Sculpt, Certainty = Certainty.Certain, AssetType = (int)AssetType.Texture });
                    break;
                }
                case "MatOvrd":
                {
                    byte[] data = TryBase64(XmlRead.Text(r));
                    pp.MaterialOverrides = data ?? Array.Empty<byte>();
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

    private static int ShapeInt(XmlReader r)
    {
        XmlRead.TryInt(XmlRead.Text(r), out int v);
        return v;
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

    /// <summary>The Flags element is the PrimFlags enumeration as names separated by spaces, or a number.</summary>
    private static bool HasFlag(string text, string word, int bit)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (XmlRead.TryInt(text.Trim(), out int bits))
            return (bits & bit) != 0;
        foreach (string w in text.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(w, word, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool HasPhysicsFlag(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (XmlRead.TryInt(text.Trim(), out int bits))
            return (bits & (int)PrimFlags.Physics) != 0;
        foreach (string word in text.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(word, "Physics", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static void ReadVehicle(XmlReader r, ParsedPart pp)
    {
        pp.HasVehicleParams = true;
        if (r.IsEmptyElement)
        {
            r.Read();
            return;
        }
        int depth = r.Depth;
        r.Read();
        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
        {
            if (r.NodeType == XmlNodeType.Element && r.Name == "TYPE")
            {
                if (XmlRead.TryInt(XmlRead.Text(r), out int type))
                    pp.VehicleType = type;
            }
            else if (r.NodeType == XmlNodeType.Element)
                r.Skip();
            else
                r.Read();
        }
        r.Read();
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
            if (shape.FlexiEntry)
                pp.Flexible = true;
            if (shape.LightEntry)
                pp.Light = true;
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
