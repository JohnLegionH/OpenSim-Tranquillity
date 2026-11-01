/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.LindenUDP.Tests;

/// <summary>
/// Whether a connected viewer is presented with larger regions as their south-west 256 m: decided from the channel
/// in its circuit data, the grid-wide list in [ClientStack.LindenUDP] LegacyRegionSizeViewers, and a region's own
/// LegacyRegionSizeViewers setting, which replaces the grid-wide list for that region (empty turns it off).
/// </summary>
public class LegacyRegionSizeClientTests : OpenSimTestCase
{
    private const uint CircuitCode = 654321;

    private static LLClientView Connect(Scene scene, TestLLUDPServer udpServer, string channel)
    {
        AgentCircuitData circuit = SceneHelpers.GenerateAgentData(UUID.Random());
        circuit.Channel = channel;
        scene.AuthenticateHandler.AddNewCircuit(CircuitCode, circuit);

        LLUDPClient udpClient = new(udpServer, new ThrottleRates(new IniConfigSource()), udpServer.Throttle,
            CircuitCode, circuit.AgentID, new IPEndPoint(IPAddress.Loopback, 999), 0, 0);
        AuthenticateResponse response = new()
        {
            LoginInfo = new Login { First = "Test", Last = "Viewer", SecureSession = circuit.SecureSessionID }
        };
        return new LLClientView(scene, udpServer, udpClient, response, circuit.AgentID, circuit.SessionID, CircuitCode);
    }

    private static bool Only256m(string channel, string? gridWideList = null, string? regionSetting = null)
    {
        Scene scene = new SceneHelpers().SetupScene("Legacy viewer test region", UUID.Random(), 1000, 1000, 1024, 1024,
            new IniConfigSource());
        if (regionSetting is not null)
            scene.RegionInfo.SetExtraSetting("LegacyRegionSizeViewers", regionSetting);

        IniConfigSource config = new();
        if (gridWideList is not null)
        {
            IConfig section = config.AddConfig("ClientStack.LindenUDP");
            section.Set("LegacyRegionSizeViewers", gridWideList);
        }

        TestLLUDPServer udpServer = ClientStackHelpers.AddUdpServer(scene, config);
        LLClientView client = Connect(scene, udpServer, channel);
        try
        {
            return ((IClientAPI)client).Only256mRegions;
        }
        finally
        {
            client.Close(false, false);
        }
    }

    [Theory]
    [InlineData("Second Life Release")]
    [InlineData("Second Life Beta")]
    [InlineData("Second Life Test")]
    public void LindenLabsViewersAreLegacyByDefault(string channel)
    {
        TestHelpers.InMethod();
        Assert.True(Only256m(channel));
    }

    [Theory]
    [InlineData("Firestorm-Releasex64")]
    [InlineData("Example Viewer")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherViewersAreNotLegacyByDefault(string channel)
    {
        TestHelpers.InMethod();
        Assert.False(Only256m(channel));
    }

    [Fact]
    public void AnEmptyGridWideListTurnsItOff()
    {
        TestHelpers.InMethod();
        Assert.False(Only256m("Second Life Release", gridWideList: ""));
    }

    [Fact]
    public void TheGridWideListReplacesTheDefault()
    {
        TestHelpers.InMethod();
        Assert.True(Only256m("Example Viewer 1.0", gridWideList: "Example Viewer"));
        Assert.False(Only256m("Second Life Release", gridWideList: "Example Viewer"));
    }

    [Fact]
    public void ARegionCanOptOut()
    {
        TestHelpers.InMethod();
        Assert.False(Only256m("Second Life Release", regionSetting: ""));
    }

    [Fact]
    public void ARegionsListReplacesTheGridWideList()
    {
        TestHelpers.InMethod();
        Assert.True(Only256m("Example Viewer 1.0", gridWideList: "Second Life", regionSetting: "Example Viewer"));
        Assert.False(Only256m("Second Life Release", gridWideList: "Second Life", regionSetting: "Example Viewer"));
    }
}
