/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

// Adapted 2026 by Legion Builds from OpenSim.Region.PhysicsModules.Meshing/Meshmerizer/Meshmerizer.cs
// (GenerateCoordsAndFacesFromPrimShapeData): the decoding of the stored shape fields and their use as PrimMesh
// settings. The mesher runs in viewer mode, which gives the faces, normals and texture coordinates a viewer draws.
// The face numbering follows the Second Life viewer's rule (a texture entry has one slot per prim face), see
// ViewerFaceRule; the grouping below is written for this exporter.

using OpenMetaverse;
using OpenSim.Archive.Inspection;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.Meshing.PrimMesher;

namespace OpenSim.Archive.GltfExport;

/// <summary>A shape with its fields decoded to the values the mesher takes.</summary>
public sealed class PrimParams
{
    /// <summary>0 circle, 1 square, 2 isometric triangle, 3 equilateral triangle, 4 right triangle, 5 half circle.</summary>
    public int ProfileCurve;
    /// <summary>0 same as the profile, 16 circle, 32 square, 48 triangle.</summary>
    public int HoleType;
    /// <summary>16 straight line, 32 and 48 circular, 128 flexible.</summary>
    public int PathCurve;
    public float ProfileBegin, ProfileEnd, ProfileHollow;
    public float PathBegin, PathEnd;
    public float PathScaleX, PathScaleY;
    public float PathShearX, PathShearY;
    public float PathTwist, PathTwistBegin;
    public float PathRadiusOffset, PathTaperX, PathTaperY;
    public float PathRevolutions, PathSkew;

    /// <summary>
    /// Decodes the stored byte and short fields the way the viewer protocol defines them: a profile or path cut is
    /// the stored value times 0.00002 (an end is counted from the far side), scale is 2 minus the stored value over
    /// 100, shear is a signed byte over 100, twist, taper, radius and skew are signed bytes over 100, revolutions
    /// are 1 plus 0.015 per step.
    /// </summary>
    public static PrimParams FromShape(ShapeData s)
    {
        int curve = s.ProfileCurve;
        int profile = curve & 0x0F;
        int hole = curve & 0xF0;
        if (!string.IsNullOrEmpty(s.ProfileShapeName) && Enum.TryParse(s.ProfileShapeName, true, out ProfileShape ps))
            profile = (int)ps;
        if (!string.IsNullOrEmpty(s.HollowShapeName) && Enum.TryParse(s.HollowShapeName, true, out HollowShape hs))
            hole = (int)hs;

        return new PrimParams
        {
            ProfileCurve = profile,
            HoleType = hole,
            PathCurve = s.PathCurve,
            ProfileBegin = s.ProfileBegin * 2.0e-5f,
            ProfileEnd = 1.0f - s.ProfileEnd * 2.0e-5f,
            ProfileHollow = s.ProfileHollow * 2.0e-5f,
            PathBegin = s.PathBegin * 2.0e-5f,
            PathEnd = 1.0f - s.PathEnd * 2.0e-5f,
            PathScaleX = (200 - s.PathScaleX) * 0.01f,
            PathScaleY = (200 - s.PathScaleY) * 0.01f,
            PathShearX = ShearOf(s.PathShearX),
            PathShearY = ShearOf(s.PathShearY),
            PathTwist = SByteOf(s.PathTwist) * 0.01f,
            PathTwistBegin = SByteOf(s.PathTwistBegin) * 0.01f,
            PathRadiusOffset = SByteOf(s.PathRadiusOffset) * 0.01f,
            PathTaperX = SByteOf(s.PathTaperX) * 0.01f,
            PathTaperY = SByteOf(s.PathTaperY) * 0.01f,
            PathRevolutions = 1.0f + 0.015f * (s.PathRevolutions & 0xFF),
            PathSkew = SByteOf(s.PathSkew) * 0.01f
        };
    }

    private static float ShearOf(int stored)
    {
        int b = stored & 0xFF;
        return b < 128 ? b * 0.01f : (b - 256) * 0.01f;
    }

    // The XML holds signed bytes as plain numbers; a file written by another tool may hold them as 0..255.
    private static int SByteOf(int stored) => (sbyte)(stored & 0xFF);
}

/// <summary>One prim face: non-indexed triangles in the prim's own axes, before the part's size is applied.</summary>
public sealed class PrimFace
{
    /// <summary>The face number a texture entry uses for this face.</summary>
    public int Number;
    public PrimFaceKind Kind;
    /// <summary>The number the mesher gave the first triangle of this face; for information only.</summary>
    public int MesherNumber;
    public float[] Positions;
    public float[] Normals;
    public float[] Uvs;
    public int TriangleCount => Positions.Length / 9;
}

public enum DetailLevel { Low, Medium, High }

