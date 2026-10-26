/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Archive.Rewrite;

/// <summary>
/// The element edits that turn a part into a mesh part with a given number of faces. The shape values are the ones
/// <see cref="PrimitiveBaseShape.CreateMesh"/> gives a mesh uploaded to the grid: the server counts a mesh part's faces
/// from its profile and path fields (SceneObjectPart.GetNumberOfSides), so the fields have to carry the count even
/// though the viewer takes the real faces from the mesh asset. The part's size, texture entry, light and every other
/// field outside the shape's geometry are left as they are.
/// </summary>
public static class MeshShape
{
    /// <summary>A mesh part can have at most this many faces (the viewer's limit for one mesh volume).</summary>
    public const int MaxFaces = 8;

    private const string Shape = "Shape/";

    /// <summary>
    /// The edits for one part. <paramref name="currentExtraParams"/> is the part's ExtraParams block as it is stored (null
    /// or empty when the part has none); its sculpt block is set and every other block is kept byte for byte.
    /// <paramref name="present"/> lists the element paths (such as "Shape/ProfileShape") the part's XML has; an optional
    /// element the part lacks is not edited, because the loader derives it from the ones that are edited. Null means every element is present.
    /// </summary>
    public static IReadOnlyList<ElementEdit> Edits(int faces, Guid meshAssetId, byte[] currentExtraParams, ISet<string> present = null)
    {
        if (faces < 1 || faces > MaxFaces)
            throw new ArgumentOutOfRangeException(nameof(faces), "a mesh part has 1 to " + MaxFaces + " faces");

        PrimitiveBaseShape s = PrimitiveBaseShape.CreateMesh(faces, new UUID(meshAssetId));
        List<ElementEdit> edits = new List<ElementEdit>();

        void Number(string name, long value) => edits.Add(ElementEdit.SetText(Shape + name, value.ToString(CultureInfo.InvariantCulture)));
        void Optional(string name, string text)
        {
            if (present == null || present.Contains(Shape + name))
                edits.Add(ElementEdit.SetText(Shape + name, text));
        }

        Number("ProfileCurve", s.ProfileCurve);
        Number("PathCurve", s.PathCurve);
        Number("PathBegin", s.PathBegin);
        Number("PathEnd", s.PathEnd);
        Number("PathRadiusOffset", s.PathRadiusOffset);
        Number("PathRevolutions", s.PathRevolutions);
        Number("PathScaleX", s.PathScaleX);
        Number("PathScaleY", s.PathScaleY);
        Number("PathShearX", s.PathShearX);
        Number("PathShearY", s.PathShearY);
        Number("PathSkew", s.PathSkew);
        Number("PathTaperX", s.PathTaperX);
        Number("PathTaperY", s.PathTaperY);
        Number("PathTwist", s.PathTwist);
        Number("PathTwistBegin", s.PathTwistBegin);
        Number("ProfileBegin", s.ProfileBegin);
        Number("ProfileEnd", s.ProfileEnd);
        Number("ProfileHollow", s.ProfileHollow);
        // The text forms of the profile and hollow shape override ProfileCurve when the loader reads them.
        Optional("ProfileShape", s.ProfileShape.ToString());
        Optional("HollowShape", s.HollowShape.ToString());

        // The mesh asset id: the element holds a UUID child, or a Guid child in the older spelling.
        string idPath = present != null && !present.Contains(Shape + "SculptTexture/UUID") && present.Contains(Shape + "SculptTexture/Guid")
            ? Shape + "SculptTexture/Guid" : Shape + "SculptTexture/UUID";
        edits.Add(ElementEdit.SetText(idPath, new UUID(meshAssetId).ToString()));
        Number("SculptType", s.SculptType);
        edits.Add(ElementEdit.SetText(Shape + "SculptEntry", "true"));
        // A part with no ExtraParams element is complete without it: the Sculpt* elements above are read after it and decide.
        if (present == null || present.Contains(Shape + "ExtraParams"))
            edits.Add(ElementEdit.SetText(Shape + "ExtraParams", Convert.ToBase64String(WithSculptBlock(currentExtraParams, meshAssetId, s.SculptType))));
        return edits;
    }

    private const ushort RenderMaterialBlockType = 0x80;

