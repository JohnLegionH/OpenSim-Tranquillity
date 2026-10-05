/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// The physics harness: one scenario at one heartbeat rate, run through the Jolt module's own per-heartbeat
// step (JoltScene.Simulate: linkset and activation drains, the vehicle controllers, one backend step with the
// character step inside it, then the body, character and contact drains) with no OpenSim region around it.
//
// What stands in for the region:
// - the terrain is a heightmap handed to the scene's own SetTerrain, as the region's terrain module does;
// - a prim is added with AddPrimShape and set up through the PhysicsActor surface a SceneObjectPart uses
//   (Density, VehicleType, vehicle params); an avatar with AddAvatar and TargetVelocity / AvatarJump, as
//   ScenePresence does;
// - the heartbeat is a loop calling Simulate(1 / rate) with simulated time, so a run is faster than real time
//   and repeatable. The vehicle controller's clock is that simulated time, and a script or key input lands
//   half a heartbeat before the step that follows it, where it would land on average in a region.

using System.Globalization;
using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

/// <summary>What to run. Unset values take the scenario's defaults.</summary>
public sealed class HarnessOptions
{
    /// <summary>The environment variable that sets <see cref="PhysicsRateHz"/>'s default, so a whole test run can
    /// be repeated with physics steps on.</summary>
    public const string PhysicsRateVariable = "JOLT_HARNESS_PHYSICS_RATE";

    public double RateHz = 11.0;
    /// <summary>[Jolt] PhysicsStepRate: physics steps per second inside each heartbeat; 0 = one step per heartbeat.</summary>
    public double PhysicsRateHz = DefaultPhysicsRate();
    public float? SlopeDeg;
    public float? Duration;
    public float? Hold;
    /// <summary>How often a held key re-sends the motor, as a script's control event does while a key is down.</summary>
    public float KeyRepeat = 0.1f;
    /// <summary>[Jolt] keys, as an operator would set them in the region's config.</summary>
    public readonly Dictionary<string, string> Jolt = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Vehicle params applied after the scenario's own, as llSetVehicle*Param calls would be.</summary>
    public readonly List<VehicleParamSetting> VehicleParams = new();

    private static double DefaultPhysicsRate()
    {
        string v = Environment.GetEnvironmentVariable(PhysicsRateVariable);
        return v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) && r >= 0 ? r : 0.0;
    }
}

/// <summary>One vehicle param: a float when <see cref="IsVector"/> is false (the value is X).</summary>
public readonly record struct VehicleParamSetting(Vehicle Code, Vector3 Value, bool IsVector)
{
    /// <summary>Parses NAME=v or NAME=x,y,z; NAME is an LSL VEHICLE_* name with or without the prefix.</summary>
    public static VehicleParamSetting Parse(string text)
    {
        int eq = text.IndexOf('=');
        if (eq <= 0)
            throw new ArgumentException($"vehicle param '{text}': expected NAME=value or NAME=x,y,z");
        string name = text[..eq].Trim().ToUpperInvariant();
        if (name.StartsWith("VEHICLE_", StringComparison.Ordinal))
            name = name["VEHICLE_".Length..];
        if (!Enum.TryParse(name, out Vehicle code) || (int)code < 16)
            throw new ArgumentException($"vehicle param '{text}': unknown parameter name");
        string[] parts = text[(eq + 1)..].Split(',');
        var f = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            f[i] = float.Parse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture);
        if (parts.Length == 1)
            return new VehicleParamSetting(code, new Vector3(f[0], 0f, 0f), false);
        if (parts.Length == 3)
            return new VehicleParamSetting(code, new Vector3(f[0], f[1], f[2]), true);
        throw new ArgumentException($"vehicle param '{text}': expected one value or three");
    }
}

/// <summary>One heartbeat's state of the scenario's object, read after the step.</summary>
public readonly record struct Sample(double T, Vector3 Position, Vector3 Velocity, float Tilt, float Height)
{
    public float Speed => Velocity.Length();
    public float HorizontalSpeed => MathF.Sqrt(Velocity.X * Velocity.X + Velocity.Y * Velocity.Y);
}

