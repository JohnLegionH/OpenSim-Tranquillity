/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using System.Reflection;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using Phlox.ScriptEngine;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// An ILSLSystemAPI that records every call (name and the arguments it received) and answers with a value the test set for
/// that function, or the default of the return type. No LSLSystemAPI is built.
/// </summary>
public class FakeLslApi : DispatchProxy
{
    public sealed record Call(string Name, object[] Args)
    {
        /// <summary>The managed thread the call ran on.</summary>
        public int Thread { get; init; } = Environment.CurrentManagedThreadId;
    }

    private readonly ConcurrentQueue<Call> m_calls = new();
    private readonly ConcurrentDictionary<string, object> m_returns = new();

    public Call[] Calls => m_calls.ToArray();

    public static (ILSLSystemAPI Api, FakeLslApi Fake) Create()
    {
        ILSLSystemAPI api = DispatchProxy.Create<ILSLSystemAPI, FakeLslApi>();
        var fake = (FakeLslApi)(object)api;
        fake.Delays("llSleep", a => (int)((float)a[0] * 1000)); // LSLSystemAPI.llSleep: ScriptSleep((int)(sec * 1000))
        return (api, fake);
    }

    /// <summary>The value the function answers with from now on.</summary>
    public void Returns(string function, object value) => m_returns[function] = value;

    private readonly ConcurrentDictionary<string, Func<object[], int>> m_delays = new();

    /// <summary>The forced delay in milliseconds the function asks for each time it is called, as LSLSystemAPI's ScriptSleep does.</summary>
    public void Delays(string function, int ms) => m_delays[function] = _ => ms;

    private readonly ConcurrentDictionary<string, Func<object[], object>> m_runs = new();

    /// <summary>What the function does when called, in place of answering a fixed value: it may block, throw, or record a result on the call's context.</summary>
    public void Runs(string function, Func<object[], object> body) => m_runs[function] = body;

    /// <summary>The same, for a delay that follows the call's arguments.</summary>
    public void Delays(string function, Func<object[], int> ms) => m_delays[function] = ms;

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        m_calls.Enqueue(new Call(targetMethod.Name, args));
        if (m_delays.TryGetValue(targetMethod.Name, out Func<object[], int> ms))
            InWorldz.Phlox.Glue.SyscallContext.Current?.AddDelay(ms(args));
        if (m_runs.TryGetValue(targetMethod.Name, out Func<object[], object> run))
            return run(args);
        if (m_returns.TryGetValue(targetMethod.Name, out object set)) return set;
        return DefaultOf(targetMethod.ReturnType);
    }

    public static object DefaultOf(System.Type t)
    {
        if (t == typeof(void)) return null;
        if (t == typeof(string)) return "";
        if (t == typeof(LSLList)) return new LSLList();
        if (t == typeof(Quaternion)) return Quaternion.Identity;
        return t.IsValueType ? System.Activator.CreateInstance(t) : null;
    }
}
