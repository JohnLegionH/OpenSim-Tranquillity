/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;

namespace OpenSim.Archive.ManagedHash;

// The digests are BouncyCastle's (MIT), which OpenSim.Framework already carries; each class only gives them the shape of a
// System.Security.Cryptography hash algorithm. The framework's own MD5, SHA1 and SHA256 call the system's OpenSSL on Linux,
// so a program that creates one fails to start on a system without libssl.

/// <summary>MD5 (RFC 1321) as a framework hash algorithm that runs in managed code.</summary>
public sealed class ManagedMD5 : MD5
{
    private readonly IDigest digest = new MD5Digest();

    public override void Initialize() => digest.Reset();

    protected override void HashCore(byte[] array, int ibStart, int cbSize) => digest.BlockUpdate(array, ibStart, cbSize);

    protected override void HashCore(ReadOnlySpan<byte> source) => digest.BlockUpdate(source);

    protected override byte[] HashFinal()
    {
        byte[] result = new byte[digest.GetDigestSize()];
        digest.DoFinal(result, 0);
        return result;
    }
}

/// <summary>SHA-1 (FIPS 180-4) as a framework hash algorithm that runs in managed code; see <see cref="ManagedMD5"/>.</summary>
public sealed class ManagedSHA1 : SHA1
{
    private readonly IDigest digest = new Sha1Digest();

    public override void Initialize() => digest.Reset();

    protected override void HashCore(byte[] array, int ibStart, int cbSize) => digest.BlockUpdate(array, ibStart, cbSize);

    protected override void HashCore(ReadOnlySpan<byte> source) => digest.BlockUpdate(source);

    protected override byte[] HashFinal()
    {
        byte[] result = new byte[digest.GetDigestSize()];
        digest.DoFinal(result, 0);
        return result;
    }
}

/// <summary>SHA-256 (FIPS 180-4) as a framework hash algorithm that runs in managed code; see <see cref="ManagedMD5"/>.</summary>
public sealed class ManagedSHA256 : SHA256
{
    private readonly IDigest digest = new Sha256Digest();

    public override void Initialize() => digest.Reset();

    protected override void HashCore(byte[] array, int ibStart, int cbSize) => digest.BlockUpdate(array, ibStart, cbSize);

    protected override void HashCore(ReadOnlySpan<byte> source) => digest.BlockUpdate(source);

    protected override byte[] HashFinal()
    {
        byte[] result = new byte[digest.GetDigestSize()];
        digest.DoFinal(result, 0);
        return result;
    }
}

/// <summary>
/// The factory methods the patched libraries call in place of MD5.Create(), SHA1.Create(), SHA256.Create() and the
/// constructors of the framework's service-provider classes.
/// </summary>
public static class ManagedHashes
{
    public static MD5 CreateMd5() => new ManagedMD5();

    public static SHA1 CreateSha1() => new ManagedSHA1();

    public static SHA256 CreateSha256() => new ManagedSHA256();
}
