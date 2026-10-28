/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using OpenMetaverse;
using OpenSim.Region.OptionalModules.World.NPC;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Each region keeps its own persistent bots: a persistent bot made in any region is persistent there, is saved with its
/// region when it shuts down, and comes back in that region after a restart. No process-wide state: each test has its
/// own two regions and its own database folder, so the class runs in parallel.
/// </summary>
public class BotPersistenceRegionTests
{
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(40);

    /// <summary>Two regions with the owner in both, so the owner may rez on each region's parcel.</summary>
    private static BotRegions Regions()
    {
        var r = new BotRegions(persistence: true);
        UUID owner = UUID.Random();
        SceneHelpers.AddScenePresence(r.A.Scene, owner);
        SceneHelpers.AddScenePresence(r.B.Scene, owner);
        r.A.Prim.OwnerID = owner;
        r.B.Prim.OwnerID = owner;
        return r;
    }

    private static int MakePersistent(SchedulerHarness h, UUID bot)
    {
        BotRegions.Run(h, "llSay(0, \"p=\" + (string)botSetPersistent(\"" + bot + "\", 0));");
        return int.Parse(BotRegions.SaidAfter(h, "p="));
    }

    private static int IsPersistent(SchedulerHarness h, UUID bot)
    {
        BotRegions.Run(h, "llSay(0, \"ip=\" + (string)botIsPersistent(\"" + bot + "\"));");
        return int.Parse(BotRegions.SaidAfter(h, "ip="));
    }

    private static void Walk(SchedulerHarness h, UUID bot, string pos)
        => BotRegions.Run(h, "botSetNavigationPoints(\"" + bot + "\", [" + pos + "], [BOT_TRAVELMODE_TELEPORT], []);");

    [Fact]
    public void ABotMadeInEitherRegionIsPersistentInThatRegion()
    {
        using var r = Regions();
        UUID a = BotRegions.MakeBot(r.A, "Aye");
        UUID b = BotRegions.MakeBot(r.B, "Bee");

        Assert.Equal(BotPersistError.OK, MakePersistent(r.A, a));
        Assert.Equal(BotPersistError.OK, MakePersistent(r.B, b));

        Assert.Equal(1, IsPersistent(r.A, a));
        Assert.Equal(1, IsPersistent(r.B, b));
        Assert.Empty(BotRegions.Errors(r.A));
        Assert.Empty(BotRegions.Errors(r.B));
    }

    [Fact]
    public void APersistentBotComesBackInItsOwnRegionAtItsSavedPlaceAfterARestart()
    {
        using var r = Regions();
        UUID a = BotRegions.MakeBot(r.A, "Aye");
        UUID b = BotRegions.MakeBot(r.B, "Bee");
        Assert.Equal(BotPersistError.OK, MakePersistent(r.A, a));
        Assert.Equal(BotPersistError.OK, MakePersistent(r.B, b));
        Walk(r.A, a, "<60, 70, 30>");
        Walk(r.B, b, "<90, 100, 30>");

        r.Restart();

        Assert.True(r.A.PumpUntil(() => r.A.Scene.GetScenePresence(a) != null && r.B.Scene.GetScenePresence(b) != null, Cap),
            "the bots never came back");
        Assert.Null(r.B.Scene.GetScenePresence(a));
        Assert.Null(r.A.Scene.GetScenePresence(b));
        Assert.Equal(2, r.Bots.GetAllBots().Count);
        var posA = r.A.Scene.GetScenePresence(a).AbsolutePosition;
        var posB = r.B.Scene.GetScenePresence(b).AbsolutePosition;
        Assert.True(Math.Abs(posA.X - 60) < 2 && Math.Abs(posA.Y - 70) < 2, "region A's bot came back at " + posA);
        Assert.True(Math.Abs(posB.X - 90) < 2 && Math.Abs(posB.Y - 100) < 2, "region B's bot came back at " + posB);
    }

    [Fact]
    public void ABotRemovedByItsScriptIsNotBroughtBackInEitherRegion()
    {
        using var r = Regions();
        UUID a = BotRegions.MakeBot(r.A, "Aye");
        UUID b = BotRegions.MakeBot(r.B, "Bee");
        UUID keptA = BotRegions.MakeBot(r.A, "Keep");
        Assert.Equal(BotPersistError.OK, MakePersistent(r.A, a));
        Assert.Equal(BotPersistError.OK, MakePersistent(r.B, b));
        Assert.Equal(BotPersistError.OK, MakePersistent(r.A, keptA));
        BotRegions.Run(r.A, "botRemoveBot(\"" + a + "\");");
        BotRegions.Run(r.B, "botRemoveBot(\"" + b + "\");");

        r.Restart();

        // The bot that was kept comes back; wait for it, then the removed ones have had their turn.
        Assert.True(r.A.PumpUntil(() => r.A.Scene.GetScenePresence(keptA) != null, Cap), "the kept bot never came back");
        Assert.Equal(new[] { keptA }, r.Bots.GetAllBots().ToArray());
        Assert.Null(r.A.Scene.GetScenePresence(a));
        Assert.Null(r.B.Scene.GetScenePresence(b));
    }

    [Fact]
    public void ARegionThatShutsDownAloneSavesAndLaterRestoresItsOwnBots()
    {
        using var r = Regions();
        UUID a = BotRegions.MakeBot(r.A, "Aye");
        UUID b = BotRegions.MakeBot(r.B, "Bee");
        Assert.Equal(BotPersistError.OK, MakePersistent(r.A, a));
        Assert.Equal(BotPersistError.OK, MakePersistent(r.B, b));
        Walk(r.A, a, "<40, 50, 30>");

        // Region A leaves the simulator; region B keeps running with its bot.
        r.Bots.RemoveRegion(r.A.Scene);

        Assert.Null(r.A.Scene.GetScenePresence(a));
        Assert.NotNull(r.B.Scene.GetScenePresence(b));
        Assert.True(r.Bots.IsBot(b));
        Assert.False(r.Bots.IsBot(a));
        // Region B still answers for its own bot.
        Assert.Equal(1, IsPersistent(r.B, b));

        // Region A loads again: its bot comes back where it was saved.
        r.Bots.AddRegion(r.A.Scene);
        r.Bots.RegionLoaded(r.A.Scene);
        r.A.Scene.EventManager.TriggerPrimsLoaded(r.A.Scene);
        Assert.True(r.A.PumpUntil(() => r.A.Scene.GetScenePresence(a) != null, Cap), "region A's bot never came back");
        var pos = r.A.Scene.GetScenePresence(a).AbsolutePosition;
        Assert.True(Math.Abs(pos.X - 40) < 2 && Math.Abs(pos.Y - 50) < 2, "region A's bot came back at " + pos);
    }
}
