/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.CoreModules.World.Wind;
using OpenSim.Region.Framework.Interfaces;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.World.Wind;

/// <summary>
/// The region's weather state: what a set does (a change, no change, or a refusal that changes nothing), the limits on
/// the values (refused, never clamped), the change throttle, the intensity ramp, the event raised once per change, and
/// the [Weather] keys that configure it, and the operator command "weather state". Time comes from a test clock, so nothing waits.
/// </summary>
// Each test owns its state and clock; nothing process-wide is touched (the assembly runs its tests one at a time anyway).
public class WeatherStateTests
{
    private static readonly UUID Obj = UUID.Random();
    private static readonly UUID Owner = UUID.Random();

    private sealed class Clock
    {
        public long Ms = 1_000_000;
        public long Unix = 1_800_000_000;
        public void Advance(double seconds)
        {
            Ms += (long)(seconds * 1000);
            Unix += (long)seconds;
        }
    }

    private static (WeatherState State, Clock Clock, List<WeatherSnapshot> Events) New(bool enabled = true, WeatherLimits limits = null)
    {
        var clock = new Clock();
        var state = new WeatherState(enabled, limits, () => clock.Ms, () => clock.Unix);
        var events = new List<WeatherSnapshot>();
        state.WeatherChanged += events.Add;
        return (state, clock, events);
    }

    // ---- a region's start ----

    [Fact]
    public void ARegionStartsWithAnEmptyStateIntensityZeroAndSerialZero()
    {
        var (state, _, _) = New();
        WeatherSnapshot now = state.Current;
        Assert.Equal(string.Empty, now.State);
        Assert.Equal(0f, now.IntensityAt(state.NowMs));
        Assert.Equal(0, now.Serial);
        Assert.Equal(0L, now.SinceUnix);
    }

    // ---- a change ----

    [Fact]
    public void ASetChangesTheStateRecordsWhenAndWhoAndRaisesOneEvent()
    {
        var (state, clock, events) = New();

        Assert.Equal(WeatherResult.Changed, state.Set("storm", 0.6f, 0f, Obj, Owner));

        WeatherSnapshot now = state.Current;
        Assert.Equal("storm", now.State);
        Assert.Equal(0.6f, now.IntensityAt(state.NowMs));
        Assert.Equal(1, now.Serial);
        Assert.Equal(clock.Unix, now.SinceUnix);
        Assert.Equal(Obj, now.SetByObject);
        Assert.Equal(Owner, now.SetByOwner);
        Assert.Single(events);
        Assert.Same(now, events[0]);
    }

    [Fact]
    public void EachRealChangeAddsOneToTheSerialAndRaisesOneEvent()
    {
        var (state, clock, events) = New();
        Assert.Equal(WeatherResult.Changed, state.Set("rain", 0.2f, 0f, Obj, Owner));
        clock.Advance(6);
        Assert.Equal(WeatherResult.Changed, state.Set("storm", 0.2f, 0f, Obj, Owner));
        clock.Advance(6);
        Assert.Equal(WeatherResult.Changed, state.Set("storm", 0.9f, 0f, Obj, Owner));
        Assert.Equal(3, state.Current.Serial);
        Assert.Equal(new[] { 1, 2, 3 }, events.Select(e => e.Serial));
    }

    [Fact]
    public void SettingTheSameStateAgainChangesNothingAndRaisesNothing()
    {
        var (state, clock, events) = New();
        state.Set("storm", 0.6f, 0f, Obj, Owner);
        WeatherSnapshot before = state.Current;

        clock.Advance(60);
        Assert.Equal(WeatherResult.Unchanged, state.Set("storm", 0.6f, 0f, Obj, Owner));
        // Within the throttle window too: the same state is "no change", not a refusal.
        Assert.Equal(WeatherResult.Unchanged, state.Set("storm", 0.6f, 30f, UUID.Random(), Owner));

        Assert.Same(before, state.Current);
        Assert.Single(events);
    }

