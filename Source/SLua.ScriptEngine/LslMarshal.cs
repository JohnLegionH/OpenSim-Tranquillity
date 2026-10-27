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
    /// <summary>Conversions between the values that cross the native boundary and the types ILSLSystemAPI takes and returns.</summary>
    internal static class LslMarshal
    {
        public static void Arity(ReadOnlySpan<Value> a, int count, string function) { }

        public static int Int(ReadOnlySpan<Value> a, int i, string function) => 0;
        public static float Float(ReadOnlySpan<Value> a, int i, string function) => 0f;
        public static string String(ReadOnlySpan<Value> a, int i, string function) => null;
        public static string Key(ReadOnlySpan<Value> a, int i, string function) => null;
        public static Vector3 Vector(ReadOnlySpan<Value> a, int i, string function) => default;
        public static Quaternion Rotation(ReadOnlySpan<Value> a, int i, string function) => default;
        public static LSLList List(ReadOnlySpan<Value> a, int i, string function) => null;

        public static Value Of(int v) => default;
        public static Value Of(float v) => default;
        public static Value OfString(string v) => default;
        public static Value OfKey(string v) => default;
        public static Value Of(Vector3 v) => default;
        public static Value Of(Quaternion v) => default;
        public static Value Of(LSLList v) => default;
    }
}