/// <summary>The figures a run is judged by. NaN means "does not apply" or "did not happen".</summary>
public sealed class Summary
{
    public float TopSpeed;
    public double ReleaseT = double.NaN;
    public float ReleaseSpeed = float.NaN;
    /// <summary>Mean speed over the scenario's steady window (see <see cref="Scenario.Steady"/>).</summary>
    public float SteadySpeed = float.NaN;
    public float SteadyHorizontalSpeed = float.NaN;
    /// <summary>Horizontal distance from the start to the release.</summary>
    public float DistanceBeforeRelease;
    /// <summary>Horizontal distance from the release to the end of the run.</summary>
    public float DistanceAfterRelease;
    /// <summary>Seconds after the release at which the object came to rest (speed under 0.1 m/s for 1 s).</summary>
    public double TimeToRest = double.NaN;
    /// <summary>Highest centre height above the terrain under it.</summary>
    public float PeakHeight;
    /// <summary>Highest rise of the centre above where it was at the release.</summary>
    public float PeakRise;
    /// <summary>Height range of the centre after the first second (a standing avatar's bob).</summary>
    public float ZRange;
    public float MaxTilt;
    public Vector3 End;
    /// <summary>When the object crossed the region's edge, which ends the run (there is no ground beyond it).</summary>
    public double LeftRegionT = double.NaN;
    public int Steps;
    public int NonFinite;
}

public sealed class RunResult
{
    public string Scenario;
    public double RateHz;
    public double PhysicsRateHz;
    public float SlopeDeg;
    public readonly List<Sample> Samples = new();
    public Summary Summary;

    public string Name => $"{Scenario}-s{Fmt(SlopeDeg, "0.#")}-r{Fmt(RateHz, "0.#")}{(PhysicsRateHz > 0 ? "-p" + Fmt(PhysicsRateHz, "0.#") : "")}";

    /// <summary>The scenario column of the summary: the name, plus "/p" and the physics rate when physics steps are on.</summary>
    public string Label => PhysicsRateHz > 0 ? $"{Scenario}/p{Fmt(PhysicsRateHz, "0.#")}" : Scenario;

    public const string CsvHeader = "t,x,y,z,vx,vy,vz,speed,hspeed,tilt_deg,height";

    /// <summary>The trace as CSV: one line per heartbeat, invariant culture, fixed decimals.</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.Append(CsvHeader).Append('\n');
        foreach (Sample s in Samples)
        {
            sb.Append(Fmt(s.T, "0.0000")).Append(',')
              .Append(Fmt(s.Position.X, "0.0000")).Append(',').Append(Fmt(s.Position.Y, "0.0000")).Append(',').Append(Fmt(s.Position.Z, "0.0000")).Append(',')
              .Append(Fmt(s.Velocity.X, "0.0000")).Append(',').Append(Fmt(s.Velocity.Y, "0.0000")).Append(',').Append(Fmt(s.Velocity.Z, "0.0000")).Append(',')
              .Append(Fmt(s.Speed, "0.0000")).Append(',').Append(Fmt(s.HorizontalSpeed, "0.0000")).Append(',')
              .Append(Fmt(s.Tilt, "0.00")).Append(',').Append(Fmt(s.Height, "0.0000")).Append('\n');
        }
        return sb.ToString();
    }

    public const string SummaryHeader =
        "scenario,slope_deg,rate_hz,steps,top_speed,release_t,release_speed,steady_speed,steady_hspeed,dist_before_release,dist_after_release,time_to_rest,peak_height,peak_rise,z_range,max_tilt_deg,end_x,end_y,end_z,left_region_t,nonfinite";

    public string SummaryLine()
    {
        Summary m = Summary;
        return string.Join(",",
            Label, Fmt(SlopeDeg, "0.#"), Fmt(RateHz, "0.#"), m.Steps.ToString(CultureInfo.InvariantCulture),
            Fmt(m.TopSpeed, "0.000"), Fmt(m.ReleaseT, "0.000"), Fmt(m.ReleaseSpeed, "0.000"),
            Fmt(m.SteadySpeed, "0.000"), Fmt(m.SteadyHorizontalSpeed, "0.000"),
            Fmt(m.DistanceBeforeRelease, "0.000"), Fmt(m.DistanceAfterRelease, "0.000"), Fmt(m.TimeToRest, "0.000"),
            Fmt(m.PeakHeight, "0.000"), Fmt(m.PeakRise, "0.000"), Fmt(m.ZRange, "0.000"), Fmt(m.MaxTilt, "0.0"),
            Fmt(m.End.X, "0.000"), Fmt(m.End.Y, "0.000"), Fmt(m.End.Z, "0.000"), Fmt(m.LeftRegionT, "0.000"), m.NonFinite.ToString(CultureInfo.InvariantCulture));
    }

    internal static string Fmt(double v, string format)
        => double.IsNaN(v) ? "-" : v.ToString(format, CultureInfo.InvariantCulture);
}

