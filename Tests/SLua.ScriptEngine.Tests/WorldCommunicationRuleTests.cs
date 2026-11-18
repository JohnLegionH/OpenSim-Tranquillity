/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using Xunit;
using static SLua.ScriptEngine.Tests.EngineRig;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// A region with no world communication module (IWorldComm) has no way to carry chat between script engines, so the engine
/// disables itself with one log line, as the Phlox engine does: no script is taken, and the region and the other engines
/// carry on.
/// </summary>
[Collection("native-library")]
public class WorldCommunicationRuleTests
{
    [Fact]
    public void WithoutAWorldCommunicationModuleTheEngineDisablesItselfWithOneErrorLine()
    {
        using var rig = new EngineRig();

        rig.Engine.RegionLoaded(rig.Scene);

        Assert.True(rig.Engine.IsDisabled);
        string line = Assert.Single(rig.Log.Errors);
        Assert.Contains("IWorldComm", line);
        Assert.Contains("disabled", line);
    }

    [Fact]
    public void ADisabledEngineTakesNoScript()
    {
        using var rig = new EngineRig();
        rig.Engine.RegionLoaded(rig.Scene);

        UUID item = rig.Rez(Slua("ossl.Mark('ran')"));

        Assert.False(rig.Engine.IsTracking(item));
        Assert.Null(rig.Engine.SchedulerScriptFor(item));
    }

    [RequiresNativeFact]
    public void WithAWorldCommunicationModuleTheEngineStaysEnabledAndSaysNothingAboutIt()
    {
        using var rig = new EngineRig(productionFunctions: true);
        rig.Scene.RegisterModuleInterface<IWorldComm>(new ChatAndListenTests.StandInWorldComm());

        rig.Engine.RegionLoaded(rig.Scene);
        UUID item = rig.Rez(Slua("ossl.Mark('ran')"));

        Assert.False(rig.Engine.IsDisabled);
        Assert.Empty(rig.Log.Errors);
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("IWorldComm"));
        Assert.True(SchedulerHarness.WaitFor(() => rig.MarksOf(item).Contains("ran")));
    }
}
