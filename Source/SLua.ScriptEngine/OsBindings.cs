/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// The os, iw and bot functions bound to ILSLSystemAPI. The table (<c>All</c>) and the host list (<c>HostLines</c>) are
    /// generated from Phlox's compiler table; see tools/slua-bindings. An SLua script calls <c>ossl.X</c>, <c>iw.X</c> and
    /// <c>bot.X</c>; an LSL script calls the function by its plain name, and the library's LSL compiler, given the host list,
    /// compiles the call to the same function. Positions are not shifted and results are not turned into booleans: Second Life
    /// defines none of these functions, so they keep the convention Phlox gives them.
    /// </summary>
    internal static partial class OsBindings
    {
        /// <summary>The functions' signatures as the library's LSL compiler is told them, one line per signature.</summary>
        internal static string HostList => string.Join("\n", HostLines);

        /// <summary>
        /// Gives the library the host list, before the first compile. The list is process-wide in the library and replaces any
        /// earlier one, so calling this again is harmless. Throws <see cref="SluaException"/> when the library refuses it.
        /// </summary>
        internal static void ApplyHostList() => Host.SetHostFunctions(HostList);

        // ---- the choice among the signatures of one name ----

        /// <summary>
        /// The signature of <paramref name="function"/> that a call with these arguments reaches, chosen as Phlox's compiler
        /// chooses for LSL (Defaults.SelectOverload): only a signature of the call's argument count is a candidate; an argument
        /// of exactly the parameter's type scores 2 and one that reaches it by LSL's implicit conversions (an integer to a float,
        /// a key for a string and the other way round) scores 1; any other argument makes the signature unfit; the best total
        /// wins and two of equal total are an error. A script's number that has no fraction also reaches an integer and a
        /// boolean reaches an integer, with a score of 1, which keeps SLua's own number type from deciding a call.
        /// </summary>
        /// <exception cref="ArgumentException">No signature fits, or two fit equally well; the message names the function.</exception>
        internal static BoundFunction Pick(OsFunction function, ReadOnlySpan<Value> args)
        {
            BoundFunction best = null, tied = null;
            int bestScore = -1;
            foreach (BoundFunction candidate in function.Overloads)
            {
                if (candidate.ParamTypes.Length != args.Length)
                    continue;
                int score = 0;
                for (int i = 0; i < args.Length && score >= 0; i++)
                {
                    int one = Score(args[i].Type, candidate.ParamTypes[i]);
                    score = one == 0 ? -1 : score + one;
                }
                if (score < 0)
                    continue;
                if (score > bestScore) { best = candidate; bestScore = score; tied = null; }
                else if (score == bestScore) tied = candidate;
            }
            if (best == null)
                throw new ArgumentException($"{function.ApiName}: no signature takes ({Given(args)}); it takes {Signatures(function)}");
            if (tied != null)
                throw new ArgumentException($"{function.ApiName}: the arguments ({Given(args)}) fit {Describe(function.ApiName, best)} and {Describe(function.ApiName, tied)} equally well");
            return best;
        }

        /// <summary>2 for a value of the parameter's own type, 1 for one that reaches it by an implicit conversion, 0 for a value that cannot.</summary>
        private static int Score(Bindings.ValueType value, string parameter) => (value, parameter) switch
        {
            (Bindings.ValueType.Int, "integer") => 2,
            (Bindings.ValueType.Int, "float") => 1,
            (Bindings.ValueType.Number, "float") => 2,
            (Bindings.ValueType.Number, "integer") => 1,
            (Bindings.ValueType.Bool, "integer") => 1,
            (Bindings.ValueType.String, "string") => 2,
            (Bindings.ValueType.String, "key") => 1,
            (Bindings.ValueType.Key, "key") => 2,
            (Bindings.ValueType.Key, "string") => 1,
            (Bindings.ValueType.Vector, "vector") => 2,
            (Bindings.ValueType.Rotation, "rotation") => 2,
            (Bindings.ValueType.List, "list") => 2,
            _ => 0,
        };

        private static string Given(ReadOnlySpan<Value> args)
        {
            var names = new string[args.Length];
            for (int i = 0; i < names.Length; i++)
                names[i] = TypeName(args[i].Type);
            return string.Join(", ", names);
        }

        private static string TypeName(Bindings.ValueType t) => t switch
        {
            Bindings.ValueType.Nil => "nil",
            Bindings.ValueType.Bool => "boolean",
            Bindings.ValueType.Int => "integer",
            Bindings.ValueType.Number => "float",
            Bindings.ValueType.String => "string",
            Bindings.ValueType.Key => "key",
            Bindings.ValueType.Vector => "vector",
            Bindings.ValueType.Rotation => "rotation",
            Bindings.ValueType.List => "list",
            _ => "unknown",
        };

        private static string Describe(string apiName, BoundFunction f) => apiName + "(" + string.Join(", ", f.ParamTypes) + ")";

        private static string Signatures(OsFunction function) => string.Join(", ", function.Overloads.Select(o => Describe(function.ApiName, o)));

        // ---- registration ----

        /// <summary>The functions that answer a script with "not available on this engine yet": the ones that reach a host member the engine lacks.</summary>
        internal static readonly IReadOnlyCollection<string> Unavailable = ListUnavailable();

        private static string[] ListUnavailable()
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string row in NotServed)
            {
                int space = row.IndexOf(' ');
                names.Add(row.Substring(space + 1, row.IndexOf('(') - space - 1));
            }
            return names.ToArray();
        }

        /// <summary>
        /// Registers every os, iw and bot function in the VM's ossl, iw and bot tables, each as one function that picks its
        /// signature from the call's arguments. With <paramref name="serviceLane"/>, a function with a long-running or
        /// service-reaching signature is registered as a pausing call whose work runs on the context's service lane while the
        /// script waits (the signature that runs decides, per call); every other function runs on the script worker. With
        /// <paramref name="unavailable"/>, the functions in <see cref="Unavailable"/> answer with an error instead.
        /// </summary>
        public static void Register(Vm vm, Func<SchedulerScript, ScriptContext> contextOf, bool serviceLane = false, bool unavailable = false, IEnumerable<string> refuse = null)
        {
            var refused = unavailable ? new HashSet<string>(Unavailable, StringComparer.Ordinal) : null;
            foreach (OsFunction f in All)
            {
                OsFunction function = f;
                if (refused != null && refused.Contains(function.ApiName))
                {
                    vm.Register(function.Table, function.Name, (tag, args) => throw new FunctionUnavailableException(function.ApiName));
                    continue;
                }

                if (serviceLane && function.Overloads.Any(o => o.Class is FunctionClass.Async or FunctionClass.Service))
                {
                    vm.RegisterPausing(function.Table, function.Name,
                        (tag, args) => LlBindings.StartLaneCall(Pick(function, args), contextOf(SchedulerScript.Current), args),
                        (tag, args) => LlBindings.TakeLaneAnswer());
                    continue;
                }

                vm.Register(function.Table, function.Name, (tag, args) =>
                {
                    BoundFunction signature = Pick(function, args);
                    ScriptContext context = contextOf(SchedulerScript.Current);
                    Value[] result = LlBindings.RunInContext(signature, context, args, out double delay);
                    if (delay > 0) context.Script.AddSleep(delay);
                    return result;
                });
            }
        }
    }
}
