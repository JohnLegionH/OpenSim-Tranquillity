/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Diagnostics;
using System.Numerics;
using MeshOptimizer;

namespace OpenSim.MeshAsset;

/// <summary>
/// Makes the lower levels of detail from the high one, the way the Linden Lab viewer does when a model is uploaded with
/// "generate" chosen for a level: the faces of the high level are simplified together as one mesh by meshoptimizer, with
/// the vertex positions as the only input to the error metric, to a triangle count of 1/3, 1/9 and 1/27 of the high level,
/// and then split back into the original faces. Written from what the viewer does (documented in FORMAT.md); no viewer
/// code is used. meshoptimizer (MIT, Copyright (c) Arseny Kapoulkine) is called through Alimer.Bindings.MeshOptimizer
/// (MIT, Copyright (c) Amer Koleci and Contributors); see ThirdPartyLicenses.md.
/// </summary>
public static class LodGenerator
{
    /// <summary>
    /// How the target triangle count of each level is chosen. <see cref="Viewer"/> is the viewer's own 1/3, 1/9, 1/27 of the high
    /// level. <see cref="Character"/> uses the fractions creators of rigged meshes author by hand: the medians, over 28 rigged
    /// mesh assets, of each level's triangle count divided by the high level's (0.25 for medium, 0.003585 for lowest), except that low is never smaller than the viewer's 1/27.
    /// </summary>
    public enum Profile { Viewer, Character }

    /// <summary>Character profile fractions of the high level's triangles, indexed by level (0 lowest .. 2 medium).</summary>
    public static readonly float[] CharacterFractions = { 0.0035848f, MathF.Max(0.0035848f, 1f / 27f), 0.25f };

    /// <summary>The profile used when none is chosen: the character profile for a rigged mesh, the viewer's for the rest.</summary>
    public static Profile DefaultProfile(MeshAssetModel model) => model.Skin != null ? Profile.Character : Profile.Viewer;

    /// <summary>The fraction of the high level's triangles a level aims for.</summary>
    public static float Fraction(Profile profile, int lod, int decimation = Decimation) =>
        profile == Profile.Character ? CharacterFractions[lod] : 1f / MathF.Pow(decimation, MeshAssetModel.LodCount - 1 - lod);

    /// <summary>How much smaller each level is than the one above it (the viewer's default).</summary>
    public const int Decimation = 3;

    /// <summary>What was done for one level, for reports.</summary>
    public sealed record LodResult(int Lod, int TargetTriangles, int Triangles, int Vertices, string Method, float Ratio);

    enum Mode { Full, NoNormals, NoUvs, NoTopology }

    /// <summary>Receives a line for every simplification pass, with its mode, target and time; null (the default) says nothing.</summary>
    [ThreadStatic] public static Action<string> Trace;

    /// <summary>Fills the medium, low and lowest levels from the high one. Levels already present are replaced.</summary>
    public static List<LodResult> GenerateAll(MeshAssetModel model, int decimation = Decimation, Profile profile = Profile.Viewer)
    {
        if (model.High == null) throw new InvalidOperationException("there is no high level of detail to simplify");
        var results = new List<LodResult>();
        for (int lod = MeshAssetModel.LodCount - 2; lod >= 0; lod--)
        {
            var (made, result) = Generate(model.High, lod, decimation, profile);
            model.SetLod(lod, made);
            results.Add(result);
        }
        model.LodsGenerated = true;
        return results;
    }

    /// <summary>One level (0 lowest .. 2 medium) from the high level.</summary>
    public static (MeshLod Lod, LodResult Result) Generate(MeshLod high, int lod, int decimation = Decimation, Profile profile = Profile.Viewer)
    {
        float baseTriangles = high.Faces.Sum(f => f.NoGeometry ? 0 : f.TriangleCount);
        float limit = MathF.Max(baseTriangles * Fraction(profile, lod, decimation), 1f);
        float indicesDecimator = baseTriangles / limit;
        var target = Auto(high, indicesDecimator, decimation, out string method, out float ratio);
        int tris = target.Faces.Sum(f => f.NoGeometry ? 0 : f.TriangleCount);
        int verts = target.Faces.Sum(f => f.NoGeometry ? 0 : f.VertexCount);
        return (target, new LodResult(lod, (int)limit, tris, verts, method, ratio));
    }

