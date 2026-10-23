/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.Converter;

/// <summary>
/// Builds the faces of a mesh from a prim's shape: the geometry of the shape in the unit box, one mesh face for each face the
/// viewer gives the prim, in the viewer's face order, with the viewer's default texture coordinates. Face number i of the
/// mesh is face number i of the prim, so a texture entry that drew the prim draws the mesh the same way.
/// </summary>
public static class FaceBuilder
{
    /// <summary>
    /// The faces of a prim. The list has one slot for every face the viewer's rule lists (<see cref="ViewerFaceRule"/>); a face
    /// that has no area is an empty slot and keeps its number.
    /// </summary>
    /// <param name="unnumberedTriangles">Triangles the mesher built that belong to no face of the viewer's list; they are left out.</param>
    public static List<EncoderFace> FromPrim(ShapeData shape, out int unnumberedTriangles)
    {
        PrimParams p = PrimParams.FromShape(shape);
        List<PrimFace> faces = PrimGeometry.BuildFaces(p, DetailLevel.High, out int count, out unnumberedTriangles);
        return Slots(faces, count);
    }

    /// <summary>Puts each face in the slot of its number; the slots no face fills are empty.</summary>
    public static List<EncoderFace> Slots(IEnumerable<PrimFace> faces, int count)
    {
        List<EncoderFace> result = new List<EncoderFace>(count);
        for (int i = 0; i < count; i++)
            result.Add(new EncoderFace());
        foreach (PrimFace f in faces)
        {
            if (f.Number < 0 || f.Number >= count)
                throw new InvalidOperationException("the geometry has a face numbered " + f.Number + " but the shape has " + count + " faces");
            result[f.Number] = ToMeshFace(f);
        }
        return result;
    }

    /// <summary>
    /// Indexes the face's triangles, merging vertices that agree in position, normal and texture coordinate, and turns the
    /// texture coordinates from the exporter's convention (v = 0 at the top of the image) to the viewer's (v = 0 at the bottom).
    /// </summary>
    public static EncoderFace ToMeshFace(PrimFace f)
    {
        int corners = f.Positions.Length / 3;
        Dictionary<(int, int, int, int, int, int, int, int), uint> seen = new Dictionary<(int, int, int, int, int, int, int, int), uint>();
        List<float> pos = new List<float>(), nor = new List<float>(), uv = new List<float>();
        List<uint> index = new List<uint>(corners);

        for (int c = 0; c < corners; c++)
        {
            float px = f.Positions[c * 3], py = f.Positions[c * 3 + 1], pz = f.Positions[c * 3 + 2];
            float nx = f.Normals[c * 3], ny = f.Normals[c * 3 + 1], nz = f.Normals[c * 3 + 2];
            float u = f.Uvs[c * 2], v = 1f - f.Uvs[c * 2 + 1];
            var key = (Bits(px), Bits(py), Bits(pz), Bits(nx), Bits(ny), Bits(nz), Bits(u), Bits(v));
            if (!seen.TryGetValue(key, out uint at))
            {
                at = (uint)(pos.Count / 3);
                seen[key] = at;
                pos.Add(px); pos.Add(py); pos.Add(pz);
                nor.Add(nx); nor.Add(ny); nor.Add(nz);
                uv.Add(u); uv.Add(v);
            }
            index.Add(at);
        }
        return new EncoderFace { Positions = pos.ToArray(), Normals = nor.ToArray(), Uvs = uv.ToArray(), Indices = index.ToArray() };
    }

    // x + 0 turns a negative zero into a positive one, so the two compare equal.
    private static int Bits(float x) => BitConverter.SingleToInt32Bits(x + 0f);
}
