/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
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
}
