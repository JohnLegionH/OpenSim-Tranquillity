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

public enum StepStatus { Ok = 0, Preempted = 1, StateChange = 2, Fault = 3, NotRun = 4, Refused = 5, Idle = 100 }
public enum FaultKind { None = 0, Runtime = 1, OutOfMemory = 2, Timeout = 3, UnexpectedYield = 4, Unknown = 255 }
public enum Watchdog { Default = 0, Resident = 1, Threaded = 2 }

/// <summary>
/// One step's result. <see cref="SleepServed"/> is the delay the script asked for in this step (API 4: the step that is
/// running reports it). <see cref="CpuSeconds"/> is the CPU time the calling thread used while the script ran (0 for a step
/// that ran nothing; Windows counts it in ticks of about 15 ms). <see cref="HandlersChanged"/> is true when the script's
/// handler mask changed during the step.
/// </summary>
public readonly record struct StepResult(StepStatus Status, int NewState, float SleepServed, FaultKind Fault, double CpuSeconds = 0, bool HandlersChanged = false);

public class SluaException : Exception
{
    public int Code { get; }
    public SluaException(string what, int code, string detail = null)
        : base($"{what} failed with code {code}" + (detail != null ? $": {detail}" : "")) { Code = code; }
}

/// <summary>Host function. Return null or an empty array for no values; return <see cref="Host.Yield"/> to yield the script.</summary>
public delegate Value[] HostFn(long scriptTag, ReadOnlySpan<Value> args);

public static unsafe class Host
{
    public const int RetYield = -1000;
    /// <summary>Return this from a HostFn to yield the script with no values.</summary>
    public static readonly Value[] Yield = new Value[0];

    internal const int Ok = 0;
    internal const int RestoredFresh = 1;

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

    /// <summary>
    /// Gives the LSL compiler the functions the host supplies (os*, iw* and bot* functions), one per line in the form of the
    /// runtime's builtins.txt, for example <c>integer osAddOne( integer n )</c>. A name may be listed once for each of its
    /// signatures. Replaces any earlier list; null or empty removes it. Throws <see cref="SluaException"/> with the library's
    /// message when a line is refused.
    /// </summary>
    public static void SetHostFunctions(string definitions)
    {
        Init();
        byte* err = null;
        int rc = Native.slua_set_host_functions(definitions, &err);
        try
        {
            if (rc != 0)
                throw new SluaException("slua_set_host_functions", rc, err != null ? Marshal.PtrToStringUTF8((IntPtr)err) : null);
        }
        finally
        {
            if (err != null) Native.slua_free(err);
        }
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

    public static void Init()
    {
        lock (_initLock)
        {
            if (_init) return;
            Check("slua_init", Native.slua_init());
            _init = true;
        }
    }

    public static string Version()
    {
        byte* buf = stackalloc byte[256];
        Check("slua_version", Native.slua_version(buf, 256));
        return Marshal.PtrToStringUTF8((IntPtr)buf);
    }

    /// <summary>What the library holds right now; see slua_debug_alloc_stats. A diagnostic for tests and monitoring.</summary>
    public static AllocStats GetAllocStats()
    {
        ulong* v = stackalloc ulong[6];
        Check("slua_debug_alloc_stats", Native.slua_debug_alloc_stats(v, 6));
        return new AllocStats(v[0], v[1], v[2], v[3], v[4], v[5]);
    }

    public static int BusyViolations() => Native.slua_busy_violations();

    /// <summary>The API version the loaded library reports. A host written for another version must not call it.</summary>
    public static int ApiVersion() => Native.slua_api_version();

    /// <summary>sizeof of a native struct as the library sees it (0 = value, 1 = step result).</summary>
    public static int NativeStructSize(int which) => Native.slua_struct_size(which);

    /// <summary>The event's number for <see cref="Script.Dispatch"/>, from its LSL name such as "touch_start".</summary>
    public static int EventIndex(string name)
    {
        Init();
        int rc = Native.slua_event_index(name);
        Check("slua_event_index", rc);
        return rc;
    }

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

public sealed unsafe class Vm : IDisposable
{
    internal IntPtr Handle;
    readonly GCHandle _self;
    readonly List<HostFn> _fns = new();
    readonly List<string> _names = new();

    public Vm(Watchdog watchdog = Watchdog.Default)
    {
        Host.Init();
        IntPtr h;
        Host.Check("slua_vm_new", Native.slua_vm_new((int)watchdog, &h));
        Handle = h;
        _self = GCHandle.Alloc(this);
    }

    public void Register(string table, string name, HostFn fn)
    {
        int id = _fns.Count;
        _fns.Add(fn);
        _names.Add(table + "." + name);
        Host.Check($"slua_register_host_fn({table}.{name})",
            Native.slua_register_host_fn(Handle, table, name, id, &HostEntry.Call, (void*)GCHandle.ToIntPtr(_self)));
    }

    /// <summary>
    /// Registers table.name as a pausing function. A script's call runs <paramref name="start"/> with the call's
    /// arguments, then yields; after the host steps the script again, <paramref name="take"/> runs with no
    /// arguments and its return values are the call's results. Throwing from either raises a script error.
    /// </summary>
    public void RegisterPausing(string table, string name, HostFn start, HostFn take)
    {
        int startId = _fns.Count;
        _fns.Add(start);
        _names.Add(table + "." + name);
        int takeId = _fns.Count;
        _fns.Add(take);
        _names.Add(table + "." + name);
        Host.Check($"slua_register_pausing_fn({table}.{name})",
            Native.slua_register_pausing_fn(Handle, table, name, startId, takeId, &HostEntry.Call, (void*)GCHandle.ToIntPtr(_self)));
    }

    public Script NewScript(byte[] asset, int memoryLimit = 128 * 1024, long tag = 0)
    {
        IntPtr s;
        int rc;
        fixed (byte* ap = asset)
            rc = Native.slua_script_new(Handle, ap, (nuint)asset.Length, memoryLimit, tag, &s);
        Host.Check("slua_script_new", rc);
        return new Script(this, s);
    }

    /// <summary>Loads an asset once so any number of scripts can be started from it.</summary>
    public Image NewImage(byte[] asset)
    {
        IntPtr img;
        int rc;
        fixed (byte* ap = asset)
            rc = Native.slua_image_new(Handle, ap, (nuint)asset.Length, &img);
        Host.Check("slua_image_new", rc);
        return new Image(this, img);
    }

    public (ulong Fires, ulong LateFires) WatchdogStats()
    {
        ulong f = 0, l = 0;
        Host.Check("slua_watchdog_stats", Native.slua_watchdog_stats(Handle, &f, &l));
        return (f, l);
    }

    internal HostFn Fn(int id) => _fns[id];

    /// <summary>The "table.name" the host function with this id was registered under.</summary>
    internal string NameOf(int id) => _names[id];

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            Native.slua_vm_free(Handle);
            Handle = IntPtr.Zero;
            _self.Free();
        }
    }
}

