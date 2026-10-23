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
// The face numbering follows the Second Life prim face convention (a texture entry has one slot per prim face);
// the grouping and renumbering below is written for this exporter.

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
    /// <summary>The number the mesher gave the face before empty groups were dropped.</summary>
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
    /// The mesher's end faces exist on a straight path always, and on a circular path when it is cut, tapered, skewed,
    /// twisted or offset (the same test PrimMesh.Extrude makes).
    /// </summary>
    public static bool HasEndFaces(PrimParams p)
    {
        if (p.PathCurve == 16 || p.PathCurve == 128)
            return true;
        int twistBegin = (int)(360 * p.PathTwistBegin), twistEnd = (int)(360 * p.PathTwist);
        return p.PathBegin != 0f || p.PathEnd != 1f || p.PathTaperX != 0f || p.PathTaperY != 0f || p.PathSkew != 0f || twistEnd - twistBegin != 0 || p.PathRadiusOffset != 0f;
    }

    /// <summary>
    /// The mesher's triangles with the bottom end cap's texture coordinates mirrored in V, so the cap reads the right
    /// way up from below, as the browser viewer draws it. The mesher writes the bottom cap first.
    /// </summary>
    public static List<ViewerFace> BuildViewerFaces(PrimParams p, DetailLevel lod, out int bottomMesherNumber)
    {
        PrimMesh mesh = BuildMesh(p, lod);
        List<ViewerFace> faces = mesh.viewerFaces;
        bottomMesherNumber = -1;
        if (faces.Count > 0 && HasEndFaces(p))
        {
            bottomMesherNumber = faces[0].primFaceNumber;
            for (int i = 0; i < faces.Count; i++)
            {
                ViewerFace f = faces[i];
                if (f.primFaceNumber != bottomMesherNumber)
                    continue;
                f.uv1.V = 1f - f.uv1.V;
                f.uv2.V = 1f - f.uv2.V;
                f.uv3.V = 1f - f.uv3.V;
                faces[i] = f;
            }
        }
        return faces;
    }

    /// <summary>Shape to faces, one entry per prim face in face-number order.</summary>
    public static List<PrimFace> BuildFaces(PrimParams p, DetailLevel lod = DetailLevel.High)
    {
        return GroupFaces(BuildViewerFaces(p, lod, out _));
    }

    /// <summary>
    /// Groups the mesher's triangles by its face number, drops triangles with no area, and numbers the remaining groups
    /// 0, 1, 2 ... in the mesher's order. A closed three or four sided profile carries a duplicate closing vertex whose
    /// zero-area quad would otherwise take a face number of its own and push the bottom cap off its texture-entry slot
    /// (a box has 0 top, 1 to 4 the sides, 5 bottom); a sphere carries two such seam groups.
    /// </summary>
    public static List<PrimFace> GroupFaces(List<ViewerFace> viewerFaces)
    {
        SortedDictionary<int, List<ViewerFace>> byFace = new SortedDictionary<int, List<ViewerFace>>();
        foreach (ViewerFace vf in viewerFaces)
        {
            if (Area(vf) < MinArea)
                continue;
            if (!byFace.TryGetValue(vf.primFaceNumber, out List<ViewerFace> list))
                byFace[vf.primFaceNumber] = list = new List<ViewerFace>();
            list.Add(vf);
        }

        List<PrimFace> result = new List<PrimFace>(byFace.Count);
        int number = 0;
        foreach (KeyValuePair<int, List<ViewerFace>> kv in byFace)
        {
            List<ViewerFace> tris = kv.Value;
            PrimFace f = new PrimFace
            {
                Number = number++,
                MesherNumber = kv.Key,
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
            result.Add(f);
        }
        return result;
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
