/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Data.Null;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// Scene.Backup wraps the pass in which the objects save themselves in the data service's backup batch, when the
/// service has one, and ends the batch even if the pass failed. A data service without a batch is used as before.
/// The assembly runs its tests one at a time (AssemblyInfo.cs); this class has no process-wide state of its own.
/// </summary>
public class SceneBackupBatchTests : OpenSimTestCase
{
    private sealed class BatchingDataService : NullDataService, ISimulationDataBatch
    {
        public readonly List<string> Calls;

        public BatchingDataService(List<string> calls)
        {
            Calls = calls;
        }

        public void BeginBackupBatch() => Calls.Add("begin");

        public void EndBackupBatch() => Calls.Add("end");
    }

    private static TestScene NewScene(ISimulationDataService service)
    {
        TestScene scene = new SceneHelpers().SetupScene();
        typeof(Scene).GetField("m_SimulationDataService", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(scene, service);
        Assert.Same(service, scene.SimulationDataService);
        return scene;
    }

    [Fact]
    public void ABackupPassRunsBetweenTheBeginAndTheEndOfTheBatch()
    {
        List<string> calls = new();
        TestScene scene = NewScene(new BatchingDataService(calls));
        scene.EventManager.OnBackup += (store, forced) => calls.Add("pass forced=" + forced);

        scene.Backup(true);

        Assert.Equal(new[] { "begin", "pass forced=True", "end" }, calls);
    }

    [Fact]
    public void TheBatchEndsWhenAHandlerOfThePassFails()
    {
        List<string> calls = new();
        TestScene scene = NewScene(new BatchingDataService(calls));
        scene.EventManager.OnBackup += (store, forced) => throw new InvalidOperationException("injected failure");
        scene.EventManager.OnBackup += (store, forced) => calls.Add("pass");

        scene.Backup(false);

        Assert.Equal(new[] { "begin", "pass", "end" }, calls);
    }

    [Fact]
    public void ADataServiceWithoutABatchIsUsedAsBefore()
    {
        List<string> calls = new();
        TestScene scene = NewScene(new NullDataService());
        scene.EventManager.OnBackup += (store, forced) => calls.Add("pass");

        scene.Backup(true);

        Assert.Equal(new[] { "pass" }, calls);
    }

    /// <summary>A batching store whose final commit fails the given number of times, and which notes what it is asked to store.</summary>
    private sealed class FailingCommitService : NullDataService, ISimulationDataService, ISimulationDataBatch
    {
        public readonly List<UUID> Stored = new();
        public int CommitsToFail;

        public new void StoreObject(SceneObjectGroup obj, UUID regionUUID)
        {
            Stored.Add(obj.UUID);
            base.StoreObject(obj, regionUUID);
        }

        public void BeginBackupBatch() { }

        public void EndBackupBatch()
        {
            if (CommitsToFail > 0)
            {
                CommitsToFail--;
                throw new InvalidOperationException("injected commit failure");
            }
        }
    }

    private static UUID[] AddObjects(TestScene scene, int count)
    {
        UUID owner = TestHelpers.ParseTail(1);
        return Enumerable.Range(1, count)
            .Select(i =>
            {
                SceneObjectGroup group = SceneHelpers.AddSceneObject(scene, 1, owner, "object" + i, 0x100 + i);
                group.AttachToBackup();
                group.HasGroupChanged = true;
                return group.UUID;
            })
            .ToArray();
    }

    [Fact]
    public void WhenTheFinalCommitFailsTheObjectsOfThePassAreSavedAgainByTheNextPass()
    {
        FailingCommitService service = new() { CommitsToFail = 1 };
        TestScene scene = NewScene(service);
        UUID[] ids = AddObjects(scene, 3);

        scene.Backup(true);
        Assert.True(ids.All(id => service.Stored.Contains(id)), "the first pass should have written every object");
        service.Stored.Clear();

        scene.Backup(true);

        Assert.True(ids.All(id => service.Stored.Contains(id)), "the next pass should write every object again");
    }

    [Fact]
    public void WhenTheFinalCommitSucceedsTheNextPassWritesNothingAgain()
    {
        FailingCommitService service = new();
        TestScene scene = NewScene(service);
        UUID[] ids = AddObjects(scene, 3);

        scene.Backup(true);
        Assert.True(ids.All(id => service.Stored.Contains(id)));
        service.Stored.Clear();

        scene.Backup(true);

        Assert.Empty(service.Stored);
    }

    [Fact]
    public void ObjectsThatAreSavedAgainAfterAFailedCommitAreSavedOnceWhenTheNextCommitWorks()
    {
        FailingCommitService service = new() { CommitsToFail = 1 };
        TestScene scene = NewScene(service);
        UUID[] ids = AddObjects(scene, 2);
        scene.Backup(true);
        scene.Backup(true);
        service.Stored.Clear();

        scene.Backup(true);

        Assert.Empty(service.Stored);
    }
}
