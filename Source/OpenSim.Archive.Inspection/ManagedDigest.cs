/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Org.BouncyCastle.Crypto.Digests;

namespace OpenSim.Archive.Inspection;

/// <summary>
/// The one entry point for the SHA-256 and MD5 digests the archive tools compute. System.Security.Cryptography calls
/// OpenSSL on Linux, so a self-contained program that uses it fails to start on a system without libssl; these digests
/// are computed in managed code and are the same bytes.
/// </summary>
public static class ManagedDigest
{
    public static byte[] Sha256(ReadOnlySpan<byte> data) => ManagedSha256.HashData(data);

    // RFC 1321 from BouncyCastle (MIT), which OpenSim.Framework already carries
    public static byte[] Md5(ReadOnlySpan<byte> data)
    {
        MD5Digest md5 = new MD5Digest();
        md5.BlockUpdate(data);
        byte[] digest = new byte[md5.GetDigestSize()];
        md5.DoFinal(digest, 0);
        return digest;
    }
}
