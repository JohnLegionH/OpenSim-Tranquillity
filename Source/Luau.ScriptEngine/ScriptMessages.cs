/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;

namespace Luau.ScriptEngine
{
    /// <summary>Where the engine tells a script's owner what went wrong; the engine's tests substitute a recorder.</summary>
    internal interface IScriptMessages
    {
        /// <summary>Says <paramref name="text"/> on DEBUG_CHANNEL from the script's prim; the region's chat delivers it to the prim's owner only.</summary>
        void ShoutError(uint localId, UUID itemId, string text);

        /// <summary>Shows the owner of the script's prim a pop-up with the compile errors nobody collected through the editor.</summary>
        void CompileFailed(uint localId, UUID itemId, IReadOnlyList<string> errors);
    }

    /// <summary>The region's chat and dialog paths.</summary>
    internal sealed class SceneScriptMessages : IScriptMessages
    {
        private readonly Scene m_scene;

        public SceneScriptMessages(Scene scene) => m_scene = scene;

        public void ShoutError(uint localId, UUID itemId, string text)
        {
        }

        public void CompileFailed(uint localId, UUID itemId, IReadOnlyList<string> errors)
        {
        }
    }
}
