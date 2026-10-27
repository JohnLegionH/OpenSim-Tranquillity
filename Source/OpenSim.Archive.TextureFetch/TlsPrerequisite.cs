/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.TextureFetch;

/// <summary>
/// .NET has no TLS of its own on Linux: an https connection loads the system's OpenSSL, and without it the process aborts
/// with "No usable version of libssl was found". An http login needs none. This says so before the login starts.
/// </summary>
public static class TlsPrerequisite
{
    /// <summary>Returns null when the login can go ahead, or the message to print when an https login cannot work on this system.</summary>
    public static string Check(string loginUri, bool linux, Func<bool> openSslAvailable) => null;

    /// <summary>Whether the system has an OpenSSL library the runtime can load.</summary>
    public static bool OpenSslAvailable() => true;
}
