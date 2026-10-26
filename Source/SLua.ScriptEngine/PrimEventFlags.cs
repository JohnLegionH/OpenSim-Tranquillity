/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// Where the engine tells the region which events a script handles. The region raises touch, collision,
    /// at_target and the like only for a prim whose script-event flags include them (Scene.ProcessObjectGrab,
    /// the part own collision checks), and it keeps the flags per script item and unions them per prim.
    /// </summary>
    internal interface IPrimEventFlags
    {
        /// <summary>Sets the flags of one script item on a prim; 0 means the script hears nothing.</summary>
        void Set(uint localId, UUID itemId, ulong flags);

        /// <summary>Forgets the script item's flags on the prim (the script is gone).</summary>
        void Remove(uint localId, UUID itemId);
    }

    /// <summary>The region's own API for it: <see cref="SceneObjectPart.SetScriptEvents"/> and <see cref="SceneObjectPart.RemoveScriptEvents"/>.</summary>
    internal sealed class ScenePrimEventFlags : IPrimEventFlags
    {
        private readonly Scene m_scene;

        public ScenePrimEventFlags(Scene scene) => m_scene = scene;

        public void Set(uint localId, UUID itemId, ulong flags) => m_scene.GetSceneObjectPart(localId)?.SetScriptEvents(itemId, flags);

        public void Remove(uint localId, UUID itemId) => m_scene.GetSceneObjectPart(localId)?.RemoveScriptEvents(itemId);
    }

    /// <summary>The region's script-event flags for the events a state handles.</summary>
    internal static class EventFlagMap
    {
        /// <summary>The flags for a state's handler mask (bit n - 1 for the runtime's event number n).</summary>
        internal static ulong ForMask(ulong stateMask) => 0;
    }
}
