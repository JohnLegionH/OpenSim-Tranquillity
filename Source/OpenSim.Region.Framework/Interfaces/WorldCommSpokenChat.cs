/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;

namespace OpenSim.Region.Framework.Interfaces;

/// <summary>
/// Marks, on the speaking thread, chat a script engine offers to <see cref="IWorldComm"/> after it has already spoken it as
/// scene chat (Scene.SimChat). Every script engine's listens hear scene chat, so the copy IWorldComm raises for it
/// (<see cref="IWorldComm.OnMessageDelivered"/>) must not be delivered again by any engine, whichever engine hosts the
/// speaking script.
/// </summary>
/// <remarks>
/// The mark lives here, in the region framework, because each script engine module is loaded as a plugin with its own
/// load context: a static in an engine's assembly can exist once per engine that loads it, so a mark kept there is not
/// seen by the other engines. This assembly is the host's, loaded once and shared by every plugin.
/// </remarks>
public static class WorldCommSpokenChat
{
    [ThreadStatic] private static bool t_offering;

    /// <summary>True while <see cref="Offer"/> runs on this thread.</summary>
    public static bool Offering => t_offering;

    /// <summary>Runs <paramref name="send"/> (the offer to IWorldComm) with the mark set on this thread.</summary>
    public static void Offer(Action send)
    {
        bool was = t_offering;
        t_offering = true;
        try { send(); }
        finally { t_offering = was; }
    }
}
