/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Framework;

namespace OpenSim.Region.Framework.Scenes;

/// <summary>TEMPORARY measurement aid for border crossings. Not for upstream. See <see cref="CrossTraceLog"/>.</summary>
public static class CrossTrace
{
    public static void Mark(SceneObjectGroup grp, string phase, string detail = "")
    {
        if (!CrossTraceLog.Enabled || grp is null)
            return;
        CrossTraceLog.Mark(grp.Scene?.Name ?? "?", grp.Name, phase, detail);
    }

    public static void Mark(SceneObjectPart part, string phase, string detail = "")
    {
        if (!CrossTraceLog.Enabled || part is null)
            return;
        Mark(part.ParentGroup, phase, detail);
    }
}
