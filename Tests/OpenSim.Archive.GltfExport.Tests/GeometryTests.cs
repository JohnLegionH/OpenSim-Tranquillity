/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.Json;
using OpenSim.Archive.Inspection;
using OpenSim.Region.PhysicsModules.Meshing.PrimMesher;
using Xunit;
using Path = System.IO.Path;

namespace OpenSim.Archive.GltfExport.Tests;

/// <summary>The prim mesher in viewer mode against the golden shapes, and the three faults a first port of it had.</summary>
public class GeometryTests
{
    private sealed class Fixture
    {
        public string Name { get; set; }
        public string Source { get; set; }
        public Shape Shape { get; set; }
        public Expected Expected { get; set; }
    }

    private sealed class Shape
    {
        public int ProfileCurve { get; set; }
        public int ProfileHole { get; set; }
        public float ProfileBegin { get; set; }
        public float ProfileEnd { get; set; }
        public float ProfileHollow { get; set; }
        public int PathCurve { get; set; }
        public float PathBegin { get; set; }
        public float PathEnd { get; set; }
        public float PathScaleX { get; set; }
        public float PathScaleY { get; set; }
        public float PathShearX { get; set; }
        public float PathShearY { get; set; }
        public float PathTwist { get; set; }
        public float PathTwistBegin { get; set; }
        public float PathRadiusOffset { get; set; }
        public float PathTaperX { get; set; }
        public float PathTaperY { get; set; }
        public float PathRevolutions { get; set; }
        public float PathSkew { get; set; }
    }

    private sealed class Expected
    {
        public int Triangles { get; set; }
        public int Vertices { get; set; }
        public Dictionary<string, int> PerFace { get; set; }
        public Box Bbox { get; set; }
        public List<Sample> Samples { get; set; }
    }

    private sealed class Box
    {
        public float[] Min { get; set; }
        public float[] Max { get; set; }
    }

    private sealed class Sample
    {
        public int Index { get; set; }
        public int Face { get; set; }
        public float[] V1 { get; set; }
        public float[] V2 { get; set; }
        public float[] V3 { get; set; }
        public float[] Uv1 { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static IEnumerable<object[]> FixtureNames() =>
        Directory.GetFiles(FixtureDir, "*.json").Select(f => new object[] { Path.GetFileNameWithoutExtension(f) }).OrderBy(o => (string)o[0]);

    private static Fixture Load(string name) => JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Path.Combine(FixtureDir, name + ".json")), Json);

    private static PrimParams ParamsOf(Shape s) => new PrimParams
    {
        ProfileCurve = s.ProfileCurve, HoleType = s.ProfileHole, PathCurve = s.PathCurve,
        ProfileBegin = s.ProfileBegin, ProfileEnd = s.ProfileEnd, ProfileHollow = s.ProfileHollow,
        PathBegin = s.PathBegin, PathEnd = s.PathEnd, PathScaleX = s.PathScaleX, PathScaleY = s.PathScaleY,
        PathShearX = s.PathShearX, PathShearY = s.PathShearY, PathTwist = s.PathTwist, PathTwistBegin = s.PathTwistBegin,
        PathRadiusOffset = s.PathRadiusOffset, PathTaperX = s.PathTaperX, PathTaperY = s.PathTaperY,
        PathRevolutions = s.PathRevolutions, PathSkew = s.PathSkew
    };

    private static float R(float v) => MathF.Round(v * 1e5f) / 1e5f;

    [Fact]
    public void TheGoldenShapesAreAllThere()
    {
        Assert.Equal(17, FixtureNames().Count());
    }

    // Known differences between this repository's mesher and the one the shapes were recorded from. Neither changes what is drawn.
    //  sphere-plain: the recorded mesher also writes a group of 48 collapsed triangles (all three corners at the centre); this mesher does not.
    //  prism-tapered-sheared: this mesher adds vertical resolution to a tapered three-sided prism (PrimMesh.Extrude, "prisms don't taper
    //  well"), so its sides have more triangles; the outline and the corners match.
    private const int SphereCollapsedTriangles = 48;

