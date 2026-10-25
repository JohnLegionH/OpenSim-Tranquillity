/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using OpenSim.MeshAsset;

namespace OpenSim.Archive.Converter;

/// <summary>
/// Writes a faces list as a Linden Lab mesh asset with <see cref="MeshCodec"/>. The faces become the high level of detail, one
/// model face for each face of the list in the same order (positions, normals, texture coordinates, no weights, so no skin);
/// the medium, low and lowest levels are made with the viewer profile (1/3, 1/9 and 1/27 of the high level's triangles,
/// <see cref="LodGenerator"/>); the physics_convex block is the bounding hull the viewer writes for an object uploaded with no
/// physics model (<see cref="PhysicsBlock.AddBoundingHull"/>); and the physics_mesh block, when the options carry a collision
/// shape, is that shape's triangles (<see cref="PhysicsMeshBlock"/>), which a region reads before any display level. A face with no triangles stays an empty slot (NoGeometry), so
/// face numbers do not move. The same faces always give the same bytes.
/// </summary>
public sealed class MeshAssetEncoder : IMeshEncoder
{
    /// <summary>Gets a line for every simplification pass of the next encodes; null says nothing.</summary>
    public Action<string> Trace { get; set; }

    public string Name => "OpenSim.MeshAsset (viewer profile levels of detail, bounding hull, prim physics mesh)";
    public bool ProducesValidMesh => true;

    public byte[] Encode(IReadOnlyList<EncoderFace> faces, EncodeOptions options)
    {
        MeshAssetModel model = new MeshAssetModel { High = new MeshLod() };
        foreach (EncoderFace f in faces)
            model.High.Faces.Add(ToModelFace(f));

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        void Lap(string what)
        {
            Trace?.Invoke(what + " in " + clock.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s");
            clock.Restart();
        }
        LodGenerator.Trace = Trace;
        try { LodGenerator.GenerateAll(model, LodGenerator.Decimation, LodGenerator.Profile.Viewer); }
        finally { LodGenerator.Trace = null; }
        Lap("levels of detail made");
        PhysicsBlock.AddBoundingHull(model);
        Lap("bounding hull made");
        if (options?.Physics != null)
            model.PhysicsMesh = PhysicsMeshBlock.Make(options.Physics);
        Lap("physics mesh made");
        byte[] bytes = MeshCodec.Encode(model);
        Lap("asset written");
        return bytes;
    }

    private static MeshFace ToModelFace(EncoderFace f)
    {
        if (f.IsEmpty)
            return new MeshFace { NoGeometry = true };

        int n = f.VertexCount;
        Vector3[] positions = new Vector3[n], normals = new Vector3[n];
        Vector2[] uvs = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            positions[i] = new Vector3(f.Positions[i * 3], f.Positions[i * 3 + 1], f.Positions[i * 3 + 2]);
            normals[i] = new Vector3(f.Normals[i * 3], f.Normals[i * 3 + 1], f.Normals[i * 3 + 2]);
            uvs[i] = new Vector2(f.Uvs[i * 2], f.Uvs[i * 2 + 1]);
        }
        int[] indices = new int[f.Indices.Length];
        for (int i = 0; i < indices.Length; i++)
            indices[i] = checked((int)f.Indices[i]);
        return new MeshFace { Positions = positions, Normals = normals, Uvs = uvs, Indices = indices };
    }
}
