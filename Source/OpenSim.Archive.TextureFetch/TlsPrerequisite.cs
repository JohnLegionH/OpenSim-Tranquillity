/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Runtime.InteropServices;

namespace OpenSim.Archive.TextureFetch;

/// <summary>
/// .NET has no TLS of its own on Linux: an https connection loads the system's OpenSSL, and without it the process aborts
/// with "No usable version of libssl was found". An http login needs none. This says so before the login starts.
/// </summary>
public static class TlsPrerequisite
{
    // the library names the runtime tries for OpenSSL 3 and 1.1, in its own order
    private static readonly string[] LibraryNames = { "libssl.so.3", "libssl.so.1.1" };

    /// <summary>Returns null when the login can go ahead, or the message to print when an https login cannot work on this system.</summary>
    public static string Check(string loginUri, bool linux, Func<bool> openSslAvailable)
    {
        if (!linux || !Uri.TryCreate(loginUri, UriKind.Absolute, out Uri uri) || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return null;
        if (openSslAvailable())
            return null;
        return "an https login needs the system's OpenSSL library (libssl), and this system does not have one the runtime can load; "
            + "install it (Debian and Ubuntu: apt install libssl3t64) or use an http login URI";
    }

    /// <summary>Whether the system has an OpenSSL library the runtime can load.</summary>
    public static bool OpenSslAvailable()
    {
        foreach (string name in LibraryNames)
        {
            if (NativeLibrary.TryLoad(name, out IntPtr handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }
        return false;
    }
}