/// <summary>
/// The test ground. A 256 m region with level ground at 25 m and water at 20 m, like a simple physics test
/// course. With a slope, the ground rises northward from y 40 to y 100 at that angle and is level again
/// beyond (the whole width of the region, so nothing can roll off a side).
/// </summary>
public static class Course
{
    public const int Size = 256;
    public const float Ground = 25f;
    public const float Water = 20f;
    public const float RampBottomY = 40f;
    public const float RampTopY = 100f;

    public static float HeightAt(float y, float slopeDeg)
    {
        if (slopeDeg <= 0f || y <= RampBottomY)
            return Ground;
        float tan = MathF.Tan(slopeDeg * MathF.PI / 180f);
        return Ground + (MathF.Min(y, RampTopY) - RampBottomY) * tan;
    }

    public static float[] Heightmap(float slopeDeg, float level = Ground)
    {
        var h = new float[Size * Size];
        for (int y = 0; y < Size; y++)
        {
            float z = slopeDeg > 0f ? HeightAt(y, slopeDeg) : level;
            for (int x = 0; x < Size; x++)
                h[y * Size + x] = z;
        }
        return h;
    }
}

/// <summary>A running scenario: the scene, the object under test and the simulated clock.</summary>
public sealed class Run
{
    internal JoltScene Scene;
    public PhysicsActor Actor;
    public Vector3 ActorSize;
    public HarnessOptions Options;
    public float Slope;
    public float Duration;
    public float Hold;
    public double Dt;
    /// <summary>Time of the heartbeat about to run (inputs read this).</summary>
    public double Now;
    /// <summary>When the key is let go, the jump is pressed or the object is let fall; NaN if never.</summary>
    public double ReleaseAt = double.NaN;
    /// <summary>Stop once the object has come to rest after the release.</summary>
    public bool StopAtRest;
    /// <summary>Stop after the sample at this time; NaN = run the whole duration.</summary>
    public double StopAt = double.NaN;
    internal double NextKey;
    internal bool Released;

    public static readonly Vector3 AvatarSize = new(0.45f, 0.6f, 1.9f);   // the default appearance's box
    private const uint ActorLocalId = 1000;

    public float GroundAt(float x, float y) => Scene.TerrainHeightAt(x, y);

    /// <summary>A physical box prim, as a SceneObjectPart adds one (density 1000).</summary>
    public PhysicsActor AddBox(Vector3 size, Vector3 position, Quaternion rotation)
    {
        PhysicsActor pa = Scene.AddPrimShape("harness box", PrimitiveBaseShape.CreateBox(), position, size, rotation, true, ActorLocalId);
        pa.Density = 1000f;
        Actor = pa;
        ActorSize = size;
        return pa;
    }

    /// <summary>A box on the ground at (x, y), its base lifted clear of a slope under its length.</summary>
    public PhysicsActor AddBoxOnGround(Vector3 size, float x, float y, Quaternion rotation)
    {
        float lift = Slope > 0f ? MathF.Max(size.X, size.Y) * 0.5f * MathF.Tan(Slope * MathF.PI / 180f) : 0f;
        return AddBox(size, new Vector3(x, y, GroundAt(x, y) + size.Z * 0.5f + lift + 0.02f), rotation);
    }

