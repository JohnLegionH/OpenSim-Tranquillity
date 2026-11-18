/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.OptionalModules.World.NPC;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A bot that a script made with botCreateBot does not outlive its object: removing the object, or taking it into
/// inventory, removes the bots it made that are not persistent. Halcyon's bot manager did not tie a bot to its object
/// (a bot went with botRemoveBot, a teleport home or the region), so this is the rule chosen where Halcyon is silent.
/// A script reset does not remove them, a persistent bot stays (the persistence cleanup retires it when the object is
/// gone), a bot another object made stays, and so does an osNpcCreate NPC, which OpenSim's NPCs have always outlived
/// their object. No process-wide state: each test has its own two regions, so the class runs in parallel.
/// </summary>
public class BotObjectLifetimeTests
{
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(20);

    private static BotRegions Regions(bool persistence = false)
    {
        var r = new BotRegions(persistence: persistence);
        UUID owner = UUID.Random();
        SceneHelpers.AddScenePresence(r.A.Scene, owner);
        SceneHelpers.AddScenePresence(r.B.Scene, owner);
        r.A.Prim.OwnerID = owner;
        r.B.Prim.OwnerID = owner;
        return r;
    }

    private static bool Gone(BotRegions r, UUID bot) => !r.Bots.IsBot(bot) && r.B.Scene.GetScenePresence(bot) == null;

    private static void Remove(BotRegions r) => r.B.Scene.DeleteSceneObject(r.B.Prim.ParentGroup, false);

    [Fact]
    public void RemovingTheObjectRemovesTheBotsItMade()
    {
        using var r = Regions();
        UUID one = BotRegions.MakeBot(r.B, "One");
        UUID two = BotRegions.MakeBot(r.B, "Two", "<130, 130, 30>");
        Assert.True(r.Bots.IsBot(one) && r.Bots.IsBot(two));

        Remove(r);

        Assert.True(r.B.PumpUntil(() => Gone(r, one) && Gone(r, two), Cap), "the bots are still there");
    }

    [Fact]
    public void ADyingObjectRemovesItsBotToo()
    {
        using var r = Regions();
        BotRegions.Run(r.B, "key b = botCreateBot(\"One\", \"Bot\", \"\", <128, 128, 30>, 0); llSay(0, \"bot=\" + (string)b);");
        UUID bot = BotRegions.KeyOf(r.B, "bot=");
        Assert.True(r.Bots.IsBot(bot));

        r.B.RezScriptInto(r.B.Prim, "default { state_entry() { llDie(); } }");

        Assert.True(r.B.PumpUntil(() => Gone(r, bot), Cap), "the bot is still there");
    }

    [Fact]
    public void TakingTheObjectIntoInventoryRemovesItsBot()
    {
        using var r = Regions();
        var client = r.B.AddClient();
        r.B.Prim.OwnerID = client.AgentId;
        r.B.Scene.InventoryService.CreateUserInventory(client.AgentId);
        UUID bot = BotRegions.MakeBot(r.B, "One");

        r.B.Scene.DeRezObjects(client, new List<uint> { r.B.Prim.LocalId }, UUID.Zero, DeRezAction.Take, UUID.Zero);

        Assert.True(r.B.PumpUntil(() => Gone(r, bot), Cap), "the bot is still there");
        Assert.True(r.B.Prim.ParentGroup.IsDeleted, "the object was not taken");
    }

    [Fact]
    public void ABotAnotherObjectMadeStays()
    {
        using var r = Regions();
        UUID mine = BotRegions.MakeBot(r.B, "Mine");
        var other = SceneHelpers.AddSceneObject(r.B.Scene, "Other prim", r.B.Prim.OwnerID).RootPart;
        r.B.RezScriptInto(other, "default { state_entry() { key b = botCreateBot(\"Other\", \"Bot\", \"\", <140, 140, 30>, 0); llSay(0, \"other=\" + (string)b); } }");
        Assert.True(r.B.PumpUntil(() => r.B.Said.Any(s => s.StartsWith("other=", StringComparison.Ordinal))), string.Join(" | ", r.B.Said));
        UUID theirs = BotRegions.KeyOf(r.B, "other=");
        Assert.True(r.Bots.IsBot(theirs));

        Remove(r);

        Assert.True(r.B.PumpUntil(() => Gone(r, mine), Cap), "the removed object's bot is still there");
        Assert.True(r.Bots.IsBot(theirs));
        Assert.NotNull(r.B.Scene.GetScenePresence(theirs));
    }

    [Fact]
    public void AScriptResetDoesNotRemoveTheBot()
    {
        using var r = Regions();
        // The first run makes the bot and resets the script; the run after the reset says so.
        r.B.RezScript(@"default { state_entry() {
            if (llGetObjectDesc() == """") {
                key b = botCreateBot(""One"", ""Bot"", """", <128, 128, 30>, 0);
                llSetObjectDesc((string)b);
                llResetScript();
            } else llSay(0, ""after reset "" + llGetObjectDesc());
        } }");
        Assert.True(r.B.PumpUntil(() => r.B.Said.Any(s => s.StartsWith("after reset ", StringComparison.Ordinal))), string.Join(" | ", r.B.Said));
        UUID bot = BotRegions.KeyOf(r.B, "after reset ");

        // Proving something does NOT happen: a window in which a removal on reset would have shown.
        r.B.PumpFor(TimeSpan.FromSeconds(1));
        Assert.NotEqual(UUID.Zero, bot);
        Assert.True(r.Bots.IsBot(bot));
        Assert.NotNull(r.B.Scene.GetScenePresence(bot));
    }

    [Fact]
    public void APersistentBotStaysWhenItsObjectGoes()
    {
        using var r = Regions(persistence: true);
        UUID bot = BotRegions.MakeBot(r.B, "One");
        BotRegions.Run(r.B, "llSay(0, \"p=\" + (string)botSetPersistent(\"" + bot + "\", 0));");
        Assert.Equal(BotPersistError.OK, int.Parse(BotRegions.SaidAfter(r.B, "p=")));

        Remove(r);

        // Proving something does NOT happen: a window in which a removal would have shown.
        r.B.PumpFor(TimeSpan.FromSeconds(1));
        Assert.True(r.Bots.IsBot(bot));
        Assert.NotNull(r.B.Scene.GetScenePresence(bot));
    }

    [Fact]
    public void AnOsNpcStaysWhenItsObjectGoes()
    {
        using var r = Regions();
        BotRegions.Run(r.B, "key n = osNpcCreate(\"Npc\", \"One\", <128, 128, 30>, \"\"); llSay(0, \"npc=\" + (string)n);");
        UUID npc = BotRegions.KeyOf(r.B, "npc=");
        Assert.NotNull(r.B.Scene.GetScenePresence(npc));

        Remove(r);

        // Proving something does NOT happen: a window in which a removal would have shown.
        r.B.PumpFor(TimeSpan.FromSeconds(1));
        Assert.NotNull(r.B.Scene.GetScenePresence(npc));
    }
}
