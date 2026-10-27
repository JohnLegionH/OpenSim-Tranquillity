/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine
{
    /// <summary>How a bound function behaves toward the worker thread that runs the script.</summary>
    internal enum FunctionClass
    {
        /// <summary>Returns at once; no forced delay.</summary>
        NonBlocking,

        /// <summary>Returns at once and costs the script a forced delay (the call delay of the Second Life wiki). The delay is whatever LSLSystemAPI asks for when the call runs.</summary>
        Delayed,

        /// <summary>Phlox runs the call off the script's thread (a long-running call such as a rez, an email or an instant message).</summary>
        Async,

        /// <summary>Phlox may hand the call to the region's service lane; it can leave the process (a name lookup, a key-value store call, a permission request).</summary>
        Service,

        /// <summary>The delay or blocking nature could not be read from the Phlox source (a delay that depends on a setting, or on a computed value). The delay still comes from LSLSystemAPI when the call runs.</summary>
        Unplaced,
    }

    internal delegate Value[] BoundCall(ScriptContext context, ReadOnlySpan<Value> args);

    /// <summary>One ll function bound to its ILSLSystemAPI method.</summary>
    internal sealed class BoundFunction
    {
        public BoundFunction(string name, string apiName, FunctionClass functionClass,
            string returnType, string[] paramTypes, BoundCall call)
        {
            Name = name;
            ApiName = apiName;
            Class = functionClass;
            ReturnType = returnType;
            ParamTypes = paramTypes;
            Call = call;
        }

        /// <summary>The name in the script's ll table, without the prefix: "Say".</summary>
        public string Name { get; }

        /// <summary>The ILSLSystemAPI method: "llSay".</summary>
        public string ApiName { get; }

        public FunctionClass Class { get; }

        /// <summary>The LSL return type: void, integer, float, string, key, vector, rotation or list.</summary>
        public string ReturnType { get; }

        /// <summary>The LSL parameter types, in order.</summary>
        public string[] ParamTypes { get; }

        /// <summary>Converts the arguments, calls the API method and converts what it returns. The API asks for any delay (ScriptSleep) itself.</summary>
        public BoundCall Call { get; }
    }
}
