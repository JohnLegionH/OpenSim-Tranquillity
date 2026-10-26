/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// Where the engine tells the region which events a script handles. The region raises touch, collision,
    /// at_target and the like only for a prim whose script-event flags include them (Scene.ProcessObjectGrab,
    /// the part's own collision checks), and it keeps the flags per script item and unions them per prim.
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

    /// <summary>
    /// The region's script-event flags for the events a state handles: the same mapping Phlox makes
    /// (LSLSystemAPI.MapEventFlag), by the runtime's event names.
    /// </summary>
    internal static class EventFlagMap
    {
        /// <summary>
        /// A handler for touch also asks for touch_start and touch_end: the region gives a prim each of the three only
        /// when it advertises that one (Scene.ProcessObjectGrab, ProcessObjectDeGrab), and SL's touch event is
        /// triggered on touch start, while held, and on touch end.
        /// </summary>
        private static readonly (string Name, scriptEvents Flags)[] Map =
        {
            ("attach", scriptEvents.attach),
            ("state_exit", scriptEvents.state_exit),
            ("timer", scriptEvents.timer),
            ("touch", scriptEvents.touch | scriptEvents.touch_start | scriptEvents.touch_end),
            ("collision", scriptEvents.collision),
            ("collision_end", scriptEvents.collision_end),
            ("collision_start", scriptEvents.collision_start),
            ("control", scriptEvents.control),
            ("dataserver", scriptEvents.dataserver),
            ("email", scriptEvents.email),
            ("http_response", scriptEvents.http_response),
            ("land_collision", scriptEvents.land_collision),
            ("land_collision_end", scriptEvents.land_collision_end),
            ("land_collision_start", scriptEvents.land_collision_start),
            ("at_target", scriptEvents.at_target),
            ("listen", scriptEvents.listen),
            ("money", scriptEvents.money),
            ("moving_end", scriptEvents.moving_end),
            ("moving_start", scriptEvents.moving_start),
            ("not_at_rot_target", scriptEvents.not_at_rot_target),
            ("not_at_target", scriptEvents.not_at_target),
            ("touch_start", scriptEvents.touch_start),
            ("object_rez", scriptEvents.object_rez),
            ("remote_data", scriptEvents.remote_data),
            ("at_rot_target", scriptEvents.at_rot_target),
            ("run_time_permissions", scriptEvents.run_time_permissions),
            ("touch_end", scriptEvents.touch_end),
            ("state_entry", scriptEvents.state_entry),
            ("changed", scriptEvents.changed),
            ("link_message", scriptEvents.link_message),
            ("no_sensor", scriptEvents.no_sensor),
            ("on_rez", scriptEvents.on_rez),
            ("sensor", scriptEvents.sensor),
            ("http_request", scriptEvents.http_request),
            ("transaction_result", scriptEvents.transaction_result),
            ("linkset_data", scriptEvents.linkset_data),
            ("path_update", scriptEvents.path_update),
            ("on_damage", scriptEvents.on_damage),
            ("final_damage", scriptEvents.final_damage),
            ("on_death", scriptEvents.on_death),
            ("game_control", scriptEvents.game_control),
        };

        private static readonly object s_lock = new object();
        private static (ulong Bit, ulong Flags)[] s_bits;

        /// <summary>The flags for a state's handler mask (bit n - 1 for the runtime's event number n).</summary>
        internal static ulong ForMask(ulong stateMask)
        {
            ulong flags = 0;
            foreach ((ulong bit, ulong f) in BitsFor())
                if ((stateMask & bit) != 0)
                    flags |= f;
            return flags;
        }

        private static (ulong Bit, ulong Flags)[] BitsFor()
        {
            lock (s_lock)
            {
                if (s_bits != null)
                    return s_bits;
                var list = new List<(ulong, ulong)>();
                foreach ((string name, scriptEvents flags) in Map)
                {
                    int index = Host.EventIndex(name);
                    if (index >= 1 && index <= 64)
                        list.Add((1UL << (index - 1), (ulong)flags));
                }
                return s_bits = list.ToArray();
            }
        }
    }
}
