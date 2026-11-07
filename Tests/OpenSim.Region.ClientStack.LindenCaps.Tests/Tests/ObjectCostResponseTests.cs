/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.Linden.Tests;

/// <summary>
/// The GetObjectCost reply. Viewers ask for the cost of every object they have not costed yet in one request, a whole
/// region's worth for an object search, so there is no limit on the ids in a request; the work per id must stay small
/// instead: a linkset's cost is worked out once per request, not once per id.
/// </summary>
// Each test builds its own scene and touches no process-wide state, so the class runs in parallel.
public class ObjectCostResponseTests : OpenSimTestCase
{
    private static readonly UUID Owner = new("00000000-0000-0000-0000-100000000000");

    private static OSDMap Ask(Scene scene, IEnumerable<UUID> ids)
    {
        var arr = new OSDArray();
        foreach (var id in ids) arr.Add(OSD.FromUUID(id));
        var body = LLSDxmlEncode2.EndToNBBytes(OpenSim.Region.ClientStack.LindenCaps.BunchOfCaps.BuildObjectCostReply(scene, arr));
        return (OSDMap)OSDParser.DeserializeLLSDXml(body);
    }

    [Fact]
    public void EveryPartOfALinkset_GetsItsOwnAndTheLinksetCost()
    {
        var scene = new SceneHelpers().SetupScene();
        var g = SceneHelpers.AddSceneObject(scene, 4, Owner, "cost", 0x300);

        var reply = Ask(scene, g.Parts.Select(p => p.UUID));

        Assert.Equal(4, reply.Count);
        foreach (var p in g.Parts)
        {
            var m = (OSDMap)reply[p.UUID.ToString()];
            Assert.Equal(4f, (float)m["linked_set_resource_cost"].AsReal());
            Assert.Equal(1f, (float)m["resource_cost"].AsReal());
            Assert.Equal(4f, (float)m["linked_set_physics_cost"].AsReal());
            Assert.Equal(1f, (float)m["physics_cost"].AsReal());
        }
    }

    [Fact]
    public void LinksetCostIsWorkedOutOnceForAnyNumberOfIdsInTheRequest()
    {
        var scene = new SceneHelpers().SetupScene();
        var g = SceneHelpers.AddSceneObject(scene, 40, Owner, "cost", 0x400);
        var ids = g.Parts.Select(p => p.UUID).ToList();

        var reply = Ask(scene, ids);

        Assert.Equal(40, reply.Count);
        Assert.Equal(1, g.ResourceCostPasses);
    }

    [Fact]
    public void ARequestOfThousandsOfIds_IsAnsweredInFull()
    {
        var scene = new SceneHelpers().SetupScene();
        var ids = new List<UUID>();
        for (int i = 0; i < 60; i++)
            ids.AddRange(SceneHelpers.AddSceneObject(scene, 1, Owner, "o" + i, 0x1000 + i * 0x10).Parts.Select(p => p.UUID));
        var asked = new List<UUID>(ids);
        while (asked.Count < 6000) asked.Add(ids[asked.Count % ids.Count]);

        var reply = Ask(scene, asked);

        Assert.Equal(60, reply.Count);
    }

    [Fact]
    public void UnknownIdsAreLeftOut_AndAnAllUnknownRequestGetsTheZeroEntry()
    {
        var scene = new SceneHelpers().SetupScene();
        var g = SceneHelpers.AddSceneObject(scene, 1, Owner, "cost", 0x500);

        var some = Ask(scene, new[] { g.RootPart.UUID, UUID.Random() });
        Assert.Equal(1, some.Count);
        Assert.True(some.ContainsKey(g.RootPart.UUID.ToString()));

        var none = Ask(scene, new[] { UUID.Random() });
        Assert.Equal(1, none.Count);
        Assert.True(none.ContainsKey(UUID.Zero.ToString()));

        Assert.Equal(0, Ask(scene, new UUID[0]).Count);
    }
}