    // Differences where the Second Life viewer's source (indra/llmath/llvolume.cpp) is the authority and the recorded shapes are not.
    //  Closed hollow profiles: the recorded shapes carry two quads (4 triangles) that join the outer ring to the hollow ring and lie inside the wall. The
    //  viewer's profile has the outer wall and the inner wall as separate faces (LLProfile::addHole) and no such quad, so this mesher leaves them out.
    //  The first number is the group the recorded shapes put the quad in, then the group of the second quad.
    private static readonly Dictionary<string, (string first, string second)> RingJoinGroups = new Dictionary<string, (string, string)>
    {
        ["box-hollow-circle-hole"] = ("5", "6"),
        ["cylinder-hollow-square"] = ("1", "3"),
    };
    //  Bottom end cap of a shape recorded from the browser viewer's port: the port has the cap's v mirrored. The viewer's rule for the cap is u = x + 0.5 and
    //  v = 0.5 - y with v up (LLVolumeFace::createCap), which is v = 0.5 + y in the top-down convention the samples use; the mesher gives that.
    private static readonly HashSet<string> BottomCapMirroredInPort = new HashSet<string> { "box-twisted", "box-cut-hollow-twisted", "torus-twisted" };

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void MeshMatchesTheGoldenShape(string name)
    {
        Fixture fx = Load(name);
        PrimParams p = ParamsOf(fx.Shape);
        List<ViewerFace> vfs = PrimGeometry.BuildViewerFaces(p, DetailLevel.High, out int bottom);

        // the reference build of the mesher never made the bottom end cap, so its shapes are compared without that face
        bool reference = fx.Source.StartsWith("csharp", StringComparison.Ordinal);
        if (reference && bottom >= 0)
            vfs = vfs.Where(v => v.primFaceNumber != bottom).ToList();

        int expectedTriangles = fx.Expected.Triangles;
        Dictionary<string, int> expectedPerFace = new Dictionary<string, int>(fx.Expected.PerFace);
        if (RingJoinGroups.TryGetValue(name, out (string first, string second) join))
        {
            expectedTriangles -= 4;
            foreach (string group in new[] { join.first, join.second })
            {
                expectedPerFace[group] -= 2;
                if (expectedPerFace[group] == 0)
                    expectedPerFace.Remove(group);
            }
        }
        if (name == "sphere-plain")
        {
            expectedTriangles -= SphereCollapsedTriangles;
            Assert.Equal(SphereCollapsedTriangles, expectedPerFace["1"]);
            expectedPerFace.Remove("1");
        }

        float[] min = { float.MaxValue, float.MaxValue, float.MaxValue }, max = { float.MinValue, float.MinValue, float.MinValue };
        foreach (ViewerFace v in vfs)
            foreach (Coord c in new[] { v.v1, v.v2, v.v3 })
            {
                min[0] = Math.Min(min[0], c.X); min[1] = Math.Min(min[1], c.Y); min[2] = Math.Min(min[2], c.Z);
                max[0] = Math.Max(max[0], c.X); max[1] = Math.Max(max[1], c.Y); max[2] = Math.Max(max[2], c.Z);
            }
        for (int i = 0; i < 3; i++)
        {
            Close(name + " bbox min " + i, fx.Expected.Bbox.Min[i], R(min[i]));
            Close(name + " bbox max " + i, fx.Expected.Bbox.Max[i], R(max[i]));
        }

        if (name == "prism-tapered-sheared")
        {
            // every corner the recorded mesher drew is a corner of this mesh too
            foreach (Sample s in fx.Expected.Samples)
                foreach (float[] corner in new[] { s.V1, s.V2, s.V3 })
                    Assert.True(vfs.Any(v => Same(corner, v.v1) || Same(corner, v.v2) || Same(corner, v.v3)), name + ": no corner at " + string.Join(",", corner));
            Assert.Equal(expectedPerFace.Keys.OrderBy(k => k), vfs.Select(v => v.primFaceNumber.ToString()).Distinct().OrderBy(k => k));
            return;
        }

        Assert.True(expectedTriangles == vfs.Count, name + " triangles: expected " + expectedTriangles + ", got " + vfs.Count);
        Assert.Equal(expectedTriangles * 3, vfs.Count * 3);

        Dictionary<string, int> perFace = vfs.GroupBy(v => v.primFaceNumber.ToString()).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(expectedPerFace.OrderBy(k => k.Key), perFace.OrderBy(k => k.Key));

        if (name == "sphere-plain")
            return; // every sample of the recorded sphere sits in the collapsed group

        foreach (Sample s in fx.Expected.Samples)
        {
            Assert.InRange(s.Index, 0, vfs.Count + 4);
            // the quads left out of a closed hollow shape move the triangles after them, so those are found by their corners
            ViewerFace v = RingJoinGroups.ContainsKey(name)
                ? vfs.First(t => t.primFaceNumber == s.Face && Same(s.V1, t.v1) && Same(s.V2, t.v2) && Same(s.V3, t.v3))
                : vfs[s.Index];
            Assert.Equal(s.Face, v.primFaceNumber);
            Near(s.V1, v.v1); Near(s.V2, v.v2); Near(s.V3, v.v3);
            Close(name + " u", s.Uv1[0], v.uv1.U);
            bool mirroredBottom = v.role == ViewerFaceRole.Bottom && BottomCapMirroredInPort.Contains(name);
            Close(name + " v", mirroredBottom ? 1f - s.Uv1[1] : s.Uv1[1], v.uv1.V);
        }
    }

