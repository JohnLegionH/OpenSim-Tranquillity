/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.IO.Compression;
using OpenMetaverse;
using OpenSim.Archive.Inspection;
using OpenSim.Framework.Serialization;
using Xunit;
using static OpenSim.Archive.Inspection.Tests.ArchiveTestKit;
using static OpenSim.Archive.Inspection.Tests.HandMade;

namespace OpenSim.Archive.Inspection.Tests;

/// <summary>
/// Large archives. The first test makes a few megabytes in memory and always runs. The second writes a multi-gigabyte
/// OAR to a path named in the environment variable ARCHIVE_INSPECTOR_SYNTHETIC_OAR (size in megabytes in
/// ARCHIVE_INSPECTOR_SYNTHETIC_MB, default 2560) and does nothing when the variable is not set, so the normal run stays fast.
/// </summary>
public class LargeArchiveTests
{
    /// <summary>Writes an OAR of random (incompressible) asset bodies and a few hundred objects with the repository's tar writer.</summary>
    internal static void WriteSyntheticOar(Stream destination, long targetCompressedBytes, int objects, int assetBytes)
    {
        using GZipStream gzip = new GZipStream(destination, CompressionLevel.Fastest, leaveOpen: true);
        TarArchiveWriter tar = new TarArchiveWriter(gzip);
        tar.WriteFile("archive.xml", OarControl("0.8"));

        UUID[] creators = { Alice, Bob, Carol, Dave };
        for (int i = 0; i < objects; i++)
        {
            UUID c = creators[i % creators.Length];
            string part = PartXml("Object " + i, new UUID("00000000-0000-4000-8000-" + (i + 1).ToString("x12")), c, c, c, null, 1600000000 + i * 3000);
            tar.WriteFile("objects/Object" + i + "_" + (i % 256) + "-" + (i / 256 % 256) + "-25__" + i + ".xml", Xml2Object(part));
        }

        Random rnd = new Random(12345);
        byte[] body = new byte[assetBytes];
        long written = 0;
        int n = 0;
        while (written < targetCompressedBytes)
        {
            rnd.NextBytes(body);
            tar.WriteFile("assets/" + new UUID("00000000-0000-4000-9000-" + (++n).ToString("x12")) + "_texture.jp2", body);
            written += body.Length;
        }
        tar.Close();
    }

    [Fact]
    public void ManyAssets_AreSkippedAndCounted()
    {
        using MemoryStream ms = new MemoryStream();
        WriteSyntheticOar(ms, 6 * 1024 * 1024, 300, 256 * 1024);
        ArchiveReport r = Inspect(ms.ToArray());
        Assert.Equal(InspectionStatus.Complete, r.Status);
        Assert.Equal(300, r.PartCount);
        Assert.Equal(24, r.AssetCount);
        Assert.All(r.Assets, a => Assert.False(a.BodyRead));
        Assert.Equal(4, r.Creators.Count);
        Assert.Equal(24, r.UnreferencedAssetCount);
    }

    [Fact]
    public void WritesASyntheticMultiGigabyteOar_WhenAskedTo()
    {
        string path = Environment.GetEnvironmentVariable("ARCHIVE_INSPECTOR_SYNTHETIC_OAR");
        if (string.IsNullOrEmpty(path))
            return;

        long megabytes = long.TryParse(Environment.GetEnvironmentVariable("ARCHIVE_INSPECTOR_SYNTHETIC_MB"), out long mb) ? mb : 2560;
        using FileStream fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        WriteSyntheticOar(fs, megabytes * 1024 * 1024, 400, 2 * 1024 * 1024);
    }
}
