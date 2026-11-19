/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Framework;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// Validation of a game-controller snapshot: canonical axes (six, range -1 to 1), mode-dependent semantic
    /// axes (count by mode, cursor pixel and normalised positions), and rejection of non-finite values.
    /// Pure value logic with no shared state, so these run in parallel with the other test classes.
    /// </summary>
    public class GameControlSnapshotTests
    {
        private static GameControlSnapshot Create(float[] raw, int mode, float[] semantic,
            uint rawButtons = 0, uint semanticButtons = 0)
        {
            bool ok = GameControlSnapshot.TryCreate(rawButtons, raw, mode, semanticButtons, semantic,
                out GameControlSnapshot snapshot);
            Assert.True(ok, "a valid snapshot was refused");
            Assert.NotNull(snapshot);
            return snapshot;
        }

        [Fact]
        public void KeepsButtonsModeAndInRangeAxes()
        {
            GameControlSnapshot s = Create(new[] { 0.5f, -0.25f, 1f, -1f, 0f, 0.75f }, GameControlSnapshot.ModeAvatar,
                new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f }, rawButtons: 0x801u, semanticButtons: 4u);

            Assert.Equal(0x801u, s.RawButtons);
            Assert.Equal(4u, s.SemanticButtons);
            Assert.Equal(GameControlSnapshot.ModeAvatar, s.Mode);
            Assert.Equal(new[] { 0.5f, -0.25f, 1f, -1f, 0f, 0.75f }, s.RawAxes);
            Assert.Equal(new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f }, s.SemanticAxes);
        }

        [Fact]
        public void RawAxesAreClampedToMinusOneToOne()
        {
            GameControlSnapshot s = Create(new[] { 3f, -9f, 1.0001f, -1.0001f, 0.5f, float.MaxValue },
                GameControlSnapshot.ModeAvatar, Array.Empty<float>());

            Assert.Equal(new[] { 1f, -1f, 1f, -1f, 0.5f, 1f }, s.RawAxes);
        }

        [Fact]
        public void RawAxesArePaddedWithZeroAndCutAtSix()
        {
            GameControlSnapshot shortList = Create(new[] { 0.5f, 0.5f }, GameControlSnapshot.ModeAvatar, Array.Empty<float>());
            Assert.Equal(new[] { 0.5f, 0.5f, 0f, 0f, 0f, 0f }, shortList.RawAxes);

            GameControlSnapshot longList = Create(new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f },
                GameControlSnapshot.ModeAvatar, Array.Empty<float>());
            Assert.Equal(new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f }, longList.RawAxes);
        }

        [Theory]
        [InlineData(GameControlSnapshot.ModeAvatar, 5)]
        [InlineData(GameControlSnapshot.ModeMouselook, 5)]
        [InlineData(GameControlSnapshot.ModeCaptive, 5)]
        [InlineData(GameControlSnapshot.ModeCursor, 6)]
        [InlineData(GameControlSnapshot.ModeFlycam, 7)]
        [InlineData(GameControlSnapshot.ModeUnknown, 0)]
        public void SemanticAxisCountFollowsTheMode(int mode, int expected)
        {
            Assert.Equal(expected, GameControlSnapshot.SemanticAxisCount(mode));

            GameControlSnapshot s = Create(new float[6], mode, new float[9]);
            Assert.Equal(expected, s.SemanticAxes.Length);
        }

        [Fact]
        public void SemanticAxesArePaddedWithZeroForTheMode()
        {
            GameControlSnapshot s = Create(new float[6], GameControlSnapshot.ModeFlycam, new[] { 0.5f });

            Assert.Equal(new[] { 0.5f, 0f, 0f, 0f, 0f, 0f, 0f }, s.SemanticAxes);
        }

        [Fact]
        public void SemanticAxesOfMovementModesAreClampedToMinusOneToOne()
        {
            GameControlSnapshot s = Create(new float[6], GameControlSnapshot.ModeAvatar,
                new[] { 2f, -2f, 0.25f, 100f, -100f });

            Assert.Equal(new[] { 1f, -1f, 0.25f, 1f, -1f }, s.SemanticAxes);
        }

        [Fact]
        public void CursorModeKeepsPixelPositionsAndNormalisesTheRest()
        {
            // CURSOR_DX, CURSOR_DY in [-1,1]; CURSOR_PX, CURSOR_PY are window pixels (not below 0);
            // CURSOR_NX, CURSOR_NY in [0,1].
            GameControlSnapshot s = Create(new float[6], GameControlSnapshot.ModeCursor,
                new[] { -3f, 0.5f, 1280f, -40f, 1.5f, -0.5f });

            Assert.Equal(new[] { -1f, 0.5f, 1280f, 0f, 1f, 0f }, s.SemanticAxes);
        }

        [Fact]
        public void UnknownModeDropsTheSemanticData()
        {
            GameControlSnapshot s = Create(new[] { 0.5f, 0f, 0f, 0f, 0f, 0f }, 9, new[] { 1f, 1f }, semanticButtons: 0xFFu);

            Assert.Equal(GameControlSnapshot.ModeUnknown, s.Mode);
            Assert.Empty(s.SemanticAxes);
            Assert.Equal(0u, s.SemanticButtons);
            Assert.Equal(0.5f, s.RawAxes[0]);
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void NonFiniteRawAxisIsRefused(float bad)
        {
            // positive control first: the same call with a finite value is accepted
            Create(new[] { 0f, 0f, 0f, 0f, 0f, 0.5f }, GameControlSnapshot.ModeAvatar, new float[5]);

            bool ok = GameControlSnapshot.TryCreate(0, new[] { 0f, 0f, 0f, 0f, 0f, bad }, GameControlSnapshot.ModeAvatar,
                0, new float[5], out GameControlSnapshot snapshot);

            Assert.False(ok);
            Assert.Null(snapshot);
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void NonFiniteSemanticAxisIsRefused(float bad)
        {
            Create(new float[6], GameControlSnapshot.ModeCursor, new[] { 0f, 0f, 10f, 10f, 0.5f, 0.5f });

            bool ok = GameControlSnapshot.TryCreate(0, new float[6], GameControlSnapshot.ModeCursor,
                0, new[] { 0f, 0f, 10f, 10f, bad, 0.5f }, out GameControlSnapshot snapshot);

            Assert.False(ok);
            Assert.Null(snapshot);
        }

        [Fact]
        public void NonFiniteBeyondTheKeptRangeIsStillRefused()
        {
            // A NaN in a slot that would be cut off must not slip through: the whole message is suspect.
            Create(new float[8], GameControlSnapshot.ModeAvatar, new float[5]);

            var raw = new float[8];
            raw[7] = float.NaN;
            bool ok = GameControlSnapshot.TryCreate(0, raw, GameControlSnapshot.ModeAvatar, 0, new float[5],
                out GameControlSnapshot snapshot);

            Assert.False(ok);
            Assert.Null(snapshot);
        }
    }
}
