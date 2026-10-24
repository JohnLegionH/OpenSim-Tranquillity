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

public class ManagedSha256Tests
{
    [Theory]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq", "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1")]
    public void MatchesTheFipsTestVectors(string text, string expected)
    {
        Assert.Equal(expected, Convert.ToHexString(ManagedSha256.HashData(Encoding.ASCII.GetBytes(text))).ToLowerInvariant());
    }

    [Fact]
    public void MatchesTheFrameworkDigestAtEveryLengthAroundTheBlockAndPaddingBoundaries()
    {
        Random random = new Random(7);
        foreach (int length in new[] { 0, 1, 54, 55, 56, 57, 63, 64, 65, 119, 120, 127, 128, 129, 1000, 65536, 100_003 })
        {
            byte[] data = new byte[length];
            random.NextBytes(data);
            Assert.Equal(SHA256.HashData(data), ManagedSha256.HashData(data));
        }
    }
}
