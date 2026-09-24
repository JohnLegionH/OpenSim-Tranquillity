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
            sb.AppendLine($"  job pool          threads={s.JobThreadCount} (process-wide)");
            sb.AppendLine($"  rejected non-finite  {s.RejectedNonFinite}");
            sb.Append($"  scene buffers     bodies={bodyBuf} (overflowed {bodyOverflowFrames} steps) characters={charBuf} (full {charFullFrames} steps) contacts={contactBuf} (overflowed {contactOverflowFrames} steps)");
            return sb.ToString();
        }
    }
}