    public PhysicsActor AddAvatar(float x, float y)
    {
        // CreateAvatar seats the capsule on the terrain at (x, y), as at login.
        PhysicsActor pa = Scene.AddAvatar(ActorLocalId, "Test User", new Vector3(x, y, GroundAt(x, y) + 2f), AvatarSize, 0f, false);
        Actor = pa;
        ActorSize = AvatarSize;
        return pa;
    }

    public void MakeVehicle(Vehicle type)
    {
        Actor.VehicleType = (int)type;
    }

    public void SetFloat(Vehicle code, float v) => Actor.VehicleFloatParam((int)code, v);
    public void SetVector(Vehicle code, Vector3 v) => Actor.VehicleVectorParam((int)code, v);

    /// <summary>The motor while a key is held: re-sent every KeyRepeat seconds until the hold ends, then zeroed once.</summary>
    public void HoldMotor(Vector3 motor)
    {
        if (Released)
            return;
        if (Now >= Hold)
        {
            SetVector(Vehicle.LINEAR_MOTOR_DIRECTION, Vector3.Zero);
            Released = true;
            return;
        }
        if (Now >= NextKey)
        {
            SetVector(Vehicle.LINEAR_MOTOR_DIRECTION, motor);
            NextKey += Options.KeyRepeat;
        }
    }

    public void ApplyVehicleOverrides()
    {
        foreach (VehicleParamSetting p in Options.VehicleParams)
        {
            if (p.IsVector) Actor.VehicleVectorParam((int)p.Code, p.Value);
            else Actor.VehicleFloatParam((int)p.Code, p.Value.X);
        }
    }
}

/// <summary>A named, self-contained scenario.</summary>
public sealed class Scenario
{
    public string Name;
    public string Description;
    /// <summary>Slopes "all" runs this scenario on; a single 0 means it ignores the slope.</summary>
    public float[] Slopes = { 0f };
    public bool UsesSlope;
    public Func<float, float> DefaultDuration;
    public Func<float, float> DefaultHold = _ => 0f;
    /// <summary>Builds the terrain heightmap and water for a slope.</summary>
    public Func<float, (float[] heights, float water)> World = s => (Course.Heightmap(s), Course.Water);
    public Action<Run> Setup;
    /// <summary>Called before every heartbeat, with Run.Now set to the input time.</summary>
    public Action<Run> Input = _ => { };
    /// <summary>Which samples make up the steady window.</summary>
    public Func<Run, Sample, bool> Steady;
    /// <summary>Instead of <see cref="Steady"/>: the steady speeds (along the path, horizontal) from positions.</summary>
    public Func<Run, List<Sample>, (float along, float horizontal)?> SteadyFromPath;
}

public static class Harness
{
    public static readonly double[] Rates = { 11.0, 22.5, 45.0, 90.0 };

    private static readonly Quaternion South = Quaternion.CreateFromEulers(0f, 0f, -MathF.PI / 2f);
    private static readonly Quaternion North = Quaternion.CreateFromEulers(0f, 0f, MathF.PI / 2f);
    private const float AvatarWalk = 4.096f;   // ScenePresence.AgentControlNormalVel at speed modifier 1

    // The test car: VEHICLE_TYPE_CAR with a test-drive script's three params, motor <8,0,0> while the key is held.
    private static readonly Vector3 CarSize = new(2f, 1f, 0.5f);
    private static readonly Vector3 CarMotor = new(8f, 0f, 0f);

    private static void SetupCar(Run r, bool testCar)
    {
        if (r.Slope > 0f)
            r.AddBoxOnGround(CarSize, 128f, 120f, South);   // top run-out, facing down the slope
        else
            r.AddBoxOnGround(CarSize, 165f, 85f, Quaternion.Identity);   // level ground, facing east
        r.MakeVehicle(Vehicle.TYPE_CAR);
        if (testCar)
        {
            r.SetVector(Vehicle.LINEAR_FRICTION_TIMESCALE, new Vector3(1f, 1f, 1000f));
            r.SetFloat(Vehicle.LINEAR_MOTOR_TIMESCALE, 1f);
            r.SetFloat(Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE, 0.5f);
        }
        r.ApplyVehicleOverrides();
        r.ReleaseAt = r.Hold;
        r.StopAtRest = true;
    }