public static class PrimGeometry
{
    private static int CircleSides(DetailLevel lod) => lod == DetailLevel.Low ? 6 : lod == DetailLevel.Medium ? 12 : 24;

    /// <summary>Builds and extrudes the mesh for a shape in viewer mode.</summary>
    public static PrimMesh BuildMesh(PrimParams p, DetailLevel lod = DetailLevel.High)
    {
        int sides = 4;
        int hollowSides = 4;
        float profileBegin = p.ProfileBegin;
        float profileEnd = p.ProfileEnd;
        bool isSphere = false;

        switch (p.ProfileCurve & 0x07)
        {
            case 0:
                sides = CircleSides(lod);
                break;
            case 3:
                sides = 3;
                break;
            case 5:
                isSphere = true;
                sides = CircleSides(lod);
                profileBegin = 0.5f * profileBegin + 0.5f;
                profileEnd = 0.5f * profileEnd + 0.5f;
                break;
        }

        switch (p.HoleType)
        {
            case 0: hollowSides = sides; break;
            case 16: hollowSides = CircleSides(lod); break;
            case 48: hollowSides = 3; break;
        }

        PrimMesh mesh = new PrimMesh(sides, profileBegin, profileEnd, p.ProfileHollow, hollowSides)
        {
            viewerMode = true,
            sphereMode = isSphere,
            holeSizeX = p.PathScaleX,
            holeSizeY = p.PathScaleY,
            pathCutBegin = p.PathBegin,
            pathCutEnd = p.PathEnd,
            topShearX = p.PathShearX,
            topShearY = p.PathShearY,
            radius = p.PathRadiusOffset,
            revolutions = p.PathRevolutions,
            skew = p.PathSkew,
            stepsPerRevolution = CircleSides(lod)
        };

        if (p.PathCurve == 16 || p.PathCurve == 128)
        {
            // straight path: the stored scale is a taper, twist is in degrees
            mesh.taperX = 1.0f - p.PathScaleX;
            mesh.taperY = 1.0f - p.PathScaleY;
            mesh.twistBegin = (int)(180 * p.PathTwistBegin);
            mesh.twistEnd = (int)(180 * p.PathTwist);
            mesh.Extrude(PathType.Linear);
        }
        else
        {
            mesh.taperX = p.PathTaperX;
            mesh.taperY = p.PathTaperY;
            mesh.twistBegin = (int)(360 * p.PathTwistBegin);
            mesh.twistEnd = (int)(360 * p.PathTwist);
            mesh.Extrude(PathType.Circular);
        }

        return mesh;
    }

    /// <summary>
    /// The mesher's triangles. Their texture coordinates are in glTF's convention, v = 0 at the top of the image: the top
    /// edge of a side face has v = 0 where the viewer has v = 1 (the viewer's v is 1 minus this). In that convention the
    /// viewer's cap rule (LLVolumeFace::createCap in indra/llmath/llvolume.cpp: u = x + 0.5 and v = y + 0.5 on the top
    /// cap, v = 0.5 - y on the bottom cap, x and y being the profile point) reads u = x + 0.5 with v = 0.5 - y on the top
    /// cap and v = 0.5 + y on the bottom cap, which is what the mesher writes for straight and circular paths alike, so
    /// no coordinate is changed here.
    /// </summary>
    public static List<ViewerFace> BuildViewerFaces(PrimParams p, DetailLevel lod, out int bottomMesherNumber)
    {
        PrimMesh mesh = BuildMesh(p, lod);
        List<ViewerFace> faces = mesh.viewerFaces;
        bottomMesherNumber = -1;
        foreach (ViewerFace f in faces)
            if (f.role == ViewerFaceRole.Bottom)
            {
                bottomMesherNumber = f.primFaceNumber;
                break;
            }
        return faces;
    }

    /// <summary>Shape to faces, one entry per prim face that has area, in face-number order.</summary>
    public static List<PrimFace> BuildFaces(PrimParams p, DetailLevel lod = DetailLevel.High)
    {
        return BuildFaces(p, lod, out _, out _);
    }

