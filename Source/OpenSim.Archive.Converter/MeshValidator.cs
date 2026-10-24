/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Framework;
using OpenSim.MeshAsset;
using OpenSim.Region.PhysicsModules.Meshing;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Archive.Converter;

/// <summary>
/// Decodes an encoded mesh and checks that it is the mesh that was meant. Two readers are used: <see cref="MeshCodec"/> (the face
/// count equals the faces encoded, each face has the vertices and triangles that went in, every level of detail is there with
/// the same number of faces, and the physics block holds a hull) and OpenSim's own physics mesher, which a region uses to make
/// a mesh part's physics shape (it yields positions and triangles, and the triangles index the positions). A test also decodes
/// meshes with LibreMetaverse's decoder.
/// </summary>
public static class MeshValidator
{
    /// <summary>What was read, for reports.</summary>
    public sealed class Reading
    {
        public int Faces;
        public int HighVertices, HighTriangles;
        public int RegionVertices, RegionTriangles;
        /// <summary>Vertices and triangles of the physics_mesh block as MeshCodec reads it (0 when the asset has none).</summary>
        public int PhysicsVertices, PhysicsTriangles;
    }

    /// <summary>The most a position read back from the physics block may differ from the physics mesher's: the block stores 16-bit positions across the geometry's box (about 1.6e-5 of a unit-size box).</summary>
    public const float PhysicsTolerance = 1e-4f;

    private static readonly Meshmerizer s_mesher = new Meshmerizer();
    private static readonly object s_mesherLock = new object();

    /// <summary>The problems found; an empty list means the mesh passed.</summary>
    public static List<string> Check(byte[] bytes, IReadOnlyList<EncoderFace> faces, out Reading reading, PhysicsGeometry physics = null)
    {
        List<string> bad = new List<string>();
        reading = new Reading();

        MeshAssetModel model;
        try
        {
            model = MeshCodec.Decode(bytes);
        }
        catch (Exception e)
        {
            // whatever the decoder throws on a bad asset is the answer, reported against the part
            bad.Add("OpenSim.MeshAsset cannot decode the asset: " + e.Message);
            return bad;
        }

        for (int level = 0; level < MeshAssetModel.LodCount; level++)
        {
            MeshLod lod = model.Lod(level);
            string name = LevelName(level);
            if (lod == null)
            {
                bad.Add("the " + name + " level of detail is missing");
                continue;
            }
            if (lod.Faces.Count != faces.Count)
                bad.Add("the " + name + " level has " + lod.Faces.Count + " faces, " + faces.Count + " were encoded");
            else if (lod.Faces.All(f => f.NoGeometry || f.TriangleCount == 0))
                bad.Add("the " + name + " level has no triangles");
        }

        MeshLod high = model.High;
        if (high != null && high.Faces.Count == faces.Count)
        {
            reading.Faces = high.Faces.Count;
            for (int i = 0; i < faces.Count; i++)
            {
                EncoderFace want = faces[i];
                MeshFace got = high.Faces[i];
                int gotVertices = got.NoGeometry ? 0 : got.VertexCount, gotTriangles = got.NoGeometry ? 0 : got.TriangleCount;
                reading.HighVertices += gotVertices;
                reading.HighTriangles += gotTriangles;
                if (want.IsEmpty != (gotTriangles == 0))
                    bad.Add("face " + i + (want.IsEmpty ? " should be empty and is not" : " should have triangles and is empty"));
                else if (!want.IsEmpty && (gotVertices != want.VertexCount || gotTriangles != want.TriangleCount))
                    bad.Add("face " + i + " has " + gotVertices + " vertices and " + gotTriangles + " triangles, " + want.VertexCount + " and " + want.TriangleCount + " were encoded");
            }
        }

        if (PhysicsBlock.ReadBoundingVerts(model.PhysicsConvex).Count < 4)
            bad.Add("the physics block holds no hull");

        if (physics != null)
            CheckPhysicsBlock(model, physics, bad, reading);

        ReadWithRegionMesher(bytes, bad, reading, physics);
        return bad;
    }