    // On a slope: the lower part of the ramp, a second after the release. On level ground: the last second of the hold.
    private static bool DriveSteady(Run r, Sample s)
        => r.Slope > 0f
            ? s.T >= r.ReleaseAt + 1.0 && s.Position.Y >= 45f && s.Position.Y <= 70f
            : s.T > r.ReleaseAt - 1.0 && s.T <= r.ReleaseAt;

    private static bool LastSeconds(Run r, Sample s, double seconds) => s.T > r.Duration - seconds;

    // Walking speed from positions, as the course's walk runs measured it: on a slope the time from y 40 to
    // y 100 gives the horizontal speed, and along the slope is that over cos(angle); on level ground the
    // distance covered from t = 1 s to the end. The avatar's reported velocity is the walk it was asked for,
    // not its motion over the ground, so it is not used here.
    private static (float along, float horizontal)? WalkSpeed(Run r, List<Sample> samples)
    {
        if (r.Slope <= 0f)
        {
            int i = samples.FindIndex(s => s.T >= 1.0);
            if (i < 0 || samples[^1].T <= samples[i].T)
                return null;
            float d = Horizontal(samples[^1].Position, samples[i].Position);
            float v = (float)(d / (samples[^1].T - samples[i].T));
            return (v, v);
        }
        double t40 = CrossingY(samples, Course.RampBottomY), t100 = CrossingY(samples, Course.RampTopY);
        if (double.IsNaN(t40) || double.IsNaN(t100) || t100 <= t40)
            return null;
        float h = (float)((Course.RampTopY - Course.RampBottomY) / (t100 - t40));
        return (h / MathF.Cos(r.Slope * MathF.PI / 180f), h);
    }

    /// <summary>Interpolated time at which y first reaches the given value going north; NaN if never.</summary>
    public static double CrossingY(List<Sample> samples, float y)
    {
        for (int i = 1; i < samples.Count; i++)
        {
            Sample a = samples[i - 1], b = samples[i];
            if (a.Position.Y < y && b.Position.Y >= y)
                return a.T + (b.T - a.T) * (y - a.Position.Y) / (b.Position.Y - a.Position.Y);
        }
        return double.NaN;
    }