    /// <summary>
    /// Shape to faces. The faces a prim has, and their numbers, come from <see cref="ViewerFaceRule"/>; the mesher
    /// supplies the triangles of each. A face the viewer lists but that has no area (the cut faces of a sphere lie on its
    /// axis) is left out and its number stays unused. Triangles the mesher makes that belong to no face of the viewer's
    /// list (the end caps it adds to a sphere with a radius offset) are counted in <paramref name="unnumberedTriangles"/>
    /// and left out.
    /// </summary>
    /// <param name="faceCount">How many faces the viewer gives the prim, including faces with no area.</param>
    public static List<PrimFace> BuildFaces(PrimParams p, DetailLevel lod, out int faceCount, out int unnumberedTriangles)
    {
        List<ViewerFace> viewerFaces = BuildViewerFaces(p, lod, out _);
        List<PrimFaceKind> kinds = ViewerFaceRule.Faces(p);
        faceCount = kinds.Count;

        // triangles with area, by what they belong to; the outer sides of a square or triangle profile by their mesher number
        List<ViewerFace> top = new List<ViewerFace>(), bottom = new List<ViewerFace>(), inner = new List<ViewerFace>();
        List<ViewerFace> cutBegin = new List<ViewerFace>(), cutEnd = new List<ViewerFace>();
        SortedDictionary<int, List<ViewerFace>> outer = new SortedDictionary<int, List<ViewerFace>>();
        bool oneOuterFace = (p.ProfileCurve & 0x0F) == 0 || (p.ProfileCurve & 0x0F) == 5;
        foreach (ViewerFace vf in viewerFaces)
        {
            if (Area(vf) < MinArea)
                continue;
            switch (vf.role)
            {
                case ViewerFaceRole.Top: top.Add(vf); break;
                case ViewerFaceRole.Bottom: bottom.Add(vf); break;
                case ViewerFaceRole.Hollow: inner.Add(vf); break;
                case ViewerFaceRole.CutBegin: cutBegin.Add(vf); break;
                case ViewerFaceRole.CutEnd: cutEnd.Add(vf); break;
                default:
                    int key = oneOuterFace ? 0 : vf.primFaceNumber;
                    if (!outer.TryGetValue(key, out List<ViewerFace> list))
                        outer[key] = list = new List<ViewerFace>();
                    list.Add(vf);
                    break;
            }
        }

        List<List<ViewerFace>> outers = outer.Values.ToList();
        int total = top.Count + bottom.Count + inner.Count + cutBegin.Count + cutEnd.Count + outers.Sum(o => o.Count);
        int used = 0;
        int nextOuter = 0;
        List<PrimFace> result = new List<PrimFace>();
        for (int number = 0; number < kinds.Count; number++)
        {
            PrimFaceKind kind = kinds[number];
            List<ViewerFace> tris = kind switch
            {
                PrimFaceKind.Top => top,
                PrimFaceKind.Bottom => bottom,
                PrimFaceKind.Inner => inner,
                PrimFaceKind.ProfileCutBegin => cutBegin,
                PrimFaceKind.ProfileCutEnd => cutEnd,
                _ => nextOuter < outers.Count ? outers[nextOuter++] : null
            };
            if (tris == null || tris.Count == 0)
                continue;
            used += tris.Count;
            result.Add(MakeFace(number, kind, tris));
        }

        unnumberedTriangles = total - used;
        return result;
    }

    private static PrimFace MakeFace(int number, PrimFaceKind kind, List<ViewerFace> tris)
    {
        PrimFace f = new PrimFace
        {
            Number = number,
            Kind = kind,
            MesherNumber = tris[0].primFaceNumber,
            Positions = new float[tris.Count * 9],
            Normals = new float[tris.Count * 9],
            Uvs = new float[tris.Count * 6]
        };
        for (int i = 0; i < tris.Count; i++)
        {
            ViewerFace t = tris[i];
            Put(f.Positions, i * 9, t.v1, t.v2, t.v3);
            Put(f.Normals, i * 9, t.n1, t.n2, t.n3);
            int u = i * 6;
            f.Uvs[u] = t.uv1.U; f.Uvs[u + 1] = t.uv1.V;
            f.Uvs[u + 2] = t.uv2.U; f.Uvs[u + 3] = t.uv2.V;
            f.Uvs[u + 4] = t.uv3.U; f.Uvs[u + 5] = t.uv3.V;
        }
        return f;
    }

    private static void Put(float[] into, int at, Coord a, Coord b, Coord c)
    {
        into[at] = a.X; into[at + 1] = a.Y; into[at + 2] = a.Z;
        into[at + 3] = b.X; into[at + 4] = b.Y; into[at + 5] = b.Z;
        into[at + 6] = c.X; into[at + 7] = c.Y; into[at + 8] = c.Z;
    }

    // Triangles with no area: the mesher writes zero-width seam strips and, at a sphere's poles, collapsed fans. A
    // float sine of pi is not exactly zero, so "no area" is judged against a size no real triangle of the unit shape reaches.
    private const float MinArea = 1e-6f;

    private static float Area(ViewerFace f)
    {
        float ux = f.v2.X - f.v1.X, uy = f.v2.Y - f.v1.Y, uz = f.v2.Z - f.v1.Z;
        float vx = f.v3.X - f.v1.X, vy = f.v3.Y - f.v1.Y, vz = f.v3.Z - f.v1.Z;
        float cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
        return 0.5f * MathF.Sqrt(cx * cx + cy * cy + cz * cz);
    }
}
