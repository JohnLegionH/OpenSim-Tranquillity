/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace OpenSim.Archive.Inspection.Tests;

public class ManagedDigestTests
{
    private static string Hex(byte[] digest) => Convert.ToHexString(digest).ToLowerInvariant();

    // FIPS 180-4 example messages (SHA-256 section of the NIST examples)
    [Theory]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq", "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1")]
    [InlineData("abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmnhijklmnoijklmnopjklmnopqklmnopqrlmnopqrsmnopqrstnopqrstu", "cf5b16a778af8380036ce59e7b0492370b249b11e8f07a51afac45037afee9d1")]
    public void Sha256MatchesTheFipsVectors(string text, string expected)
    {
        Assert.Equal(expected, Hex(ManagedDigest.Sha256(Encoding.ASCII.GetBytes(text))));
    }

    [Fact]
    public void Sha256OfAMillionAsIsTheFipsVector()
    {
        byte[] data = Enumerable.Repeat((byte)'a', 1_000_000).ToArray();
        Assert.Equal("cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0", Hex(ManagedDigest.Sha256(data)));
    }

    // RFC 1321 section A.5 test suite
    [Theory]
    [InlineData("", "d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("a", "0cc175b9c0f1b6a831c399e269772661")]
    [InlineData("abc", "900150983cd24fb0d6963f7d28e17f72")]
    [InlineData("message digest", "f96b697d7cb7938d525a2f31aaf161d0")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", "c3fcd3d76192e4007dfb496cca67e13b")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", "d174ab98d277d9f5a5611c2c9f419d9f")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345678901234567890", "57edf4a22be3c955ac49da2e2107b67a")]
    public void Md5MatchesTheRfc1321Vectors(string text, string expected)
    {
        Assert.Equal(expected, Hex(ManagedDigest.Md5(Encoding.ASCII.GetBytes(text))));
    }

    [Fact]
    public void BothDigestsOfAOneMegabyteInputEqualTheFrameworksOutput()
    {
        byte[] data = new byte[1_048_576];
        new Random(19).NextBytes(data);
        Assert.Equal(SHA256.HashData(data), ManagedDigest.Sha256(data));
        Assert.Equal(MD5.HashData(data), ManagedDigest.Md5(data));
    }

    [Fact]
    public void BothDigestsEqualTheFrameworksOutputAroundTheBlockAndPaddingBoundaries()
    {
        Random random = new Random(23);
        foreach (int length in new[] { 0, 1, 54, 55, 56, 57, 63, 64, 65, 119, 120, 121, 127, 128, 129, 1000, 65536, 100_003 })
        {
            byte[] data = new byte[length];
            random.NextBytes(data);
            Assert.Equal(SHA256.HashData(data), ManagedDigest.Sha256(data));
            Assert.Equal(MD5.HashData(data), ManagedDigest.Md5(data));
        }
    }
}
