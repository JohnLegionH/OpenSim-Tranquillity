/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace SLua.ScriptEngine
{
    /// <summary>Where the engine tells a script's owner what went wrong; the engine's tests substitute a recorder.</summary>
    internal interface IScriptMessages
    {
        /// <summary>Says <paramref name="text"/> on DEBUG_CHANNEL from the script's prim; the region's chat delivers it to the prim's owner only.</summary>
        void ShoutError(uint localId, UUID itemId, string text);

        /// <summary>Shows the owner of the script's prim a pop-up with the compile errors nobody collected through the editor.</summary>
        void CompileFailed(uint localId, UUID itemId, IReadOnlyList<string> errors);
    }

    /// <summary>
    /// The region's chat and dialog paths. A script error is object chat on DEBUG_CHANNEL (the channel the Second Life
    /// wiki sets aside for script errors); the region's chat module delivers object chat on that channel to the
    /// object's owner only. A compile failure goes to the owner as an alert, once, with the object, the script and the
    /// errors, as the other engines do for a failure nobody collected.
    /// </summary>
    internal sealed class SceneScriptMessages : IScriptMessages
    {
        private const int DebugChannel = 0x7fffffff;

        /// <summary>Errors shown in one alert; the rest are counted.</summary>
        private const int MaxErrorsShown = 10;

        private readonly Scene m_scene;

        public SceneScriptMessages(Scene scene) => m_scene = scene;

        public void ShoutError(uint localId, UUID itemId, string text)
        {
            SceneObjectPart part = m_scene?.GetSceneObjectPart(localId);
            if (part == null)
                return;
            m_scene.SimChat("Script error: " + text, ChatTypeEnum.Say, DebugChannel, part.AbsolutePosition, part.Name, part.UUID, false);
        }

        public void CompileFailed(uint localId, UUID itemId, IReadOnlyList<string> errors)
        {
            if (errors == null || errors.Count == 0)
                return;
            SceneObjectPart part = m_scene?.GetSceneObjectPart(localId);
            IDialogModule dialogs = m_scene?.RequestModuleInterface<IDialogModule>();
            if (part == null || dialogs == null)
                return;
            string scriptName = part.Inventory?.GetInventoryItem(itemId)?.Name;
            dialogs.SendAlertToUser(part.OwnerID, Describe(part.Name, scriptName, errors), false);
        }

        /// <summary>One message per failed compile however many errors it carried: the object, the script, then each error with its position.</summary>
        internal static string Describe(string objectName, string scriptName, IReadOnlyList<string> errors)
        {
            var sb = new StringBuilder();
            sb.Append(string.IsNullOrEmpty(objectName) ? "Object" : objectName);
            sb.Append(" [");
            sb.Append(string.IsNullOrEmpty(scriptName) ? "Script" : scriptName);
            sb.Append("]: script failed to compile");
            for (int i = 0; i < errors.Count && i < MaxErrorsShown; i++)
            {
                sb.Append(Environment.NewLine);
                sb.Append(errors[i]);
            }
            if (errors.Count > MaxErrorsShown)
            {
                sb.Append(Environment.NewLine);
                sb.Append("... and ").Append(errors.Count - MaxErrorsShown).Append(" more; the rest are in the region log.");
            }
            return sb.ToString();
        }
    }
}
