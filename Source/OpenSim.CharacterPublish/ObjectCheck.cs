/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using OpenMetaverse;

namespace OpenSim.CharacterPublish;

/// <summary>What the structural check read from an attachment object's text.</summary>
public sealed record ObjectFacts(
    string Name, string Description, UUID SculptTexture, int SculptType, int AttachmentPoint,
    float ScaleX, float ScaleY, float ScaleZ, UUID Creator, UUID Owner,
    uint BaseMask, uint OwnerMask, uint GroupMask, uint EveryoneMask, uint NextOwnerMask);

/// <summary>
/// The check Robust makes on an object asset before storing it. It reads the text only, and needs no region code: the
/// text must be well-formed XML of one scene object with exactly one part, a mesh shape that names a mesh asset, no task
/// inventory (so no scripts), no scripted flag, no flexible path, light or particle system, a sane size and a valid
/// attachment point. The element names are those the region's serializer writes for a saved attachment
/// (SceneObjectSerializer.ToOriginalXmlFormat).
/// </summary>
public static class ObjectCheck
{
    /// <summary>An object asset longer than this is refused. A one-part rigged mesh object is about 5 KB.</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>The largest side a part may have, in metres (the viewer's and region's usual prim limit).</summary>
    public const float MaxSide = 64f;

    /// <summary>The highest attachment point the region accepts (AttachmentPoint.LastValid); a test keeps it equal.</summary>
    public const int MaxAttachmentPoint = 55;

    /// <summary>The mesh sculpt type (OpenMetaverse SculptType.Mesh).</summary>
    public const int SculptTypeMesh = 5;

    /// <summary>Returns the facts, or null after adding the reasons the object is refused to <paramref name="problems"/>.</summary>
    public static ObjectFacts? Check(byte[] asset, List<string> problems)
    {
        var before = problems.Count;
        if (asset.Length > MaxBytes) { problems.Add($"object asset is {asset.Length} bytes; the limit is {MaxBytes}"); return null; }
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var sr = new StreamReader(new MemoryStream(asset), new UTF8Encoding(false, true));
            using var xr = XmlReader.Create(sr, settings);
            doc = XDocument.Load(xr);
        }
        catch (Exception e) when (e is XmlException or DecoderFallbackException)
        {
            problems.Add("object asset is not well-formed XML: " + e.Message);
            return null;
        }

        var root = doc.Root;
        if (root?.Name.LocalName != "SceneObjectGroup") { problems.Add("object root element is not SceneObjectGroup"); return null; }
        var parts = root.Element("RootPart")?.Elements("SceneObjectPart").ToList() ?? new List<XElement>();
        if (parts.Count != 1) { problems.Add($"object must hold exactly one part; its RootPart holds {parts.Count}"); return null; }
        if (root.Element("OtherParts")?.HasElements == true) problems.Add("object has more than one part (OtherParts is not empty)");
        var part = parts[0];

        if (root.Descendants().Any(e => e.Name.LocalName is "TaskInventoryItem" or "ScriptState" or "ScriptStates")
            || part.Element("TaskInventory")?.HasElements == true)
            problems.Add("object has task inventory or script state; an attachment of a character holds no scripts");
        var flags = (string?)part.Element("Flags") ?? "";
        if (flags.Contains("Scripted", StringComparison.OrdinalIgnoreCase)) problems.Add($"object is flagged scripted (Flags {flags})");

        var shape = part.Element("Shape");
        if (shape == null) { problems.Add("object part has no Shape"); return null; }
        if (!Bool(shape, "SculptEntry")) problems.Add("shape is not a sculpt/mesh shape (SculptEntry is not true)");
        var sculptType = Int(shape, "SculptType");
        if (sculptType != SculptTypeMesh) problems.Add($"sculpt type {sculptType} is not mesh ({SculptTypeMesh})");
        var sculpt = Uuid(shape.Element("SculptTexture"));
        if (sculpt.IsZero()) problems.Add("shape names no mesh asset (SculptTexture is empty)");
        if (Bool(shape, "FlexiEntry")) problems.Add("shape is flexible");
        if (Bool(shape, "LightEntry")) problems.Add("shape carries a light");
        if (!string.IsNullOrWhiteSpace((string?)part.Element("ParticleSystem"))) problems.Add("part carries a particle system");

        var point = Int(shape, "State");
        if (point < 1 || point > MaxAttachmentPoint) problems.Add($"attachment point {point} is outside 1..{MaxAttachmentPoint}");

        var scale = part.Element("Scale");
        float sx = Float(scale, "X"), sy = Float(scale, "Y"), sz = Float(scale, "Z");
        if (!(sx > 0 && sy > 0 && sz > 0 && sx <= MaxSide && sy <= MaxSide && sz <= MaxSide))
            problems.Add($"size {sx} x {sy} x {sz} m is outside 0..{MaxSide} m on a side");

        var name = (string?)part.Element("Name") ?? "";
        if (name.Length == 0) problems.Add("object has no name");

        return problems.Count > before ? null : new ObjectFacts(
            name, (string?)part.Element("Description") ?? "", sculpt, sculptType, point, sx, sy, sz,
            Uuid(part.Element("CreatorID")), Uuid(part.Element("OwnerID")),
            Mask(part, "BaseMask"), Mask(part, "OwnerMask"), Mask(part, "GroupMask"), Mask(part, "EveryoneMask"), Mask(part, "NextOwnerMask"));
    }

    static bool Bool(XElement e, string name) => string.Equals((string?)e.Element(name), "true", StringComparison.OrdinalIgnoreCase);

    static int Int(XElement e, string name) =>
        int.TryParse((string?)e.Element(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1;

    static uint Mask(XElement e, string name) =>
        uint.TryParse((string?)e.Element(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    static float Float(XElement? e, string name) =>
        float.TryParse((string?)e?.Element(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : float.NaN;

    /// <summary>The region writes an id as &lt;Name&gt;&lt;UUID&gt;id&lt;/UUID&gt;&lt;/Name&gt;.</summary>
    static UUID Uuid(XElement? e) => UUID.TryParse((string?)e?.Element("UUID"), out var u) ? u : UUID.Zero;
}
