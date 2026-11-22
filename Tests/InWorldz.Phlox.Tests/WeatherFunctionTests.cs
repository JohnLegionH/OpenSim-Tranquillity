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
/// iwSetWeather and iwGetWeather: setting needs an owner who is the estate owner, an estate manager or a god (the
/// iwSetWind rule) and [Weather] enabled; reading needs only [Weather] enabled. A refused call returns its code and
/// changes nothing. Values outside the limits are refused, not clamped. Also the constants scripts compile against.
/// </summary>
// Calls the API directly on the harness prim with a weather state of its own; no clock is shared and nothing
// process-wide is changed, so the class runs in parallel.
public class WeatherFunctionTests
{
    private static LSLSystemAPI Api(SchedulerHarness h) => new LSLSystemAPI(h.Engine, h.Prim, h.Prim.LocalId, UUID.Random());

    // A wind module that has a weather state and nothing else.
    internal class WeatherWind : DispatchProxy
    {
        public IWeatherState Weather;

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod.Name == "get_Weather") return Weather;
            var rt = targetMethod.ReturnType;
            return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
        }
    }

    internal static IWeatherState Weather(SchedulerHarness h, bool weatherOn = true, bool withState = true)
    {
        var proxy = DispatchProxy.Create<IWindModule, WeatherWind>();
        var wind = (WeatherWind)(object)proxy;
        // No change interval, so a test can make several changes without waiting.
        wind.Weather = withState ? new WeatherState(weatherOn, new WeatherLimits { MinChangeIntervalSeconds = 0f }) : null;
        h.Scene.RegisterModuleInterface<IWindModule>(proxy);
        return wind.Weather;
    }

    private static void Estate(SchedulerHarness h, bool manager, bool god = false)
    {
        UUID owner = h.Prim.OwnerID;
        h.Scene.Permissions.OnIsAdministrator += id => god && id == owner;
        h.Scene.Permissions.OnIssueEstateCommand += (user, ownerCommand) => manager && user == owner;
    }

    // ---- setting: the gate ----

    [Fact]
    public void AnEstateManagersObjectSetsTheWeather()
    {
        using var h = new SchedulerHarness();
        Estate(h, manager: true);
        IWeatherState weather = Weather(h);

        Assert.Equal(WeatherResult.Changed, Api(h).iwSetWeather("storm", 0.75f, 0f));

        Assert.Equal("storm", weather.Current.State);
        Assert.Equal(0.75f, weather.Current.TargetIntensity);
        Assert.Equal(h.Prim.ParentGroup.UUID, weather.Current.SetByObject);
        Assert.Equal(h.Prim.OwnerID, weather.Current.SetByOwner);
    }

    [Fact]
    public void AGodsObjectSetsTheWeather()
    {
        using var h = new SchedulerHarness();
        Estate(h, manager: false, god: true);
        IWeatherState weather = Weather(h);
        Assert.Equal(WeatherResult.Changed, Api(h).iwSetWeather("storm", 0.5f, 0f));
        Assert.Equal(1, weather.Current.Serial);
    }

    [Fact]
    public void AnyoneElseIsRefusedAndNothingChangesOrIsRaised()
    {
        using var h = new SchedulerHarness();
        Estate(h, manager: false);
        IWeatherState weather = Weather(h);
        int raised = 0;
        weather.WeatherChanged += _ => raised++;

        Assert.Equal(WeatherResult.NotAllowed, Api(h).iwSetWeather("storm", 0.5f, 0f));
        // The refusal comes before the values are looked at: a refused caller learns nothing about the limits.
        Assert.Equal(WeatherResult.NotAllowed, Api(h).iwSetWeather(new string('a', 99), 7f, -1f));

        Assert.Same(WeatherSnapshot.Initial, weather.Current);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void ARefusedCallerCannotUndoTheEstatesWeather()
    {
        using var h = new SchedulerHarness();
        Estate(h, manager: false);
        IWeatherState weather = Weather(h);
        weather.Set("storm", 0.9f, 0f, UUID.Random(), UUID.Random());   // set by an estate object elsewhere
        WeatherSnapshot before = weather.Current;

        Assert.Equal(WeatherResult.NotAllowed, Api(h).iwSetWeather("clear", 0f, 0f));
        Assert.Same(before, weather.Current);
    }

    [Fact]
    public void AnOwnerWhoLosesTheEstateRightIsRefusedFromThenOn()
    {
        using var h = new SchedulerHarness();
        bool manager = true;
        UUID owner = h.Prim.OwnerID;
        h.Scene.Permissions.OnIssueEstateCommand += (user, ownerCommand) => manager && user == owner;
        h.Scene.Permissions.OnIsAdministrator += user => false;   // with no handler the check answers yes
        IWeatherState weather = Weather(h);
        var api = Api(h);

        Assert.Equal(WeatherResult.Changed, api.iwSetWeather("storm", 0.5f, 0f));
        manager = false;
        Assert.Equal(WeatherResult.NotAllowed, api.iwSetWeather("clear", 0f, 0f));
        Assert.Equal("storm", weather.Current.State);
    }

    // ---- setting: values, repeats ----

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0.5f, 0f)]   // 33 bytes
    [InlineData("storm", 1.5f, 0f)]
    [InlineData("storm", -0.5f, 0f)]
    [InlineData("storm", float.NaN, 0f)]
    [InlineData("storm", 0.5f, -2f)]
    [InlineData("storm", 0.5f, 4000f)]
    public void AValueOutsideTheLimitsIsRefusedNotClamped(string name, float intensity, float transition)
    {
        using var h = new SchedulerHarness();
        Estate(h, manager: true);
        IWeatherState weather = Weather(h);
        Assert.Equal(WeatherResult.Invalid, Api(h).iwSetWeather(name, intensity, transition));
        Assert.Same(WeatherSnapshot.Initial, weather.Current);
    }

    [Fact]
    public void SettingTheSameWeatherAgainIsUnchanged()
    {
        using var h = new SchedulerHarness();
        Estate(h, manager: true);
        IWeatherState weather = Weather(h);
        int raised = 0;
        weather.WeatherChanged += _ => raised++;
        var api = Api(h);

        Assert.Equal(WeatherResult.Changed, api.iwSetWeather("storm", 0.5f, 0f));
        Assert.Equal(WeatherResult.Unchanged, api.iwSetWeather("storm", 0.5f, 0f));
        Assert.Equal(1, weather.Current.Serial);
        Assert.Equal(1, raised);
    }

    // ---- weather off ----

    [Fact]
    public void WithWeatherOffSettingIsDisabledAndReadingIsEmpty()
    {
        using var h = new SchedulerHarness();
        Estate(h, manager: true);
        IWeatherState weather = Weather(h, weatherOn: false);
        var api = Api(h);

        Assert.Equal(WeatherResult.Disabled, api.iwSetWeather("storm", 0.5f, 0f));
        Assert.Empty(api.iwGetWeather().Data);
        Assert.Same(WeatherSnapshot.Initial, weather.Current);
    }

    [Fact]
    public void WithNoWindModuleOrNoWeatherStateSettingIsDisabledAndReadingIsEmpty()
    {
        using var h = new SchedulerHarness();
        Estate(h, manager: true);
        Assert.Equal(WeatherResult.Disabled, Api(h).iwSetWeather("storm", 0.5f, 0f));
        Assert.Empty(Api(h).iwGetWeather().Data);

        Weather(h, withState: false);
        Assert.Equal(WeatherResult.Disabled, Api(h).iwSetWeather("storm", 0.5f, 0f));
        Assert.Empty(Api(h).iwGetWeather().Data);
    }

    // ---- reading ----

    [Fact]
    public void AnyScriptReadsStateIntensitySinceAndSerial()
    {
        using var h = new SchedulerHarness();
        Estate(h, manager: false);   // the reader has no estate right
        IWeatherState weather = Weather(h);
        weather.Set("tornado-warning", 0.4f, 0f, UUID.Random(), UUID.Random());

        object[] got = Api(h).iwGetWeather().Data;

        Assert.Equal(4, got.Length);
        Assert.Equal("tornado-warning", got[0]);
        Assert.Equal(0.4f, (float)got[1], 4);
        Assert.Equal((int)weather.Current.SinceUnix, got[2]);
        Assert.Equal(1, got[3]);
    }

    [Fact]
    public void BeforeAnySetTheReadIsTheRegionsStartingWeather()
    {
        using var h = new SchedulerHarness();
        Weather(h);
        Assert.Equal(new object[] { string.Empty, 0f, 0, 0 }, Api(h).iwGetWeather().Data);
    }

    // ---- what scripts compile against ----

    [Fact]
    public void TheConstantsMatchTheCore()
    {
        var c = DefaultConstants.Constants;
        Assert.Equal(WeatherResult.Changed.ToString(), c["WEATHER_SET"].ConstValue);
        Assert.Equal(WeatherResult.Unchanged.ToString(), c["WEATHER_UNCHANGED"].ConstValue);
        Assert.Equal(WeatherResult.Disabled.ToString(), c["WEATHER_ERR_DISABLED"].ConstValue);
        Assert.Equal(WeatherResult.NotAllowed.ToString(), c["WEATHER_ERR_NOT_ALLOWED"].ConstValue);
        Assert.Equal(WeatherResult.Invalid.ToString(), c["WEATHER_ERR_INVALID"].ConstValue);
        Assert.Equal(WeatherResult.Throttled.ToString(), c["WEATHER_ERR_THROTTLED"].ConstValue);
    }

    [Fact]
    public void TheFunctionsHaveTheirSignatures()
    {
        Assert.True(Defaults.TryGetMethod("iwSetWeather", out FunctionSig set));
        Assert.Equal(VarType.Integer, set.ReturnType);
        Assert.Equal(new[] { VarType.String, VarType.Float, VarType.Float }, set.ParamTypes);
        Assert.True(Defaults.TryGetMethod("iwGetWeather", out FunctionSig get));
        Assert.Equal(VarType.List, get.ReturnType);
        Assert.Empty(get.ParamTypes);
    }
}
