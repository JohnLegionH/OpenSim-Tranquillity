/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Numerics;

namespace OpenSim.MeshAsset;

/// <summary>
/// Finds the shape slider values that put the avatar skeleton's joints where a rigged model's own joints are. The
/// sliders act on the skeleton by <see cref="ShapeRules"/> (bone scales and offsets, and the drivers between
/// parameters); the fit minimises the weighted squared distance between each target joint's world position and the
/// avatar joint's, within every slider's range, and then rounds the values to the precision a shape wearable stores.
/// </summary>
public static class ShapeFit
{
    /// <summary>A joint the fit should reach: the model's own world position for it, and how much its distance counts.</summary>
    public sealed record Target(string Joint, Vector3 Position, double Weight = 1);

    /// <summary>
    /// The sliders a fit may move: height, thickness, shoulders, hip width and length, torso length, hand size, neck thickness
    /// and length, leg length, arm length, and the head and eye placement sliders (head size, eye spacing, eye depth, face
    /// shear, head length, eye size), by parameter id in avatar_lad.xml.
    /// </summary>
    public static readonly int[] BodySliders =
    {
        33, 34, 36, 37, 842, 38, 675, 683, 692, 693, 756,
        682, 196, 769, 662, 773, 690,
    };

    /// <summary>The joints whose placement matters most: the arm chain, which the limbs turn about, and the eyes, which the viewer's eyeballs sit on.</summary>
    public static readonly string[] HeavyJoints =
    {
        "mCollarLeft", "mCollarRight", "mShoulderLeft", "mShoulderRight", "mElbowLeft", "mElbowRight", "mWristLeft", "mWristRight", "mEyeLeft", "mEyeRight",
    };

