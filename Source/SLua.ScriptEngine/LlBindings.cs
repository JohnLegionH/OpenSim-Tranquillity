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
        /// <summary>The ll functions that answer a script with "not available on this engine yet": the ones the host cannot serve and the ones not bound.</summary>
        internal static readonly System.Collections.Generic.IReadOnlyCollection<string> Unavailable = ListUnavailable();

        private static string[] ListUnavailable()
        {
            var names = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
            foreach (string row in LlServed.NotServed) names.Add(NameOf(row));
            foreach (string name in LlCoverage.MissingFromSeam) names.Add(name);
            foreach (string row in LlCoverage.SignatureMismatch) names.Add(NameOf(row));
            var list = new string[names.Count];
            names.CopyTo(list);
            return list;
        }

        /// <summary>The function name at the start of a generated row ("llSay: SendToWorldComm").</summary>
        private static string NameOf(string row) => row.Substring(0, row.IndexOf(':'));

        /// <summary>
        /// Registers every bound function in the VM's "ll" table and again in its "llcompat" table. With
        /// <paramref name="serviceLane"/>, the long-running (async) and service-reaching functions are registered as pausing
        /// calls whose work runs on the context's service lane while the script waits; every other function, and those two
        /// kinds without a lane, run on the script worker. With <paramref name="unavailable"/>, the functions in
        /// <see cref="Unavailable"/> (bound or not) are registered as errors instead.
        /// </summary>
        /// <remarks>
        /// The two tables differ in how they count positions, as in the Second Life runtime (VM/src/lll.cpp registers its
        /// functions in "ll" with compatibility mode off and in "llcompat" with it on): in "ll", an SLua script counts from 1
        /// and a failed search is nil; in "llcompat", and in "ll" for an LSL script, positions are LSL's (from 0, -1 for no
        /// match). The API underneath always sees LSL's.
        /// </remarks>
        public static void Register(Vm vm, Func<SchedulerScript, ScriptContext> contextOf, bool serviceLane = false, bool unavailable = false)
        {
            RegisterTable(vm, "ll", compat: false, contextOf, serviceLane, unavailable);
            RegisterTable(vm, "llcompat", compat: true, contextOf, serviceLane, unavailable);
        }

        private static void RegisterTable(Vm vm, string table, bool compat, Func<SchedulerScript, ScriptContext> contextOf, bool serviceLane, bool unavailable)
        {
            var refused = unavailable ? new System.Collections.Generic.HashSet<string>(Unavailable, StringComparer.Ordinal) : null;
            if (refused != null)
            {
                // These answer the script with an error that names the function; they never reach the API or the lane.
                foreach (string name in Unavailable)
                {
                    string apiName = name;
                    vm.Register(table, apiName.Substring(2), (tag, args) => throw new FunctionUnavailableException(apiName));
                }
            }

            foreach (BoundFunction f in All)
            {
                if (refused != null && refused.Contains(f.ApiName))
                    continue;
                BoundFunction function = f;
                LlIndexes.Arguments.TryGetValue(function.ApiName, out int[] indexArguments);
                bool indexResult = Array.IndexOf(LlIndexes.Returns, function.ApiName) >= 0;
                bool booleanResult = Array.IndexOf(LlBooleans.Returns, function.ApiName) >= 0;
                bool booleanList = LlBooleans.ListRules.ContainsKey(function.ApiName);
                LlIndexes.ListStrides.TryGetValue(function.ApiName, out ListPositions listPositions);
                // The "ll" table gives an SLua script its positions and truth values (the definitions' index and bool marks);
                // "llcompat", and "ll" for an LSL script, keep LSL's. The API underneath always sees LSL's.
                bool Shifts(ScriptContext context) => !compat && context.Script.Language == ScriptLanguage.Slua;

                if (serviceLane && function.Class is FunctionClass.Async or FunctionClass.Service)
                {
                    vm.RegisterPausing(table, function.Name,
                        (tag, args) =>
                        {
                            ScriptContext context = contextOf(SchedulerScript.Current);
                            ReadOnlySpan<Value> given = Shifts(context) && indexArguments != null ? ShiftArguments(function.Name, indexArguments, args) : args;
                            return StartLaneCall(function, context, given);
                        },
                        (tag, args) =>
                        {
                            Value[] answer = TakeLaneAnswer();
                            if (!Shifts(contextOf(SchedulerScript.Current)))
                                return answer;
                            return listPositions != null ? ShiftListResult(answer, listPositions) : booleanResult ? ToBoolean(answer) : answer;
                        });
                    continue;
                }

                vm.Register(table, function.Name, (tag, args) =>
                {
                    ScriptContext context = contextOf(SchedulerScript.Current);
                    bool shift = Shifts(context);
                    ReadOnlySpan<Value> given = shift && indexArguments != null ? ShiftArguments(function.Name, indexArguments, args) : args;
                    Value[] result = RunInContext(function, context, given, out double delay);
                    if (delay > 0) context.Script.AddSleep(delay);
                    if (!shift) return result;
                    return indexResult ? ShiftResult(result) : listPositions != null ? ShiftListResult(result, listPositions) : booleanResult ? ToBoolean(result)
                        : booleanList ? ConvertListBooleans(function.ApiName, RequestList(given), result) : result;
                });
            }
        }

        /// <summary>A whole number in a script's list: an SLua number is a Number, and a list from the API holds Ints.</summary>
        private static bool IntegerOf(Value v, out long n)
        {
            n = v.Type == Bindings.ValueType.Int ? v.I : v.Type == Bindings.ValueType.Number ? (long)v.N : 0;
            return v.Type == Bindings.ValueType.Int || (v.Type == Bindings.ValueType.Number && v.N == n);
        }

        /// <summary>The items of the last list among a call's arguments: the request of a function that reads a list of tags.</summary>
        private static Value[] RequestList(ReadOnlySpan<Value> args)
        {
            for (int i = args.Length - 1; i >= 0; i--)
                if (args[i].Type == Bindings.ValueType.List)
                    return args[i].Items;
            return null;
        }

        /// <summary>A truth value for an SLua script (VM/src/llsl.cpp luaSL_pushboollike): an integer result becomes true or false.</summary>
        private static Value[] ToBoolean(Value[] result)
        {
            if (result == null || result.Length == 0 || result[0].Type != Bindings.ValueType.Int)
                return result;
            return new[] { Value.Bool(result[0].I != 0) };
        }

        /// <summary>
        /// An SLua script's positions to LSL's (VM/src/llsl.cpp luaSL_checkindexlike): 1 and up count from the start and become
        /// 0 and up, negative positions count from the end and stay as they are, and 0 is an error.
        /// </summary>
        internal static Value[] ShiftArguments(string function, int[] positions, ReadOnlySpan<Value> args)
        {
            Value[] shifted = args.ToArray();
            foreach (int at in positions)
            {
                if (at >= shifted.Length)
                    continue;
                Value v = shifted[at];
                if (v.Type != Bindings.ValueType.Int && v.Type != Bindings.ValueType.Number)
                    continue; // not a number: the marshalling reports it as it does for any argument
                long n = v.Type == Bindings.ValueType.Int ? v.I : (long)v.N;
                if (n == 0)
                    throw new ArgumentException("passed 0 when a 1-based index was expected");
                shifted[at] = n > 0 ? Value.Int(n - 1) : Value.Int(n);
            }
            return shifted;
        }

        /// <summary>
        /// A strided list result whose elements at <see cref="ListPositions.Positions"/> are positions, for an SLua script: those
        /// elements count from 1 (LSL's 0 and up become 1 and up). The other elements, and a list that is not made of such
        /// strides (a "NAK" answer, an empty list), are left as they are.
        /// </summary>
        internal static Value[] ShiftListResult(Value[] result, ListPositions positions)
        {
            if (result == null || result.Length == 0 || result[0].Type != Bindings.ValueType.List)
                return result;
            Value[] items = result[0].Items;
            if (items == null || items.Length == 0 || items.Length % positions.Stride != 0)
                return result;
            var shifted = (Value[])items.Clone();
            for (int stride = 0; stride < shifted.Length; stride += positions.Stride)
            {
                foreach (int at in positions.Positions)
                {
                    Value v = shifted[stride + at];
                    if (v.Type == Bindings.ValueType.Int && v.I >= 0)
                        shifted[stride + at] = Value.Int(v.I + 1);
                }
            }
            return new[] { Value.List(shifted) };
        }

        /// <summary>
        /// The truth values of a list result, for an SLua script. The definitions mark these functions' list results bool-semantics
        /// and give each request tag the type of the value that follows it (<see cref="LlBooleans.Tags"/>), so a value that
        /// the tag's layout says is a boolean becomes true or false (an integer other than 0 is true) and every other element is
        /// left as it was. A tag that takes a face returns its values once for the face, or once for each face of the prim for
        /// ALL_SIDES; the number of faces is what the result's length leaves after the other tags. Where the request does not
        /// say where an element is (a tag the table does not know, or one whose number of values the definitions do not give),
        /// the elements before that tag are converted and the rest are left; and a result whose length does not fit the request is
        /// left as it is.
        /// </summary>
        internal static Value[] ConvertListBooleans(string function, Value[] request, Value[] result)
        {
            if (request == null || result == null || result.Length != 1 || result[0].Type != Bindings.ValueType.List)
                return result;
            Value[] items = result[0].Items;
            if (items == null || items.Length == 0
                || !LlBooleans.ListRules.TryGetValue(function, out string rule) || !LlBooleans.Tags.TryGetValue(rule, out var tags))
                return result;

            // The request's tags in order, as far as the table places them.
            var layouts = new System.Collections.Generic.List<TagLayout>();
            var allSides = new System.Collections.Generic.List<bool>();
            bool placed = true;
            for (int at = 0; at < request.Length && placed;)
            {
                if (!IntegerOf(request[at], out long tag) || !tags.TryGetValue(tag, out TagLayout layout) || layout.Cells.Contains('?'))
                {
                    placed = false;
                    break;
                }
                if (layout.PerFace)
                {
                    if (at + 1 >= request.Length || !IntegerOf(request[at + 1], out long face))
                    {
                        placed = false;
                        break;
                    }
                    allSides.Add(face == -1);
                    at += 2;
                }
                else
                {
                    allSides.Add(false);
                    at += 1;
                }
                layouts.Add(layout);
            }

            int faces = 1;
            if (placed)
            {
                int fixedCount = 0, perFace = 0;
                for (int i = 0; i < layouts.Count; i++)
                {
                    if (allSides[i]) perFace += layouts[i].Cells.Length;
                    else fixedCount += layouts[i].Cells.Length;
                }
                if (perFace == 0 ? items.Length != fixedCount : items.Length <= fixedCount || (items.Length - fixedCount) % perFace != 0)
                    return result;
                if (perFace > 0) faces = (items.Length - fixedCount) / perFace;
            }
            else
            {
                // The elements after an all-sides tag or an unplaced tag cannot be found: keep the tags before the first of them.
                int keep = allSides.IndexOf(true);
                if (keep >= 0) layouts.RemoveRange(keep, layouts.Count - keep);
            }

            Value[] converted = null;
            int position = 0;
            for (int i = 0; i < layouts.Count; i++)
            {
                for (int face = 0; face < (allSides[i] ? faces : 1); face++)
                {
                    foreach (char cell in layouts[i].Cells)
                    {
                        if (position >= items.Length)
                            return converted == null ? result : new[] { Value.List(converted) };
                        if (cell == 'b' && items[position].Type == Bindings.ValueType.Int)
                        {
                            converted ??= (Value[])items.Clone();
                            converted[position] = Value.Bool(items[position].I != 0);
                        }
                        position++;
                    }
                }
            }
            return converted == null ? result : new[] { Value.List(converted) };
        }

        /// <summary>LSL's position to an SLua script's (VM/src/llsl.cpp luaSL_pushindexlike): -1, no match, is nil; 0 and up count from 1.</summary>
        internal static Value[] ShiftResult(Value[] result)
        {
            if (result == null || result.Length == 0 || result[0].Type != Bindings.ValueType.Int)
                return result;
            long n = result[0].I;
            return new[] { n == -1 ? Value.Nil : n >= 0 ? Value.Int(n + 1) : Value.Int(n) };
        }

        /// <summary>
        /// Runs the call on the calling thread inside a SyscallContext for its script, which is where LSLSystemAPI puts the
        /// delay it asks for (its ScriptSleep adds to the context of the call it is running in; Phlox does the same for a call
        /// that runs off its script's thread). Returns the call's values and its delay in seconds. A call that returns no values
        /// also ends the script's turn when the delay is given to the runtime; one that returns values keeps them and the delay
        /// takes effect at the script's next interrupt, and two such delays add up.
        /// </summary>
        internal static Value[] RunInContext(BoundFunction function, ScriptContext context, ReadOnlySpan<Value> args, out double delaySeconds)
        {
            var call = new SyscallContext(context.ItemId, SyscallContext.NextSeq());
            Value[] result;
            call.Enter();
            try { result = function.Call(context, args); }
            finally { SyscallContext.Exit(); }
            delaySeconds = call.DelayMs / 1000.0;
            // A long-running call run here (no service lane) gives its value with SysReturn, as it does on the lane.
            if (function.Class == FunctionClass.Async && result == null)
                result = RecordedResult(function, call);
            return result;
        }

        // ---- the service lane ----

        /// <summary>
        /// The start of a pausing call. A long-running call always goes to the lane (Phlox: RunAsync); a service-reaching call
        /// goes when the API says it needs the service (Phlox: Defer, which asks ISyscallDeferralAdvisor.NeedsService) and
        /// deferral is not off; otherwise it runs here, on the worker, as in Phlox. A call that goes to the lane parks its
        /// script until the lane answers.
        /// </summary>
        internal static Value[] StartLaneCall(BoundFunction function, ScriptContext context, ReadOnlySpan<Value> args)
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
        internal static Value[] TakeLaneAnswer()
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
