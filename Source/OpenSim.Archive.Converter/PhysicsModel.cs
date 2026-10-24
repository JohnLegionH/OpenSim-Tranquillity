/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using System.Numerics;
using OpenSim.Archive.Eligibility;
using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Inspection;
using OpenSim.Framework;
using OpenSim.MeshAsset;
using OpenSim.Region.PhysicsModules.Meshing;
using OpenSim.Region.PhysicsModules.Meshing.PrimMesher;
using OpenSim.Region.PhysicsModules.SharedBase;
using SkiaSharp;
using SculptMap = OpenSim.Archive.GltfExport.SculptMap;

namespace OpenSim.Archive.Converter;

/// <summary>The triangles of a part's collision shape in the part's own unit-size space: vertices as x, y, z, and three vertex numbers per triangle.</summary>
public sealed class PhysicsGeometry
{
    public float[] Positions = Array.Empty<float>();
    public int[] Indices = Array.Empty<int>();

    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// The collision shape of a prim or sculpt, made by the region's own physics mesher: <see cref="Meshmerizer"/> (PrimMesher in
/// physics mode) asked for the physics detail the Jolt scene asks for, at unit size. A region scales this geometry by the
/// part's size (PrimMesh.Scale, and AddSubMesh for a mesh asset's physics block), so the same triangles written into a mesh
/// asset's physics_mesh block give the converted part the shape the prim had. The geometry is read back through the mesher's
/// own accessors (<see cref="IMesh.getVertexListAsFloat"/> and <see cref="IMesh.getIndexListAsInt"/>), which is what a physics
/// scene reads, so vertices are merged and numbered exactly as it would.
/// </summary>
public static class PrimPhysics
{
    /// <summary>The level of detail a region's physics scene asks the mesher for (the Jolt scene's MeshLod, BulletSim's default).</summary>
    public const float Detail = (float)LevelOfDetail.High;

    private static readonly Meshmerizer s_mesher = new Meshmerizer();
    private static readonly object s_mesherLock = new object();

    /// <summary>
    /// The unit-size physics triangles of the shape, or null with <paramref name="problem"/> set. A sculpt is made from
    /// <paramref name="sculptJp2"/> (the sculpt map as stored in the archive) when it is given, else from the decoded
    /// <paramref name="sculptMap"/>; both are the region's sculpt mesher (<see cref="SculptMesh"/>) on the map's pixels.
    /// </summary>
    public static PhysicsGeometry Build(ShapeData shape, byte[] sculptJp2, SculptMap sculptMap, out string problem)
    {
        problem = null;
        PrimitiveBaseShape pbs = FaceAgreement.ToServerShape(shape);
        try
        {
            if (shape.SculptEntry && sculptJp2 == null)
                return FromSculptMap(pbs, sculptMap, out problem);

            if (shape.SculptEntry)
                pbs.SculptData = (byte[])sculptJp2.Clone();

            IMesh mesh;
            lock (s_mesherLock)
                mesh = s_mesher.CreateMesh("physics-model", pbs, new OpenMetaverse.Vector3(1f, 1f, 1f), Detail, false, false);
            return Read(mesh, out problem);
        }
        catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is IndexOutOfRangeException || e is NullReferenceException || e is IOException)
        {
            problem = e.GetType().Name + ": " + e.Message;
            return null;
        }
    }

