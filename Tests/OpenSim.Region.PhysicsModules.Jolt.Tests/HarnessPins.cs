/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Tests that hold an invariant the harness's earlier avatar (0.45 x 0.6 x 1.9 m) keeps and the simulator's avatar
/// (0.45 x 0.6 x 2.1 m, what <c>AvatarAppearance.SetSize</c> hands the engine) does not, run with the earlier avatar and say
/// so here. Each such test passes <see cref="EarlierAvatar"/> as <c>SimulatorAvatars</c>; setting it to true gives them the
/// simulator's avatar, and a search for the name lists them. They are the avatar hits where a box thrown at the avatar
/// moves it faster than the two's common speed allows, a flying avatar that follows a fast linkset up by more than its
/// limit, and the avatar's reported strike speed on a 10 m/s hit.
/// </summary>
internal static class HarnessPins
{
    internal static readonly bool EarlierAvatar = false;
}
