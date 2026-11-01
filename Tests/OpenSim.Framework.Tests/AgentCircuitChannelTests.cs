/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace OpenSim.Framework.Tests;

/// <summary>
/// The viewer's login channel travels with the agent's circuit data to every region the viewer connects to
/// (child agents on neighbouring regions, teleport and hypergrid arrivals), because those regions decide from it
/// what the viewer can be sent.
/// </summary>
public class AgentCircuitChannelTests
{
    [Fact]
    public void ChannelSurvivesPackingForAnotherRegion()
    {
        AgentCircuitData sent = new()
        {
            AgentID = UUID.Random(),
            SessionID = UUID.Random(),
            SecureSessionID = UUID.Random(),
            firstname = "Test",
            lastname = "User",
            Channel = "Second Life Release",
            Viewer = "26.4.0.1"
        };

        OSDMap packed = sent.PackAgentCircuitData(new EntityTransferContext());
        AgentCircuitData received = new();
        received.UnpackAgentCircuitData(packed);

        Assert.Equal("Second Life Release", received.Channel);
    }
}
