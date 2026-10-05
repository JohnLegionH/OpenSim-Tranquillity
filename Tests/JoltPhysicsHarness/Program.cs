/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;

namespace OpenSim.Region.PhysicsModules.Jolt.Harness;

public static class Program
{
    private const string Usage =
@"JoltPhysicsHarness: run Jolt physics scenarios with no region, at chosen heartbeat rates.

  --list                     list the scenarios and exit
  --scenario NAME[,NAME..]   scenarios to run, or 'all' (default: all)
  --rate HZ[,HZ..]           heartbeat rates, or 'all' = 11,22.5,45,90 (default: 11)
  --physics-rate HZ[,HZ..]   physics steps per second inside each heartbeat ([Jolt] PhysicsStepRate);
                             0 = one step per heartbeat (default: 0, or JOLT_HARNESS_PHYSICS_RATE)
  --slope DEG[,DEG..]        ramp angles for the scenarios that use one (default: each scenario's own list)
  --duration S               seconds to simulate (default: the scenario's)
  --hold S                   seconds the drive key is held (default: the scenario's)
  --keyrepeat S              how often a held key re-sends the motor (default: 0.1)
  --jolt KEY=VALUE           a [Jolt] config key, as in the region's ini (repeatable)
  --vparam NAME=V|X,Y,Z      a vehicle param applied after the scenario's own, by its LSL name,
                             e.g. LINEAR_FRICTION_TIMESCALE=1,1,1000 (repeatable)
  --out DIR                  write <scenario>-s<slope>-r<rate>.csv per run and summary.csv to DIR

The summary table always goes to standard output. Nothing is written anywhere else.";

    public static int Main(string[] args)
    {
        try
        {
            return Run(args, Console.Out);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }
    }

    public static int Run(string[] args, TextWriter output)
    {
        var o = new HarnessOptions();
        List<Scenario> scenarios = Harness.Scenarios.ToList();
        double[] rates = { 11.0 };
        double[] physicsRates = { o.PhysicsRateHz };
        float[] slopes = null;
        string outDir = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            switch (a)
            {
                case "--help": case "-h": case "/?":
                    output.WriteLine(Usage);
                    return 0;
                case "--list":
                    foreach (Scenario s in Harness.Scenarios)
                        output.WriteLine($"{s.Name,-14} {(s.UsesSlope ? "slopes " + string.Join("/", s.Slopes.Select(f => f.ToString(CultureInfo.InvariantCulture))) : "level"),-18} {s.Description}");
                    return 0;
                case "--scenario":
                    string names = Next();
                    scenarios = names == "all" ? Harness.Scenarios.ToList() : names.Split(',').Select(Harness.Find).ToList();
                    break;
                case "--rate":
                    string r = Next();
                    rates = r == "all" ? Harness.Rates : r.Split(',').Select(x => PositiveDouble(x, a)).ToArray();
                    break;
                case "--physics-rate":
                    physicsRates = Next().Split(',').Select(x => NonNegativeDouble(x, a)).ToArray();
                    break;
                case "--slope":
                    slopes = Next().Split(',').Select(x => (float)NonNegativeDouble(x, a)).ToArray();
                    if (slopes.Any(s => s >= 60f)) throw new ArgumentException("--slope must be under 60 degrees");
                    break;
                case "--duration": o.Duration = (float)PositiveDouble(Next(), a); break;
                case "--hold": o.Hold = (float)NonNegativeDouble(Next(), a); break;
                case "--keyrepeat": o.KeyRepeat = (float)PositiveDouble(Next(), a); break;
                case "--jolt":
                    string kv = Next();
                    int eq = kv.IndexOf('=');
                    if (eq <= 0) throw new ArgumentException($"--jolt '{kv}': expected KEY=VALUE");
                    o.Jolt[kv[..eq].Trim()] = kv[(eq + 1)..].Trim();
                    break;
                case "--vparam": o.VehicleParams.Add(VehicleParamSetting.Parse(Next())); break;
                case "--out": outDir = Next(); break;
                default: throw new ArgumentException($"unknown argument '{a}'");
            }
        }

        if (outDir != null)
            Directory.CreateDirectory(outDir);

        var summary = new StringBuilder();
        summary.Append(RunResult.SummaryHeader).Append('\n');
        output.WriteLine(RunResult.SummaryHeader);
        foreach (Scenario sc in scenarios)
        {
            float[] runSlopes = sc.UsesSlope ? (slopes ?? sc.Slopes) : new[] { 0f };
            foreach (float slope in runSlopes)
                foreach (double rate in rates)
                foreach (double physicsRate in physicsRates)
                {
                    var opts = Copy(o, rate, slope);
                    opts.PhysicsRateHz = physicsRate;
                    RunResult res = Harness.Run(sc, opts);
                    string line = res.SummaryLine();
                    output.WriteLine(line);
                    summary.Append(line).Append('\n');
                    if (outDir != null)
                        File.WriteAllText(Path.Combine(outDir, res.Name + ".csv"), res.ToCsv());
                }
        }
        if (outDir != null)
            File.WriteAllText(Path.Combine(outDir, "summary.csv"), summary.ToString());
        return 0;
    }

    private static HarnessOptions Copy(HarnessOptions o, double rate, float slope)
    {
        var c = new HarnessOptions { RateHz = rate, SlopeDeg = slope, Duration = o.Duration, Hold = o.Hold, KeyRepeat = o.KeyRepeat };
        foreach (KeyValuePair<string, string> kv in o.Jolt) c.Jolt[kv.Key] = kv.Value;
        c.VehicleParams.AddRange(o.VehicleParams);
        return c;
    }

    private static double PositiveDouble(string s, string arg)
    {
        double v = NonNegativeDouble(s, arg);
        if (v <= 0) throw new ArgumentException($"{arg} must be above 0");
        return v;
    }

    private static double NonNegativeDouble(string s, string arg)
    {
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v) || v < 0)
            throw new ArgumentException($"{arg}: '{s}' is not a number of 0 or more");
        return v;
    }
}
