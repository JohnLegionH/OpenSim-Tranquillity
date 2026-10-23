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
using OpenSim.Framework.Serialization;
using SkiaSharp;
using Xunit;
using static OpenSim.Archive.GltfExport.Tests.TestKit;

namespace OpenSim.Archive.GltfExport.Tests;

/// <summary>
/// Archives of many builds: the progress lines, a build that fails, a rerun into the same folder, the cache folder, the
/// one pass over the archive and the textures a PBR material names. Serial: the tests share a static hook that makes a build fail.
/// </summary>
[Collection("exporter-hook")]
public class ArchiveScaleTests
{
    // the part's id follows from its name, so building the same name twice gives the same object file
    private static string Build(string name) => ObjectXml(new PartSpec { Name = name, Id = new UUID(new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name)))), Shape = Box() });

    private static string WriteArchive(string dir, string file, params (string, string)[] objects)
    {
        string path = Path.Combine(dir, file);
        File.WriteAllBytes(path, Oar(objects));
        return path;
    }

    private static ExportSummary Run(string archive, string output, Action<ExportOptions> tune = null, List<string> lines = null)
    {
        ExportOptions o = new ExportOptions { OutputDirectory = output };
        if (lines != null)
            o.Progress = lines.Add;
        tune?.Invoke(o);
        return ArchiveExporter.Export(archive, o);
    }

    // ------------------------------------------------------------ progress

    [Fact]
    public void ProgressLinesGiveDoneOverTotalElapsedTimeAndTimeLeft()
    {
        string dir = NewFolder();
        string archive = WriteArchive(dir, "many.oar", Enumerable.Range(0, 12).Select(i => ("o" + i, Build("Build" + i))).ToArray());
        List<string> lines = new List<string>();
        Run(archive, Path.Combine(dir, "out"), o => o.ProgressEvery = 4, lines);
        Assert.Contains(lines, l => l.StartsWith("12 builds to do"));
        string[] progress = lines.Where(l => l.Contains(" builds (") && l.Contains("left")).ToArray();
        Assert.True(progress.Length >= 3, string.Join("\n", lines));
        Assert.Matches(@"^4/12 builds \(33\.3%\), elapsed \d\d:\d\d:\d\d, about \d\d:\d\d:\d\d left$", progress.First(l => l.StartsWith("4/12")));
        Assert.Contains(progress, l => l.StartsWith("12/12"));
    }

    // ------------------------------------------------------------ isolation

    [Fact]
    public void ABuildThatFailsIsRecordedWithItsErrorAndTheRestGoOn()
    {
        string dir = NewFolder();
        string archive = WriteArchive(dir, "three.oar", ("a", Build("Good1")), ("b", Build("Bad")), ("c", Build("Good2")));
        List<string> lines = new List<string>();
        ArchiveExporter.BeforeBuild = g => { if (g.Name == "Bad") throw new InvalidOperationException("this build cannot be written"); };
        ExportSummary s;
        try
        {
            s = Run(archive, Path.Combine(dir, "out"), null, lines);
        }
        finally
        {
            ArchiveExporter.BeforeBuild = null;
        }
        Assert.Equal((3, 1, 2), (s.Builds, s.BuildsFailed, s.BuildsWithAFile));
        SummaryBuild bad = s.BuildList.Single(b => b.Status == "failed");
        Assert.Equal("Bad", bad.Name);
        Assert.Contains("this build cannot be written", bad.Error);
        Assert.Contains(lines, l => l.Contains("build failed, continuing") && l.Contains("Bad"));
        // the file on disk says the same
        JsonElement written = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "out", "export-summary.json"))).RootElement;
        Assert.Equal(1, written.GetProperty("buildsFailed").GetInt32());
        Assert.Single(written.GetProperty("buildList").EnumerateArray().Where(b => b.GetProperty("status").GetString() == "failed"));
    }

    [Fact]
    public void AFailedBuildIsDoneAgainOnTheNextRun()
    {
        string dir = NewFolder();
        string archive = WriteArchive(dir, "two.oar", ("a", Build("Good")), ("b", Build("Flaky")));
        string output = Path.Combine(dir, "out");
        ArchiveExporter.BeforeBuild = g => { if (g.Name == "Flaky") throw new IOException("disk full"); };
        try { Run(archive, output); }
        finally { ArchiveExporter.BeforeBuild = null; }
        ExportSummary again = Run(archive, output);
        Assert.Equal((2, 0, 1), (again.Builds, again.BuildsFailed, again.BuildsResumed));
    }

    // ------------------------------------------------------------ resume

    [Fact]
    public void ARerunLeavesTheBuildsThatAreDoneAndForceWritesThemAgain()
    {
        string dir = NewFolder();
        string archive = WriteArchive(dir, "r.oar", ("a", Build("One")), ("b", Build("Two")), ("c", Build("Three")));
        string output = Path.Combine(dir, "out");
        ExportSummary first = Run(archive, output);
        Assert.Equal((3, 0), (first.Builds, first.BuildsResumed));
        string[] glbs = Directory.GetFiles(output, "*.glb", SearchOption.AllDirectories);
        Dictionary<string, DateTime> stamps = glbs.ToDictionary(f => f, f => File.GetLastWriteTimeUtc(f));
        Thread.Sleep(50);

        ExportSummary second = Run(archive, output);
        Assert.Equal((3, 3), (second.Builds, second.BuildsResumed));
        Assert.Equal(first.Parts, second.Parts);
        Assert.Equal(first.OutputBytes, second.OutputBytes);
        Assert.All(glbs, f => Assert.Equal(stamps[f], File.GetLastWriteTimeUtc(f)));

        ExportSummary forced = Run(archive, output, o => o.Force = true);
        Assert.Equal((3, 0), (forced.Builds, forced.BuildsResumed));
        Assert.Contains(glbs, f => File.GetLastWriteTimeUtc(f) > stamps[f]);
    }

    [Fact]
    public void ARerunDoesTheBuildWhoseObjectFileChangedAndNotTheOthers()
    {
        string dir = NewFolder();
        string output = Path.Combine(dir, "out");
        Run(WriteArchive(dir, "v1.oar", ("a", Build("One")), ("b", Build("Two"))), output);

        // the second object file differs (a part's description is added); the first is byte for byte the same
        string xml = Build("Two").Replace("<Name>Two</Name>", "<Name>Two</Name><Description>changed</Description>");
        Assert.NotEqual(Build("Two"), xml);
        List<string> lines = new List<string>();
        ExportSummary s = Run(WriteArchive(dir, "v2.oar", ("a", Build("One")), ("b", xml)), output, null, lines);
        Assert.Equal((2, 1), (s.Builds, s.BuildsResumed));
        Assert.Contains(lines, l => l.StartsWith("1 builds to do (1 already written"));
    }

    [Fact]
    public void ARunThatWasStoppedPartwayIsFinishedByTheNextOne()
    {
        string dir = NewFolder();
        string archive = WriteArchive(dir, "s.oar", Enumerable.Range(0, 6).Select(i => ("o" + i, Build("B" + i))).ToArray());
        string output = Path.Combine(dir, "out");
        Run(archive, output);

        // A hard stop leaves the journal lines of the builds that were finished, and perhaps a line cut short; no summary.
        string journal = Path.Combine(output, ArchiveExporter.JournalName);
        string[] lines = File.ReadAllLines(journal);
        Assert.Equal(6, lines.Length);
        File.WriteAllLines(journal, lines.Take(3).Append("{\"status\":\"ok\",\"folder\":\"cut"));
        ExportSummary s = Run(archive, output);
        Assert.Equal(6, s.Builds);
        Assert.Equal(3, s.BuildsResumed);
        Assert.Equal(0, s.BuildsFailed);
        Assert.Equal(6, Directory.GetFiles(output, "*.glb", SearchOption.AllDirectories).Length);
        // the builds done in the second run are journaled too, so a third run leaves all six
        Assert.Equal(6, Run(archive, output).BuildsResumed);
    }

    // ------------------------------------------------------------ one pass, and the cache

    private static readonly UUID TexId = UUID.Parse("a0000000-0000-4000-8000-0000000000e1");

    private static byte[] NoisyJ2k(int size)
    {
        using SKBitmap bmp = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgb888x, SKAlphaType.Opaque));
        Random r = new Random(7);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                bmp.SetPixel(x, y, new SKColor((byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256)));
        Assert.True(SkiaImageUtils.TryEncodeToJ2KLossless(bmp, out byte[] encoded));
        return encoded;
    }

    private static string TexturedBuild(UUID texture, string name = "Textured") =>
        ObjectXml(new PartSpec { Name = name, Shape = Box(), TextureEntryBase64 = TextureEntryBase64(texture) });

    [Fact]
    public void TheArchiveIsReadOnceAndALargeTextureWaitsInTheCacheFolder()
    {
        string dir = NewFolder();
        byte[] j2k = NoisyJ2k(256);
        Assert.True(j2k.Length > 64 * 1024, "the texture is large enough to leave memory");
        string archive = Path.Combine(dir, "t.oar");
        File.WriteAllBytes(archive, OarWithAssets(new[] { ("t", TexturedBuild(TexId)) }, new[] { (TexId, (sbyte)AssetType.Texture, j2k) }));
        string cache = Path.Combine(dir, "cache");
        ExportSummary s = Run(archive, Path.Combine(dir, "out"), o => o.CacheDirectory = cache);
        Assert.Equal(1, s.ArchivePasses);
        Assert.Equal(1, s.TexturesEmbedded);
        Assert.Equal(j2k.Length, s.CacheBytesWritten);
        Assert.Equal(j2k, File.ReadAllBytes(Path.Combine(cache, TexId + ".bin")));
        string build = Directory.GetDirectories(Path.Combine(dir, "out")).Single(d => !Path.GetFileName(d).StartsWith('.'));
        Assert.Equal(j2k, File.ReadAllBytes(Path.Combine(build, "textures", TexId + ".jp2")));
        Assert.True(s.PeakWorkingSetMegabytes > 0);
    }

    [Fact]
    public void ATextureThatCameByBeforeAnythingNamedItIsFetchedByASecondPassForJustThatId()
    {
        string dir = NewFolder();
        byte[] j2k = NoisyJ2k(16);
        // an archive with its assets ahead of its objects
        List<(string, byte[])> entries = new List<(string, byte[])>
        {
            ("archive.xml", OarControl()),
            (ArchiveConstants.ASSETS_PATH + TexId + ArchiveConstants.ASSET_TYPE_TO_EXTENSION[(sbyte)AssetType.Texture], j2k),
            ("objects/t.xml", Utf8(TexturedBuild(TexId)))
        };
        string archive = Path.Combine(dir, "early.oar");
        File.WriteAllBytes(archive, Tar(entries));
        ExportSummary s = Run(archive, Path.Combine(dir, "out"));
        Assert.Equal(2, s.ArchivePasses);
        Assert.Equal(1, s.TexturesEmbedded);
        Assert.Equal(0, s.TexturesMissing);
    }

    // ------------------------------------------------------------ PBR textures

    [Fact]
    public void TheTexturesAPbrMaterialNamesAreFetchedCopiedAndEmbedded()
    {
        UUID pbrId = UUID.Parse("f0000000-0000-4000-8000-0000000000a1");
        UUID baseTex = UUID.Parse("a0000000-0000-4000-8000-0000000000e2"), normalTex = UUID.Parse("a0000000-0000-4000-8000-0000000000e3"), mrTex = UUID.Parse("a0000000-0000-4000-8000-0000000000e4");
        string material = "{\"asset\":{\"version\":\"2.0\"},\"images\":[{\"uri\":\"" + baseTex + "\"},{\"uri\":\"" + normalTex + "\"},{\"uri\":\"" + mrTex + "\"}]," +
            "\"textures\":[{\"source\":0},{\"source\":1},{\"source\":2}],\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0},\"metallicRoughnessTexture\":{\"index\":2}},\"normalTexture\":{\"index\":1}}]}";

        PrimitiveBaseShape shape = new PrimitiveBaseShape();
        shape.RenderMaterials = new Primitive.RenderMaterials
        {
            entries = new[] { new Primitive.RenderMaterials.RenderMaterialEntry { te_index = 0, id = pbrId } },
            overrides = Array.Empty<Primitive.RenderMaterials.RenderMaterialOverrideEntry>()
        };
        PartSpec root = new PartSpec { Name = "Pbr", Shape = Box(), TextureEntryBase64 = TextureEntryBase64(UUID.Zero), ExtraParamsBase64 = Convert.ToBase64String(shape.ExtraParams) };

        string dir = NewFolder();
        string archive = Path.Combine(dir, "pbr.oar");
        File.WriteAllBytes(archive, OarWithAssets(new[] { ("pbr", ObjectXml(root)) }, new[]
        {
            (pbrId, (sbyte)AssetType.Material, Utf8(material)),
            (baseTex, (sbyte)AssetType.Texture, NoisyJ2k(16)),
            (normalTex, (sbyte)AssetType.Texture, NoisyJ2k(16)),
            (mrTex, (sbyte)AssetType.Texture, NoisyJ2k(16))
        }));
        ExportSummary s = Run(archive, Path.Combine(dir, "out"));
        SummaryBuild b = Assert.Single(s.BuildList);
        string folder = Path.Combine(dir, "out", b.Folder);
        JsonElement manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json"))).RootElement;
        JsonElement[] roles = manifest.GetProperty("textures").EnumerateArray().Where(t => t.GetProperty("role").GetString().StartsWith("pbr-")).ToArray();
        Assert.Equal(new[] { "pbr-base-color", "pbr-metallic-roughness", "pbr-normal" }, roles.Select(t => t.GetProperty("role").GetString()).OrderBy(x => x));
        Assert.All(roles, t => Assert.Equal("embedded", t.GetProperty("status").GetString()));
        foreach (UUID id in new[] { baseTex, normalTex, mrTex })
            Assert.True(File.Exists(Path.Combine(folder, "textures", id + ".jp2")));

        Glb glb = ReadGlb(File.ReadAllBytes(Path.Combine(folder, b.File)));
        JsonElement m = glb.Root.GetProperty("materials").EnumerateArray().Single(x => x.GetProperty("extras").TryGetProperty("pbr", out _));
        Assert.True(m.GetProperty("pbrMetallicRoughness").TryGetProperty("baseColorTexture", out _));
        Assert.True(m.GetProperty("pbrMetallicRoughness").TryGetProperty("metallicRoughnessTexture", out _));
        Assert.True(m.TryGetProperty("normalTexture", out _));
        Assert.Equal(3, glb.Root.GetProperty("images").GetArrayLength());
    }
}
