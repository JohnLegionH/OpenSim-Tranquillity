/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse.Packets;
using OpenSim.Framework;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// Decoding and filtering of the viewer's GameControlData message: canonical and mode axes and buttons, list
    /// length caps, out-of-order and resent messages, and the per-agent event-rate cap. The rate cap reads an
    /// injected clock, so no test waits on real time. Each test builds its own filter, no shared state, so the
    /// class runs in parallel.
    /// </summary>
    public class GameControlFilterTests
    {
        private sealed class Clock
        {
            public long Now = 1_000_000;
            public long Read() => Now;
        }

        private static GameControlDataPacket Packet(byte sequence, byte mode, short[] axes = null, uint? buttons = null,
            short[] modeAxes = null, uint? modeButtons = null)
        {
            var p = new GameControlDataPacket();
            p.AgentData.AgentID = UUID.Random();
            p.AgentData.SessionID = UUID.Random();
            p.AgentData.Packet = sequence;
            p.AgentData.ActionMode = mode;
            p.Axes = (axes ?? Array.Empty<short>()).Select(v => new GameControlDataPacket.AxesBlock { Value = v }).ToArray();
            p.Buttons = buttons.HasValue
                ? new[] { new GameControlDataPacket.ButtonsBlock { Flags = buttons.Value } }
                : Array.Empty<GameControlDataPacket.ButtonsBlock>();
            p.ModeAxes = (modeAxes ?? Array.Empty<short>()).Select(v => new GameControlDataPacket.ModeAxesBlock { Value = v }).ToArray();
            p.ModeButtons = modeButtons.HasValue
                ? new[] { new GameControlDataPacket.ModeButtonsBlock { Flags = modeButtons.Value } }
                : Array.Empty<GameControlDataPacket.ModeButtonsBlock>();
            return p;
        }

        private static GameControlSnapshot Accept(GameControlFilter filter, GameControlDataPacket packet)
        {
            GameControlSnapshot s = filter.Process(packet);
            Assert.NotNull(s);
            return s;
        }

        [Fact]
        public void DecodesCanonicalAxesButtonsAndMode()
        {
            var filter = new GameControlFilter(new Clock().Read);

            GameControlSnapshot s = Accept(filter, Packet(1, 0,
                axes: new short[] { 32767, -32768, 0, 16384, 32767, 0 }, buttons: 0x80000801u,
                modeAxes: new short[] { 0, 32767, 0, 0, -32768 }, modeButtons: 0x4u));

            Assert.Equal(0x80000801u, s.RawButtons);
            Assert.Equal(GameControlSnapshot.ModeAvatar, s.Mode);
            Assert.Equal(6, s.RawAxes.Length);
            Assert.Equal(1f, s.RawAxes[0]);
            Assert.Equal(-1f, s.RawAxes[1]);                       // -32768 clamps to -1
            Assert.Equal(0f, s.RawAxes[2]);
            Assert.Equal(16384f / 32767f, s.RawAxes[3], 5);
            Assert.Equal(1f, s.RawAxes[4]);
            Assert.Equal(new[] { 0f, 1f, 0f, 0f, -1f }, s.SemanticAxes);
            Assert.Equal(4u, s.SemanticButtons);
        }

        [Fact]
        public void BlocksTheViewerLeftOutDecodeAsZero()
        {
            var filter = new GameControlFilter(new Clock().Read);

            GameControlSnapshot s = Accept(filter, Packet(1, 0, axes: new short[] { 32767 }));

            Assert.Equal(new[] { 1f, 0f, 0f, 0f, 0f, 0f }, s.RawAxes);
            Assert.Equal(0u, s.RawButtons);
            Assert.Equal(new float[5], s.SemanticAxes);
            Assert.Equal(0u, s.SemanticButtons);
        }

        [Fact]
        public void ListsLongerThanTheMessageAllowsAreCut()
        {
            var filter = new GameControlFilter(new Clock().Read);
            GameControlDataPacket p = Packet(1, 0,
                axes: new short[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 },
                modeAxes: new short[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
            p.Buttons = new[]
            {
                new GameControlDataPacket.ButtonsBlock { Flags = 0x10u },
                new GameControlDataPacket.ButtonsBlock { Flags = 0xFFFFFFFFu },
            };

            GameControlSnapshot s = Accept(filter, p);

            Assert.Equal(6, s.RawAxes.Length);
            Assert.Equal(5, s.SemanticAxes.Length);
            Assert.Equal(0x10u, s.RawButtons);                     // first Buttons block only
        }

        [Fact]
        public void ModeAxisCountFollowsTheMode()
        {
            var filter = new GameControlFilter(new Clock().Read);
            short[] ten = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

            Assert.Equal(7, Accept(filter, Packet(1, 2, modeAxes: ten)).SemanticAxes.Length);   // flycam
            Assert.Equal(6, Accept(filter, Packet(2, 4, modeAxes: ten)).SemanticAxes.Length);   // cursor
            Assert.Equal(5, Accept(filter, Packet(3, 3, modeAxes: ten)).SemanticAxes.Length);   // captive
        }

        [Fact]
        public void CursorPositionsDecodeAsPixelsAndFractions()
        {
            var filter = new GameControlFilter(new Clock().Read);

            GameControlSnapshot s = Accept(filter, Packet(1, 4,
                modeAxes: new short[] { 0, 0, 1280, 720, 32767, 16384 }));

            Assert.Equal(GameControlSnapshot.ModeCursor, s.Mode);
            Assert.Equal(1280f, s.SemanticAxes[2]);
            Assert.Equal(720f, s.SemanticAxes[3]);
            Assert.Equal(1f, s.SemanticAxes[4]);
            Assert.Equal(16384f / 32767f, s.SemanticAxes[5], 5);
        }

        [Fact]
        public void UnknownModeKeepsCanonicalDataAndDropsTheRest()
        {
            var filter = new GameControlFilter(new Clock().Read);

            GameControlSnapshot s = Accept(filter, Packet(1, 9, axes: new short[] { 32767 }, buttons: 1,
                modeAxes: new short[] { 32767, 32767 }, modeButtons: 0xFFu));

            Assert.Equal(GameControlSnapshot.ModeUnknown, s.Mode);
            Assert.Equal(1f, s.RawAxes[0]);
            Assert.Equal(1u, s.RawButtons);
            Assert.Empty(s.SemanticAxes);
            Assert.Equal(0u, s.SemanticButtons);
        }

        [Fact]
        public void NullPacketAndNullBlocksAreHandled()
        {
            var filter = new GameControlFilter(new Clock().Read);
            Accept(filter, Packet(1, 0, axes: new short[] { 5 }));       // control: the filter works

            Assert.Null(filter.Process(null));

            GameControlDataPacket p = Packet(2, 0);
            p.Axes = null;
            p.Buttons = null;
            p.ModeAxes = null;
            p.ModeButtons = null;
            GameControlSnapshot s = Accept(filter, p);
            Assert.Equal(new float[6], s.RawAxes);
        }

        [Fact]
        public void AResendThatCarriesNothingNewIsIgnored()
        {
            var filter = new GameControlFilter(new Clock().Read);
            short[] axes = { 100, 200 };

            Accept(filter, Packet(1, 0, axes: axes, buttons: 3));
            Assert.Null(filter.Process(Packet(2, 0, axes: axes, buttons: 3)));
            Assert.Null(filter.Process(Packet(3, 0, axes: axes, buttons: 3)));

            // a real change after the resends comes through
            GameControlSnapshot changed = Accept(filter, Packet(4, 0, axes: axes, buttons: 7));
            Assert.Equal(7u, changed.RawButtons);
            Assert.Null(filter.Process(Packet(5, 0, axes: axes, buttons: 7)));
        }

        [Fact]
        public void AChangeOfModeAloneIsNew()
        {
            var filter = new GameControlFilter(new Clock().Read);
            Accept(filter, Packet(1, 0));

            GameControlSnapshot s = Accept(filter, Packet(2, 1));

            Assert.Equal(GameControlSnapshot.ModeMouselook, s.Mode);
        }

        [Fact]
        public void LateAndRepeatedSequenceNumbersAreIgnored()
        {
            var filter = new GameControlFilter(new Clock().Read);

            Accept(filter, Packet(10, 0, axes: new short[] { 1 }));
            Assert.Null(filter.Process(Packet(9, 0, axes: new short[] { 2 })));    // arrived late
            Assert.Null(filter.Process(Packet(10, 0, axes: new short[] { 3 })));   // same number again
            GameControlSnapshot s = Accept(filter, Packet(11, 0, axes: new short[] { 4 }));
            Assert.Equal(4f / 32767f, s.RawAxes[0], 6);
        }

        [Fact]
        public void TheSequenceNumberWrapsAtTwoFiftySix()
        {
            var filter = new GameControlFilter(new Clock().Read);

            Accept(filter, Packet(254, 0, axes: new short[] { 1 }));
            Accept(filter, Packet(255, 0, axes: new short[] { 2 }));
            Accept(filter, Packet(0, 0, axes: new short[] { 3 }));
            Accept(filter, Packet(1, 0, axes: new short[] { 4 }));
            Assert.Null(filter.Process(Packet(255, 0, axes: new short[] { 9 })));   // now 2 behind
        }

        [Fact]
        public void AtMostSixtyChangesPerSecondAreRaised()
        {
            var clock = new Clock();
            var filter = new GameControlFilter(clock.Read);

            for (int i = 0; i < GameControlFilter.MaxEventsPerSecond; i++)
                Accept(filter, Packet((byte)i, 0, axes: new short[] { (short)(i + 1) }));

            Assert.Null(filter.Process(Packet((byte)GameControlFilter.MaxEventsPerSecond, 0, axes: new short[] { 1000 })));

            clock.Now += 999;
            Assert.Null(filter.Process(Packet((byte)(GameControlFilter.MaxEventsPerSecond + 1), 0, axes: new short[] { 1001 })));

            clock.Now += 1;     // a full second since the window began
            Accept(filter, Packet((byte)(GameControlFilter.MaxEventsPerSecond + 2), 0, axes: new short[] { 1002 }));
        }

        [Fact]
        public void AChangeRefusedByTheRateCapIsNotMistakenForAResend()
        {
            var clock = new Clock();
            var filter = new GameControlFilter(clock.Read);
            for (int i = 0; i < GameControlFilter.MaxEventsPerSecond; i++)
                Accept(filter, Packet((byte)i, 0, axes: new short[] { (short)(i + 1) }));

            byte next = (byte)GameControlFilter.MaxEventsPerSecond;
            Assert.Null(filter.Process(Packet(next, 0, axes: new short[] { 5000 })));      // refused by the cap

            clock.Now += 1000;
            GameControlSnapshot s = Accept(filter, Packet((byte)(next + 1), 0, axes: new short[] { 5000 }));
            Assert.Equal(5000f / 32767f, s.RawAxes[0], 6);
        }

        [Fact]
        public void IgnoredResendsDoNotUseUpTheRateCap()
        {
            var clock = new Clock();
            var filter = new GameControlFilter(clock.Read);

            Accept(filter, Packet(0, 0, axes: new short[] { 1 }));
            for (int i = 1; i <= 200; i++)
                Assert.Null(filter.Process(Packet((byte)i, 0, axes: new short[] { 1 })));

            // 200 identical resends later, a change still comes through inside the same second
            Accept(filter, Packet(201, 0, axes: new short[] { 2 }));
        }

        [Fact]
        public void EachFilterKeepsItsOwnCount()
        {
            var clock = new Clock();
            var a = new GameControlFilter(clock.Read);
            var b = new GameControlFilter(clock.Read);
            for (int i = 0; i < GameControlFilter.MaxEventsPerSecond; i++)
                Accept(a, Packet((byte)i, 0, axes: new short[] { (short)(i + 1) }));
            Assert.Null(a.Process(Packet(200, 0, axes: new short[] { 4000 })));

            Accept(b, Packet(0, 0, axes: new short[] { 4000 }));
        }
    }
}
