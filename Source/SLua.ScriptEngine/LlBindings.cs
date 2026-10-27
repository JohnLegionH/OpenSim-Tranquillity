/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Types;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine
{
    /// <summary>The ll functions bound to ILSLSystemAPI. The table itself (<c>All</c>) is generated; see tools/slua-bindings.</summary>
    internal static partial class LlBindings
    {
        /// <summary>
        /// Registers every bound function in the VM's "ll" table. With <paramref name="serviceLane"/>, the long-running (async)
        /// and service-reaching functions are registered as pausing calls whose work runs on the context's service lane while
        /// the script waits; every other function, and those two kinds without a lane, run on the script worker.
        /// </summary>
        public static void Register(Vm vm, Func<SchedulerScript, ScriptContext> contextOf, bool serviceLane = false)
        {
            foreach (BoundFunction f in All)
            {
                BoundFunction function = f;
                if (serviceLane && function.Class is FunctionClass.Async or FunctionClass.Service)
                {
                    vm.RegisterPausing("ll", function.Name,
                        (tag, args) => StartLaneCall(function, contextOf(SchedulerScript.Current), args),
                        (tag, args) => TakeLaneAnswer());
                    continue;
                }

                vm.Register("ll", function.Name, (tag, args) =>
                {
                    ScriptContext context = contextOf(SchedulerScript.Current);
                    Value[] result = RunInContext(function, context, args, out double delay);
                    if (delay > 0) context.Script.AddSleep(delay);
                    return result;
                });
            }
        }

        /// <summary>
        /// Runs the call on the calling thread inside a SyscallContext for its script, which is where LSLSystemAPI puts the
        /// delay it asks for (its ScriptSleep adds to the context of the call it is running in; Phlox does the same for a call
        /// that runs off its script's thread). Returns the call's values and its delay in seconds. A call that returns no values
        /// also ends the script's turn when the delay is given to the runtime; one that returns values keeps them and the delay
        /// takes effect at the script's next interrupt, and two such delays add up.
        /// </summary>
        private static Value[] RunInContext(BoundFunction function, ScriptContext context, ReadOnlySpan<Value> args, out double delaySeconds)
        {
            var call = new SyscallContext(context.ItemId, SyscallContext.NextSeq());
            Value[] result;
            call.Enter();
            try { result = function.Call(context, args); }
            finally { SyscallContext.Exit(); }
            delaySeconds = call.DelayMs / 1000.0;
            return result;
        }

        // ---- the service lane ----

        /// <summary>
        /// The start of a pausing call. A long-running call always goes to the lane (Phlox: RunAsync); a service-reaching call
        /// goes when the API says it needs the service (Phlox: Defer, which asks ISyscallDeferralAdvisor.NeedsService) and
        /// deferral is not off; otherwise it runs here, on the worker, as in Phlox. A call that goes to the lane parks its
        /// script until the lane answers.
        /// </summary>
        private static Value[] StartLaneCall(BoundFunction function, ScriptContext context, ReadOnlySpan<Value> args)
        {
            SchedulerScript script = context.Script;
            ServiceLane lane = context.Lane;
            bool defer = lane != null && (function.Class == FunctionClass.Async
                || (lane.Defers && context.Api.NeedsService(function.ApiName, function.ApiArgs(args))));

            if (!defer)
            {
                Value[] inline = RunInContext(function, context, args, out double delay);
                script.CompleteInline(inline, delay);
                return null;
            }

            Value[] copy = args.ToArray(); // the span is only valid for this call
            var syscall = new SyscallContext(context.ItemId, SyscallContext.NextSeq());
            script.BeginWait(syscall.Seq);
            lane.Submit(new LaneCall
            {
                Script = script,
                Context = syscall,
                Function = function.ApiName,
                Failure = FailureOf(function),
                Body = () =>
                {
                    Value[] values = function.Call(context, copy);
                    // A long-running call gives its value through SysReturn, recorded on the call's context; the value of the
                    // method itself is not the answer (Phlox: RunAsync, "the key reaches the script through SysReturn").
                    return function.Class == FunctionClass.Async ? RecordedResult(function, syscall) : values;
                },
            });
            return null;
        }

        /// <summary>
        /// The take of a pausing call, in the step that carries on after the answer: a fault the call ended with is raised in the
        /// script (Phlox: SetPendingFault), the values are the call's results, and the call's delay is the script's.
        /// </summary>
        private static Value[] TakeLaneAnswer()
        {
            SchedulerScript script = SchedulerScript.Current;
            (Value[] result, double delay, Exception fault) = script.TakeOutcome();
            if (fault != null)
                throw new InvalidOperationException(fault.Message, fault);
            if (delay > 0)
                script.AddSleep(delay);
            return result;
        }

        /// <summary>What the script receives when the deadline passes: the failure value of Phlox's shim, as the function's return type.</summary>
        private static Value[] FailureOf(BoundFunction function) => ValuesOf(function.ReturnType, function.FailureValue);

        /// <summary>The value a long-running call recorded with SysReturn, as the function's return type; nothing for a call with no value.</summary>
        private static Value[] RecordedResult(BoundFunction function, SyscallContext syscall)
        {
            if (function.ReturnType == "void")
                return null;
            return syscall.HasResult ? ValuesOf(function.ReturnType, syscall.Result) : new[] { Value.Nil };
        }

        private static Value[] ValuesOf(string returnType, object value)
        {
            if (returnType == "void")
                return null;
            return returnType switch
            {
                "key" => new[] { LslMarshal.OfKey(Convert.ToString(value)) },
                "string" => new[] { LslMarshal.OfString(Convert.ToString(value)) },
                "integer" => new[] { LslMarshal.Of(Convert.ToInt32(value)) },
                "float" => new[] { LslMarshal.Of(Convert.ToSingle(value)) },
                "list" => new[] { LslMarshal.Of(value as LSLList) },
                _ => new[] { Value.Nil },
            };
        }
    }
}
