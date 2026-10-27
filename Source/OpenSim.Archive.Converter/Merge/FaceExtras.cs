/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using System.Text.Json;
using System.Xml;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.Converter.Merge;

/// <summary>What a part holds for one face with a PBR (glTF) material: the material asset and the override, both by face number.</summary>
public sealed class PbrFace
{
    /// <summary>The material asset, or empty when the face has an override and no assignment.</summary>
    public Guid Material { get; set; }
    /// <summary>The override as the part stores it (JSON text), empty when the face has none.</summary>
    public string Override { get; set; } = "";
}

/// <summary>What the merge needs of a PBR material asset: whether its alpha mode is blend, and how much it scales its textures.</summary>
public sealed record PbrMaterialInfo(bool Blend, float TextureScale);

/// <summary>
/// Reads the per-face PBR assignments (the part's extra parameter block of type 0x80: a count, then a face number and a material id for each)
/// and the per-face overrides (the part's MatOvrd element: a count, then a face number, a 16-bit length and UTF-8 text for each), the way the
/// region reads them (PrimitiveBaseShape.ReadRenderMaterials and RenderMaterialsOvrFromRawBin). Anything the region would not read the same
/// way is reported as unreadable, and the part stays as it is.
/// </summary>
public static class PbrFaceData
{
    private const ushort RenderMaterialBlock = 0x80;

    public static Dictionary<int, PbrFace> Read(ParsedPart pp, int faceCount, out bool unreadable)
    {
        Dictionary<int, PbrFace> faces = new Dictionary<int, PbrFace>();
        unreadable = false;
        ReadAssignments(pp.ExtraParams, faces, faceCount, ref unreadable);
        ReadOverrides(pp.MaterialOverrides, faces, faceCount, ref unreadable);
        return faces;
    }

    private static void ReadAssignments(byte[] extra, Dictionary<int, PbrFace> faces, int faceCount, ref bool unreadable)
    {
        if (extra == null || extra.Length <= 1)
            return;
        int blocks = extra[0], at = 1;
        for (int k = 0; k < blocks; k++)
        {
            if (at + 6 > extra.Length)
                return; // a block list cut short is not a material problem: the rewrite stage refuses it on its own
            ushort type = BitConverter.ToUInt16(extra, at);
            long length = BitConverter.ToUInt32(extra, at + 2);
            if (at + 6 + length > extra.Length)
                return;
            if (type == RenderMaterialBlock)
            {
                int data = at + 6, count = length >= 1 ? extra[data] : 0;
                if (length < 1 + 17L * count)
                {
                    unreadable = true; // the region reads no entries from a block that is too short for its count
                    return;
                }
                for (int i = 0; i < count; i++)
                {
                    int face = extra[data + 1 + 17 * i];
                    Guid id = new UUID(extra, data + 2 + 17 * i).Guid;
                    if (face >= faceCount || faces.ContainsKey(face))
                    {
                        unreadable = true;
                        return;
                    }
                    faces[face] = new PbrFace { Material = id };
                }
            }
            at += (int)(6 + length);
        }
    }

