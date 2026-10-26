/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// Whether an event that wakes an idle script puts it at the front of its worker's ring is a setting, on by default
/// (the behaviour of the release before the setting existed).
/// </summary>
[Collection("native-library")]
public class FrontOfRingSettingTests
{
    private const string Busy = "while true do ossl.Mark(\"busy\", 0) coroutine.yield() end";
    private const string Listener = "LLEvents:on(\"dataserver\", function(q, d) ossl.Mark(\"event\", d) end)";

    private static LuauSettings Read(string value)
    {
        var source = new IniConfigSource();
        IConfig c = source.AddConfig(EngineIdentity.ConfigSection);
        if (value != null)
            c.Set("EventFrontOfRing", value);
        return LuauSettings.Read(source, new RecordingLog());
    }

    private static SchedulerOptions EngineOptions(string value)
    {
        var source = new IniConfigSource();
        IConfig c = source.AddConfig(EngineIdentity.ConfigSection);
        if (value != null)
            c.Set("EventFrontOfRing", value);
        var engine = new LuauEngine(new RecordingLog(), (settings, log) => true);
        engine.Initialise(source);
        return engine.BuildSchedulerOptions();
    }

    [Fact]
    public void TheSettingIsOnByDefaultAndCanBeTurnedOff()
    {
        Assert.True(Read(null).EventFrontOfRing);
        Assert.True(Read("true").EventFrontOfRing);
        Assert.False(Read("false").EventFrontOfRing);
    }

    [Fact]
    public void TheEngineHandsTheSettingToTheScheduler()
    {
        Assert.True(EngineOptions(null).FrontOfRing);
        Assert.False(EngineOptions("false").FrontOfRing);
    }

    [RequiresNativeFact]
    public void WithTheSettingOffAnEventThatWakesAnIdleScriptWaitsItsTurnInTheRing()
    {
        using var h = new SchedulerHarness(configure: o => o.FrontOfRing = false);
        for (int i = 0; i < 8; i++) h.AddAndStart(Busy);
        SchedulerScript quiet = h.AddAndStart(Listener);
        h.Passes(3);
        int seen = h.Marks.Length;

        h.Scheduler.Post(quiet, "dataserver", Value.Key("k"), Value.Str("1"));
        h.Passes(1);

        string[] pass = h.Marks.Skip(seen).Select(m => m.Split(':')[1]).ToArray();
        Assert.Equal("event", pass[^1]); // after the eight busy scripts that were already in the ring
        Assert.Equal(8, pass.Count(k => k == "busy"));
        Assert.Equal(0, quiet.FrontGrants);
    }
}
