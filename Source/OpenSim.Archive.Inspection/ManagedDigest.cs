/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.Inspection;

/// <summary>
/// The one entry point for the SHA-256 and MD5 digests the archive tools compute. System.Security.Cryptography calls
/// OpenSSL on Linux, so a self-contained program that uses it fails to start on a system without libssl; these digests
/// are computed in managed code and are the same bytes.
/// </summary>
public static class ManagedDigest
{
    public static byte[] Sha256(ReadOnlySpan<byte> data) => throw new NotImplementedException();

    public static byte[] Md5(ReadOnlySpan<byte> data) => throw new NotImplementedException();
}
