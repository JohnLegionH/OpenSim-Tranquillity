/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using System.Text;
using Xunit;

namespace OpenSim.Archive.Rewrite.Tests;

/// <summary>
/// The rewrite to a file is all or nothing: the output name appears only when the whole archive was written. Each test works in its own
/// folder and shares no state, so the class runs in parallel with others.
/// </summary>
public class AtomicOutputTests
{
    private static readonly Guid Part = Guid.Parse("00000000-0000-4000-8000-0000000000a1");

    private static string Folder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "atomic-output-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Source(string folder)
    {
        string xml = "<SceneObjectGroup><RootPart><SceneObjectPart><UUID><UUID>" + Part + "</UUID></UUID><Name>Alpha</Name></SceneObjectPart></RootPart></SceneObjectGroup>";
        string path = Path.Combine(folder, "source.oar");
        using FileStream fs = File.Create(path);
        using (GZipStream gz = new GZipStream(fs, CompressionLevel.Fastest, true))
        using (TarWriter w = new TarWriter(gz, true))
        {
            w.WriteFile("archive.xml", Encoding.UTF8.GetBytes("<archive major_version=\"0\" minor_version=\"8\" />"));
            w.WriteFile("objects/Thing__" + Part + ".xml", Encoding.UTF8.GetBytes(xml));
        }
        return path;
    }

    private static ArchiveRewriter Failing()
    {
        // the element does not exist, which is found only when the object file is patched, after the output has been started
        ArchiveRewriter rw = new ArchiveRewriter();
        rw.AddPatch(new PartPatch(Part, ElementEdit.SetText("NoSuchElement", "x")));
        return rw;
    }

    [Fact]
    public void AFailedRewriteNeverCreatesTheOutputNameAndKeepsThePartialFileUnderAnotherName()
    {
        string folder = Folder(), source = Source(folder), output = Path.Combine(folder, "out.oar");
        Assert.Throws<InvalidDataException>(() => Failing().Rewrite(source, output));

        Assert.False(File.Exists(output));
        string[] left = Directory.GetFiles(folder).Select(Path.GetFileName).Where(n => n != "source.oar").ToArray();
        string partial = Assert.Single(left);
        Assert.StartsWith("out.oar.", partial);
        Assert.EndsWith(".incomplete", partial);
    }

    [Fact]
    public void ASuccessfulRewriteLeavesTheOutputAndNothingElse()
    {
        string folder = Folder(), source = Source(folder), output = Path.Combine(folder, "out.oar");
        new ArchiveRewriter().Rewrite(source, output);

        Assert.True(File.Exists(output));
        Assert.Equal(new[] { "out.oar", "source.oar" }, Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(0x1f, File.ReadAllBytes(output)[0]);
    }

    [Fact]
    public void AnOutputThatExistsIsRefusedAndNotChangedAndNoFileIsLeft()
    {
        string folder = Folder(), source = Source(folder), output = Path.Combine(folder, "out.oar");
        File.WriteAllText(output, "earlier result");
        Assert.Throws<IOException>(() => new ArchiveRewriter().Rewrite(source, output));

        Assert.Equal("earlier result", File.ReadAllText(output));
        Assert.Equal(new[] { "out.oar", "source.oar" }, Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void TheSourceIsUntouchedByAFailedRewrite()
    {
        string folder = Folder(), source = Source(folder), output = Path.Combine(folder, "out.oar");
        byte[] before = File.ReadAllBytes(source);
        Assert.Throws<InvalidDataException>(() => Failing().Rewrite(source, output));
        Assert.Equal(before, File.ReadAllBytes(source));
    }
}
