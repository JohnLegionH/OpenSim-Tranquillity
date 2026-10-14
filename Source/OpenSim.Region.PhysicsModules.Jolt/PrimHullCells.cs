/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Splits a physical prim that is not convex (hollow, cut, twisted, a torus, tube, ring or hollow sphere) into convex
// cells, so it can collide as a compound of convex hulls instead of one hull that fills in its cavities.
//
// A prim is its profile (the outline of its cross-section) copied along its path (see PrimProfileRings in the mesher).
// A cell is one convex piece of the profile swept over one span of the path; its hull is the hull of that piece's
// points on the rings of the span. The profile is cut into pieces by angle around its centre: a hollow profile into
// sectors that each hold a stretch of the outer ring and of the hollow ring, a cut solid profile into wedges of at most
// half a turn, an uncut solid profile stays whole. The path is cut into spans that each turn and twist by at most
// a set angle (never less than one step of the mesher's path).
//
// The fine split is 30 degrees of profile per sector and 30 degrees of path per span. A hollow torus at that size is 144
// cells and a hollow sphere 72, so when the fine split is over MaxCells the profile is coarsened first (sectors of 45, 60,
// 90, 120, 180 degrees and more) with the finest path span that still fits, every such pair is cooked, and the one whose
// compound volume is closest to the volume of the mesher's own mesh is kept. A cell the hull builder refuses rejects its
// pair; when no pair cooks cleanly the build fails and the caller keeps today's single hull.
//
// Only the mesher's rings are used, so the cells follow what the mesher draws. The design (convex pieces of a profile
// swept along a path) is the one Halcyon's physics shape determiner chooses between by prim type; the pieces here come
// from the prim's own parameters instead of a decomposition of its triangles.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.Meshing;
using SVector3 = System.Numerics.Vector3;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    /// <summary>The cells chosen for one prim and how well they fit it.</summary>
    internal sealed class PrimHullCellSet
    {
        /// <summary>False when no split could be cooked; <see cref="Failure"/> says why and the caller keeps the single hull.</summary>
        internal bool Success;

        internal string Failure = "";

        /// <summary>The points of each cell, in the prim's own frame at the size the rings were asked for. At most <see cref="PrimHullCells.MaxCells"/>.</summary>
        internal SVector3[][] Cells = Array.Empty<SVector3[]>();

        /// <summary>Convex pieces of the profile times spans of the path: <see cref="Cells"/> is their product.</summary>
        internal int Pieces, Spans;

        /// <summary>The profile sector and path span in degrees the cells were cut with.</summary>
        internal double SectorDegrees, SpanDegrees;

        /// <summary>Whether the fine split (30 / 30 degrees) was over the cell limit and the profile had to be coarsened.</summary>
        internal bool Coarsened;

        /// <summary>How many pairs of sector and span were cooked to choose this one.</summary>
        internal int PairsCooked;

        /// <summary>The volume of the cells as a compound, the volume of the mesher's mesh, and their ratio (1 is exact; hulls overshoot).</summary>
        internal double CellsVolume, MeshVolume, VolumeRatio;

        internal int CellCount => Cells.Length;
    }

    internal static class PrimHullCells
    {
        /// <summary>The most cells (hulls) one prim is split into: a constant, not a setting.</summary>
        internal const int MaxCells = 64;

        /// <summary>The fine split: degrees of profile per sector and of path per span.</summary>
        internal const double FineSectorDegrees = 30, FineSpanDegrees = 30;

        // Coarser profile sectors and path spans, in degrees, tried when the fine split is over MaxCells. A span of 1440
        // degrees is the whole path (the mesher's longest is two turns plus), a sector of 360 the whole profile.
        private static readonly double[] SectorLadder = { 30, 45, 60, 90, 120, 180, 360 };
        private static readonly double[] SpanLadder = { 15, 30, 45, 60, 90, 120, 180, 360, 720, 1440 };

        private const double AngleTolerance = 1e-3;     // radians: two angles closer than this are the same ray (0.06 degrees)
        private const double Degrees = 180.0 / Math.PI;

        /// <summary>A point on a ring of a layer: vertex A, or the fraction T of the way from vertex A to vertex B.</summary>
        private readonly struct RingPoint
        {
            internal readonly int A, B;
            internal readonly float T;
            internal RingPoint(int a, int b, float t) { A = a; B = b; T = t; }
        }

        private sealed class ProfileSplit
        {
            internal double SectorDegrees;
            internal List<RingPoint[]> Pieces = new List<RingPoint[]>();
            internal string Key = "";
        }

        private sealed class PathSplit
        {
            internal double SpanDegrees;
            internal List<(int First, int Last)> Spans = new List<(int, int)>();
            internal string Key = "";
        }

        /// <summary>One sector and span to try.</summary>
        private sealed class Candidate
        {
            internal ProfileSplit Profile;
            internal PathSplit Path;
            internal int Count => Profile.Pieces.Count * Path.Spans.Count;
        }

        // ------------------------------------------------------------------------------------------ building

        /// <summary>
        /// Splits the prim described by <paramref name="rings"/> into convex cells and cooks the candidates through
        /// <paramref name="backend"/> to choose the best fit. Nothing it cooks is kept: the caller cooks the chosen cells
        /// itself with <see cref="CookCompound"/>.
        /// </summary>
        /// <param name="rings">The mesher's rings for the prim at the size it is cooked at.</param>
        /// <param name="meshVolume">The volume of the mesher's mesh at that size (see <see cref="MeshVolume"/>).</param>
        internal static PrimHullCellSet Build(IPhysicsBackend backend, PrimProfileRings rings, double meshVolume)
        {
            var result = new PrimHullCellSet { MeshVolume = meshVolume };
            string problem = CheckRings(rings);
            if (problem != null) { result.Failure = problem; return result; }
            if (!(meshVolume > 0.0) || double.IsInfinity(meshVolume)) { result.Failure = "the mesh has no volume"; return result; }

            List<ProfileSplit> profiles = ProfileSplits(rings);
            List<PathSplit> paths = PathSplits(rings);

            // the fine split, when it fits
            Candidate fine = new Candidate { Profile = profiles[0], Path = FinestSpan(paths, FineSpanDegrees) };
            bool fits = fine.Count <= MaxCells;
            var candidates = new List<Candidate>();
            if (fits)
                candidates.Add(fine);
            candidates.AddRange(CoarserCandidates(profiles, paths, fine));

            // The fine split is used when it fits and cooks. Otherwise the coarser pairs compete on volume.
            PrimHullCellSet best = null;
            int cooked = 0;
            foreach (Candidate candidate in candidates)
            {
                PrimHullCellSet attempt = Cook(backend, rings, candidate, meshVolume);
                cooked++;
                if (attempt == null)
                    continue;
                if (fits && ReferenceEquals(candidate, fine))
                {
                    best = attempt;
                    break;
                }
                if (best == null || Better(attempt, best))
                    best = attempt;
            }

            if (best == null)
            {
                result.Failure = candidates.Count == 0
                    ? "no split of the prim is within " + MaxCells + " cells"
                    : "no split of the prim cooks into convex hulls";
                result.PairsCooked = cooked;
                return result;
            }

            best.Coarsened = !fits;
            best.PairsCooked = cooked;
            return best;
        }

        private static bool Better(PrimHullCellSet a, PrimHullCellSet b)
        {
            double ea = Math.Abs(a.VolumeRatio - 1.0), eb = Math.Abs(b.VolumeRatio - 1.0);
            if (Math.Abs(ea - eb) > 1e-9)
                return ea < eb;
            return a.CellCount < b.CellCount;
        }

        /// <summary>
        /// The pairs tried when the fine split does not fit: for each coarser profile sector (the fine sector too), the
        /// finest path span that brings the cell count within <see cref="MaxCells"/>. Pairs that cut the prim the same way
        /// as an earlier one are dropped.
        /// </summary>
        private static IEnumerable<Candidate> CoarserCandidates(List<ProfileSplit> profiles, List<PathSplit> paths, Candidate skip)
        {
            var seen = new HashSet<string>();
            if (skip != null)
                seen.Add(skip.Profile.Key + "|" + skip.Path.Key);
            foreach (ProfileSplit profile in profiles)
            {
                foreach (PathSplit path in paths)
                {
                    if (profile.Pieces.Count * path.Spans.Count > MaxCells)
                        continue;
                    if (seen.Add(profile.Key + "|" + path.Key))
                        yield return new Candidate { Profile = profile, Path = path };
                    break;      // paths run from the finest span to the coarsest: the first that fits is the finest
                }
            }
        }

        /// <summary>
        /// The cell counts of the pairs that <see cref="Build"/> would cook, without cooking anything: the fine split when
        /// it fits, else the coarser candidates. Empty when no split fits (it cannot happen: a whole-profile, whole-path
        /// cell is always one cell).
        /// </summary>
        internal static IReadOnlyList<int> CandidateCellCounts(PrimProfileRings rings)
        {
            if (CheckRings(rings) != null)
                return Array.Empty<int>();
            List<ProfileSplit> profiles = ProfileSplits(rings);
            List<PathSplit> paths = PathSplits(rings);
            Candidate fine = new Candidate { Profile = profiles[0], Path = FinestSpan(paths, FineSpanDegrees) };
            if (fine.Count <= MaxCells)
                return new[] { fine.Count };
            return CoarserCandidates(profiles, paths, fine).Select(c => c.Count).ToArray();
        }

        private static PathSplit FinestSpan(List<PathSplit> paths, double spanDegrees) =>
            paths.First(p => Math.Abs(p.SpanDegrees - spanDegrees) < 1e-9);

        private static string CheckRings(PrimProfileRings rings)
        {
            if (rings == null)
                return "no rings";
            int centre = rings.HasCentreVertex ? 1 : 0;
            if (rings.LayerCount < 2 || rings.OuterVertexCount < 2
                || rings.LayerVertexCount != centre + rings.OuterVertexCount + rings.HollowVertexCount
                || rings.ProfileAngles.Count != rings.LayerVertexCount
                || rings.Vertices.Count != rings.LayerCount * rings.LayerVertexCount)
                return "the rings are not a profile swept along a path";
            return null;
        }

        // ------------------------------------------------------------------------------------------ the profile

        /// <summary>The profile cut at each sector size of the ladder, finest first. Splits that came out the same are kept once.</summary>
        private static List<ProfileSplit> ProfileSplits(PrimProfileRings rings)
        {
            var splits = new List<ProfileSplit>();
            var keys = new HashSet<string>();
            foreach (double sector in SectorLadder)
            {
                ProfileSplit split = SplitProfile(rings, sector);
                if (keys.Add(split.Key) || splits.Count == 0)
                    splits.Add(split);
            }
            return splits;
        }

        private static ProfileSplit SplitProfile(PrimProfileRings rings, double sectorDegrees)
        {
            int centre = rings.HasCentreVertex ? 1 : 0;
            int no = rings.OuterVertexCount, nh = rings.HollowVertexCount;
            var split = new ProfileSplit { SectorDegrees = sectorDegrees };
            double sector = sectorDegrees / Degrees;

            double[] outer = new double[no];
            for (int k = 0; k < no; k++)
                outer[k] = rings.ProfileAngles[centre + k];
            double sweep = outer[no - 1];

            if (nh > 0)
            {
                // the hollow ring in angular order: stored index first + nh - 1 - j is its j-th vertex
                int first = centre + no;
                double[] hollow = new double[nh];
                for (int j = 0; j < nh; j++)
                    hollow[j] = rings.ProfileAngles[first + nh - 1 - j];

                // the angles where either ring has a vertex, in order; a piece is a run of them
                var bounds = new List<double>();
                foreach (double b in outer.Concat(hollow).OrderBy(x => x))
                    if (bounds.Count == 0 || b - bounds[bounds.Count - 1] > AngleTolerance)
                        bounds.Add(b);

                int s = 0;
                while (s < bounds.Count - 1)
                {
                    int e = s + 1;
                    while (e + 1 < bounds.Count && bounds[e + 1] - bounds[s] <= sector + AngleTolerance)
                        e++;
                    var piece = new List<RingPoint>();
                    for (int k = s; k <= e; k++)
                    {
                        piece.Add(PointAt(outer, i => centre + i, bounds[k]));
                        piece.Add(PointAt(hollow, j => first + nh - 1 - j, bounds[k]));
                    }
                    split.Pieces.Add(piece.ToArray());
                    s = e;
                }
            }
            else if (sweep >= 2.0 * Math.PI - AngleTolerance)
            {
                // a solid profile that goes all the way round is convex
                var piece = new RingPoint[rings.LayerVertexCount];
                for (int i = 0; i < piece.Length; i++)
                    piece[i] = new RingPoint(i, i, 0f);
                split.Pieces.Add(piece);
            }
            else
            {
                // a cut solid profile is a fan from the centre; wider than half a turn it is cut into fans of at most half a turn
                double limit = Math.PI + AngleTolerance;
                int s = 0;
                while (s < no - 1)
                {
                    int e = s + 1;
                    while (e + 1 < no && outer[e + 1] - outer[s] <= limit)
                        e++;
                    var piece = new List<RingPoint>();
                    if (centre == 1)
                        piece.Add(new RingPoint(0, 0, 0f));
                    for (int k = s; k <= e; k++)
                        piece.Add(new RingPoint(centre + k, centre + k, 0f));
                    split.Pieces.Add(piece.ToArray());
                    s = e;
                }
            }

            var key = new StringBuilder();
            foreach (RingPoint[] piece in split.Pieces)
            {
                foreach (RingPoint p in piece)
                    key.Append(p.A.ToString(CultureInfo.InvariantCulture)).Append(',').Append(p.B.ToString(CultureInfo.InvariantCulture))
                       .Append(',').Append(p.T.ToString("R", CultureInfo.InvariantCulture)).Append(' ');
                key.Append('/');
            }
            split.Key = key.ToString();
            return split;
        }

        /// <summary>The point of a ring at angle <paramref name="b"/>: its vertex when one is there, else a point on the edge that spans it.</summary>
        private static RingPoint PointAt(double[] angles, Func<int, int> layerIndex, double b)
        {
            int n = angles.Length;
            for (int j = 0; j < n; j++)
                if (Math.Abs(angles[j] - b) < AngleTolerance)
                {
                    int i = layerIndex(j);
                    return new RingPoint(i, i, 0f);
                }
            for (int j = 0; j < n - 1; j++)
                if (b > angles[j] && b < angles[j + 1])
                {
                    float t = (float)((b - angles[j]) / (angles[j + 1] - angles[j]));
                    return new RingPoint(layerIndex(j), layerIndex(j + 1), t);
                }
            int end = layerIndex(b <= angles[0] ? 0 : n - 1);
            return new RingPoint(end, end, 0f);
        }

        // ------------------------------------------------------------------------------------------ the path

        /// <summary>The path cut into spans at each span size of the ladder, finest first. Splits that came out the same are kept once, but the 30 degree one always.</summary>
        private static List<PathSplit> PathSplits(PrimProfileRings rings)
        {
            int intervals = rings.LayerCount - 1;
            double total = 0.0;
            for (int i = 0; i < intervals; i++)
                total += (Math.Abs(rings.LayerTurns[i + 1] - rings.LayerTurns[i]) + Math.Abs(rings.LayerTwists[i + 1] - rings.LayerTwists[i])) * Degrees;
            double perInterval = total / intervals;

            var splits = new List<PathSplit>();
            var keys = new HashSet<string>();
            foreach (double span in SpanLadder)
            {
                var split = new PathSplit { SpanDegrees = span };
                int per = perInterval < 1e-6 ? intervals : Math.Clamp((int)Math.Floor(span / perInterval + 1e-6), 1, intervals);
                int count = (intervals + per - 1) / per;
                int at = 0;
                var key = new StringBuilder();
                for (int sp = 0; sp < count; sp++)
                {
                    // spread the intervals evenly over the spans
                    int length = intervals / count + (sp < intervals % count ? 1 : 0);
                    split.Spans.Add((at, at + length));
                    key.Append(at).Append('-').Append(at + length).Append(' ');
                    at += length;
                }
                split.Key = key.ToString();
                if (keys.Add(split.Key) || Math.Abs(span - FineSpanDegrees) < 1e-9)
                    splits.Add(split);
            }
            return splits;
        }

        // ------------------------------------------------------------------------------------------ cooking

        /// <summary>The cell points of a candidate: for each piece and span, the piece's points on every layer of the span.</summary>
        private static SVector3[][] CellPoints(PrimProfileRings rings, Candidate candidate)
        {
            var cells = new List<SVector3[]>(candidate.Count);
            int size = rings.LayerVertexCount;
            foreach (RingPoint[] piece in candidate.Profile.Pieces)
                foreach ((int first, int last) in candidate.Path.Spans)
                {
                    var points = new HashSet<SVector3>();
                    var list = new List<SVector3>();
                    for (int layer = first; layer <= last; layer++)
                        foreach (RingPoint p in piece)
                        {
                            OpenMetaverse.Vector3 a = rings.Vertices[layer * size + p.A], b = rings.Vertices[layer * size + p.B];
                            var v = new SVector3(a.X + (b.X - a.X) * p.T, a.Y + (b.Y - a.Y) * p.T, a.Z + (b.Z - a.Z) * p.T);
                            if (points.Add(v))
                                list.Add(v);
                        }
                    cells.Add(list.ToArray());
                }
            return cells.ToArray();
        }

        /// <summary>Cooks one candidate's cells as a compound, measures it and throws the shapes away. Null when a cell does not cook.</summary>
        private static PrimHullCellSet Cook(IPhysicsBackend backend, PrimProfileRings rings, Candidate candidate, double meshVolume)
        {
            SVector3[][] cells = CellPoints(rings, candidate);
            ShapeId compound = CookCompound(backend, cells, out int failed);
            if (failed > 0 || !compound.IsValid)
                return null;
            double volume = backend.GetShapeVolume(compound);
            backend.ReleaseShape(compound);
            if (!(volume > 0.0) || double.IsInfinity(volume))
                return null;
            return new PrimHullCellSet
            {
                Success = true,
                Cells = cells,
                Pieces = candidate.Profile.Pieces.Count,
                Spans = candidate.Path.Spans.Count,
                SectorDegrees = candidate.Profile.SectorDegrees,
                SpanDegrees = candidate.Path.SpanDegrees,
                CellsVolume = volume,
                MeshVolume = meshVolume,
                VolumeRatio = volume / meshVolume,
            };
        }

        /// <summary>
        /// Cooks each cell as a convex hull and joins them into one compound shape (a lone cell is returned as its hull).
        /// When any cell does not cook, everything made is released and an invalid shape is returned, with
        /// <paramref name="failed"/> the number of cells that did not cook.
        /// </summary>
        internal static ShapeId CookCompound(IPhysicsBackend backend, IReadOnlyList<SVector3[]> cells, out int failed)
        {
            var hulls = new List<ShapeId>(cells.Count);
            failed = 0;
            foreach (SVector3[] points in cells)
            {
                ShapeId hull = points.Length >= 4 ? backend.CreateConvexHullShape(points) : default;
                if (hull.IsValid)
                    hulls.Add(hull);
                else
                    failed++;
            }

            if (failed > 0 || hulls.Count == 0)
            {
                foreach (ShapeId hull in hulls)
                    backend.ReleaseShape(hull);
                return default;
            }
            if (hulls.Count == 1)
                return hulls[0];

            ShapeId compound = backend.CreateHullCompoundShape(hulls.ToArray());
            foreach (ShapeId hull in hulls)
                backend.ReleaseShape(hull);         // the compound holds its own reference to each
            if (!compound.IsValid)
                failed = hulls.Count;
            return compound;
        }

        // ------------------------------------------------------------------------------------------ volume

        /// <summary>
        /// The volume of a closed triangle mesh (the divergence theorem: the sum of the signed volumes of the tetrahedra
        /// from the origin to each triangle). <paramref name="vertices"/> is x, y, z for each vertex.
        /// </summary>
        internal static double MeshVolume(float[] vertices, int[] indices)
        {
            double volume = 0.0;
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                int a = indices[t] * 3, b = indices[t + 1] * 3, c = indices[t + 2] * 3;
                double ax = vertices[a], ay = vertices[a + 1], az = vertices[a + 2];
                double bx = vertices[b], by = vertices[b + 1], bz = vertices[b + 2];
                double cx = vertices[c], cy = vertices[c + 1], cz = vertices[c + 2];
                volume += ax * (by * cz - bz * cy) - ay * (bx * cz - bz * cx) + az * (bx * cy - by * cx);
            }
            return Math.Abs(volume) / 6.0;
        }
    }
}
