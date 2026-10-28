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
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Tests.Common;
using Xunit;
using static SLua.ScriptEngine.Tests.EngineRig;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Dataserver answers. The requests (notecard reads, agent and name requests, inventory and simulator data, key-value
/// calls) run on the real LSLSystemAPI; the engine delivers their answer as the dataserver event, to every script of the
/// asking script's prim (SL wiki Dataserver: a request raises the event in every script of the prim it was made from),
/// whatever engine runs it, in the order Phlox delivers it (by script name, then item id).
/// </summary>
[Collection("native-library")]
public class DataserverTests
{
    private static readonly string[] MovedOntoTheRealApi =
    {
        "llGetNotecardLine", "llGetNumberOfNotecardLines", "llRequestInventoryData", "llRequestSimulatorData", "llRequestUserKey",
        "llRequestAgentData", "llRequestDisplayName", "llRequestUsername", "llCreateKeyValue", "llReadKeyValue", "llUpdateKeyValue",
        "llDeleteKeyValue", "llDataSizeKeyValue", "llKeyCountKeyValue", "llKeysKeyValue",
    };

    private static void AddNotecard(EngineRig rig, string name, string text, SceneObjectPart part = null)
    {
        part ??= rig.Object.RootPart;
        AssetBase card = AssetHelpers.CreateNotecardAsset(UUID.Random(), text);
        rig.Scene.AssetService.Store(card);
        part.Inventory.AddInventoryItem(new TaskInventoryItem
        {
            ItemID = UUID.Random(),
            AssetID = card.FullID,
            Name = name,
            OwnerID = Owner,
            Type = (int)OpenMetaverse.AssetType.Notecard,
            InvType = (int)OpenMetaverse.InventoryType.Notecard,
        }, false);
    }

    private static string Diag(EngineRig rig, UUID item) =>
        "marks=[" + string.Join(",", rig.MarksOf(item)) + "] shouts=[" + string.Join(" | ", rig.Messages.Shouts.Select(s => s.Text)) + "] log=[" + string.Join(" | ", rig.Log.Lines) + "]";

    private static void Heard(EngineRig rig, UUID item, string mark) =>
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains(mark)), "no '" + mark + "': " + Diag(rig, item));

    private static void NeverHeard(EngineRig rig, UUID item, string prefix)
    {
        Thread.Sleep(600);   // a real wait: it proves nothing arrives
        Assert.DoesNotContain(rig.MarksOf(item), m => m.StartsWith(prefix));
    }

    private const string Listening = "LLEvents:on('dataserver', function(q, data) ossl.Mark('d=' .. data) end)\nossl.Mark('ready')\n";

    // ---- the functions move onto the real API ----

    [Fact]
    public void TheDataserverFunctionsRunOnTheRealApi()
    {
        Assert.All(MovedOntoTheRealApi, n => Assert.Contains(n, LlServed.Served));
        Assert.DoesNotContain(LlServed.NotServed, r => r.Contains("PostDataserverToPrim") || r.Contains("NotecardCache"));
    }

    [Fact]
    public void TheEngineIsOneOfTheRegionsScriptEnginesSoPhloxOffersItsEventsToIt()
    {
        using var rig = new EngineRig();
        Assert.IsAssignableFrom<IScriptEngine>((object)rig.Engine);
    }

    // ---- notecards ----

    [RequiresNativeFact]
    public void AnSLuaScriptReadsANotecardsLineCountAndALine()
    {
        using var rig = new EngineRig(productionFunctions: true);
        AddNotecard(rig, "card", "alpha\nbeta\ngamma");
        UUID item = rig.Rez(Slua(
            "local count_q, line_q\n" +
            "LLEvents:on('dataserver', function(q, data)\n" +
            "  if tostring(q) == tostring(count_q) then ossl.Mark('count=' .. data)\n" +
            "  elseif tostring(q) == tostring(line_q) then ossl.Mark('line=' .. data) end\n" +
            "end)\n" +
            "count_q = ll.GetNumberOfNotecardLines('card')\n" +
            "line_q = ll.GetNotecardLine('card', 2)\n"));

        Heard(rig, item, "count=3");
        Heard(rig, item, "line=beta");   // SLua counts lines from 1
    }

    [RequiresNativeFact]
    public void AnLslScriptRunByTheEngineReadsANotecardsLineCountAndALine()
    {
        using var rig = new EngineRig(productionFunctions: true);
        AddNotecard(rig, "card", "alpha\nbeta\ngamma");
        UUID item = rig.Rez(Lsl(
            "default { state_entry() { llGetNumberOfNotecardLines(\"card\"); llGetNotecardLine(\"card\", 1); }\n" +
            "dataserver(key q, string d) { llOwnerSay(\"d=\" + d); } }"));

        Heard(rig, item, "d=3");
        Heard(rig, item, "d=beta");
    }

    // ---- who hears an answer ----

    [RequiresNativeFact]
    public void AnAnswerReachesEveryScriptInThePrimAndNoScriptInAnotherPrim()
    {
        using var rig = new EngineRig(productionFunctions: true);
        AddNotecard(rig, "card", "alpha\nbeta\ngamma");
        SceneObjectGroup other = rig.AddObject("Other", rig.Object.AbsolutePosition + new Vector3(3, 0, 0), Owner);
        UUID bystander = rig.Rez(Slua(Listening), part: other.RootPart);
        UUID sibling = rig.Rez(Slua(Listening));
        Heard(rig, sibling, "ready");
        Heard(rig, bystander, "ready");
        UUID asker = rig.Rez(Slua(Listening + "ll.GetNumberOfNotecardLines('card')\n"));

        Heard(rig, asker, "d=3");
        Heard(rig, sibling, "d=3");
        NeverHeard(rig, bystander, "d=");
    }

    [RequiresNativeFact]
    public void TheAskerThatIsNoLongerOwedTheAnswerIsSkippedAndTheOtherScriptsGetIt()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID first = rig.Rez(Slua(Listening));
        UUID second = rig.Rez(Slua(Listening));
        Heard(rig, first, "ready");
        Heard(rig, second, "ready");

        int posted = rig.Engine.HostServices.PostDataserverToPrim(rig.Object.RootPart, first, first, "query", "answer");

        Assert.Equal(1, posted);
        Heard(rig, second, "d=answer");
        NeverHeard(rig, first, "d=");
    }

    [RequiresNativeFact]
    public void AnAnswerIsOfferedOnceToEachOtherEngineWithThePrimsLocalId()
    {
        using var rig = new EngineRig(productionFunctions: true);
        var other = new FakeOtherEngine();
        rig.Scene.StackModuleInterface<IScriptModule>(other);
        AddNotecard(rig, "card", "alpha\nbeta\ngamma");
        UUID asker = rig.Rez(Slua(Listening + "ll.GetNumberOfNotecardLines('card')\n"));

        Heard(rig, asker, "d=3");
        Assert.True(SchedulerHarness.WaitFor(() => other.ObjectPosts.Length > 0), "the other engine was offered nothing");
        (uint local, EventParams parms) = Assert.Single(other.ObjectPosts);
        Assert.Equal(rig.LocalId, local);
        Assert.Equal("dataserver", parms.EventName);
        Assert.Equal("3", parms.Params[1].ToString());
    }

    [RequiresNativeFact]
    public void AnAnswerAnotherEngineAsksForReachesAnSLuaHandlerAndAnLslHandler()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID slua = rig.Rez(Slua("LLEvents:on('dataserver', function(q, data) ossl.Mark('d=' .. tostring(q) .. '|' .. data) end)\nossl.Mark('ready')\n"));
        UUID lsl = rig.Rez(Lsl("default { state_entry() { llOwnerSay(\"ready\"); } dataserver(key q, string d) { llOwnerSay(\"d=\" + (string)q + \"|\" + d); } }"));
        Heard(rig, slua, "ready");
        Heard(rig, lsl, "ready");

        // the values another engine posts (Phlox's own post to the engines it does not run)
        IScriptEngine engine = (object)rig.Engine as IScriptEngine;
        Assert.NotNull(engine);
        Assert.True(engine.PostObjectEvent(rig.LocalId, new EventParams("dataserver",
            new object[] { new OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString("5a1f0000-0000-4000-8000-000000000001"), new OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString("from elsewhere") },
            new DetectParams[0])));

        Heard(rig, slua, "d=5a1f0000-0000-4000-8000-000000000001|from elsewhere");
        Heard(rig, lsl, "d=5a1f0000-0000-4000-8000-000000000001|from elsewhere");
    }

    // ---- requests that name an avatar, and key-value calls ----

    [RequiresNativeFact]
    public void ARequestForAnAvatarsUsernameAnswersThroughDataserver()
    {
        using var rig = new EngineRig(productionFunctions: true);
        ScenePresence avatar = SceneHelpers.AddScenePresence(rig.Scene, UUID.Random());
        UUID item = rig.Rez(Slua(Listening + "ll.RequestUsername('" + avatar.UUID + "')\n"));

        Heard(rig, item, "d=" + avatar.Name);
    }

    [RequiresNativeFact]
    public void ARequestForAnAvatarsUsernameFromAnLslScriptAnswersToo()
    {
        using var rig = new EngineRig(productionFunctions: true);
        ScenePresence avatar = SceneHelpers.AddScenePresence(rig.Scene, UUID.Random());
        UUID item = rig.Rez(Lsl("default { state_entry() { llRequestUsername(\"" + avatar.UUID + "\"); } dataserver(key q, string d) { llOwnerSay(\"d=\" + d); } }"));

        Heard(rig, item, "d=" + avatar.Name);
    }

    [RequiresNativeFact]
    public void AKeyValueCallWithNoExperienceStoreAnswersThroughDataserverInSlsForm()
    {
        using var rig = new EngineRig(productionFunctions: true);
        UUID item = rig.Rez(Slua(
            "local q\nLLEvents:on('dataserver', function(id, data) if tostring(id) == tostring(q) then ossl.Mark('kv=' .. data) end end)\n" +
            "q = ll.CreateKeyValue('k', 'v')\nossl.Mark('asked')\n"));

        Heard(rig, item, "asked");
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Any(m => m.StartsWith("kv=0,"))),
            "no failed key-value answer (0,<error>): " + Diag(rig, item));
    }
}