    [Fact]
    public void AnIntensityMoveBelowTheStepIsNoChangeAndAtTheStepIsAChange()
    {
        var (state, clock, events) = New();
        state.Set("storm", 0.2f, 0f, Obj, Owner);
        clock.Advance(6);

        Assert.Equal(WeatherResult.Unchanged, state.Set("storm", 0.24f, 0f, Obj, Owner));
        Assert.Equal(0.2f, state.Current.TargetIntensity);
        Assert.Single(events);

        // 0.25 - 0.2 is 0.05 in decimal, a little under it in float; it still counts as the step.
        Assert.Equal(WeatherResult.Changed, state.Set("storm", 0.25f, 0f, Obj, Owner));
        Assert.Equal(0.25f, state.Current.TargetIntensity);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void ANewNameIsAChangeWhateverTheIntensity()
    {
        var (state, clock, _) = New();
        state.Set("storm", 0.5f, 0f, Obj, Owner);
        clock.Advance(6);
        Assert.Equal(WeatherResult.Changed, state.Set("Storm", 0.5f, 0f, Obj, Owner));
        Assert.Equal("Storm", state.Current.State);
    }

    // ---- limits: refused, never clamped ----

    public static IEnumerable<object[]> BadValues() => new[]
    {
        new object[] { new string('a', 33), 0.5f, 0f },               // 33 bytes
        new object[] { new string('é', 17), 0.5f, 0f },          // 17 two-byte characters = 34 bytes
        new object[] { "storm\nwarning", 0.5f, 0f },                   // a control character
        new object[] { null, 0.5f, 0f },
        new object[] { "storm", -0.01f, 0f },
        new object[] { "storm", 1.01f, 0f },
        new object[] { "storm", float.NaN, 0f },
        new object[] { "storm", float.PositiveInfinity, 0f },
        new object[] { "storm", 0.5f, -1f },
        new object[] { "storm", 0.5f, float.NaN },
        new object[] { "storm", 0.5f, float.PositiveInfinity },
        new object[] { "storm", 0.5f, 3600.5f },                       // above max_transition
    };

    [Theory]
    [MemberData(nameof(BadValues))]
    public void AValueOutsideItsLimitsIsRefusedAndChangesNothing(string name, float intensity, float transition)
    {
        var (state, clock, events) = New();
        state.Set("clear", 0.1f, 0f, Obj, Owner);
        WeatherSnapshot before = state.Current;
        clock.Advance(60);

        Assert.Equal(WeatherResult.Invalid, state.Set(name, intensity, transition, Obj, Owner));

        Assert.Same(before, state.Current);
        Assert.Single(events);
    }

    [Fact]
    public void TheLimitsThemselvesAreAccepted()
    {
        var (state, clock, _) = New();
        Assert.Equal(WeatherResult.Changed, state.Set(new string('a', 32), 1f, 3600f, Obj, Owner));
        clock.Advance(6);
        Assert.Equal(WeatherResult.Changed, state.Set(new string('é', 16), 0f, 0f, Obj, Owner));
        clock.Advance(6);
        // The empty name is the state a region starts with, and a script may go back to it.
        Assert.Equal(WeatherResult.Changed, state.Set(string.Empty, 0.5f, 0f, Obj, Owner));
    }

    // ---- the change throttle ----

    [Fact]
    public void AChangeWithinTheIntervalIsRefusedAndOneAfterItIsTaken()
    {
        var (state, clock, events) = New();
        state.Set("rain", 0.3f, 0f, Obj, Owner);
        WeatherSnapshot before = state.Current;

        clock.Advance(4.9);
        Assert.Equal(WeatherResult.Throttled, state.Set("storm", 0.8f, 0f, Obj, Owner));
        Assert.Same(before, state.Current);
        Assert.Single(events);

        clock.Advance(0.1);
        Assert.Equal(WeatherResult.Changed, state.Set("storm", 0.8f, 0f, Obj, Owner));
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void TheFirstChangeIsNeverThrottled()
    {
        var (state, _, _) = New(limits: new WeatherLimits { MinChangeIntervalSeconds = 1_000_000f });
        Assert.Equal(WeatherResult.Changed, state.Set("storm", 0.5f, 0f, Obj, Owner));
    }

    // ---- the ramp ----

    [Fact]
    public void TheIntensityRampsFromWhereItIsToTheTargetOverTheTransition()
    {
        var (state, clock, events) = New();
        state.Set("storm", 0.2f, 0f, Obj, Owner);
        clock.Advance(10);

        Assert.Equal(WeatherResult.Changed, state.Set("storm", 1.0f, 100f, Obj, Owner));
        WeatherSnapshot now = state.Current;
        Assert.Equal(0.2f, now.IntensityAt(state.NowMs), 4);
        clock.Advance(50);
        Assert.Equal(0.6f, now.IntensityAt(state.NowMs), 4);
        clock.Advance(50);
        Assert.Equal(1.0f, now.IntensityAt(state.NowMs), 4);
        clock.Advance(500);
        Assert.Equal(1.0f, now.IntensityAt(state.NowMs), 4);

        // One event for the set, none for the ramp.
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void ANewSetDuringARampStartsFromTheIntensityReachedSoFar()
    {
        var (state, clock, _) = New();
        state.Set("storm", 0f, 0f, Obj, Owner);
        clock.Advance(10);
        state.Set("storm", 1f, 100f, Obj, Owner);
        clock.Advance(25);                                            // 0.25 reached
        Assert.Equal(WeatherResult.Changed, state.Set("clear", 0f, 50f, Obj, Owner));
        Assert.Equal(0.25f, state.Current.IntensityAt(state.NowMs), 4);
        clock.Advance(25);
        Assert.Equal(0.125f, state.Current.IntensityAt(state.NowMs), 4);
    }

    [Fact]
    public void TheChangeTestComparesWithTheTargetNotTheRampedValue()
    {
        var (state, clock, _) = New();
        state.Set("storm", 0f, 0f, Obj, Owner);
        clock.Advance(10);
        state.Set("storm", 1f, 100f, Obj, Owner);
        clock.Advance(50);
        // Half way up the ramp, asking for the same target again is no change.
        Assert.Equal(WeatherResult.Unchanged, state.Set("storm", 1f, 100f, Obj, Owner));
    }

    // ---- off ----

    [Fact]
    public void WithWeatherOffEverySetIsDisabledAndNothingIsRaised()
    {
        var (state, _, events) = New(enabled: false);
        Assert.Equal(WeatherResult.Disabled, state.Set("storm", 0.5f, 0f, Obj, Owner));
        Assert.Equal(WeatherResult.Disabled, state.Set(new string('a', 99), 5f, -1f, Obj, Owner));
        Assert.Same(WeatherSnapshot.Initial, state.Current);
        Assert.Empty(events);
    }

    // ---- the [Weather] keys ----

    [Fact]
    public void NoSectionOrEnabledFalseGivesAStateThatIsOff()
    {
        Assert.False(WindModule.CreateWeather(null).Enabled);
        var config = new IniConfigSource();
        config.AddConfig("Weather");
        Assert.False(WindModule.CreateWeather(config.Configs["Weather"]).Enabled);
    }

    [Fact]
    public void TheKeysAreReadAndAbsentOrBadValuesTakeTheDefaults()
    {
        var config = new IniConfigSource();
        IConfig w = config.AddConfig("Weather");
        w.Set("enabled", "true");
        w.Set("min_change_interval", "12");
        w.Set("max_transition", "90");
        IWeatherState state = WindModule.CreateWeather(w);
        Assert.True(state.Enabled);
        Assert.Equal(12f, state.Limits.MinChangeIntervalSeconds);
        Assert.Equal(90f, state.Limits.MaxTransitionSeconds);

        w.Set("min_change_interval", "-3");
        w.Remove("max_transition");
        state = WindModule.CreateWeather(w);
        Assert.Equal(5f, state.Limits.MinChangeIntervalSeconds);
        Assert.Equal(3600f, state.Limits.MaxTransitionSeconds);
    }

    [Fact]
    public void AValueThatIsNotANumberTakesTheDefaultAndTheRegionStillStarts()
    {
        var config = new IniConfigSource();
        IConfig w = config.AddConfig("Weather");
        w.Set("enabled", "true");
        w.Set("min_change_interval", "abc");
        w.Set("max_transition", "1e999");
        IWeatherState state = WindModule.CreateWeather(w);
        Assert.True(state.Enabled);
        Assert.Equal(5f, state.Limits.MinChangeIntervalSeconds);
        Assert.Equal(3600f, state.Limits.MaxTransitionSeconds);
    }

    [Fact]
    public void TheConfiguredTransitionCapIsTheLimit()
    {
        var (state, _, _) = New(limits: new WeatherLimits { MaxTransitionSeconds = 30f });
        Assert.Equal(WeatherResult.Invalid, state.Set("storm", 0.5f, 31f, Obj, Owner));
        Assert.Equal(WeatherResult.Changed, state.Set("storm", 0.5f, 30f, Obj, Owner));
    }

    // ---- weather state on the console ----

    [Fact]
    public void TheConsoleShowsTheStateAndWhoSetIt()
    {
        var clockMs = 5_000L;
        var weather = new WeatherState(true, null, () => clockMs, () => 1_800_000_000L);
        weather.Set("storm", 0.8f, 0f, Obj, Owner);
        var lines = new List<string>();

        WindVolumeConsole.State(weather, "Test Region", lines.Add);

        string text = string.Join("\n", lines);
        Assert.Contains("Test Region", text);
        Assert.Contains("storm", text);
        Assert.Contains("0.8", text);
        Assert.Contains("serial 1", text);
        Assert.Contains(Obj.ToString(), text);
        Assert.Contains(Owner.ToString(), text);
    }

    [Fact]
    public void TheConsoleSaysWhenWeatherIsOffOrUnset()
    {
        var lines = new List<string>();
        WindVolumeConsole.State(new WeatherState(false), "Test Region", lines.Add);
        Assert.Equal(new[] { "Weather is off in Test Region." }, lines);

        lines.Clear();
        WindVolumeConsole.State(new WeatherState(true), "Test Region", lines.Add);
        Assert.Single(lines);
        Assert.Contains("not been set", lines[0]);
    }
}
