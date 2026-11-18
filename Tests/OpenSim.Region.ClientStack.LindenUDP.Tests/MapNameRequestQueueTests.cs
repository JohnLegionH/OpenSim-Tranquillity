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
using OpenMetaverse.Packets;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.LindenUDP.Tests;

/// <summary>
/// A viewer asks for a region by name while the region is still busy with that viewer's login. The name search must
/// not wait behind the other requests of the same client: they run one after another on the client's own queue, and
/// a login puts requests on it that take seconds.
/// </summary>
public class MapNameRequestQueueTests : OpenSimTestCase
{
    private const uint CircuitCode = 654321;
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    public MapNameRequestQueueTests()
    {
        // These tests need real worker threads: the client's queue runs its jobs through the thread pool.
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
    }

    private static LLClientView Connect(out Scene scene)
    {
        scene = new SceneHelpers().SetupScene();
        TestLLUDPServer udpServer = ClientStackHelpers.AddUdpServer(scene);
        AgentCircuitData circuit = SceneHelpers.GenerateAgentData(UUID.Random());
        scene.AuthenticateHandler.AddNewCircuit(CircuitCode, circuit);
        LLUDPClient udpClient = new(udpServer, new ThrottleRates(new IniConfigSource()), udpServer.Throttle,
            CircuitCode, circuit.AgentID, new IPEndPoint(IPAddress.Loopback, 999), 0, 0);
        AuthenticateResponse response = new()
        {
            LoginInfo = new Login { First = "Test", Last = "Viewer", SecureSession = circuit.SecureSessionID }
        };
        LLClientView client = new(scene, udpServer, udpClient, response, circuit.AgentID, circuit.SessionID, CircuitCode);
        client.m_asyncPacketProcess.Start();
        return client;
    }

    private static MapNameRequestPacket NameRequest(string name)
    {
        return new MapNameRequestPacket
        {
            AgentData = new MapNameRequestPacket.AgentDataBlock { AgentID = UUID.Random(), SessionID = UUID.Random(), Flags = 0 },
            NameData = new MapNameRequestPacket.NameDataBlock { Name = Util.UTF8.GetBytes(name + "\0") }
        };
    }

    /// <summary>Puts a job on the client's queue that keeps the queue busy until the gate opens, as a login job does.</summary>
    private static ManualResetEventSlim BusyQueue(LLClientView client)
    {
        ManualResetEventSlim started = new(false);
        ManualResetEventSlim gate = new(false);
        client.m_asyncPacketProcess.QueueJob("CompleteAgentMovement", () => { started.Set(); gate.Wait(TimeSpan.FromSeconds(60)); });
        Assert.True(started.Wait(Generous));
        return gate;
    }

    [Fact]
    public void ANameSearchIsAnsweredWhileTheClientsQueueIsBusyWithALoginJob()
    {
        TestHelpers.InMethod();
        LLClientView client = Connect(out Scene _);
        string asked = null;
        using ManualResetEventSlim handled = new(false);
        client.OnMapNameRequest += (_, name, _) => { asked = name; handled.Set(); };
        ManualResetEventSlim gate = BusyQueue(client);
        try
        {
            client.ProcessInPacket(NameRequest("Example Region"));

            Assert.True(handled.Wait(Generous), "the name search waited behind the busy queue");
            Assert.Equal("Example Region", asked);
        }
        finally
        {
            gate.Set();
            client.Close(false, false);
        }
    }

    [Fact]
    public void AnEmptyNameMeansTheClientsOwnRegion()
    {
        TestHelpers.InMethod();
        LLClientView client = Connect(out Scene scene);
        string asked = null;
        using ManualResetEventSlim handled = new(false);
        client.OnMapNameRequest += (_, name, _) => { asked = name; handled.Set(); };
        try
        {
            // A request that carries no name bytes at all asks for the client's own region.
            MapNameRequestPacket request = NameRequest("");
            request.NameData.Name = Array.Empty<byte>();
            client.ProcessInPacket(request);

            Assert.True(handled.Wait(Generous));
            Assert.Equal(scene.RegionInfo.RegionName, asked);
        }
        finally
        {
            client.Close(false, false);
        }
    }