    public static readonly IReadOnlyList<Scenario> Scenarios = new List<Scenario>
    {
        new()
        {
            Name = "car",
            Description = "VEHICLE_TYPE_CAR with its presets only; motor <8,0,0> while the key is held, then released. Level ground: 3 s from rest facing east. Slope: 6 s from the top facing down.",
            Slopes = new[] { 0f, 5f, 15f, 33f }, UsesSlope = true,
            DefaultDuration = s => s > 0f ? 90f : 20f, DefaultHold = s => s > 0f ? 6f : 3f,
            Setup = r => SetupCar(r, false),
            Input = r => r.HoldMotor(CarMotor),
            Steady = DriveSteady,
        },
        new()
        {
            Name = "testcar",
            Description = "A typical test-drive script's car: as car, plus linear friction timescale <1,1,1000>, motor timescale 1, motor decay 0.5.",
            Slopes = new[] { 0f, 5f, 15f, 33f }, UsesSlope = true,
            DefaultDuration = s => s > 0f ? 90f : 20f, DefaultHold = s => s > 0f ? 6f : 3f,
            Setup = r => SetupCar(r, true),
            Input = r => r.HoldMotor(CarMotor),
            Steady = DriveSteady,
        },
        new()
        {
            Name = "sled",
            Description = "VEHICLE_TYPE_SLED with its presets, let go at rest on the slope facing down; no motor.",
            Slopes = new[] { 15f }, UsesSlope = true,
            DefaultDuration = _ => 40f,
            Setup = r =>
            {
                r.AddBoxOnGround(CarSize, 128f, 90f, South);
                r.MakeVehicle(Vehicle.TYPE_SLED);
                r.ApplyVehicleOverrides();
                r.ReleaseAt = 0;
                r.StopAtRest = true;
            },
            Steady = DriveSteady,
        },
        new()
        {
            Name = "boat",
            Description = "VEHICLE_TYPE_BOAT with its presets on 10 m of water; motor <5,0,0> for 5 s, then released.",
            DefaultDuration = _ => 30f, DefaultHold = _ => 5f,
            World = _ => (Course.Heightmap(0f, 10f), Course.Water),
            Setup = r =>
            {
                r.AddBox(new Vector3(3f, 1.5f, 0.5f), new Vector3(60f, 128f, Course.Water + 0.5f), Quaternion.Identity);
                r.MakeVehicle(Vehicle.TYPE_BOAT);
                r.ApplyVehicleOverrides();
                r.ReleaseAt = r.Hold;
                r.StopAtRest = true;
            },
            Input = r => r.HoldMotor(new Vector3(5f, 0f, 0f)),
            Steady = (r, s) => s.T > r.ReleaseAt - 1.0 && s.T <= r.ReleaseAt,
        },
        new()
        {
            Name = "airplane",
            Description = "VEHICLE_TYPE_AIRPLANE with its presets, 40 m above level ground, motor <15,0,0> held for the whole run.",
            DefaultDuration = _ => 15f, DefaultHold = s => 1000f,
            Setup = r =>
            {
                r.AddBox(new Vector3(3f, 3f, 0.5f), new Vector3(30f, 128f, Course.Ground + 40f), Quaternion.Identity);
                r.MakeVehicle(Vehicle.TYPE_AIRPLANE);
                r.ApplyVehicleOverrides();
            },
            Input = r => r.HoldMotor(new Vector3(15f, 0f, 0f)),
            Steady = (r, s) => LastSeconds(r, s, 2.0),
        },
        new()
        {
            Name = "balloon",
            Description = "VEHICLE_TYPE_BALLOON with its presets (hover 5 m), from rest on level ground; no motor.",
            DefaultDuration = _ => 40f,
            Setup = r =>
            {
                r.AddBoxOnGround(new Vector3(1f, 1f, 1f), 128f, 128f, Quaternion.Identity);
                r.MakeVehicle(Vehicle.TYPE_BALLOON);
                r.ApplyVehicleOverrides();
            },
            Steady = (r, s) => LastSeconds(r, s, 2.0),
        },
        new()
        {
            Name = "avatar-stand",
            Description = "An avatar standing still for 10 s on the slope (y 70).",
            Slopes = new[] { 5f, 15f }, UsesSlope = true,
            DefaultDuration = _ => 10f,
            Setup = r => r.AddAvatar(128f, 70f),
            Steady = (r, s) => s.T >= 1.0,
        },
        new()
        {
            Name = "avatar-walk",
            Description = "An avatar walking at the scene's walk speed (4.096 m/s asked). Level ground: east for 5 s. Slope: north from y 24 up the ramp to y 110.",
            Slopes = new[] { 0f, 5f, 15f, 33f }, UsesSlope = true,
            DefaultDuration = s => s > 0f ? 40f : 5f,
            Setup = r =>
            {
                if (r.Slope > 0f) r.AddAvatar(128f, 24f);
                else r.AddAvatar(170f, 60f);
                r.Actor.TargetVelocity = r.Slope > 0f ? new Vector3(0f, AvatarWalk, 0f) : new Vector3(AvatarWalk, 0f, 0f);
            },
            Input = r =>
            {
                if (r.Slope > 0f && r.Actor.Position.Y >= 110f && r.Actor.TargetVelocity != Vector3.Zero)
                {
                    r.Actor.TargetVelocity = Vector3.Zero;   // at the top: stop, and end the run a second later
                    r.ReleaseAt = r.Now;
                    r.StopAt = r.Now + 1.0;
                }
            },
            SteadyFromPath = WalkSpeed,
        },
        new()
        {
            Name = "avatar-jump",
            Description = "An avatar standing on level ground jumps at t = 1 s.",
            DefaultDuration = _ => 4f,
            Setup = r =>
            {
                r.AddAvatar(170f, 60f);
                r.ReleaseAt = 1.0;
            },
            Input = r =>
            {
                if (!r.Released && r.Now >= r.ReleaseAt)
                {
                    r.Actor.AvatarJump(0f);
                    r.Released = true;
                }
            },
        },
        new()
        {
            Name = "drop",
            Description = "A 1 m physical box let fall from 5 m above level ground, until it settles.",
            DefaultDuration = _ => 15f,
            Setup = r =>
            {
                r.AddBox(new Vector3(1f, 1f, 1f), new Vector3(128f, 128f, Course.Ground + 5f + 0.5f), Quaternion.Identity);
                r.ReleaseAt = 0;
                r.StopAtRest = true;
            },
        },
    };

