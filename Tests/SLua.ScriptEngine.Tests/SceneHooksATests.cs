/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Tests.Common;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>Records what the hooks post.</summary>
internal sealed class RecordingPoster : IEventPoster
{
    public readonly List<(uint Local, EventParams Parms)> Objects = new();
    public readonly List<(UUID Item, EventParams Parms)> Scripts = new();

    public bool PostObjectEvent(uint localID, EventParams parms) { Objects.Add((localID, parms)); return true; }

    public bool PostScriptEvent(UUID itemID, EventParams parms) { Scripts.Add((itemID, parms)); return true; }

    public (uint Local, EventParams Parms)[] ObjectsNamed(string name) => Objects.Where(o => o.Parms.EventName == name).ToArray();

    public (UUID Item, EventParams Parms)[] ScriptsNamed(string name) => Scripts.Where(o => o.Parms.EventName == name).ToArray();
}

/// <summary>
/// The region's object and avatar events, raised on a real scene's EventManager, arrive as the LSL event with its
/// arguments and detected data, posted to the prim or script concerned. One test for each hook (hooks 10 to 31 and 44
/// of the engine design, apart from the shutdown hook, which is not a scene event of this kind).
/// </summary>
public class SceneHooksATests
{
    private sealed class Fixture
    {
        public readonly Scene Scene = new SceneHelpers().SetupScene();
        public readonly RecordingPoster Poster = new();
        public readonly ManualClock Clock = new();
        public readonly SceneHooksA Hooks;
        public readonly SceneObjectGroup Object;
        public SceneObjectPart Part => Object.RootPart;

        public Fixture(bool physicsThrottle = true, bool subscribe = true)
        {
            Hooks = new SceneHooksA(Scene, Poster, physicsThrottle, Clock);
            Object = SceneHelpers.AddSceneObject(Scene, "Example Object", UUID.Random());
            if (subscribe)
                Hooks.Subscribe();
        }

        public ScenePresence AddAvatar() => SceneHelpers.AddScenePresence(Scene, UUID.Random());

        public EventManager Events => Scene.EventManager;
    }

    private static SurfaceTouchEventArgs Surface() => new()
    {
        FaceIndex = 2,
        UVCoord = new Vector3(0.25f, 0.5f, 0f),
        STCoord = new Vector3(0.75f, 0.125f, 0f),
        Normal = new Vector3(0f, 1f, 0f),
        Binormal = new Vector3(1f, 0f, 0f),
        Position = new Vector3(130f, 130f, 25f),
    };

    private static DetectedObject Collider(string name, UUID? key = null) => new()
    {
        keyUUID = key ?? UUID.Random(),
        nameStr = name,
        ownerUUID = UUID.Random(),
        groupUUID = UUID.Random(),
        posVector = new Vector3(1f, 2f, 3f),
        rotQuat = new Quaternion(0f, 0f, 0.7071f, 0.7071f),
        velVector = new Vector3(0f, 0f, -1f),
        colliderType = 4,
        linkNumber = 2,
    };

    private static ColliderArgs Colliders(params DetectedObject[] objects)
    {
        var args = new ColliderArgs();
        args.Colliders.AddRange(objects);
        return args;
    }

    // ---- touch: hooks 10 to 12 ----

    [Fact]
    public void ObjectGrabPostsTouchStartWithTheTouchersDetectedData()
    {
        var f = new Fixture();
        ScenePresence toucher = f.AddAvatar();

        f.Events.TriggerObjectGrab(f.Part.LocalId, 0, Vector3.Zero, toucher.ControllingClient, Surface());

        var (local, parms) = Assert.Single(f.Poster.Objects);
        Assert.Equal(f.Part.LocalId, local);
        Assert.Equal("touch_start", parms.EventName);
        Assert.Equal(new object[] { 1 }, parms.Params);
        DetectParams d = Assert.Single(parms.DetectParams);
        Assert.Equal(toucher.UUID, d.Key);
        Assert.Equal(toucher.UUID, d.Owner);
        Assert.Equal(toucher.Firstname + " " + toucher.Lastname, d.Name);
        Assert.True((d.Type & DetectParams.AGENT) != 0);
        Assert.Equal(f.Part.LinkNum, d.LinkNum);
        Assert.Equal(2, d.TouchFace);
        Assert.Equal(0.25, d.TouchUV.x, 4);
        Assert.Equal(0.125, d.TouchST.y, 4);
        Assert.Equal(1.0, d.TouchNormal.y, 4);
    }

