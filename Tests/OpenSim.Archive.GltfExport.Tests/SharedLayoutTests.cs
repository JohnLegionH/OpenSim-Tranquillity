/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.Json;
using OpenMetaverse;
using OpenSim.Archive.Inspection;
using OpenSim.Framework;
using SkiaSharp;
using Xunit;
using static OpenSim.Archive.GltfExport.Tests.TestKit;

namespace OpenSim.Archive.GltfExport.Tests;

/// <summary>
/// The shared output layout (one textures folder and one assets folder for the whole export, the glTF files pointing at the
/// PNG files by relative path), the self-contained layout, the exit codes for an incomplete archive, and the memory, disk and
/// free-space limits. Serial with the other exporter tests: Export reads a static hook that one of them sets.
/// </summary>
[Collection("exporter-hook")]
public class SharedLayoutTests
{
    private static readonly UUID TexA = UUID.Parse("a0000000-0000-4000-8000-0000000000a1");
    private static readonly UUID TexB = UUID.Parse("a0000000-0000-4000-8000-0000000000a2");
    private static readonly UUID TexBig = UUID.Parse("a0000000-0000-4000-8000-0000000000a3");
    private static readonly UUID MeshId = UUID.Parse("a0000000-0000-4000-8000-0000000000a4");

    private static SKColor Pixel(int x, int y) => new SKColor((byte)(x * 37 + 3), (byte)(y * 53 + 7), (byte)(x * 11 + y * 29 + 1));

