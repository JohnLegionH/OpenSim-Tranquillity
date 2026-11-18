/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Net;
using OpenMetaverse;

namespace OpenSim.Region.ClientStack.LindenUDP.Tests;

/// <summary>
/// <see cref="OpenSimUDPBase.FreeUDPBuffer"/> returns a buffer to one static pool, and several senders write into a
/// pooled buffer through a raw pointer assuming the standard size. The pool therefore has to take standard size
/// buffers only; a buffer of any other size is left to the garbage collector.
/// The pool is process-wide, which is safe here because this test assembly runs its tests one at a time.
/// </summary>
public class FreeUdpBufferTests
{
    private static readonly IPEndPoint Peer = new(IPAddress.Loopback, 9000);

    private static int StandardSize => new UDPPacketBuffer(Peer).Data.Length;

    private static void EmptyPool()
    {
        lock (OpenSimUDPBase.m_udpBuffersPoolLock)
        {
            Array.Clear(OpenSimUDPBase.m_udpBuffersPool);
            OpenSimUDPBase.m_udpBuffersPoolPtr = -1;
        }
    }

    private static int PooledCount()
    {
        lock (OpenSimUDPBase.m_udpBuffersPoolLock)
            return OpenSimUDPBase.m_udpBuffersPoolPtr + 1;
    }

    private static UDPPacketBuffer InUse(UDPPacketBuffer buffer)
    {
        buffer.DataLength = 10; // a buffer that has carried a packet; a negative length marks one already freed
        return buffer;
    }

    [Fact]
    public void A_standard_size_buffer_is_pooled_and_handed_out_again()
    {
        EmptyPool();
        try
        {
            UDPPacketBuffer buffer = InUse(new UDPPacketBuffer(Peer));

            OpenSimUDPBase.FreeUDPBuffer(buffer);

            Assert.Equal(1, PooledCount());
            UDPPacketBuffer again = OpenSimUDPBase.GetNewUDPBuffer(Peer);
            Assert.Same(buffer, again);
            Assert.Equal(0, again.DataLength);
            Assert.Equal(0, PooledCount());
        }
        finally
        {
            EmptyPool();
        }
    }

    [Fact]
    public void A_buffer_shorter_than_standard_is_left_out_of_the_pool()
    {
        EmptyPool();
        try
        {
            UDPPacketBuffer shortBuffer = InUse(new UDPPacketBuffer(Peer, 46));
            Assert.Equal(46, shortBuffer.Data.Length);

            OpenSimUDPBase.FreeUDPBuffer(shortBuffer);

            Assert.Equal(0, PooledCount());
            Assert.Equal(StandardSize, OpenSimUDPBase.GetNewUDPBuffer(Peer).Data.Length);
        }
        finally
        {
            EmptyPool();
        }
    }

    [Fact]
    public void A_buffer_longer_than_standard_is_left_out_of_the_pool()
    {
        EmptyPool();
        try
        {
            UDPPacketBuffer longBuffer = InUse(new UDPPacketBuffer(Peer, StandardSize * 2));

            OpenSimUDPBase.FreeUDPBuffer(longBuffer);

            Assert.Equal(0, PooledCount());
        }
        finally
        {
            EmptyPool();
        }
    }

    [Fact]
    public void Only_the_standard_size_buffer_of_a_mixed_pair_is_pooled()
    {
        EmptyPool();
        try
        {
            UDPPacketBuffer odd = InUse(new UDPPacketBuffer(Peer, 46));
            UDPPacketBuffer standard = InUse(new UDPPacketBuffer(Peer));

            OpenSimUDPBase.FreeUDPBuffer(odd);
            OpenSimUDPBase.FreeUDPBuffer(standard);

            Assert.Equal(1, PooledCount());
            Assert.Same(standard, OpenSimUDPBase.GetNewUDPBuffer(Peer));
        }
        finally
        {
            EmptyPool();
        }
    }

    [Fact]
    public void Freeing_a_standard_buffer_twice_pools_it_once()
    {
        EmptyPool();
        try
        {
            UDPPacketBuffer buffer = InUse(new UDPPacketBuffer(Peer));

            OpenSimUDPBase.FreeUDPBuffer(buffer);
            OpenSimUDPBase.FreeUDPBuffer(buffer);

            Assert.Equal(1, PooledCount());
        }
        finally
        {
            EmptyPool();
        }
    }

    [Fact]
    public void A_full_pool_takes_no_more_buffers()
    {
        EmptyPool();
        try
        {
            for (int i = 0; i < 1005; i++)
                OpenSimUDPBase.FreeUDPBuffer(InUse(new UDPPacketBuffer(Peer)));

            Assert.Equal(1000, PooledCount());
        }
        finally
        {
            EmptyPool();
        }
    }
}
