/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// Compiles the script API's code once per process, on a background thread, so the first ll call of a script does not pay
    /// for it (a first call can overrun the script's run window, which the runtime punishes with a sleep).
    /// </summary>
    internal static class ApiWarmUp
    {
        private const int NeverStarted = 0, Running = 1, Finished = 2;

        /// <summary>The longest a script's first ll call waits for a warm-up that is still running.</summary>
        private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

        private static readonly ManualResetEventSlim s_done = new ManualResetEventSlim(false);
        private static int s_state;
        private static int s_runs;
        private static int s_prepared;

        /// <summary>What the warm-up does; tests replace it. It gets the engine's log.</summary>
        internal static Action<IEngineLog> Work = DefaultWork;

        /// <summary>How many times the warm-up has been started in this process.</summary>
        internal static int Runs => Volatile.Read(ref s_runs);

        /// <summary>How many methods the default work compiled.</summary>
        internal static int Prepared => Volatile.Read(ref s_prepared);

        /// <summary>True once the warm-up has run to its end (or failed).</summary>
        internal static bool IsDone => Volatile.Read(ref s_state) == Finished;

        /// <summary>
        /// Compiles the script API's methods ahead of their first call: LSLSystemAPI itself (the real work behind every served
        /// function), the generated bindings and their marshalling, and the router with the proxies it is built from.
        /// </summary>
        internal static void DefaultWork(IEngineLog log)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (Type type in new[] { typeof(Phlox.ScriptEngine.LSLSystemAPI), typeof(LlBindings), typeof(LslMarshal), typeof(ApiRouter), typeof(EngineHostServices), typeof(ScriptContext) })
            {
                foreach (MethodInfo method in type.GetMethods(all))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters)
                        continue;
                    try
                    {
                        RuntimeHelpers.PrepareMethod(method.MethodHandle);
                        Interlocked.Increment(ref s_prepared);
                    }
                    catch (Exception)
                    {
                        // A method that cannot be compiled ahead of time is compiled when it is first called.
                    }
                }
            }
            ApiRouter.Create(UnavailableApi.Create(), UnavailableApi.Create());
        }

        /// <summary>Starts the warm-up if this process has not started it yet.</summary>
        internal static void Start(IEngineLog log)
        {
            if (Interlocked.CompareExchange(ref s_state, Running, NeverStarted) != NeverStarted)
                return;
            Interlocked.Increment(ref s_runs);
            var thread = new Thread(() =>
            {
                try { Work(log); }
                catch (Exception e) { log.Warn("The script API warm-up failed; the first ll calls of scripts will compile the code themselves: " + e.Message); }
                finally
                {
                    Volatile.Write(ref s_state, Finished);
                    s_done.Set();
                }
            })
            {
                IsBackground = true,
                Name = "SLua API warm-up",
                Priority = ThreadPriority.BelowNormal,
            };
            thread.Start();
        }

        /// <summary>Blocks while a warm-up that has started has not ended; returns at once when none has started or it has ended.</summary>
        internal static void WaitDone()
        {
            if (Volatile.Read(ref s_state) == Running)
                s_done.Wait(MaxWait);
        }

        /// <summary>Puts the process-wide state back to "never started"; for tests.</summary>
        internal static void ResetForTest()
        {
            s_done.Reset();
            Volatile.Write(ref s_state, NeverStarted);
            Volatile.Write(ref s_runs, 0);
            Volatile.Write(ref s_prepared, 0);
        }
    }
}
