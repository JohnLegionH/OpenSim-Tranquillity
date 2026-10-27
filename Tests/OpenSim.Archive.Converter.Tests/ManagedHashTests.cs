/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Security.Cryptography;
using OpenSim.Archive.ManagedHash;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>The managed hash algorithms give the framework's digests, one-shot, in pieces and when reused.</summary>
public class ManagedHashTests
{
    private static byte[] Pseudo(int length)
    {
        byte[] data = new byte[length];
        new Random(20261027).NextBytes(data);
        return data;
    }

    public static IEnumerable<object[]> Inputs()
    {
        yield return new object[] { Array.Empty<byte>() };
        yield return new object[] { "abc"u8.ToArray() };
        yield return new object[] { Pseudo(55) };
        yield return new object[] { Pseudo(64) };
        yield return new object[] { Pseudo(65) };
        yield return new object[] { Pseudo(1_000_003) };
    }

    [Theory, MemberData(nameof(Inputs))]
    public void Md5GivesTheFrameworksDigest(byte[] data) =>
        Assert.Equal(Convert.ToHexString(MD5.HashData(data)), Convert.ToHexString(new ManagedMD5().ComputeHash(data)));

    [Theory, MemberData(nameof(Inputs))]
    public void Sha1GivesTheFrameworksDigest(byte[] data) =>
        Assert.Equal(Convert.ToHexString(SHA1.HashData(data)), Convert.ToHexString(new ManagedSHA1().ComputeHash(data)));

    [Theory, MemberData(nameof(Inputs))]
    public void Sha256GivesTheFrameworksDigest(byte[] data) =>
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), Convert.ToHexString(new ManagedSHA256().ComputeHash(data)));

    [Fact]
    public void TheSizesAreThoseOfTheAlgorithms()
    {
        Assert.Equal(128, new ManagedMD5().HashSize);
        Assert.Equal(160, new ManagedSHA1().HashSize);
        Assert.Equal(256, new ManagedSHA256().HashSize);
    }

    [Fact]
    public void ADigestOfAStreamReadInPiecesIsTheDigestOfTheWhole()
    {
        byte[] data = Pseudo(100_000);
        using MemoryStream stream = new MemoryStream(data);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), Convert.ToHexString(new ManagedSHA256().ComputeHash(stream)));
        byte[] expected = MD5.HashData(data);
        ManagedMD5 md5 = new ManagedMD5();
        for (int offset = 0; offset < data.Length; offset += 7919)
            md5.TransformBlock(data, offset, Math.Min(7919, data.Length - offset), null, 0);
        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(md5.Hash));
    }

    [Fact]
    public void AnInstanceDigestsTwoInputsOneAfterTheOther()
    {
        byte[] first = Pseudo(300), second = Pseudo(301);
        ManagedSHA1 sha1 = new ManagedSHA1();
        Assert.Equal(Convert.ToHexString(SHA1.HashData(first)), Convert.ToHexString(sha1.ComputeHash(first)));
        Assert.Equal(Convert.ToHexString(SHA1.HashData(second)), Convert.ToHexString(sha1.ComputeHash(second)));
    }

    [Fact]
    public void InitializeDropsWhatWasAlreadyFed()
    {
        ManagedMD5 md5 = new ManagedMD5();
        md5.TransformBlock(Pseudo(500), 0, 500, null, 0);
        md5.Initialize();
        Assert.Equal(Convert.ToHexString(MD5.HashData("abc"u8)), Convert.ToHexString(md5.ComputeHash("abc"u8.ToArray())));
    }

    [Fact]
    public void TheFactoriesGiveTheManagedClasses()
    {
        Assert.IsType<ManagedMD5>(ManagedHashes.CreateMd5());
        Assert.IsType<ManagedSHA1>(ManagedHashes.CreateSha1());
        Assert.IsType<ManagedSHA256>(ManagedHashes.CreateSha256());
        Assert.NotSame(ManagedHashes.CreateMd5(), ManagedHashes.CreateMd5());
    }
}
