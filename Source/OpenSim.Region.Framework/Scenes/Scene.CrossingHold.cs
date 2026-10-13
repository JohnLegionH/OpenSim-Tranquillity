/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Region.Framework.Scenes;

public partial class Scene
{
    /// <summary>How long a released hold is remembered, so that a late recall of it is refused (ms).</summary>
    private const int ReleasedHoldMemoryMs = 60000;

    /// <summary>
    /// An object that arrived together with avatars, kept at rest here (scripts and keyframed motion not started)
    /// until they have all arrived or the time ran out.
    /// </summary>
    private sealed class ArrivalHold
    {
        public SceneObjectGroup Group;
        public GridRegion Source;
        public HashSet<UUID> Waiting;
        public double DeadlineMs;
        public bool AnyArrived;
        public bool Released;
        public double ReleasedAtMs;
    }

    private readonly object m_crossingHoldLock = new();
    private readonly Dictionary<UUID, ArrivalHold> m_arrivalHolds = new();
    private readonly Dictionary<UUID, SceneObjectGroup> m_pendingCrossings = new();

    /// <summary>The clock (ms) the arrival hold of a crossing object is measured on; a test can replace it.</summary>
    public Func<double> CrossingClock { get; set; } = Util.GetTimeStampMS;

    /// <summary>
    /// Called with a short label at each step of an object's crossing from the freeze to the end, so that a test can
    /// make the step fail. Null in a running region.
    /// </summary>
    public Action<SceneObjectGroup, string> CrossingStepHook { get; set; }

    /// <summary>True while an object that arrived with riders is held here, waiting for them.</summary>
    public bool HoldsArrival(UUID objectID)
    {
        lock (m_crossingHoldLock)
            return m_arrivalHolds.TryGetValue(objectID, out ArrivalHold hold) && !hold.Released;
    }

    /// <summary>True while this region keeps the frozen original of an object it sent, until the other region reports it complete.</summary>
    public bool HasPendingCrossing(UUID objectID)
    {
        lock (m_crossingHoldLock)
            return m_pendingCrossings.ContainsKey(objectID);
    }

    /// <summary>
    /// Takes in an object that crossed with riders, held. With no riders listed it is taken in as any crossing object.
    /// </summary>
    public bool IncomingCreateObject(Vector3 newPosition, ISceneObject sog, CrossingHoldRequest hold)
    {
        if (hold is null || hold.ExpectedRiders.Count == 0 || sog is not SceneObjectGroup newObject)
            return IncomingCreateObject(newPosition, sog);

        newObject.HeldForRiders = true;
        if (!IncomingCreateObject(newPosition, sog))
        {
            newObject.HeldForRiders = false;
            return false;
        }

        lock (m_crossingHoldLock)
        {
            m_arrivalHolds[newObject.UUID] = new ArrivalHold
            {
                Group = newObject,
                Source = hold.Source,
                Waiting = new HashSet<UUID>(hold.ExpectedRiders),
                DeadlineMs = CrossingClock() + RideOnLimits.ArriveHoldMs
            };
        }
        return true;
    }

    /// <summary>
    /// The sending region asks to give a held object back. It is discarded, unstarted, unless a rider has arrived or
    /// it was released; an object that is not here (never was, or was recalled before) counts as given back.
    /// </summary>
    public bool RecallHeldObject(UUID objectID)
    {
        ArrivalHold hold;
        lock (m_crossingHoldLock)
        {
            if (!m_arrivalHolds.TryGetValue(objectID, out hold))
                return true;
            if (hold.Released || hold.AnyArrived)
                return false;
            m_arrivalHolds.Remove(objectID);
        }

        try
        {
            hold.Group.HeldForRiders = false;
            DeleteSceneObject(hold.Group, false, true);
        }
        catch (Exception e)
        {
            m_log.LogError(e, $"[SCENE]: Problem discarding the recalled object {objectID}");
        }
        return true;
    }

