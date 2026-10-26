/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace SLua.ScriptEngine.Bindings;

/// <summary>Raw P/Invoke declarations for slua_shim. Mirrors ngc-host/shim/slua_shim.h.</summary>
internal static unsafe class Native
{
    const string Lib = "slua_shim";

    [StructLayout(LayoutKind.Sequential)]
    internal struct SluaValue
    {
        public int Type;
        public int Len;
        public long I;
        public double N;
        public byte* S;
        public SluaValue* Items;
        public float V0, V1, V2, V3;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StepResultN
    {
        public int Status;
        public int NewState;
        public float SleepServed;
        public int FaultKind;
    }

    internal static Value ToManaged(in SluaValue v) => (ValueType)v.Type switch
    {
        ValueType.Nil => Value.Nil,
        ValueType.Bool => Value.Bool(v.I != 0),
        ValueType.Int => Value.Int(v.I),
        ValueType.Number => Value.Num(v.N),
        ValueType.String => Value.Str(v.S == null ? "" : System.Text.Encoding.UTF8.GetString(v.S, v.Len)),
        ValueType.Vector => Value.Vec(v.V0, v.V1, v.V2),
        ValueType.Key => Value.Key(v.S == null ? "" : System.Text.Encoding.UTF8.GetString(v.S, v.Len)),
        ValueType.Rotation => Value.Rot(v.V0, v.V1, v.V2, v.V3),
        ValueType.List => Value.List(ListItems(v)),
        _ => Value.Nil,
    };

    static Value[] ListItems(in SluaValue v)
    {
        var items = new Value[v.Len];
        for (int i = 0; i < items.Length; i++) items[i] = ToManaged(v.Items[i]);
        return items;
    }

    internal static void FromManaged(in Value m, SluaValue* o, Arena arena)
    {
        *o = default;
        o->Type = (int)m.Type;
        switch (m.Type)
        {
            case ValueType.Bool:
            case ValueType.Int: o->I = m.I; break;
            case ValueType.Number: o->N = m.N; break;
            case ValueType.String: o->S = arena.Utf8(m.S ?? "", out o->Len); break;
            case ValueType.Vector: o->V0 = m.X; o->V1 = m.Y; o->V2 = m.Z; break;
            case ValueType.Key: o->S = arena.Utf8(m.S ?? "", out o->Len); break;
            case ValueType.Rotation: o->V0 = m.X; o->V1 = m.Y; o->V2 = m.Z; o->V3 = m.W; break;
            case ValueType.List:
            {
                Value[] items = m.Items ?? Array.Empty<Value>();
                o->Len = items.Length;
                o->Items = (SluaValue*)arena.Alloc((nuint)(Math.Max(items.Length, 1) * sizeof(SluaValue)));
                for (int i = 0; i < items.Length; i++) FromManaged(items[i], &o->Items[i], arena);
                break;
            }
        }
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_version(byte* buf, int cap);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_init();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_api_version();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_timer_interval(IntPtr s, double* seconds);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_struct_size(int which);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_image_new(IntPtr vm, byte* asset, nuint len, IntPtr* o);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_image_free(IntPtr img);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_script_new_from_image(IntPtr img, int memLimit, long tag, IntPtr* o);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_event_index([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_dispatch_event(IntPtr s, int ev, SluaValue* args, int nargs, double quanta, StepResultN* r);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_compile(byte* src, nuint len, int isLsl, byte** o, nuint* outLen, byte** err);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void slua_free(void* p);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_debug_alloc_stats(ulong* o, int n);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_vm_new(int watchdog, IntPtr* o);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_vm_free(IntPtr vm);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int slua_register_host_fn(IntPtr vm, [MarshalAs(UnmanagedType.LPUTF8Str)] string table, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int fnId, delegate* unmanaged[Cdecl]<void*, int, long, SluaValue*, int, SluaValue*, int, int> cb, void* user);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int slua_register_pausing_fn(IntPtr vm, [MarshalAs(UnmanagedType.LPUTF8Str)] string table, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int startId, int takeId, delegate* unmanaged[Cdecl]<void*, int, long, SluaValue*, int, SluaValue*, int, int> cb, void* user);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_add_sleep(IntPtr s, double seconds);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_watchdog_stats(IntPtr vm, ulong* fires, ulong* late);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_script_new(IntPtr vm, byte* asset, nuint len, int memLimit, long tag, IntPtr* o);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_script_free(IntPtr s);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_script_start(IntPtr s);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_step(IntPtr s, double quanta, StepResultN* r);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_kill(IntPtr s);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_serialize(IntPtr s, byte** o, nuint* len);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_restore(IntPtr s, byte* data, nuint len);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_fault(IntPtr s, int* kind, byte* buf, int cap);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_used_memory(IntPtr s);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_get_memory_limit(IntPtr s);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_set_memory_limit(IntPtr s, int limit);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int slua_busy_violations();
}
