/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Net;
using OpenMetaverse;
using OpenMetaverse.Packets;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.LindenUDP.Tests;

/// <summary>
/// A UseCircuitCode for a circuit the region already serves: the viewer resends it, or connects again from a new
/// socket. The region answers it without error and keeps the client it has.
/// </summary>
public class RepeatedUseCircuitCodeTests : OpenSimTestCase
{
    private const uint CircuitCode = 123456;
    private static readonly IPEndPoint FirstEndPoint = new(IPAddress.Loopback, 999);
    private static readonly IPEndPoint SecondEndPoint = new(IPAddress.Loopback, 1000);

    private Scene m_scene = null!;
    private TestLLUDPServer m_udpServer = null!;
    private UUID m_agentId;
    private UUID m_sessionId;

    public RepeatedUseCircuitCodeTests()
    {
        // The server runs the circuit handling on the calling thread in these tests.
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
    }

    public override void Dispose()
    {
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        base.Dispose();
    }

    public override void SetUp()
    {
        base.SetUp();
        m_scene = new SceneHelpers().SetupScene();
        StatsManager.SimExtraStats = new SimExtraStatsCollector();
        m_udpServer = ClientStackHelpers.AddUdpServer(m_scene);
        m_agentId = TestHelpers.ParseTail(0x1);
        m_sessionId = TestHelpers.ParseTail(0x2);
    }

    private void SendUseCircuitCode(IPEndPoint from, UUID sessionId)
    {
        UseCircuitCodePacket uccp = new()
        {
            CircuitCode = new UseCircuitCodePacket.CircuitCodeBlock
            {
                Code = CircuitCode,
                ID = m_agentId,
                SessionID = sessionId
            }
        };
        byte[] bytes = uccp.ToBytes();
        UDPPacketBuffer buffer = new(from);
        buffer.DataLength = bytes.Length;
        Buffer.BlockCopy(bytes, 0, buffer.Data, 0, bytes.Length);
        m_udpServer.PacketReceived(buffer);
    }

    private IClientAPI ConnectFirst()
    {
        ScenePresence sp = ClientStackHelpers.AddChildClient(m_scene, m_udpServer, m_agentId, m_sessionId, CircuitCode);
        Assert.NotNull(sp);
        Assert.True(m_scene.TryGetClient(m_agentId, out IClientAPI client));
        return client;
    }

    [Fact]
    public void ARepeatFromANewEndPointKeepsTheExistingClient()
    {
        TestHelpers.InMethod();
        IClientAPI first = ConnectFirst();

        SendUseCircuitCode(SecondEndPoint, m_sessionId);

        Assert.NotNull(m_scene.GetScenePresence(m_agentId));
        Assert.True(m_scene.TryGetClient(m_agentId, out IClientAPI now));
        Assert.Same(first, now);
        Assert.True(now.IsActive);
        Assert.NotNull(m_scene.AuthenticateHandler.GetAgentCircuitData(CircuitCode));
    }

    [Fact]
    public void ARepeatFromANewEndPointLeavesNoHalfOpenConnectionBehind()
    {
        TestHelpers.InMethod();
        ConnectFirst();

        SendUseCircuitCode(SecondEndPoint, m_sessionId);

        // A later packet from the new end point must not be held back waiting for a connection that never finishes:
        // the same repeat can be sent again and is again answered the same way.
        SendUseCircuitCode(SecondEndPoint, m_sessionId);
        Assert.NotNull(m_scene.GetScenePresence(m_agentId));
    }

    [Fact]
    public void ARepeatFromTheSameEndPointKeepsTheExistingClient()
    {
        TestHelpers.InMethod();
        IClientAPI first = ConnectFirst();

        SendUseCircuitCode(FirstEndPoint, m_sessionId);

        Assert.True(m_scene.TryGetClient(m_agentId, out IClientAPI now));
        Assert.Same(first, now);
        Assert.NotNull(m_scene.GetScenePresence(m_agentId));
    }

    [Fact]
    public void ACircuitCodeWithAnotherSessionStillReplacesTheClient()
    {
        TestHelpers.InMethod();
        IClientAPI first = ConnectFirst();

        // A new login for the same agent comes with a new session and new circuit data.
        UUID newSession = TestHelpers.ParseTail(0x3);
        AgentCircuitData acd = new() { AgentID = m_agentId, SessionID = newSession };
        m_scene.AuthenticateHandler.AddNewCircuit(CircuitCode, acd);
        SendUseCircuitCode(SecondEndPoint, newSession);

        Assert.True(m_scene.TryGetClient(m_agentId, out IClientAPI now));
        Assert.NotSame(first, now);
    }
}
