/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using InWorldz.Phlox.Compiler;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using OpenSim.Region.CoreModules.World.Wind;
using OpenSim.Region.Framework.Interfaces;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// iwWindVolumeCreate with WV_DRIFT: an estate owner's, estate manager's or god's script makes a volume that moves;
/// a drift above the cap and a create or remove past the per-object throttle come back to the script as their error
/// codes; anyone else is refused before the store is reached, so a refused call never counts against the throttle.
/// </summary>
// Calls the API directly on the harness prim with a store of its own and a test clock; no process-wide state, so the
// class runs in parallel.
public class WindVolumeDriftFunctionTests
{
    private static LSLSystemAPI Api(SchedulerHarness h) => new LSLSystemAPI(h.Engine, h.Prim, h.Prim.LocalId, UUID.Random());

    private class StoreWind : DispatchProxy
    {
        public IWindVolumeStore Store;

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod.Name == "get_WindVolumes") return Store;
            var rt = targetMethod.ReturnType;
            return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
        }
    }

    private long m_clockMs = 1000;

    private WindVolumeStore Wind(SchedulerHarness h, WindVolumeLimits limits = null)
    {
        var proxy = DispatchProxy.Create<IWindModule, StoreWind>();
        var store = new WindVolumeStore(true, limits, () => m_clockMs);
        ((StoreWind)(object)proxy).Store = store;
        h.Scene.RegisterModuleInterface<IWindModule>(proxy);
        return store;
    }

    private static void Estate(SchedulerHarness h, bool manager, bool god = false)
    {
        UUID owner = h.Prim.OwnerID;
        h.Scene.Permissions.OnIsAdministrator += id => god && id == owner;
        h.Scene.Permissions.OnIssueEstateCommand += (user, ownerCommand) => manager && user == owner;
    }

    private static LSLList Drifting(Vector3 drift)
        => new LSLList(new List<object>
        {
            WindVolumeRules.WV_CENTER, new Vector3(60, 128, 60), WindVolumeRules.WV_RADIUS, 10f,
            WindVolumeRules.WV_FALLOFF, 1f, WindVolumeRules.WV_VELOCITY, new Vector3(5, 0, 2),
            WindVolumeRules.WV_LIFETIME, 120f, WindVolumeRules.WV_DRIFT, drift,
        });

    private static SchedulerHarness Harness()
        => new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "Severe"));

    [Fact]
    public void AnEstateManagersScriptMakesAVolumeThatDrifts()
    {
        using var h = Harness();
        Estate(h, manager: true);
        WindVolumeStore store = Wind(h);

        int handle = Api(h).iwWindVolumeCreate(Drifting(new Vector3(4, 0, 0)));
        Assert.True(handle >= 1, $"got {handle}");

        WindVolume v = Assert.Single(store.List(UUID.Zero));
        Assert.Equal(new Vector3(4, 0, 0), v.Drift);
        Assert.Equal(new Vector3(5, 0, 2), v.Velocity);
        m_clockMs += 10_000;
        Assert.Equal(new Vector3(5, 0, 2), store.Sample(new Vector3(100, 128, 60)));
        Assert.Equal(Vector3.Zero, store.Sample(new Vector3(60, 128, 60)));
    }

    [Fact]
    public void AGodsScriptMakesAVolumeThatDrifts()
    {
        using var h = Harness();
        Estate(h, manager: false, god: true);
        WindVolumeStore store = Wind(h);
        Assert.True(Api(h).iwWindVolumeCreate(Drifting(new Vector3(0, 3, 0))) >= 1);
        Assert.Equal(new Vector3(0, 3, 0), Assert.Single(store.List(UUID.Zero)).Drift);
    }

    [Fact]
    public void AScriptOutsideTheEstateIsRefusedAndItsCallsNeverReachTheThrottle()
    {
        using var h = Harness();
        Estate(h, manager: false);
        WindVolumeStore store = Wind(h, new WindVolumeLimits { CreateThrottlePerMinute = 2 });
        var api = Api(h);

        for (int i = 0; i < 20; i++)
            Assert.Equal(WindVolumeError.NotAllowed, api.iwWindVolumeCreate(Drifting(new Vector3(4, 0, 0))));
        Assert.Equal(0, store.Count);

        // The object still has its whole allowance of two.
        var spec = new WindVolumeSpec
        {
            Shape = WindVolumeShape.Sphere, Center = new Vector3(60, 128, 60), Radius = 10f, FalloffStart = 1f,
            LifetimeSeconds = 120f, Velocity = new Vector3(5, 0, 2),
        };
        int drifting = store.Create(h.Prim.ParentGroup.UUID, h.Prim.OwnerID, spec with { Drift = new Vector3(4, 0, 0) });
        Assert.True(drifting >= 1);
        Assert.True(store.Create(h.Prim.ParentGroup.UUID, h.Prim.OwnerID, spec) >= 1);

        // Nor can it remove a drifting volume, even one its own object made.
        Assert.Equal(WindVolumeError.NotAllowed, api.iwWindVolumeRemove(drifting));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void ADriftAboveTheCapIsRefusedWithItsOwnCode()
    {
        using var h = Harness();
        Estate(h, manager: true);
        WindVolumeStore store = Wind(h);

        Assert.Equal(WindVolumeError.DriftTooFast, Api(h).iwWindVolumeCreate(Drifting(new Vector3(40.5f, 0, 0))));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void ADriftThatIsNotAVectorIsAnInvalidRule()
    {
        using var h = Harness();
        Estate(h, manager: true);
        WindVolumeStore store = Wind(h);
        var rules = new LSLList(new List<object>
        {
            WindVolumeRules.WV_CENTER, new Vector3(60, 128, 60), WindVolumeRules.WV_RADIUS, 10f,
            WindVolumeRules.WV_VELOCITY, new Vector3(5, 0, 2), WindVolumeRules.WV_LIFETIME, 120f,
            WindVolumeRules.WV_DRIFT, 4f,
        });
        Assert.Equal(WindVolumeError.InvalidParameter, Api(h).iwWindVolumeCreate(rules));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void AFloodOfCreatesAndRemovesIsThrottled()
    {
        using var h = Harness();
        Estate(h, manager: true);
        WindVolumeStore store = Wind(h);
        var api = Api(h);

        // The default allowance is 10 a minute: five creates and five removes.
        for (int i = 0; i < 5; i++)
        {
            int handle = api.iwWindVolumeCreate(Drifting(Vector3.Zero));
            Assert.True(handle >= 1, $"create {i}: {handle}");
            Assert.Equal(WindVolumeError.Ok, api.iwWindVolumeRemove(handle));
        }
        Assert.Equal(WindVolumeError.Throttled, api.iwWindVolumeCreate(Drifting(Vector3.Zero)));
        Assert.Equal(0, store.Count);

        m_clockMs += 60_000;
        Assert.True(api.iwWindVolumeCreate(Drifting(Vector3.Zero)) >= 1);
    }

    [Theory]
    [InlineData("WV_DRIFT", WindVolumeRules.WV_DRIFT)]
    [InlineData("WV_ERR_DRIFT_TOO_FAST", WindVolumeError.DriftTooFast)]
    [InlineData("WV_ERR_THROTTLED", WindVolumeError.Throttled)]
    public void TheCompilersConstantsMatchTheRegionsValues(string name, int value)
    {
        Assert.True(DefaultConstants.Constants.TryGetValue(name, out var symbol), name);
        Assert.Equal(value.ToString(System.Globalization.CultureInfo.InvariantCulture), symbol.ConstValue);
    }
}
