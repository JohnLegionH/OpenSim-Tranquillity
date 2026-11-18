/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A bot is made in the region of the script that makes it (as Halcyon's per-region bot manager did), whether the
/// owner is in that region, in another region, or away. With the owner away and no outfit notecard it is made from the
/// owner's stored appearance, and the wearables the stored record names by item are resolved to their assets from the
/// owner's inventory, so the bot has something to bake. No process-wide state: each test has its own two regions, so
/// the class runs in parallel.
/// </summary>
public class BotMadeInScriptRegionTests
{
    private const int Marker = 25;           // a visual parameter this look sets, to tell one look from another
    private const byte Stored = 77;
    private const byte Current = 111;

    private static void AssertIn(BotRegions r, Region inRegion, UUID bot)
    {
        Scene mine = inRegion == Region.A ? r.A.Scene : r.B.Scene;
        Scene other = inRegion == Region.A ? r.B.Scene : r.A.Scene;
        Assert.True(r.Bots.IsBot(bot));
        Assert.NotNull(mine.GetScenePresence(bot));
        Assert.Null(other.GetScenePresence(bot));
    }

    private enum Region { A, B }

    [Fact]
    public void AScriptInRegionBMakesItsBotInRegionBWhenTheOwnerIsInRegionA()
    {
        using var r = new BotRegions();
        UUID owner = UUID.Random();
        SceneHelpers.AddScenePresence(r.A.Scene, owner);
        r.B.Prim.OwnerID = owner;

        UUID bot = BotRegions.MakeBot(r.B, "Bee");

        Assert.NotEqual(UUID.Zero, bot);
        AssertIn(r, Region.B, bot);
    }

    [Fact]
    public void AScriptInRegionAMakesItsBotInRegionAWhenTheOwnerIsInRegionB()
    {
        using var r = new BotRegions();
        UUID owner = UUID.Random();
        SceneHelpers.AddScenePresence(r.B.Scene, owner);
        r.A.Prim.OwnerID = owner;

        UUID bot = BotRegions.MakeBot(r.A, "Aye");

        Assert.NotEqual(UUID.Zero, bot);
        AssertIn(r, Region.A, bot);
    }

    [Fact]
    public void WithTheOwnerAwayTheBotIsMadeInTheScriptsRegionNotTheFirstRegion()
    {
        using var r = new BotRegions();
        r.B.Prim.OwnerID = UUID.Random();       // nobody of that key is in either region

        UUID bot = BotRegions.MakeBot(r.B, "Bee");

        Assert.NotEqual(UUID.Zero, bot);
        AssertIn(r, Region.B, bot);
        Assert.Empty(BotRegions.Errors(r.B));
    }

    [Fact]
    public void WithTheOwnerAwayTheBotWearsTheOwnersStoredLookWithTheWearablesResolved()
    {
        using var r = new BotRegions();
        UUID owner = UUID.Random();
        r.B.Prim.OwnerID = owner;
        r.B.Scene.InventoryService.CreateUserInventory(owner);
        var worn = UserInventoryHelpers.CreateInventoryItem(r.B.Scene, "shape", UUID.Random(), UUID.Random(), owner, InventoryType.Notecard);
        UUID shapeItem = worn.ID, shapeAsset = worn.AssetID;
        // The record names the worn shape by its inventory item only, as a record written without the assets does.
        var stored = new AvatarAppearance();
        stored.Wearables[(int)WearableType.Shape].Wear(shapeItem, UUID.Zero);
        stored.VisualParams[Marker] = Stored;
        r.Avatars.SetAvatar(owner, new AvatarData(stored));

        UUID bot = BotRegions.MakeBot(r.B, "Bee");

        Assert.NotEqual(UUID.Zero, bot);
        AssertIn(r, Region.B, bot);
        var look = r.B.Scene.GetScenePresence(bot).Appearance;
        Assert.Equal(Stored, look.VisualParams[Marker]);
        var shape = look.Wearables[(int)WearableType.Shape];
        Assert.True(shape.Count > 0 && shape[0].ItemID == shapeItem, "the bot does not wear the stored shape item");
        Assert.Equal(shapeAsset, shape[0].AssetID);
    }

    [Fact]
    public void OsNpcCreateMakesItsNpcInTheScriptsRegionToo()
    {
        using var r = new BotRegions();
        r.B.Prim.OwnerID = UUID.Random();

        BotRegions.Run(r.B, "key n = osNpcCreate(\"Npc\", \"Bee\", <128, 128, 30>, \"\"); llSay(0, \"npc=\" + (string)n);");
        UUID npc = BotRegions.KeyOf(r.B, "npc=");

        Assert.NotEqual(UUID.Zero, npc);
        AssertIn(r, Region.B, npc);
    }

    [Fact]
    public void TheOwnerInTheScriptsRegionStillGivesTheBotTheOwnersCurrentLook()
    {
        using var r = new BotRegions();
        UUID owner = UUID.Random();
        var sp = SceneHelpers.AddScenePresence(r.B.Scene, owner);
        sp.Appearance.VisualParams[Marker] = Current;
        r.B.Prim.OwnerID = owner;
        var stored = new AvatarAppearance();
        stored.VisualParams[Marker] = Stored;
        r.Avatars.SetAvatar(owner, new AvatarData(stored));

        UUID bot = BotRegions.MakeBot(r.B, "Bee");

        AssertIn(r, Region.B, bot);
        Assert.Equal(Current, r.B.Scene.GetScenePresence(bot).Appearance.VisualParams[Marker]);
    }
}