    /// <summary>
    /// A copy of the ExtraParams block with the PBR material assignment block (type 0x80: a count, then a face number and a material id
    /// for each) replaced by one that holds <paramref name="entries"/>, or dropped when there are none. Every other block is copied unchanged.
    /// </summary>
    public static byte[] WithRenderMaterials(byte[] extraParams, IReadOnlyList<(byte Face, Guid Id)> entries)
    {
        List<byte[]> kept = new List<byte[]>();
        if (extraParams != null && extraParams.Length > 1)
        {
            int count = extraParams[0], at = 1;
            for (int k = 0; k < count; k++)
            {
                if (at + 6 > extraParams.Length)
                    throw new InvalidDataException("the ExtraParams block is cut short");
                long length = BitConverter.ToUInt32(extraParams, at + 2);
                if (at + 6 + length > extraParams.Length)
                    throw new InvalidDataException("the ExtraParams block is cut short");
                int total = (int)(6 + length);
                if (BitConverter.ToUInt16(extraParams, at) != RenderMaterialBlockType)
                    kept.Add(extraParams.AsSpan(at, total).ToArray());
                at += total;
            }
        }
        if (entries.Count > 0)
        {
            byte[] block = new byte[6 + 1 + 17 * entries.Count];
            BitConverter.GetBytes(RenderMaterialBlockType).CopyTo(block, 0);
            BitConverter.GetBytes((uint)(1 + 17 * entries.Count)).CopyTo(block, 2);
            block[6] = (byte)entries.Count;
            for (int i = 0; i < entries.Count; i++)
            {
                block[7 + 17 * i] = entries[i].Face;
                new UUID(entries[i].Id).GetBytes().CopyTo(block, 8 + 17 * i);
            }
            kept.Add(block);
        }
        using MemoryStream output = new MemoryStream();
        output.WriteByte((byte)kept.Count);
        foreach (byte[] block in kept)
            output.Write(block);
        return output.ToArray();
    }

    private const byte SculptBlockType = 0x30;
    private const byte MeshBlockType = 0x60;   // an older spelling of the same block that the reader accepts
    private const int SculptBlockLength = 17;  // asset id (16 bytes) and sculpt type (1 byte)

    /// <summary>
    /// ExtraParams is a count byte followed by blocks of (type: 2 bytes, length: 4 bytes, data). Returns a copy with the
    /// sculpt block set to the given asset and type: replaced where the part has one, appended where it has none. All
    /// other blocks are copied unchanged.
    /// </summary>
    public static byte[] WithSculptBlock(byte[] extraParams, Guid meshAssetId, byte sculptType)
    {
        byte[] block = new byte[2 + 4 + SculptBlockLength];
        block[0] = SculptBlockType;
        block[2] = SculptBlockLength;
        new UUID(meshAssetId).GetBytes().CopyTo(block, 6);
        block[6 + 16] = sculptType;

        using MemoryStream output = new MemoryStream();
        if (extraParams == null || extraParams.Length <= 1)
        {
            output.WriteByte(1);
            output.Write(block);
            return output.ToArray();
        }

        int count = extraParams[0];
        int at = 1;
        bool replaced = false;
        List<(int start, int length)> kept = new List<(int, int)>();
        for (int k = 0; k < count; k++)
        {
            if (at + 6 > extraParams.Length)
                throw new InvalidDataException("the ExtraParams block is cut short");
            long length = BitConverter.ToUInt32(extraParams, at + 2);
            if (at + 6 + length > extraParams.Length)
                throw new InvalidDataException("the ExtraParams block is cut short");
            int total = (int)(6 + length);
            if (extraParams[at] == SculptBlockType || extraParams[at] == MeshBlockType)
            {
                if (!replaced)
                    kept.Add((-1, 0));
                replaced = true;
            }
            else
                kept.Add((at, total));
            at += total;
        }

        // Trailing bytes after the counted blocks are not part of the format; keep them where they are.
        // The placeholder in the list stands for the one sculpt block; a second sculpt block in the input is dropped.
        output.WriteByte((byte)(replaced ? kept.Count : kept.Count + 1));
        foreach ((int start, int length) in kept)
        {
            if (start < 0)
                output.Write(block);
            else
                output.Write(extraParams, start, length);
        }
        if (!replaced)
            output.Write(block);
        if (at < extraParams.Length)
            output.Write(extraParams, at, extraParams.Length - at);
        return output.ToArray();
    }
}
