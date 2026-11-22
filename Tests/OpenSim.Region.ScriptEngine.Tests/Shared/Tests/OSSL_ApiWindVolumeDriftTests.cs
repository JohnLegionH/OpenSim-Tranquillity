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
    /// osWindVolumeCreate with WV_DRIFT: an estate owner's, estate manager's or god's script makes a volume that moves;
    /// a drift above the cap and a create or remove past the per-object throttle return their error codes; anyone else
    /// is refused before the store is reached. YEngine's constants carry the region's values.
    /// </summary>
    // OSSL_Api keeps its settings in statics read once; each test resets them before and after, as
    // OSSL_ApiSetScriptEngineTests does. The assembly runs its tests one at a time (LSL_ApiHttpTests.cs:
    // DisableTestParallelization), so the reset cannot race another class.
    public class OSSL_ApiWindVolumeDriftTests : OpenSimTestCase
    {
        private class StoreWind : DispatchProxy
        {
            public IWindVolumeStore? Store;

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod!.Name == "get_WindVolumes") return Store;
                var rt = targetMethod.ReturnType;
                return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
            }
        }

        private Scene m_scene = null!;
        private MockScriptEngine m_engine = null!;
        private UUID m_owner;
        private SceneObjectGroup m_group = null!;
        private OSSL_Api m_api = null!;
        private WindVolumeStore m_store = null!;
        private long m_clockMs = 1000;

        private static void ResetOsslSharedState()
        {
            Type api = typeof(OSSL_Api);
            void Set(string name, object? value)
                => api.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(null, value);
            Set("m_doneSharedInit", false);
            Set("m_OSFunctionsEnabled", false);
            Set("m_PermissionErrortoOwner", false);
            Set("m_MaxThreatLevel", ThreatLevel.VeryLow);
            Set("m_osslconfig", null);
            ((System.Collections.IDictionary)api.GetField("m_FunctionPerms", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Clear();
        }

        public override void Dispose()
        {
            ResetOsslSharedState();
            base.Dispose();
        }

        private void Setup(bool manager, bool god = false, WindVolumeLimits? limits = null)
        {
            ResetOsslSharedState();
            IConfigSource config = new IniConfigSource();
            config.AddConfig("XEngine").Set("Enabled", "true");
            IConfig ossl = config.AddConfig("OSSL");
            ossl.Set("DebuggerSafe", false);
            ossl.Set("AllowOSFunctions", "true");
            ossl.Set("OSFunctionThreatLevel", "Severe");

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

            var proxy = DispatchProxy.Create<IWindModule, StoreWind>();
            m_store = new WindVolumeStore(true, limits, () => m_clockMs);
            ((StoreWind)(object)proxy).Store = m_store;
            m_scene.RegisterModuleInterface<IWindModule>(proxy);

            var item = new TaskInventoryItem { ItemID = UUID.Random(), OwnerID = m_owner, Name = "wind volume script" };
            m_api = new OSSL_Api();
            m_api.Initialize(m_engine, m_group.RootPart, item);
        }

        private static LSL_Types.list Drifting(LSL_Types.Vector3 drift)
            => new LSL_Types.list(
                (LSL_Types.LSLInteger)WindVolumeRules.WV_CENTER, new LSL_Types.Vector3(60, 128, 60),
                (LSL_Types.LSLInteger)WindVolumeRules.WV_RADIUS, new LSL_Types.LSLFloat(10),
                (LSL_Types.LSLInteger)WindVolumeRules.WV_FALLOFF, new LSL_Types.LSLFloat(1),
                (LSL_Types.LSLInteger)WindVolumeRules.WV_VELOCITY, new LSL_Types.Vector3(5, 0, 2),
                (LSL_Types.LSLInteger)WindVolumeRules.WV_LIFETIME, new LSL_Types.LSLFloat(120),
                (LSL_Types.LSLInteger)WindVolumeRules.WV_DRIFT, drift);

        [Fact]
        public void AnEstateManagersScriptMakesAVolumeThatDrifts()
        {
            TestHelpers.InMethod();
            Setup(manager: true);

            int handle = m_api.osWindVolumeCreate(Drifting(new LSL_Types.Vector3(4, 0, 0)));
            Assert.True(handle >= 1, $"got {handle}");

            WindVolume v = Assert.Single(m_store.List(UUID.Zero));
            Assert.Equal(new Vector3(4, 0, 0), v.Drift);
            m_clockMs += 10_000;
            Assert.Equal(new Vector3(5, 0, 2), m_store.Sample(new Vector3(100, 128, 60)));
            Assert.Equal(Vector3.Zero, m_store.Sample(new Vector3(60, 128, 60)));
        }

        [Fact]
        public void AGodsScriptMakesAVolumeThatDrifts()
        {
            TestHelpers.InMethod();
            Setup(manager: false, god: true);
            Assert.True(m_api.osWindVolumeCreate(Drifting(new LSL_Types.Vector3(0, 3, 0))) >= 1);
            Assert.Equal(new Vector3(0, 3, 0), Assert.Single(m_store.List(UUID.Zero)).Drift);
        }

        [Fact]
        public void AScriptOutsideTheEstateIsRefusedAndItsCallsNeverReachTheThrottle()
        {
            TestHelpers.InMethod();
            Setup(manager: false, limits: new WindVolumeLimits { CreateThrottlePerMinute = 2 });

            for (int i = 0; i < 20; i++)
                Assert.Equal(WindVolumeError.NotAllowed, (int)m_api.osWindVolumeCreate(Drifting(new LSL_Types.Vector3(4, 0, 0))));
            Assert.Equal(0, m_store.Count);

            var spec = new WindVolumeSpec
            {
                Shape = WindVolumeShape.Sphere, Center = new Vector3(60, 128, 60), Radius = 10f, FalloffStart = 1f,
                LifetimeSeconds = 120f, Velocity = new Vector3(5, 0, 2),
            };
            int drifting = m_store.Create(m_group.UUID, m_owner, spec with { Drift = new Vector3(4, 0, 0) });
            Assert.True(drifting >= 1);
            Assert.True(m_store.Create(m_group.UUID, m_owner, spec) >= 1);

            // Nor can it remove a drifting volume, even one its own object made.
            Assert.Equal(WindVolumeError.NotAllowed, (int)m_api.osWindVolumeRemove(drifting));
            Assert.Equal(2, m_store.Count);
        }

        [Fact]
        public void ADriftAboveTheCapIsRefusedWithItsOwnCode()
        {
            TestHelpers.InMethod();
            Setup(manager: true);
            Assert.Equal(WindVolumeError.DriftTooFast, (int)m_api.osWindVolumeCreate(Drifting(new LSL_Types.Vector3(40.5, 0, 0))));
            Assert.Equal(0, m_store.Count);
        }

        [Fact]
        public void AFloodOfCreatesAndRemovesIsThrottled()
        {
            TestHelpers.InMethod();
            Setup(manager: true);

            for (int i = 0; i < 5; i++)
            {
                int handle = m_api.osWindVolumeCreate(Drifting(new LSL_Types.Vector3(0, 0, 0)));
                Assert.True(handle >= 1, $"create {i}: {handle}");
                Assert.Equal(WindVolumeError.Ok, (int)m_api.osWindVolumeRemove(handle));
            }
            Assert.Equal(WindVolumeError.Throttled, (int)m_api.osWindVolumeCreate(Drifting(new LSL_Types.Vector3(0, 0, 0))));
            Assert.Equal(0, m_store.Count);

            m_clockMs += 60_000;
            Assert.True(m_api.osWindVolumeCreate(Drifting(new LSL_Types.Vector3(0, 0, 0))) >= 1);
        }

        [Theory]
        [InlineData("WV_DRIFT", WindVolumeRules.WV_DRIFT)]
        [InlineData("WV_ERR_DRIFT_TOO_FAST", WindVolumeError.DriftTooFast)]
        [InlineData("WV_ERR_THROTTLED", WindVolumeError.Throttled)]
        public void YEnginesConstantsMatchTheRegionsValues(string name, int value)
        {
            FieldInfo? field = typeof(ScriptBaseClass).GetField(name, BindingFlags.Public | BindingFlags.Static);
            Assert.True(field != null, $"{name} is not a YEngine constant");
            Assert.Equal(value, (int)field!.GetValue(null)!);
        }
    }
}
