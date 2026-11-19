/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SLua.ScriptEngine.Bindings;

public enum ValueType { Nil = 0, Bool = 1, Int = 2, Number = 3, String = 4, Vector = 5, Key = 6, Rotation = 7, List = 8 }

/// <summary>A scripting value as it crosses the native boundary.</summary>
public readonly record struct Value(ValueType Type, long I = 0, double N = 0, string S = null, float X = 0, float Y = 0, float Z = 0, float W = 0, Value[] Items = null)
{
    public static Value Nil => default;
    public static Value Bool(bool b) => new(ValueType.Bool, b ? 1 : 0);
    public static Value Int(long i) => new(ValueType.Int, i);
    public static Value Num(double n) => new(ValueType.Number, 0, n);
    public static Value Str(string s) => new(ValueType.String, 0, 0, s);
    public static Value Vec(float x, float y, float z) => new(ValueType.Vector, 0, 0, null, x, y, z);
    public static Value Key(string k) => new(ValueType.Key, 0, 0, k);
    public static Value Rot(float x, float y, float z, float s) => new(ValueType.Rotation, 0, 0, null, x, y, z, s);
    public static Value List(params Value[] items) => new(ValueType.List, 0, 0, null, 0, 0, 0, 0, items);

    public override string ToString() => Type switch
    {
        ValueType.Nil => "nil",
        ValueType.Bool => I != 0 ? "true" : "false",
        ValueType.Int => $"int:{I}",
        ValueType.Number => $"num:{N.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        ValueType.String => $"str:{S}",
        ValueType.Vector => $"vec:<{X},{Y},{Z}>",
        ValueType.Key => $"key:{S}",
        ValueType.Rotation => $"rot:<{X},{Y},{Z},{W}>",
        ValueType.List => "list:[" + string.Join(",", (Items ?? Array.Empty<Value>()).Select(v => v.ToString())) + "]",
        _ => "?",
    };
}

/// <summary>A call into the native library that reported a failure code.</summary>
public class SluaException : Exception
{
    public int Code { get; }
    public SluaException(string what, int code, string detail = null)
        : base($"{what} failed with code {code}" + (detail != null ? $": {detail}" : "")) { Code = code; }
}

/// <summary>The library as a whole: its version, its API version and the names it uses in its messages.</summary>
public static unsafe class Host
{
    internal static void Check(string what, int code)
    {
        if (code < 0) throw new SluaException(what, code);
    }

    /// <summary>
    /// Names the engine for the library's refusal and error text. <paramref name="engineName"/> is this engine; the other two
    /// name the engine that runs LSL scripts using os*, iw* or bot* functions and the script header that selects it. The
    /// library has no built-in names; null or empty leaves a name out. Call before compiling.
    /// </summary>
    public static void SetEngineIdentity(string engineName, string altEngineName = null, string altEngineHeader = null)
    {
        Init();
        Check("slua_set_engine_identity", Native.slua_set_engine_identity(engineName, altEngineName, altEngineHeader));
    }

    /// <summary>The engine name the library holds (empty when none was set).</summary>
    public static string EngineName
    {
        get
        {
            Init();
            byte* buf = stackalloc byte[256];
            Check("slua_engine_name", Native.slua_engine_name(buf, 256));
            return Marshal.PtrToStringUTF8((IntPtr)buf);
        }
    }

    static bool _init;
    static readonly object _initLock = new();

    /// <summary>Starts the library once per process; later calls do nothing.</summary>
    public static void Init()
    {
        lock (_initLock)
        {
            if (_init) return;
            Check("slua_init", Native.slua_init());
            _init = true;
        }
    }

    /// <summary>The library's version text, which names the runtime commit and the API version ("api=5").</summary>
    public static string Version()
    {
        byte* buf = stackalloc byte[256];
        Check("slua_version", Native.slua_version(buf, 256));
        return Marshal.PtrToStringUTF8((IntPtr)buf);
    }

    /// <summary>The API version the loaded library reports. A host written for another version must not call it.</summary>
    public static int ApiVersion() => Native.slua_api_version();

    /// <summary>Compiles source to an asset. Throws SluaException (code -5) with the compiler message on failure.</summary>
    public static byte[] Compile(string source, bool lsl)
    {
        Init();
        byte[] src = Encoding.UTF8.GetBytes(source);
        byte* outp = null; nuint outLen = 0; byte* err = null;
        int rc;
        fixed (byte* sp = src)
            rc = Native.slua_compile(sp, (nuint)src.Length, lsl ? 1 : 0, &outp, &outLen, &err);
        try
        {
            if (rc != 0)
                throw new SluaException("slua_compile", rc, err != null ? Marshal.PtrToStringUTF8((IntPtr)err) : null);
            return new ReadOnlySpan<byte>(outp, (int)outLen).ToArray();
        }
        finally
        {
            if (outp != null) Native.slua_free(outp);
            if (err != null) Native.slua_free(err);
        }
    }
}
