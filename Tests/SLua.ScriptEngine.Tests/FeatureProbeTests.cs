/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The single-feature probes: 79 small SLua scripts, each using one feature of Second Life's SLua (syntax, the standard
/// library, metamethods, the vector and quaternion types, LLEvents, LLTimers, table keys) and asserting what Second Life's
/// documentation says it does. A probe passes when its script runs to the end with no fault and no reported error, so a
/// failed assert is a failed probe. The probes are in TestData/feature-probes.txt, one section per probe under a line
/// "=== name".
/// </summary>
[Collection("native-library")]
public class FeatureProbeTests
{
    private static readonly string ProbeFile = Path.Combine(AppContext.BaseDirectory, "TestData", "feature-probes.txt");

    /// <summary>Text spliced in front of every probe by the test that proves the check can fail; empty in a normal run.</summary>
    private static readonly string Prelude = Environment.GetEnvironmentVariable("SLUA_PROBE_PRELUDE") ?? "";

    public static IEnumerable<object[]> Probes() => Read().Select(p => new object[] { p.Key });

    private static Dictionary<string, string> Read()
    {
        var probes = new Dictionary<string, string>();
        string name = null;
        var body = new List<string>();
        void Flush()
        {
            if (name != null) probes[name] = string.Join("\n", body);
        }
        foreach (string line in File.ReadAllLines(ProbeFile))
        {
            if (line.StartsWith("=== "))
            {
                Flush();
                name = line.Substring(4).Trim();
                body.Clear();
            }
            else body.Add(line);
        }
        Flush();
        return probes;
    }

    [Fact]
    public void TheProbeFileHoldsSeventyNineProbes()
    {
        Dictionary<string, string> probes = Read();
        Assert.Equal(79, probes.Count);
        Assert.All(probes, p => Assert.False(string.IsNullOrWhiteSpace(p.Value), p.Key + " is empty"));
    }

    private static void RunToTheEnd(string label, string source)
    {
        using var rig = new BindingRig();
        SchedulerScript s = rig.H.AddAndStart(Prelude + source, lsl: false);
        // Timers and event registration are given a few seconds of script time to fire.
        for (int i = 0; i < 6; i++)
        {
            rig.H.Pump();
            rig.H.Advance(1.1);
        }
        Assert.True(rig.H.FaultedScripts.IsEmpty, label + " faulted: " + string.Join(" / ", rig.H.FaultedScripts.Select(f => f.FaultMessage)));
        Assert.True(rig.H.ReportedErrors.IsEmpty, label + " reported: " + string.Join(" / ", rig.H.ReportedErrors));
        Assert.NotEqual(ScriptRunState.Loading, s.State);
    }

    [RequiresNativeTheory]
    [MemberData(nameof(Probes))]
    public void AProbeRunsToTheEndWithNoFaultAndNoError(string probe) => RunToTheEnd(probe, Read()[probe]);

    /// <summary>
    /// What tostring prints for a number that is not exact. Linden Lab's runtime prints the shortest text that reads back as
    /// the same double (VM/src/lnumprint.cpp, the Schubfach method; its own test tests/conformance/strconv.luau expects
    /// tostring(math.pi) to be "3.141592653589793"), so one third has sixteen digits. Lua 5.1's "%.14g" would give fourteen
    /// ("0.33333333333333"), and the number is a double, not a 32-bit float.
    /// </summary>
    [RequiresNativeFact]
    public void TostringOfAnInexactNumberIsTheShortestTextThatReadsBack()
    {
        RunToTheEnd("tostring",
            "assert(tostring(1 / 3) == \"0.3333333333333333\")\n" +
            "assert(tostring(100 / 3) == \"33.333333333333336\")\n" +
            "assert(tostring(math.pi) == \"3.141592653589793\")\n" +
            "assert(tostring(0.1) == \"0.1\")\n" +
            "assert(tostring(100000000000000) == \"100000000000000\")");
    }
}
