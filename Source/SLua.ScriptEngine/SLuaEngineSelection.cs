/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using OpenSim.Region.Framework.Interfaces;

namespace SLua.ScriptEngine
{
    public partial class SLuaEngine : IScriptEngineSelection
    {
        public IReadOnlyList<string> HeaderAliases => System.Array.Empty<string>();

        public bool ClaimsScript(string scriptSource) => false;
    }
}
