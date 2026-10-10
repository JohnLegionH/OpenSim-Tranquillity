/* Copyright (c) Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Dragging a physical object with the mouse, through core's own drag path against a Jolt scene.
//
// What the viewer sends: ObjectGrab when the button goes down, then an ObjectGrabUpdate whenever the mouse moves
// (LLToolGrabBase::handleHoverActive in the viewer's lltoolgrab.cpp sends only when the mouse delta is not zero, with no
// timer), carrying the point under the cursor in region coordinates as GrabPosition, and ObjectDeGrab on release.
// What core does with an update on a physical object: SceneObjectGroup.GrabMovement adds one force,
// (GrabPosition - the root's position) x mass x 0.1, through PhysicsActor.AddForce with pushforce false, to the root's
// actor. It does nothing to the actor on ObjectDeGrab (Scene.ProcessObjectDeGrab only raises the touch_end events).
//
// These scenarios call SceneObjectGroup.GrabMovement itself, on a group whose root part holds the Jolt actor, so the force
// is the one core asks for. Nothing is tuned: they record what happens today, and the tests built on them assert only
// that the run finishes and nothing is not-a-number. The figures a run gives are in <see cref="GrabMeasure"/>.
//
// Modelling choices, each of them a choice and not a measurement of the viewer:
// - The viewer's update rate is <see cref="ViewerHz"/> while the mouse moves; the updates that fall between two
//   heartbeats all reach the actor before the step, as packets read between two region heartbeats do.
// - The cursor follows a smooth start and stop (smoothstep) from the object's starting place to the target.
// - The cursor sits at the root's centre, so the grabbed point and the root's position agree. (A viewer sends the point
//   under the cursor on the surface; core subtracts the root's position from it and ignores GrabOffsetInitial.)
// - While the object is held at the target the viewer is taken to keep sending updates at the same rate
//   (<see cref="GrabPlan.UpdatesWhileHeld"/>), as a hand never holds a mouse perfectly still.

using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

/// <summary>The cursor's course and when the updates stop, for one drag.</summary>
public sealed class GrabPlan
{
    public string Kind;
    /// <summary>Where the object's root starts, and the way and distance the cursor takes it.</summary>
    public Vector3 Start;
    public Vector3 Direction;
    public float Distance;
    /// <summary>The drag starts at <see cref="DragStart"/>, takes <see cref="DragSeconds"/>, and the cursor then holds.</summary>
    public double DragStart = 0.5;
    public double DragSeconds = 2.0;
    /// <summary>The button is let go at this time (NaN = never, the drag is held to the end of the run).</summary>
    public double ReleaseAt = double.NaN;
    public bool UpdatesWhileHeld = true;
    /// <summary>The cursor's offset from the root's centre (a linkset grabbed by a child).</summary>
    public Vector3 CursorOffset;
    /// <summary>A fixed wall or a second physical box lies along the way.</summary>
    public float Mass;
    public bool Obstacle;
    public bool ObstaclePhysical;
    /// <summary>The next update's number (updates are numbered from the start of the run, at j / ViewerHz).</summary>
    internal int NextUpdate;
    /// <summary>How many ObjectGrabUpdates reached core's GrabMovement.</summary>
    public int Sent;
    internal CoreGrab Core;

    public double DragEnd => DragStart + DragSeconds;

    /// <summary>Where the object's centre would be if it followed the cursor exactly, at time t.</summary>
    public Vector3 Ideal(double t)
    {
        double s = Math.Clamp((t - DragStart) / DragSeconds, 0.0, 1.0);
        float smooth = (float)(s * s * (3.0 - 2.0 * s));
        return Start + Direction * Distance * smooth;
    }

    public Vector3 Target => Start + Direction * Distance;
}

/// <summary>A scene object group around the Jolt actor, so core's GrabMovement runs on it as it does in a region.</summary>
internal sealed class CoreGrab
{
    private static readonly FieldInfo SceneField = typeof(EntityBase).GetField("m_scene", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo EventsField = typeof(SceneBase).GetField("m_eventManager", BindingFlags.NonPublic | BindingFlags.Instance);

    public readonly SceneObjectGroup Group;
    public readonly SceneObjectPart Root;
    private readonly List<SceneObjectPart> _parts = new();

    /// <summary>The group of the root part holding <paramref name="rootActor"/>, with one more part for each of
    /// <paramref name="childActors"/>. The group's scene is a bare one with an event manager: GrabMovement reaches nothing
    /// else of it when the root's actor is physical.</summary>
    public CoreGrab(PhysicsActor rootActor, params PhysicsActor[] childActors)
    {
        Root = NewPart("grab root", rootActor);
        Group = new SceneObjectGroup(Root);
        foreach (PhysicsActor child in childActors)
        {
            SceneObjectPart part = NewPart("grab child", child);
            Group.AddPart(part);
        }
        Scene scene = (Scene)RuntimeHelpers.GetUninitializedObject(typeof(Scene));
        EventsField.SetValue(scene, new EventManager());
        SceneField.SetValue(Group, scene);
    }

    private SceneObjectPart NewPart(string name, PhysicsActor actor)
    {
        var part = new SceneObjectPart { Name = name, UUID = UUID.Random(), Shape = PrimitiveBaseShape.CreateBox() };
        part.PhysActor = actor;
        _parts.Add(part);
        return part;
    }

    /// <summary>The part grabbed: the root, or the child <paramref name="index"/> (0-based) of the group.</summary>
    public UUID PartId(int child = -1) => child < 0 ? Root.UUID : _parts[1 + child].UUID;

    /// <summary>One ObjectGrabUpdate, as Scene.ProcessObjectGrabUpdate hands it to the group when the agent may move it.</summary>
    public void Update(UUID part, Vector3 cursor) => Group.GrabMovement(part, Vector3.Zero, cursor, null);

    /// <summary>Takes the actors back from the parts, so none is removed from a scene that is gone when a part is finalized.</summary>
    public void Release()
    {
        foreach (SceneObjectPart part in _parts)
        {
            part.PhysActor = null;
            part.Dispose();
        }
    }
}

/// <summary>What a drag did, from its samples (the object's centre at each heartbeat).</summary>
public sealed class GrabMeasure
{
    public string Run;
    public float Mass;
    public int Updates;
    /// <summary>How far past the final target the object got, along the drag, at most (m; 0 = never past it).</summary>
    public float Overshoot;
    /// <summary>The seconds after the cursor stopped until the object was last more than 5 cm from the target; when it was
    /// still farther than that at the end of the run <see cref="Settled"/> is false and this is the whole time watched.</summary>
    public double SwingSeconds;
    public bool Settled;
    /// <summary>The farthest the object was from the target after the cursor stopped (m), and in the last second.</summary>
    public float DeviationAfterStop;
    public float DeviationLastSecond;
    /// <summary>The object's distance from where it ends, to where the cursor is held (m): the sag when it hangs below.</summary>
    public float EndOffset;
    public float EndOffsetZ;
    /// <summary>The farthest the object was behind the cursor's course while it moved (m).</summary>
    public float MaxLag;
    public float TopSpeed;
    /// <summary>Release scenarios: the place and speed at the release, where the object ends, and how far it went after.</summary>
    public Vector3 AtRelease;
    public float SpeedAtRelease;
    public Vector3 End;
    public float TravelAfterRelease;
    public double RestAfterRelease = double.NaN;
    public bool PassedThrough;
    public bool NotANumber;
    public bool LeftRegion;

    public static string Header => "run,updates,mass,overshoot_m,swing_s,settled,dev_after_stop_m,dev_last_s_m,end_offset_m,end_offset_z_m,max_lag_m,top_speed_mps,speed_at_release_mps,travel_after_release_m,rest_after_release_s,end_x,end_y,end_z,passed_through,nan,left_region";

    public string Row()
    {
        string F(float v) => float.IsNaN(v) ? "" : v.ToString("0.000", CultureInfo.InvariantCulture);
        string D(double v) => double.IsNaN(v) ? "" : v.ToString("0.00", CultureInfo.InvariantCulture);
        return string.Join(",", Run, Updates.ToString(CultureInfo.InvariantCulture), F(Mass), F(Overshoot), D(SwingSeconds), Settled ? "1" : "0", F(DeviationAfterStop), F(DeviationLastSecond),
                           F(EndOffset), F(EndOffsetZ), F(MaxLag), F(TopSpeed), F(SpeedAtRelease), F(TravelAfterRelease), D(RestAfterRelease),
                           F(End.X), F(End.Y), F(End.Z), PassedThrough ? "1" : "0", NotANumber ? "1" : "0", LeftRegion ? "1" : "0");
    }
}

public static class GrabScenarios
{
    /// <summary>How many ObjectGrabUpdate packets a second the viewer sends while the mouse moves. The viewer sends one per
    /// hover event with mouse motion and has no timer, so this is a stand-in for a viewer drawing about 30 frames a second.</summary>
    public static float ViewerHz = 30f;

    private const float X = 128f, Y = 128f;
    private const float Cube = 1f;
    private const float Reach = 5f;
    private const float LiftHeight = 3f;
    private const float ObstacleAt = 2.5f;        // from the start, along the drag
    private const double ReleaseTime = 2.5;       // as the cursor stops, with the object still catching up and moving
    private const uint RootId = 1401, LeftId = 1402, RightId = 1403;

    // The plan of the run on this thread: a run is made and measured on one thread, one after another.
    [ThreadStatic] private static GrabPlan _last;

    public static GrabPlan PlanOf(Run r) => _last;

    // A 1 m cube's mass is its density / 100 in the scene's units (1000 makes 10): density 100 makes it 1, and 10000 makes it 100.
    private static float DensityFor(float mass) => mass * 100f;

    private static GrabPlan Begin(Run r, string kind, Vector3 direction, float distance, float mass, double drag = 2.0)
    {
        float z = r.GroundAt(X, Y) + Cube * 0.5f + 0.02f;
        var plan = new GrabPlan { Kind = kind, Start = new Vector3(X, Y, z), Direction = direction, Distance = distance, DragSeconds = drag };
        _last = plan;
        PhysicsActor a = r.AddBox(new Vector3(Cube, Cube, Cube), plan.Start, Quaternion.Identity);
        a.Density = DensityFor(mass);
        plan.Mass = a.Mass;
        plan.Core = new CoreGrab(a);
        r.Cleanups.Add(plan.Core.Release);
        return plan;
    }

    /// <summary>The updates the viewer sent since the last heartbeat, each with the cursor where it then was.</summary>
    private static void Feed(Run r)
    {
        GrabPlan p = PlanOf(r);
        UUID part = p.Core.PartId(p.Kind == "linkset" ? 0 : -1);
        double until = r.Now + 1e-9;
        while (true)
        {
            double t = p.NextUpdate / (double)ViewerHz;
            if (t > until)
                break;
            p.NextUpdate++;
            if (t < p.DragStart)
                continue;
            if (!double.IsNaN(p.ReleaseAt) && t > p.ReleaseAt)
                continue;
            if (!p.UpdatesWhileHeld && t > p.DragEnd)
                continue;
            p.Core.Update(part, p.Ideal(t) + p.CursorOffset);
            p.Sent++;
        }
    }

    private static Scenario Drag(string name, string description, float mass, Vector3 direction, float distance, bool release = false, bool held = true)
        => new()
        {
            Name = name,
            Description = description,
            DefaultDuration = _ => release ? 7f : 7.5f,
            Setup = r =>
            {
                GrabPlan p = Begin(r, "box", direction, distance, mass);
                p.UpdatesWhileHeld = held;
                if (release)
                    p.ReleaseAt = ReleaseTime;
            },
            Input = Feed,
        };

    private static Scenario Linkset() => new()
    {
        Name = "grab-linkset",
        Description = "Three 1 m cubes in a row along x (10 kg each) dragged 5 m along y by the left child, then held 5 s.",
        DefaultDuration = _ => 7.5f,
        Setup = r =>
        {
            float z = r.GroundAt(X, Y) + Cube * 0.5f + 0.02f;
            var unit = new Vector3(Cube, Cube, Cube);
            PhysicsActor root = r.Actor = r.AddPart(PrimitiveBaseShape.CreateBox(), unit, new Vector3(X, Y, z), Quaternion.Identity, true, RootId);
            r.ActorSize = unit;
            PhysicsActor left = r.AddPart(PrimitiveBaseShape.CreateBox(), unit, new Vector3(X - 1f, Y, z), Quaternion.Identity, true, LeftId, root);
            PhysicsActor right = r.AddPart(PrimitiveBaseShape.CreateBox(), unit, new Vector3(X + 1f, Y, z), Quaternion.Identity, true, RightId, root);
            var plan = new GrabPlan
            {
                Kind = "linkset", Start = new Vector3(X, Y, z), Direction = Vector3.UnitY, Distance = Reach,
                CursorOffset = new Vector3(-1f, 0f, 0f),
            };
            _last = plan;
            plan.Mass = root.Mass;
            plan.Core = new CoreGrab(root, left, right);
            r.Cleanups.Add(plan.Core.Release);
        },
        Input = Feed,
    };

    private static Scenario Obstacle(string name, string description, bool physical) => new()
    {
        Name = name,
        Description = description,
        DefaultDuration = _ => 7.5f,
        Setup = r =>
        {
            GrabPlan p = Begin(r, "box", Vector3.UnitX, Reach, 10f);
            p.Obstacle = true;
            p.ObstaclePhysical = physical;
            if (physical)
                r.AddOtherBox(new Vector3(Cube, Cube, Cube), p.Start + new Vector3(ObstacleAt, 0f, 0f), Quaternion.Identity, true);
            else
                r.AddOtherBox(new Vector3(0.5f, 4f, 3f), new Vector3(p.Start.X + ObstacleAt, p.Start.Y, r.GroundAt(X, Y) + 1.5f), Quaternion.Identity, false);
        },
        Input = Feed,
        PassedThrough = (r, s) => PassedObstacle(PlanOf(r), s),
    };

    // Through the obstacle: the object's centre is past the obstacle's centre along the drag while the two still overlap
    // across it (in y and z), taking the obstacle at its largest reach. A fixed wall is 0.5 m thick and 4 m wide.
    private static bool PassedObstacle(GrabPlan p, Sample s)
    {
        if (p == null || !p.Obstacle)
            return false;
        if (p.ObstaclePhysical)
        {
            float reach = Cube * 0.5f * 2f * MathF.Sqrt(3f);
            return s.Position.X > s.Other.X && MathF.Abs(s.Position.Y - s.Other.Y) < reach && MathF.Abs(s.Position.Z - s.Other.Z) < reach;
        }
        return s.Position.X > s.Other.X + 0.25f && MathF.Abs(s.Position.Y - s.Other.Y) < 2f && s.Position.Z < s.Other.Z + 1.5f;
    }

    public static readonly IReadOnlyList<Scenario> All = new List<Scenario>
    {
        Drag("grab-side-1kg", "A 1 kg cube dragged 5 m along x in 2 s through core's grab path, then held 5 s.", 1f, Vector3.UnitX, Reach),
        Drag("grab-side-100kg", "A 100 kg cube dragged 5 m along x in 2 s through core's grab path, then held 5 s.", 100f, Vector3.UnitX, Reach),
        Drag("grab-lift-1kg", "A 1 kg cube lifted 3 m straight up in 2 s through core's grab path, then held 5 s.", 1f, Vector3.UnitZ, LiftHeight),
        Drag("grab-lift-100kg", "A 100 kg cube lifted 3 m straight up in 2 s through core's grab path, then held 5 s.", 100f, Vector3.UnitZ, LiftHeight),
        Drag("grab-side-1kg-still", "As grab-side-1kg, but the viewer sends nothing once the cursor has stopped (a mouse held perfectly still).", 1f, Vector3.UnitX, Reach, held: false),
        Drag("grab-release-1kg", "A 1 kg cube dragged along x and let go as the cursor stops, while still moving; 4.5 s watched.", 1f, Vector3.UnitX, Reach, release: true),
        Drag("grab-release-100kg", "A 100 kg cube dragged along x and let go as the cursor stops, while still moving; 4.5 s watched.", 100f, Vector3.UnitX, Reach, release: true),
        Linkset(),
        Obstacle("grab-wall", "A 10 kg cube dragged 5 m along x into a fixed wall 2.5 m ahead, held 5 s.", false),
        Obstacle("grab-box", "A 10 kg cube dragged 5 m along x into a second 10 kg physical cube 2.5 m ahead, held 5 s.", true),
    };

    /// <summary>What the drag did, from the run's samples.</summary>
    public static GrabMeasure Measure(RunResult res)
    {
        GrabPlan p = _last;
        List<Sample> ss = res.Samples;
        var m = new GrabMeasure { Run = $"{res.Scenario}@{res.RateHz:0.#}Hz/{ViewerHz:0.#}upd/{(res.PhysicsRateHz > 0 ? res.PhysicsRateHz.ToString("0.#") + "Hz" : "heartbeat")}" };
        m.Mass = p.Mass;
        m.Updates = p.Sent;
        Vector3 target = p.Target;
        double dragEnd = p.DragEnd;
        double lastT = ss[^1].T;
        double lastBad = double.NaN;
        bool moving = false;
        Sample? at = null;
        double restCandidate = double.NaN;
        foreach (Sample s in ss)
        {
            if (!float.IsFinite(s.Position.X) || !float.IsFinite(s.Position.Y) || !float.IsFinite(s.Position.Z) || !float.IsFinite(s.Velocity.X) || !float.IsFinite(s.Velocity.Y) || !float.IsFinite(s.Velocity.Z))
            {
                m.NotANumber = true;
                continue;
            }
            m.TopSpeed = MathF.Max(m.TopSpeed, s.Speed);
            if (s.T > p.DragStart && s.T <= dragEnd + 1e-9)
                m.MaxLag = MathF.Max(m.MaxLag, Vector3.Distance(s.Position, p.Ideal(s.T)));
            m.Overshoot = MathF.Max(m.Overshoot, Vector3.Dot(s.Position - target, p.Direction));
            if (s.T > dragEnd)
            {
                float dev = Vector3.Distance(s.Position, target);
                m.DeviationAfterStop = MathF.Max(m.DeviationAfterStop, dev);
                if (s.T > lastT - 1.0)
                    m.DeviationLastSecond = MathF.Max(m.DeviationLastSecond, dev);
                if (dev > 0.05f)
                    lastBad = s.T;
            }
            if (!double.IsNaN(p.ReleaseAt) && s.T >= p.ReleaseAt)
            {
                if (at == null)
                {
                    at = s;
                    m.AtRelease = s.Position;
                    m.SpeedAtRelease = s.Speed;
                }
                if (s.Speed > 0.5f) moving = true;
                if (moving && s.Speed < 0.05f)
                {
                    if (double.IsNaN(restCandidate)) restCandidate = s.T;
                    if (double.IsNaN(m.RestAfterRelease) && s.T - restCandidate >= 0.5 - 1e-9)
                        m.RestAfterRelease = restCandidate - p.ReleaseAt;
                }
                else if (double.IsNaN(m.RestAfterRelease))
                    restCandidate = double.NaN;
            }
            if (res.Scenario.StartsWith("grab-", StringComparison.Ordinal) && PassedObstacle(p, s))
                m.PassedThrough = true;
        }
        Sample end = ss[^1];
        m.End = end.Position;
        m.LeftRegion = !(end.Position.X > 0f && end.Position.X < 256f && end.Position.Y > 0f && end.Position.Y < 256f);
        // Linkset: the ideal is the root following the cursor; the cursor was a metre off it, so it is the offset core leaves.
        m.EndOffset = Vector3.Distance(end.Position, target);
        m.EndOffsetZ = end.Position.Z - target.Z;
        if (double.IsNaN(p.ReleaseAt))
        {
            m.Settled = double.IsNaN(lastBad) || lastBad < lastT - 1e-9;
            m.SwingSeconds = double.IsNaN(lastBad) ? 0.0 : lastBad - dragEnd;
        }
        else
        {
            m.Settled = true;
            m.SwingSeconds = double.NaN;
            m.TravelAfterRelease = at == null ? 0f : Vector3.Distance(end.Position, m.AtRelease);
        }
        return m;
    }
}
