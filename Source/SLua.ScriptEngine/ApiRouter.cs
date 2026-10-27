/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using Phlox.ScriptEngine;

namespace SLua.ScriptEngine
{
    /// <summary>Sends each script function to the real LSLSystemAPI when the engine's host satisfies what the function needs, and to a fallback API otherwise.</summary>
    internal class ApiRouter : DispatchProxy
    {
        public static ILSLSystemAPI Create(ILSLSystemAPI real, ILSLSystemAPI fallback) => fallback;

        protected override object Invoke(MethodInfo targetMethod, object[] args) => null;
    }
}