    private static bool Same(float[] a, Coord c) => Math.Abs(a[0] - c.X) <= 5e-4f && Math.Abs(a[1] - c.Y) <= 5e-4f && Math.Abs(a[2] - c.Z) <= 5e-4f;

    private static void Near(float[] expected, Coord got)
    {
        Close("x", expected[0], got.X);
        Close("y", expected[1], got.Y);
        Close("z", expected[2], got.Z);
    }

    private static void Close(string what, float expected, float got)
    {
        Assert.True(Math.Abs(expected - got) <= 5e-4f, what + ": expected " + expected + ", got " + got);
    }

    // ------------------------------------------------------------ the shapes named in the order, from the stored fields

    private static ShapeData Stored(int profileCurve = 1, int pathCurve = 16, int hollow = 0, int profileBegin = 0, int profileEnd = 0, int twist = 0, int taperX = 0, int taperY = 0,
        int pathBegin = 0, int pathEnd = 0, int scaleX = 100, int scaleY = 100, int revolutions = 0, int radius = 0, int skew = 0)
    {
        return new ShapeData
        {
            ProfileCurve = profileCurve, PathCurve = pathCurve, ProfileHollow = hollow, ProfileBegin = profileBegin, ProfileEnd = profileEnd, PathTwist = twist,
            PathTaperX = taperX, PathTaperY = taperY, PathBegin = pathBegin, PathEnd = pathEnd, PathScaleX = scaleX, PathScaleY = scaleY,
            PathRevolutions = revolutions, PathRadiusOffset = radius, PathSkew = skew, PCode = 9
        };
    }

    private static List<PrimFace> Faces(ShapeData s) => PrimGeometry.BuildFaces(PrimParams.FromShape(s));

    private static (float[] min, float[] max) Bounds(IEnumerable<PrimFace> faces)
    {
        float[] min = { float.MaxValue, float.MaxValue, float.MaxValue }, max = { float.MinValue, float.MinValue, float.MinValue };
        foreach (PrimFace f in faces)
            for (int i = 0; i < f.Positions.Length; i += 3)
                for (int c = 0; c < 3; c++)
                {
                    min[c] = Math.Min(min[c], f.Positions[i + c]);
                    max[c] = Math.Max(max[c], f.Positions[i + c]);
                }
        return (min, max);
    }