    // OpenSim's physics mesher, run the way a region runs it for a mesh part: on the asset bytes of a shape with a mesh sculpt type.
    private static void ReadWithRegionMesher(byte[] bytes, List<string> bad, Reading reading, PhysicsGeometry physics)
    {
        PrimitiveBaseShape shape = new PrimitiveBaseShape
        {
            SculptEntry = true,
            SculptType = (byte)OpenMetaverse.SculptType.Mesh,
            SculptData = (byte[])bytes.Clone()
        };
        IMesh mesh;
        try
        {
            lock (s_mesherLock)
                mesh = s_mesher.CreateMesh("validity-check", shape, new OpenMetaverse.Vector3(1f, 1f, 1f), 32f, true, false);
        }
        catch (Exception e)
        {
            bad.Add("OpenSim's mesher cannot read the asset: " + e.Message);
            return;
        }
        if (mesh == null)
        {
            bad.Add("OpenSim's mesher cannot read the asset");
            return;
        }
        List<OpenMetaverse.Vector3> vertices = mesh.getVertexList();
        int[] indices = mesh.getIndexListAsInt();
        reading.RegionVertices = vertices.Count;
        reading.RegionTriangles = indices.Length / 3;
        if (vertices.Count == 0 || indices.Length < 3)
            bad.Add("OpenSim's mesher reads no positions or no triangles from the asset");
        else if (indices.Any(i => i < 0 || i >= vertices.Count))
            bad.Add("OpenSim's mesher reads a triangle that points outside its positions");
        else if (physics != null)
        {
            // With a physics_mesh block present the region's mesher reads that block, not a display level: it must give back the physics geometry.
            if (!SameCollision(physics.Positions, physics.Indices, mesh.getVertexListAsFloat(), indices, PhysicsTolerance, CollapsedArea, out string why))
                bad.Add("OpenSim's mesher reads physics triangles that differ from the physics model: " + why);
        }
    }

    /// <summary>A triangle smaller than this (unit-size area, or the same fraction of any size's square) is a collapsed sliver, not a surface.</summary>
    public const float CollapsedArea = 1e-7f;

    /// <summary>
    /// Whether two triangle lists collide alike: the triangles that have an area, taken in order, have the same corners to within
    /// <paramref name="positionTolerance"/>. A collapsed triangle (area below <paramref name="collapsedArea"/>) is left out on both
    /// sides. The prim mesher puts several vertices a rounding error apart at a sphere's pole and along a seam; the 16-bit positions
    /// of a mesh asset make them one point, and the reader drops the triangles that collapse, which are no surface at all.
    /// </summary>
    public static bool SameCollision(float[] positionsA, int[] indicesA, float[] positionsB, int[] indicesB, float positionTolerance, float collapsedArea, out string why)
    {
        List<float[]> a = Solid(positionsA, indicesA, collapsedArea), b = Solid(positionsB, indicesB, collapsedArea);
        why = null;
        if (a.Count != b.Count)
        {
            why = a.Count + " triangles with an area against " + b.Count;
            return false;
        }
        for (int t = 0; t < a.Count; t++)
            for (int k = 0; k < 9; k++)
                if (Math.Abs(a[t][k] - b[t][k]) > positionTolerance)
                {
                    why = "triangle " + t + " differs by " + Math.Abs(a[t][k] - b[t][k]);
                    return false;
                }
        return true;
    }

    private static List<float[]> Solid(float[] positions, int[] indices, float collapsedArea)
    {
        List<float[]> solid = new List<float[]>();
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            float[] t = new float[9];
            for (int c = 0; c < 3; c++)
                Array.Copy(positions, indices[i + c] * 3, t, c * 3, 3);
            float ux = t[3] - t[0], uy = t[4] - t[1], uz = t[5] - t[2], vx = t[6] - t[0], vy = t[7] - t[1], vz = t[8] - t[2];
            float cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            if (0.5f * MathF.Sqrt(cx * cx + cy * cy + cz * cz) > collapsedArea)
                solid.Add(t);
        }
        return solid;
    }

    // MeshCodec's reading of the physics_mesh block: one submesh with the geometry's vertices and triangles.
    private static void CheckPhysicsBlock(MeshAssetModel model, PhysicsGeometry physics, List<string> bad, Reading reading)
    {
        if (model.PhysicsMesh == null)
        {
            bad.Add("the asset has no physics_mesh block");
            return;
        }
        // Read the block back through a carrier: the block is the value a display level of one face has.
        if (!(model.PhysicsMesh is System.Collections.IList submeshes) || submeshes.Count != 1 || !(submeshes[0] is IDictionary<string, object> sub)
            || !sub.ContainsKey("Position") || !sub.ContainsKey("TriangleList"))
        {
            bad.Add("the physics_mesh block is not one submesh with positions and triangles");
            return;
        }
        reading.PhysicsVertices = ((byte[])sub["Position"]).Length / 6;
        reading.PhysicsTriangles = ((byte[])sub["TriangleList"]).Length / 6;
        if (reading.PhysicsVertices != physics.VertexCount || reading.PhysicsTriangles != physics.TriangleCount)
            bad.Add("the physics_mesh block holds " + reading.PhysicsVertices + " vertices and " + reading.PhysicsTriangles + " triangles, the physics model has " + physics.VertexCount + " and " + physics.TriangleCount);
    }

    private static bool Differs(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return true;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > PhysicsTolerance)
                return true;
        return false;
    }

    private static string LevelName(int level) => level switch { 0 => "lowest", 1 => "low", 2 => "medium", _ => "high" };
}
