/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ClientStack.LindenUDP.Tests;

/// <summary>
/// The UDP server keeps its packet buffers in one static pool and several senders (for example the agent
/// movement-complete and terse update writers in LLClientView) write into a pooled buffer through a raw pointer,
/// assuming it has the standard size. A buffer of any other size that ends up in the pool is therefore written past
/// its end by the next sender that takes it, which damages the managed heap and crashes the process some time later.
/// The test helper that adds a client has to leave only standard size buffers in the pool.
/// </summary>
public class UdpBufferPoolTests : OpenSimTestCase
{
    private static void EmptyPool()
    {
        lock (OpenSimUDPBase.m_udpBuffersPoolLock)
        {
            Array.Clear(OpenSimUDPBase.m_udpBuffersPool);
            OpenSimUDPBase.m_udpBuffersPoolPtr = -1;
        }
    }

    [Fact]
    public void Adding_a_client_through_the_test_helper_leaves_only_standard_size_buffers_in_the_pool()
    {
        TestHelpers.InMethod();
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
        EmptyPool();
        try
        {
            int standardSize = new UDPPacketBuffer(new IPEndPoint(IPAddress.Loopback, 999)).Data.Length;
            Scene scene = new SceneHelpers().SetupScene();
            TestLLUDPServer udpServer = ClientStackHelpers.AddUdpServer(scene);

            ClientStackHelpers.AddChildClient(
                scene, udpServer, TestHelpers.ParseTail(0x1), TestHelpers.ParseTail(0x2), 123456);

            List<int> sizes = new();
            lock (OpenSimUDPBase.m_udpBuffersPoolLock)
            {
                for (int i = 0; i <= OpenSimUDPBase.m_udpBuffersPoolPtr; i++)
                    sizes.Add(OpenSimUDPBase.m_udpBuffersPool[i].Data.Length);
            }

            Assert.NotEmpty(sizes);
            Assert.All(sizes, size => Assert.Equal(standardSize, size));
        }
        finally
        {
            // never leave an odd sized buffer behind for the tests that run after this one
            EmptyPool();
            Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        }
    }
}
