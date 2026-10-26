/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Inspection;
using OpenSim.MeshAsset;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The levels of detail in the merged archive: the mesh asset carries the levels the report names, one face for each slot in every level, and
/// the limit the levels are checked against can be changed. Each test writes its own archive and shares no state, so the class runs in parallel.
/// </summary>
public class MergeLodWriteTests
{
    private static byte[] Entry(string archive, string name)
    {
        using FileStream fs = File.OpenRead(archive);
        TarStreamReader tar = new TarStreamReader(new GZipStream(fs, CompressionMode.Decompress));
        TarEntryHeader h;
        while ((h = tar.ReadNext()) != null)
            if (h.Name == name)
                return tar.ReadBody();
        return null;
    }

    private static (MergeWriteResult Result, string Output) Write(MergeOptions options = null)
    {
        string output = Path.Combine(Path.GetTempPath(), "merge-lod-tests", Guid.NewGuid().ToString("N") + ".oar");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        return (MergeArchiveWriter.Write(MapsFixture.Write(), output, options), output);
    }

    private static MeshAssetModel Mesh(string archive, MergedPartReport part) => MeshCodec.Decode(Entry(archive, "assets/" + part.MeshId + "_mesh.llmesh"));

    private static int Triangles(MeshLod lod) => lod.Faces.Sum(f => f.NoGeometry ? 0 : f.TriangleCount);

    [Fact]
    public void TheMeshAssetCarriesTheLevelsTheReportNamesWithOneFaceForEachSlotInEveryLevel()
    {
        (MergeWriteResult r, string output) = Write();
        Assert.NotEmpty(r.Parts);
        foreach (MergedPartReport part in r.Parts)
        {
            Assert.Equal(new[] { 2, 1, 0 }, part.Levels.Select(l => l.Level));
            MeshAssetModel model = Mesh(output, part);
            Assert.Equal(part.Levels[0].Triangles, Triangles(model.Medium));
            Assert.Equal(part.Levels[1].Triangles, Triangles(model.Low));
            Assert.Equal(part.Levels[2].Triangles, Triangles(model.Lowest));
            Assert.Equal(part.Triangles, Triangles(model.High));
            foreach (MeshLod lod in new[] { model.Medium, model.Low, model.Lowest })
                Assert.Equal(model.High.Faces.Count, lod.Faces.Count);
            Assert.All(part.Levels, l => Assert.True(l.ErrorPixels <= 1.0 + 1e-6, "level " + l.Level + " " + l.ErrorPixels));
            Assert.True(part.Levels[0].Triangles <= part.Triangles && part.Levels[1].Triangles <= part.Levels[0].Triangles && part.Levels[2].Triangles <= part.Levels[1].Triangles);
        }
    }

    [Fact]
    public void ALargerPixelLimitLetsTheLevelsKeepFewerTriangles()
    {
        (MergeWriteResult strict, _) = Write();
        (MergeWriteResult loose, _) = Write(new MergeOptions { Lod = new LodOptions { PixelLimit = 16 } });
        long Total(MergeWriteResult w) => w.Parts.Sum(p => p.Levels.Sum(l => (long)l.Triangles));
        Assert.True(Total(loose) < Total(strict), "strict " + Total(strict) + ", loose " + Total(loose));
        Assert.All(loose.Parts, p => Assert.All(p.Levels, l => Assert.True(l.ErrorPixels <= 16 + 1e-6)));
    }

    [Fact]
    public void TheReportSaysWhatEachLevelKeptAndHowFarItIs()
    {
        (MergeWriteResult r, _) = Write();
        string text = r.ToText();
        Assert.Contains("levels of detail", text);
        Assert.Contains("pixel", text);
        foreach (MergedPartReport p in r.Parts)
            Assert.Contains(p.Levels[2].Triangles + " triangles", text);
    }
}
