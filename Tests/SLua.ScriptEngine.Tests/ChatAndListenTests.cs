/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Xunit;
using static SLua.ScriptEngine.Tests.EngineRig;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Chat and listens run on the real LSLSystemAPI with the engine's own listen manager and world communication. The listen
/// manager is Phlox's (so the filters, the 65 listens per script and the ranges are Phlox's, which follow Second Life's
/// documentation); chat leaves through the region's chat (Scene.SimChat) and is offered to the other engines' listens through
/// the region's IWorldComm. A listen event reaches an SLua handler and an //SLua:LSL listen() alike.
/// </summary>
[Collection("native-library")]
public class ChatAndListenTests
{
    private const string NullKey = "00000000-0000-0000-0000-000000000000";
    private static readonly UUID Alice = new("a11ce000-0000-4000-8000-000000000001");
    private static readonly UUID Bob = new("b0b00000-0000-4000-8000-000000000002");

    private static readonly string[] MovedOntoTheRealApi =
    {
        "llListen", "llListenControl", "llListenRemove", "llSay", "llShout", "llWhisper", "llRegionSay", "llRegionSayTo", "llGiveInventory",
    };

    private static string Listener(string tag, int channel, string name = "", string key = "", string message = "") =>
        Slua("LLEvents:on('listen', function(ch, name, id, msg) ossl.Mark('" + tag + "=' .. ch .. '|' .. name .. '|' .. tostring(id) .. '|' .. msg) end)\n" +
             "ll.Listen(" + channel + ", '" + name + "', '" + key + "', '" + message + "')\nossl.Mark('" + tag + "-ready')");

