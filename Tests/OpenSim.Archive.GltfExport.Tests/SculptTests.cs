/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.Json;
using OpenMetaverse;
using OpenSim.Framework;
using SkiaSharp;
using Xunit;
using static OpenSim.Archive.GltfExport.Tests.TestKit;

namespace OpenSim.Archive.GltfExport.Tests;

/// <summary>Sculpted prims: the viewer's rule for turning a sculpt map into a face, checked with maps generated in code.</summary>
public class SculptTests
{
    // ------------------------------------------------------------ maps

    /// <summary>A map from a function of (column, row from the top) to a point in the unit cube [-0.5, 0.5].</summary>
    internal static SculptMap Map(int w, int h, Func<int, int, (double x, double y, double z)> point)
    {
        SculptMap m = new SculptMap { Width = w, Height = h, Rgb = new byte[w * h * 3] };
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                (double x, double y, double z) = point(c, r);
                m.Rgb[(r * w + c) * 3] = (byte)Math.Round((x + 0.5) * 255);
                m.Rgb[(r * w + c) * 3 + 1] = (byte)Math.Round((y + 0.5) * 255);
                m.Rgb[(r * w + c) * 3 + 2] = (byte)Math.Round((z + 0.5) * 255);
            }
        return m;
    }

    /// <summary>A sphere with its north pole in the top row of the picture and longitude increasing to the right.</summary>
    internal static SculptMap SphereMap(int w = 64, int h = 64, bool longitudeToTheLeft = false) => Map(w, h, (c, r) =>
    {
        double theta = Math.PI * r / (h - 1), phi = 2 * Math.PI * c / w * (longitudeToTheLeft ? -1 : 1);
        return (0.45 * Math.Sin(theta) * Math.Cos(phi), 0.45 * Math.Sin(theta) * Math.Sin(phi), 0.45 * Math.Cos(theta));
    });

    /// <summary>A torus with the tube round the picture's rows and the ring round its columns.</summary>
    internal static SculptMap TorusMap(int w = 64, int h = 64) => Map(w, h, (c, r) =>
    {
        double ring = 2 * Math.PI * c / w, tube = 2 * Math.PI * r / h;
        double radius = 0.3 + 0.15 * Math.Cos(tube);
        return (radius * Math.Cos(ring), radius * Math.Sin(ring), 0.15 * Math.Sin(tube));
    });

    /// <summary>A plane: a bumpy sheet, columns along x and rows along y.</summary>
    internal static SculptMap PlaneMap(int w = 64, int h = 64) => Map(w, h, (c, r) => (c / (double)(w - 1) - 0.5, r / (double)(h - 1) - 0.5, 0.2 * Math.Sin(c * 0.3) * Math.Cos(r * 0.2)));

    /// <summary>A cylinder round the picture's columns, rows along the axis.</summary>
    internal static SculptMap CylinderMap(int w = 64, int h = 64) => Map(w, h, (c, r) =>
    {
        double phi = 2 * Math.PI * c / w;
        return (0.4 * Math.Cos(phi), 0.4 * Math.Sin(phi), r / (double)(h - 1) - 0.5);
    });

    private static SculptMap MapFor(int stitching) => stitching switch
    {
        SculptGeometry.TypeSphere => SphereMap(),
        SculptGeometry.TypeTorus => TorusMap(),
        SculptGeometry.TypePlane => PlaneMap(),
        _ => CylinderMap()
    };

    // ------------------------------------------------------------ the rule, written again for the tests

    /// <summary>
    /// The viewer's vertex for grid position (row s, column t) on a map whose sides divide the map's size evenly, written in
    /// whole numbers. Row 0 of the viewer's picture is the bottom row of the picture as the map holds it.
    /// </summary>
    private static (float x, float y, float z, float u, float vUp) Expected(SculptMap m, int type, int rows, int cols, int s, int t)
    {
        int stitching = type & 7;
        bool invert = (type & 0x40) != 0, mirror = (type & 0x80) != 0;
        bool reverse = invert ? !mirror : mirror;
        int rt = reverse ? cols - 1 - t : t;
        int x = rt * m.Width / (cols - 1);
        int y = s * m.Height / (rows - 1);
        if (y == 0 && stitching == 1) x = m.Width / 2;
        if (y == m.Height)
        {
            y = stitching == 2 ? 0 : m.Height - 1;
            if (stitching == 1) x = m.Width / 2;
        }
        if (x == m.Width)
            x = stitching == 1 || stitching == 2 || stitching == 4 ? 0 : m.Width - 1;
        int top = m.Height - 1 - y;
        int o = (top * m.Width + x) * 3;
        float px = m.Rgb[o] / 255f - 0.5f, py = m.Rgb[o + 1] / 255f - 0.5f, pz = m.Rgb[o + 2] / 255f - 0.5f;
        if (mirror) px = -px;
        float u = t / (float)(cols - 1);
        if (reverse) u = 1f - u;
        return (px, py, pz, u, s / (float)(rows - 1));
    }

    private static float Volume(float[] positions)
    {
        double v = 0;
        for (int i = 0; i + 8 < positions.Length; i += 9)
        {
            double ax = positions[i], ay = positions[i + 1], az = positions[i + 2];
            double bx = positions[i + 3], by = positions[i + 4], bz = positions[i + 5];
            double cx = positions[i + 6], cy = positions[i + 7], cz = positions[i + 8];
            v += (ax * (by * cz - bz * cy) - ay * (bx * cz - bz * cx) + az * (bx * cy - by * cx)) / 6.0;
        }
        return (float)v;
    }

    // ------------------------------------------------------------ resolution

    [Theory]
    [InlineData(64, 64, 32, 32)]    // the most the viewer builds: 32 x 32 quads
    [InlineData(256, 256, 32, 32)]
    [InlineData(32, 32, 16, 16)]    // a quarter of the pixels
    [InlineData(16, 16, 8, 8)]
    [InlineData(128, 64, 22, 46)]   // the grid follows the map's shape
    [InlineData(64, 128, 46, 22)]
    public void TheGridHasAsManyVerticesAsTheHighestDetailAndTheMapAllow(int w, int h, int pathSides, int profileSides)
    {
        SculptGeometry.Resolution(w, h, out int s, out int t);
        Assert.Equal((pathSides, profileSides), (s, t));
        SculptResult r = SculptGeometry.Build(SphereMap(w, h), SculptGeometry.TypeSphere);
        Assert.Equal((pathSides + 1, profileSides + 1), (r.Rows, r.Columns));
    }

    // ------------------------------------------------------------ every stitching type, with and without mirror and invert

    [Theory]
    [InlineData(1, 0)] [InlineData(1, 0x40)] [InlineData(1, 0x80)] [InlineData(1, 0xC0)]
    [InlineData(2, 0)] [InlineData(2, 0x40)] [InlineData(2, 0x80)] [InlineData(2, 0xC0)]
    [InlineData(3, 0)] [InlineData(3, 0x40)] [InlineData(3, 0x80)] [InlineData(3, 0xC0)]
    [InlineData(4, 0)] [InlineData(4, 0x40)] [InlineData(4, 0x80)] [InlineData(4, 0xC0)]
    public void TheGridAndItsTextureCoordinatesFollowTheRule(int stitching, int flags)
    {
        int type = stitching | flags;
        SculptMap map = MapFor(stitching);
        SculptResult r = SculptGeometry.Build(map, type);
        Assert.NotNull(r.Face);
        Assert.Equal(0, r.Face.Number);

        // the grid itself
        bool reverse = (type & 0x40) != 0 ? (type & 0x80) == 0 : (type & 0x80) != 0;
        for (int s = 0; s < r.Rows; s++)
            for (int t = 0; t < r.Columns; t++)
            {
                (float x, float y, float z, _, _) = Expected(map, type, r.Rows, r.Columns, s, t);
                int o = (s * r.Columns + t) * 3;
                Assert.Equal(x, r.Grid[o], 5);
                Assert.Equal(y, r.Grid[o + 1], 5);
                Assert.Equal(z, r.Grid[o + 2], 5);
            }
        // every vertex of every triangle is a vertex of that grid, with the rule's texture coordinates (v down in the file)
        for (int i = 0; i < r.Face.Positions.Length / 3; i++)
        {
            float u = r.Face.Uvs[i * 2], vDown = r.Face.Uvs[i * 2 + 1];
            int t = (int)Math.Round((reverse ? 1f - u : u) * (r.Columns - 1));
            int s = (int)Math.Round((1f - vDown) * (r.Rows - 1));
            (float x, float y, float z, float eu, float evUp) = Expected(map, type, r.Rows, r.Columns, s, t);
            Assert.Equal(eu, u, 5);
            Assert.Equal(1f - evUp, vDown, 5);
            Assert.Equal(x, r.Face.Positions[i * 3], 5);
            Assert.Equal(y, r.Face.Positions[i * 3 + 1], 5);
            Assert.Equal(z, r.Face.Positions[i * 3 + 2], 5);
        }
        // two triangles for each quad of the grid, less the ones with no area
        Assert.Equal(2 * (r.Rows - 1) * (r.Columns - 1) - r.DegenerateTrianglesDropped, r.Face.TriangleCount);
    }

    [Fact]
    public void ASphereIsPinchedAtBothEndsAndWrapsAtTheSeam()
    {
        SculptResult r = SculptGeometry.Build(SphereMap(), SculptGeometry.TypeSphere);
        for (int t = 1; t < r.Columns; t++)
        {
            for (int c = 0; c < 3; c++)
            {
                Assert.Equal(r.Grid[c], r.Grid[t * 3 + c]);                                            // first row: one point
                Assert.Equal(r.Grid[(r.Rows - 1) * r.Columns * 3 + c], r.Grid[((r.Rows - 1) * r.Columns + t) * 3 + c]); // last row: one point
            }
        }
        // the viewer's first row is the picture's bottom row: the south pole of this map, the last row its north pole
        Assert.True(r.Grid[2] < -0.4f);
        Assert.True(r.Grid[(r.Rows - 1) * r.Columns * 3 + 2] > 0.4f);
        for (int s = 0; s < r.Rows; s++)
            for (int c = 0; c < 3; c++)
                Assert.Equal(r.Grid[s * r.Columns * 3 + c], r.Grid[(s * r.Columns + r.Columns - 1) * 3 + c]);
    }

    [Fact]
    public void ATorusWrapsInBothDirections()
    {
        SculptResult r = SculptGeometry.Build(TorusMap(), SculptGeometry.TypeTorus);
        for (int t = 0; t < r.Columns; t++)
            for (int c = 0; c < 3; c++)
                Assert.Equal(r.Grid[t * 3 + c], r.Grid[((r.Rows - 1) * r.Columns + t) * 3 + c]);
        for (int s = 0; s < r.Rows; s++)
            for (int c = 0; c < 3; c++)
                Assert.Equal(r.Grid[s * r.Columns * 3 + c], r.Grid[(s * r.Columns + r.Columns - 1) * 3 + c]);
    }

    [Fact]
    public void AnOpenPlaneDoesNotWrapAndACylinderWrapsOnlyAcross()
    {
        SculptResult plane = SculptGeometry.Build(PlaneMap(), SculptGeometry.TypePlane);
        // the last column is the map's last pixel column, not its first
        Assert.NotEqual(plane.Grid[0], plane.Grid[(plane.Columns - 1) * 3]);

        SculptResult cylinder = SculptGeometry.Build(CylinderMap(), SculptGeometry.TypeCylinder);
        for (int s = 0; s < cylinder.Rows; s++)
            for (int c = 0; c < 3; c++)
                Assert.Equal(cylinder.Grid[s * cylinder.Columns * 3 + c], cylinder.Grid[(s * cylinder.Columns + cylinder.Columns - 1) * 3 + c]);
        // not down: the last row is the map's last row, not its first
        Assert.NotEqual(cylinder.Grid[2], cylinder.Grid[(cylinder.Rows - 1) * cylinder.Columns * 3 + 2]);
    }

    [Fact]
    public void MirrorReflectsXAndReadsTheMapBackwardsInvertOnlyReadsItBackwards()
    {
        SculptMap map = CylinderMap();
        SculptResult plain = SculptGeometry.Build(map, SculptGeometry.TypeCylinder);
        SculptResult mirror = SculptGeometry.Build(map, SculptGeometry.TypeCylinder | SculptGeometry.FlagMirror);
        SculptResult invert = SculptGeometry.Build(map, SculptGeometry.TypeCylinder | SculptGeometry.FlagInvert);
        SculptResult both = SculptGeometry.Build(map, SculptGeometry.TypeCylinder | SculptGeometry.FlagMirror | SculptGeometry.FlagInvert);
        int cols = plain.Columns;
        for (int s = 0; s < plain.Rows; s += 5)
            for (int t = 0; t < cols; t++)
            {
                int o = (s * cols + t) * 3, back = (s * cols + (cols - 1 - t)) * 3;
                Assert.Equal(plain.Grid[back], invert.Grid[o]);
                Assert.Equal(plain.Grid[back + 1], invert.Grid[o + 1]);
                Assert.Equal(-plain.Grid[back], mirror.Grid[o]);
                Assert.Equal(plain.Grid[back + 1], mirror.Grid[o + 1]);
                Assert.Equal(-plain.Grid[o], both.Grid[o]);
                Assert.Equal(plain.Grid[o + 1], both.Grid[o + 1]);
            }
    }

    // ------------------------------------------------------------ which way the surface faces

    [Fact]
    public void ASphereMapComesOutFacingOutwardAndInvertTurnsItInsideOut()
    {
        SculptResult normal = SculptGeometry.Build(SphereMap(), SculptGeometry.TypeSphere);
        SculptResult turned = SculptGeometry.Build(SphereMap(), SculptGeometry.TypeSphere | SculptGeometry.FlagInvert);
        SculptResult mirrored = SculptGeometry.Build(SphereMap(), SculptGeometry.TypeSphere | SculptGeometry.FlagMirror);
        // the viewer's triangle order makes this map (longitude increasing to the right, north pole at the top) face one way;
        // invert faces the other, and mirror, which reflects the shape as well, faces as the plain map does
        float volume = Volume(normal.Face.Positions);
        Assert.True(Math.Abs(volume) > 0.2f);
        Assert.Equal(-Math.Sign(volume), Math.Sign(Volume(turned.Face.Positions)));
        Assert.Equal(Math.Sign(volume), Math.Sign(Volume(mirrored.Face.Positions)));
        SculptResult both = SculptGeometry.Build(SphereMap(), SculptGeometry.TypeSphere | SculptGeometry.FlagInvert | SculptGeometry.FlagMirror);
        Assert.Equal(-Math.Sign(volume), Math.Sign(Volume(both.Face.Positions)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheSurfaceFacesTheWayTheRegionsOwnSculptMesherFacesIt(bool longitudeToTheLeft)
    {
        // the region's physics mesher (a reference, not the authority) reads the same picture top row first
        SculptMap map = SphereMap(64, 64, longitudeToTheLeft);
        float ours = Volume(SculptGeometry.Build(map, SculptGeometry.TypeSphere).Face.Positions);

        using SKBitmap bmp = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                bmp.SetPixel(x, y, new SKColor(map.Rgb[(y * 64 + x) * 3], map.Rgb[(y * 64 + x) * 3 + 1], map.Rgb[(y * 64 + x) * 3 + 2]));
        OpenSim.Region.PhysicsModules.Meshing.PrimMesher.SculptMesh reference =
            new OpenSim.Region.PhysicsModules.Meshing.PrimMesher.SculptMesh(bmp, OpenSim.Region.PhysicsModules.Meshing.PrimMesher.SculptMesh.SculptType.sphere, 32, false);
        double v = 0;
        foreach (OpenSim.Region.PhysicsModules.Meshing.PrimMesher.Face f in reference.faces)
        {
            OpenSim.Region.PhysicsModules.Meshing.PrimMesher.Coord a = reference.coords[f.v1], b = reference.coords[f.v2], c = reference.coords[f.v3];
            v += (a.X * (b.Y * c.Z - b.Z * c.Y) - a.Y * (b.X * c.Z - b.Z * c.X) + a.Z * (b.X * c.Y - b.Y * c.X)) / 6.0;
        }
        Assert.True(Math.Abs(v) > 0.1);
        Assert.Equal(Math.Sign(v), Math.Sign(ours));
    }

    [Fact]
    public void NormalsPointAlongTheSurfaceAndMeetAtTheSeams()
    {
        SculptMap map = SphereMap();
        SculptResult r = SculptGeometry.Build(map, SculptGeometry.TypeSphere);
        float sign = Math.Sign(Volume(r.Face.Positions));
        for (int s = 2; s < r.Rows - 2; s++)
            for (int t = 0; t < r.Columns; t++)
            {
                int o = (s * r.Columns + t) * 3;
                float len = MathF.Sqrt(r.Grid[o] * r.Grid[o] + r.Grid[o + 1] * r.Grid[o + 1] + r.Grid[o + 2] * r.Grid[o + 2]);
                float dot = (r.GridNormals[o] * r.Grid[o] + r.GridNormals[o + 1] * r.Grid[o + 1] + r.GridNormals[o + 2] * r.Grid[o + 2]) / len;
                Assert.True(dot * sign > 0.95f, "row " + s + " column " + t + " dot " + dot);
            }
        // the first and last column are one point, so they share a normal
        for (int s = 0; s < r.Rows; s++)
            for (int c = 0; c < 3; c++)
                Assert.Equal(r.GridNormals[s * r.Columns * 3 + c], r.GridNormals[(s * r.Columns + r.Columns - 1) * 3 + c], 5);
        // the pole rows share one averaged normal, and it lies on the axis
        Assert.Equal(r.GridNormals[0], r.GridNormals[3 * 5], 5);
        Assert.True(Math.Abs(r.GridNormals[2]) > 0.99f);
    }

    [Fact]
    public void AMapWithNoVariationIsWhatTheViewerShowsAsAPlaceholder()
    {
        SculptMap flat = Map(64, 64, (c, r) => (0.1, 0.1, 0.1));
        SculptResult r = SculptGeometry.Build(flat, SculptGeometry.TypeSphere);
        Assert.Null(r.Face);
        Assert.Contains("placeholder", r.Problem);
        Assert.Null(SculptGeometry.Build(null, 1).Face);
    }

    // ------------------------------------------------------------ in an archive

    private static readonly UUID MapId = UUID.Parse("a0000000-0000-4000-8000-0000000000b1");

    private static byte[] J2k(SculptMap m)
    {
        using SKBitmap bmp = new SKBitmap(new SKImageInfo(m.Width, m.Height, SKColorType.Rgb888x, SKAlphaType.Opaque));
        for (int y = 0; y < m.Height; y++)
            for (int x = 0; x < m.Width; x++)
                bmp.SetPixel(x, y, new SKColor(m.Rgb[(y * m.Width + x) * 3], m.Rgb[(y * m.Width + x) * 3 + 1], m.Rgb[(y * m.Width + x) * 3 + 2]));
        Assert.True(SkiaImageUtils.TryEncodeToJ2KLossless(bmp, out byte[] encoded));
        return encoded;
    }

    private static (ExportSummary summary, Glb glb, JsonElement manifest, string folder, byte[] j2k) Export(int sculptType, bool withMap, Primitive.TextureEntry te = null)
    {
        SculptMap map = SphereMap();
        byte[] j2k = J2k(map);
        PartSpec root = new PartSpec
        {
            Name = "Sculpt", Scale = new Vector3(2, 3, 4),
            Shape = Box(profileCurve: 0, pathCurve: 32, sculptType: sculptType, sculptTexture: MapId.ToString(), sculptEntry: true),
            TextureEntryBase64 = te == null ? "" : Convert.ToBase64String(te.GetBytes())
        };
        string dir = NewFolder();
        string file = Path.Combine(dir, "sculpt.oar");
        File.WriteAllBytes(file, OarWithAssets(new[] { ("sculpt", ObjectXml(root)) },
            withMap ? new[] { (MapId, (sbyte)AssetType.Texture, j2k) } : Array.Empty<(UUID, sbyte, byte[])>()));
        ExportSummary s = ArchiveExporter.Export(file, new ExportOptions { OutputDirectory = Path.Combine(dir, "out") });
        SummaryBuild b = Assert.Single(s.BuildList);
        string folder = Path.Combine(dir, "out", b.Folder);
        Glb glb = b.File == null ? null : ReadGlb(File.ReadAllBytes(Path.Combine(folder, b.File)));
        return (s, glb, JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json"))).RootElement, folder, j2k);
    }

    [Fact]
    public void ASculptPartIsExportedAsOneFaceScaledByItsSizeWithTheMapCopiedUnchanged()
    {
        (ExportSummary s, Glb glb, JsonElement manifest, string folder, byte[] j2k) = Export(SculptGeometry.TypeSphere, true);
        Assert.Equal((1, 0, 1), (s.Exported, s.Placeheld, s.SculptsExported));

        JsonElement part = manifest.GetProperty("parts")[0];
        Assert.Equal("sculpt", part.GetProperty("kind").GetString());
        Assert.Equal("exported", part.GetProperty("status").GetString());
        Assert.Equal(1, part.GetProperty("faces").GetInt32());

        JsonElement mesh = glb.Root.GetProperty("meshes")[0];
        Assert.Single(mesh.GetProperty("primitives").EnumerateArray());
        Assert.Equal(0, mesh.GetProperty("primitives")[0].GetProperty("extras").GetProperty("primFace").GetInt32());
        Assert.Equal(1, mesh.GetProperty("extras").GetProperty("primFaceCount").GetInt32());

        // size 2 x 3 x 4 scales the unit cube: the sphere map spans about 0.9 of it, and y and z swap into glTF's axes
        JsonElement acc = glb.Root.GetProperty("accessors")[mesh.GetProperty("primitives")[0].GetProperty("attributes").GetProperty("POSITION").GetInt32()];
        float[] max = acc.GetProperty("max").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        Assert.Equal(0.45f * 2, max[0], 1);
        Assert.Equal(0.45f * 4, max[1], 1);
        Assert.Equal(0.45f * 3, max[2], 1);

        JsonElement info = glb.Node(1).GetProperty("extras").GetProperty("sculpt");
        Assert.Equal("Sphere", info.GetProperty("stitching").GetString());
        Assert.Equal(33, info.GetProperty("gridRows").GetInt32());
        Assert.Equal(64, info.GetProperty("mapWidth").GetInt32());

        Assert.Equal(j2k, File.ReadAllBytes(Path.Combine(folder, "textures", MapId + ".jp2")));
        JsonElement row = manifest.GetProperty("textures").EnumerateArray().Single(t => t.GetProperty("role").GetString() == "sculpt");
        Assert.Equal("copied", row.GetProperty("status").GetString());
        Assert.Equal("textures/" + MapId + ".jp2", row.GetProperty("copiedTo").GetString());
        Assert.Equal(MapId.ToString(), part.GetProperty("geometryAssetId").GetString());
    }

    [Fact]
    public void ASculptsFaceTakesItsMaterialFromTheTextureEntry()
    {
        UUID texture = UUID.Parse("a0000000-0000-4000-8000-0000000000c1");
        Primitive.TextureEntry te = new Primitive.TextureEntry(texture);
        te.DefaultTexture.RGBA = new Color4(1f, 0f, 0f, 1f);
        (_, Glb glb, _, _, _) = Export(SculptGeometry.TypeSphere, true, te);
        JsonElement m = glb.Root.GetProperty("materials")[glb.Root.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("material").GetInt32()];
        Assert.Equal(texture.ToString(), m.GetProperty("extras").GetProperty("textureId").GetString());
    }

    [Fact]
    public void AMissingSculptMapLeavesThePartPlaceheldWithTheReason()
    {
        (ExportSummary s, Glb glb, JsonElement manifest, string folder, _) = Export(SculptGeometry.TypeSphere, false);
        Assert.Equal((0, 1, 0), (s.Exported, s.Placeheld, s.SculptsExported));
        JsonElement part = manifest.GetProperty("parts")[0];
        Assert.Equal("placeheld", part.GetProperty("status").GetString());
        Assert.Contains(MapId.ToString(), part.GetProperty("reason").GetString());
        Assert.Contains("not in the archive", part.GetProperty("reason").GetString());
        Assert.Equal("missing-from-archive", part.GetProperty("geometryAssetStatus").GetString());
        JsonElement node = glb.Node(1).GetProperty("extras");
        Assert.Equal("pending", node.GetProperty("geometry").GetString());
        Assert.Contains("not in the archive", node.GetProperty("geometryPending").GetString());
        Assert.Equal(1, s.TexturesMissing);
        Assert.False(Directory.Exists(Path.Combine(folder, "textures")));
    }

    [Fact]
    public void AnUndecodableSculptMapLeavesThePartPlaceheld()
    {
        string dir = NewFolder();
        string file = Path.Combine(dir, "bad.oar");
        PartSpec root = new PartSpec { Name = "Sculpt", Shape = Box(sculptType: 1, sculptTexture: MapId.ToString(), sculptEntry: true) };
        File.WriteAllBytes(file, OarWithAssets(new[] { ("sculpt", ObjectXml(root)) }, new[] { (MapId, (sbyte)AssetType.Texture, new byte[] { 1, 2, 3, 4 }) }));
        ExportSummary s = ArchiveExporter.Export(file, new ExportOptions { OutputDirectory = Path.Combine(dir, "out") });
        Assert.Equal((0, 1), (s.Exported, s.Placeheld));
        Assert.Contains("undecodable", s.Reasons.Keys.Single());
    }

    [Fact]
    public void TheMirrorAndInvertFlagsAreRecordedAndChangeTheGeometry()
    {
        (_, Glb plain, _, _, _) = Export(SculptGeometry.TypeSphere, true);
        (_, Glb turned, _, _, _) = Export(SculptGeometry.TypeSphere | SculptGeometry.FlagInvert, true);
        JsonElement info = turned.Node(1).GetProperty("extras").GetProperty("sculpt");
        Assert.True(info.GetProperty("invert").GetBoolean());
        Assert.False(info.GetProperty("mirror").GetBoolean());
        float[] a = plain.Floats(plain.Root.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes").GetProperty("NORMAL").GetInt32());
        float[] b = turned.Floats(turned.Root.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes").GetProperty("NORMAL").GetInt32());
        Assert.NotEqual(a, b);
    }
}
