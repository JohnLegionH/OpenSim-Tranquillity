/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */
using System.Runtime.InteropServices;

namespace SLua.ScriptEngine.Bindings;

/// <summary>
/// Raw P/Invoke declarations for slua_shim, the native SLua runtime the UtopiaSkye.SLua package carries. They mirror the
/// library's header, slua_shim.h. Only the calls the engine makes so far are declared: the library's version and API
/// version, its one-time start-up, the engine name it uses in its messages, and the compiler with the call that frees
/// what the compiler returns.
/// </summary>
internal static unsafe class Native
{
    const string Lib = "slua_shim";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_version(byte* buf, int cap);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_init();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_api_version();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_set_engine_identity([MarshalAs(UnmanagedType.LPUTF8Str)] string engine, [MarshalAs(UnmanagedType.LPUTF8Str)] string altEngine, [MarshalAs(UnmanagedType.LPUTF8Str)] string altHeader);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_engine_name(byte* buf, int cap);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_compile(byte* src, nuint len, int isLsl, byte** o, nuint* outLen, byte** err);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void slua_free(void* p);
}
