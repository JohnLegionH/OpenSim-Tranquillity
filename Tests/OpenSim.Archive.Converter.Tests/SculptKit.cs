/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Archive.Eligibility.Tests;
using OpenSim.Framework;
using SkiaSharp;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>Sculpt maps made from formulas, and archives of sculpt parts that use them.</summary>
internal static class SculptKit
{
    public enum Shape { Sphere, Torus, Plane, Cylinder }

    public static SculptType TypeOf(Shape s) => s switch
    {
        Shape.Sphere => SculptType.Sphere, Shape.Torus => SculptType.Torus, Shape.Plane => SculptType.Plane, _ => SculptType.Cylinder
    };

    /// <summary>
    /// A square sculpt map: each pixel's red, green and blue are the x, y and z of a point of the shape in the unit box, 0 to 255 for -0.5 to 0.5.
    /// The sphere's first and last rows are one point each (the poles), so a whole row of the map is one position.
    /// </summary>
    public static byte[] Map(Shape shape, int n)
    {
        using SKBitmap bmp = new SKBitmap(new SKImageInfo(n, n, SKColorType.Rgb888x, SKAlphaType.Opaque));
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                double u = 2 * Math.PI * x / n, rowFraction = (double)y / (n - 1);
                (double px, double py, double pz) = shape switch
                {
                    Shape.Sphere => (0.5 * Math.Sin(Math.PI * rowFraction) * Math.Cos(u), 0.5 * Math.Sin(Math.PI * rowFraction) * Math.Sin(u), 0.5 * Math.Cos(Math.PI * rowFraction)),
                    Shape.Torus => ((0.32 + 0.14 * Math.Cos(2 * Math.PI * y / n)) * Math.Cos(u), (0.32 + 0.14 * Math.Cos(2 * Math.PI * y / n)) * Math.Sin(u), 0.14 * Math.Sin(2 * Math.PI * y / n)),
                    Shape.Plane => ((double)x / (n - 1) - 0.5, rowFraction - 0.5, 0.05 * Math.Sin(6 * (double)x / n)),
                    _ => (0.5 * Math.Cos(u), 0.5 * Math.Sin(u), rowFraction - 0.5)
                };
                byte B(double v) => (byte)Math.Round((v + 0.5) * 255);
                bmp.SetPixel(x, y, new SKColor(B(px), B(py), B(pz)));
            }
        Assert.True(SkiaImageUtils.TryEncodeToJ2KLossless(bmp, out byte[] j2k));
        return j2k;
    }

    /// <summary>One sculpt part of the given shape, its map named by the returned id.</summary>
    public static KitPart Part(string name, int linkNum, Shape shape, string offset, Guid map, string textureEntry)
    {
        KitPart p = new KitPart(name, linkNum) { Offset = offset, Scale = "1.2,1.4,1", SculptTexture = map, TextureEntry = textureEntry, Shape = FaceMatrix.Shape("sphere", "plain") };
        p.Shape.SculptEntry = true;
        p.Shape.SculptType = (int)TypeOf(shape);
        return p;
    }
}