/// <summary>Entry point native code calls for every host function call. Nothing may throw out of it.</summary>
static unsafe class HostEntry
{
    // Strings and arrays handed back to native are copied by the shim before it returns, so one
    // arena per thread, emptied at the start of the next call on that thread, is enough.
    [ThreadStatic] static Arena _arena;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static int Call(void* user, int fnId, long tag, Native.SluaValue* args, int nargs, Native.SluaValue* rets, int maxRets)
    {
        try
        {
            var arena = _arena ??= new Arena();
            arena.Reset();
            var vm = (Vm)GCHandle.FromIntPtr((IntPtr)user).Target;
            Value[] a = new Value[nargs];
            for (int k = 0; k < nargs; k++) a[k] = Native.ToManaged(args[k]);
            SLua.ScriptEngine.SchedulerScript.Current?.NoteHostCall(vm.NameOf(fnId));
            Value[] r = vm.Fn(fnId)(tag, a);
            if (ReferenceEquals(r, Host.Yield)) return Host.RetYield;
            if (r == null) return 0;
            int n = Math.Min(r.Length, maxRets);
            for (int k = 0; k < n; k++)
                Native.FromManaged(r[k], &rets[k], arena);
            return n;
        }
        catch (Exception e)
        {
            try
            {
                // A function the engine cannot run yet says so in its own words; any other failure carries its type.
                string text = e is SLua.ScriptEngine.FunctionUnavailableException ? e.Message : "host function failed: " + e.GetType().Name + ": " + e.Message;
                var m = new Value(ValueType.String, 0, 0, text);
                Native.FromManaged(m, &rets[0], _arena ??= new Arena());
            }
            catch { }
            return -1;
        }
    }
}

/// <summary>A loaded asset shared by the scripts started from it. Scripts keep it alive after it is disposed.</summary>
public sealed unsafe class Image : IDisposable
{
    readonly Vm _vm;
    IntPtr _h;
    internal Image(Vm vm, IntPtr h) { _vm = vm; _h = h; }

    public Script NewScript(int memoryLimit = 0, long tag = 0)
    {
        IntPtr s;
        Host.Check("slua_script_new_from_image", Native.slua_script_new_from_image(_h, memoryLimit, tag, &s));
        return new Script(_vm, s);
    }

    public void Dispose()
    {
        if (_h != IntPtr.Zero)
        {
            Native.slua_image_free(_h);
            _h = IntPtr.Zero;
        }
    }
}

public sealed unsafe class Script : IDisposable
{
    readonly Vm _vm;
    IntPtr _h;
    internal Script(Vm vm, IntPtr h) { _vm = vm; _h = h; }
    internal IntPtr Handle => _h;

    public void Start() => Host.Check("slua_script_start", Native.slua_script_start(_h));

