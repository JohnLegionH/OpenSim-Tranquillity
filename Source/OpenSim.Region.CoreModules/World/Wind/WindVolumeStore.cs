/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.CoreModules.World.Wind;

/// <summary>
/// The region's wind volumes, in memory only.
///
/// Writers (create, remove, prune) take a lock and publish a new immutable array of volumes. Readers (sample, list,
/// count) read the current array with a single volatile read and never take the lock, so a sampler never waits for a
/// writer, and never sees a half-made volume. Expiry is decided when a volume is read, so a volume whose lifetime
/// has passed stops counting at once, before anything frees it. A drifting volume's centre is worked out from the
/// time of each read (WindVolume.CenterAt), so motion needs no writer either.
///
/// Each creating object may make only so many create and remove calls in any 60 seconds
/// (WindVolumeLimits.CreateThrottlePerMinute); the record of its recent calls is kept under the write lock.
/// </summary>
public sealed class WindVolumeStore : IWindVolumeStore
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(typeof(WindVolumeStore));
    private static readonly WindVolume[] None = Array.Empty<WindVolume>();

    /// <summary>The window of the create and remove throttle, in milliseconds.</summary>
    public const long ThrottleWindowMs = 60_000;

    private readonly Func<long> m_clockMs;
    private readonly object m_writeLock = new();
    private WindVolume[] m_snapshot = None;
    private int m_lastId;

    // The times of each creating object's counted calls in the last window, oldest first; under m_writeLock.
    private readonly Dictionary<UUID, CallWindow> m_calls = new();

    private sealed class CallWindow
    {
        public readonly Queue<long> Times = new();
        public bool Logged;
    }

    private enum CallResult { Allowed, Throttled, ThrottledFirst }

    public bool Enabled { get; }
    public WindVolumeLimits Limits { get; }

    /// <summary>
    /// The region's size in metres (X and Y), which bounds where a drifting volume may go. The wind module sets it when
    /// it is added to a region, before any script can create a volume; 256 by 256 until then.
    /// </summary>
    public Vector2 RegionSize { get; set; } = new Vector2(256f, 256f);

    /// <summary>The region's name, for the log. The wind module sets it with <see cref="RegionSize"/>.</summary>
    public string RegionName { get; set; } = "";

    /// <param name="enabled">False makes a store that accepts nothing.</param>
    /// <param name="limits">The caps; defaults when null.</param>
    /// <param name="clockMs">Milliseconds from a steady clock; the environment's tick count when null. Tests pass their own.</param>
    public WindVolumeStore(bool enabled, WindVolumeLimits limits = null, Func<long> clockMs = null)
    {
        Enabled = enabled;
        Limits = limits ?? new WindVolumeLimits();
        m_clockMs = clockMs ?? (() => Environment.TickCount64);
    }

    public int Count
    {
        get
        {
            long now = m_clockMs();
            int n = 0;
            foreach (WindVolume v in Volatile.Read(ref m_snapshot))
                if (now < v.ExpiresAtMs) n++;
            return n;
        }
    }

    public int Create(UUID creatorObject, UUID owner, in WindVolumeSpec spec)
    {
        if (!Enabled) return WindVolumeError.Disabled;

        WindVolume made = null;
        CallResult call = CallResult.Allowed;
        int result;
        lock (m_writeLock)
        {
            long now = m_clockMs();
            if (!creatorObject.IsZero())
                call = TakeCall(creatorObject, now);
            result = call == CallResult.Allowed ? CreateLocked(creatorObject, owner, spec, now, out made) : WindVolumeError.Throttled;
        }

        // Logged outside the lock. The throttle bounds how often one object can add a line.
        if (call == CallResult.ThrottledFirst)
            LogThrottled(creatorObject);
        if (made != null)
            m_log.LogInformation(
                "[WEATHER] {0}: object {1} (owner {2}) made wind volume {3}: {4} centre {5} radius {6} height {7} wind {8} drift {9}, {10} s.",
                RegionName, creatorObject, owner, made.Id, made.Shape.ToString().ToLowerInvariant(), made.Center, made.Radius,
                made.Height, made.Velocity, made.Drift, spec.LifetimeSeconds);
        return result;
    }

    private int CreateLocked(UUID creatorObject, UUID owner, in WindVolumeSpec spec, long now, out WindVolume made)
    {
        made = null;
        int bad = Validate(spec);
        if (bad != WindVolumeError.Ok) return bad;

        WindVolume[] live = Live(m_snapshot, now);

        if (live.Length >= Limits.MaxVolumesRegion) return WindVolumeError.RegionFull;
        if (!creatorObject.IsZero() && CountBy(live, creatorObject) >= Limits.MaxVolumesObject)
            return WindVolumeError.ObjectFull;

        int id = NextId(live);
        made = new WindVolume(id, creatorObject, owner, spec, now, now + (long)(spec.LifetimeSeconds * 1000f), RegionSize);

        var next = new WindVolume[live.Length + 1];
        live.CopyTo(next, 0);
        next[live.Length] = made;
        Volatile.Write(ref m_snapshot, next);
        return id;
    }

    public int Remove(int handle, UUID creatorObject)
    {
        CallResult call = CallResult.Allowed;
        int result;
        lock (m_writeLock)
        {
            long now = m_clockMs();
            if (!creatorObject.IsZero())
                call = TakeCall(creatorObject, now);
            result = call == CallResult.Allowed ? RemoveLocked(handle, creatorObject, now) : WindVolumeError.Throttled;
        }

        if (call == CallResult.ThrottledFirst)
            LogThrottled(creatorObject);
        if (result == WindVolumeError.Ok && !creatorObject.IsZero())
            m_log.LogInformation("[WEATHER] {0}: object {1} removed wind volume {2}.", RegionName, creatorObject, handle);
        return result;
    }

    private int RemoveLocked(int handle, UUID creatorObject, long now)
    {
        WindVolume[] live = Live(m_snapshot, now);
        int at = Array.FindIndex(live, v => v.Id == handle);
        if (at < 0)
        {
            Publish(live);
            return WindVolumeError.NotFound;
        }
        if (!creatorObject.IsZero() && live[at].CreatorObject != creatorObject)
        {
            Publish(live);
            return WindVolumeError.NotAllowed;
        }
        Volatile.Write(ref m_snapshot, live.Where((_, i) => i != at).ToArray());
        return WindVolumeError.Ok;
    }

    public int RemoveByCreator(UUID creatorObject)
    {
        lock (m_writeLock)
        {
            WindVolume[] live = Live(m_snapshot, m_clockMs());
            WindVolume[] kept = live.Where(v => v.CreatorObject != creatorObject).ToArray();
            Volatile.Write(ref m_snapshot, kept);
            return live.Length - kept.Length;
        }
    }

    public int Clear()
    {
        lock (m_writeLock)
        {
            int n = Live(m_snapshot, m_clockMs()).Length;
            Volatile.Write(ref m_snapshot, None);
            return n;
        }
    }

    public IReadOnlyList<WindVolume> List(UUID creatorObject)
    {
        long now = m_clockMs();
        return Volatile.Read(ref m_snapshot)
            .Where(v => now < v.ExpiresAtMs && (creatorObject.IsZero() || v.CreatorObject == creatorObject))
            .ToArray();
    }

    public Vector3 Sample(Vector3 point) => Sample(point, m_clockMs());

    public Vector3 Sample(Vector3 point, long nowMs)
    {
        WindVolume[] volumes = Volatile.Read(ref m_snapshot);
        if (volumes.Length == 0) return Vector3.Zero;
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) return Vector3.Zero;

        float x = 0f, y = 0f, z = 0f;
        foreach (WindVolume v in volumes)
        {
            if (nowMs >= v.ExpiresAtMs) continue;
            float w = v.Weight(point, nowMs);
            if (w <= 0f) continue;
            x += v.Velocity.X * w;
            y += v.Velocity.Y * w;
            z += v.Velocity.Z * w;
        }

        var sum = new Vector3(x, y, z);
        float length = sum.Length();
        float max = Limits.MaxSpeed;
        if (length > max)
            sum = length > 0f && max > 0f ? sum * (max / length) : Vector3.Zero;
        return sum;
    }

    public void PruneExpired()
    {
        lock (m_writeLock)
        {
            long now = m_clockMs();
            Publish(Live(m_snapshot, now));

            // An object that has made no counted call for a whole window no longer needs a record.
            if (m_calls.Count > 0)
            {
                List<UUID> idle = null;
                foreach (KeyValuePair<UUID, CallWindow> entry in m_calls)
                {
                    Queue<long> times = entry.Value.Times;
                    if (times.Count == 0 || now - LastOf(times) >= ThrottleWindowMs)
                        (idle ??= new List<UUID>()).Add(entry.Key);
                }
                if (idle != null)
                    foreach (UUID id in idle)
                        m_calls.Remove(id);
            }
        }
    }

    private static long LastOf(Queue<long> times)
    {
        long last = long.MinValue;
        foreach (long t in times) last = t;
        return last;
    }

    // Counts one create or remove call of an object against its allowance for the last window, unless the allowance is
    // used up: then the call is refused and not counted, so an object that keeps trying gets its next call as soon as
    // its oldest counted call is a window old. Called under m_writeLock.
    private CallResult TakeCall(UUID creatorObject, long now)
    {
        int limit = Limits.CreateThrottlePerMinute;
        if (limit <= 0) return CallResult.Allowed;

        if (!m_calls.TryGetValue(creatorObject, out CallWindow window))
            m_calls[creatorObject] = window = new CallWindow();

        Queue<long> times = window.Times;
        while (times.Count > 0 && now - times.Peek() >= ThrottleWindowMs)
            times.Dequeue();

        if (times.Count >= limit)
        {
            if (window.Logged) return CallResult.Throttled;
            window.Logged = true;
            return CallResult.ThrottledFirst;
        }

        window.Logged = false;
        times.Enqueue(now);
        return CallResult.Allowed;
    }

    // Each placeholder appears once: the structured logger matches placeholders to values by position, not by number.
    private void LogThrottled(UUID creatorObject)
        => m_log.LogInformation(
            "[WEATHER] {0}: object {1} has made {2} wind volume create or remove calls in the last {3} s; further calls are refused until the oldest is {4} s old.",
            RegionName, creatorObject, Limits.CreateThrottlePerMinute, ThrottleWindowMs / 1000, ThrottleWindowMs / 1000);

    // Publishes the live array if it dropped any volume.
    private void Publish(WindVolume[] live)
    {
        if (live.Length != m_snapshot.Length)
            Volatile.Write(ref m_snapshot, live);
    }

    private int Validate(in WindVolumeSpec s)
    {
        if (s.Shape != WindVolumeShape.Sphere && s.Shape != WindVolumeShape.Cylinder)
            return WindVolumeError.InvalidParameter;
        if (!Finite(s.Center) || !Finite(s.Velocity) || !Finite(s.Drift)) return WindVolumeError.InvalidParameter;
        if (!float.IsFinite(s.Radius) || s.Radius <= 0f) return WindVolumeError.InvalidParameter;
        if (s.Shape == WindVolumeShape.Cylinder && (!float.IsFinite(s.Height) || s.Height <= 0f))
            return WindVolumeError.InvalidParameter;
        if (!float.IsFinite(s.FalloffStart) || s.FalloffStart < 0f || s.FalloffStart > 1f)
            return WindVolumeError.InvalidParameter;
        if (!float.IsFinite(s.LifetimeSeconds) || s.LifetimeSeconds <= 0f) return WindVolumeError.InvalidParameter;

        if (s.Radius > Limits.MaxRadius) return WindVolumeError.TooLarge;
        if (s.Shape == WindVolumeShape.Cylinder && s.Height > Limits.MaxHeight) return WindVolumeError.TooLarge;
        if (s.LifetimeSeconds > Limits.MaxLifetimeSeconds) return WindVolumeError.LifetimeTooLong;
        if (s.Velocity.Length() > Limits.MaxSpeed) return WindVolumeError.TooFast;
        if (s.Drift.Length() > Limits.MaxMoveSpeed) return WindVolumeError.DriftTooFast;

        // A volume may start outside the region (a storm that drifts in), but not wholly outside it: no further than
        // one radius past an edge, or half its height above the region's height or below 0. This is the same bound a
        // drifting volume stops at (WindVolume.CenterAt).
        float across = s.Radius;
        float up = s.Shape == WindVolumeShape.Sphere ? s.Radius : s.Height * 0.5f;
        Vector2 size = RegionSize;
        if (s.Center.X < -across || s.Center.X > size.X + across || s.Center.Y < -across || s.Center.Y > size.Y + across
            || s.Center.Z < -up || s.Center.Z > Constants.RegionHeight + up)
            return WindVolumeError.InvalidParameter;
        return WindVolumeError.Ok;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static WindVolume[] Live(WindVolume[] volumes, long now)
    {
        int expired = 0;
        foreach (WindVolume v in volumes)
            if (now >= v.ExpiresAtMs) expired++;
        return expired == 0 ? volumes : volumes.Where(v => now < v.ExpiresAtMs).ToArray();
    }

    private static int CountBy(WindVolume[] volumes, UUID creatorObject)
    {
        int n = 0;
        foreach (WindVolume v in volumes)
            if (v.CreatorObject == creatorObject) n++;
        return n;
    }

    // Handles count up from 1; if the counter ever wraps, a handle still in use is skipped.
    private int NextId(WindVolume[] live)
    {
        while (true)
        {
            m_lastId = m_lastId == int.MaxValue ? 1 : m_lastId + 1;
            int candidate = m_lastId;
            if (!live.Any(v => v.Id == candidate)) return candidate;
        }
    }
}
