/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */
using System;
using System.Runtime.InteropServices;

namespace SLua.ScriptEngine.Bindings;

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
}