    [Fact]
    public void ATouchWithNoSurfaceDataKeepsTheInvalidFace()
    {
        var f = new Fixture();
        ScenePresence toucher = f.AddAvatar();

        f.Events.TriggerObjectGrab(f.Part.LocalId, 0, Vector3.Zero, toucher.ControllingClient, null);

        Assert.Equal(-1, Assert.Single(f.Poster.Objects).Parms.DetectParams[0].TouchFace);
    }

    [Fact]
    public void ATouchOnAPrimTheRegionDoesNotKnowPostsNothing()
    {
        var f = new Fixture();
        ScenePresence toucher = f.AddAvatar();

        f.Events.TriggerObjectGrab(f.Part.LocalId + 12345, 0, Vector3.Zero, toucher.ControllingClient, null);

        Assert.Empty(f.Poster.Objects);
    }

    [Fact]
    public void ObjectGrabbingPostsTouchWithTheGrabOffset()
    {
        var f = new Fixture();
        ScenePresence toucher = f.AddAvatar();

        f.Events.TriggerObjectGrabbing(f.Part.LocalId, 0, new Vector3(0.5f, 0.25f, 0f), toucher.ControllingClient, Surface());

        var (local, parms) = Assert.Single(f.Poster.Objects);
        Assert.Equal(f.Part.LocalId, local);
        Assert.Equal("touch", parms.EventName);
        Assert.Equal(new object[] { 1 }, parms.Params);
        Assert.Equal(0.5, parms.DetectParams[0].OffsetPos.x, 4);
        Assert.Equal(0.25, parms.DetectParams[0].OffsetPos.y, 4);
    }

    [Fact]
    public void GrabUpdatesCloserThanTheTouchIntervalAreFoldedIntoTheOneBeforeIt()
    {
        var f = new Fixture();
        ScenePresence toucher = f.AddAvatar();

        f.Events.TriggerObjectGrabbing(f.Part.LocalId, 0, Vector3.Zero, toucher.ControllingClient, null);
        f.Clock.Now += 0.05;
        f.Events.TriggerObjectGrabbing(f.Part.LocalId, 0, Vector3.Zero, toucher.ControllingClient, null);
        Assert.Single(f.Poster.ObjectsNamed("touch"));

        f.Clock.Now += 0.06; // 110 ms after the first
        f.Events.TriggerObjectGrabbing(f.Part.LocalId, 0, Vector3.Zero, toucher.ControllingClient, null);
        Assert.Equal(2, f.Poster.ObjectsNamed("touch").Length);
    }

    [Fact]
    public void ObjectDeGrabPostsTouchEndAndTheNextTouchIsNotHeldBackByTheInterval()
    {
        var f = new Fixture();
        ScenePresence toucher = f.AddAvatar();
        f.Events.TriggerObjectGrabbing(f.Part.LocalId, 0, Vector3.Zero, toucher.ControllingClient, null);

        f.Events.TriggerObjectDeGrab(f.Part.LocalId, 0, toucher.ControllingClient, Surface());

        var (local, parms) = Assert.Single(f.Poster.ObjectsNamed("touch_end"));
        Assert.Equal(f.Part.LocalId, local);
        Assert.Equal(new object[] { 1 }, parms.Params);
        Assert.Equal(toucher.UUID, Assert.Single(parms.DetectParams).Key);

        f.Events.TriggerObjectGrabbing(f.Part.LocalId, 0, Vector3.Zero, toucher.ControllingClient, null);
        Assert.Equal(2, f.Poster.ObjectsNamed("touch").Length);
    }

    // ---- changed: hook 13 ----

    [Fact]
    public void ScriptChangedPostsChangedWithTheBitsToThePartThatChanged()
    {
        var f = new Fixture();

        f.Events.TriggerOnScriptChangedEvent(f.Part.LocalId, 0x20 | 0x8);

        var (local, parms) = Assert.Single(f.Poster.Objects);
        Assert.Equal(f.Part.LocalId, local);
        Assert.Equal("changed", parms.EventName);
        Assert.Equal(new object[] { 0x28 }, parms.Params);
        Assert.True(parms.DetectParams == null || parms.DetectParams.Length == 0);
    }

    // ---- death and damage: hooks 14 to 16 ----

    private static SceneObjectGroup Wear(Fixture f, ScenePresence sp)
    {
        f.Object.AttachedAvatar = sp.UUID;
        sp.AddAttachment(f.Object);
        Assert.Contains(f.Object, sp.GetAttachments());
        return f.Object;
    }

