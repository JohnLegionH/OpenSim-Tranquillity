/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.Rewrite;

/// <summary>
/// Sets the OS field of a finished gzip member's header (byte 9, RFC 1952 section 2.3) to a fixed value. The framework writes
/// its running platform's code there (10 on Windows, 3 on Linux), so without this the same input gives a different file on
/// each platform although the compressed data is the same. A stream that cannot seek keeps the platform's value.
/// </summary>
internal static class GzipHeader
{
    private const int OsFieldOffset = 9;
    public const byte OsField = 10;

    public static void FixOsField(Stream output, long memberStart)
    {
        if (!output.CanSeek || output.Length < memberStart + OsFieldOffset + 1)
            return;
        long end = output.Position;
        output.Position = memberStart + OsFieldOffset;
        output.WriteByte(OsField);
        output.Position = end;
    }
}
