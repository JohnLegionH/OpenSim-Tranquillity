/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Phlox.ScriptEngine;

namespace SLua.ScriptEngine
{
    /// <summary>Stub: the receiver of the function trampolines.</summary>
    internal sealed class ScriptContext
    {
        public ScriptContext(SchedulerScript script, ILSLSystemAPI api) { }

        public SchedulerScript Script => null;

        public ILSLSystemAPI Api => null;
    }
}