    // The viewer's automatic mode: precise first, then with normals and UVs ignored when it falls short of the target,
    // then the sloppy simplifier, keeping whichever reached further; if nothing works the faces are copied unchanged.
    static MeshLod Auto(MeshLod high, float dec, int decimation, out string method, out float ratio)
    {
        const float allowedRatioDrift = 1.8f, sloppyRatioDrift = 1.4f;
        var target = new MeshLod();
        method = "precise";

        float precise = Run(high, target, dec, Mode.Full);
        if (precise < 0 || precise * allowedRatioDrift < dec) { precise = Run(high, target, dec, Mode.NoNormals); method = "precise, normals ignored"; }
        if (precise < 0 || precise * allowedRatioDrift < dec) { precise = Run(high, target, dec, Mode.NoUvs); method = "precise, normals and UVs ignored"; }
        if (precise >= 0 && precise * allowedRatioDrift >= dec) { ratio = precise; return target; }

        float lastDec = 0, lastRatio = float.MaxValue;
        float sloppy = Run(high, target, dec, Mode.NoTopology);
        if (sloppy > 0) { lastDec = dec; lastRatio = sloppy; }

        // Sloppy tends to land low on the triangle count, so a partial correction is tried when it is far off.
        if (sloppy > dec * sloppyRatioDrift || sloppy < 0)
        {
            float corrected = dec * (dec / sloppy + 1) / 2;
            sloppy = Run(high, target, corrected, Mode.NoTopology);
        }
        if (lastDec > 0 && sloppy < lastRatio) sloppy = Run(high, target, dec, Mode.NoTopology);

        if (sloppy < 0)
        {
            // Find a decimator sloppy can do: step down by sqrt(decimation), for at most five seconds.
            float step = MathF.Sqrt(decimation), sd = dec / step;
            var clock = Stopwatch.StartNew();
            while (sloppy < 0 && sd > precise && sd > 1 && clock.Elapsed < TimeSpan.FromSeconds(5))
            {
                sloppy = Run(high, target, sd, Mode.NoTopology);
                sd /= step;
            }
        }

        if (sloppy < 0 || sloppy < precise)
        {
            if (precise < 0)
            {
                target = high.Copy();
                precise = 1f;
                method = "copied (no simplification possible)";
            }
            else
            {
                precise = Run(high, target, dec, Mode.Full);
                method = "precise";
            }
            ratio = precise;
        }
        else
        {
            method = "sloppy";
            ratio = sloppy;
        }
        return target;
    }

