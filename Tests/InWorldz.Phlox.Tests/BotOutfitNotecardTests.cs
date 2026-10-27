/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Avatar.Attachments;
using OpenSim.Region.CoreModules.Avatar.AvatarFactory;
using OpenSim.Region.CoreModules.Avatar.Chat;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.CoreModules.Framework.UserManagement;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.OptionalModules.World.NPC;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A saved bot outfit is an appearance notecard in the calling object's contents, in the format osNpcSaveAppearance
/// writes. botSetOutfit, osNpcSaveAppearance and osOwnerSaveAppearance write it; botCreateBot's outfit argument,
/// osNpcCreate's notecard argument, osNpcLoadAppearance and botChangeOutfit read it. A bot made from it while its
/// owner is away wears what the notecard says. Each test has its own harness and scene, so the class runs in parallel.
/// </summary>
public class BotOutfitNotecardTests
{
    private readonly ITestOutputHelper _out;
    public BotOutfitNotecardTests(ITestOutputHelper o) => _out = o;
    private const int DebugChannel = 0x7FFFFFFF;
    private const int Marker = 25;           // a visual parameter this look sets, to tell one look from another
    private const byte Saved = 111;
    private const byte Changed = 22;

    private static SchedulerHarness NpcScene()
    {
        var h = new SchedulerHarness(cfg =>
        {
            cfg.AddConfig("NPC").Set("Enabled", "true");
            cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "High");
            cfg.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
            cfg.AddConfig("Chat");
        });
        SceneHelpers.SetupSceneModules(h.Scene, h.Config,
            new AvatarFactoryModule(), new UserManagementModule(), new AttachmentsModule(), new NPCModule(),
            new BasicInventoryAccessModule(), new BotManager(), new ChatModule());
        return h;
    }

    private static string Errors(SchedulerHarness h) => string.Join(" | ", h.SaidOn.Where(s => s.Channel == DebugChannel).Select(s => s.Message));

    /// <summary>An owner in the region, wearing a shape and carrying the marker parameter; the prim is the owner's.</summary>
    private sealed record Owner(SchedulerHarness H, ScenePresence Presence, UUID ShapeAsset)
    {
        public UUID Id => Presence.UUID;
    }

    private static Owner Arrive(SchedulerHarness h)
    {
        var client = h.AddClient();
        var sp = h.Scene.GetScenePresence(client.AgentId);
        h.Prim.OwnerID = sp.UUID;
        var shape = UUID.Random();
        sp.Appearance.Wearables[(int)WearableType.Shape].Wear(UUID.Random(), shape);
        sp.Appearance.VisualParams[Marker] = Saved;
        return new Owner(h, sp, shape);
    }

    private static void Run(SchedulerHarness h, string body, string done)
    {
        h.RezScript("default { state_entry() { " + body + " llSay(0, \"" + done + "\"); } }");
        Assert.True(h.PumpUntil(() => h.Said.Contains(done) || Errors(h).Length > 0 && h.Said.Contains(done)),
            "said: " + string.Join(" | ", h.Said) + " errors: " + Errors(h));
    }

    private static TaskInventoryItem NotecardNamed(SceneObjectPart part, string name)
        => part.Inventory.GetInventoryItems().FirstOrDefault(i => i.Type == (int)AssetType.Notecard && i.Name == name);

    private static AvatarAppearance Load(SchedulerHarness h, SceneObjectPart part, string name)
    {
        Assert.True(AppearanceNotecard.TryLoad(part, h.Scene.AssetService, name, out var appearance, out var reason),
            $"notecard '{name}' is not there or not an appearance: {reason}");
        return appearance;
    }

    private static UUID KeyOf(SchedulerHarness h, string prefix)
        => UUID.Parse(h.Said.Last(s => s.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..]);

    private static void AssertWears(ScenePresence bot, UUID shapeAsset, byte marker)
    {
        Assert.NotNull(bot);
        var shape = bot.Appearance.Wearables[(int)WearableType.Shape];
        Assert.True(shape.Count > 0 && shape[0].AssetID == shapeAsset, "the bot's shape is not the saved one");
        Assert.Equal(marker, bot.Appearance.VisualParams[Marker]);
    }

    // ------------------------------------------------------------------ saving

    [Fact]
    public void BotSetOutfitWritesTheOwnersLookAsANotecardInTheCallingObject()
    {
        using var h = NpcScene();
        var owner = Arrive(h);

        Run(h, "botSetOutfit(\"Priest\");", "done");

        var item = NotecardNamed(h.Prim, "Priest");
        Assert.NotNull(item);
        Assert.Equal(owner.Id, item.OwnerID);
        var saved = Load(h, h.Prim, "Priest");
        Assert.Equal(Saved, saved.VisualParams[Marker]);
        Assert.Equal(owner.ShapeAsset, saved.Wearables[(int)WearableType.Shape][0].AssetID);
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void TheNotecardIsInTheFormatOsNpcSaveAppearanceWrites()
    {
        using var h = NpcScene();
        Arrive(h);

        Run(h, "botSetOutfit(\"Priest\");", "done");

        var item = NotecardNamed(h.Prim, "Priest");
        Assert.NotNull(item);
        var data = h.Scene.AssetService.Get(item.AssetID.ToString()).Data;
        var text = Encoding.UTF8.GetString(data);
        Assert.StartsWith("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length ", text);
        Assert.EndsWith("}", text);
        Assert.Contains("<llsd>", text);
        Assert.Contains("visualparams", text);
    }

    [Fact]
    public void SavingAgainUnderTheSameNameReplacesTheNotecard()
    {
        using var h = NpcScene();
        var owner = Arrive(h);
        Run(h, "botSetOutfit(\"Priest\");", "first");
        owner.Presence.Appearance.VisualParams[Marker] = Changed;

        Run(h, "botSetOutfit(\"Priest\");", "second");

        Assert.Single(h.Prim.Inventory.GetInventoryItems().Where(i => i.Type == (int)AssetType.Notecard && i.Name.StartsWith("Priest", StringComparison.Ordinal)));
        Assert.Equal(Changed, Load(h, h.Prim, "Priest").VisualParams[Marker]);
    }

    [Fact]
    public void OsNpcSaveAppearanceWritesTheBotsLookAsANotecardInTheCallingObject()
    {
        using var h = NpcScene();
        var owner = Arrive(h);

        Run(h, "key npc = osNpcCreate(\"Test\", \"Npc\", llGetPos() + <2,0,0>, \"\"); osNpcSaveAppearance(npc, \"NpcLook\");", "done");

        var saved = Load(h, h.Prim, "NpcLook");
        Assert.Equal(Saved, saved.VisualParams[Marker]);
        Assert.Equal(owner.ShapeAsset, saved.Wearables[(int)WearableType.Shape][0].AssetID);
    }

    [Fact]
    public void OsOwnerSaveAppearanceWritesANotecardToo()
    {
        using var h = NpcScene();
        var owner = Arrive(h);

        Run(h, "osOwnerSaveAppearance(\"Mine\");", "done");

        Assert.Equal(owner.ShapeAsset, Load(h, h.Prim, "Mine").Wearables[(int)WearableType.Shape][0].AssetID);
    }

    // ------------------------------------------------------------------ reading

    private static void SaveThenChangeTheOwner(SchedulerHarness h, Owner owner, string extra = "")
    {
        Run(h, "botSetOutfit(\"Priest\");" + extra, "saved");
        owner.Presence.Appearance.VisualParams[Marker] = Changed;
        owner.Presence.Appearance.Wearables[(int)WearableType.Shape].Wear(UUID.Random(), UUID.Random());
    }

    [Fact]
    public void BotCreateBotDressesTheBotFromTheNotecardNotFromTheOwnersCurrentLook()
    {
        using var h = NpcScene();
        var owner = Arrive(h);
        SaveThenChangeTheOwner(h, owner);

        Run(h, "key b = botCreateBot(\"Pri\", \"Est\", \"Priest\", llGetPos() + <2,0,0>, 0); llSay(0, \"bot=\" + (string)b);", "made");

        AssertWears(h.Scene.GetScenePresence(KeyOf(h, "bot=")), owner.ShapeAsset, Saved);
    }

    [Fact]
    public void OsNpcCreateReadsTheNotecardForm()
    {
        using var h = NpcScene();
        var owner = Arrive(h);
        SaveThenChangeTheOwner(h, owner);

        Run(h, "key n = osNpcCreate(\"Pri\", \"Est\", llGetPos() + <2,0,0>, \"Priest\"); llSay(0, \"npc=\" + (string)n);", "made");

        AssertWears(h.Scene.GetScenePresence(KeyOf(h, "npc=")), owner.ShapeAsset, Saved);
    }

    [Fact]
    public void OsNpcLoadAppearanceAndBotChangeOutfitReadTheNotecard()
    {
        using var h = NpcScene();
        var owner = Arrive(h);
        SaveThenChangeTheOwner(h, owner);
        Run(h, "key n = osNpcCreate(\"Pri\", \"Est\", llGetPos() + <2,0,0>, \"\"); llSay(0, \"npc=\" + (string)n);", "made");
        var npc = KeyOf(h, "npc=");
        Assert.Equal(Changed, h.Scene.GetScenePresence(npc).Appearance.VisualParams[Marker]);

        Run(h, "osNpcLoadAppearance(\"" + npc + "\", \"Priest\");", "loaded");

        AssertWears(h.Scene.GetScenePresence(npc), owner.ShapeAsset, Saved);

        h.Scene.GetScenePresence(npc).Appearance.VisualParams[Marker] = Changed;
        Run(h, "botChangeOutfit(\"" + npc + "\", \"Priest\");", "changed");
        Assert.Equal(Saved, h.Scene.GetScenePresence(npc).Appearance.VisualParams[Marker]);
    }

    [Fact]
    public void TheNotecardDressesTheBotWithTheOwnersMemoryStoreEmptied()
    {
        using var h = NpcScene();
        var owner = Arrive(h);
        SaveThenChangeTheOwner(h, owner);
        // botRemoveOutfit now removes the notecard too, so the memory store is emptied through the manager.
        h.Scene.RequestModuleInterface<IBotManager>().RemoveOutfitFromDatabase(owner.Id, "Priest");

        Run(h, "key b = botCreateBot(\"Pri\", \"Est\", \"Priest\", llGetPos() + <2,0,0>, 0); llSay(0, \"bot=\" + (string)b);", "made");

        AssertWears(h.Scene.GetScenePresence(KeyOf(h, "bot=")), owner.ShapeAsset, Saved);
    }

    [Fact]
    public void AnOutfitSavedFromAnotherObjectOfTheOwnerStillDressesTheBotThroughTheMemoryStore()
    {
        using var h = NpcScene();
        var owner = Arrive(h);
        var other = SceneHelpers.AddSceneObject(h.Scene, "other prim", owner.Id);
        h.RezScriptInto(other.RootPart, "default { state_entry() { botSetOutfit(\"Priest\"); llSay(0, \"saved\"); } }");
        Assert.True(h.PumpUntil(() => h.Said.Contains("saved")));
        owner.Presence.Appearance.VisualParams[Marker] = Changed;
        Assert.Null(NotecardNamed(h.Prim, "Priest"));

        Run(h, "key b = botCreateBot(\"Pri\", \"Est\", \"Priest\", llGetPos() + <2,0,0>, 0); llSay(0, \"bot=\" + (string)b);", "made");

        Assert.Equal(Saved, h.Scene.GetScenePresence(KeyOf(h, "bot=")).Appearance.VisualParams[Marker]);
    }

    [Fact]
    public void ANotecardThatIsNotAnAppearanceIsSaidSoAndTheBotIsMadeAsForAnUnknownOutfit()
    {
        using var h = NpcScene();
        Arrive(h);

        Run(h, "osMakeNotecard(\"Junk\", \"hello\"); key n = osNpcCreate(\"Pri\", \"Est\", llGetPos() + <2,0,0>, \"Junk\"); llSay(0, \"npc=\" + (string)n);", "made");

        Assert.Contains("Junk", Errors(h));
        Assert.NotNull(h.Scene.GetScenePresence(KeyOf(h, "npc=")));
    }

    // ------------------------------------------------------------------ the owner is away

    [Fact]
    public void OsNpcCreateFromTheNotecardWhileTheOwnerIsAwayWearsWhatTheNotecardSays()
    {
        using var h = NpcScene();
        var owner = Arrive(h);
        Run(h, "botSetOutfit(\"Priest\");", "saved");
        h.Scene.CloseAgent(owner.Id, false);
        Assert.True(h.PumpUntil(() => h.Scene.GetScenePresence(owner.Id) is null or { IsChildAgent: true } or { IsDeleted: true }), "the owner did not leave");

        Run(h, "key n = osNpcCreate(\"Pri\", \"Est\", llGetPos() + <2,0,0>, \"Priest\"); llSay(0, \"npc=\" + (string)n);", "made");

        var npcId = KeyOf(h, "npc=");
        Assert.NotEqual(UUID.Zero, npcId);
        AssertWears(h.Scene.GetScenePresence(npcId), owner.ShapeAsset, Saved);
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void BotCreateBotFromTheNotecardWhileTheOwnerIsAwayWearsWhatTheNotecardSaysAndStaysABot()
    {
        using var h = NpcScene();
        var owner = Arrive(h);
        Run(h, "botSetOutfit(\"Priest\");", "saved");
        h.Scene.CloseAgent(owner.Id, false);
        Assert.True(h.PumpUntil(() => h.Scene.GetScenePresence(owner.Id) is null or { IsChildAgent: true } or { IsDeleted: true }), "the owner did not leave");

        Run(h, "key b = botCreateBot(\"Pri\", \"Est\", \"Priest\", llGetPos() + <2,0,0>, 0); llSay(0, \"bot=\" + (string)b + \"|isbot=\" + (string)botIsBot(b));", "made");

        var botId = KeyOf(h, "bot=");
        Assert.NotEqual(UUID.Zero, botId);
        AssertWears(h.Scene.GetScenePresence(botId), owner.ShapeAsset, Saved);
        Assert.Contains(h.Said, s => s.EndsWith("|isbot=1", StringComparison.Ordinal));
    }
}
