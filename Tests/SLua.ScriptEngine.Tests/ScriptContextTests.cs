/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using System.Reflection;
using Phlox.ScriptEngine;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The engine reaches the script functions through ILSLSystemAPI, the interface the Phlox system API implements, not through
/// the class: a trampoline calls <c>context.Api.llSay(...)</c> and the same implementation Phlox uses answers. The context
/// holds the script and that interface and nothing of Phlox's engine.
/// </summary>
[Collection("native-library")]
public class ScriptContextTests
{
    /// <summary>An ILSLSystemAPI that records the name of every call and answers with the default of the return type.</summary>
    public class Recorder : DispatchProxy
    {
        public readonly ConcurrentQueue<string> Calls = new();

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            Calls.Enqueue(targetMethod.Name);
            if (targetMethod.Name == "llStringLength") return 42;
            return targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void) ? System.Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }

    [Fact]
    public void TheContextsApiIsTheInterfaceNotTheClass()
    {
        PropertyInfo api = typeof(ScriptContext).GetProperty(nameof(ScriptContext.Api));
        Assert.NotNull(api);
        Assert.Equal(typeof(ILSLSystemAPI), api.PropertyType);
        Assert.True(typeof(ILSLSystemAPI).IsAssignableFrom(typeof(LSLSystemAPI)), "the Phlox system API does not implement the interface");
    }

    [Fact]
    public void ACallThroughTheContextReachesWhatImplementsTheInterface()
    {
        ILSLSystemAPI proxy = DispatchProxy.Create<ILSLSystemAPI, Recorder>();
        var context = new ScriptContext(null, proxy);

        Assert.Same(proxy, context.Api);
        Assert.Equal(42, context.Api.llStringLength("abc"));
        context.Api.llSay(0, "hello");
        Assert.Equal(new[] { "llStringLength", "llSay" }, ((Recorder)(object)proxy).Calls.ToArray());
    }

    [RequiresNativeFact]
    public void TheContextKeepsTheScriptItWasMadeFor()
    {
        using var h = new SchedulerHarness();
        SchedulerScript script = h.Add("ossl.Count()");
        var context = new ScriptContext(script, DispatchProxy.Create<ILSLSystemAPI, Recorder>());
        Assert.Same(script, context.Script);
    }
}
