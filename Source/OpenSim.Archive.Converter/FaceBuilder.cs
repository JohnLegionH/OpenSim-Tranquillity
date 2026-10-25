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
    public static List<EncoderFace> FromPrim(ShapeData shape, out int unnumberedTriangles, bool clean = true)
    {
        PrimParams p = PrimParams.FromShape(shape);
        List<PrimFace> faces = PrimGeometry.BuildFaces(p, DetailLevel.High, out int count, out unnumberedTriangles);
        return Slots(faces, count, clean);
    }

    /// <summary>Puts each face in the slot of its number; the slots no face fills are empty.</summary>
    public static List<EncoderFace> Slots(IEnumerable<PrimFace> faces, int count, bool clean = true)
    {
        List<EncoderFace> result = new List<EncoderFace>(count);
        for (int i = 0; i < count; i++)
            result.Add(new EncoderFace());
        foreach (PrimFace f in faces)
        {
            if (f.Number < 0 || f.Number >= count)
                throw new InvalidOperationException("the geometry has a face numbered " + f.Number + " but the shape has " + count + " faces");
            result[f.Number] = ToMeshFace(f, clean);
        }
        return result;
    }

    /// <summary>
    /// A face is closed and a new one started when it holds this many vertices at the end of a triangle. This is the limit the
    /// viewer's model uploader uses for a triangle list (lldaeloader.cpp:300, "verts.size() >= 65532"); a face's indices are 16
    /// bits wide, so it is a few below 65535.
    /// </summary>
    public const int SplitAtVertices = 65532;

    /// <summary>
    /// Like <see cref="Slots"/>, but a face with <see cref="SplitAtVertices"/> or more vertices becomes several faces, as the
    /// viewer's uploader splits one. The faces come out in slot order, a split face's parts together. <paramref name="sourceSlot"/>
    /// gives, for each face of the result, the slot it came from, so a caller can give the parts the settings of their slot.
    /// </summary>
    public static List<EncoderFace> SlotsSplit(IEnumerable<PrimFace> faces, int count, out List<int> sourceSlot)
    {
        List<PrimFace>[] bySlot = new List<PrimFace>[count];
        foreach (PrimFace f in faces)
        {
            if (f.Number < 0 || f.Number >= count)
                throw new InvalidOperationException("the geometry has a face numbered " + f.Number + " but the shape has " + count + " faces");
            (bySlot[f.Number] ??= new List<PrimFace>()).Add(f);
        }
        List<EncoderFace> result = new List<EncoderFace>(count);
        sourceSlot = new List<int>(count);
        for (int slot = 0; slot < count; slot++)
        {
            if (bySlot[slot] == null)
            {
                result.Add(new EncoderFace());
                sourceSlot.Add(slot);
                continue;
            }
            // a slot gets one face from the geometry; if a caller gave several for one slot, the last wins as in Slots
            foreach (EncoderFace part in ToMeshFaces(bySlot[slot][bySlot[slot].Count - 1]))
            {
                result.Add(part);
                sourceSlot.Add(slot);
            }
        }
        return result;
    }

    /// <summary>
    /// <see cref="ToMeshFace"/>, split the way the viewer's uploader splits a triangle list: vertices are shared inside the part
    /// being built; when a triangle ends and the part holds <see cref="SplitAtVertices"/> or more vertices, the part is closed
    /// and the next triangle starts a new part with an empty vertex list.
    /// </summary>
    public static List<EncoderFace> ToMeshFaces(PrimFace f)
    {
        List<EncoderFace> parts = new List<EncoderFace>();
        int corners = f.Positions.Length / 3;
        Dictionary<(int, int, int, int, int, int, int, int), uint> seen = new Dictionary<(int, int, int, int, int, int, int, int), uint>();
        List<float> pos = new List<float>(), nor = new List<float>(), uv = new List<float>();
        List<uint> index = new List<uint>();

        void Close()
        {
            parts.Add(new EncoderFace { Positions = pos.ToArray(), Normals = nor.ToArray(), Uvs = uv.ToArray(), Indices = index.ToArray() });
            seen.Clear();
            pos.Clear(); nor.Clear(); uv.Clear(); index.Clear();
        }

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
            if (index.Count % 3 == 0 && pos.Count / 3 >= SplitAtVertices && c + 1 < corners)
                Close();
        }
        if (index.Count > 0 || parts.Count == 0)
            Close();
        return parts;
    }

    /// <summary>
    /// Indexes the face's triangles, merging vertices that agree in position, normal and texture coordinate, and turns the
    /// texture coordinates from the exporter's convention (v = 0 at the top of the image) to the viewer's (v = 0 at the bottom).
    /// </summary>
    public static EncoderFace ToMeshFace(PrimFace f, bool clean = false)
    {
        if (clean)
        {
            EncoderFace cleaned = Cleaned(f);
            if (!cleaned.KeptAsWas)
                return cleaned;
        }
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

    /// <summary>The face with duplicate and loose vertices and zero-area triangles removed (<see cref="GeometryCleaner"/>), in the viewer's texture convention.</summary>
    private static EncoderFace Cleaned(PrimFace f)
    {
        float[] uv = new float[f.Uvs.Length];
        for (int i = 0; i < uv.Length; i += 2)
        {
            uv[i] = f.Uvs[i];
            uv[i + 1] = 1f - f.Uvs[i + 1];
        }
        CleanedFace c = GeometryCleaner.Clean((float[])f.Positions.Clone(), (float[])f.Normals.Clone(), uv);
        uint[] index = new uint[c.Indices.Length];
        for (int i = 0; i < index.Length; i++)
            index[i] = (uint)c.Indices[i];
        return new EncoderFace { Positions = c.Positions, Normals = c.Normals, Uvs = c.Uvs, Indices = index, KeptAsWas = c.KeptAsWas };
    }

    // x + 0 turns a negative zero into a positive one, so the two compare equal.
    private static int Bits(float x) => BitConverter.SingleToInt32Bits(x + 0f);
}
