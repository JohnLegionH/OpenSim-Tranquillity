/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace Luau.ScriptEngine.Tests;

/// <summary>A dialog module that records the alerts sent to users.</summary>
internal sealed class RecordingDialogModule : IDialogModule
{
    public readonly List<(UUID Agent, string Message, bool Modal)> Alerts = new();

    public void SendAlertToUser(UUID agentID, string message, bool modal) { lock (Alerts) Alerts.Add((agentID, message, modal)); }
    public void SendAlertToUser(UUID agentID, string message) => SendAlertToUser(agentID, message, false);
    public void SendAlertToUser(IClientAPI client, string message) { }
    public void SendAlertToUser(IClientAPI client, string message, bool modal) { }
    public void SendAlertToUser(string firstName, string lastName, string message, bool modal) { }
    public void SendGeneralAlert(string message) { }
    public void SendDialogToUser(UUID avatarID, string objectName, UUID objectID, UUID ownerID, string message, UUID textureID, int ch, string[] buttonlabels) { }
    public void SendDialogToUser(UUID avatarID, string objectName, UUID objectID, UUID ownerID, string ownerFirstName, string ownerLastName, string message, UUID textureID, int ch, string[] buttonlabels) { }
    public void SendUrlToUser(UUID avatarID, string objectName, UUID objectID, UUID ownerID, bool groupOwned, string message, string url) { }
    public void SendNotificationToUsersInRegion(UUID fromAvatarID, string fromAvatarName, string message) { }
    public void SendTextBoxToUser(UUID avatarid, string message, int chatChannel, string name, UUID objectid, UUID ownerid) { }
    public void SendTextBoxToUser(UUID avatarid, string message, int chatChannel, string name, UUID objectid, string ownerFirstName, string ownerLastName, UUID ownerID) { }
}

/// <summary>
/// The engine's two messages to a script's owner, through the region's own paths: an error is said on DEBUG_CHANNEL
/// from the script's prim (the region's chat module delivers it to the prim's owner only), and a compile failure
/// nobody collected is shown to the owner as an alert. The region is a test scene with the chat and dialog paths
/// observed.
/// </summary>
public class ScriptMessagesTests
{
    private const int DebugChannel = 0x7fffffff;
    private static readonly UUID Owner = UUID.Random();

    private sealed class Rig
    {
        public readonly Scene Scene = new SceneHelpers().SetupScene();
        public readonly SceneObjectGroup Object;
        public readonly TaskInventoryItem Item;
        public readonly List<OSChatMessage> Chat = new();
        public readonly SceneScriptMessages Messages;

        public Rig()
        {
            Object = SceneHelpers.AddSceneObject(Scene, "Example Object", Owner);
            Item = new TaskInventoryItem { ItemID = UUID.Random(), AssetID = UUID.Random(), Name = "Example Script", OwnerID = Owner };
            Object.RootPart.Inventory.AddInventoryItem(Item, false);
            Scene.EventManager.OnChatFromWorld += (sender, chat) => { lock (Chat) Chat.Add(chat); };
            Messages = new SceneScriptMessages(Scene);
        }
    }

    [Fact]
    public void AnErrorIsSaidOnDebugChannelFromTheScriptsPrim()
    {
        var rig = new Rig();

        rig.Messages.ShoutError(rig.Object.RootPart.LocalId, rig.Item.ItemID, "Script x encountered a problem and was stopped: boom");

        OSChatMessage chat = Assert.Single(rig.Chat);
        Assert.Equal(DebugChannel, chat.Channel);
        Assert.Equal(rig.Object.RootPart.UUID, chat.SenderUUID);
        Assert.Equal("Example Object", chat.From);
        Assert.Contains("encountered a problem and was stopped: boom", chat.Message);
    }

    [Fact]
    public void AnErrorForAPrimThatIsGoneIsDroppedWithoutAFuss()
    {
        var rig = new Rig();

        rig.Messages.ShoutError(uint.MaxValue, UUID.Random(), "nobody to tell");

        Assert.Empty(rig.Chat);
    }

    [Fact]
    public void ACompileFailureIsShownToTheOwnerOnceWithTheObjectTheScriptAndTheErrors()
    {
        var rig = new Rig();
        var dialogs = new RecordingDialogModule();
        rig.Scene.RegisterModuleInterface<IDialogModule>(dialogs);

        rig.Messages.CompileFailed(rig.Object.RootPart.LocalId, rig.Item.ItemID, new[] { "(2,0) Error: bad", "(3,0) Error: worse" });

        var alert = Assert.Single(dialogs.Alerts);
        Assert.Equal(Owner, alert.Agent);
        Assert.False(alert.Modal);
        Assert.Contains("Example Object", alert.Message);
        Assert.Contains("Example Script", alert.Message);
        Assert.Contains("script failed to compile", alert.Message);
        Assert.Contains("(2,0) Error: bad", alert.Message);
        Assert.Contains("(3,0) Error: worse", alert.Message);
    }

    [Fact]
    public void ALongListOfErrorsIsOneAlertThatSaysHowManyWereLeftOut()
    {
        var rig = new Rig();
        var dialogs = new RecordingDialogModule();
        rig.Scene.RegisterModuleInterface<IDialogModule>(dialogs);
        var errors = Enumerable.Range(1, 25).Select(i => $"({i},0) Error: e{i}").ToArray();

        rig.Messages.CompileFailed(rig.Object.RootPart.LocalId, rig.Item.ItemID, errors);

        var alert = Assert.Single(dialogs.Alerts);
        Assert.Contains("(10,0) Error: e10", alert.Message);
        Assert.DoesNotContain("(11,0) Error: e11", alert.Message);
        Assert.Contains("and 15 more", alert.Message);
    }

    [Fact]
    public void ACompileFailureWithNoDialogModuleOrNoPrimIsDroppedWithoutAFuss()
    {
        var rig = new Rig();

        rig.Messages.CompileFailed(rig.Object.RootPart.LocalId, rig.Item.ItemID, new[] { "(1,0) Error: x" }); // no dialog module in the scene
        rig.Scene.RegisterModuleInterface<IDialogModule>(new RecordingDialogModule());
        rig.Messages.CompileFailed(uint.MaxValue, UUID.Random(), new[] { "(1,0) Error: x" }); // no such prim
        rig.Messages.CompileFailed(rig.Object.RootPart.LocalId, rig.Item.ItemID, Array.Empty<string>()); // nothing to say
    }
}

/// <summary>The settings the lifecycle and kill rules read.</summary>
public class KillSettingTests
{
    private static LuauSettings Read(string value)
    {
        var source = new IniConfigSource();
        IConfig config = source.AddConfig(EngineIdentity.ConfigSection);
        config.Set("Enabled", "true");
        if (value != null) config.Set("KillAfterPunishments", value);
        return LuauSettings.Read(source, new RecordingLog());
    }

    [Fact]
    public void TheKillLimitDefaultsToTwenty() => Assert.Equal(20, Read(null).KillAfterPunishments);

    [Fact]
    public void TheKillLimitCanBeSet() => Assert.Equal(5, Read("5").KillAfterPunishments);

    [Fact]
    public void AKillLimitBelowOneIsRaisedToOneSoTheRuleCannotBeSwitchedOffByAccident() => Assert.Equal(1, Read("0").KillAfterPunishments);
}
