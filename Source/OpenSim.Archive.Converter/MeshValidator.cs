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
    }

    private static readonly Meshmerizer s_mesher = new Meshmerizer();
    private static readonly object s_mesherLock = new object();

    /// <summary>The problems found; an empty list means the mesh passed.</summary>
    public static List<string> Check(byte[] bytes, IReadOnlyList<EncoderFace> faces, out Reading reading)
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

        ReadWithRegionMesher(bytes, bad, reading);
        return bad;
    }

    // OpenSim's physics mesher, run the way a region runs it for a mesh part: on the asset bytes of a shape with a mesh sculpt type.
    private static void ReadWithRegionMesher(byte[] bytes, List<string> bad, Reading reading)
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
    }

    private static string LevelName(int level) => level switch { 0 => "lowest", 1 => "low", 2 => "medium", _ => "high" };
}
