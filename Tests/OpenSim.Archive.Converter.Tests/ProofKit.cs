/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Inspection;
using OpenSim.Framework;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>Pieces the proofs of the merged archives share: the merged meshes rebuilt from the plan, the viewer's coordinates for a face, and the figures a part holds for its PBR and media faces.</summary>
internal static class ProofKit
{
    public static Primitive.TextureEntryFace FaceOf(Primitive.TextureEntry te, int i) =>
        ((te.FaceTextures != null && i < te.FaceTextures.Length) ? te.FaceTextures[i] : null) ?? te.DefaultTexture;

    public static string Temp(string stem) => Path.Combine(Path.GetTempPath(), stem + "-" + Guid.NewGuid().ToString("N") + ".oar");

    public static FaceTransform Of(Primitive.TextureEntryFace f) => new FaceTransform(f.RepeatU, f.RepeatV, f.OffsetU, f.OffsetV, f.Rotation);

    /// <summary>A source archive, the merged archive written from it, and both read back.</summary>
    public sealed class Written
    {
        public string Source, Output;
        public MergePlan Plan;
        public Dictionary<Guid, ParsedPart> Parsed;
        public Dictionary<Guid, ParsedPart> OutParsed;
        public List<MergeLinkset> Linksets;
    }

    public static Written WriteAndRead(string source)
    {
        Written w = new Written { Source = source, Output = Temp("merged") };
        MergeArchiveWriter.Write(w.Source, w.Output);
        w.Plan = ArchiveMergePlanner.Plan(w.Source, new MergeOptions(), null, out w.Parsed, out w.Linksets);
        ArchiveMergePlanner.Plan(w.Output, new MergeOptions(), null, out w.OutParsed);
        return w;
    }

    /// <summary>The members of a merged part as the writer builds them.</summary>
    public static List<MergeMember> Members(Written w, int linkset, MergedPartPlan mp)
    {
        Dictionary<Guid, MergePart> read = w.Linksets[linkset].Parts.ToDictionary(p => p.PartId);
        List<MergeMember> members = new List<MergeMember>();
        foreach (MemberPlan x in mp.Members)
        {
            ParsedPart pp = w.Parsed[x.PartId];
            MergePart part = read[x.PartId];
            members.Add(new MergeMember
            {
                PartId = x.PartId, IsRoot = part.IsRoot, Scale = new Vector3(pp.Scale.X, pp.Scale.Y, pp.Scale.Z),
                Offset = part.IsRoot ? Vector3.Zero : new Vector3(pp.OffsetPosition.X, pp.OffsetPosition.Y, pp.OffsetPosition.Z),
                Rotation = part.IsRoot ? Quaternion.Identity : Quaternion.Normalize(new Quaternion(pp.RotationOffset.X, pp.RotationOffset.Y, pp.RotationOffset.Z, pp.RotationOffset.W)),
                Faces = FaceBuilder.FromPrim(pp.Shape, out _, true), FaceInfo = part.Faces, Physics = PrimPhysics.Build(pp.Shape, null, null, pp.Scale, out _)
            });
        }
        return members;
    }

    /// <summary>The viewer's coordinates for one corner of a face: planar projection when the face is planar, then the transform (llface.cpp planarProjection and xform, written out again).</summary>
    public static void Viewer(Vector3 normal, Vector3 pos, Vector3 size, FaceTransform t, bool planar, float u0, float v0, out float s, out float tv)
    {
        float su = u0, tt = v0;
        if (planar)
        {
            Vector3 vec = pos * size;
            Vector3 binormal;
            float d = normal.X;
            if (d >= 0.5f || d <= -0.5f)
                binormal = d < 0 ? new Vector3(0, -1, 0) : new Vector3(0, 1, 0);
            else
                binormal = normal.Y > 0 ? new Vector3(-1, 0, 0) : new Vector3(1, 0, 0);
            Vector3 tangent = Vector3.Cross(binormal, normal);
            tt = -(Vector3.Dot(tangent, vec) * 2 - 0.5f);
            su = 1.0f + (Vector3.Dot(binormal, vec) * 2 - 0.5f);
        }
        float cosAng = (float)Math.Cos(t.Rotation), sinAng = (float)Math.Sin(t.Rotation);
        su -= 0.5f; tt -= 0.5f;
        float temp = su;
        su = su * cosAng + tt * sinAng;
        tt = -temp * sinAng + tt * cosAng;
        su *= t.RepeatU; tt *= t.RepeatV;
        su += t.OffsetU + 0.5f; tt += t.OffsetV + 0.5f;
        s = su; tv = tt;
    }

    /// <summary>The PBR material ids and overrides a part holds, by face, read the way the region reads them.</summary>
    public static (Dictionary<int, Guid> Materials, Dictionary<int, string> Overrides) PbrOf(ParsedPart pp)
    {
        PrimitiveBaseShape shape = new PrimitiveBaseShape();
        if (pp.ExtraParams.Length > 0)
            shape.ReadInExtraParamsBytes(pp.ExtraParams);
        if (pp.MaterialOverrides.Length > 0)
            shape.RenderMaterialsOvrFromRawBin(pp.MaterialOverrides);
        Dictionary<int, Guid> materials = new Dictionary<int, Guid>();
        Dictionary<int, string> overrides = new Dictionary<int, string>();
        if (shape.RenderMaterials?.entries != null)
            foreach (var e in shape.RenderMaterials.entries)
                materials[e.te_index] = e.id.Guid;
        if (shape.RenderMaterials?.overrides != null)
            foreach (var o in shape.RenderMaterials.overrides)
                if (!string.IsNullOrEmpty(o.data))
                    overrides[o.te_index] = o.data;
        return (materials, overrides);
    }
}
