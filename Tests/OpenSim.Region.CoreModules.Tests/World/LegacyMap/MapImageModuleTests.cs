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

using Xunit;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.CoreModules.World.LegacyMap;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using SkiaSharp;

namespace OpenSim.Region.CoreModules.LegacyMap.Tests;

/// <summary>
/// Headless smoke tests for the legacy MapImageModule renderer: the tile must be
/// rendered at heightmap resolution, flat terrain must not come out black, and
/// var regions must not wrap into stripes.
/// </summary>
public class MapImageModuleTests : OpenSimTestCase
{
    private static IConfigSource MapConfig(bool textured)
    {
        IConfigSource config = new IniConfigSource();
        IConfig map = config.AddConfig("Map");
        map.Set("MapImageModule", "MapImageModule");
        map.Set("TextureOnMapTile", textured);
        map.Set("DrawPrimOnMapTile", true);
        return config;
    }

    private static (Scene scene, MapImageModule module) SetupRegion(uint size, IConfigSource config)
    {
        Scene scene = new SceneHelpers().SetupScene("map test", UUID.Random(), 1000, 1000, size, size, config);
        MapImageModule module = new MapImageModule();
        SceneHelpers.SetupSceneModules(scene, config, module);
        return (scene, module);
    }

    private static void FillHeightmap(Scene scene, Func<int, int, float> heightAt)
    {
        for (int x = 0; x < scene.Heightmap.Width; x++)
            for (int y = 0; y < scene.Heightmap.Height; y++)
                scene.Heightmap[x, y] = heightAt(x, y);
    }

    private static byte[] EncodeJpeg(SKBitmap bmp)
    {
        using SKImage image = SKImage.FromBitmap(bmp);
        using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }

    // Set MAPTILE_TEST_DUMP=<dir> to write each rendered tile out as a JPEG for eyeballing.
    private static void Dump(SKBitmap bmp, string name)
    {
        string dir = Environment.GetEnvironmentVariable("MAPTILE_TEST_DUMP");
        if (string.IsNullOrEmpty(dir))
            return;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".jpg"), EncodeJpeg(bmp));
    }

    private static (int black, int distinct) Census(SKBitmap bmp)
    {
        int black = 0;
        HashSet<uint> colors = new HashSet<uint>();
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                if (c.Red == 0 && c.Green == 0 && c.Blue == 0)
                    black++;
                colors.Add((uint)c & 0x00FFFFFF);
            }
        }
        return (black, colors.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FlatTerrainAboveWaterIsNotBlack(bool textured)
    {
        TestHelpers.InMethod();

        (Scene scene, MapImageModule module) = SetupRegion(256, MapConfig(textured));
        FillHeightmap(scene, (x, y) => 25f); // Ebony-style flat 25m region, water at 20m

        using SKBitmap tile = module.CreateMapTile();

        Assert.NotNull(tile);
        Dump(tile, textured ? "flat-textured" : "flat-shaded");
        Assert.Equal(256, tile.Width);
        Assert.Equal(256, tile.Height);

        (int black, int distinct) = Census(tile);
        Assert.Equal(0, black);
        Assert.True(distinct < 4096, $"flat terrain should be near-uniform, got {distinct} distinct colours");
    }

    [Fact]
    public void SlopedTerrainRendersLandAndWaterWithVariation()
    {
        TestHelpers.InMethod();

        (Scene scene, MapImageModule module) = SetupRegion(256, MapConfig(true));
        // 0m at the south edge rising to 60m at the north edge: water band, then all four texture bands.
        FillHeightmap(scene, (x, y) => y * 60f / 255f);

        using SKBitmap tile = module.CreateMapTile();

        Assert.NotNull(tile);
        Dump(tile, "slope-256");
        (int black, int distinct) = Census(tile);
        Assert.Equal(0, black);
        Assert.True(distinct > 200, $"expected varied terrain colours, got {distinct}");

        // Bitmap is Y-flipped: bottom rows are the low (south) edge and must be water.
        SKColor south = tile.GetPixel(128, 255);
        SKColor north = tile.GetPixel(128, 0);
        Assert.NotEqual(south, north);

        // A smooth synthetic slope compresses far better than real terrain; the broken
        // renderer produced ~1.2KB solid-black tiles, so anything over 4KB is real content.
        byte[] jpeg = EncodeJpeg(tile);
        Assert.True(jpeg.Length > 4 * 1024, $"expected a real JPEG, got {jpeg.Length} bytes");
    }

    [Fact]
    public void VarRegionRendersAtFullResolutionWithoutStripes()
    {
        TestHelpers.InMethod();

        (Scene scene, MapImageModule module) = SetupRegion(1024, MapConfig(true));
        // Height depends only on Y, so every row is (up to noise) one colour band and
        // adjacent rows are close; the old 256x256 wrap produced a hard edge every 64 rows.
        FillHeightmap(scene, (x, y) => 22f + y * 40f / 1023f);

        using SKBitmap tile = module.CreateMapTile();

        Assert.NotNull(tile);
        Dump(tile, "slope-1024-var");
        Assert.Equal(1024, tile.Width);
        Assert.Equal(1024, tile.Height);

        (int black, int distinct) = Census(tile);
        Assert.Equal(0, black);

        // Compare row means: consecutive rows must differ by only a small amount everywhere,
        // including across the old wrap boundaries (rows 64, 128, ...).
        double[] rowMean = new double[1024];
        for (int y = 0; y < 1024; y++)
        {
            long sum = 0;
            for (int x = 0; x < 1024; x += 8)
            {
                SKColor c = tile.GetPixel(x, y);
                sum += c.Red + c.Green + c.Blue;
            }
            rowMean[y] = sum / (1024.0 / 8);
        }
        for (int y = 1; y < 1024; y++)
            Assert.True(Math.Abs(rowMean[y] - rowMean[y - 1]) < 40,
                $"row {y} jumps by {Math.Abs(rowMean[y] - rowMean[y - 1]):F1}: stripe artefact");
    }
}