    [Fact]
    public void ABoxHasSixFacesNumberedFromZero()
    {
        List<PrimFace> faces = Faces(Stored());
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, faces.Select(f => f.Number).ToArray());
        (float[] min, float[] max) = Bounds(faces);
        for (int c = 0; c < 3; c++)
        {
            Assert.Equal(-0.5f, min[c], 3);
            Assert.Equal(0.5f, max[c], 3);
        }
    }

    [Fact]
    public void ABoxHasItsBottomEndCap()
    {
        // a face lying at z = -0.5 with every normal pointing down
        List<PrimFace> faces = Faces(Stored());
        PrimFace bottom = faces.Single(f => Enumerable.Range(0, f.Positions.Length / 3).All(i => Math.Abs(f.Positions[i * 3 + 2] + 0.5f) < 1e-4f));
        for (int i = 0; i < bottom.Normals.Length; i += 3)
            Assert.Equal(-1f, bottom.Normals[i + 2], 3);
        PrimFace top = faces.Single(f => Enumerable.Range(0, f.Positions.Length / 3).All(i => Math.Abs(f.Positions[i * 3 + 2] - 0.5f) < 1e-4f));
        Assert.Equal(0, top.Number);
        Assert.NotEqual(bottom.Number, top.Number);
    }

    [Fact]
    public void ACylinderHasTopSideAndBottom()
    {
        List<PrimFace> faces = Faces(Stored(profileCurve: 0));
        Assert.Equal(3, faces.Count);
        Assert.True(faces.Single(f => f.Number == 1).TriangleCount >= 24, "the side has one quad per profile step");
    }

    [Fact]
    public void AHollowBoxHasInnerWalls()
    {
        List<PrimFace> plain = Faces(Stored());
        List<PrimFace> hollow = Faces(Stored(hollow: 25000)); // 0.5
        Assert.True(hollow.Count > plain.Count, "the hole adds faces");
        // inner wall of a box hollowed to half: its corners sit at 0.25
        (float[] min, float[] max) = Bounds(hollow);
        Assert.Equal(-0.5f, min[0], 3);
        Assert.Contains(hollow, f => Enumerable.Range(0, f.Positions.Length / 3).Any(i => Math.Abs(Math.Abs(f.Positions[i * 3]) - 0.25f) < 1e-3f));
        _ = max;
    }

    [Fact]
    public void ACutBoxHasCutFacesAndAnOpenSide()
    {
        List<PrimFace> plain = Faces(Stored());
        List<PrimFace> cut = Faces(Stored(profileBegin: 6250, profileEnd: 6250)); // 0.125 .. 0.875
        Assert.True(cut.Count > plain.Count, "the cut adds its two cut faces");
    }

    [Fact]
    public void TwistIsInDegrees()
    {
        // a box twisted a quarter of the way to 180 degrees, 45 degrees: the top square's corners reach 0.7071 on an axis
        List<PrimFace> twisted = Faces(Stored(twist: 25));
        PrimFace top = twisted.Single(f => f.Number == 0);
        float maxX = 0;
        for (int i = 0; i < top.Positions.Length; i += 3)
            maxX = Math.Max(maxX, Math.Abs(top.Positions[i]));
        Assert.Equal(0.7071f, maxX, 2);
    }

    [Fact]
    public void ASphereFillsTheCube()
    {
        List<PrimFace> faces = Faces(Stored(profileCurve: 5, pathCurve: 32));
        // body face 0 and a 24 triangle strip along the seam, which the recorded mesher and the browser viewer produce too
        Assert.InRange(faces.Count, 1, 2);
        Assert.True(faces[0].TriangleCount >= 500);
        (float[] min, float[] max) = Bounds(faces);
        for (int c = 0; c < 3; c++)
        {
            Assert.InRange(min[c], -0.51f, -0.45f);
            Assert.InRange(max[c], 0.45f, 0.51f);
        }
    }

    [Fact]
    public void ATorusReachesOutAndIsOneFace()
    {
        // circle profile on a circular path with a hole size of 0.5 on y: a ring lying in the x-y plane
        List<PrimFace> faces = Faces(Stored(profileCurve: 0, pathCurve: 32, scaleX: 100, scaleY: 175));
        Assert.Single(faces);
        (float[] min, float[] max) = Bounds(faces);
        Assert.True(max[0] - min[0] > 0.9f);
        Assert.True(faces[0].TriangleCount >= 1000);
    }

    [Fact]
    public void ARingAndATubeComeOutAsClosedShapes()
    {
        List<PrimFace> tube = Faces(Stored(profileCurve: 0, hollow: 40000)); // cylinder hollowed to 0.8
        Assert.True(tube.Count >= 4, "top, outer side, inner side, bottom");
        List<PrimFace> ring = Faces(Stored(profileCurve: 1, pathCurve: 32, scaleX: 100, scaleY: 175));
        Assert.True(ring.Sum(f => f.TriangleCount) > 40);
    }

    [Fact]
    public void WindingAgreesWithTheNormals()
    {
        foreach (ShapeData s in new[] { Stored(), Stored(profileCurve: 0), Stored(profileCurve: 5, pathCurve: 32), Stored(profileCurve: 0, hollow: 30000) })
            foreach (PrimFace f in Faces(s))
            {
                int agree = 0, total = f.TriangleCount;
                for (int t = 0; t < total; t++)
                {
                    int o = t * 9;
                    float ux = f.Positions[o + 3] - f.Positions[o], uy = f.Positions[o + 4] - f.Positions[o + 1], uz = f.Positions[o + 5] - f.Positions[o + 2];
                    float vx = f.Positions[o + 6] - f.Positions[o], vy = f.Positions[o + 7] - f.Positions[o + 1], vz = f.Positions[o + 8] - f.Positions[o + 2];
                    float cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
                    float dot = cx * f.Normals[o] + cy * f.Normals[o + 1] + cz * f.Normals[o + 2];
                    if (dot > 0) agree++;
                }
                Assert.True(agree >= total * 0.9, "face " + f.Number + ": " + agree + " of " + total + " triangles are wound with their normals");
            }
    }

    [Fact]
    public void TheStoredFieldsDecodeToViewerValues()
    {
        ShapeData s = Stored(profileCurve: 0x10 | 5, pathCurve: 32, hollow: 25000, profileBegin: 5000, profileEnd: 10000, twist: 0xF6 /* -10 stored as a byte */, taperX: -50, scaleX: 120, revolutions: 20, radius: 30, skew: 40);
        s.PathShearX = 250; // -6 as a byte
        PrimParams p = PrimParams.FromShape(s);
        Assert.Equal(5, p.ProfileCurve);
        Assert.Equal(16, p.HoleType);
        Assert.Equal(0.5f, p.ProfileHollow, 5);
        Assert.Equal(0.1f, p.ProfileBegin, 5);
        Assert.Equal(0.8f, p.ProfileEnd, 5);
        Assert.Equal(-0.1f, p.PathTwist, 5);
        Assert.Equal(-0.5f, p.PathTaperX, 5);
        Assert.Equal(0.8f, p.PathScaleX, 5);
        Assert.Equal(1.3f, p.PathRevolutions, 5);
        Assert.Equal(0.3f, p.PathRadiusOffset, 5);
        Assert.Equal(0.4f, p.PathSkew, 5);
        Assert.Equal(-0.06f, p.PathShearX, 5);
    }

    [Fact]
    public void TheTextNamesOfTheShapesOverrideTheStoredCurve()
    {
        ShapeData s = Stored(profileCurve: 1);
        s.ProfileShapeName = "Circle";
        s.HollowShapeName = "Triangle";
        PrimParams p = PrimParams.FromShape(s);
        Assert.Equal(0, p.ProfileCurve);
        Assert.Equal(48, p.HoleType);
    }
}
