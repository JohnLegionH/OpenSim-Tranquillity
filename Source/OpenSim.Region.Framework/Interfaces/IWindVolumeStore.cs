/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Region.Framework.Interfaces;

/// <summary>The shape of a wind volume.</summary>
public enum WindVolumeShape : int
{
    /// <summary>A ball: the radius is measured in three dimensions.</summary>
    Sphere = 0,

    /// <summary>An upright cylinder: the radius is horizontal, the height is the full vertical extent.</summary>
    Cylinder = 1,
}

/// <summary>
/// Results of the wind-volume calls. A create call returns a handle (1 or more) or one of these negative codes;
/// a remove call returns <see cref="Ok"/> or one of them.
/// </summary>
public static class WindVolumeError
{
    public const int Ok = 0;

    /// <summary>Weather is switched off in this region ([Weather] enabled = false).</summary>
    public const int Disabled = -1;

    /// <summary>The caller may not do this (a script whose owner is not an estate owner, estate manager or god).</summary>
    public const int NotAllowed = -2;

    /// <summary>A rule is missing, unknown, of the wrong type, not a finite number or out of its range.</summary>
    public const int InvalidParameter = -3;

    /// <summary>The radius or the height is above the configured maximum.</summary>
    public const int TooLarge = -4;

    /// <summary>The lifetime is above the configured maximum.</summary>
    public const int LifetimeTooLong = -5;

    /// <summary>The wind speed of the volume is above the configured maximum.</summary>
    public const int TooFast = -6;

    /// <summary>The region already holds the maximum number of volumes.</summary>
    public const int RegionFull = -7;

    /// <summary>The creating object already holds the maximum number of volumes.</summary>
    public const int ObjectFull = -8;

    /// <summary>No volume has that handle (or it has expired).</summary>
    public const int NotFound = -9;

    /// <summary>The drift speed of the volume is above the configured maximum.</summary>
    public const int DriftTooFast = -10;

    /// <summary>The creating object has already made or removed as many volumes as the region allows in a minute.</summary>
    public const int Throttled = -11;
}

/// <summary>What a script asks for when it creates a volume.</summary>
public readonly struct WindVolumeSpec
{
    public WindVolumeShape Shape { get; init; }

    /// <summary>The middle of the volume, in region coordinates.</summary>
    public Vector3 Center { get; init; }

    /// <summary>Metres. Horizontal for a cylinder, in all directions for a sphere.</summary>
    public float Radius { get; init; }

    /// <summary>Metres, the full vertical extent of a cylinder, centred on <see cref="Center"/>. Ignored for a sphere.</summary>
    public float Height { get; init; }

    /// <summary>
    /// The fraction of the radius, 0 to 1, inside which the wind is at full strength. From there it falls smoothly
    /// to nothing at the radius. 1 gives an edge with no falloff.
    /// </summary>
    public float FalloffStart { get; init; }

    /// <summary>Seconds from creation. Must be above 0.</summary>
    public float LifetimeSeconds { get; init; }

    /// <summary>The wind inside the volume at full strength, in metres per second. All three components count.</summary>
    public Vector3 Velocity { get; init; }

    /// <summary>
    /// How fast the volume itself moves, in metres per second, from <see cref="Center"/> at creation. Zero keeps it
    /// where it was made.
    /// </summary>
    public Vector3 Drift { get; init; }
}

/// <summary>
/// A volume as it is stored. Immutable: a reader that holds one never sees it change. A drifting volume's centre is
/// worked out from the time it is asked for (<see cref="CenterAt"/>), so it moves without any update step.
/// </summary>
public sealed class WindVolume
{
    /// <summary>The fraction of a cylinder's height, at its top and at its bottom, over which the wind falls to nothing.</summary>
    public const float VerticalFalloffFraction = 0.2f;

    public int Id { get; }
    public UUID CreatorObject { get; }
    public UUID Owner { get; }
    public WindVolumeShape Shape { get; }
    public Vector3 Center { get; }
    public float Radius { get; }
    public float Height { get; }
    public float FalloffStart { get; }
    public Vector3 Velocity { get; }

    /// <summary>Metres per second: how fast the centre moves from <see cref="Center"/>, where it was at creation.</summary>
    public Vector3 Drift { get; }

    /// <summary>When the volume was created, in the store's clock (milliseconds).</summary>
    public long CreatedMs { get; }

