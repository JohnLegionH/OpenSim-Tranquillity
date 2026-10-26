/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared;

namespace SLua.ScriptEngine
{
    /// <summary>Where the scene hooks post the events they translate: the two entry points the engine offers other engines.</summary>
    internal interface IEventPoster
    {
        bool PostObjectEvent(uint localID, EventParams parms);

        bool PostScriptEvent(UUID itemID, EventParams parms);
    }

    /// <summary>
    /// The region's object and avatar events (touch, changed, collision, land collision, attach, moving, targets,
    /// control, death and damage, frame time) as LSL events posted to the scripts concerned.
    /// </summary>
    internal sealed class SceneHooksA
    {
        public SceneHooksA(Scene scene, IEventPoster poster, bool physicsThrottle, ISchedulerClock clock = null)
        {
        }

        /// <summary>Average of the last ten physics frame times in milliseconds, fed once per frame while the physics throttle is on.</summary>
        public int PhysicsFrameTimeAvg => 0;

        /// <summary>True between Subscribe and Unsubscribe.</summary>
        public bool IsSubscribed => false;

        public void Subscribe()
        {
        }

        public void Unsubscribe()
        {
        }
    }
}
