/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace Luau.ScriptEngine.Tests;

/// <summary>
/// Every engine receives every rez, with the engine name the region selected. This engine asks the ownership
/// question first and, for a script that is not its own, forgets the item (disowns it) and keeps its saved row.
/// </summary>
public class OwnershipTests
{
    private const string Phlox = "InWorldz.Phlox", Ours = "NGC.Luau";
    private const uint LocalId = 7000;

    private sealed class Rig
    {
        public Scene Scene = new SceneHelpers().SetupScene();
        public RecordingLog Log = new();
        public LuauEngine Engine;

        public Rig(ISavedStateStore store = null)
        {
            Engine = new LuauEngine(Log, (settings, log) => true, store, (settings, log, scene) => new RecordingLoader());
            var source = new IniConfigSource();
            source.AddConfig(EngineIdentity.ConfigSection).Set("Enabled", "true");
            Engine.Initialise(source);
            Engine.AddRegion(Scene);
        }

        public void Rez(UUID item, string script, string selectedEngine) =>
            Scene.EventManager.TriggerRezScript(LocalId, item, script, 0, false, selectedEngine, 0);
    }

    [Fact]
    public void AScriptWithOurHeaderIsAccepted()
    {
        var rig = new Rig();
        UUID item = UUID.Random();
        rig.Rez(item, "//NGC.Luau:slua\nprint(1)", Phlox);
        Assert.True(rig.Engine.IsTracking(item));
    }

    [Fact]
    public void AScriptTheRegionSelectedUsForIsAccepted()
    {
        var rig = new Rig();
        UUID item = UUID.Random();
        rig.Rez(item, "default { state_entry() {} }", Ours);
        Assert.True(rig.Engine.IsTracking(item));
    }

    [Fact]
    public void APlainScriptSelectedForPhloxIsNotAccepted()
    {
        var rig = new Rig();
        UUID item = UUID.Random();
        rig.Rez(item, "default { state_entry() {} }", Phlox);
        Assert.False(rig.Engine.IsTracking(item));
        Assert.False(rig.Engine.HasScript(item, out _));
    }

    [Fact]
    public void ASluaScriptWithoutOurHeaderSelectedForPhloxIsNotAccepted()
    {
        var rig = new Rig();
        UUID item = UUID.Random();
        rig.Rez(item, "--!slua\nprint(1)", Phlox);
        Assert.False(rig.Engine.IsTracking(item));
    }

    [Fact]
    public void AScriptThatMovesToAnotherEngineIsDisownedAndItsSavedRowIsKept()
    {
        UUID item = UUID.Random(), asset = UUID.Random();
        var store = new RowStore(item, asset);
        var rig = new Rig(store);

        rig.Rez(item, "//NGC.Luau:\ndefault {}", Phlox);
        Assert.True(rig.Engine.IsTracking(item));

        // The owner edits the first line to name Phlox: this engine must let go of the item.
        rig.Rez(item, "//InWorldz.Phlox:\ndefault {}", Phlox);

        Assert.False(rig.Engine.IsTracking(item));
        Assert.Equal(0, store.Deletes);
        Assert.True(rig.Engine.HasSavedScriptState(item, asset));   // the row is still ours: the script resumes if it returns
        Assert.Empty(rig.Engine.GetScriptErrors(item));             // no answer for the editor: the owning engine gives it
    }

    [Fact]
    public void ADisownedScriptThatReturnsWithTheSameAssetIsAcceptedAgain()
    {
        UUID item = UUID.Random(), asset = UUID.Random();
        var rig = new Rig(new RowStore(item, asset));

        rig.Rez(item, "//NGC.Luau:\ndefault {}", Phlox);
        rig.Rez(item, "//YEngine:\ndefault {}", Phlox);
        Assert.False(rig.Engine.IsTracking(item));
        rig.Rez(item, "//NGC.Luau:\ndefault {}", Phlox);
        Assert.True(rig.Engine.IsTracking(item));
        Assert.True(rig.Engine.HasSavedScriptState(item, asset));
    }

    [Fact]
    public void DisowningAnItemTheEngineNeverSawChangesNothing()
    {
        UUID item = UUID.Random(), asset = UUID.Random();
        var store = new RowStore(item, asset);
        var rig = new Rig(store);
        rig.Rez(item, "//YEngine:\ndefault {}", Phlox);
        Assert.False(rig.Engine.IsTracking(item));
        Assert.Equal(0, store.Deletes);
        Assert.Empty(rig.Log.Errors);
    }

    [Fact]
    public void AfterTheRegionIsRemovedRezEventsAreIgnored()
    {
        var rig = new Rig();
        rig.Engine.RemoveRegion(rig.Scene);
        UUID item = UUID.Random();
        rig.Rez(item, "//NGC.Luau:\ndefault {}", Phlox);
        Assert.False(rig.Engine.IsTracking(item));
    }
}
