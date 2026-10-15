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
/// simulator's avatar, and a search for the name lists them. Two rows are left: a 100 kg box thrown at a standing avatar
/// at one physics step per heartbeat, where the avatar's position moves 0.12 m more in one heartbeat than its velocity
/// says (its velocity stays within the momentum limit; the box is overlapping it at the start of that heartbeat), and a fast linkset thrown at a flying avatar,
/// where the avatar ends 0.38 m below where it flew, once the linkset has passed it (the 0.3 m allowance was set with the
/// shorter avatar; not traced).
/// </summary>
internal static class HarnessPins
{
    internal static readonly bool EarlierAvatar = false;
}