    /// <summary>The volume is gone at this time and after it (the store's clock, milliseconds).</summary>
    public long ExpiresAtMs { get; }

    // Where the centre stops on each axis, in the direction it drifts (see the constructor).
    private readonly Vector3 m_stop;

    /// <param name="regionSize">
    /// The region's size in metres (X and Y). A drifting centre stops once the volume is wholly outside the region: one
    /// radius past the edge it drifts toward, or half its height above <see cref="Constants.RegionHeight"/> or below 0.
    /// From there it adds no wind inside the region, and it lives on until its lifetime ends or it is removed. A volume
    /// made already beyond that point stays where it was made.
    /// </param>
    public WindVolume(int id, UUID creatorObject, UUID owner, in WindVolumeSpec spec, long createdMs, long expiresAtMs, Vector2 regionSize)
    {
        Id = id;
        CreatorObject = creatorObject;
        Owner = owner;
        Shape = spec.Shape;
        Center = spec.Center;
        Radius = spec.Radius;
        Height = spec.Shape == WindVolumeShape.Sphere ? spec.Radius * 2f : spec.Height;
        FalloffStart = spec.FalloffStart;
        Velocity = spec.Velocity;
        Drift = spec.Drift;
        CreatedMs = createdMs;
        ExpiresAtMs = expiresAtMs;

        float across = Radius;
        float up = Height * 0.5f;
        m_stop = new Vector3(
            Stop(Center.X, Drift.X, 0f, regionSize.X, across),
            Stop(Center.Y, Drift.Y, 0f, regionSize.Y, across),
            Stop(Center.Z, Drift.Z, 0f, Constants.RegionHeight, up));
    }

    // The coordinate where motion along one axis ends: the far side of the region plus the margin, in the direction of
    // the drift, but never behind where the volume started.
    private static float Stop(float start, float drift, float low, float high, float margin)
    {
        if (drift > 0f) return MathF.Max(start, high + margin);
        if (drift < 0f) return MathF.Min(start, low - margin);
        return start;
    }

    /// <summary>
    /// The centre at a time of the store's clock (milliseconds): where it was made, plus the drift times its age, each
    /// axis held where it stops (see the constructor). Takes no lock and allocates nothing.
    /// </summary>
    public Vector3 CenterAt(long nowMs)
    {
        long ageMs = nowMs - CreatedMs;
        if (ageMs <= 0 || Drift == Vector3.Zero) return Center;
        float age = ageMs * 0.001f;
        return new Vector3(
            Along(Center.X, Drift.X, m_stop.X, age),
            Along(Center.Y, Drift.Y, m_stop.Y, age),
            Along(Center.Z, Drift.Z, m_stop.Z, age));
    }

    private static float Along(float start, float drift, float stop, float age)
    {
        if (drift == 0f) return start;
        float at = start + drift * age;
        return drift > 0f ? MathF.Min(at, stop) : MathF.Max(at, stop);
    }

    /// <summary>As <see cref="Weight(Vector3)"/>, with the centre where it is at a time of the store's clock.</summary>
    public float Weight(Vector3 point, long nowMs) => WeightAround(CenterAt(nowMs), point);

    /// <summary>
    /// How much of the volume's wind reaches a point, with the centre where the volume was made: 1 at full strength,
    /// 0 outside, a smooth fall between. A point exactly on the radius, or on the top or bottom of a cylinder, gets 0.
    /// </summary>
    public float Weight(Vector3 point) => WeightAround(Center, point);

    private float WeightAround(Vector3 center, Vector3 point)
    {
        float dx = point.X - center.X;
        float dy = point.Y - center.Y;
        float dz = point.Z - center.Z;

        if (Shape == WindVolumeShape.Sphere)
            return Falloff(MathF.Sqrt(dx * dx + dy * dy + dz * dz), Radius, FalloffStart);

        float across = Falloff(MathF.Sqrt(dx * dx + dy * dy), Radius, FalloffStart);
        if (across <= 0f) return 0f;

        // Over the top and the bottom band of the cylinder the wind falls to nothing at the end face.
        float half = Height * 0.5f;
        float band = Height * VerticalFalloffFraction;
        float up = Falloff(MathF.Abs(dz), half, (half - band) / half);
        return across * up;
    }

    // 1 up to start x limit, then a smoothstep down to 0 at the limit; 0 at and beyond the limit.
    private static float Falloff(float distance, float limit, float start)
    {
        if (distance >= limit) return 0f;
        float full = start * limit;
        if (distance <= full) return 1f;
        float t = (distance - full) / (limit - full);
        return 1f - t * t * (3f - 2f * t);
    }
}

