/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// Conversions between the values that cross the native boundary and the types ILSLSystemAPI takes and returns. A wrong
    /// argument throws an ArgumentException that names the function and the argument; the host entry turns it into a script
    /// error. LSL integers are 32 bits and LSL floats are 32-bit (a double beyond their range becomes infinity). A key is a
    /// string in ILSLSystemAPI and inside a list, as it is in Phlox, so a key element of a list comes back as a string element.
    /// </summary>
    internal static class LslMarshal
    {
        /// <summary>Requires at least <paramref name="count"/> arguments; extra ones are ignored.</summary>
        public static void Arity(ReadOnlySpan<Value> a, int count, string function)
        {
            if (a.Length < count)
                throw new ArgumentException($"missing argument #{a.Length + 1} to '{function}' (expected {count}, got {a.Length})");
        }

        // ---- script to API ----

        public static int Int(ReadOnlySpan<Value> a, int i, string function)
        {
            Value v = At(a, i, function);
            switch (v.Type)
            {
                case Bindings.ValueType.Int:
                    return unchecked((int)v.I);
                case Bindings.ValueType.Bool:
                    return v.I != 0 ? 1 : 0;
                case Bindings.ValueType.Number:
                    return Saturate(v.N);
                default:
                    throw Wrong(i, function, "integer", v);
            }
        }

        public static float Float(ReadOnlySpan<Value> a, int i, string function)
        {
            Value v = At(a, i, function);
            switch (v.Type)
            {
                case Bindings.ValueType.Number:
                    return (float)v.N;
                case Bindings.ValueType.Int:
                    return (float)v.I;
                default:
                    throw Wrong(i, function, "float", v);
            }
        }

        public static string String(ReadOnlySpan<Value> a, int i, string function) => Text(a, i, function, "string");

        public static string Key(ReadOnlySpan<Value> a, int i, string function) => Text(a, i, function, "key");

        public static Vector3 Vector(ReadOnlySpan<Value> a, int i, string function)
        {
            Value v = At(a, i, function);
            if (v.Type != Bindings.ValueType.Vector) throw Wrong(i, function, "vector", v);
            return new Vector3(v.X, v.Y, v.Z);
        }

        public static Quaternion Rotation(ReadOnlySpan<Value> a, int i, string function)
        {
            Value v = At(a, i, function);
            if (v.Type != Bindings.ValueType.Rotation) throw Wrong(i, function, "rotation", v);
            return new Quaternion(v.X, v.Y, v.Z, v.W);
        }

        public static LSLList List(ReadOnlySpan<Value> a, int i, string function)
        {
            Value v = At(a, i, function);
            if (v.Type != Bindings.ValueType.List) throw Wrong(i, function, "list", v);
            Value[] items = v.Items ?? Array.Empty<Value>();
            var members = new List<object>(items.Length);
            foreach (Value item in items)
                members.Add(Element(item, i, function));
            return new LSLList(members);
        }

        // ---- API to script ----

        public static Value Of(int v) => Value.Int(v);

        public static Value Of(float v) => Value.Num(v);

        public static Value OfString(string v) => Value.Str(v ?? "");

        public static Value OfKey(string v) => Value.Key(v ?? "");

        public static Value Of(Vector3 v) => Value.Vec(v.X, v.Y, v.Z);

        public static Value Of(Quaternion v) => Value.Rot(v.X, v.Y, v.Z, v.W);

        public static Value Of(LSLList v)
        {
            if (v == null) return Value.List();
            object[] members = v.Data;
            var items = new Value[members.Length];
            for (int k = 0; k < items.Length; k++)
            {
                switch (members[k])
                {
                    case int n: items[k] = Of(n); break;
                    case float f: items[k] = Of(f); break;
                    case string s: items[k] = OfString(s); break;
                    case Vector3 vec: items[k] = Of(vec); break;
                    case Quaternion rot: items[k] = Of(rot); break;
                    default:
                        throw new ArgumentException("a list returned by the script function holds an element that is not an integer, float, string, vector or rotation ("
                            + (members[k]?.GetType().Name ?? "null") + ")");
                }
            }
            return Value.List(items);
        }

        // ---- helpers ----

        private static Value At(ReadOnlySpan<Value> a, int i, string function)
        {
            if (i >= a.Length) throw new ArgumentException($"missing argument #{i + 1} to '{function}'");
            return a[i];
        }

        private static string Text(ReadOnlySpan<Value> a, int i, string function, string expected)
        {
            Value v = At(a, i, function);
            if (v.Type != Bindings.ValueType.String && v.Type != Bindings.ValueType.Key) throw Wrong(i, function, expected, v);
            return v.S ?? "";
        }

        private static object Element(Value item, int i, string function)
        {
            switch (item.Type)
            {
                case Bindings.ValueType.Int: return unchecked((int)item.I);
                case Bindings.ValueType.Bool: return item.I != 0 ? 1 : 0;
                case Bindings.ValueType.Number: return (float)item.N;
                case Bindings.ValueType.String:
                case Bindings.ValueType.Key: return item.S ?? "";
                case Bindings.ValueType.Vector: return new Vector3(item.X, item.Y, item.Z);
                case Bindings.ValueType.Rotation: return new Quaternion(item.X, item.Y, item.Z, item.W);
                case Bindings.ValueType.List:
                    throw new ArgumentException($"bad argument #{i + 1} to '{function}' (a list cannot hold a list)");
                default:
                    throw new ArgumentException($"bad argument #{i + 1} to '{function}' (a list cannot hold {Name(item.Type)})");
            }
        }

        /// <summary>A double to an integer the way a C# cast on the Phlox side reads it, with the ends held: NaN is 0.</summary>
        private static int Saturate(double n)
        {
            if (double.IsNaN(n)) return 0;
            if (n >= int.MaxValue) return int.MaxValue;
            if (n <= int.MinValue) return int.MinValue;
            return (int)n;
        }

        private static ArgumentException Wrong(int i, string function, string expected, Value got) =>
            new ArgumentException($"bad argument #{i + 1} to '{function}' ({expected} expected, got {Name(got.Type)})");

        private static string Name(Bindings.ValueType t) => t switch
        {
            Bindings.ValueType.Nil => "nil",
            Bindings.ValueType.Bool => "boolean",
            Bindings.ValueType.Int => "integer",
            Bindings.ValueType.Number => "number",
            Bindings.ValueType.String => "string",
            Bindings.ValueType.Vector => "vector",
            Bindings.ValueType.Key => "key",
            Bindings.ValueType.Rotation => "rotation",
            Bindings.ValueType.List => "list",
            _ => "unknown",
        };
    }
}