    public static Scenario Find(string name)
        => Scenarios.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
           ?? throw new ArgumentException($"unknown scenario '{name}'; known: {string.Join(", ", Scenarios.Select(s => s.Name))}");

    private const float RestSpeed = 0.1f;
    private const double RestSeconds = 1.0;
    private static readonly DateTime ClockEpoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Runs one scenario at one rate and slope. Writes nothing; the caller decides where output goes.</summary>
    public static RunResult Run(Scenario sc, HarnessOptions o)
    {
        float slope = sc.UsesSlope ? (o.SlopeDeg ?? sc.Slopes[0]) : 0f;
        var r = new Run
        {
            Options = o,
            Slope = slope,
            Duration = o.Duration ?? sc.DefaultDuration(slope),
            Hold = o.Hold ?? sc.DefaultHold(slope),
            Dt = 1.0 / o.RateHz,
        };

        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        foreach (KeyValuePair<string, string> kv in o.Jolt)
            jolt.Set(kv.Key, kv.Value);
        if (o.PhysicsRateHz > 0)
            jolt.Set("PhysicsStepRate", o.PhysicsRateHz.ToString(CultureInfo.InvariantCulture));

        var scene = new JoltScene();
        r.Scene = scene;
        scene.Initialise(config);
        double clock = 0;
        scene.VehicleClock = () => ClockEpoch.AddTicks((long)Math.Round(clock * TimeSpan.TicksPerSecond));
        (float[] heights, float water) = sc.World(slope);
        scene.InitialiseWithoutScene("Harness", Course.Size, Course.Size, heights, water, (float)r.Dt);

        var result = new RunResult { Scenario = sc.Name, RateHz = o.RateHz, PhysicsRateHz = scene.Substepping ? scene.Substeps.RateHz : 0, SlopeDeg = slope };
        try
        {
            clock = -r.Dt * 0.5;
            sc.Setup(r);
            float dt = (float)r.Dt;
            int frames = (int)Math.Ceiling(r.Duration / r.Dt - 1e-9);
            double restCandidate = double.NaN;
            bool movedSinceRelease = false;
            double leftAt = double.NaN;
            for (int k = 0; k < frames; k++)
            {
                // Inputs land half a heartbeat before the step; the step runs at the heartbeat's time.
                r.Now = k * r.Dt;
                clock = r.Now - r.Dt * 0.5;
                sc.Input(r);
                clock = r.Now;
                scene.Simulate(dt);

                Sample s = Read(r, (k + 1) * r.Dt);
                result.Samples.Add(s);
                if (s.T >= r.StopAt - 1e-9)
                    break;
                if (!InRegion(s.Position))
                {
                    leftAt = s.T;
                    break;
                }

                if (!double.IsNaN(r.ReleaseAt) && s.T > r.ReleaseAt)
                {
                    if (s.Speed > 0.5f) movedSinceRelease = true;
                    if (movedSinceRelease && s.Speed < RestSpeed)
                    {
                        if (double.IsNaN(restCandidate)) restCandidate = s.T;
                        if (r.StopAtRest && s.T - restCandidate >= RestSeconds - 1e-9)
                            break;
                    }
                    else
                        restCandidate = double.NaN;
                }
            }
            result.Summary = Summarise(r, sc, result.Samples);
            result.Summary.LeftRegionT = leftAt;
        }
        finally
        {
            scene.Dispose();
        }
        return result;
    }

