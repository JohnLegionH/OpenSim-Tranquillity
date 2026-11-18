/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Phlox.ScriptEngine;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The scheduler harness with the generated ll bindings registered in every VM, calling a fake ILSLSystemAPI that records
/// the calls. The harness's own test functions stay, so a script can still use ossl.Count and ossl.Mark; its ll test
/// functions of the same names are replaced by the bound ones.
/// </summary>
internal sealed class BindingRig : IDisposable
{
    public readonly ILSLSystemAPI Api;
    public readonly FakeLslApi Fake;
    public readonly SchedulerHarness H;

    public BindingRig()
    {
        (Api, Fake) = FakeLslApi.Create();
        H = new SchedulerHarness(configure: o =>
        {
            Func<Bindings.Vm> inner = o.VmFactory;
            o.VmFactory = () =>
            {
                Bindings.Vm vm = inner();
                LlBindings.Register(vm, script => new ScriptContext(script, Api));
                OsBindings.Register(vm, script => new ScriptContext(script, Api));
                return vm;
            };
        });
    }

    /// <summary>How many times the fake saw this function called.</summary>
    public int CallsOf(string apiName) => Fake.Calls.Count(c => c.Name == apiName);

    /// <summary>Pumps the scheduler until the condition holds or the cap passes, moving the manual clock on so a punished script wakes.</summary>
    public bool PumpUntil(Func<bool> condition) => H.PumpAdvancing(condition);

    public void Dispose() => H.Dispose();
}
