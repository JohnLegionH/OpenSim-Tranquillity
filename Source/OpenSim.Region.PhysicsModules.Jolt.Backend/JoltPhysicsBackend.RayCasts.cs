/* Copyright (c) Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Script ray casts at a bounded cost (IPhysicsBackend.RayCastLimited).
//
// RayCastAll asks Jolt for every hit along the ray (AllHitSorted) and keeps the closest few. Its cost grows with
// everything the ray crosses: a long ray through a pile of prims or a dense mesh collects every triangle it passes,
// and all of it runs under _simLock, which the region's physics step also takes, while holding its job pool. Script
// casts come from strangers' scripts at script rate, so the script path bounds three things:
//   - the hits looked at: a collector that keeps only the closest N (N = the hits the caller asked for) and tells
//     Jolt, once it has N, to stop looking past the furthest of them (the early-out fraction), so the engine skips
//     whatever lies beyond;
//   - one cast: it is cut short when the engine has reported RayCastMaxTestedHits hits to it, or when it runs past
//     what is left of the region's ray cast time;
//   - the region: the time its casts may take in one heartbeat (RayCastBudgetMs); past it, casts are refused until
//     the next heartbeat (BeginRayCastBudget).
// What a script gets for a refused or cut-short cast is the caller's to say (JoltScene: Second Life's
// RCERR_CAST_TIME_EXCEEDED).
//
// The binding has no collector with an early-out callback, so this calls joltc's JPH_NarrowPhaseQuery_CastRay2
// directly (a CastRayCollector whose AddHit hands each hit to a callback and takes the callback's return as the new
// early-out fraction, joltc.cpp CastRayCollectorCallback). The callback runs on the calling thread, inside the
// native call, so the collector's state is per thread.

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using JoltPhysicsSharp;

namespace OpenSim.Region.PhysicsModules.Jolt.Backend
{
    public sealed partial class JoltPhysicsBackend
    {
        // joltc's JPH_RayCastResult.
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRayCastResult
        {
            public uint BodyId;
            public float Fraction;
            public uint SubShapeId2;
        }

        [DllImport("joltc", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern unsafe bool JPH_NarrowPhaseQuery_CastRay2(
            IntPtr query, Vector3* origin, Vector3* direction, RayCastSettings* settings,
            delegate* unmanaged[Cdecl]<IntPtr, NativeRayCastResult*, float> callback, IntPtr userData,
            IntPtr broadPhaseLayerFilter, IntPtr objectLayerFilter, IntPtr bodyFilter, IntPtr shapeFilter);

        // Jolt's CastRayCollector starts at 1 + FLT_EPSILON (a hit at the very end of the ray counts); returning it
        // leaves the early-out where it was.
        private const float InitialEarlyOut = 1f + 1.1920929e-7f;

        // The closest hits seen so far, at most Want of them, coincident duplicates collapsed as RayCastAll does.
        private sealed class ClosestHits
        {
            public NativeRayCastResult[] Kept = new NativeRayCastResult[16];
            public int Count;
            public int Want;
            public int Tested;
            public int MaxTested;
            public long Deadline;
            public bool CutShort;
            public float EarlyOut;
            public Vector3 Origin;
            public Vector3 RayDir;

            public void Reset(int want, int maxTested, long deadline, Vector3 origin, Vector3 rayDir)
            {
                if (Kept.Length < want)
                    Kept = new NativeRayCastResult[want];
                Count = 0;
                Want = want;
                Tested = 0;
                MaxTested = maxTested;
                Deadline = deadline;
                CutShort = false;
                EarlyOut = InitialEarlyOut;
                Origin = origin;
                RayDir = rayDir;
            }

            // Returns the new early-out fraction: never above the last one (Jolt requires it to only shrink).
            public float Add(in NativeRayCastResult r)
            {
                if (++Tested > MaxTested || Stopwatch.GetTimestamp() > Deadline)
                {
                    CutShort = true;
                    return EarlyOut = -float.MaxValue;   // Jolt tests nothing more: no fraction is below this
                }

                // The same body at the same point (the heightfield's two triangles meeting under the ray): one hit,
                // the nearer.
                Vector3 point = Origin + RayDir * r.Fraction;
                for (int i = 0; i < Count; i++)
                {
                    if (Kept[i].BodyId == r.BodyId
                        && Vector3.DistanceSquared(Origin + RayDir * Kept[i].Fraction, point) < CoincidentEpsilonSq)
                    {
                        if (r.Fraction < Kept[i].Fraction)
                            Kept[i] = r;
                        return EarlyOut;
                    }
                }

                if (Count < Want)
                {
                    Kept[Count++] = r;
                }
                else
                {
                    int far = Furthest();
                    if (!(r.Fraction < Kept[far].Fraction))
                        return EarlyOut;
                    Kept[far] = r;
                }
                if (Count == Want)
                    EarlyOut = Math.Min(EarlyOut, Kept[Furthest()].Fraction);
                return EarlyOut;
            }

            private int Furthest()
            {
                int far = 0;
                for (int i = 1; i < Count; i++)
                    if (Kept[i].Fraction > Kept[far].Fraction)
                        far = i;
                return far;
            }
        }

        [ThreadStatic] private static ClosestHits? t_closest;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static unsafe float OnRayHit(IntPtr userData, NativeRayCastResult* result)
        {
            // Nothing may throw out of a native callback.
            try
            {
                ClosestHits? c = t_closest;
                return c == null ? -float.MaxValue : c.Add(*result);
            }
            catch
            {
                return -float.MaxValue;
            }
        }

        // Ray cast time spent this heartbeat, and the counters GetCapacityStats reports.
        private long _rayUsedTicks;
        private long _rayCasts;
        private long _rayCastsRefused;
        private long _rayCastsCutShort;
        private long _rayTicksTotal;
        private long _rayTicksMaxHeartbeat;

        private long RayBudgetTicks
        {
            get
            {
                float ms = _settings.RayCastBudgetMs > 0f ? _settings.RayCastBudgetMs : PhysicsBackendSettings.DefaultRayCastBudgetMs;
                return (long)(ms * Stopwatch.Frequency / 1000.0);
            }
        }

        private int RayMaxTested => _settings.RayCastMaxTestedHits > 0 ? _settings.RayCastMaxTestedHits : PhysicsBackendSettings.DefaultRayCastMaxTestedHits;

        public void BeginRayCastBudget() => Interlocked.Exchange(ref _rayUsedTicks, 0);

        public unsafe int RayCastLimited(Vector3 origin, Vector3 direction, float maxDistance, QueryFilter filter, Span<RayHit> hits, out RayCastStatus status)
        {
            status = RayCastStatus.Ok;
            if (_system == null || hits.Length == 0)
                return 0;
            if (!IsFinite(origin) || !IsFinite(direction) || !float.IsFinite(maxDistance))
                return 0;
            float len = direction.Length();
            if (len < 1e-12f || maxDistance <= 0f)
                return 0;

            Vector3 rayDir = direction / len * maxDistance;
            lock (_simLock)
            {
                if (_disposed) return 0;   // backend torn down (shutdown race) - no native call
                Interlocked.Increment(ref _rayCasts);

                long budget = RayBudgetTicks;
                long used = Interlocked.Read(ref _rayUsedTicks);
                if (used >= budget)
                {
                    Interlocked.Increment(ref _rayCastsRefused);
                    status = RayCastStatus.Refused;
                    return 0;
                }

                long start = Stopwatch.GetTimestamp();
                ClosestHits c = t_closest ??= new ClosestHits();
                c.Reset(hits.Length, RayMaxTested, start + (budget - used), origin, rayDir);

                var settings = new RayCastSettings();   // what RayCastAll passes: the same faces are tested
                Vector3 o = origin, d = rayDir;
                IntPtr objectFilter = FilterFor(filter).Handle;
                JPH_NarrowPhaseQuery_CastRay2(_system.NarrowPhaseQuery.Handle, &o, &d, &settings,
                    &OnRayHit, IntPtr.Zero, IntPtr.Zero, objectFilter, IntPtr.Zero, IntPtr.Zero);

                int n = 0;
                if (c.CutShort)
                {
                    Interlocked.Increment(ref _rayCastsCutShort);
                    status = RayCastStatus.CutShort;
                }
                else
                {
                    Span<NativeRayCastResult> kept = c.Kept.AsSpan(0, c.Count);
                    kept.Sort(static (a, b) => a.Fraction.CompareTo(b.Fraction));
                    for (; n < kept.Length; n++)
                    {
                        NativeRayCastResult r = kept[n];
                        var bodyId = new BodyID(r.BodyId);
                        Vector3 point = origin + rayDir * r.Fraction;
                        _joltToRecord.TryGetValue(r.BodyId, out JoltBodyRecord? rec);
                        hits[n] = new RayHit
                        {
                            Body = rec != null ? new BodyId(rec.Handle) : BodyId.Invalid,
                            UserData = rec != null ? rec.UserData : 0u,
                            ChildUserData = ResolveChildUserData(rec, r.SubShapeId2),
                            Point = point,
                            Normal = SurfaceNormalOf(bodyId, r.SubShapeId2, point),
                            Distance = maxDistance * r.Fraction,
                        };
                    }
                }

                long spent = Stopwatch.GetTimestamp() - start;
                long nowUsed = Interlocked.Add(ref _rayUsedTicks, spent);
                Interlocked.Add(ref _rayTicksTotal, spent);
                long max;
                while (nowUsed > (max = Interlocked.Read(ref _rayTicksMaxHeartbeat))
                       && Interlocked.CompareExchange(ref _rayTicksMaxHeartbeat, nowUsed, max) != max) { }
                return n;
            }
        }

        private void FillRayCastStats(ref PhysicsCapacityStats s)
        {
            s.RayCasts = Interlocked.Read(ref _rayCasts);
            s.RayCastsRefused = Interlocked.Read(ref _rayCastsRefused);
            s.RayCastsCutShort = Interlocked.Read(ref _rayCastsCutShort);
            s.RayCastMsTotal = Interlocked.Read(ref _rayTicksTotal) * 1000.0 / Stopwatch.Frequency;
            s.RayCastMsMaxHeartbeat = Interlocked.Read(ref _rayTicksMaxHeartbeat) * 1000.0 / Stopwatch.Frequency;
        }
    }
}
