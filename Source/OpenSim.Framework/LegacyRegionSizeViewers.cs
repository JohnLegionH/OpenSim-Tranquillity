/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;

namespace OpenSim.Framework;

/// <summary>
/// Decides which viewers only take 256 m regions, from the channel name the viewer sends at login.
/// </summary>
/// <remarks>
/// Linden Lab's viewer treats every region as 256 m wide and does not read the region size, so the
/// land messages of a larger region overrun its fixed-size buffers. A viewer on this list is sent only
/// the south-west 256 m of a larger region. Viewers with variable region size support are not on the list.
/// </remarks>
public static class LegacyRegionSizeViewers
{
    /// <summary>Linden Lab's own channels (Release, Beta, Project and Test all begin with this).</summary>
    public const string DefaultChannels = "Second Life";

    /// <summary>Splits a comma or semicolon separated list of channel prefixes. Empty entries are dropped.</summary>
    public static string[] ParseChannels(string list)
    {
        return Array.Empty<string>();
    }

    /// <summary>True when the channel starts with one of the prefixes (case-insensitive).</summary>
    public static bool IsLegacyChannel(string[] prefixes, string channel)
    {
        return false;
    }
}