    [Fact]
    public void OtherRequestsOfTheClientStillWaitTheirTurnOnTheQueue()
    {
        TestHelpers.InMethod();
        LLClientView client = Connect(out Scene _);
        using ManualResetEventSlim handled = new(false);
        client.OnRequestMapBlocks += (_, _, _, _, _, _) => handled.Set();
        ManualResetEventSlim gate = BusyQueue(client);
        try
        {
            client.ProcessInPacket(new MapBlockRequestPacket
            {
                AgentData = new MapBlockRequestPacket.AgentDataBlock { AgentID = UUID.Random(), SessionID = UUID.Random() },
                PositionData = new MapBlockRequestPacket.PositionDataBlock { MinX = 1, MaxX = 2, MinY = 1, MaxY = 2 }
            });

            // Still waiting behind the busy job: a window in which something must not happen.
            Assert.False(handled.Wait(TimeSpan.FromMilliseconds(500)));
            gate.Set();
            Assert.True(handled.Wait(Generous), "the request never ran once the queue was free");
        }
        finally
        {
            gate.Set();
            client.Close(false, false);
        }
    }

    // ---- one name search at a time per client ----

    /// <summary>
    /// Stands in for the grid service behind the map search: records every search it is given and how many ran at
    /// the same moment, and holds each search until the test lets it finish.
    /// </summary>
    private sealed class CountingSearch
    {
        private readonly object m_lock = new();
        private readonly SemaphoreSlim m_release = new(0);
        private int m_running;
        private int m_started;
        private int m_maxRunning;
        private readonly List<string> m_answered = new();
        public bool ThrowOnFirst;

        public int Started => Volatile.Read(ref m_started);
        public int MaxRunning => Volatile.Read(ref m_maxRunning);

        public void Attach(LLClientView client)
        {
            client.OnMapNameRequest += (_, name, _) =>
            {
                int now = Interlocked.Increment(ref m_running);
                int started = Interlocked.Increment(ref m_started);
                lock (m_lock)
                {
                    m_maxRunning = Math.Max(m_maxRunning, now);
                }
                m_release.Wait(TimeSpan.FromSeconds(60));
                lock (m_lock)
                {
                    m_answered.Add(name);
                }
                Interlocked.Decrement(ref m_running);
                if (ThrowOnFirst && started == 1)
                    throw new InvalidOperationException("the grid service failed");
            };
        }

        public void Let(int searches) => m_release.Release(searches);

        public string[] Snapshot()
        {
            lock (m_lock)
            {
                return m_answered.ToArray();
            }
        }
    }

