/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenMetaverse;

namespace OpenSim.Region.Framework.Interfaces;

/// <summary>
/// Results of a weather set call (iwSetWeather, osSetWeather): <see cref="Changed"/> or <see cref="Unchanged"/>, or a
/// negative code when the call was refused and nothing changed.
/// </summary>
public static class WeatherResult
{
    /// <summary>The region's weather is now the one asked for, and scripts are told with changed(CHANGED_WEATHER).</summary>
    public const int Changed = 1;

    /// <summary>
    /// The weather asked for is the one in force: the same state name and an intensity less than
    /// <see cref="WeatherLimits.MinIntensityStep"/> from the current target. Nothing changes and no script is told.
    /// </summary>
    public const int Unchanged = 0;

    /// <summary>Weather is switched off in this region ([Weather] enabled = false), or the region has no wind module.</summary>
    public const int Disabled = -1;

    /// <summary>The object's owner is not the estate owner, an estate manager or a god.</summary>
    public const int NotAllowed = -2;

    /// <summary>
    /// A value is out of its range: a state name longer than <see cref="WeatherLimits.MaxStateBytes"/> bytes or holding a
    /// control character, an intensity that is not a number from 0 to 1, or a transition that is not a number from 0 to
    /// <see cref="WeatherLimits.MaxTransitionSeconds"/>. Values are refused, never clamped.
    /// </summary>
    public const int Invalid = -3;

    /// <summary>The last change was less than <see cref="WeatherLimits.MinChangeIntervalSeconds"/> ago.</summary>
    public const int Throttled = -4;
}

/// <summary>The bounds on a region's weather state. The interval and the transition come from the [Weather] section.</summary>
public sealed class WeatherLimits
{
    /// <summary>The longest state name, in bytes of UTF-8.</summary>
    public const int MaxStateBytes = 32;

    /// <summary>
    /// The smallest move of the intensity that counts as a change when the state name stays the same. A smaller move
    /// is <see cref="WeatherResult.Unchanged"/>, so scripts are not woken for a difference they would not act on.
    /// </summary>
    public const float MinIntensityStep = 0.05f;

    /// <summary>Seconds that must pass after a change before the next change is taken. Default 5.</summary>
    public float MinChangeIntervalSeconds { get; init; } = 5f;

    /// <summary>The longest transition, in seconds, over which the intensity may ramp to its new value. Default 3600.</summary>
    public float MaxTransitionSeconds { get; init; } = 3600f;

    /// <summary>
    /// Checks a state name: at most <see cref="MaxStateBytes"/> bytes of UTF-8 and no control character. An empty name
    /// is allowed: it is the state a region starts with.
    /// </summary>
    public static bool IsValidStateName(string state)
    {
        if (state == null) return false;
        if (Encoding.UTF8.GetByteCount(state) > MaxStateBytes) return false;
        foreach (char c in state)
            if (char.IsControl(c)) return false;
        return true;
    }
}

/// <summary>
/// A region's weather as it is at one moment. Immutable: a reader that holds one never sees it change.
/// </summary>
public sealed class WeatherSnapshot
{
    /// <summary>The weather of a region that nobody has set: an empty state, intensity 0, serial 0.</summary>
    public static readonly WeatherSnapshot Initial = new(string.Empty, 0f, 0f, 0, 0, 0, 0, UUID.Zero, UUID.Zero);

    /// <summary>The state name the setter gave, such as "storm". Empty until a script sets one.</summary>
    public string State { get; }

    /// <summary>The intensity, 0 to 1, that the weather is going to (or has reached).</summary>
    public float TargetIntensity { get; }

    /// <summary>The intensity the ramp started from: the intensity in force when this state was set.</summary>
    public float FromIntensity { get; }

    /// <summary>When the ramp started, in the weather state's clock (milliseconds).</summary>
    public long RampStartMs { get; }

    /// <summary>How long the ramp lasts, in milliseconds. 0 means the target is reached at once.</summary>
    public long RampMs { get; }

    /// <summary>When this state was set, in seconds since 1970 (UTC). 0 until a script sets one.</summary>
    public long SinceUnix { get; }

    /// <summary>Counts the changes since the region started: 0 at the start, one more on each change.</summary>
    public int Serial { get; }

    /// <summary>The object whose script set this state (its linkset's root), or <see cref="UUID.Zero"/>.</summary>
    public UUID SetByObject { get; }

    /// <summary>The owner of that object, or <see cref="UUID.Zero"/>.</summary>
    public UUID SetByOwner { get; }

    public WeatherSnapshot(string state, float targetIntensity, float fromIntensity, long rampStartMs, long rampMs,
        long sinceUnix, int serial, UUID setByObject, UUID setByOwner)
    {
        State = state ?? string.Empty;
        TargetIntensity = targetIntensity;
        FromIntensity = fromIntensity;
        RampStartMs = rampStartMs;
        RampMs = rampMs;
        SinceUnix = sinceUnix;
        Serial = serial;
        SetByObject = setByObject;
        SetByOwner = setByOwner;
    }

    /// <summary>
    /// The intensity at a time of the weather state's clock: a straight ramp from <see cref="FromIntensity"/> to
    /// <see cref="TargetIntensity"/> over the transition, then the target.
    /// </summary>
    public float IntensityAt(long nowMs)
    {
        if (RampMs <= 0 || nowMs >= RampStartMs + RampMs) return TargetIntensity;
        if (nowMs <= RampStartMs) return FromIntensity;
        float t = (float)(nowMs - RampStartMs) / RampMs;
        return FromIntensity + (TargetIntensity - FromIntensity) * t;
    }
}

/// <summary>
/// A region's weather state: a name and an intensity that a script of the estate sets and every script can read, so
/// objects can react to the weather (close awnings, reef sails, start a rain sound). Held in memory only; a region
/// starts with <see cref="WeatherSnapshot.Initial"/>. The wind module owns it, from the [Weather] section.
/// </summary>
public interface IWeatherState
{
    /// <summary>False when [Weather] enabled is false: the state then accepts nothing and reads as nothing.</summary>
    bool Enabled { get; }

    WeatherLimits Limits { get; }

    /// <summary>The weather now. Never waits for a writer.</summary>
    WeatherSnapshot Current { get; }

    /// <summary>The weather state's clock, milliseconds, for <see cref="WeatherSnapshot.IntensityAt"/>.</summary>
    long NowMs { get; }

    /// <summary>
    /// Sets the weather. Returns a <see cref="WeatherResult"/>: Changed (and <see cref="WeatherChanged"/> is raised once),
    /// Unchanged, or a negative code with nothing changed. Whether the caller may set it is for the caller to check
    /// first (<see cref="WindVolumeAccess.MayChangeWind"/>).
    /// </summary>
    /// <param name="state">The state name; see <see cref="WeatherLimits.IsValidStateName"/>.</param>
    /// <param name="intensity">0 to 1.</param>
    /// <param name="transitionSeconds">0 to <see cref="WeatherLimits.MaxTransitionSeconds"/>: the intensity ramps from where it is to the new value over this time.</param>
    /// <param name="byObject">The object whose script asks, for the record and the log.</param>
    /// <param name="byOwner">Its owner.</param>
    int Set(string state, float intensity, float transitionSeconds, UUID byObject, UUID byOwner);

    /// <summary>Raised once after each change, with the new weather, on the thread that made the change.</summary>
    event Action<WeatherSnapshot> WeatherChanged;
}
