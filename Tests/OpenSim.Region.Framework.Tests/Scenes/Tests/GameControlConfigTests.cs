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
    /// [GameControl] Enabled is grid-wide and off by default. A region can only opt out (GameControlEnabled = false
    /// in its own settings); it cannot opt in when the grid-wide switch is off.
    /// Builds its own config sources and region objects, no shared state, so it runs in parallel.
    /// </summary>
    public class GameControlConfigTests
    {
        private static IConfigSource Source(string enabled)
        {
            IniConfigSource source = new();
            if (enabled != null)
            {
                IConfig section = source.AddConfig("GameControl");
                section.Set("Enabled", enabled);
            }
            return source;
        }

        private static RegionInfo Region(string gameControlEnabled)
        {
            RegionInfo region = new();
            if (gameControlEnabled != null)
                region.SetExtraSetting("GameControlEnabled", gameControlEnabled);
            return region;
        }

        [Fact]
        public void OffByDefaultWithNoSection()
        {
            Assert.True(GameControlConfig.IsEnabled(Source("true"), Region(null)), "control case: enabled grid-wide");
            Assert.False(GameControlConfig.IsEnabled(new IniConfigSource(), Region(null)));
        }

        [Fact]
        public void OffByDefaultWhenTheKeyIsMissing()
        {
            IniConfigSource source = new();
            source.AddConfig("GameControl");

            Assert.True(GameControlConfig.IsEnabled(Source("true"), Region(null)), "control case: enabled grid-wide");
            Assert.False(GameControlConfig.IsEnabled(source, Region(null)));
        }

        [Fact]
        public void EnabledGridWideTurnsItOnForARegionWithNoSetting()
        {
            Assert.True(GameControlConfig.IsEnabled(Source("true"), Region(null)));
        }

        [Fact]
        public void ExplicitFalseKeepsItOff()
        {
            Assert.True(GameControlConfig.IsEnabled(Source("true"), Region(null)), "control case: enabled grid-wide");
            Assert.False(GameControlConfig.IsEnabled(Source("false"), Region(null)));
        }

        [Fact]
        public void ARegionCanOptOut()
        {
            Assert.True(GameControlConfig.IsEnabled(Source("true"), Region(null)), "control case: enabled grid-wide");
            Assert.False(GameControlConfig.IsEnabled(Source("true"), Region("false")));
            Assert.False(GameControlConfig.IsEnabled(Source("true"), Region("False")));
        }

        [Fact]
        public void ARegionCannotOptIn()
        {
            Assert.True(GameControlConfig.IsEnabled(Source("true"), Region("true")), "control case: both on");
            Assert.False(GameControlConfig.IsEnabled(Source("false"), Region("true")));
            Assert.False(GameControlConfig.IsEnabled(new IniConfigSource(), Region("true")));
        }

        [Fact]
        public void ARegionSettingThatIsNotAFalseValueDoesNotOptOut()
        {
            Assert.True(GameControlConfig.IsEnabled(Source("true"), Region("maybe")));
            Assert.True(GameControlConfig.IsEnabled(Source("true"), Region("")));
        }

        [Fact]
        public void ANullRegionOrSourceIsOff()
        {
            Assert.True(GameControlConfig.IsEnabled(Source("true"), null), "control case: no region means grid-wide only");
            Assert.False(GameControlConfig.IsEnabled(null, Region(null)));
        }
    }
}
