/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using SLua.ScriptEngine.Bindings;
using OpenMetaverse;
using OpenSim.Region.ScriptEngine.Shared;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// Turns the arguments of a posted event (the types the region's other engines and plugins pass: CLR numbers and
    /// strings, OpenMetaverse and LSL vectors, LSL value types, lists as arrays) into the runtime's values. An argument
    /// the event declares as a key becomes a key whether it arrives as a UUID or as text.
    /// </summary>
    internal static class EventArguments
    {
        // Positions of the key parameters of each event, taken from the runtime's event list through the generated
        // EventSignatures table (tools/slua-events/generate-event-signatures.py).
        private static readonly Dictionary<string, int[]> s_keyPositions = KeyPositionsOf(EventSignatures.Parameters);

        private static Dictionary<string, int[]> KeyPositionsOf(IReadOnlyDictionary<string, string[]> parameters)
        {
            var positions = new Dictionary<string, int[]>();
            foreach (KeyValuePair<string, string[]> e in parameters)
            {
                var keys = new List<int>();
                for (int i = 0; i < e.Value.Length; i++)
                {
                    if (e.Value[i] == "key")
                        keys.Add(i);
                }
                if (keys.Count > 0)
                    positions[e.Key] = keys.ToArray();
            }
            return positions;
        }

        internal static Value[] Convert(string eventName, object[] args)
        {
            if (args == null || args.Length == 0)
                return Array.Empty<Value>();
            s_keyPositions.TryGetValue(eventName, out int[] keys);
            var values = new Value[args.Length];
            for (int i = 0; i < args.Length; i++)
                values[i] = One(args[i], keys != null && Array.IndexOf(keys, i) >= 0);
            return values;
        }

        private static Value One(object a, bool isKey)
        {
            switch (a)
            {
                case null: return Value.Nil;
                case Value v: return v;
                case UUID id: return Value.Key(id.ToString());
                case LSL_Types.key k: return Value.Key(k.value);
                case string s: return isKey ? Value.Key(s) : Value.Str(s);
                case LSL_Types.LSLString ls: return isKey ? Value.Key(ls.m_string) : Value.Str(ls.m_string);
                case int i: return Value.Int(i);
                case long l: return Value.Int(l);
                case uint u: return Value.Int(u);
                case bool b: return Value.Int(b ? 1 : 0);
                case LSL_Types.LSLInteger li: return Value.Int(li.value);
                case float f: return Value.Num(f);
                case double d: return Value.Num(d);
                case LSL_Types.LSLFloat lf: return Value.Num(lf.value);
                case Vector3 v3: return Value.Vec(v3.X, v3.Y, v3.Z);
                case LSL_Types.Vector3 lv: return Value.Vec((float)lv.x, (float)lv.y, (float)lv.z);
                case Quaternion q: return Value.Rot(q.X, q.Y, q.Z, q.W);
                case LSL_Types.Quaternion lq: return Value.Rot((float)lq.x, (float)lq.y, (float)lq.z, (float)lq.s);
                case LSL_Types.list list: return Items(list.Data);
                case object[] array: return Items(array);
                default: return Value.Str(a.ToString());
            }
        }

        private static Value Items(object[] items)
        {
            var values = new Value[items.Length];
            for (int i = 0; i < items.Length; i++)
                values[i] = One(items[i], false);
            return Value.List(values);
        }
    }
}