    /// <summary>The receiving region reports the crossing of an object this region sent as complete: this region lets go of it.</summary>
    public void CrossingCompleted(UUID objectID, IList<UUID> missingRiders)
    {
        SceneObjectGroup group;
        lock (m_crossingHoldLock)
        {
            if (!m_pendingCrossings.Remove(objectID, out group))
                return;
        }

        try
        {
            DeleteSceneObject(group, true, true);
        }
        catch (Exception e)
        {
            m_log.LogError(e, $"[SCENE]: Problem removing the object {objectID} after its crossing completed");
        }
    }

    /// <summary>This region now keeps the frozen original of an object it sent, until the other region reports it complete.</summary>
    internal void AddPendingCrossing(SceneObjectGroup group)
    {
        lock (m_crossingHoldLock)
            m_pendingCrossings[group.UUID] = group;
    }

    internal bool RemovePendingCrossing(UUID objectID)
    {
        lock (m_crossingHoldLock)
            return m_pendingCrossings.Remove(objectID);
    }

    /// <summary>Releases the held arrivals whose time has run out.</summary>
    public void ReleaseExpiredCrossingHolds()
    {
        List<ArrivalHold> due = null;
        lock (m_crossingHoldLock)
        {
            if (m_arrivalHolds.Count == 0)
                return;

            double now = CrossingClock();
            List<UUID> forgotten = null;
            foreach (KeyValuePair<UUID, ArrivalHold> entry in m_arrivalHolds)
            {
                ArrivalHold hold = entry.Value;
                if (hold.Released)
                {
                    if (now - hold.ReleasedAtMs >= ReleasedHoldMemoryMs)
                        (forgotten ??= new List<UUID>()).Add(entry.Key);
                }
                else if (now >= hold.DeadlineMs)
                    (due ??= new List<ArrivalHold>()).Add(hold);
            }

            if (forgotten is not null)
            {
                foreach (UUID id in forgotten)
                    m_arrivalHolds.Remove(id);
            }
        }

        if (due is not null)
        {
            foreach (ArrivalHold hold in due)
                ReleaseArrivalHold(hold);
        }
    }

    /// <summary>An avatar has completed its arrival in this region.</summary>
    internal void NoteArrival(UUID agentId)
    {
        List<ArrivalHold> complete = null;
        lock (m_crossingHoldLock)
        {
            if (m_arrivalHolds.Count == 0)
                return;

            foreach (ArrivalHold hold in m_arrivalHolds.Values)
            {
                if (hold.Released || !hold.Waiting.Remove(agentId))
                    continue;

                hold.AnyArrived = true;
                if (hold.Waiting.Count == 0)
                    (complete ??= new List<ArrivalHold>()).Add(hold);
            }
        }

        if (complete is not null)
        {
            foreach (ArrivalHold hold in complete)
                ReleaseArrivalHold(hold);
        }
    }

    /// <summary>
    /// Lets a held object run: its keyframed motion starts and its scripts resume, then the sending region is told
    /// the crossing is complete, with the riders that did not arrive.
    /// </summary>
    private void ReleaseArrivalHold(ArrivalHold hold)
    {
        List<UUID> missing;
        lock (m_crossingHoldLock)
        {
            if (hold.Released)
                return;
            hold.Released = true;
            hold.ReleasedAtMs = CrossingClock();
            missing = new List<UUID>(hold.Waiting);
        }

        SceneObjectGroup group = hold.Group;
        try
        {
            if (!group.IsDeleted)
            {
                group.HeldForRiders = false;
                foreach (SceneObjectPart part in group.Parts)
                    part.KeyframeMotion?.ReleaseHold();
                group.ResumeScripts();
            }
        }
        catch (Exception e)
        {
            m_log.LogError(e, $"[SCENE]: Problem starting the arrived object {group.UUID}");
        }

        try
        {
            SimulationService?.CrossingCompleted(hold.Source, group.UUID, missing);
        }
        catch (Exception e)
        {
            m_log.LogError(e, $"[SCENE]: Problem reporting the crossing of {group.UUID} complete");
        }
    }
}
