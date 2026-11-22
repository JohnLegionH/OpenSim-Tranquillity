/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.CoreModules.World.Wind;

/// <summary>
/// A region's weather state, in memory only.
///
/// A set takes a lock, decides, and publishes a new immutable <see cref="WeatherSnapshot"/>. Readers take the current
/// snapshot with a single volatile read and never wait. The change event is raised after the lock is released, once per
/// change, so a handler may read the state (or even set it) without deadlocking.
/// </summary>
public sealed class WeatherState : IWeatherState
{
    private readonly Func<long> m_clockMs;
    private readonly Func<long> m_unixNow;
    private readonly object m_writeLock = new();
    private WeatherSnapshot m_current = WeatherSnapshot.Initial;

    // When the last change was made (the clock's milliseconds), or null before the first.
    private long? m_lastChangeMs;

    public bool Enabled { get; }
    public WeatherLimits Limits { get; }

    /// <param name="enabled">False makes a state that accepts nothing.</param>
    /// <param name="limits">The bounds; defaults when null.</param>
    /// <param name="clockMs">Milliseconds from a steady clock, for the throttle and the ramp; the tick count when null.</param>
    /// <param name="unixNow">Seconds since 1970 (UTC), for "since"; the system clock when null.</param>
    public WeatherState(bool enabled, WeatherLimits limits = null, Func<long> clockMs = null, Func<long> unixNow = null)
    {
        Enabled = enabled;
        Limits = limits ?? new WeatherLimits();
        m_clockMs = clockMs ?? (() => Environment.TickCount64);
        m_unixNow = unixNow ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public WeatherSnapshot Current => Volatile.Read(ref m_current);

    public long NowMs => m_clockMs();

    public event Action<WeatherSnapshot> WeatherChanged;

    public int Set(string state, float intensity, float transitionSeconds, UUID byObject, UUID byOwner)
    {
        if (!Enabled) return WeatherResult.Disabled;

        // Out-of-range values are refused, never clamped, so a script learns that its value was not used.
        if (!WeatherLimits.IsValidStateName(state)) return WeatherResult.Invalid;
        if (!float.IsFinite(intensity) || intensity < 0f || intensity > 1f) return WeatherResult.Invalid;
        if (!float.IsFinite(transitionSeconds) || transitionSeconds < 0f || transitionSeconds > Limits.MaxTransitionSeconds)
            return WeatherResult.Invalid;

        WeatherSnapshot next;
        lock (m_writeLock)
        {
            WeatherSnapshot now = m_current;
            long nowMs = m_clockMs();

            // The same name and a target that moves less than the step: nothing a script would act on.
            if (string.Equals(state, now.State, StringComparison.Ordinal)
                && MathF.Abs(intensity - now.TargetIntensity) < WeatherLimits.MinIntensityStep - StepTolerance)
                return WeatherResult.Unchanged;

            if (m_lastChangeMs is long last && nowMs - last < (long)(Limits.MinChangeIntervalSeconds * 1000f))
                return WeatherResult.Throttled;

            next = new WeatherSnapshot(
                state,
                intensity,
                now.IntensityAt(nowMs),
                nowMs,
                (long)(transitionSeconds * 1000f),
                m_unixNow(),
                now.Serial + 1,
                byObject,
                byOwner);
            m_lastChangeMs = nowMs;
            Volatile.Write(ref m_current, next);
        }

        WeatherChanged?.Invoke(next);
        return WeatherResult.Changed;
    }

    // Float rounding: 0.25f - 0.2f is a little under 0.05f, and a move of the full step still counts.
    private const float StepTolerance = 1e-5f;
}
