/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.World.Land.Tests;

/// <summary>
/// Selecting and deselecting objects changes the parcel's "selected prims" count. It must do so without a full recount
/// of every object in the region: a viewer that selects and deselects a whole region's objects (an object search) would
/// otherwise cost one region-wide recount per count read.
/// </summary>
// Each test builds its own scene and modules and touches no process-wide state, so the class runs in parallel.
public class PrimCountSelectionTests : OpenSimTestCase
{
    private readonly UUID m_owner = new("00000000-0000-0000-0000-100000000000");
    private TestScene m_scene = null!;
    private PrimCountModule m_pcm = null!;
    private ILandObject m_parcel = null!;
    private ScenePresence m_sp = null!;

    public override void SetUp()
    {
        base.SetUp();
        m_pcm = new PrimCountModule();
        var lmm = new LandManagementModule();
        m_scene = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(m_scene, lmm, m_pcm);

        ILandObject lo = new LandObject(m_owner, false, m_scene);
        lo.SetLandBitmap(lo.GetSquareLandBitmap(0, 0, (int)Constants.RegionSize, (int)Constants.RegionSize));
        m_parcel = lmm.AddLandObject(lo);
        m_sp = SceneHelpers.AddScenePresence(m_scene, m_owner);
    }

    private SceneObjectGroup Add(int parts, string name, int tail)
    {
        var sog = SceneHelpers.CreateSceneObject(parts, m_owner, name, tail);
        m_scene.AddNewSceneObject(sog, false);
        return sog;
    }

    private void Select(params SceneObjectGroup[] groups)
    {
        var ids = new List<uint>();
        foreach (var g in groups) ids.Add(g.RootPart.LocalId);
        m_scene.SelectPrim(ids, m_sp.ControllingClient);
    }

    private void Deselect(params SceneObjectGroup[] groups)
    {
        foreach (var g in groups) m_scene.DeselectPrim(g.RootPart.LocalId, m_sp.ControllingClient);
    }

    private int Selected => m_parcel.PrimCounts.Selected;

    [Fact]
    public void SelectedCountFollowsSelectAndDeselect()
    {
        var a = Add(3, "a", 0x01);
        var b = Add(2, "b", 0x10);
        Assert.Equal(0, Selected);

        Select(a);
        Assert.Equal(3, Selected);
        Select(b);
        Assert.Equal(5, Selected);
        Select(a); // selecting again changes nothing
        Assert.Equal(5, Selected);

        Deselect(a);
        Assert.Equal(2, Selected);
        Deselect(b);
        Assert.Equal(0, Selected);
    }

    [Fact]
    public void SelectAndDeselect_DoNotTriggerAFullRecount()
    {
        var objects = new SceneObjectGroup[20];
        for (int i = 0; i < objects.Length; i++)
            objects[i] = Add(2, "o" + i, 0x100 + i * 0x10);

        // the first read after objects were added recounts once
        Assert.Equal(0, Selected);
        int before = m_pcm.RecountCount;

        for (int round = 0; round < 3; round++)
        {
            Select(objects);
            Assert.Equal(40, Selected); // a read between the select and the deselect, as ParcelProperties does
            Deselect(objects);
            Assert.Equal(0, Selected);
        }

        Assert.Equal(before, m_pcm.RecountCount);
    }

    [Fact]
    public void SelectedCountAfterARecount_StillIncludesSelectedObjects()
    {
        var a = Add(3, "a", 0x01);
        Select(a);
        Assert.Equal(3, Selected);

        m_pcm.TaintPrimCount(); // any other change to the objects forces a recount
        Assert.Equal(3, Selected);

        Deselect(a);
        Assert.Equal(0, Selected);
    }
}