    /// <summary>
    /// One simplification of the whole mesh. Returns the ratio of input to output index counts, or -1 when nothing usable
    /// came out; <paramref name="target"/> is rewritten only when it did.
    /// </summary>
    static float Run(MeshLod high, MeshLod target, float indicesDecimator, Mode mode)
    {
        Stopwatch clock = Trace == null ? null : Stopwatch.StartNew();
        float ratio = RunPass(high, target, indicesDecimator, mode);
        Trace?.Invoke("simplify " + mode + " by " + indicesDecimator.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ": ratio " + ratio.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " in " + clock.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s");
        return ratio;
    }

    static float RunPass(MeshLod high, MeshLod target, float indicesDecimator, Mode mode)
    {
        // I. Weld every face into one vertex and index buffer.
        int totalV = 0, totalI = 0;
        foreach (var f in high.Faces) if (!f.NoGeometry) { totalV += f.VertexCount; totalI += f.Indices.Length; }
        if (totalI < 3) return -1;

        var pos = new float[totalV * 3];
        var nrm = new Vector3[totalV];
        var uv = new Vector2[totalV];
        var indices = new uint[totalI];
        var faceStart = new int[high.Faces.Count];
        var ownerOf = new int[totalV]; // face each welded vertex came from
        int v = 0, ii = 0;
        for (int fi = 0; fi < high.Faces.Count; fi++)
        {
            var f = high.Faces[fi];
            faceStart[fi] = v;
            if (f.NoGeometry) continue;
            for (int k = 0; k < f.VertexCount; k++)
            {
                pos[(v + k) * 3] = f.Positions[k].X; pos[(v + k) * 3 + 1] = f.Positions[k].Y; pos[(v + k) * 3 + 2] = f.Positions[k].Z;
                if (f.Normals != null) nrm[v + k] = f.Normals[k];
                if (f.Uvs != null) uv[v + k] = f.Uvs[k];
            }
            for (int k = 0; k < f.VertexCount; k++) ownerOf[v + k] = fi;
            foreach (var ix in f.Indices) indices[ii++] = (uint)(ix + v);
            v += f.VertexCount;
        }

        // II. Weld vertices that agree on the kept attributes, when the mode asks for it.
        var source = mode switch
        {
            Mode.NoNormals => Shadow(indices, pos, uv, useUv: true),
            Mode.NoUvs => Shadow(indices, pos, uv, useUv: false),
            _ => indices,
        };

        // III. Simplify. The target is a count of indices; at least one triangle is kept.
        int targetIndices = indicesDecimator > 0 ? Math.Clamp((int)MathF.Floor(totalI / indicesDecimator), 3, totalI) : 3;
        var output = new uint[totalI];
        float error = 0;
        int newIndices;
        unsafe
        {
            // The pointer overloads take the vertex count explicitly; the span ones work it out from the span lengths.
            fixed (uint* dst = output) fixed (uint* idx = source) fixed (float* p = pos)
            {
                newIndices = mode == Mode.NoTopology
                    ? (int)Meshopt.SimplifySloppy(dst, idx, (UIntPtr)totalI, p, (UIntPtr)totalV, (UIntPtr)12, null, (UIntPtr)targetIndices, 1f, &error)
                    : (int)Meshopt.Simplify(dst, idx, (UIntPtr)totalI, p, (UIntPtr)totalV, (UIntPtr)12, (UIntPtr)targetIndices, 1f, SimplificationOptions.None, &error);
            }
        }
        if (newIndices < 3) return -1;

        // IV. Split back into the original faces: a triangle belongs to the face its first index falls in; each face
        // gets a fresh vertex list in order of first use.
        var faces = new List<MeshFace>(high.Faces.Count);
        var map = new int[totalV];
        int validFaces = 0;
        for (int fi = 0; fi < high.Faces.Count; fi++)
        {
            var src = high.Faces[fi];
            if (src.NoGeometry) { faces.Add(new MeshFace { NoGeometry = true }); continue; }
            Array.Fill(map, -1);
            int lo = faceStart[fi], hi = lo + src.VertexCount;
            var outIdx = new List<int>();
            var srcOf = new List<int>(); // for each new vertex, the welded vertex it came from
            for (int t = 0; t + 2 < newIndices; t += 3)
            {
                int first = (int)output[t];
                if (first < lo || first >= hi) continue;
                for (int c = 0; c < 3; c++)
                {
                    int old = (int)output[t + c];
                    if (map[old] < 0) { map[old] = srcOf.Count; srcOf.Add(old); }
                    outIdx.Add(map[old]);
                }
            }
            if (outIdx.Count < 3) { faces.Add(new MeshFace { NoGeometry = true, NormalizedScale = src.NormalizedScale }); continue; }

            var dst = new MeshFace
            {
                Positions = new Vector3[srcOf.Count],
                Normals = src.Normals == null ? null : new Vector3[srcOf.Count],
                Uvs = src.Uvs == null ? null : new Vector2[srcOf.Count],
                Weights = src.Weights == null ? null : new Influence[srcOf.Count][],
                Indices = outIdx.ToArray(),
                NormalizedScale = src.NormalizedScale,
            };
            for (int k = 0; k < srcOf.Count; k++)
            {
                // The kept vertex may belong to another face when welding crossed a face boundary; find its owner.
                int w = srcOf[k];
                var owner = high.Faces[ownerOf[w]];
                int local = w - faceStart[ownerOf[w]];
                dst.Positions[k] = owner.Positions[local];
                if (dst.Normals != null) dst.Normals[k] = owner.Normals?[local] ?? default;
                if (dst.Uvs != null) dst.Uvs[k] = owner.Uvs?[local] ?? default;
                if (dst.Weights != null) dst.Weights[k] = owner.Weights?[local] ?? Array.Empty<Influence>();
            }
            faces.Add(dst);
            validFaces++;
        }
        if (validFaces == 0) return -1;

        target.Faces.Clear();
        target.Faces.AddRange(faces);
        return (float)totalI / newIndices;
    }

    // Maps each index to the first vertex with the same position (and UV, when asked), so the simplifier can collapse
    // across a normal seam, or across a UV seam as well.
    static uint[] Shadow(uint[] indices, float[] pos, Vector2[] uv, bool useUv)
    {
        int n = pos.Length / 3;
        var canon = new uint[n];
        var seen = new Dictionary<(int, int, int, int, int), uint>();
        for (int i = 0; i < n; i++)
        {
            var key = (BitConverter.SingleToInt32Bits(pos[i * 3]), BitConverter.SingleToInt32Bits(pos[i * 3 + 1]), BitConverter.SingleToInt32Bits(pos[i * 3 + 2]),
                       useUv ? BitConverter.SingleToInt32Bits(uv[i].X) : 0, useUv ? BitConverter.SingleToInt32Bits(uv[i].Y) : 0);
            if (!seen.TryGetValue(key, out var c)) { c = (uint)i; seen[key] = c; }
            canon[i] = c;
        }
        var result = new uint[indices.Length];
        for (int i = 0; i < indices.Length; i++) result[i] = canon[indices[i]];
        return result;
    }
}
