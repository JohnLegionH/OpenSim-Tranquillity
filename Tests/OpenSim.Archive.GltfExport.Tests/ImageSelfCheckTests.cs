/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;
using static OpenSim.Archive.GltfExport.Tests.TestKit;

namespace OpenSim.Archive.GltfExport.Tests;

/// <summary>The start-up check of the image library. These tests touch no process-wide state, so they run in parallel with the rest.</summary>
public class ImageSelfCheckTests
{
    [Fact]
    public void TheBuiltInImageDecodesThroughTheTexturePathToTheRightPixels()
    {
        Assert.Null(ImageSelfCheck.Run());
        Assert.Null(ImageSelfCheck.Problem());
    }

    [Fact]
    public void ABrokenImageLibraryStopsTheExportBeforeAnythingIsWritten()
    {
        string dir = NewFolder();
        string outDir = Path.Combine(dir, "out");
        string archive = Path.Combine(dir, "a.oar");
        File.WriteAllBytes(archive, Oar(("Box_1", ObjectXml(new PartSpec { Name = "Box" }))));

        ImageLibraryException e = Assert.Throws<ImageLibraryException>(() =>
            ArchiveExporter.Export(archive, new ExportOptions { OutputDirectory = outDir, ImageCheck = () => "libSkiaSharp could not be loaded" }));
        Assert.Contains("libSkiaSharp could not be loaded", e.Message);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void AWorkingCheckLetsTheExportRun()
    {
        string dir = NewFolder();
        string archive = Path.Combine(dir, "a.oar");
        File.WriteAllBytes(archive, Oar(("Box_1", ObjectXml(new PartSpec { Name = "Box" }))));
        ExportSummary s = ArchiveExporter.Export(archive, new ExportOptions { OutputDirectory = Path.Combine(dir, "out"), ImageCheck = () => null });
        Assert.Equal(1, s.Builds);
    }
}
