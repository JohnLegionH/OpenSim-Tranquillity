/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.ScriptEngine.Shared;
using Phlox.ScriptEngine;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>The engine builds its host services for the region it is added to, and makes a script's functions out of them.</summary>
[Collection("native-library")]
public class EngineHostWiringTests
{
    [RequiresNativeFact]
    public void TheEngineHasHostServicesForItsRegionAndTheyAnswerFromTheEnginesSettings()
    {
        using var rig = new EngineRig(new Dictionary<string, string> { ["ChatThrottle"] = "false", ["MinTimerInterval"] = "0.4" });

        EngineHostServices host = rig.Engine.HostServices;

        Assert.NotNull(host);
        Assert.Same(rig.Scene, host.World);
        Assert.Same(rig.Engine, host.ScriptModule);
        Assert.False(host.ChatThrottle);
        Assert.Equal(0.4f, host.MinTimerInterval);
    }

    [RequiresNativeFact]
    public void TheHostsCallsIntoTheEngineAnswerForScriptsTheEngineDoesNotHold()
    {
        using var rig = new EngineRig();
        EngineHostServices host = rig.Engine.HostServices;
        UUID unknown = UUID.Random();

        Assert.False(host.HasScript(unknown, out bool running));
        Assert.False(running);
        Assert.False(host.PostScriptEvent(unknown, new EventParams("timer", new object[0], null)));
        host.ResetScript(unknown); // no script: nothing to do
    }

    [RequiresNativeFact]
    public void AScriptsApiIsTheRealOneForServedFunctionsAndTheFallbackForTheOthers()
    {
        using var rig = new EngineRig();
        (ILSLSystemAPI fallback, FakeLslApi fake) = FakeLslApi.Create();
        fake.Returns("llStringLength", 99);
        fake.Returns("llTransferOwnership", 5);
        var script = new SchedulerScript(1, 0, new byte[0], ScriptLanguage.Slua);

        ILSLSystemAPI api = rig.Engine.CreateScriptApi(script, UUID.Random(), rig.LocalId, fallback);

        Assert.Equal(3, api.llStringLength("abc")); // the real API's answer, not the fake's 99
        Assert.Equal(5, api.llTransferOwnership("00000000-0000-0000-0000-000000000000")); // the fallback's
        Assert.Equal(new[] { "llTransferOwnership" }, fake.Calls.Select(c => c.Name).ToArray());
    }
}
