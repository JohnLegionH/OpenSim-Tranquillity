/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Xunit;
using static SLua.ScriptEngine.Tests.EngineRig;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Sensors and HTTP requests run on the real LSLSystemAPI with the asynchronous command plugins Phlox uses (the sensor repeat
/// and the HTTP request plugin), built on the engine as one of the region's script engines. A sensor reaches the script as
/// sensor or no_sensor with the detected objects, which an SLua handler reads through the DetectedEvent wrappers the runtime
/// builds from the event's count (Luau VM/src/lllevents.cpp, luaSL_pushdetectedevent) and an LSL handler through llDetected*.
/// An HTTP response reaches an SLua and an LSL handler alike. A script's sensors and requests end with the script.
/// </summary>
[Collection("native-library")]
public class SensorAndHttpTests
{
    private const int Passive = 4;

    private static string Diag(EngineRig rig, UUID item) =>
        "marks=[" + string.Join(",", rig.MarksOf(item)) + "] shouts=[" + string.Join(" | ", rig.Messages.Shouts.Select(s => s.Text)) + "] log=[" + string.Join(" | ", rig.Log.Lines) + "]";

    private static void Heard(EngineRig rig, UUID item, string mark) =>
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains(mark)), "no '" + mark + "': " + Diag(rig, item));

    private static EngineRig RigWithTarget(out SceneObjectGroup target)
    {
        var rig = new EngineRig(productionFunctions: true);
        target = rig.AddObject("Sensor Target", rig.Object.AbsolutePosition + new Vector3(4, 0, 0), Owner);
        return rig;
    }

    private const string SluaHandlers =
        "LLEvents:on('sensor', function(detected) ossl.Mark('sensor=' .. #detected .. '|' .. detected[1]:getName()) end)\n" +
        "LLEvents:on('no_sensor', function() ossl.Mark('none') end)\n";

    [Fact]
    public void SensorsAndHttpRunOnTheRealApi()
    {
        Assert.All(new[] { "llSensor", "llSensorRepeat", "llSensorRemove", "llHTTPRequest" }, n => Assert.Contains(n, LlServed.Served));
        Assert.DoesNotContain(LlServed.NotServed, r => r.Contains("AsyncCommands"));
    }

    // ---- sensors ----

    [RequiresNativeFact]
    public void AnSLuaSensorFindsANamedObjectInRangeAndHandsTheHandlerADetectedEvent()
    {
        using EngineRig rig = RigWithTarget(out _);
        UUID item = rig.Rez(Slua(SluaHandlers + "ll.Sensor('Sensor Target', NULL_KEY, " + Passive + ", 20.0, PI)\nossl.Mark('asked')\n"));

        Heard(rig, item, "sensor=1|Sensor Target");
        Assert.DoesNotContain("none", rig.MarksOf(item));
    }

    [RequiresNativeFact]
    public void ASensorThatFindsNothingRaisesNoSensor()
    {
        using EngineRig rig = RigWithTarget(out _);
        UUID item = rig.Rez(Slua(SluaHandlers + "ll.Sensor('Nobody Here', NULL_KEY, " + Passive + ", 20.0, PI)\n"));

        Heard(rig, item, "none");
        Assert.DoesNotContain(rig.MarksOf(item), m => m.StartsWith("sensor="));
    }

    [RequiresNativeFact]
    public void ATargetOutOfRangeIsNotFound()
    {
        using EngineRig rig = RigWithTarget(out SceneObjectGroup target);
        target.AbsolutePosition = rig.Object.AbsolutePosition + new Vector3(60, 0, 0);
        UUID item = rig.Rez(Slua(SluaHandlers + "ll.Sensor('Sensor Target', NULL_KEY, " + Passive + ", 10.0, PI)\n"));

        Heard(rig, item, "none");
    }

    [RequiresNativeFact]
    public void AnLslSensorReadsTheDetectedNameThroughLlDetectedName()
    {
        using EngineRig rig = RigWithTarget(out _);
        UUID item = rig.Rez(Lsl("default { state_entry() { llSensor(\"Sensor Target\", NULL_KEY, PASSIVE, 20.0, PI); }\n" +
            "sensor(integer n) { llOwnerSay(\"sensor=\" + (string)n + \"|\" + llDetectedName(0)); }\nno_sensor() { llOwnerSay(\"none\"); } }"));

        Heard(rig, item, "sensor=1|Sensor Target");
    }

    [RequiresNativeFact]
    public void ARepeatingSensorRepeatsAndSensorRemoveStopsIt()
    {
        using EngineRig rig = RigWithTarget(out _);
        UUID item = rig.Rez(Slua(
            "local n = 0\nLLEvents:on('sensor', function(d) n = n + 1 ossl.Mark('s' .. n) if n == 3 then ll.SensorRemove() end end)\n" +
            "ll.SensorRepeat('Sensor Target', NULL_KEY, " + Passive + ", 20.0, PI, 0.5)\n"));

        Heard(rig, item, "s3");
        Thread.Sleep(1500);   // a real wait: it proves no fourth sensor comes after the removal
        Assert.DoesNotContain("s4", rig.MarksOf(item));
    }

    // ---- a script's sensors end with the script ----

    private static object[] Sensors(EngineRig rig, UUID item) => rig.Engine.HostServices.AsyncCommands?.SensorRepeatPlugin.GetSerializationData(item) ?? Array.Empty<object>();

    [RequiresNativeFact]
    public void AResetOrAnUnloadEndsAScriptsRepeatingSensor()
    {
        using EngineRig rig = RigWithTarget(out _);
        // the sensor is set in answer to an event, so the restart a reset causes does not set it again
        string repeat = "LLEvents:on('changed', function() ll.SensorRepeat('Nobody Here', NULL_KEY, " + Passive + ", 20.0, PI, 30.0) ossl.Mark('armed') end)\nossl.Mark('ready')\n";
        UUID reset = rig.Rez(Slua(repeat));
        UUID unload = rig.Rez(Slua(repeat));
        foreach (UUID item in new[] { reset, unload })
        {
            Heard(rig, item, "ready");
            Assert.True(rig.Engine.PostScriptEvent(item, "changed", new object[] { 1 }));
            Heard(rig, item, "armed");
        }
        Assert.NotEmpty(Sensors(rig, reset));
        Assert.NotEmpty(Sensors(rig, unload));

        rig.Scene.EventManager.TriggerScriptReset(rig.LocalId, reset);
        rig.Scene.EventManager.TriggerRemoveScript(rig.LocalId, unload);

        Assert.True(SchedulerHarness.WaitFor(() => Sensors(rig, reset).Length == 0), "the reset left the sensor");
        Assert.True(SchedulerHarness.WaitFor(() => Sensors(rig, unload).Length == 0), "the unload left the sensor");
    }

    // ---- HTTP ----

    [RequiresNativeFact]
    public void AnHttpResponseReachesAnSLuaHandlerAndAnLslHandler()
    {
        using var rig = new EngineRig(productionFunctions: true);
        var http = new FakeHttpModule();
        rig.Scene.RegisterModuleInterface<IHttpRequestModule>(http);
        UUID slua = rig.Rez(Slua(
            "local q\nLLEvents:on('http_response', function(id, status, meta, body) if tostring(id) == tostring(q) then ossl.Mark('http=' .. status .. '|' .. body) end end)\n" +
            "q = ll.HTTPRequest('https://example.org/slua', {}, '')\nossl.Mark('asked')\n"));
        UUID lsl = rig.Rez(Lsl("key q;\ndefault { state_entry() { q = llHTTPRequest(\"https://example.org/lsl\", [], \"\"); }\n" +
            "http_response(key id, integer status, list meta, string body) { if (id == q) llOwnerSay(\"http=\" + (string)status + \"|\" + body); } }"));

        Heard(rig, slua, "http=200|" + FakeHttpModule.Body);
        Heard(rig, lsl, "http=200|" + FakeHttpModule.Body);
        Assert.Contains("https://example.org/slua", http.Urls);
    }

    [RequiresNativeFact]
    public void ARequestTheOutboundFilterRefusesReturnsNoKeyAndIsNotSent()
    {
        using var rig = new EngineRig(productionFunctions: true);
        var http = new FakeHttpModule();
        rig.Scene.RegisterModuleInterface<IHttpRequestModule>(http);
        UUID item = rig.Rez(Slua("local q = ll.HTTPRequest('https://" + FakeHttpModule.RefusedHost + "/x', {}, '')\nossl.Mark('key=[' .. tostring(q) .. ']')\n"));

        Heard(rig, item, "key=[00000000-0000-0000-0000-000000000000]");   // SL: NULL_KEY when a request is refused
        Assert.Empty(http.Urls);
    }
}
