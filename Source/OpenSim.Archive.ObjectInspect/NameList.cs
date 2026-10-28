/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace OpenSim.Archive.ObjectInspect;

/// <summary>A list of account ids and the names to show for them.</summary>
public static class NameList
{
    public static Dictionary<UUID, string> Parse(string text)
    {
        return new Dictionary<UUID, string>();
    }
}
