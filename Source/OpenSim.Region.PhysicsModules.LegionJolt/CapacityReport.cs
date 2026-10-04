// Legion Grid - capacity surfacing for the Jolt module (JOLT-3, audit S-4a/S-4b).
//
// The backend now counts every PhysicsUpdateError flag and every CreateBody that MaxBodies refused. This turns two
// snapshots of those counters into the one warning line an operator can act on (which [Jolt] key to raise), and
// renders the `jolt capacity` read-out. Pure: no scene, no backend, no logger - so it is tested directly.

using System.Collections.Generic;
using System.Text;
using Legion.Physics;

namespace OpenSim.Region.PhysicsModules.LegionJolt
{
    internal static class CapacityReport
    {
        /// <summary>
        /// The warning for what went wrong between <paramref name="prev"/> and <paramref name="cur"/>, or null when
        /// nothing did. Example: "[LEGION JOLT] Ebony: collisions dropped (ContactConstraintsFull x37 in 10s) - raise
        /// [Jolt] MaxContactConstraints".
        /// </summary>
        internal static string Warning(string region, in PhysicsCapacityStats prev, in PhysicsCapacityStats cur, double seconds)
        {
            var dropped = new List<string>();
            var keys = new List<string>();
            void Flag(string name, long delta, string key)
            {
                if (delta <= 0) return;
                dropped.Add($"{name} x{delta}");
                if (!keys.Contains(key)) keys.Add(key);
            }
            Flag("ContactConstraintsFull", cur.ContactConstraintsFullSteps - prev.ContactConstraintsFullSteps, "MaxContactConstraints");
            Flag("BodyPairCacheFull", cur.BodyPairCacheFullSteps - prev.BodyPairCacheFullSteps, "MaxBodyPairs");
            // Jolt sizes the manifold cache from both caps.
            Flag("ManifoldCacheFull", cur.ManifoldCacheFullSteps - prev.ManifoldCacheFullSteps, "MaxBodyPairs");
            if (cur.ManifoldCacheFullSteps > prev.ManifoldCacheFullSteps && !keys.Contains("MaxContactConstraints"))
                keys.Add("MaxContactConstraints");

            long refused = cur.BodyCreateFailures - prev.BodyCreateFailures;
            if (dropped.Count == 0 && refused <= 0)
                return null;

            string span = $"{seconds:0}s";
            var sb = new StringBuilder($"{LegionJoltScene.LogHeader} {region}:");
            if (dropped.Count > 0)
                sb.Append($" collisions dropped ({string.Join(", ", dropped)} in {span}) - raise [Jolt] {string.Join(", ", keys)}");
            if (refused > 0)
                sb.Append(dropped.Count > 0 ? ";" : "")
                  .Append($" bodies refused (MaxBodies {cur.MaxBodies} reached x{refused} in {span}; those prims have no physics) - raise [Jolt] MaxBodies");
            return sb.ToString();
        }

        /// <summary>The share of frame time above which waiting at the job pool's update gate is worth a warning.</summary>
        internal const double GateWaitWarnFraction = 0.20;

        /// <summary>
        /// JOLT-7: the warning when this region spent more than 20% of its frame time over the last interval waiting
        /// for its job pool (which runs one region's physics update at a time), or null when it did not.
        /// </summary>
        internal static string GateWarning(string region, double waitMs, double frameMs, int poolIndex, int jobPools)
        {
            if (frameMs <= 0 || waitMs <= GateWaitWarnFraction * frameMs)
                return null;
            return $"{LegionJoltScene.LogHeader} {region}: waited {waitMs:0} ms of {frameMs:0} ms frame time ({waitMs / frameMs:0%}) " +
                   $"for Jolt job pool {poolIndex} of {jobPools} (one physics update at a time per pool) - raise [Jolt] JobPools";
        }

        /// <summary>The `jolt capacity` read-out: backend stats plus the scene's own buffers.</summary>
        internal static string Render(string region, in PhysicsCapacityStats s,
            int bodyBuf, long bodyOverflowFrames, int charBuf, long charFullFrames, int contactBuf, long contactOverflowFrames)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{LegionJoltScene.LogHeader} capacity for '{region}':");
            sb.AppendLine($"  bodies            live={s.LiveBodyCount} active={s.ActiveBodyCount} MaxBodies={s.MaxBodies} refused={s.BodyCreateFailures}");
            sb.AppendLine($"  caps              MaxBodyPairs={s.MaxBodyPairs} MaxContactConstraints={s.MaxContactConstraints}");
            sb.AppendLine($"  update errors     last={s.LastUpdateError} steps: BodyPairCacheFull={s.BodyPairCacheFullSteps} ManifoldCacheFull={s.ManifoldCacheFullSteps} ContactConstraintsFull={s.ContactConstraintsFullSteps}");
            sb.AppendLine($"  characters        {s.CharacterCount}");
            sb.AppendLine($"  contact ring      capacity={s.ContactRingCapacity} dropped={s.DroppedContacts} (cumulative)");
            sb.AppendLine($"  job pools         JobPools={s.JobPools} threadsPerPool={s.JobThreadsPerPool} (ThreadCount {s.JobThreadCount}; process-wide)");
            sb.AppendLine($"  this region       pool={s.PoolIndex} waits={s.UpdateGateWaits} waitMs total={s.UpdateGateWaitMsTotal:0.0} max={s.UpdateGateWaitMsMax:0.0}; pool peakInside={s.PoolPeakInside}");
            sb.AppendLine($"  rejected non-finite  {s.RejectedNonFinite}");
            sb.Append($"  scene buffers     bodies={bodyBuf} (overflowed {bodyOverflowFrames} steps) characters={charBuf} (full {charFullFrames} steps) contacts={contactBuf} (overflowed {contactOverflowFrames} steps)");
            return sb.ToString();
        }
    }
}
