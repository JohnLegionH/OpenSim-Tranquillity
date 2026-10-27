/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Restore.Import;
using Xunit;
using Vector3 = System.Numerics.Vector3;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The texture assets an import writes are what a viewer can show. The viewer's JPEG 2000 decoder is created for a bare codestream only
/// (indra/llimagej2coj/llimagej2coj.cpp opj_create_decompress(OPJ_CODEC_J2K)), so a file wrapped in JP2 boxes is shown as a grey face although the
/// region's own decoder reads it. The region serves the asset's bytes as they are stored. Each test writes its own folder and archive, so the class runs in parallel.
/// </summary>
public class ImportTextureAssetTests
{
    private static readonly byte[] CodestreamStart = { 0xFF, 0x4F, 0xFF, 0x51 };
    private static readonly byte[] Jp2SignatureBox = { 0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20 };

    private static Dictionary<string, byte[]> Textures(int pictureSize, bool normalMap)
    {
        ImportKit.Folder folder = ImportKit.Write(normalMap, pictureSize: pictureSize);
        string output = ImportKit.OutputPath();
        ObjectImporter.Import(folder.Path, output, new ImportOptions { Avatar = ImportKit.Avatar, Position = new Vector3(10f, 20f, 30f), Created = 1767225600 });
        Dictionary<string, byte[]> textures = ImportKit.ReadOar(output).Where(kv => kv.Key.EndsWith("_texture.jp2", StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.NotEmpty(textures);
        return textures;
    }

    [Fact]
    public void EveryTextureAssetIsABareCodestreamAndNotAJp2File()
    {
        Dictionary<string, byte[]> textures = Textures(64, normalMap: true);
        Assert.Equal(3, textures.Count);
        foreach (KeyValuePair<string, byte[]> t in textures)
        {
            Assert.True(t.Value.AsSpan(0, 4).SequenceEqual(CodestreamStart), t.Key + " does not start with the codestream markers (SOC, SIZ)");
            Assert.False(t.Value.AsSpan(0, 8).SequenceEqual(Jp2SignatureBox), t.Key + " is wrapped in JP2 boxes");
        }
    }

    [Fact]
    public void TheCodestreamIsOneTileInResolutionOrderWithTheDiscardLevelsAViewerAsksFor()
    {
        // 256 pixels: six wavelet levels (log2 256 less two), seven resolutions to discard down to; resolution-position-component order puts the small ones first
        foreach (byte[] data in Textures(256, normalMap: false).Values)
        {
            Assert.True(data.AsSpan(0, 4).SequenceEqual(CodestreamStart));
            // SIZ: marker, length, capabilities, then Xsiz, Ysiz, XOsiz, YOsiz, XTsiz, YTsiz (four bytes each)
            uint Be32(int at) => (uint)(data[at] << 24 | data[at + 1] << 16 | data[at + 2] << 8 | data[at + 3]);
            Assert.Equal(256u, Be32(8));
            Assert.Equal(256u, Be32(12));
            Assert.True(Be32(24) >= 256u && Be32(28) >= 256u, "the image is cut into more than one tile");
            int cod = -1;
            for (int i = 4; i + 1 < data.Length && cod < 0; i++)
                if (data[i] == 0xFF && data[i + 1] == 0x52)
                    cod = i;
            Assert.True(cod > 0, "no COD marker");
            Assert.Equal(2, data[cod + 5]);   // progression order: 0 LRCP, 1 RLCP, 2 RPCL
            Assert.Equal(6, data[cod + 9]);   // decomposition levels
        }
    }
}