    public StepResult Step(double quantaSeconds = 0.01)
    {
        Native.StepResultN r;
        Host.Check("slua_step", Native.slua_step(_h, quantaSeconds, &r));
        return new((StepStatus)r.Status, r.NewState, r.SleepServed, (FaultKind)r.FaultKind, r.CpuSeconds, r.HandlersChanged != 0);
    }

    /// <summary>Steps until the script has nothing left to run, a fault, or maxSteps steps. Returns the last status that was not Idle.</summary>
    public StepResult RunToIdle(double quantaSeconds = 0.01, int maxSteps = 100000)
    {
        StepResult last = default;
        for (int i = 0; i < maxSteps; i++)
        {
            var r = Step(quantaSeconds);
            if (r.Status == StepStatus.Idle) return last;
            last = r;
            if (r.Status == StepStatus.Fault || r.Status == StepStatus.Refused || r.Status == StepStatus.NotRun) return r;
        }
        return last;
    }

    /// <summary>
    /// Starts an event handler with arguments and runs its first step. Only valid when the script is idle
    /// (no handler in flight, no state change waiting); throws SluaException with code -8 otherwise.
    /// Status NotRun means the script has no handler for the event.
    /// </summary>
    public StepResult Dispatch(int eventIndex, ReadOnlySpan<Value> args, double quantaSeconds = 0.01)
    {
        using var arena = new Arena();
        Native.SluaValue* native = (Native.SluaValue*)arena.Alloc((nuint)(Math.Max(args.Length, 1) * sizeof(Native.SluaValue)));
        for (int i = 0; i < args.Length; i++) Native.FromManaged(args[i], &native[i], arena);
        Native.StepResultN r;
        Host.Check("slua_dispatch_event", Native.slua_dispatch_event(_h, eventIndex, native, args.Length, quantaSeconds, &r));
        return new((StepStatus)r.Status, r.NewState, r.SleepServed, (FaultKind)r.FaultKind, r.CpuSeconds, r.HandlersChanged != 0);
    }

    public StepResult Dispatch(string eventName, ReadOnlySpan<Value> args, double quantaSeconds = 0.01) =>
        Dispatch(Host.EventIndex(eventName), args, quantaSeconds);

    /// <summary>The interval in seconds the script last asked for with LLTimers; 0 when it has no timer.</summary>
    public double TimerInterval
    {
        get
        {
            double seconds;
            Host.Check("slua_timer_interval", Native.slua_timer_interval(_h, &seconds));
            return seconds;
        }
    }

    /// <summary>
    /// Adds a delay to this script: the step that is running reports it in <see cref="StepResult.SleepServed"/>. A call that returns no values also ends the script's turn. Only valid
    /// inside a host function this script called (throws SluaException with code -8 otherwise).
    /// </summary>
    public void AddSleep(double seconds) => Host.Check("slua_add_sleep", Native.slua_add_sleep(_h, seconds));

    /// <summary>The events the script handles now, one bit each (the bit of event index n is 1 &lt;&lt; (n - 1)), and whether that set is complete.</summary>
    public (ulong Mask, bool Known) Handlers()
    {
        ulong mask; int known;
        Host.Check("slua_handler_mask", Native.slua_handler_mask(_h, &mask, &known));
        return (mask, known != 0);
    }

    public void Kill() => Host.Check("slua_kill", Native.slua_kill(_h));

    public byte[] Serialize()
    {
        byte* p = null; nuint len = 0;
        Host.Check("slua_serialize", Native.slua_serialize(_h, &p, &len));
        try { return new ReadOnlySpan<byte>(p, (int)len).ToArray(); }
        finally { Native.slua_free(p); }
    }

    /// <summary>Returns true when the saved state was restored, false when the shim refused it and started the script fresh.</summary>
    public bool Restore(byte[] data)
    {
        int rc;
        fixed (byte* dp = data)
            rc = Native.slua_restore(_h, dp, (nuint)data.Length);
        Host.Check("slua_restore", rc);
        return rc == Host.Ok;
    }

    public (FaultKind Kind, string Message) Fault()
    {
        byte* buf = stackalloc byte[4096];
        int kind;
        Host.Check("slua_fault", Native.slua_fault(_h, &kind, buf, 4096));
        return ((FaultKind)kind, Marshal.PtrToStringUTF8((IntPtr)buf) ?? "");
    }

    public int UsedMemory => Native.slua_used_memory(_h);
    public int MemoryLimit => Native.slua_get_memory_limit(_h);
    public void SetMemoryLimit(int limit) => Host.Check("slua_set_memory_limit", Native.slua_set_memory_limit(_h, limit));

    public void Dispose()
    {
        if (_h != IntPtr.Zero)
        {
            Native.slua_script_free(_h);
            _h = IntPtr.Zero;
        }
    }
}

/// <summary>Live native objects and bytes, as the library counts them. HeapInUseBytes is 0 where the system gives no figure.</summary>
public readonly record struct AllocStats(ulong LiveVms, ulong LiveImages, ulong LiveScripts, ulong OutstandingBlocks, ulong OutstandingBytes, ulong HeapInUseBytes);