    /// <summary>One target per model joint that is also an avatar joint, except collision volumes (never a pivot): the heavy joints count ten times.</summary>
    public static List<Target> TargetsFor(IReadOnlyDictionary<string, Vector3> positions, RestSkeleton bare)
    {
        var list = new List<Target>();
        foreach (var (name, pos) in positions.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (bare.Find(name) is { IsCollisionVolume: false })
                list.Add(new Target(name, pos, HeavyJoints.Contains(name) ? 10 : 1));
        return list;
    }

    /// <summary>
    /// The weights a shape wearable should hold for a fit: the fitted sliders and, beside each driver among them, the weight it
    /// gives the parameter it drives, so the wearable agrees with itself whichever order a viewer applies its parameters in.
    /// </summary>
    public static Dictionary<int, float> WearableValues(ShapeRules rules, IReadOnlyDictionary<int, float> fitted, int decimals = 2)
    {
        var all = rules.Resolve(fitted);
        var result = new Dictionary<int, float>();
        foreach (var (id, v) in fitted) result[id] = v;
        foreach (var id in fitted.Keys)
            if (rules.Drivers.TryGetValue(id, out var drives))
                foreach (var d in drives)
                    result[d.DrivenId] = MathF.Round(all[d.DrivenId], decimals) is var r && r == 0 ? 0f : r;
        return result;
    }

    public sealed record Result(Dictionary<int, float> Values, double CostUnrounded, double Cost, int Iterations);

    /// <summary>The weighted sum of squared distances, in square metres.</summary>
    public static double Cost(RestSkeleton bare, ShapeRules rules, IReadOnlyList<Target> targets, IReadOnlyDictionary<int, float> values)
    {
        double sum = 0;
        var shaped = bare.WithSliders(rules, values);
        foreach (var t in targets) sum += t.Weight * (shaped.World(t.Joint).Translation - t.Position).LengthSquared();
        return sum;
    }

    /// <summary>The distance in metres from each target to the avatar joint, for a set of slider values.</summary>
    public static Dictionary<string, float> Distances(RestSkeleton bare, ShapeRules rules, IReadOnlyList<Target> targets, IReadOnlyDictionary<int, float> values)
    {
        var shaped = bare.WithSliders(rules, values);
        return targets.ToDictionary(t => t.Joint, t => (shaped.World(t.Joint).Translation - t.Position).Length());
    }

    /// <param name="free">The ids of the parameters the fit may change; every other parameter keeps the value in <paramref name="fixedValues"/> or its default.</param>
    /// <param name="decimals">Digits after the decimal point of a stored value (a shape wearable keeps two).</param>
    /// <param name="keepDefaultBelow">
    /// A parameter that the fit moved from its starting value stays at that value when moving it lowers the cost by less than
    /// this (square metres): a slider that hardly matters is not left at an odd number.
    /// </param>
    public static Result Fit(RestSkeleton bare, ShapeRules rules, IReadOnlyList<Target> targets, IReadOnlyList<int> free,
        IReadOnlyDictionary<int, float>? fixedValues = null, int decimals = 2, double keepDefaultBelow = 0)
    {
        int n = free.Count;
        var x = free.Select(id => fixedValues != null && fixedValues.TryGetValue(id, out var f) ? f : rules.Params[id].Default).ToArray();
        var lo = free.Select(id => rules.Params[id].Min).ToArray();
        var hi = free.Select(id => rules.Params[id].Max).ToArray();

        Dictionary<int, float> Values(double[] v)
        {
            var d = fixedValues == null ? new Dictionary<int, float>() : new Dictionary<int, float>(fixedValues);
            for (int i = 0; i < n; i++) d[free[i]] = v[i] == 0 ? 0f : (float)v[i];   // no negative zero in a stored value
            return d;
        }
        double[] Residual(double[] v)
        {
            var shaped = bare.WithSliders(rules, Values(v));
            var r = new double[targets.Count * 3];
            for (int k = 0; k < targets.Count; k++)
            {
                var d = shaped.World(targets[k].Joint).Translation - targets[k].Position;
                double s = Math.Sqrt(targets[k].Weight);
                r[3 * k] = s * d.X; r[3 * k + 1] = s * d.Y; r[3 * k + 2] = s * d.Z;
            }
            return r;
        }
        double Sq(double[] r) { double s = 0; foreach (var e in r) s += e * e; return s; }

        var cur = x.Select(v => (double)v).ToArray();
        var res = Residual(cur);
        double cost = Sq(res), lambda = 1e-3;
        int iter = 0;
        for (; iter < 200; iter++)
        {
            // forward-difference Jacobian; a parameter at its bound with the step pointing out is stepped the other way
            var J = new double[n][];
            for (int i = 0; i < n; i++)
            {
                double h = 1e-3 * (hi[i] - lo[i]);
                var p = (double[])cur.Clone();
                double sign = cur[i] + h <= hi[i] ? 1 : -1;
                p[i] = cur[i] + sign * h;
                var r2 = Residual(p);
                J[i] = new double[res.Length];
                for (int k = 0; k < res.Length; k++) J[i][k] = (r2[k] - res[k]) / (sign * h);
            }
            var A = new double[n, n];
            var g = new double[n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++) { double s = 0; for (int k = 0; k < res.Length; k++) s += J[i][k] * J[j][k]; A[i, j] = s; }
                double gs = 0; for (int k = 0; k < res.Length; k++) gs += J[i][k] * res[k];
                g[i] = gs;
            }
            bool improved = false;
            for (int attempt = 0; attempt < 12 && !improved; attempt++)
            {
                var M = (double[,])A.Clone();
                for (int i = 0; i < n; i++) M[i, i] += lambda * (A[i, i] + 1e-9) + 1e-12;
                var step = Solve(M, g.Select(v => -v).ToArray());
                var next = new double[n];
                for (int i = 0; i < n; i++) next[i] = Math.Clamp(cur[i] + step[i], lo[i], hi[i]);
                var rn = Residual(next);
                double cn = Sq(rn);
                if (cn < cost)
                {
                    bool small = cost - cn < 1e-14 * Math.Max(1, cost);
                    cur = next; res = rn; cost = cn; lambda = Math.Max(lambda / 3, 1e-9); improved = true;
                    if (small) { iter = 1000; }
                }
                else lambda *= 4;
            }
            if (!improved) break;
        }
        double unrounded = cost;

        // round to the stored precision, then settle by single steps of the last digit
        double unit = Math.Pow(10, -decimals);
        for (int i = 0; i < n; i++) cur[i] = Math.Clamp(Math.Round(cur[i], decimals), lo[i], hi[i]);
        cost = Sq(Residual(cur));
        for (int round = 0; round < 100; round++)
        {
            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                double bestV = cur[i], bestC = cost;
                foreach (int m in new[] { -3, -2, -1, 1, 2, 3 })
                {
                    double v = Math.Round(cur[i] + m * unit, decimals);
                    if (v < lo[i] || v > hi[i]) continue;
                    var p = (double[])cur.Clone(); p[i] = v;
                    double c = Sq(Residual(p));
                    if (c < bestC - 1e-15) { bestC = c; bestV = v; }
                }
                if (bestV != cur[i]) { cur[i] = bestV; cost = bestC; changed = true; }
            }
            if (!changed) break;
        }
        if (keepDefaultBelow > 0)
            for (bool again = true; again;)
            {
                again = false;
                for (int i = 0; i < n; i++)
                {
                    if (cur[i] == x[i]) continue;
                    var p = (double[])cur.Clone(); p[i] = x[i];
                    double c = Sq(Residual(p));
                    if (c - cost < keepDefaultBelow) { cur = p; cost = c; again = true; }
                }
            }
        return new Result(Values(cur), unrounded, cost, iter);
    }

    static double[] Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[piv, c])) piv = r;
            if (Math.Abs(m[piv, c]) < 1e-300) continue;
            if (piv != c)
            {
                for (int k = 0; k < n; k++) (m[c, k], m[piv, k]) = (m[piv, k], m[c, k]);
                (x[c], x[piv]) = (x[piv], x[c]);
            }
            for (int r = c + 1; r < n; r++)
            {
                double f = m[r, c] / m[c, c];
                for (int k = c; k < n; k++) m[r, k] -= f * m[c, k];
                x[r] -= f * x[c];
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            double s = x[r];
            for (int k = r + 1; k < n; k++) s -= m[r, k] * x[k];
            x[r] = Math.Abs(m[r, r]) < 1e-300 ? 0 : s / m[r, r];
        }
        return x;
    }
}
