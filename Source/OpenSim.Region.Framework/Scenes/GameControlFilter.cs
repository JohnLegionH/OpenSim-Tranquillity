/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse.Packets;
using OpenSim.Framework;

namespace OpenSim.Region.Framework.Scenes;

// Temporary stub so the tests compile: every member returns a default value.
public sealed class GameControlFilter
{
    public const int MaxEventsPerSecond = 60;

    public GameControlFilter(Func<long> nowMilliseconds = null) { }

    public GameControlSnapshot Process(GameControlDataPacket packet) => null;
}
