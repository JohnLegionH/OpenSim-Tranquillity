/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using Phlox.ScriptEngine;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// Sends each script function to the real LSLSystemAPI when the engine's host satisfies what the function needs
    /// (<see cref="LlServed.Served"/>, written by the binding generator from the Phlox source), and to a fallback API otherwise.
    /// The question whether a call needs the service goes where the call will go.
    /// </summary>
    internal class ApiRouter : DispatchProxy
    {
        private static readonly HashSet<string> s_served = new HashSet<string>(LlServed.Served, StringComparer.Ordinal);

        /// <summary>Whether the function runs on the real API (a stub for now: nothing is known to be served).</summary>
        internal static bool Serves(string function) => false;

        private ILSLSystemAPI m_real;
        private ILSLSystemAPI m_fallback;

        public static ILSLSystemAPI Create(ILSLSystemAPI real, ILSLSystemAPI fallback)
        {
            ILSLSystemAPI api = Create<ILSLSystemAPI, ApiRouter>();
            var router = (ApiRouter)(object)api;
            router.m_real = real;
            router.m_fallback = fallback;
            return api;
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            string function = targetMethod.Name == nameof(ILSLSystemAPI.NeedsService) && args.Length > 0 ? args[0] as string : targetMethod.Name;
            ILSLSystemAPI target = function != null && s_served.Contains(function) ? m_real : m_fallback;
            try { return targetMethod.Invoke(target, args); }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }
}
