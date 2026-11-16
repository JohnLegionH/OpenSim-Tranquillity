/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// Conversions between the Second Life runtime's signature for an ll function (secondlife/lsl-definitions) and
    /// Phlox's, for the functions Phlox implements with a different shape. The list, and the source of each conversion, is
    /// <c>LlCoverage.Adapted</c>; the table in <c>LlBindings.g.cs</c> calls these.
    /// </summary>
    internal static class LlAdapters
    {
        /// <summary>
        /// llManageEstateAccess(action, avatar). The runtime's actions are ESTATE_ACCESS_ALLOWED_AGENT_ADD 4, _REMOVE 8,
        /// ALLOWED_GROUP_ADD 16, _REMOVE 32, BANNED_AGENT_ADD 64, _REMOVE 128; Phlox numbers the same six 0 to 5. Any other
        /// action is passed as -1, which Phlox answers with FALSE. The result (TRUE or FALSE) is the one Phlox records with
        /// SysReturn.
        /// </summary>
        public static Value[] ManageEstateAccess(ScriptContext c, ReadOnlySpan<Value> a)
        {
            LslMarshal.Arity(a, 2, "ManageEstateAccess");
            int action = LslMarshal.Int(a, 0, "ManageEstateAccess") switch
            {
                4 => 0,
                8 => 1,
                16 => 2,
                32 => 3,
                64 => 4,
                128 => 5,
                _ => -1,
            };
            c.Api.llManageEstateAccess(action, LslMarshal.Key(a, 1, "ManageEstateAccess"));
            return null;
        }

        /// <summary>llOpenFloater(floater_name, url, params): Phlox's has the same arguments and no result; the runtime's result is 0 for success.</summary>
        public static Value[] OpenFloater(ScriptContext c, ReadOnlySpan<Value> a)
        {
            LslMarshal.Arity(a, 3, "OpenFloater");
            c.Api.llOpenFloater(LslMarshal.String(a, 0, "OpenFloater"), LslMarshal.String(a, 1, "OpenFloater"), LslMarshal.List(a, 2, "OpenFloater"));
            return new[] { LslMarshal.Of(0) };
        }

        /// <summary>
        /// llSetGroundTexture(changes). The changes are pairs of a TERRAIN_* number and its value (wiki LlSetGroundTexture):
        /// 0 to 3 (TERRAIN_DETAIL_1 to 4) take one texture, 4 to 7 (height ranges) two numbers, 8 to 11 (PBR scale) one
        /// vector, 12 to 15 (PBR rotation) one number, 16 to 19 (PBR offset) one vector. Phlox sets one corner's texture
        /// (llSetGroundTexture(texture, corner)): each detail change is that call, the others are skipped. The wiki gives the
        /// result as always 0.
        /// </summary>
        public static Value[] SetGroundTexture(ScriptContext c, ReadOnlySpan<Value> a)
        {
            LslMarshal.Arity(a, 1, "SetGroundTexture");
            LSLList changes = LslMarshal.List(a, 0, "SetGroundTexture");
            object[] data = changes.Data;
            for (int i = 0; i < data.Length;)
            {
                int param;
                if (data[i] is int whole) param = whole;
                else if (data[i] is float number && number == Math.Floor(number)) param = (int)number; // an SLua number
                else break; // not a change number: the rest cannot be read
                int values = param >= 4 && param <= 7 ? 2 : 1;
                if (param >= 0 && param <= 3 && i + 1 < data.Length)
                    c.Api.llSetGroundTexture(Convert.ToString(data[i + 1]), param);
                i += 1 + values;
            }
            return new[] { LslMarshal.Of(0) };
        }
    }
}
