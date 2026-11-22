/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Wind;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ScriptEngine.Shared.Tests
{
    /// <summary>
    /// osSetWeather and osGetWeather: setting needs the OSSL threat level High (or an Allow_osSetWeather entry), an owner
    /// who is the estate owner, an estate manager or a god, and [Weather] enabled; reading is threat level None and needs
    /// only [Weather] enabled. A refused call returns its code (or, for the threat level, raises the OSSL error) and
    /// changes nothing. Values outside the limits are refused, not clamped.
    /// </summary>
    // Each test builds its own scene and weather state. OSSL_Api keeps its settings (threat level, Allow_ entries) in
    // statics read once; each test resets them, as OSSL_ApiSetScriptEngineTests does. The assembly runs its tests one at
    // a time (LSL_ApiHttpTests.cs: DisableTestParallelization), so the reset cannot race another class.
    public class OSSL_ApiWeatherTests : OpenSimTestCase
    {
        private class WeatherWind : DispatchProxy
        {
            public IWeatherState Weather;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == "get_Weather") return Weather;
                var rt = targetMethod.ReturnType;
                return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
            }
        }

        private Scene m_scene;
        private MockScriptEngine m_engine;
        private UUID m_owner;
        private SceneObjectGroup m_group;
        private OSSL_Api m_api;
        private IWeatherState m_weather;
        private int m_raised;

        private static void ResetOsslSharedState()
        {
            Type api = typeof(OSSL_Api);
            void Set(string name, object value)
                => api.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(null, value);
            Set("m_doneSharedInit", false);
            Set("m_OSFunctionsEnabled", false);
            Set("m_PermissionErrortoOwner", false);
            Set("m_MaxThreatLevel", ThreatLevel.VeryLow);
            Set("m_osslconfig", null);
            ((System.Collections.IDictionary)api.GetField("m_FunctionPerms", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)).Clear();
        }

        public override void Dispose()
        {
            ResetOsslSharedState();
            base.Dispose();
        }

        private void Setup(bool weatherOn, bool manager, bool god = false, bool withState = true,
            string threatLevel = "Severe", string allowSetWeather = null)
        {
            ResetOsslSharedState();
            IConfigSource config = new IniConfigSource();
            config.AddConfig("XEngine").Set("Enabled", "true");
            IConfig ossl = config.AddConfig("OSSL");
            ossl.Set("DebuggerSafe", false);
            ossl.Set("AllowOSFunctions", "true");
            ossl.Set("OSFunctionThreatLevel", threatLevel);
            if (allowSetWeather != null) ossl.Set("Allow_osSetWeather", allowSetWeather);

            m_scene = new SceneHelpers().SetupScene();
            SceneHelpers.SetupSceneModules(m_scene, config);
            m_engine = new MockScriptEngine();
            m_engine.Initialise(config);
            m_engine.AddRegion(m_scene);

            m_owner = TestHelpers.ParseTail(0x1);
            m_group = SceneHelpers.CreateSceneObject(1, m_owner, 0x10);
            m_scene.AddSceneObject(m_group);
            m_scene.Permissions.OnIsAdministrator += id => god && id == m_owner;
            m_scene.Permissions.OnIssueEstateCommand += (user, ownerCommand) => manager && user == m_owner;

            var proxy = DispatchProxy.Create<IWindModule, WeatherWind>();
            var wind = (WeatherWind)(object)proxy;
            m_weather = wind.Weather = withState
                ? new WeatherState(weatherOn, new WeatherLimits { MinChangeIntervalSeconds = 0f })
                : null;
            if (m_weather != null) m_weather.WeatherChanged += _ => m_raised++;
            m_scene.RegisterModuleInterface<IWindModule>(proxy);

            // The script's item: the Allow_ entries are checked against its owner (OSSL_Api.CheckThreatLevelTest).
            var item = new TaskInventoryItem { ItemID = UUID.Random(), OwnerID = m_owner, Name = "weather script" };
            m_api = new OSSL_Api();
            m_api.Initialize(m_engine, m_group.RootPart, item);
        }

        // ---- setting: the gate ----

        [Fact]
        public void AnEstateManagersObjectSetsTheWeather()
        {
            TestHelpers.InMethod();
            Setup(weatherOn: true, manager: true);

            Assert.Equal(WeatherResult.Changed, (int)m_api.osSetWeather("storm", 0.75, 0.0));

            Assert.Equal("storm", m_weather.Current.State);
            Assert.Equal(0.75f, m_weather.Current.TargetIntensity);
            Assert.Equal(m_group.UUID, m_weather.Current.SetByObject);
            Assert.Equal(m_owner, m_weather.Current.SetByOwner);
            Assert.Equal(1, m_raised);
        }

        [Fact]
        public void AGodsObjectSetsTheWeather()
        {
            TestHelpers.InMethod();
            Setup(weatherOn: true, manager: false, god: true);
            Assert.Equal(WeatherResult.Changed, (int)m_api.osSetWeather("storm", 0.5, 0.0));
        }

        [Fact]
        public void AnyoneElseIsRefusedAndNothingChangesOrIsRaised()
        {
            TestHelpers.InMethod();
            Setup(weatherOn: true, manager: false);

            Assert.Equal(WeatherResult.NotAllowed, (int)m_api.osSetWeather("storm", 0.5, 0.0));
            Assert.Equal(WeatherResult.NotAllowed, (int)m_api.osSetWeather(new string('a', 99), 7.0, -1.0));
            Assert.Same(WeatherSnapshot.Initial, m_weather.Current);
            Assert.Equal(0, m_raised);
        }

        [Fact]
        public void BelowThreatLevelHighWithNoAllowEntryTheSetRaisesTheOsslErrorAndChangesNothing()
        {
            TestHelpers.InMethod();
            Setup(weatherOn: true, manager: true, threatLevel: "Moderate");

            Assert.ThrowsAny<Exception>(() => m_api.osSetWeather("storm", 0.5, 0.0));
            Assert.Same(WeatherSnapshot.Initial, m_weather.Current);
            Assert.Equal(0, m_raised);
        }

        [Fact]
        public void TheShippedAllowEntryLetsAGodThroughBelowHigh()
        {
            TestHelpers.InMethod();
            // The entry as osslDefaultEnable.ini ships it: a god who is not on the estate may set the weather, as in Phlox.
            Setup(weatherOn: true, manager: false, god: true, threatLevel: "VeryLow", allowSetWeather: ShippedAllowSetWeather());
            Assert.Equal(WeatherResult.Changed, (int)m_api.osSetWeather("storm", 0.5, 0.0));
        }

        // The Allow_osSetWeather line of the shipped osslDefaultEnable.ini, read from the source tree.
        private static string ShippedAllowSetWeather()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Tranquillity.sln")))
                dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            string ini = Path.Combine(dir, "Source", "OpenSim.Server.RegionServer", "AppData", "config-include", "osslDefaultEnable.ini");
            string line = File.ReadLines(ini).Single(l => l.TrimStart().StartsWith("Allow_osSetWeather", StringComparison.Ordinal));
            return line.Substring(line.IndexOf('=') + 1).Trim();
        }

        [Fact]
        public void TheShippedAllowEntryRefusesAnOwnerOutsideTheEstateBelowHigh()
        {
            TestHelpers.InMethod();
            Setup(weatherOn: true, manager: false, threatLevel: "VeryLow", allowSetWeather: ShippedAllowSetWeather());
            Assert.ThrowsAny<Exception>(() => m_api.osSetWeather("storm", 0.5, 0.0));
            Assert.Same(WeatherSnapshot.Initial, m_weather.Current);
        }

        [Fact]
        public void TheShippedAllowEntryLetsAnEstateManagerThroughBelowHigh()
        {
            TestHelpers.InMethod();
            // The entry as osslDefaultEnable.ini ships it.
            Setup(weatherOn: true, manager: true, threatLevel: "VeryLow", allowSetWeather: ShippedAllowSetWeather());
            m_scene.RegionInfo.EstateSettings.EstateOwner = m_owner;
            Assert.Equal(WeatherResult.Changed, (int)m_api.osSetWeather("storm", 0.5, 0.0));
        }

        // ---- setting: values, repeats ----

        [Theory]
        [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0.5, 0.0)]   // 33 bytes
        [InlineData("storm", 1.5, 0.0)]
        [InlineData("storm", -0.5, 0.0)]
        [InlineData("storm", double.NaN, 0.0)]
        [InlineData("storm", 0.5, -2.0)]
        [InlineData("storm", 0.5, 4000.0)]
        public void AValueOutsideTheLimitsIsRefusedNotClamped(string name, double intensity, double transition)
        {
            TestHelpers.InMethod();
            Setup(weatherOn: true, manager: true);
            Assert.Equal(WeatherResult.Invalid, (int)m_api.osSetWeather(name, intensity, transition));
            Assert.Same(WeatherSnapshot.Initial, m_weather.Current);
        }

        [Fact]
        public void ADoubleThatDoesNotFitAFloatIsRefusedNotTurnedIntoInfinity()
        {
            TestHelpers.InMethod();
            Setup(weatherOn: true, manager: true);
            Assert.Equal(WeatherResult.Invalid, (int)m_api.osSetWeather("storm", 0.5, 1e300));
        }

        [Fact]
        public void SettingTheSameWeatherAgainIsUnchanged()
        {
            TestHelpers.InMethod();
            Setup(weatherOn: true, manager: true);
            Assert.Equal(WeatherResult.Changed, (int)m_api.osSetWeather("storm", 0.5, 0.0));
            Assert.Equal(WeatherResult.Unchanged, (int)m_api.osSetWeather("storm", 0.5, 0.0));
            Assert.Equal(1, m_raised);
        }

        // ---- weather off ----

        [Fact]
        public void WithWeatherOffSettingIsDisabledAndReadingIsEmpty()
        {
            TestHelpers.InMethod();
            Setup(weatherOn: false, manager: true);
            Assert.Equal(WeatherResult.Disabled, (int)m_api.osSetWeather("storm", 0.5, 0.0));
            Assert.Equal(0, m_api.osGetWeather().Length);
            Assert.Equal(0, m_raised);
        }

        [Fact]
        public void WithNoWeatherStateSettingIsDisabledAndReadingIsEmpty()
        {
            TestHelpers.InMethod();
            Setup(weatherOn: true, manager: true, withState: false);
            Assert.Equal(WeatherResult.Disabled, (int)m_api.osSetWeather("storm", 0.5, 0.0));
            Assert.Equal(0, m_api.osGetWeather().Length);
        }

        // ---- reading ----

        [Fact]
        public void AnyScriptReadsStateIntensitySinceAndSerialAtThreatLevelNone()
        {
            TestHelpers.InMethod();
            Setup(weatherOn: true, manager: false, threatLevel: "None");
            m_weather.Set("tornado-warning", 0.4f, 0f, UUID.Random(), UUID.Random());

            LSL_Types.list got = m_api.osGetWeather();

            Assert.Equal(4, got.Length);
            Assert.Equal("tornado-warning", got.GetStringItem(0));
            Assert.Equal(0.4, got.GetLSLFloatItem(1).value, 4);
            Assert.Equal((int)m_weather.Current.SinceUnix, got.GetLSLIntegerItem(2).value);
            Assert.Equal(1, got.GetLSLIntegerItem(3).value);
        }

        // ---- what scripts compile against ----

        [Fact]
        public void TheConstantsMatchTheCoreAndPhlox()
        {
            Assert.Equal(WeatherResult.Changed, ScriptBaseClass.WEATHER_SET);
            Assert.Equal(WeatherResult.Unchanged, ScriptBaseClass.WEATHER_UNCHANGED);
            Assert.Equal(WeatherResult.Disabled, ScriptBaseClass.WEATHER_ERR_DISABLED);
            Assert.Equal(WeatherResult.NotAllowed, ScriptBaseClass.WEATHER_ERR_NOT_ALLOWED);
            Assert.Equal(WeatherResult.Invalid, ScriptBaseClass.WEATHER_ERR_INVALID);
            Assert.Equal(WeatherResult.Throttled, ScriptBaseClass.WEATHER_ERR_THROTTLED);
        }
    }
}
