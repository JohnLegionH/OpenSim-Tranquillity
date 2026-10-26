/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using OpenSim.Archive.Converter.Merge;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>Shapes in the unit box, and merged parts built from them, for the tests of the levels of detail.</summary>
internal static class LodKit
{
    private static EncoderFace Build(List<Vector3> pos, List<Vector3> nor, List<Vector2> uv, List<uint> index) => new EncoderFace
    {
        Positions = pos.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray(),
        Normals = nor.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray(),
        Uvs = uv.SelectMany(p => new[] { p.X, p.Y }).ToArray(),
        Indices = index.ToArray()
    };

    /// <summary>A sphere whose texture wraps once around it: the first and last column of vertices sit at one place with coordinates 0 and 1, and the poles have a vertex for each column.</summary>
    public static EncoderFace Sphere(int rings, int segments, Vector3 center, float radius)
    {
        List<Vector3> pos = new List<Vector3>(), nor = new List<Vector3>();
        List<Vector2> uv = new List<Vector2>();
        List<uint> index = new List<uint>();
        for (int r = 0; r <= rings; r++)
            for (int s = 0; s <= segments; s++)
            {
                // the last column is the first again, to the bit, with the coordinate 1 in place of 0
                double theta = Math.PI * r / rings, phi = 2 * Math.PI * (s % segments) / segments;
                double ring = r == 0 || r == rings ? 0 : Math.Sin(theta);
                Vector3 n = new Vector3((float)(ring * Math.Cos(phi)), (float)Math.Cos(theta), (float)(ring * Math.Sin(phi)));
                pos.Add(center + n * radius);
                nor.Add(n);
                uv.Add(new Vector2((float)s / segments, 1f - (float)r / rings));
            }
        uint At(int r, int s) => (uint)(r * (segments + 1) + s);
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < segments; s++)
            {
                // one triangle in the rows at the poles (the other of the quad has no area), two elsewhere; every vertex is used
                if (r == 0) { index.Add(At(0, s)); index.Add(At(1, s)); index.Add(At(1, s + 1)); }
                else if (r == rings - 1) { index.Add(At(r, s)); index.Add(At(rings, s)); index.Add(At(r, s + 1)); }
                else
                {
                    index.Add(At(r, s)); index.Add(At(r + 1, s)); index.Add(At(r + 1, s + 1));
                    index.Add(At(r, s)); index.Add(At(r + 1, s + 1)); index.Add(At(r, s + 1));
                }
            }
        return Build(pos, nor, uv, index);
    }

    /// <summary>A box with a vertex for each corner of each side (24), so every edge is a texture seam.</summary>
    public static EncoderFace Box(Vector3 center, Vector3 size)
    {
        List<Vector3> pos = new List<Vector3>(), nor = new List<Vector3>();
        List<Vector2> uv = new List<Vector2>();
        List<uint> index = new List<uint>();
        Vector3[] normals = { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
        foreach (Vector3 n in normals)
        {
            Vector3 u = Math.Abs(n.Z) > 0.5f ? Vector3.UnitX : Vector3.Cross(Vector3.UnitZ, n);
            Vector3 v = Vector3.Cross(n, u);
            uint first = (uint)pos.Count;
            foreach ((float a, float b) in new[] { (-0.5f, -0.5f), (0.5f, -0.5f), (0.5f, 0.5f), (-0.5f, 0.5f) })
            {
                pos.Add(center + (n * 0.5f + u * a + v * b) * size);
                nor.Add(n);
                uv.Add(new Vector2(a + 0.5f, b + 0.5f));
            }
            index.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
        }
        return Build(pos, nor, uv, index);
    }

    /// <summary>A square sheet of n by n cells over the unit box in x and y, its height (in the box's z) from <paramref name="height"/>, coordinates <paramref name="uvOf"/> of the grid position (0 to 1).</summary>
    public static EncoderFace Grid(int n, Func<float, float, float> height, Func<float, float, Vector2> uvOf = null)
    {
        List<Vector3> pos = new List<Vector3>(), nor = new List<Vector3>();
        List<Vector2> uv = new List<Vector2>();
        List<uint> index = new List<uint>();
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                float x = (float)i / n, y = (float)j / n;
                pos.Add(new Vector3(x - 0.5f, y - 0.5f, height(x, y) - 0.5f));
                nor.Add(Vector3.UnitZ);
                uv.Add(uvOf == null ? new Vector2(x, y) : uvOf(x, y));
            }
        uint At(int i, int j) => (uint)(j * (n + 1) + i);
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                index.AddRange(new[] { At(i, j), At(i + 1, j), At(i + 1, j + 1), At(i, j), At(i + 1, j + 1), At(i, j + 1) });
            }
        return Build(pos, nor, uv, index);
    }

    /// <summary>
    /// A merged part: for each slot, the shapes of the members that put triangles into it (member number, shape); the part is
    /// <paramref name="size"/> metres across and each member is the given size in metres.
    /// </summary>
    public static (MergedGeometry Geometry, List<MergeMember> Members) Part(Vector3 size, Vector3[] memberSizes, params (int Member, EncoderFace Shape)[][] slots)
    {
        MergedGeometry g = new MergedGeometry { Scale = size };
        List<MergeMember> members = memberSizes.Select(s => new MergeMember { Scale = s }).ToList();
        for (int slot = 0; slot < slots.Length; slot++)
        {
            List<float> pos = new List<float>(), nor = new List<float>(), uv = new List<float>();
            List<uint> index = new List<uint>();
            foreach ((int member, EncoderFace shape) in slots[slot])
            {
                uint baseVertex = (uint)(pos.Count / 3);
                int firstIndex = index.Count;
                pos.AddRange(shape.Positions);
                nor.AddRange(shape.Normals);
                uv.AddRange(shape.Uvs);
                index.AddRange(shape.Indices.Select(i => i + baseVertex));
                g.Runs.Add(new FaceRun(member, 0, slot, firstIndex, shape.Indices.Length, 0));
            }
            g.Faces.Add(new EncoderFace { Positions = pos.ToArray(), Normals = nor.ToArray(), Uvs = uv.ToArray(), Indices = index.ToArray() });
            g.Template.Add((slots[slot][0].Member, 0));
        }
        return (g, members);
    }
}
