/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Reflection;
using OSHttpServer;
using Xunit;
using Assert = Xunit.Assert;

namespace OpenSim.Framework.Servers.Tests;

/// <summary>
/// The HTTP server's connection watcher (<see cref="ContextTimeoutManager"/>) is one thread shared by every listener
/// of the process, and every listener stops it when it stops. Stopping must never throw, however the stops of several
/// listeners fall in time against the watcher thread's own shutdown.
/// </summary>
/// <remarks>
/// The watcher is static, so these tests take it back to its never-started state through its private fields; they
/// share the process-wide state and so run one at a time.
/// </remarks>
[Collection(ContextTimeoutManagerCollection.Name)]
public class ContextTimeoutManagerTests : IDisposable
{
    private const BindingFlags Fields = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly FieldInfo ThreadField = typeof(ContextTimeoutManager).GetField("m_internalThread", Fields)!;
    private static readonly FieldInfo ShuttingDownField = typeof(ContextTimeoutManager).GetField("m_shuttingDown", Fields)!;
    private static readonly FieldInfo WaitEventField = typeof(ContextTimeoutManager).GetField("m_processWaitEven", Fields)!;

    public ContextTimeoutManagerTests() => NeverStarted();

    public void Dispose() => NeverStarted();

    private static void NeverStarted()
    {
        Thread? worker = (Thread?)ThreadField.GetValue(null);
        if (worker is not null && worker.IsAlive)
        {
            ContextTimeoutManager.Stop();
            Assert.True(worker.Join(10000), "the watcher thread did not end");
        }

        ThreadField.SetValue(null, null);
        ShuttingDownField.SetValue(null, false);
        WaitEventField.SetValue(null, new AutoResetEvent(false));
    }

    private static string FirstFrame(Exception e) => (e.StackTrace ?? "").Split(new[] { (char)10 })[0].Trim();

    private static Thread Worker => (Thread)ThreadField.GetValue(null)!;

    [Fact]
    public void Stop_ends_the_watcher_thread_and_a_second_stop_after_that_does_nothing()
    {
        ContextTimeoutManager.Start();
        Thread worker = Worker;

        ContextTimeoutManager.Stop();
        Assert.True(worker.Join(10000), "the watcher thread did not end after Stop");

        Exception? second = Record.Exception(ContextTimeoutManager.Stop);

        Assert.Null(second);
    }

    [Fact]
    public void Stop_called_by_several_threads_while_the_watcher_thread_shuts_down_never_throws()
    {
        const int Stoppers = 4;
        const int Rounds = 600;
        List<Exception> thrown = new();
        Stopwatch clock = Stopwatch.StartNew();

        int round = 0;
        for (; round < Rounds && thrown.Count == 0 && clock.Elapsed < TimeSpan.FromSeconds(60); round++)
        {
            NeverStarted();
            ContextTimeoutManager.Start();
            Thread worker = Worker;

            using ManualResetEventSlim go = new(false);
            Thread[] stoppers = new Thread[Stoppers];
            for (int i = 0; i < Stoppers; i++)
            {
                stoppers[i] = new Thread(() =>
                {
                    go.Wait();
                    // keep stopping until the watcher thread has gone, as several listeners stopping at once do
                    while (worker.IsAlive)
                    {
                        try
                        {
                            ContextTimeoutManager.Stop();
                        }
                        catch (Exception e)
                        {
                            lock (thrown)
                                thrown.Add(e);
                            return;
                        }
                    }
                });
                stoppers[i].Start();
            }

            go.Set();
            foreach (Thread stopper in stoppers)
                Assert.True(stopper.Join(20000), "a stopping thread did not end");
        }

        Assert.True(thrown.Count == 0,
            "Stop threw in round " + round + ": " + (thrown.Count == 0 ? "" : thrown[0].GetType().Name + " " + thrown[0].Message + " at " + FirstFrame(thrown[0])));
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public class ContextTimeoutManagerCollection
{
    public const string Name = "ContextTimeoutManager process-wide state";
}
