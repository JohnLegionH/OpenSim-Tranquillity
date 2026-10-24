/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Globalization;
using System.Text;
using OpenMetaverse;
using OpenSim.MeshAsset;
using Xunit.Abstractions;
using V3 = OpenMetaverse.Vector3;

namespace OpenSim.CharacterPublish.Tests;

/// <summary>
/// Real attachment objects (private, from resident inventory archives) read by the region's deserializer and
/// compared with what our writer produces. Skipped unless CHARPUB_REAL_OBJECTS names a folder holding
/// obj/*_object.xml and mesh/*_mesh.llmesh extracted from an archive.
/// </summary>
public class RealAttachmentTests
{
    readonly ITestOutputHelper _out;
    public RealAttachmentTests(ITestOutputHelper output) => _out = output;

    sealed class RealObjectsFact : FactAttribute
    {
        public RealObjectsFact()
        {
            var d = Environment.GetEnvironmentVariable("CHARPUB_REAL_OBJECTS");
            if (string.IsNullOrEmpty(d) || !Directory.Exists(Path.Combine(d, "obj")))
                Skip = "set CHARPUB_REAL_OBJECTS to a folder with obj/ and mesh/ extracted from an inventory archive; private, not committed. Nothing is asserted without it.";
        }
    }

    /// <summary>
    /// The size the viewer's uploader gives the object: a mesh asset stores its vertices scaled into a unit box and
    /// keeps the original size as the face's NormalizedScale (llmodel.cpp:200-340 normalizeVolumeFaces, written per face
    /// by LLModel::writeModel). Without it, the extent of the positions inside the position domain.
    /// </summary>
    internal static V3 MeshExtent(MeshAssetModel m)
    {
        var lod = m.High ?? m.Medium ?? m.Low ?? m.Lowest ?? throw new InvalidDataException("no LOD");
        var face = lod.Faces.FirstOrDefault(f => f.NormalizedScale is not null);
        if (face?.NormalizedScale is { } ns) return new V3(ns.X, ns.Y, ns.Z);
        float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
        foreach (var f in lod.Faces)
            foreach (var p in f.Positions)
            {
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
                x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
            }
        return new V3(x1 - x0, y1 - y0, z1 - z0);
    }

    [RealObjectsFact]
    public void TheRegionReadsEveryRealRiggedAttachmentAndWeCompareFields()
    {
        var dir = Environment.GetEnvironmentVariable("CHARPUB_REAL_OBJECTS")!;
        var report = new StringBuilder();
        var read = 0;
        var sizeMatch = 0;
        var sizeCompared = 0;
        var perFile = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(dir, "obj"), "*_object.xml").OrderBy(x => x, StringComparer.Ordinal))
        {
            var grp = AttachmentObjectTests.Read(File.ReadAllText(file));
            if (grp.Parts.Length != 1 || !grp.RootPart.Shape.SculptEntry || grp.RootPart.Shape.SculptType != (byte)SculptType.Mesh || grp.AttachmentPoint == 0) continue;
            var part = grp.RootPart;
            var meshFile = Path.Combine(dir, "mesh", Path.GetFileName(file).Split('_')[0] + "_" + part.Shape.SculptTexture + "_mesh.llmesh");
            if (!File.Exists(meshFile)) continue; // not rigged (no skin block), or not in the archive
            MeshAssetModel mesh;
            try { mesh = MeshCodec.Decode(File.ReadAllBytes(meshFile)); } catch (Exception) { continue; }
            if (mesh.Skin is null) continue;
            read++;

            var ext = MeshExtent(mesh);
            sizeCompared++;
            var close = Math.Abs(ext.X - part.Scale.X) < 0.01f * Math.Max(1f, ext.X) && Math.Abs(ext.Y - part.Scale.Y) < 0.01f * Math.Max(1f, ext.Y) && Math.Abs(ext.Z - part.Scale.Z) < 0.01f * Math.Max(1f, ext.Z);
            if (close) sizeMatch++;
            perFile.Add(string.Join('\t', grp.AttachmentPoint, part.Shape.LastAttachPoint, part.Flags, part.Material, part.PhysicsShapeType,
                part.Shape.ProfileCurve, part.Shape.PathCurve, part.Shape.PathScaleX, part.Shape.PathScaleY, part.GetNumberOfSides(),
                part.Shape.Textures.DefaultTexture?.TextureID == UUID.Zero ? "default-zero" : part.Shape.Textures.DefaultTexture?.TextureID == Primitive.TextureEntry.WHITE_TEXTURE ? "default-white" : "default-other",
                close ? "size=NormalizedScale" : $"size!=NormalizedScale({part.Scale.X:0.###},{part.Scale.Y:0.###},{part.Scale.Z:0.###} vs {ext.X:0.###},{ext.Y:0.###},{ext.Z:0.###})"));
        }
        var o = Read(AttachmentObjectWriter.Write(AttachmentObjectTests.Spec())).RootPart;
        report.AppendLine($"OURS	{o.Shape.State}	{o.Shape.LastAttachPoint}	{o.Flags}	{o.Material}	{o.PhysicsShapeType}	{o.Shape.ProfileCurve}	{o.Shape.PathCurve}	{o.Shape.PathScaleX}	{o.Shape.PathScaleY}	{o.GetNumberOfSides()}");
        report.AppendLine($"rigged mesh attachments read by the region's deserializer: {read}; size equals the mesh's NormalizedScale: {sizeMatch} of {sizeCompared}");
        report.AppendLine("State\tLastAttach\tFlags\tMaterial\tPhysShape\tProfileCurve\tPathCurve\tPathScaleX\tPathScaleY\tFaces\tDefaultTexture\tSize");
        foreach (var l in perFile) report.AppendLine(l);
        _out.WriteLine(report.ToString());
        Assert.True(read >= 5, $"only {read} rigged mesh attachments found");
    }

    static Region.Framework.Scenes.SceneObjectGroup Read(string xml) => AttachmentObjectTests.Read(xml);
}

