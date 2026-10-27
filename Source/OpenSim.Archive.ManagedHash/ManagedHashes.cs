/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Security.Cryptography;

namespace OpenSim.Archive.ManagedHash;

/// <summary>
/// MD5 (RFC 1321) as a framework hash algorithm that runs in managed code. The framework's MD5 calls the system's OpenSSL
/// on Linux, so a program that creates one fails to start on a system without libssl.
/// </summary>
public sealed class ManagedMD5 : MD5
{
    public override void Initialize()
    {
    }

    protected override void HashCore(byte[] array, int ibStart, int cbSize)
    {
    }

    protected override byte[] HashFinal() => Array.Empty<byte>();
}

/// <summary>SHA-1 (FIPS 180-4) in managed code; see <see cref="ManagedMD5"/>.</summary>
public sealed class ManagedSHA1 : SHA1
{
    public override void Initialize()
    {
    }

    protected override void HashCore(byte[] array, int ibStart, int cbSize)
    {
    }

    protected override byte[] HashFinal() => Array.Empty<byte>();
}

/// <summary>SHA-256 (FIPS 180-4) in managed code; see <see cref="ManagedMD5"/>.</summary>
public sealed class ManagedSHA256 : SHA256
{
    public override void Initialize()
    {
    }

    protected override void HashCore(byte[] array, int ibStart, int cbSize)
    {
    }

    protected override byte[] HashFinal() => Array.Empty<byte>();
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