/// <summary>The caps on what a region's wind volumes may be. All come from the [Weather] section.</summary>
public sealed class WindVolumeLimits
{
    /// <summary>Volumes alive at one time in the region.</summary>
    public int MaxVolumesRegion { get; init; } = 16;

    /// <summary>Volumes alive at one time that one creating object holds.</summary>
    public int MaxVolumesObject { get; init; } = 4;

    /// <summary>Metres, the horizontal radius, and for a sphere the radius.</summary>
    public float MaxRadius { get; init; } = 128f;

    /// <summary>Metres, the vertical extent of a cylinder.</summary>
    public float MaxHeight { get; init; } = 512f;

    /// <summary>Metres per second: the longest velocity a volume may be given, and the clamp on the sum of all volumes at a point.</summary>
    public float MaxSpeed { get; init; } = 50f;

    /// <summary>Seconds.</summary>
    public float MaxLifetimeSeconds { get; init; } = 3600f;

    /// <summary>Metres per second: the fastest a volume may drift.</summary>
    public float MaxMoveSpeed { get; init; } = 40f;

    /// <summary>
    /// Create and remove calls one creating object may make in any 60 seconds, whatever their result; a call refused
    /// because of this limit does not count. Removals by the console and by an object leaving the region are not
    /// counted or limited. 0 sets no limit.
    /// </summary>
    public int CreateThrottlePerMinute { get; init; } = 10;
}

/// <summary>
/// A region's wind volumes: places where a script has added wind to the base field. They are held in memory only and
/// are gone when the region restarts. The region's wind module adds <see cref="Sample(Vector3)"/> to the wind it
/// reports through <see cref="IWindModule.WindSpeed(int,int,int)"/> and <see cref="IWindModule.WindSpeedAt"/>, which is
/// what llWind, iwWind and any physics engine that samples the wind read. The wind model's own cells and the wind
/// array sent to viewers do not include them.
/// </summary>
public interface IWindVolumeStore
{
    /// <summary>False when [Weather] enabled is false: the store then accepts nothing and answers as if empty.</summary>
    bool Enabled { get; }

    WindVolumeLimits Limits { get; }

    /// <summary>The number of volumes that have not expired.</summary>
    int Count { get; }

    /// <summary>
    /// Adds a volume. Returns its handle (1 or more) or a negative <see cref="WindVolumeError"/> code, and stores
    /// nothing on an error. A creating object past <see cref="WindVolumeLimits.CreateThrottlePerMinute"/> gets
    /// <see cref="WindVolumeError.Throttled"/>.
    /// </summary>
    /// <param name="creatorObject">The object whose script made the call; its volumes are counted and listed together.</param>
    /// <param name="owner">The owner of that object.</param>
    int Create(UUID creatorObject, UUID owner, in WindVolumeSpec spec);

    /// <summary>
    /// Removes a volume. With a creator object, only a volume that object made can be removed
    /// (<see cref="WindVolumeError.NotAllowed"/> otherwise), and the call counts against that object's
    /// <see cref="WindVolumeLimits.CreateThrottlePerMinute"/>; <see cref="UUID.Zero"/> removes any volume and is never
    /// limited.
    /// </summary>
    int Remove(int handle, UUID creatorObject);

    /// <summary>Removes every volume a creating object made, for instance when the object is deleted. Returns how many.</summary>
    int RemoveByCreator(UUID creatorObject);

    /// <summary>Removes every volume. Returns how many.</summary>
    int Clear();

    /// <summary>The volumes that have not expired; with a creating object, only those it made. Oldest first.</summary>
    IReadOnlyList<WindVolume> List(UUID creatorObject);

    /// <summary>
    /// The wind that all volumes together add at a point, region coordinates, now: each volume's velocity times its
    /// weight there (with a drifting volume's centre where it is now), summed, and the sum clamped to
    /// <see cref="WindVolumeLimits.MaxSpeed"/>. Never waits for a writer.
    /// </summary>
    Vector3 Sample(Vector3 point);

    /// <summary>The same at a given time of the store's clock (milliseconds).</summary>
    Vector3 Sample(Vector3 point, long nowMs);

    /// <summary>Drops the volumes that have expired. Sampling and listing already ignore them; this frees them.</summary>
    void PruneExpired();
}
