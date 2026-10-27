/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using OpenMetaverse;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Bot movement, navigation and positioning are bounded by the region the bot is in, not by 256 m: a bot in a
/// 1,024 m region goes to any point inside it. Halcyon kept a bot's points inside its region with
/// Util.ForceValidRegionXYZ and its bots never crossed a region border (its bot movement code has no crossing), so a
/// point outside the region is held at the region's edge. No process-wide state: each test has its own two regions,
/// so the class runs in parallel.
/// </summary>
public class BotVarregionTests
{
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(20);

    private static bool Near(Vector3 p, float x, float y, float within) => Math.Abs(p.X - x) <= within && Math.Abs(p.Y - y) <= within;

    private static Vector3 PositionOf(SchedulerHarness h, UUID bot) => h.Scene.GetScenePresence(bot).AbsolutePosition;

    private static BotRegions Varregion()
    {
        var r = new BotRegions(sizeB: 1024);
        r.B.Prim.OwnerID = UUID.Random();
        r.A.Prim.OwnerID = UUID.Random();
        return r;
    }

    [Fact]
    public void ANavigationPointBeyond256mInA1024mRegionIsWhereTheBotGoes()
    {
        using var r = Varregion();
        UUID bot = BotRegions.MakeBot(r.B, "Bee", "<128, 128, 30>");

        BotRegions.Run(r.B, "botSetNavigationPoints(\"" + bot + "\", [<700, 600, 50>], [BOT_TRAVELMODE_TELEPORT], []);");

        Assert.True(r.B.PumpUntil(() => Near(PositionOf(r.B, bot), 700, 600, 1), Cap), "the bot is at " + PositionOf(r.B, bot));
    }

    [Fact]
    public void ANavigationPointNearTheFarEdgeOfA1024mRegionIsKept()
    {
        using var r = Varregion();
        UUID bot = BotRegions.MakeBot(r.B, "Bee", "<128, 128, 30>");

        BotRegions.Run(r.B, "botSetNavigationPoints(\"" + bot + "\", [<1000, 1010, 50>], [BOT_TRAVELMODE_TELEPORT], []);");

        Assert.True(r.B.PumpUntil(() => Near(PositionOf(r.B, bot), 1000, 1010, 1), Cap), "the bot is at " + PositionOf(r.B, bot));
    }

    [Fact]
    public void ANavigationPointOutsideTheRegionIsHeldAtItsEdge()
    {
        using var r = Varregion();
        UUID bot = BotRegions.MakeBot(r.B, "Bee", "<128, 128, 30>");

        BotRegions.Run(r.B, "botSetNavigationPoints(\"" + bot + "\", [<1500, 1100, 50>], [BOT_TRAVELMODE_TELEPORT], []);");

        Assert.True(r.B.PumpUntil(() => PositionOf(r.B, bot).X > 1000, Cap), "the bot is at " + PositionOf(r.B, bot));
        var p = PositionOf(r.B, bot);
        Assert.True(p.X <= 1024f && p.Y <= 1024f, "the bot is at " + p);
        Assert.True(p.X > 1020f && p.Y > 1020f, "the bot is at " + p);
    }

    [Fact]
    public void ANavigationPointIsLiftedToTheGroundAtThatPointInA1024mRegion()
    {
        using var r = Varregion();
        r.B.Scene.Heightmap[700, 600] = 80f;
        UUID bot = BotRegions.MakeBot(r.B, "Bee", "<128, 128, 30>");

        BotRegions.Run(r.B, "botSetNavigationPoints(\"" + bot + "\", [<700, 600, 1>], [BOT_TRAVELMODE_TELEPORT], []);");

        Assert.True(r.B.PumpUntil(() => Near(PositionOf(r.B, bot), 700, 600, 1), Cap), "the bot is at " + PositionOf(r.B, bot));
        Assert.True(PositionOf(r.B, bot).Z >= 79.9f, "the bot is at " + PositionOf(r.B, bot));
    }

    [Fact]
    public void AWanderAreaBeyond256mInA1024mRegionIsWanderedIn()
    {
        using var r = Varregion();
        UUID bot = BotRegions.MakeBot(r.B, "Bee", "<128, 128, 30>");

        // Option 1 is BOT_WANDER_MOVEMENT_TYPE; the wander points are teleported to, so the bot gets there at once.
        BotRegions.Run(r.B, "botWanderWithin(\"" + bot + "\", <900, 900, 30>, 3.0, 3.0, [1, BOT_TRAVELMODE_TELEPORT]);");

        Assert.True(r.B.PumpUntil(() => Near(PositionOf(r.B, bot), 900, 900, 4), Cap), "the bot is at " + PositionOf(r.B, bot));
    }

    [Fact]
    public void ABotWalkingToAPointBeyond256mIsSentThere()
    {
        using var r = Varregion();
        UUID bot = BotRegions.MakeBot(r.B, "Bee", "<128, 128, 30>");

        // The bot is not moved by physics in the test region; the test reads where the bot was last sent. Far to the
        // east and a little to the north: a bot held to 256 m would be sent to <256, 256>.
        BotRegions.Run(r.B, "botSetNavigationPoints(\"" + bot + "\", [<900, 300, 30>], [BOT_TRAVELMODE_WALK], [BOT_MOVEMENT_TELEPORT_AFTER, 600]);");

        Assert.True(r.B.PumpUntil(() =>
        {
            var sp = r.B.Scene.GetScenePresence(bot);
            return sp.MovingToTarget && Math.Abs(sp.MoveToPositionTarget.X - 900f) < 0.01f && Math.Abs(sp.MoveToPositionTarget.Y - 300f) < 0.01f;
        }, Cap), "the bot is heading for " + r.B.Scene.GetScenePresence(bot).MoveToPositionTarget);
    }

    [Fact]
    public void A256mRegionStillHoldsAPointAtItsEdge()
    {
        using var r = Varregion();
        UUID bot = BotRegions.MakeBot(r.A, "Aye", "<128, 128, 30>");

        BotRegions.Run(r.A, "botSetNavigationPoints(\"" + bot + "\", [<300, 300, 50>], [BOT_TRAVELMODE_TELEPORT], []);");

        Assert.True(r.A.PumpUntil(() => PositionOf(r.A, bot).X > 200f, Cap), "the bot is at " + PositionOf(r.A, bot));
        var p = PositionOf(r.A, bot);
        Assert.True(p.X <= 256f && p.Y <= 256f, "the bot is at " + p);
        Assert.True(p.X > 250f && p.Y > 250f, "the bot is at " + p);
    }
}
