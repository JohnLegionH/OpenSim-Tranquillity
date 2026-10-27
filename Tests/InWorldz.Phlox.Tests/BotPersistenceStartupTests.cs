/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using OpenMetaverse;
using OpenSim.Region.OptionalModules.World.NPC;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A region's modules are loaded before the region loads its objects (the simulator loads the objects after every
/// module's RegionLoaded). A persistent bot's creator object is therefore not in the region yet when the bots are
/// brought back, and it must not be taken for gone then: the check for bots whose object is gone waits until the
/// region has loaded its objects (the PrimsLoaded event). No process-wide state: each test has its own two regions and
/// database folder, so the class runs in parallel.
/// </summary>
public class BotPersistenceStartupTests
{
    private const int CreatorTail = 0x77;
    private static readonly UUID Creator = TestHelpers.ParseTail(CreatorTail);
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(40);

    /// <summary>A persistent bot in region B that names an object, which is not in the region, as its creator.</summary>
    private static (BotRegions R, UUID Owner, UUID Bot) PersistentBotOfAnObjectNotYetLoaded()
    {
        var r = new BotRegions(persistence: true);
        UUID owner = UUID.Random();
        SceneHelpers.AddScenePresence(r.B.Scene, owner);
        UUID bot = r.Bots.CreateBotInRegion(r.B.Scene, "Bee", "Bot", new Vector3(128, 128, 30), "", UUID.Zero, owner,
            true, true, true, Creator, out string reason);
        Assert.True(bot != UUID.Zero, reason);
        Assert.Equal(BotPersistError.OK, r.Bots.SetBotPersistent(r.B.Scene, bot, owner, UUID.Zero, Creator, 0));
        return (r, owner, bot);
    }

    [Fact]
    public void APersistentBotComesBackWhenItsObjectIsLoadedAfterTheModulesAre()
    {
        var (r, owner, bot) = PersistentBotOfAnObjectNotYetLoaded();
        using (r)
        {
            r.Restart(primsLoaded: false);       // the modules load; the creator object is not in the region
            var creator = SceneHelpers.CreateSceneObject(1, owner, "Creator ", CreatorTail);
            r.B.Scene.AddNewSceneObject(creator, false);
            r.B.Scene.EventManager.TriggerPrimsLoaded(r.B.Scene);

            Assert.True(r.B.PumpUntil(() => r.B.Scene.GetScenePresence(bot) != null, Cap), "the bot never came back");
            Assert.True(r.Bots.IsBot(bot));
            Assert.True(r.Bots.IsBotPersistent(r.B.Scene, bot));
        }
    }

    [Fact]
    public void APersistentBotIsNotBroughtBackBeforeTheRegionHasLoadedItsObjects()
    {
        var (r, owner, bot) = PersistentBotOfAnObjectNotYetLoaded();
        using (r)
        {
            r.Restart(primsLoaded: false);
            r.B.Scene.AddNewSceneObject(SceneHelpers.CreateSceneObject(1, owner, "Creator ", CreatorTail), false);

            // Proving something does NOT happen: a window over which the bot would have been made.
            r.B.PumpFor(TimeSpan.FromSeconds(2));
            Assert.Null(r.B.Scene.GetScenePresence(bot));

            r.B.Scene.EventManager.TriggerPrimsLoaded(r.B.Scene);
            Assert.True(r.B.PumpUntil(() => r.B.Scene.GetScenePresence(bot) != null, Cap), "the bot never came back");
        }
    }

    [Fact]
    public void ABotWhoseObjectIsStillMissingOnceTheObjectsAreLoadedIsRetiredAndNotBroughtBack()
    {
        var (r, _, bot) = PersistentBotOfAnObjectNotYetLoaded();
        using (r)
        {
            r.Restart(primsLoaded: false);
            r.A.Scene.EventManager.TriggerPrimsLoaded(r.A.Scene);
            r.B.Scene.EventManager.TriggerPrimsLoaded(r.B.Scene);       // the objects are loaded; the creator is not among them

            // Proving something does NOT happen: a window over which the queued bot would have been made.
            r.B.PumpFor(TimeSpan.FromSeconds(2));
            Assert.Null(r.B.Scene.GetScenePresence(bot));
            Assert.False(r.Bots.IsBot(bot));
            Assert.False(r.Bots.IsBotPersistent(r.B.Scene, bot));
        }
    }
}