    [Fact]
    public void AvatarKilledPostsOnDeathToEveryPartOfEveryAttachment()
    {
        var f = new Fixture();
        ScenePresence dead = f.AddAvatar();
        SceneObjectGroup worn = Wear(f, dead);
        ScenePresence bystander = f.AddAvatar();

        f.Events.TriggerAvatarKill(0, dead);

        Assert.Equal(worn.Parts.Select(p => p.LocalId).OrderBy(x => x), f.Poster.ObjectsNamed("on_death").Select(o => o.Local).OrderBy(x => x));
        Assert.All(f.Poster.ObjectsNamed("on_death"), o => Assert.Empty(o.Parms.Params));

        f.Events.TriggerAvatarKill(0, bystander); // wears nothing
        Assert.Equal(worn.Parts.Count(), f.Poster.ObjectsNamed("on_death").Length);
    }

    [Fact]
    public void AvatarDamagePostsOnDamageWithOneDetectedEntryPerDamageToTheAttachments()
    {
        var f = new Fixture();
        ScenePresence hurt = f.AddAvatar();
        Wear(f, hurt);
        UUID source = UUID.Random(), owner = UUID.Random();
        var batch = new List<DamageEntry>
        {
            new(source, owner, 0, 15f, DamageEntry.TYPE_GENERIC) { OriginalDamage = 15f },
            new(UUID.Zero, UUID.Zero, 0, 3f, DamageEntry.TYPE_IMPACT) { OriginalDamage = 4f },
        };

        f.Events.TriggerAvatarDamage(hurt, batch);

        var (local, parms) = Assert.Single(f.Poster.ObjectsNamed("on_damage"));
        Assert.Equal(f.Part.LocalId, local);
        Assert.Equal(new object[] { 2 }, parms.Params);
        Assert.Equal(2, parms.DetectParams.Length);
        Assert.Equal(source, parms.DetectParams[0].Key);
        Assert.Equal(owner, parms.DetectParams[0].Owner);
        Assert.Equal(15f, parms.DetectParams[0].Damage);
        Assert.Equal(DamageEntry.TYPE_GENERIC, parms.DetectParams[0].DamageType);
        Assert.Equal(4f, parms.DetectParams[1].OriginalDamage);
        Assert.Equal(DamageEntry.TYPE_IMPACT, parms.DetectParams[1].DamageType);
        Assert.NotNull(parms.DetectParams[0].AdjustDamage);
        parms.DetectParams[0].AdjustDamage(5f); // llAdjustDamage writes the pending entry
        Assert.Equal(5f, batch[0].Amount);
    }

    [Fact]
    public void AvatarDamageAppliedPostsFinalDamageWithEntriesThatCannotBeAdjusted()
    {
        var f = new Fixture();
        ScenePresence hurt = f.AddAvatar();
        Wear(f, hurt);
        var batch = new List<DamageEntry> { new(UUID.Random(), UUID.Random(), 0, 9f, DamageEntry.TYPE_GENERIC) };

        f.Events.TriggerAvatarDamageApplied(hurt, batch);

        var (local, parms) = Assert.Single(f.Poster.ObjectsNamed("final_damage"));
        Assert.Equal(f.Part.LocalId, local);
        Assert.Equal(new object[] { 1 }, parms.Params);
        Assert.Equal(9f, parms.DetectParams[0].Damage);
        Assert.Null(parms.DetectParams[0].AdjustDamage);
    }

    // ---- control: hook 17 ----