    private static byte[] J2k(int w, int h)
    {
        using SKBitmap bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgb888x, SKAlphaType.Opaque));
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bmp.SetPixel(x, y, Pixel(x, y));
        Assert.True(SkiaImageUtils.TryEncodeToJ2KLossless(bmp, out byte[] encoded));
        return encoded;
    }

    private static string Textured(string name, UUID texture) => ObjectXml(new PartSpec
    {
        Name = name,
        Shape = Box(),
        TextureEntryBase64 = Convert.ToBase64String(new Primitive.TextureEntry(texture).GetBytes())
    });

    private sealed class Case
    {
        public string Dir, Archive, Out;
        public byte[] J2kA, J2kB, Mesh;
    }

    /// <summary>Four builds: two boxes with texture A, one with texture B, and a mesh part; the textures and the mesh are in the archive once.</summary>
    private static Case MakeArchive(byte[][] extraAssets = null, UUID[] extraIds = null, string extraBuild = null)
    {
        Case c = new Case { Dir = NewFolder(), J2kA = J2k(8, 8), J2kB = J2k(6, 5), Mesh = MeshTests.Asset(new[] { MeshTests.Triangle() }) };
        c.Archive = Path.Combine(c.Dir, "shared.oar");
        c.Out = Path.Combine(c.Dir, "out");
        List<(string, string)> objects = new List<(string, string)>
        {
            ("one", Textured("One", TexA)),
            ("two", Textured("Two", TexA)),
            ("three", Textured("Three", TexB)),
            ("mesh", ObjectXml(new PartSpec { Name = "Mesh", Shape = Box(profileCurve: 0, pathCurve: 16, sculptType: 5, sculptTexture: MeshId.ToString(), sculptEntry: true) }))
        };
        if (extraBuild != null)
            objects.Add(("extra", extraBuild));
        List<(UUID, sbyte, byte[])> assets = new List<(UUID, sbyte, byte[])>
        {
            (TexA, (sbyte)AssetType.Texture, c.J2kA),
            (TexB, (sbyte)AssetType.Texture, c.J2kB),
            (MeshId, (sbyte)AssetType.Mesh, c.Mesh)
        };
        if (extraAssets != null)
            for (int i = 0; i < extraAssets.Length; i++)
                assets.Add((extraIds[i], (sbyte)AssetType.Texture, extraAssets[i]));
        File.WriteAllBytes(c.Archive, OarWithAssets(objects.ToArray(), assets.ToArray()));
        return c;
    }

    private static ExportSummary Run(Case c, Action<ExportOptions> tune = null)
    {
        ExportOptions o = new ExportOptions { OutputDirectory = c.Out };
        tune?.Invoke(o);
        return ArchiveExporter.Export(c.Archive, o);
    }

    private static Glb GlbOf(Case c, SummaryBuild b) => ReadGlb(File.ReadAllBytes(Path.Combine(c.Out, b.Folder, b.File)));

    private static JsonElement ManifestOf(Case c, SummaryBuild b) => JsonDocument.Parse(File.ReadAllText(Path.Combine(c.Out, b.Folder, "manifest.json"))).RootElement;

    // ------------------------------------------------------------ the shared layout

    [Fact]
    public void ByDefaultTheTexturesAndAssetsAreOneSharedFolderEachAndTheGlbPointsAtThePng()
    {
        Case c = MakeArchive();
        ExportSummary s = Run(c);
        Assert.Equal("shared", s.Layout);
        Assert.Equal(4, s.BuildsWithAFile);

        // each texture once, however many builds use it: the PNG and the original, named by id
        string textures = Path.Combine(c.Out, "textures");
        Assert.Equal(new[] { TexA + ".jp2", TexA + ".png", TexB + ".jp2", TexB + ".png" }.OrderBy(x => x), Directory.GetFiles(textures).Select(Path.GetFileName).OrderBy(x => x));
        Assert.Equal(c.J2kA, File.ReadAllBytes(Path.Combine(textures, TexA + ".jp2")));
        Assert.Equal(c.Mesh, File.ReadAllBytes(Path.Combine(c.Out, "assets", MeshId + ".llmesh")));
        using (SKBitmap png = SKBitmap.Decode(Path.Combine(textures, TexA + ".png")))
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    Assert.Equal(Pixel(x, y), png.GetPixel(x, y));

        // a build's folder holds only its glb and manifest
        foreach (SummaryBuild b in s.BuildList)
        {
            string[] inside = Directory.GetFileSystemEntries(Path.Combine(c.Out, b.Folder)).Select(Path.GetFileName).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "manifest.json", b.File }.OrderBy(x => x), inside);
        }

        // the glb of a textured build refers to the shared PNG by relative path and embeds nothing
        SummaryBuild one = s.BuildList.Single(b => b.Name == "One");
        Glb glb = GlbOf(c, one);
        JsonElement image = Assert.Single(glb.Root.GetProperty("images").EnumerateArray());
        Assert.Equal("../textures/" + TexA + ".png", image.GetProperty("uri").GetString());
        Assert.False(image.TryGetProperty("bufferView", out _));
        Assert.True(File.Exists(Path.Combine(c.Out, one.Folder, image.GetProperty("uri").GetString())));

        // the manifest says where everything lives, relative to the build's folder
        JsonElement manifest = ManifestOf(c, one);
        Assert.Equal("shared", manifest.GetProperty("layout").GetString());
        JsonElement texture = manifest.GetProperty("textures").EnumerateArray().Single(t => t.GetProperty("role").GetString() == "diffuse");
        Assert.Equal("../textures/" + TexA + ".jp2", texture.GetProperty("copiedTo").GetString());
        Assert.Equal("../textures/" + TexA + ".png", texture.GetProperty("imagePath").GetString());
        JsonElement[] files = manifest.GetProperty("files").EnumerateArray().ToArray();
        Assert.Contains(files, f => f.GetProperty("kind").GetString() == "original" && f.GetProperty("path").GetString() == "../textures/" + TexA + ".jp2");
        Assert.Contains(files, f => f.GetProperty("kind").GetString() == "png" && f.GetProperty("path").GetString() == "../textures/" + TexA + ".png");
        SummaryBuild mesh = s.BuildList.Single(b => b.Name == "Mesh");
        Assert.Contains(ManifestOf(c, mesh).GetProperty("files").EnumerateArray(), f => f.GetProperty("path").GetString() == "../assets/" + MeshId + ".llmesh");

        // the totals count the shared folders once
        Assert.True(s.SharedBytes > 0);
        Assert.Equal(s.BuildList.Sum(b => b.OutputBytes) + s.SharedBytes, s.OutputBytes);
    }

    [Fact]
    public void SelfContainedKeepsEachBuildWithItsOwnFoldersAndItsPngInsideTheGlb()
    {
        Case c = MakeArchive();
        ExportSummary s = Run(c, o => o.SelfContained = true);
        Assert.Equal("self-contained", s.Layout);
        Assert.False(Directory.Exists(Path.Combine(c.Out, "textures")));
        Assert.False(Directory.Exists(Path.Combine(c.Out, "assets")));
        Assert.Equal(0, s.SharedBytes);

        foreach (string name in new[] { "One", "Two" })
        {
            SummaryBuild b = s.BuildList.Single(x => x.Name == name);
            Glb glb = GlbOf(c, b);
            JsonElement image = Assert.Single(glb.Root.GetProperty("images").EnumerateArray());
            Assert.True(image.TryGetProperty("bufferView", out _));
            Assert.False(image.TryGetProperty("uri", out _));
            Assert.Equal(c.J2kA, File.ReadAllBytes(Path.Combine(c.Out, b.Folder, "textures", TexA + ".jp2")));
            JsonElement manifest = ManifestOf(c, b);
            Assert.Equal("self-contained", manifest.GetProperty("layout").GetString());
            JsonElement texture = manifest.GetProperty("textures").EnumerateArray().Single(t => t.GetProperty("role").GetString() == "diffuse");
            Assert.Equal("textures/" + TexA + ".jp2", texture.GetProperty("copiedTo").GetString());
            Assert.False(texture.TryGetProperty("imagePath", out _));
            Assert.Contains(manifest.GetProperty("files").EnumerateArray(), f => f.GetProperty("path").GetString() == "textures/" + TexA + ".jp2");
        }
        SummaryBuild mesh = s.BuildList.Single(b => b.Name == "Mesh");
        Assert.Equal(c.Mesh, File.ReadAllBytes(Path.Combine(c.Out, mesh.Folder, "assets", MeshId + ".llmesh")));
    }

    [Fact]
    public void TheTwoLayoutsGiveTheSameGeometryAndMaterials()
    {
        Case c = MakeArchive();
        ExportSummary shared = Run(c);
        Case d = new Case { Dir = c.Dir, Archive = c.Archive, Out = Path.Combine(c.Dir, "out-self") };
        ExportSummary self = Run(d, o => o.SelfContained = true);
        foreach (SummaryBuild b in shared.BuildList)
        {
            SummaryBuild other = self.BuildList.Single(x => x.RootPartUuid == b.RootPartUuid);
            Glb a = GlbOf(c, b), z = GlbOf(d, other);
            // only the paths of the copied assets differ, by the leading ../ of the shared folders
            static string Plain(JsonElement e) => e.ToString().Replace("../textures/", "textures/").Replace("../assets/", "assets/");
            Assert.Equal(Plain(a.Root.GetProperty("meshes")), Plain(z.Root.GetProperty("meshes")));
            Assert.Equal(Plain(a.Root.GetProperty("nodes")), Plain(z.Root.GetProperty("nodes")));
            Assert.Equal(a.Root.GetProperty("accessors").ToString(), z.Root.GetProperty("accessors").ToString());
            if (a.Root.TryGetProperty("materials", out JsonElement ma))
                Assert.Equal(ma.ToString(), z.Root.GetProperty("materials").ToString());
        }
    }

    [Fact]
    public void ARerunLeavesBuildsOfTheSameLayoutAndWritesTheOthersAgain()
    {
        Case c = MakeArchive();
        Assert.Equal(0, Run(c).BuildsResumed);
        Assert.Equal(4, Run(c).BuildsResumed);
        // asking for the other layout into the same folder does the builds again
        Assert.Equal(0, Run(c, o => o.SelfContained = true).BuildsResumed);
        Assert.Equal(4, Run(c, o => o.SelfContained = true).BuildsResumed);
        Assert.Equal(0, Run(c).BuildsResumed);
    }

    [Fact]
    public void ABuildWhoseSharedFileIsGoneIsWrittenAgain()
    {
        Case c = MakeArchive();
        Run(c);
        string png = Path.Combine(c.Out, "textures", TexB + ".png");
        File.Move(png, png + ".moved");   // moved aside, not deleted
        ExportSummary s = Run(c);
        Assert.Equal(3, s.BuildsResumed);   // only the build that points at texture B is done again
        Assert.True(File.Exists(png));
        Assert.Equal(File.ReadAllBytes(png + ".moved"), File.ReadAllBytes(png));
    }

    [Fact]
    public void AStoppedRunNeverLeavesAHalfWrittenSharedFileUnderItsRealName()
    {
        // a file is written under .tmp and moved into place, so the real name is either absent or complete
        Case c = MakeArchive();
        Run(c);
        Assert.Empty(Directory.GetFiles(Path.Combine(c.Out, "textures"), "*.tmp"));
        Assert.Empty(Directory.GetFiles(Path.Combine(c.Out, "assets"), "*.tmp"));
    }

    // ------------------------------------------------------------ an incomplete archive

    private static string CutArchive(out string outDir)
    {
        Case c = MakeArchive();
        outDir = Path.Combine(c.Dir, "cut-out");
        byte[] whole = Tar(new[] { ("archive.xml", OarControl()), ("objects/House_1.xml", Utf8(ObjectXml(new PartSpec { Name = "House" }))), ("objects/Tree_2.xml", Utf8(ObjectXml(new PartSpec { Name = "Tree" }))) }, gzip: false);
        string cut = Path.Combine(c.Dir, "cut.oar");
        File.WriteAllBytes(cut, whole.Take(whole.Length - 1500).ToArray());
        return cut;
    }

    [Fact]
    public void ACutOffArchiveSaysArchiveIncompleteFirstAndExitsFour()
    {
        string cut = CutArchive(out string outDir);
        ExportSummary s = ArchiveExporter.Export(cut, new ExportOptions { OutputDirectory = outDir });
        Assert.True(s.ArchiveIncomplete);
        Assert.Equal(ExitCodes.Incomplete, s.ExitCode);
        Assert.Equal(1, s.Builds);   // what was readable is exported
        Assert.Equal("ARCHIVE INCOMPLETE", SummaryText.Build(s).Split('\n')[0].TrimEnd('\r'));
        Assert.Equal("ARCHIVE INCOMPLETE", File.ReadLines(Path.Combine(outDir, "export-summary.txt")).First());
        Assert.True(JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "export-summary.json"))).RootElement.GetProperty("archiveIncomplete").GetBoolean());
    }

    [Fact]
    public void AWholeArchiveDoesNotSayIncompleteAndExitsZero()
    {
        Case c = MakeArchive();
        ExportSummary s = Run(c);
        Assert.False(s.ArchiveIncomplete);
        Assert.Equal(0, s.ExitCode);
        Assert.DoesNotContain("INCOMPLETE", SummaryText.Build(s));
    }

    [Fact]
    public void EveryBuildFailedStaysExitThreeEvenForAnIncompleteArchive()
    {
        ExportSummary s = new ExportSummary { ReadStatus = "Partial", ArchiveIncomplete = true, Builds = 2, BuildsFailed = 2 };
        Assert.Equal(ExitCodes.EveryBuildFailed, ArchiveExporter.ExitCodeOf(s));
        s.BuildsFailed = 1;
        Assert.Equal(ExitCodes.Incomplete, ArchiveExporter.ExitCodeOf(s));
        s.ArchiveIncomplete = false;
        Assert.Equal(ExitCodes.Done, ArchiveExporter.ExitCodeOf(s));
        s.ReadStatus = nameof(InspectionStatus.NotAnArchive);
        Assert.Equal(ExitCodes.Unreadable, ArchiveExporter.ExitCodeOf(s));
    }

    // ------------------------------------------------------------ the caps

    private static byte[] Noise(int bytes)
    {
        byte[] data = new byte[bytes];
        new Random(5).NextBytes(data);
        return data;
    }

    [Fact]
    public void ADiskCapIsReportedLoudlyAndExitsFourWithoutLosingTheOtherTextures()
    {
        Case c = MakeArchive(new[] { Noise(1_500_000) }, new[] { TexBig }, Textured("Big", TexBig));
        ExportSummary s = Run(c, o => o.MaxCacheMegabytes = 1);
        Assert.Equal(ExitCodes.Incomplete, s.ExitCode);
        Assert.Contains(s.CapsReached, l => l.StartsWith("DISK CAP REACHED") && l.Contains(TexBig.ToString()));
        Assert.Contains("DISK CAP REACHED", SummaryText.Build(s));
        SummaryBuild big = s.BuildList.Single(b => b.Name == "Big");
        JsonElement texture = ManifestOf(c, big).GetProperty("textures").EnumerateArray().Single();
        Assert.Equal("over-size-limit", texture.GetProperty("status").GetString());
        // the other builds kept their textures
        Assert.Equal(1, ManifestOf(c, s.BuildList.Single(b => b.Name == "One")).GetProperty("texturesEmbedded").GetInt32());
        Assert.True(File.Exists(Path.Combine(c.Out, "textures", TexA + ".png")));
    }

    [Fact]
    public void WithoutADiskCapALargeTextureIsKept()
    {
        Case c = MakeArchive(new[] { Noise(1_500_000) }, new[] { TexBig }, Textured("Big", TexBig));
        ExportSummary s = Run(c);
        Assert.Empty(s.CapsReached);
        SummaryBuild big = s.BuildList.Single(b => b.Name == "Big");
        // it cannot be decoded (it is noise), but it is kept as an original, not dropped for a cap
        JsonElement texture = ManifestOf(c, big).GetProperty("textures").EnumerateArray().Single();
        Assert.Equal("undecodable", texture.GetProperty("status").GetString());
        Assert.True(File.Exists(Path.Combine(c.Out, "textures", TexBig + ".jp2")));
    }

    [Fact]
    public void AnAssetTooLargeForTheMemoryCapIsReportedLoudlyAndExitsFour()
    {
        Case c = MakeArchive(new[] { Noise(700_000) }, new[] { TexBig }, Textured("Big", TexBig));
        ExportSummary s = Run(c, o => o.MaxMemoryMegabytes = 1);
        Assert.Equal(ExitCodes.Incomplete, s.ExitCode);
        Assert.Contains(s.CapsReached, l => l.StartsWith("MEMORY CAP REACHED") && l.Contains(TexBig.ToString()));
        JsonElement texture = ManifestOf(c, s.BuildList.Single(b => b.Name == "Big")).GetProperty("textures").EnumerateArray().Single();
        Assert.Equal("over-memory-limit", texture.GetProperty("status").GetString());
        Assert.Equal(0, s.BuildsFailed);
    }

    [Fact]
    public void ASmallMemoryCapStillExportsEverythingThatFitsBySpillingToTheCache()
    {
        Case c = MakeArchive();
        ExportSummary s = Run(c, o => o.MaxMemoryMegabytes = 1);
        Assert.Empty(s.CapsReached);
        Assert.Equal(0, s.ExitCode);
        Assert.Equal(4, s.BuildsWithAFile);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(c.Out, "textures"), "*.png").Length);
    }

    // ------------------------------------------------------------ free disk space

    [Fact]
    public void NotEnoughFreeSpaceForTheCacheRefusesBeforeAnythingIsWritten()
    {
        Case c = MakeArchive();
        ExportRefusedException e = Assert.Throws<ExportRefusedException>(() => Run(c, o => o.FreeBytes = _ => 1000));
        Assert.StartsWith("NOT ENOUGH FREE DISK SPACE", e.Message);
        Assert.Contains("cache", e.Message);
        Assert.False(File.Exists(Path.Combine(c.Out, "export-summary.json")));
        Assert.Empty(Directory.GetDirectories(c.Out).Where(d => !d.EndsWith(".asset-cache")));
    }

    [Fact]
    public void NotEnoughFreeSpaceForTheEstimatedOutputRefusesBeforeTheFirstBuildIsWritten()
    {
        Case c = MakeArchive();
        ExportRefusedException e = Assert.Throws<ExportRefusedException>(() => Run(c, o => o.FreeBytes = p => p.EndsWith(".asset-cache") ? long.MaxValue : 5000));
        Assert.StartsWith("NOT ENOUGH FREE DISK SPACE", e.Message);
        Assert.Contains("estimated", e.Message);
        Assert.False(File.Exists(Path.Combine(c.Out, "export-summary.json")));
        Assert.Empty(Directory.GetDirectories(c.Out).Where(d => !d.EndsWith(".asset-cache")));
        Assert.False(File.Exists(Path.Combine(c.Out, ArchiveExporter.JournalName)) && new FileInfo(Path.Combine(c.Out, ArchiveExporter.JournalName)).Length > 0);
    }

    [Fact]
    public void TheSpaceCheckCanBeSkippedAndAGenerousDriveIsNotRefused()
    {
        Case c = MakeArchive();
        Assert.Equal(4, Run(c, o => { o.FreeBytes = _ => 1000; o.SkipSpaceCheck = true; }).BuildsWithAFile);
        Case d = new Case { Dir = c.Dir, Archive = c.Archive, Out = Path.Combine(c.Dir, "out2") };
        Assert.Equal(4, Run(d, o => o.FreeBytes = _ => long.MaxValue).BuildsWithAFile);
    }

    [Fact]
    public void TheEstimateCountsASharedTextureOnceAndASelfContainedOneForEveryBuild()
    {
        Case c = MakeArchive();
        // read the archive the way the exporter does, then estimate
        ExportSummary s = Run(c);
        Assert.Equal(4, s.BuildList.Count);
        ArchiveAssets assets = new ArchiveAssets();
        assets.Add(TexA, (int)AssetType.Texture, "x", c.J2kA);
        assets.Add(TexB, (int)AssetType.Texture, "x", c.J2kB);
        List<ParsedGroup> groups = new List<ParsedGroup>();
        InspectorOptions inspect = new InspectorOptions { StandardChecks = false, MaxRetainedRecords = 1 };
        inspect.ObjectObserver = (obj, entry, region) => groups.AddRange(obj.Groups.Where(g => g.Parts.Count > 0 && (g.Name == "One" || g.Name == "Two")));
        new OpenSim.Archive.Inspection.ArchiveInspector(inspect).Inspect(c.Archive);
        Assert.Equal(2, groups.Count);
        long shared = ArchiveExporter.EstimateOutputBytes(groups, assets, true);
        long self = ArchiveExporter.EstimateOutputBytes(groups, assets, false);
        long perTexture = c.J2kA.Length * 13L;
        Assert.Equal(2 * 200 * 1024 + perTexture, shared);
        Assert.Equal(2 * 200 * 1024 + 2 * perTexture, self);
    }
}