    private static bool Ready(EngineRig rig, UUID item, string tag) => SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains(tag + "-ready"));

    private static void Speak(EngineRig rig, string text, int channel, string name = "Test Speaker", UUID? from = null, ChatTypeEnum type = ChatTypeEnum.Say, Vector3? at = null) =>
        rig.Scene.SimChat(text, type, channel, at ?? rig.Object.AbsolutePosition + new Vector3(1, 0, 0), name, from ?? Alice, false);

    private static bool Heard(EngineRig rig, UUID item, string expected) => SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains(expected));

    /// <summary>A real wait, because it proves nothing arrives: a heard message needs far less than this.</summary>
    private static bool NothingHeard(EngineRig rig, UUID item, string tag)
    {
        Thread.Sleep(600);
        return !rig.MarksOf(item).Any(m => m.StartsWith(tag + "="));
    }

    // ---- the functions move onto the real API ----

    [Fact]
    public void TheChatListenAndGiveFunctionsRunOnTheRealApi()
    {
        Assert.All(MovedOntoTheRealApi, n => Assert.Contains(n, LlServed.Served));
        Assert.DoesNotContain(LlServed.NotServed, r => r.Contains("ListenManager") || r.Contains("SendToWorldComm") && !r.StartsWith("llHTTPRequest"));
        // llHTTPRequest also needs the asynchronous command plugins and the event queue figure: it stays where it was
        Assert.Contains(LlServed.NotServed, r => r.StartsWith("llHTTPRequest:") && r.Contains("AsyncCommands"));
    }

    // ---- hearing ----

    [RequiresNativeFact]
    public void AnSLuaHandlerHearsWhatIsSaidOnItsChannelAndNothingOnAnother()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Listener("s", 5));
        Assert.True(Ready(rig, item, "s"));

        Speak(rig, "other channel", 6);
        Speak(rig, "hello", 5);

        Assert.True(Heard(rig, item, "s=5|Test Speaker|" + Alice + "|hello"));
        Assert.Single(rig.MarksOf(item), m => m.StartsWith("s="));
    }

    [RequiresNativeFact]
    public void AnLslListenEventHearsToo()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Lsl("default { state_entry() { llListen(5, \"\", \"\", \"\"); llOwnerSay(\"l-ready\"); } listen(integer c, string n, key id, string m) { llOwnerSay(\"l=\" + (string)c + \"|\" + n + \"|\" + (string)id + \"|\" + m); } }"));
        Assert.True(Ready(rig, item, "l"));

        Speak(rig, "hello", 5);

        Assert.True(Heard(rig, item, "l=5|Test Speaker|" + Alice + "|hello"));
    }

    [RequiresNativeFact]
    public void AnSLuaScriptAndAnLslScriptTalkToEachOtherInBothDirections()
    {
        using var rig = new EngineRig(productionFunctions: true);
        SceneObjectGroup talker = rig.AddObject("Talker", rig.Object.AbsolutePosition + new Vector3(3, 0, 0), Owner);
        UUID lsl = rig.Rez(Lsl("default { state_entry() { llListen(7, \"\", \"\", \"\"); llOwnerSay(\"l-ready\"); } listen(integer c, string n, key id, string m) { llOwnerSay(\"l=\" + m); llSay(8, \"to-slua\"); } }"));
        Assert.True(Ready(rig, lsl, "l"));
        UUID slua = rig.Rez(Slua(
            "LLEvents:on('listen', function(ch, name, id, msg) ossl.Mark('s=' .. msg) end)\nll.Listen(8, '', '', '')\nossl.Mark('s-ready')\nll.Say(7, 'to-lsl')"),
            part: talker.RootPart);
        Assert.True(Ready(rig, slua, "s"));

        Assert.True(Heard(rig, lsl, "l=to-lsl"));
        Assert.True(Heard(rig, slua, "s=to-slua"));
    }

    // ---- ranges ----

    [RequiresNativeFact]
    public void ARegionSayReachesAcrossTheRegionAndASayDoesNot()
    {
        using var rig = new EngineRig(productionFunctions: true);
        SceneObjectGroup far = rig.AddObject("Far", new Vector3(10, 10, 25), Owner);
        rig.Object.AbsolutePosition = new Vector3(240, 240, 25);
        UUID listener = rig.Rez(Listener("s", 5), part: far.RootPart);
        Assert.True(Ready(rig, listener, "s"));

        Speak(rig, "near-miss", 5, at: new Vector3(240, 240, 25));
        Assert.True(NothingHeard(rig, listener, "s"));
        Speak(rig, "region", 5, at: new Vector3(240, 240, 25), type: ChatTypeEnum.Region);

        Assert.True(Heard(rig, listener, "s=5|Test Speaker|" + Alice + "|region"));
    }

    // ---- filters ----

    [RequiresNativeFact]
    public void ANameKeyAndMessageFilterPassAndBlockAsLlListenDocuments()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID byName = rig.Rez(Listener("n", 5, name: "Alice Resident"));
        UUID byKey = rig.Rez(Listener("k", 5, key: Alice.ToString()));
        UUID byMessage = rig.Rez(Listener("m", 5, message: "open"));
        Assert.True(Ready(rig, byName, "n") && Ready(rig, byKey, "k") && Ready(rig, byMessage, "m"));

        Speak(rig, "open", 5, name: "Alice Resident", from: Alice);
        Speak(rig, "open", 5, name: "Bob Resident", from: Bob);
        Speak(rig, "close", 5, name: "Alice Resident", from: Alice);

        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(byName).Count(m => m.StartsWith("n=")) == 2));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(byKey).Count(m => m.StartsWith("k=")) == 2));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(byMessage).Count(m => m.StartsWith("m=")) == 2));
        Assert.Equal(new[] { "n=5|Alice Resident|" + Alice + "|open", "n=5|Alice Resident|" + Alice + "|close" }, rig.MarksOf(byName).Where(m => m.StartsWith("n=")));
        Assert.Equal(new[] { "k=5|Alice Resident|" + Alice + "|open", "k=5|Alice Resident|" + Alice + "|close" }, rig.MarksOf(byKey).Where(m => m.StartsWith("k=")));
        Assert.Equal(new[] { "m=5|Alice Resident|" + Alice + "|open", "m=5|Bob Resident|" + Bob + "|open" }, rig.MarksOf(byMessage).Where(m => m.StartsWith("m=")));
    }

    // ---- listen control, the limit and the end of a listen ----

    [RequiresNativeFact]
    public void TheSixtySixthListenOfAScriptFailsAndTheFirstSixtyFiveDoNot()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Slua("local failed = 0 local ok = 0 for ch = 1, 66 do local h = ll.Listen(ch, '', '', '') if h < 0 then failed = failed + 1 else ok = ok + 1 end end\nossl.Mark('ok=' .. ok .. ' failed=' .. failed)"));

        Assert.True(Heard(rig, item, "ok=65 failed=1"));
        Assert.Equal(65, rig.Engine.Listens.ListenCount);
    }

    [RequiresNativeFact]
    public void ListenControlSwitchesAListenOffAndOnAndListenRemoveEndsIt()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Slua(
            "LLEvents:on('listen', function(ch, name, id, msg) ossl.Mark('s=' .. msg) end)\nlocal h = ll.Listen(5, '', '', '')\n" +
            "LLEvents:on('timer', function() end)\nll.ListenControl(h, false)\nossl.Mark('off')\n" +
            "LLEvents:on('touch_start', function() ll.ListenControl(h, true) ossl.Mark('on') end)\n"));
        Assert.True(Heard(rig, item, "off"));

        Speak(rig, "while off", 5);
        Assert.True(NothingHeard(rig, item, "s"));
        Assert.True(rig.Engine.PostScriptEvent(item, "touch_start", new object[] { 1 }));
        Assert.True(Heard(rig, item, "on"));
        Speak(rig, "while on", 5);

        Assert.True(Heard(rig, item, "s=while on"));
    }

    [RequiresNativeFact]
    public void AResetAndAnUnloadEndTheScriptsListens()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Slua("ll.Listen(5, '', '', '')\nossl.Mark('s-ready')\nLLEvents:on('touch_start', function() ll.Listen(6, '', '', '') ossl.Mark('six') end)"));
        Assert.True(Ready(rig, item, "s"));
        Assert.True(rig.Engine.PostScriptEvent(item, "touch_start", new object[] { 1 }));
        Assert.True(Heard(rig, item, "six"));
        Assert.Equal(2, rig.Engine.Listens.ListenCount);

        rig.Scene.EventManager.TriggerScriptReset(rig.LocalId, item);
        // the script starts again from the top and listens on channel 5 once more
        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.Listens.ListenCount == 1));

        rig.Scene.EventManager.TriggerRemoveScript(rig.LocalId, item);
        Assert.True(SchedulerHarness.WaitFor(() => rig.Engine.Listens.ListenCount == 0));
    }

    // ---- the region's world communication ----

    private sealed class StandInWorldComm : IWorldComm
    {
        public readonly List<string> Delivered = new();
        public int ListenerCount => 0;
        public int Listen(UUID itemID, UUID hostID, int channel, string name, UUID id, string msg) => -1;
        public int Listen(UUID itemID, UUID hostID, int channel, string name, UUID id, string msg, int regexBitfield) => -1;
        public void DeliverMessage(ChatTypeEnum type, int channel, string name, UUID id, string msg) { lock (Delivered) Delivered.Add(type + "|" + channel + "|" + name + "|" + msg); RaiseDelivered(type, channel, name, id, msg); }
        public void DeliverMessage(ChatTypeEnum type, int channel, string name, UUID id, string msg, Vector3 sourcePosition) => DeliverMessage(type, channel, name, id, msg);
        public void DeliverMessageTo(UUID target, int channel, Vector3 pos, string name, UUID id, string msg) { lock (Delivered) Delivered.Add("To:" + target + "|" + channel + "|" + name + "|" + msg); }
        public event Action<OSChatMessage> OnMessageDelivered;
        public void ListenControl(UUID itemID, int handle, int active) { }
        public void ListenRemove(UUID itemID, int handle) { }
        public void DeleteListener(UUID itemID) { }
        public object[] GetSerializationData(UUID itemID) => null;
        public void CreateFromData(UUID itemID, UUID hostID, object[] data) { }
        public void Raise(OSChatMessage chat) => OnMessageDelivered?.Invoke(chat);
        private void RaiseDelivered(ChatTypeEnum type, int channel, string name, UUID id, string msg) =>
            OnMessageDelivered?.Invoke(new OSChatMessage { Type = type, Channel = channel, From = name, SenderUUID = id, Message = msg });
    }

    [RequiresNativeFact]
    public void ARegionSayIsOfferedToTheRegionsWorldCommAndWhatOtherEnginesSendIsHeard()
    {
        using var rig = new EngineRig(productionFunctions: true);
        var comm = new StandInWorldComm();
        rig.Scene.RegisterModuleInterface<IWorldComm>(comm);
        rig.Engine.RegionLoaded(rig.Scene);
        UUID listener = rig.Rez(Listener("s", 9));
        Assert.True(Ready(rig, listener, "s"));
        SceneObjectGroup talker = rig.AddObject("Talker", rig.Object.AbsolutePosition + new Vector3(2, 0, 0), Owner);
        rig.Rez(Slua("ll.RegionSay(9, 'across')"), part: talker.RootPart);

        Assert.True(SchedulerHarness.WaitFor(() => { lock (comm.Delivered) return comm.Delivered.Contains("Region|9|Talker|across"); }));
        // the engine's own listen heard it once through the scene; the copy WorldComm raised for it is not delivered again
        Assert.True(Heard(rig, listener, "s=9|Talker|" + talker.RootPart.UUID + "|across"));
        Thread.Sleep(400);
        Assert.Single(rig.MarksOf(listener), m => m.StartsWith("s="));

        comm.Raise(new OSChatMessage { Type = ChatTypeEnum.Region, Channel = 9, From = "Other Engine", SenderUUID = Bob, Message = "from yengine" });
        Assert.True(Heard(rig, listener, "s=9|Other Engine|" + Bob + "|from yengine"));
    }
}