    [Fact]
    public void ScriptControlPostsControlToTheScriptWithTheAvatarHeldAndChangedBits()
    {
        var f = new Fixture();
        UUID item = UUID.Random(), agent = UUID.Random();

        // The scene raises this one from inside the region core; the test reaches the same method.
        typeof(EventManager).GetMethod("TriggerControlEvent", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(f.Events, new object[] { item, agent, 5u, 1u });

        var (target, parms) = Assert.Single(f.Poster.Scripts);
        Assert.Equal(item, target);
        Assert.Equal("control", parms.EventName);
        Assert.Equal(new object[] { agent, 5, 1 }, parms.Params);
    }

    // ---- collisions: hooks 19 to 21 ----

    [Theory]
    [InlineData("collision_start")]
    [InlineData("collision")]
    [InlineData("collision_end")]
    public void CollisionsPostTheEventWithTheCountAndEachColliderAsDetectedData(string eventName)
    {
        var f = new Fixture();
        DetectedObject first = Collider("First Object"), second = Collider("Second Object");
        ColliderArgs args = Colliders(first, second);

        switch (eventName)
        {
            case "collision_start": f.Events.TriggerScriptCollidingStart(f.Part.LocalId, args); break;
            case "collision": f.Events.TriggerScriptColliding(f.Part.LocalId, args); break;
            default: f.Events.TriggerScriptCollidingEnd(f.Part.LocalId, args); break;
        }

        var (local, parms) = Assert.Single(f.Poster.Objects);
        Assert.Equal(f.Part.LocalId, local);
        Assert.Equal(eventName, parms.EventName);
        Assert.Equal(new object[] { 2 }, parms.Params);
        Assert.Equal(2, parms.DetectParams.Length);
        DetectParams d = parms.DetectParams[0];
        Assert.Equal(first.keyUUID, d.Key);
        Assert.Equal("First Object", d.Name);
        Assert.Equal(first.ownerUUID, d.Owner);
        Assert.Equal(first.groupUUID, d.Group);
        Assert.Equal(3.0, d.Position.z, 4);
        Assert.Equal(-1.0, d.Velocity.z, 4);
        Assert.Equal(2, d.LinkNum);
        Assert.Equal(4, d.Type);
        Assert.Equal("Second Object", parms.DetectParams[1].Name);
    }

    [Fact]
    public void ACollisionFilterKeepsFilteredCollidersOutOfTheCountAndTheData()
    {
        var f = new Fixture();
        f.Part.SetCollisionFilter(false, "Blocked Object", null);

        f.Events.TriggerScriptColliding(f.Part.LocalId, Colliders(Collider("Blocked Object"), Collider("Allowed Object")));

        var (_, parms) = Assert.Single(f.Poster.Objects);
        Assert.Equal(new object[] { 1 }, parms.Params);
        Assert.Equal("Allowed Object", Assert.Single(parms.DetectParams).Name);
    }

    [Fact]
    public void ACollisionWhoseCollidersAreAllFilteredOrNonePostsNothing()
    {
        var f = new Fixture();
        f.Part.SetCollisionFilter(false, "Blocked Object", null);

        f.Events.TriggerScriptColliding(f.Part.LocalId, Colliders(Collider("Blocked Object")));
        f.Events.TriggerScriptColliding(f.Part.LocalId, Colliders());

        Assert.Empty(f.Poster.Objects);
    }

    // ---- land collisions: hooks 22 to 24 ----

    [Theory]
    [InlineData("land_collision_start")]
    [InlineData("land_collision")]
    [InlineData("land_collision_end")]
    public void LandCollisionsPostTheEventOncePerContactPointWithItsPosition(string eventName)
    {
        var f = new Fixture();
        ColliderArgs args = Colliders(
            new DetectedObject { posVector = new Vector3(10f, 20f, 30f) },
            new DetectedObject { posVector = new Vector3(11f, 21f, 31f) });

        switch (eventName)
        {
            case "land_collision_start": f.Events.TriggerScriptLandCollidingStart(f.Part.LocalId, args); break;
            case "land_collision": f.Events.TriggerScriptLandColliding(f.Part.LocalId, args); break;
            default: f.Events.TriggerScriptLandCollidingEnd(f.Part.LocalId, args); break;
        }

        Assert.Equal(2, f.Poster.Objects.Count);
        Assert.All(f.Poster.Objects, o => { Assert.Equal(f.Part.LocalId, o.Local); Assert.Equal(eventName, o.Parms.EventName); });
        Assert.Equal(new Vector3(10f, 20f, 30f), Assert.IsType<Vector3>(Assert.Single(f.Poster.Objects[0].Parms.Params)));
        Assert.Equal(new Vector3(11f, 21f, 31f), Assert.IsType<Vector3>(Assert.Single(f.Poster.Objects[1].Parms.Params)));
    }

    // ---- attach: hook 25 ----

    [Fact]
    public void AttachPostsAttachWithTheWearerAndNullKeyOnDetach()
    {
        var f = new Fixture();
        UUID wearer = UUID.Random();

        f.Events.TriggerOnAttach(f.Part.LocalId, UUID.Random(), wearer);
        f.Events.TriggerOnAttach(f.Part.LocalId, UUID.Random(), UUID.Zero);

        var attaches = f.Poster.ObjectsNamed("attach");
        Assert.Equal(2, attaches.Length);
        Assert.Equal(f.Part.LocalId, attaches[0].Local);
        Assert.Equal(new object[] { wearer }, attaches[0].Parms.Params);
        Assert.Equal(new object[] { UUID.Zero }, attaches[1].Parms.Params);
    }

    // ---- moving: hooks 26 and 27 ----

    [Fact]
    public void MovingStartAndEndPostWithNoArguments()
    {
        var f = new Fixture();

        f.Events.TriggerMovingStartEvent(f.Part.LocalId);
        f.Events.TriggerMovingEndEvent(f.Part.LocalId);

        Assert.Equal(new[] { "moving_start", "moving_end" }, f.Poster.Objects.Select(o => o.Parms.EventName));
        Assert.All(f.Poster.Objects, o => { Assert.Equal(f.Part.LocalId, o.Local); Assert.Empty(o.Parms.Params); });
    }

    // ---- targets: hooks 28 to 31 ----

    [Fact]
    public void AtTargetPostsTheHandleTheTargetAndWhereTheObjectIsToTheScript()
    {
        var f = new Fixture();
        UUID item = UUID.Random();

        f.Events.TriggerAtTargetEvent(item, 3, new Vector3(1f, 2f, 3f), new Vector3(1.5f, 2.5f, 3.5f));

        var (target, parms) = Assert.Single(f.Poster.Scripts);
        Assert.Equal(item, target);
        Assert.Equal("at_target", parms.EventName);
        Assert.Equal(new object[] { 3, new Vector3(1f, 2f, 3f), new Vector3(1.5f, 2.5f, 3.5f) }, parms.Params);
    }

    [Fact]
    public void NotAtTargetPostsWithNoArgumentsToTheScript()
    {
        var f = new Fixture();
        UUID item = UUID.Random();

        f.Events.TriggerNotAtTargetEvent(item);

        var (target, parms) = Assert.Single(f.Poster.Scripts);
        Assert.Equal(item, target);
        Assert.Equal("not_at_target", parms.EventName);
        Assert.Empty(parms.Params);
    }

    [Fact]
    public void AtRotTargetPostsTheHandleTheTargetRotationAndTheObjectsRotationToTheScript()
    {
        var f = new Fixture();
        UUID item = UUID.Random();
        var target = new Quaternion(0f, 0f, 0.5f, 0.866f);
        var current = new Quaternion(0f, 0f, 0.4f, 0.917f);

        f.Events.TriggerAtRotTargetEvent(item, 7, target, current);

        var (script, parms) = Assert.Single(f.Poster.Scripts);
        Assert.Equal(item, script);
        Assert.Equal("at_rot_target", parms.EventName);
        Assert.Equal(new object[] { 7, target, current }, parms.Params);
    }

    [Fact]
    public void NotAtRotTargetPostsWithNoArgumentsToTheScript()
    {
        var f = new Fixture();
        UUID item = UUID.Random();

        f.Events.TriggerNotAtRotTargetEvent(item);

        var (script, parms) = Assert.Single(f.Poster.Scripts);
        Assert.Equal(item, script);
        Assert.Equal("not_at_rot_target", parms.EventName);
        Assert.Empty(parms.Params);
    }

    // ---- frame: hook 44 ----

    private static void SetPhysicsTimes(Scene scene, float update, float sync)
    {
        typeof(Scene).GetField("physicsMS", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(scene, update);
        typeof(Scene).GetField("physicsMS2", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(scene, sync);
    }

    [Fact]
    public void EachFrameFeedsAMovingAverageOfTheTenLastPhysicsTimes()
    {
        var f = new Fixture(physicsThrottle: true);

        for (int i = 0; i < 10; i++) { SetPhysicsTimes(f.Scene, 4f, 1f); f.Events.TriggerOnFrame(); } // 5 ms each
        Assert.Equal(5, f.Hooks.PhysicsFrameTimeAvg);

        for (int i = 0; i < 10; i++) { SetPhysicsTimes(f.Scene, 14f, 1f); f.Events.TriggerOnFrame(); } // the ten old ones are gone
        Assert.Equal(15, f.Hooks.PhysicsFrameTimeAvg);
    }

    [Fact]
    public void WithThePhysicsThrottleOffTheFrameEventIsNotFollowed()
    {
        var f = new Fixture(physicsThrottle: false);

        SetPhysicsTimes(f.Scene, 9f, 1f);
        f.Events.TriggerOnFrame();

        Assert.Equal(0, f.Hooks.PhysicsFrameTimeAvg);
    }

    // ---- subscribing ----

    [Fact]
    public void AfterUnsubscribeNothingIsPostedAndSubscribingTwiceDoesNotPostTwice()
    {
        var f = new Fixture(subscribe: false);
        f.Hooks.Subscribe();
        f.Hooks.Subscribe();

        f.Events.TriggerMovingStartEvent(f.Part.LocalId);
        Assert.Single(f.Poster.Objects);

        f.Hooks.Unsubscribe();
        f.Events.TriggerMovingStartEvent(f.Part.LocalId);
        Assert.Single(f.Poster.Objects);
    }
}
