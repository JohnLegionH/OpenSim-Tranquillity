/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

/// <summary>
/// --temp-starve: physics steps while this process cannot commit more memory.
///
/// joltc's temp allocator is a fixed stack with a malloc fallback (TempAllocatorImplWithMallocFallback, made by
/// JPH_Init). A step whose scratch memory does not fit the stack takes a fresh block from malloc, and Jolt uses the
/// block unchecked: ContactConstraintManager::PrepareConstraintBuffer keeps a null mConstraints, and the first contact
/// of the step writes through it (ContactConstraintManager.cpp, TemplatedAddContactConstraint). When the machine is
/// out of commit, that is an access violation in the job worker.
///
/// This mode builds two scenes on the shared job pool, each with a settled pile of boxes (so every step has contacts),
/// then caps THIS process's committed memory with a Windows job object at what it has committed now plus a small
/// headroom, and steps both scenes. Only this process is starved; nothing else on the machine is. It prints
/// "SURVIVED steps=N" and returns 0 when every step ran.
/// </summary>
public static class TempStarve
{
    public static int Run(string[] args, TextWriter output)
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("SKIPPED: job objects are Windows only");
            return 3;
        }

        double headroomMb = 4;
        int steps = 100;
        var joltKeys = new List<KeyValuePair<string, string>>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--headroom-mb": headroomMb = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--steps": steps = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--jolt":
                    string kv = args[++i];
                    int eq = kv.IndexOf('=');
                    joltKeys.Add(new KeyValuePair<string, string>(kv[..eq], kv[(eq + 1)..]));
                    break;
                default: throw new ArgumentException("unknown --temp-starve option " + args[i]);
            }
        }

        const float dt = 1f / 45f;
        var scenes = new[] { NewScene("starve-a", joltKeys, dt), NewScene("starve-b", joltKeys, dt) };
        var pushed = new PhysicsActor[scenes.Length];
        for (int i = 0; i < scenes.Length; i++)
            pushed[i] = AddPile(scenes[i], 60);

        // Settle the piles so every later step has contacts, and let the runtime finish its own first-use work.
        for (int i = 0; i < 90; i++)
            foreach (JoltScene s in scenes)
                s.Simulate(dt);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long committed = Process.GetCurrentProcess().PrivateMemorySize64;
        long limit = committed + (long)(headroomMb * 1024 * 1024);
        if (!CapCommit(limit, out int error))
        {
            output.WriteLine("SETUP FAILED: job object error " + error);
            return 2;
        }
        output.WriteLine($"CAPPED committed={committed} limit={limit}");
        output.Flush();

        int ran = 0;
        for (int i = 0; i < steps; i++)
        {
            // One box of each pile is pushed every step, so the piles stay awake and every step adds contacts.
            for (int k = 0; k < scenes.Length; k++)
            {
                pushed[k].AddForce(new Vector3((i & 1) == 0 ? 400f : -400f, 0f, 0f), true);
                scenes[k].Simulate(dt);
            }
            ran++;
        }
        output.WriteLine("SURVIVED steps=" + ran.ToString(CultureInfo.InvariantCulture));
        output.Flush();
        return 0;
    }

    private static JoltScene NewScene(string name, List<KeyValuePair<string, string>> joltKeys, float dt)
    {
        var config = new IniConfigSource();
        IConfig startup = config.AddConfig("Startup");
        startup.Set("physics", "Jolt");
        startup.Set("meshing", "Meshmerizer");
        IConfig jolt = config.AddConfig("Jolt");
        jolt.Set("PhysicsStepRate", "0");
        foreach (KeyValuePair<string, string> kv in joltKeys)
            jolt.Set(kv.Key, kv.Value);
        var scene = new JoltScene();
        scene.Initialise(config);
        scene.InitialiseWithoutScene(name, Course.Size, Course.Size, Course.Heightmap(0f), Course.Water, dt);
        return scene;
    }

    // Returns the box on top of the pile.
    private static PhysicsActor AddPile(JoltScene scene, int boxes)
    {
        PhysicsActor top = null!;
        for (int i = 0; i < boxes; i++)
        {
            int x = i % 5, y = (i / 5) % 4, z = i / 20;
            var p = new Vector3(120f + x * 0.85f, 120f + y * 0.85f, Course.Ground + 0.5f + z * 0.82f);
            PhysicsActor pa = scene.AddPrimShape("starve box", PrimitiveBaseShape.CreateBox(), p, new Vector3(0.8f, 0.8f, 0.8f),
                                                 Quaternion.Identity, true, (uint)(5000 + i));
            pa.Density = 1000f;
            top = pa;
        }
        return top;
    }

    // A job object with JOB_OBJECT_LIMIT_PROCESS_MEMORY caps the committed memory of the processes in it.
    private static bool CapCommit(long limitBytes, out int error)
    {
        error = 0;
        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) { error = Marshal.GetLastWin32Error(); return false; }
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_PROCESS_MEMORY;
        info.ProcessMemoryLimit = (UIntPtr)(ulong)limitBytes;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        { error = Marshal.GetLastWin32Error(); return false; }
        if (!AssignProcessToJobObject(job, GetCurrentProcess()))
        { error = Marshal.GetLastWin32Error(); return false; }
        return true;
    }

    private const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint cbInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
