/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Reflection;
using Phlox.ScriptEngine;

namespace SLua.ScriptEngine
{
    /// <summary>A script called an ll function this engine cannot run yet.</summary>
    internal sealed class FunctionUnavailableException : Exception
    {
        public FunctionUnavailableException(string function) : base(function + " is not available on this engine yet")
        {
            Function = function;
        }

        public string Function { get; }
    }

    /// <summary>The script API for the functions the engine does not serve: every call is a <see cref="FunctionUnavailableException"/>.</summary>
    internal class UnavailableApi : DispatchProxy
    {
        public static ILSLSystemAPI Create() => Create<ILSLSystemAPI, UnavailableApi>();

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            // The question whether a call needs the service has to be answerable: nothing here runs, so nothing is deferred.
            if (targetMethod.Name == nameof(ILSLSystemAPI.NeedsService))
                return false;
            throw new FunctionUnavailableException(targetMethod.Name);
        }
    }
}