    private static void Wait(Func<bool> condition, string what)
    {
        DateTime end = DateTime.UtcNow + Generous;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < end, what);
            Thread.Sleep(20);
        }
    }

    private static void Ask(LLClientView client, string name) => client.ProcessInPacket(NameRequest(name));

    [Fact]
    public void ABurstOfFiftySearchesFromOneClientNeverHasMoreThanOneRunning()
    {
        TestHelpers.InMethod();
        LLClientView client = Connect(out Scene _);
        CountingSearch grid = new();
        grid.Attach(client);
        try
        {
            for (int i = 1; i <= 50; i++)
                Ask(client, $"Region {i:00}");
            Wait(() => grid.Started >= 1, "no search started");
            grid.Let(100);
            Wait(() => grid.Snapshot().Length >= 5, "the kept searches were not answered");
            Thread.Sleep(300); // a window in which nothing more may start

            Assert.Equal(1, grid.MaxRunning);
            Assert.Equal(5, grid.Snapshot().Length);
        }
        finally
        {
            grid.Let(100);
            client.Close(false, false);
        }
    }

    [Fact]
    public void TheKeptSearchesAreAnsweredInOrderAndTheOldestWaitingOnesAreDropped()
    {
        TestHelpers.InMethod();
        LLClientView client = Connect(out Scene _);
        CountingSearch grid = new();
        grid.Attach(client);
        try
        {
            for (int i = 1; i <= 50; i++)
                Ask(client, $"Region {i:00}");
            Wait(() => grid.Started >= 1, "no search started");
            grid.Let(100);
            Wait(() => grid.Snapshot().Length >= 5, "the kept searches were not answered");
            Thread.Sleep(300);

            // The first one was already running; of the 49 that waited, the 4 asked last are kept.
            Assert.Equal(new[] { "Region 01", "Region 47", "Region 48", "Region 49", "Region 50" }, grid.Snapshot());
        }
        finally
        {
            grid.Let(100);
            client.Close(false, false);
        }
    }

    [Fact]
    public void OneRunningAndFourWaitingSearchesAreAllAnsweredInOrder()
    {
        TestHelpers.InMethod();
        LLClientView client = Connect(out Scene _);
        CountingSearch grid = new();
        grid.Attach(client);
        try
        {
            for (int i = 1; i <= 5; i++)
                Ask(client, $"Region {i}");
            Wait(() => grid.Started >= 1, "no search started");
            grid.Let(100);
            Wait(() => grid.Snapshot().Length >= 5, "not every search was answered");

            Assert.Equal(new[] { "Region 1", "Region 2", "Region 3", "Region 4", "Region 5" }, grid.Snapshot());
            Assert.Equal(1, grid.MaxRunning);
        }
        finally
        {
            grid.Let(100);
            client.Close(false, false);
        }
    }

    [Fact]
    public void AFifthWaitingSearchDropsTheOldestWaitingOne()
    {
        TestHelpers.InMethod();
        LLClientView client = Connect(out Scene _);
        CountingSearch grid = new();
        grid.Attach(client);
        try
        {
            for (int i = 1; i <= 6; i++)
                Ask(client, $"Region {i}");
            Wait(() => grid.Started >= 1, "no search started");
            grid.Let(100);
            Wait(() => grid.Snapshot().Length >= 5, "the kept searches were not answered");
            Thread.Sleep(300);

            Assert.Equal(new[] { "Region 1", "Region 3", "Region 4", "Region 5", "Region 6" }, grid.Snapshot());
        }
        finally
        {
            grid.Let(100);
            client.Close(false, false);
        }
    }

    [Fact]
    public void AnotherClientsSearchIsAnsweredWhileTheFirstClientsBurstRuns()
    {
        TestHelpers.InMethod();
        LLClientView first = Connect(out Scene _);
        LLClientView second = Connect(out Scene _);
        CountingSearch firstGrid = new();
        firstGrid.Attach(first);
        CountingSearch secondGrid = new();
        secondGrid.Attach(second);
        try
        {
            for (int i = 1; i <= 50; i++)
                Ask(first, $"Region {i:00}");
            Wait(() => firstGrid.Started >= 1, "the first client's search did not start");

            Ask(second, "Other Region");
            Wait(() => secondGrid.Started >= 1, "the other client's search waited behind the first client's burst");
            secondGrid.Let(1);
            Wait(() => secondGrid.Snapshot().Length == 1, "the other client's search was not answered");

            Assert.Equal(new[] { "Other Region" }, secondGrid.Snapshot());
            Assert.Empty(firstGrid.Snapshot()); // the first client's search is still held
        }
        finally
        {
            firstGrid.Let(100);
            secondGrid.Let(100);
            first.Close(false, false);
            second.Close(false, false);
        }
    }

    [Fact]
    public void ASearchThatFailsDoesNotStopTheNextOne()
    {
        TestHelpers.InMethod();
        LLClientView client = Connect(out Scene _);
        CountingSearch grid = new() { ThrowOnFirst = true };
        grid.Attach(client);
        try
        {
            Ask(client, "Region 1");
            Ask(client, "Region 2");
            Wait(() => grid.Started >= 1, "no search started");
            grid.Let(100);
            Wait(() => grid.Snapshot().Length >= 2, "the search after the failed one was not answered");

            Assert.Equal(new[] { "Region 1", "Region 2" }, grid.Snapshot());
        }
        finally
        {
            grid.Let(100);
            client.Close(false, false);
        }
    }

    [Fact]
    public void AClosedClientDoesNotRunItsWaitingSearches()
    {
        TestHelpers.InMethod();
        LLClientView client = Connect(out Scene _);
        CountingSearch grid = new();
        grid.Attach(client);
        try
        {
            for (int i = 1; i <= 4; i++)
                Ask(client, $"Region {i}");
            Wait(() => grid.Started >= 1, "no search started");

            client.Close(false, false);
            grid.Let(100);
            Wait(() => grid.Snapshot().Length >= 1, "the running search did not finish");
            Thread.Sleep(300); // a window in which nothing more may start

            Assert.Equal(new[] { "Region 1" }, grid.Snapshot());
        }
        finally
        {
            grid.Let(100);
        }
    }
}
