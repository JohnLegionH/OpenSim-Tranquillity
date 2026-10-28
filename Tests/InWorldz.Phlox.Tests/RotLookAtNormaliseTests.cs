/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llRotLookAt takes a rotation, and a script's quaternion need not be unit length (llRotBetween of two vectors that are not
/// unit gives a scaled one). On an object that is not physical it sets the rotation as llSetLocalRot does (the SL wiki's
/// llRotLookAt page: "In Non-Physical objects this function operates effectively the same as llSetLocalRot"), so a target
/// that is not unit length gives the same rotation as the same target made unit length, and a zero one reads as no rotation,
/// as it does for llSetRot and llSetLocalRot. Calls the API directly on the harness prim; no clock and no process-wide
/// state, so the class runs in parallel.
/// </summary>
public class RotLookAtNormaliseTests
{
    private static LSLSystemAPI Api(SchedulerHarness h) => new LSLSystemAPI(h.Engine, h.Prim, h.Prim.LocalId, UUID.Random());

    private static readonly Quaternion Unit = Quaternion.Normalize(new Quaternion(0.1f, -0.3f, 0.5f, 0.8f));

    private static Quaternion Result(Quaternion target)
    {
        using var h = new SchedulerHarness();
        Api(h).llRotLookAt(target, 0f, 0f);
        return h.Prim.RotationOffset;
    }

    private static void Same(Quaternion expected, Quaternion actual)
        => Assert.True(Math.Abs(expected.X * actual.X + expected.Y * actual.Y + expected.Z * actual.Z + expected.W * actual.W) > 0.99999f,
                       $"expected {expected}, got {actual}");

    [Theory]
    [InlineData(0.01f)]
    [InlineData(3f)]
    [InlineData(250f)]
    public void A_target_that_is_not_unit_length_gives_the_rotation_of_the_unit_one(float scale)
    {
        Quaternion scaled = new(Unit.X * scale, Unit.Y * scale, Unit.Z * scale, Unit.W * scale);
        Same(Result(Unit), Result(scaled));
        Same(Unit, Result(scaled));
    }

    [Fact]
    public void A_zero_target_reads_as_no_rotation()
    {
        Same(Quaternion.Identity, Result(new Quaternion(0f, 0f, 0f, 0f)));
    }
}