/// <summary>
/// The Max body (a real encoded rigged mesh of eight faces) as an attachment object. Skipped unless CHARACTER_REFS
/// holds work/max-body-1.9.llmesh. When CHARPUB_SAMPLES_OUT names a folder the object and the mesh asset are written there.
/// </summary>
public class MaxBodyAttachmentTests
{
    sealed class MaxFact : FactAttribute
    {
        public MaxFact()
        {
            if (!File.Exists(MeshPath)) Skip = "set CHARACTER_REFS to a folder holding work/max-body-1.9.llmesh (private, not committed)";
        }
    }

    static string MeshPath => Path.Combine(Environment.GetEnvironmentVariable("CHARACTER_REFS") ?? "", "work", "max-body-1.9.llmesh");

    /// <summary>Face to bake layer for the eight faces of the body mesh (head; neck, top, sleeves, gloves; shorts, legs, feet).</summary>
    static readonly Dictionary<int, BakeChannel> FaceMap = new()
    {
        [0] = BakeChannel.Head,
        [1] = BakeChannel.Upper, [2] = BakeChannel.Upper, [3] = BakeChannel.Upper, [4] = BakeChannel.Upper,
        [5] = BakeChannel.Lower, [6] = BakeChannel.Lower, [7] = BakeChannel.Lower,
    };

    [MaxFact]
    public void TheBodyMeshBecomesAnAttachmentObjectTheRegionReadsBack()
    {
        var bytes = File.ReadAllBytes(MeshPath);
        var mesh = MeshCodec.Decode(bytes);
        var faces = mesh.High!.Faces.Count;
        Assert.Equal(8, faces);
        Assert.NotNull(mesh.Skin);

        var size = RealAttachmentTests.MeshExtent(mesh);
        Assert.True(size.X > 0.01f && size.Y > 0.01f && size.Z > 0.01f);

        var meshId = new UUID(System.Security.Cryptography.MD5.HashData(bytes), 0);
        var creator = new UUID("aaaaaaaa-0000-4000-8000-000000000001");
        var owner = new UUID("bbbbbbbb-0000-4000-8000-000000000002");
        var description = Environment.GetEnvironmentVariable("CHARPUB_MAX_DESCRIPTION") ?? "Example mesh body, CC BY 4.0, modified.";
        var spec = new AttachmentObjectSpec(meshId, faces, FaceMap, AttachmentPoint.Chest, size,
            PermissionBlock.ForOwner(creator, owner, PermissionMasks.CopyModifyTransfer), "Max Body", description);

        var xml = AttachmentObjectWriter.Write(spec);
        var part = AttachmentObjectTests.Read(xml).RootPart;
        Assert.Equal(faces, part.GetNumberOfSides());
        Assert.Equal(meshId, part.Shape.SculptTexture);
        Assert.Equal((byte)AttachmentPoint.Chest, part.Shape.State);
        Assert.Equal(size.X, part.Scale.X, 4);
        Assert.Equal(size.Y, part.Scale.Y, 4);
        Assert.Equal(size.Z, part.Scale.Z, 4);
        foreach (var (face, channel) in FaceMap)
            Assert.Equal(BakeChannelIds.Of(channel), part.Shape.Textures.GetFace((uint)face).TextureID);
        Assert.Equal(description, part.Description);

        var outDir = Environment.GetEnvironmentVariable("CHARPUB_SAMPLES_OUT");
        if (!string.IsNullOrEmpty(outDir) && Directory.Exists(outDir))
        {
            File.WriteAllText(Path.Combine(outDir, "max-body-attachment.object.xml"), xml, new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(outDir, $"max-body.{meshId}.llmesh"), bytes);
        }
    }
}
