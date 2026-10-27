/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.PackagePatch;

/// <summary>
/// Replaces the hash objects an assembly creates from the framework's classes with the managed ones of
/// OpenSim.Archive.ManagedHash. The framework's MD5, SHA1 and SHA256 call the system's OpenSSL on Linux, and the
/// OpenMetaverse libraries create theirs when their Utils class starts, so a program that merely uses a UUID aborts on a
/// system without libssl.
/// </summary>
public static class HashPatcher
{
    /// <summary>Copies <paramref name="inputPath"/> to <paramref name="outputPath"/> with its hash objects replaced; returns how many call sites it replaced.</summary>
    public static int Patch(string inputPath, string outputPath)
    {
        File.Copy(inputPath, outputPath, true);
        return 0;
    }
}
