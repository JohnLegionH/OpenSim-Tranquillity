/* Copyright (c) Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A contact whose bodies listen for collisions gets an impulse estimate (JoltPhysicsSharp's
/// EstimateCollisionResponse). joltc mallocs that estimate's per-point impulse array on every call, so the
/// backend must free it. Memory is a process-wide figure, so the runs happen in a child test process and the
/// parent compares them: a kicked pile of listening boxes against the same pile not listening. Unfreed, each
/// listening contact report kept its array: about 18 MB more over these steps.
/// </summary>
public class ContactEstimateMemoryTests
{
    private readonly ITestOutputHelper _out;
    public ContactEstimateMemoryTests(ITestOutputHelper output) { _out = output; }

    private const string ChildVar = "JOLT_CONTACT_ESTIMATE_MEMORY_CHILD";
    private const int Steps = 600;

    private static PhysicsBackendSettings Settings() => new()
    {
        Gravity = new Vector3(0f, 0f, -9.8f),
        MaxBodies = 65536,
        MaxBodyPairs = 65536,
        MaxContactConstraints = 10240,
        CollisionSteps = 6,
        PositionIterations = 2,
        VelocityIterations = 10,
    };

    private static long PrivateBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.PrivateMemorySize64;
    }

    // 100 boxes in a touching pile on a flat terrain, a share of them kicked up every step so contacts keep
    // beginning and persisting. Returns the growth of private bytes over the steps (after the first 50).
    private static long RunPile(bool listening, int steps)
    {
        var b = new JoltPhysicsBackend();
        b.Initialize(Settings());
        try
        {
            float[] field = new float[257 * 257];
            Array.Fill(field, 25f);
            ShapeId terrain = b.CreateHeightFieldShape(field, 257, 257, new Vector3(1f, 1f, 1f));
            b.SetTerrain(terrain, Vector3.Zero);
            ShapeId box = b.CreateBoxShape(new Vector3(0.4f, 0.4f, 0.4f));
            var bodies = new List<BodyId>();
            for (int i = 0; i < 100; i++)
            {
                int x = i % 6, y = (i / 6) % 6, z = i / 36;
                bodies.Add(b.CreateBody(new BodyDesc
                {
                    Shape = box,
                    Orientation = Quaternion.Identity,
                    Position = new Vector3(120f + x * 0.85f + (z % 2) * 0.3f, 120f + y * 0.85f + (z % 2) * 0.3f, 26f + z * 0.9f),
                    Layer = PhysicsLayer.Dynamic,
                    MotionType = BodyMotionType.Dynamic,
                    Density = 1000f,
                    Friction = 0.5f,
                    GravityFactor = 1f,
                    WantsContactEvents = listening,
                }));
            }
            var bs = new BodyState[256];
            var cs = new CharacterState[8];
            var cr = new ContactReport[4096];
            long start = 0;
            for (int k = 0; k < steps; k++)
            {
                if (k == 50)
                    start = PrivateBytes();
                for (int i = k % 11; i < bodies.Count; i += 11)
                    b.SetBodyLinearVelocity(bodies[i], new Vector3(0f, 0f, 3f));
                b.Step(1f / 11f, bs, cs, cr);
            }
            return PrivateBytes() - start;
        }
        finally { b.Dispose(); }
    }

    /// <summary>The child entry: gated on an environment variable so it never runs in an ordinary test pass.</summary>
    [Fact]
    public void Child_runs()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ChildVar)))
            return;   // not the child; nothing to do

        RunPile(false, 200);   // warm the process: first-use growth lands here
        long quiet = RunPile(false, Steps);
        long freedBefore = JoltPhysicsBackend.EstimatesFreed;
        long listening = RunPile(true, Steps);
        long estimates = JoltPhysicsBackend.EstimatesFreed - freedBefore;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"CONTACT-ESTIMATE-MEMORY quiet_kb={quiet / 1024} listening_kb={listening / 1024} estimates={estimates}"));
    }

#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    private (int exit, string output) RunChild()
    {
        string asm = Assembly.GetExecutingAssembly().Location;
        string project = Path.GetDirectoryName(asm)!.Split(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)[0];
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = project,
        };
        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add(project);
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(Configuration);
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("--filter");
        psi.ArgumentList.Add("FullyQualifiedName~ContactEstimateMemoryTests.Child_runs");
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add("console;verbosity=detailed");
        psi.Environment[ChildVar] = "1";

        using var p = Process.Start(psi)!;
        Task<string> se = p.StandardError.ReadToEndAsync();
        string so = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10 * 60 * 1000);
        return (p.ExitCode, so + se.Result);
    }

    private static long Value(string line, string key)
        => long.Parse(line.Split(' ').Single(f => f.StartsWith(key + "=", StringComparison.Ordinal)).Substring(key.Length + 1), CultureInfo.InvariantCulture);

    [Fact]
    public void Listening_contacts_cost_no_more_memory_than_quiet_ones()
    {
        var (exit, output) = RunChild();
        string line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("CONTACT-ESTIMATE-MEMORY ", StringComparison.Ordinal));
        Assert.True(exit == 0 && line != null, $"child exit {exit}:\n{output}");
        _out.WriteLine(line);

        // The listening run made estimates (thousands of contact reports), so the path was taken.
        Assert.True(Value(line, "estimates") > 10000, line);

        // Unfreed, the listening run grew about 18 MB more than the quiet one; freed, the two grow alike. The
        // allowance is for heap noise between two runs.
        long extraKb = Value(line, "listening_kb") - Value(line, "quiet_kb");
        Assert.True(extraKb < 6 * 1024, $"the listening run grew {extraKb} KB more than the quiet one: {line}");
    }
}
