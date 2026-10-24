/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.GltfExport;

/// <summary>Recognises the error a full disk raises, so the export can stop cleanly instead of reporting a failed build.</summary>
internal static class DiskFull
{
    private const int WindowsErrorHandleDiskFull = 39;
    private const int WindowsErrorDiskFull = 112;
    private const int PosixNoSpace = 28;   // ENOSPC; .NET puts the raw errno in HResult of an IOException on Linux and macOS

    /// <summary>True when the exception, or one inside it, means the disk is full.</summary>
    public static bool Is(Exception e)
    {
        for (; e != null; e = e.InnerException)
            if (e is IOException && IsCode(e.HResult))
                return true;
        return false;
    }

    internal static bool IsCode(int hresult)
    {
        if (OperatingSystem.IsWindows())
        {
            // HRESULT_FROM_WIN32: facility 7 in the high word, the Win32 error in the low word
            if (((uint)hresult & 0xFFFF0000u) != 0x80070000u)
                return false;
            int win32 = hresult & 0xFFFF;
            return win32 == WindowsErrorDiskFull || win32 == WindowsErrorHandleDiskFull;
        }
        return hresult == PosixNoSpace;
    }
}