    private static PhysicsGeometry FromSculptMap(PrimitiveBaseShape pbs, SculptMap map, out string problem)
    {
        problem = null;
        if (map == null || map.Rgb == null || map.Width < 2 || map.Height < 2)
        {
            problem = "the sculpt map has no pixels";
            return null;
        }

        // The region's sculpt path (Meshmerizer.GenerateCoordsAndFacesFromPrimSculptData) on a decoded bitmap: the map type
        // from the low three bits, mirror and invert from the flag bits, the same level of detail, unit size.
        SculptMesh.SculptType type = (OpenMetaverse.SculptType)(pbs.SculptType & 0x07) switch
        {
            OpenMetaverse.SculptType.Cylinder => SculptMesh.SculptType.cylinder,
            OpenMetaverse.SculptType.Torus => SculptMesh.SculptType.torus,
            OpenMetaverse.SculptType.Sphere => SculptMesh.SculptType.sphere,
            _ => SculptMesh.SculptType.plane
        };
        using SKBitmap bitmap = new SKBitmap(map.Width, map.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
            {
                int i = (y * map.Width + x) * 3;
                bitmap.SetPixel(x, y, new SKColor(map.Rgb[i], map.Rgb[i + 1], map.Rgb[i + 2], 255));
            }
        SculptMesh sm = new SculptMesh(bitmap, type, (int)Detail, false, (pbs.SculptType & 128) != 0, (pbs.SculptType & 64) != 0);

        Mesh mesh = new Mesh();
        List<Vertex> vertices = sm.coords.Select(c => new Vertex(c.X, c.Y, c.Z)).ToList();
        foreach (var f in sm.faces)
            mesh.Add(new Triangle(vertices[f.v1], vertices[f.v2], vertices[f.v3]));
        return Read(mesh, out problem);
    }

    private static PhysicsGeometry Read(IMesh mesh, out string problem)
    {
        problem = null;
        if (mesh == null)
        {
            problem = "the region's mesher gave no physics mesh for the shape";
            return null;
        }
        float[] positions = mesh.getVertexListAsFloat();
        int[] indices = mesh.getIndexListAsInt();
        if (positions == null || indices == null || positions.Length < 9 || indices.Length < 3 || indices.Length % 3 != 0)
        {
            problem = "the region's mesher gave an empty physics mesh for the shape";
            return null;
        }
        return new PhysicsGeometry { Positions = positions, Indices = indices };
    }
}

/// <summary>The physics_mesh block of a mesh asset (the block a region reads before the display levels; see Meshmerizer.GenerateCoordsAndFacesFromPrimMeshData).</summary>
public static class PhysicsMeshBlock
{
    /// <summary>
    /// The LLSD value of a physics_mesh block holding the geometry as one submesh: positions in a domain spanning them, 16-bit
    /// triangle indices, no normals or texture coordinates. It is written the way a display level's face is written, so the
    /// block is made by <see cref="MeshCodec"/> and read out of an asset that holds the geometry as its only level.
    /// </summary>
    public static object Make(PhysicsGeometry g)
    {
        if (g.VertexCount > 65535)
            throw new MeshFormatException("the physics mesh has " + g.VertexCount + " vertices; the asset stores 16-bit indices");

        Vector3[] positions = new Vector3[g.VertexCount];
        for (int i = 0; i < positions.Length; i++)
            positions[i] = new Vector3(g.Positions[i * 3], g.Positions[i * 3 + 1], g.Positions[i * 3 + 2]);
        MeshAssetModel carrier = new MeshAssetModel { High = new MeshLod() };
        carrier.High.Faces.Add(new OpenSim.MeshAsset.MeshFace { Positions = positions, Indices = (int[])g.Indices.Clone() });
        byte[] bytes = MeshCodec.Encode(carrier);

        if (!(LlsdBinary.Read(bytes, out int headerLength) is IDictionary<string, object> header)
            || !(header["high_lod"] is IDictionary<string, object> entry))
            throw new MeshFormatException("the carrier asset has no high_lod entry");
        int offset = Convert.ToInt32(entry["offset"]), size = Convert.ToInt32(entry["size"]);
        using MemoryStream compressed = new MemoryStream(bytes, headerLength + offset, size);
        using ZLibStream z = new ZLibStream(compressed, CompressionMode.Decompress);
        using MemoryStream raw = new MemoryStream();
        z.CopyTo(raw);
        return LlsdBinary.Read(raw.ToArray(), out _);
    }
}
