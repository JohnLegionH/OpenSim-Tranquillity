/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Archive.Converter;
using OpenSim.Framework;
using SkiaSharp;
using Xunit;

namespace OpenSim.Archive.Eligibility.Tests;

/// <summary>A sculpt whose map is a real JPEG 2000 texture inside the archive (the converter's default map source) converts.</summary>
public class SculptMapFromArchiveTests
{
    private static byte[] SphereMapJ2k(int n)
    {
        using SKBitmap bmp = new SKBitmap(new SKImageInfo(n, n, SKColorType.Rgb888x, SKAlphaType.Opaque));
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                double th = Math.PI * y / (n - 1), ph = 2 * Math.PI * x / n;
                bmp.SetPixel(x, y, new SKColor((byte)(127.5 + 114 * Math.Sin(th) * Math.Cos(ph)), (byte)(127.5 + 114 * Math.Sin(th) * Math.Sin(ph)), (byte)(127.5 + 114 * Math.Cos(th))));
            }
        Assert.True(SkiaImageUtils.TryEncodeToJ2KLossless(bmp, out byte[] encoded));
        return encoded;
    }

    [Fact]
    public void ASculptWithAJpeg2000MapInTheArchiveIsConvertedNotKeptForWantOfAPhysicsShape()
    {
        Guid map = Guid.NewGuid();
        KitPart sculpt = new KitPart("Sculpt") { Shape = FaceMatrix.Shape("sphere", "plain"), Scale = "2,3,4", SculptTexture = map };
        sculpt.Shape.SculptEntry = true;
        sculpt.Shape.SculptType = (int)SculptType.Sphere;
        string source = EligibilityKit.WriteArchive(new List<List<KitPart>> { new List<KitPart> { sculpt } },
            new Dictionary<string, byte[]> { ["assets/" + map + "_texture.jp2"] = SphereMapJ2k(64) });

        // no map source given: the converter reads the maps from the archive, as the command line does
        ConvertResult r = new ArchiveConverter(new ConvertOptions()).Convert(source, Path.Combine(Path.GetDirectoryName(source), Guid.NewGuid().ToString("N") + "-out.oar"));

        Assert.Empty(r.Kept);
        Assert.Equal("sculpt", Assert.Single(r.Converted).Kind);
        Assert.True(r.Converted[0].PhysicsTriangles > 100);
    }
}