    private static void ReadOverrides(byte[] data, Dictionary<int, PbrFace> faces, int faceCount, ref bool unreadable)
    {
        if (data == null || data.Length == 0)
            return;
        int count = data[0], at = 1;
        if (count > 128)
        {
            unreadable = true;
            return;
        }
        HashSet<int> seen = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            if (at + 3 > data.Length)
            {
                unreadable = true;
                return;
            }
            int face = data[at], length = data[at + 1] | (data[at + 2] << 8);
            at += 3;
            if (at + length > data.Length)
            {
                unreadable = true;
                return;
            }
            string text = Encoding.UTF8.GetString(data, at, length);
            at += length;
            // the region drops every override of a part when one starts like this (old test data); it would not carry them
            if (text.StartsWith("{\"asset", StringComparison.Ordinal) || face >= faceCount || !seen.Add(face))
            {
                unreadable = true;
                return;
            }
            if (text.Length == 0)
                continue;
            if (!faces.TryGetValue(face, out PbrFace f))
                faces[face] = f = new PbrFace();
            f.Override = text;
        }
    }

    /// <summary>The alpha mode an override sets (0 opaque, 1 blend, 2 mask) and the largest scale it gives any texture, from the override's JSON.</summary>
    public static (int? AlphaMode, float Scale) Override(string json)
    {
        if (json.Length == 0)
            return (null, 1f);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            int? mode = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("am", out JsonElement am) && am.TryGetInt32(out int m) ? m : null;
            float scale = 1f;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ti", out JsonElement ti) && ti.ValueKind == JsonValueKind.Array)
                foreach (JsonElement slot in ti.EnumerateArray())
                    if (slot.ValueKind == JsonValueKind.Object && slot.TryGetProperty("s", out JsonElement s) && s.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement v in s.EnumerateArray())
                            if (v.TryGetSingle(out float f))
                                scale = Math.Max(scale, Math.Abs(f));
            return (mode, scale);
        }
        catch (JsonException)
        {
            return (null, 1f);
        }
    }

    /// <summary>Reads a PBR material asset (the viewer's map around glTF JSON, or bare glTF JSON): its alpha mode is blend, and the largest scale its KHR_texture_transform entries give.</summary>
    public static PbrMaterialInfo ReadMaterial(byte[] asset)
    {
        byte[] text = PbrMaterialText.Unwrap(asset);
        if (text == null)
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            bool blend = false;
            float scale = 1f;
            if (doc.RootElement.TryGetProperty("materials", out JsonElement materials) && materials.ValueKind == JsonValueKind.Array && materials.GetArrayLength() > 0)
            {
                JsonElement first = materials[0];
                blend = first.TryGetProperty("alphaMode", out JsonElement mode) && mode.ValueKind == JsonValueKind.String && mode.GetString() == "BLEND";
                scale = MaxTransformScale(first, 1f);
            }
            return new PbrMaterialInfo(blend, scale);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static float MaxTransformScale(JsonElement e, float scale)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty p in e.EnumerateObject())
            {
                if (p.Name == "KHR_texture_transform" && p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("scale", out JsonElement s) && s.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement v in s.EnumerateArray())
                        if (v.TryGetSingle(out float f))
                            scale = Math.Max(scale, Math.Abs(f));
                scale = MaxTransformScale(p.Value, scale);
            }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (JsonElement v in e.EnumerateArray())
                scale = MaxTransformScale(v, scale);
        return scale;
    }

    /// <summary>
    /// True when the face is drawn through the viewer's alpha pool because of its PBR material: the effective alpha mode (the override's, else
    /// the material asset's) is blend. A material the archive does not hold, or that cannot be read, is taken as blending.
    /// </summary>
    public static bool Blends(PbrFace face, Dictionary<Guid, PbrMaterialInfo> materials)
    {
        (int? mode, _) = Override(face.Override);
        if (mode.HasValue)
            return mode.Value == 1;
        if (face.Material == Guid.Empty)
            return false;
        return !materials.TryGetValue(face.Material, out PbrMaterialInfo info) || info == null || info.Blend;
    }

    /// <summary>The largest scale the face's material or override gives its textures (1 when neither scales them).</summary>
    public static float Scale(PbrFace face, Dictionary<Guid, PbrMaterialInfo> materials)
    {
        float scale = Override(face.Override).Scale;
        if (face.Material != Guid.Empty && materials.TryGetValue(face.Material, out PbrMaterialInfo info) && info != null)
            scale = Math.Max(scale, info.TextureScale);
        return scale;
    }
}

/// <summary>Reads the media entries of a part (its Media element: an OSMedia document with one entry, or an empty place, for each face).</summary>
public static class MediaFaceData
{
    /// <summary>
    /// The entries by face number, null for a face with none. <paramref name="unreadable"/> is set when the text is not a media list, or holds an
    /// entry for a face the part does not have.
    /// </summary>
    public static List<MediaEntry> Read(ParsedPart pp, int faceCount, out bool unreadable)
    {
        unreadable = false;
        List<MediaEntry> entries = new List<MediaEntry>();
        if (string.IsNullOrWhiteSpace(pp.MediaXml))
            return entries;
        try
        {
            using XmlReader xr = XmlReader.Create(new StringReader(pp.MediaXml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            xr.MoveToContent();
            if (xr.LocalName != "OSMedia" || xr.GetAttribute("type") != "sl")
            {
                unreadable = true;
                return entries;
            }
            xr.ReadStartElement("OSMedia");
            if (OSDParser.DeserializeLLSDXml(xr.ReadInnerXml()) is not OSDArray array)
            {
                unreadable = true;
                return entries;
            }
            foreach (OSD item in array)
                entries.Add(item is OSDMap ? MediaEntry.FromOSD(item) : null);
        }
        catch (Exception e) when (e is XmlException || e is OSDException || e is FormatException || e is InvalidCastException)
        {
            unreadable = true;
            return new List<MediaEntry>();
        }
        for (int i = faceCount; i < entries.Count; i++)
            if (entries[i] != null)
            {
                unreadable = true;
                break;
            }
        return entries;
    }

    /// <summary>The entry as text that two equal entries share, empty for none.</summary>
    public static string Key(MediaEntry entry) => entry == null ? "" : OSDParser.SerializeJsonString(entry.GetOSD());
}