    private static Sample Read(Run r, double t)
    {
        PhysicsActor a = r.Actor;
        Vector3 p = a.Position;
        Vector3 v = a.Velocity;
        Vector3 up = Vector3.UnitZ * a.Orientation;
        float tilt = MathF.Acos(Math.Clamp(up.Z, -1f, 1f)) * 180f / MathF.PI;
        float height = p.Z - r.GroundAt(p.X, p.Y);
        return new Sample(t, p, v, tilt, height);
    }

    private static bool InRegion(Vector3 p) => p.X >= 0f && p.Y >= 0f && p.X <= Course.Size && p.Y <= Course.Size;

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static float Horizontal(Vector3 a, Vector3 b)
        => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    internal static Summary Summarise(Run r, Scenario sc, List<Sample> samples)
    {
        var m = new Summary { Steps = samples.Count };
        if (samples.Count == 0)
            return m;
        Vector3 start = samples[0].Position;
        Vector3 atRelease = start;
        double rel = r.ReleaseAt;
        bool haveRelease = !double.IsNaN(rel);
        float minZ = float.MaxValue, maxZ = float.MinValue;
        double steadySum = 0, steadyHSum = 0;
        int steadyN = 0;
        double restCandidate = double.NaN;
        bool moved = false;
        float relZ = float.NaN;

        foreach (Sample s in samples)
        {
            if (!Finite(s.Position) || !Finite(s.Velocity)) { m.NonFinite++; continue; }
            m.TopSpeed = MathF.Max(m.TopSpeed, s.Speed);
            m.PeakHeight = MathF.Max(m.PeakHeight, s.Height);
            m.MaxTilt = MathF.Max(m.MaxTilt, s.Tilt);
            if (s.T >= 1.0) { minZ = MathF.Min(minZ, s.Position.Z); maxZ = MathF.Max(maxZ, s.Position.Z); }
            if (sc.Steady != null && sc.Steady(r, s)) { steadySum += s.Speed; steadyHSum += s.HorizontalSpeed; steadyN++; }

            if (!haveRelease)
                continue;
            if (s.T <= rel + 1e-9)
            {
                atRelease = s.Position;
                m.ReleaseT = rel;
                m.ReleaseSpeed = s.Speed;
                relZ = s.Position.Z;
                continue;
            }
            if (float.IsNaN(relZ)) { relZ = start.Z; m.ReleaseT = rel; m.ReleaseSpeed = 0f; }
            m.PeakRise = MathF.Max(m.PeakRise, s.Position.Z - relZ);
            if (s.Speed > 0.5f) moved = true;
            if (moved && s.Speed < RestSpeed)
            {
                if (double.IsNaN(restCandidate)) restCandidate = s.T;
                if (double.IsNaN(m.TimeToRest) && s.T - restCandidate >= RestSeconds - 1e-9)
                    m.TimeToRest = restCandidate - rel;
            }
            else if (double.IsNaN(m.TimeToRest))
                restCandidate = double.NaN;
        }

        Sample last = samples[^1];
        m.End = last.Position;
        m.DistanceBeforeRelease = haveRelease ? Horizontal(atRelease, start) : Horizontal(last.Position, start);
        m.DistanceAfterRelease = haveRelease ? Horizontal(last.Position, atRelease) : float.NaN;
        if (steadyN > 0) { m.SteadySpeed = (float)(steadySum / steadyN); m.SteadyHorizontalSpeed = (float)(steadyHSum / steadyN); }
        if (sc.SteadyFromPath != null && sc.SteadyFromPath(r, samples) is (float along, float horizontal))
        {
            m.SteadySpeed = along;
            m.SteadyHorizontalSpeed = horizontal;
        }
        m.ZRange = maxZ >= minZ ? maxZ - minZ : 0f;
        return m;
    }
}
