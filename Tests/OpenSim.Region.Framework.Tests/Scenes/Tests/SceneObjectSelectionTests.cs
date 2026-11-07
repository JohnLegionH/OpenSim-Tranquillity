/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// Which agent holds an object selected. A viewer selects an object when its owner edits it, and the simulator holds the
/// object still for as long as anyone has it selected. Selection is recorded per agent: one agent deselecting must not
/// release an object another agent still has selected, and an agent's selections end when it leaves the region.
/// </summary>
// Each test builds its own scene and stand-in physics actor and touches no process-wide state, so the class runs in parallel.
public class SceneObjectSelectionTests : OpenSimTestCase
{
    /// <summary>A physics actor that remembers whether the simulator is holding the object selected.</summary>
    private sealed class SelectionActor : NullPhysicsActor
    {
        public bool Held;
        public int Changes;
        public override bool Selected { set { Held = value; Changes++; } }
    }

    private sealed class Fixture
    {
        public TestScene Scene;
        public SceneObjectGroup Group;
        public SelectionActor Actor;
        public ScenePresence A, B;
        public SceneObjectPart Root => Group.RootPart;

        public void Select(ScenePresence by, SceneObjectPart part = null)
            => Scene.SelectPrim(new List<uint> { (part ?? Root).LocalId }, by.ControllingClient);
        public void Deselect(ScenePresence by, SceneObjectPart part = null)
            => Scene.DeselectPrim((part ?? Root).LocalId, by.ControllingClient);
    }

    private static Fixture Make(int parts = 1)
    {
        var f = new Fixture { Scene = new SceneHelpers().SetupScene() };
        f.A = SceneHelpers.AddScenePresence(f.Scene, TestHelpers.ParseTail(0x21));
        f.B = SceneHelpers.AddScenePresence(f.Scene, TestHelpers.ParseTail(0x22));
        f.Group = SceneHelpers.AddSceneObject(f.Scene, parts, f.A.UUID, "sel", 0x200);
        f.Actor = new SelectionActor();
        f.Root.PhysActor = f.Actor;
        return f;
    }

    [Fact]
    public void OneAgentSelectsAndDeselects_ObjectIsReleased()
    {
        var f = Make();
        f.Select(f.A);
        Assert.True(f.Group.IsSelected);
        Assert.True(f.Actor.Held);

        f.Deselect(f.A);
        Assert.False(f.Group.IsSelected);
        Assert.False(f.Actor.Held);
    }

    [Fact]
    public void SelectIsRecordedForTheSelectingAgentOnly()
    {
        var f = Make();
        f.Select(f.A);
        Assert.True(f.Root.IsSelectedBy(f.A.UUID));
        Assert.False(f.Root.IsSelectedBy(f.B.UUID));
    }

    [Fact]
    public void DeselectByAnAgentThatNeverSelected_DoesNotReleaseTheObject()
    {
        var f = Make();
        f.Select(f.A);

        f.Deselect(f.B);

        Assert.True(f.Group.IsSelected);
        Assert.True(f.Actor.Held);
        Assert.True(f.Root.IsSelectedBy(f.A.UUID));
    }

    [Fact]
    public void TwoAgentsSelect_OneDeselects_ObjectStaysSelectedUntilTheOtherDeselects()
    {
        var f = Make();
        f.Select(f.A);
        f.Select(f.B);

        f.Deselect(f.A);
        Assert.True(f.Group.IsSelected);
        Assert.True(f.Actor.Held);
        Assert.False(f.Root.IsSelectedBy(f.A.UUID));
        Assert.True(f.Root.IsSelectedBy(f.B.UUID));

        f.Deselect(f.B);
        Assert.False(f.Group.IsSelected);
        Assert.False(f.Actor.Held);
    }

    [Fact]
    public void RepeatedSelectByOneAgent_NeedsOnlyOneDeselect()
    {
        var f = Make();
        f.Select(f.A);
        f.Select(f.A);
        f.Select(f.A);

        f.Deselect(f.A);

        Assert.False(f.Group.IsSelected);
        Assert.False(f.Actor.Held);
    }

    [Fact]
    public void ChildPartSelectedByTwoAgents_OtherAgentsDeselectKeepsTheLinksetSelected()
    {
        var f = Make(3);
        var child = f.Group.Parts[1];
        f.Select(f.A, child);
        f.Select(f.B, child);

        f.Deselect(f.B, child);

        Assert.True(child.IsSelected);
        Assert.True(f.Group.IsSelected);
        Assert.True(f.Actor.Held);
    }

    [Fact]
    public void AgentLogsOut_ItsSelectionsEndAndTheObjectResumes()
    {
        var f = Make();
        f.Select(f.A);
        Assert.True(f.Actor.Held);

        f.Scene.RemoveClient(f.A.UUID, true);

        Assert.False(f.Group.IsSelected);
        Assert.False(f.Actor.Held);
    }

    [Fact]
    public void AgentDisconnects_ItsSelectionsEndAndTheObjectResumes()
    {
        var f = Make();
        f.Select(f.A);

        // the circuit closing, as it does when a viewer stops answering
        ((TestClient)f.A.ControllingClient).Close(false, true);

        Assert.False(f.Group.IsSelected);
        Assert.False(f.Actor.Held);
    }

    [Fact]
    public void AgentLeavesTheRegion_ItsSelectionsEndAndTheObjectResumes()
    {
        var f = Make();
        f.Select(f.A);
        Assert.True(f.Actor.Held);

        // a teleport away and a crossing into a neighbour both turn the root agent into a child agent of this region
        f.A.MakeChildAgent(1099511627776UL);

        Assert.False(f.Group.IsSelected);
        Assert.False(f.Actor.Held);
    }

    [Fact]
    public void AgentLeaves_SelectionsOfOtherAgentsStay()
    {
        var f = Make();
        f.Select(f.A);
        f.Select(f.B);

        f.Scene.RemoveClient(f.A.UUID, true);

        Assert.True(f.Group.IsSelected);
        Assert.True(f.Actor.Held);
        Assert.True(f.Root.IsSelectedBy(f.B.UUID));
    }

    [Fact]
    public void SelectArrivingAfterTheAgentLeft_IsIgnored()
    {
        var f = Make();
        var client = f.A.ControllingClient;
        f.Scene.RemoveClient(f.A.UUID, true);

        // a select that was still queued when the circuit closed
        f.Scene.SelectPrim(new List<uint> { f.Root.LocalId }, client);

        Assert.False(f.Group.IsSelected);
        Assert.False(f.Actor.Held);
    }
}
