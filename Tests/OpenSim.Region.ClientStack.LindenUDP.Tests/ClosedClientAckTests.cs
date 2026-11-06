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
/// The server's send loop sends a client's packets on its own thread while the client is closed on another one.
/// Closing a client (<see cref="LLUDPClient.Shutdown"/>) clears its list of packets waiting for an acknowledgement,
/// and a packet that was already on its way out is added to that list afterwards. The list used to throw a
/// NullReferenceException for every call made after it was cleared, so one iteration of the send loop was lost
/// with an error in the log. A cleared list now stays usable, drops what it is given and stays empty.
/// </summary>
/// <remarks>
/// The test assembly runs its classes one at a time. The packets carry buffers that are returned to the process-wide
/// buffer pool when the list drops them; nothing here depends on the pool's content.
/// </remarks>
public class ClosedClientAckTests : OpenSimTestCase
{
    private const uint CircuitCode = 777301;
    private static readonly IPEndPoint Peer = new(IPAddress.Loopback, 9000);
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);

    private static LLUDPClient NewClient()
    {
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
        Scene scene = new SceneHelpers().SetupScene("Closed client test region", UUID.Random(), 1000, 1000, 256, 256,
            new IniConfigSource());
        TestLLUDPServer server = ClientStackHelpers.AddUdpServer(scene);
        return new LLUDPClient(server, new ThrottleRates(new IniConfigSource()), server.Throttle,
            CircuitCode, UUID.Random(), Peer, 0, 0);
    }

    private static OutgoingPacket NewPacket(LLUDPClient client, uint sequenceNumber)
    {
        UDPPacketBuffer buffer = new(Peer) { DataLength = 40 };
        return new OutgoingPacket(client, buffer, ThrottleOutPacketType.Task, null) { SequenceNumber = sequenceNumber };
    }

    [Fact]
    public void A_packet_added_after_the_client_was_closed_is_dropped_without_an_error()
    {
        LLUDPClient client = NewClient();
        client.Shutdown();

        Exception error = Record.Exception(() => client.NeedAcks.Add(NewPacket(client, 1)));

        Assert.Null(error);
        Assert.Equal(0, CountOf(client));
        Assert.Equal(0, client.UnackedBytes);
    }

    [Fact]
    public void An_acknowledgement_or_removal_after_the_client_was_closed_is_dropped_without_an_error()
    {
        LLUDPClient client = NewClient();
        client.Shutdown();

        Exception ack = Record.Exception(() => client.NeedAcks.Acknowledge(5, Environment.TickCount, false));
        Exception remove = Record.Exception(() => client.NeedAcks.Remove(6));

        Assert.Null(ack);
        Assert.Null(remove);
        Assert.Equal(0, CountOf(client));
    }

    [Fact]
    public void A_closed_client_has_no_packets_waiting_for_an_acknowledgement()
    {
        LLUDPClient client = NewClient();
        client.NeedAcks.Add(NewPacket(client, 1));
        client.NeedAcks.Add(NewPacket(client, 2));
        Assert.Equal(2, CountOf(client));

        client.Shutdown();

        List<OutgoingPacket> expired = null;
        Exception error = Record.Exception(() => expired = client.NeedAcks.GetExpiredPackets(0));
        Assert.Null(error);
        Assert.Null(expired);
        Assert.Equal(0, CountOf(client));
        Assert.Equal(0u, client.NeedAcks.Oldest());
    }

    [Fact]
    public void Packets_added_while_the_client_is_being_closed_never_raise_an_error()
    {
        LLUDPClient client = NewClient();
        int added = 0;
        bool stop = false;
        Exception adderError = null;
        Thread adder = new(() =>
        {
            try
            {
                uint sequence = 0;
                while (!Volatile.Read(ref stop))
                {
                    client.NeedAcks.Add(NewPacket(client, ++sequence));
                    Interlocked.Increment(ref added);
                }
            }
            catch (Exception e)
            {
                adderError = e;
            }
        });
        adder.Start();
        try
        {
            WaitUntil(() => Volatile.Read(ref added) > 100 || adderError is not null);
            client.Shutdown();
            // let the sender carry on adding after the client is closed
            int after = Volatile.Read(ref added);
            WaitUntil(() => Volatile.Read(ref added) > after + 100 || adderError is not null);
        }
        finally
        {
            Volatile.Write(ref stop, true);
            Assert.True(adder.Join(Generous));
        }

        Assert.Null(adderError);
        Assert.Equal(0, CountOf(client));
    }

    /// <summary>The number of packets waiting for an acknowledgement, or -1 when asking raised an error.</summary>
    private static int CountOf(LLUDPClient client)
    {
        try { return client.NeedAcks.Count(); }
        catch (Exception) { return -1; }
    }

    private static void WaitUntil(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow + Generous;
        while (!condition() && DateTime.UtcNow < end)
            Thread.Yield();
        Assert.True(condition());
    }
}
