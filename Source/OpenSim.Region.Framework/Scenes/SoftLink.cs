/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace OpenSim.Region.Framework.Scenes;

/// <summary>
/// The numbers of the standing-rider rules, in one place.
/// </summary>
public static class RideOnLimits
{
    /// <summary>How long an avatar's feet must touch the same object before it counts as standing on it (ms).</summary>
    public const int StartMs = 250;

    /// <summary>How long an avatar may be off the object it stands on before the link ends (ms). Covers a jump or hop.</summary>
    public const int BreakMs = 1000;

    /// <summary>How far outside the object's horizontal bounds a standing avatar may be and still be carried (m).</summary>
    public const float PositionGate = 1.0f;

    /// <summary>How long a region that received a crossing object waits for its riders before it runs it (ms).</summary>
    public const int ArriveHoldMs = 10000;
}

/// <summary>
/// The soft link of a standing avatar to the object it stands on: not a physical or a scene-graph link, only a record
/// that the avatar has been standing on that object, so the object's crossing can take the avatar with it.
/// </summary>
public sealed class SoftLink
{
    private UUID m_candidate = UUID.Zero;
    private double m_candidateSince;
    private double m_lastContact;

    /// <summary>The object the avatar is linked to, or <see cref="UUID.Zero"/>.</summary>
    public UUID GroupID { get; private set; } = UUID.Zero;
    public bool IsLinked => GroupID.IsNotZero();

    /// <summary>
    /// One report of the avatar's feet. The link starts when the feet have touched the same object in every report for
    /// <see cref="RideOnLimits.StartMs"/>, and ends when they have touched it in none for <see cref="RideOnLimits.BreakMs"/>
    /// (a jump or a gap in the engine's reports does not end it), or at once when the avatar cannot be standing.
    /// </summary>
    /// <param name="nowMs">time of the report, milliseconds</param>
    /// <param name="contactGroup">the object the lowest feet contact names, or <see cref="UUID.Zero"/> for none or for ground</param>
    /// <param name="canStand">false while flying, sitting, in transit, a child agent or an NPC</param>
    public void Update(double nowMs, UUID contactGroup, bool canStand)
    {
        if (!canStand)
        {
            Clear();
            return;
        }

        if (contactGroup.IsZero())
        {
            m_candidate = UUID.Zero;
            if (IsLinked && nowMs - m_lastContact >= RideOnLimits.BreakMs)
                GroupID = UUID.Zero;
            return;
        }

        if (contactGroup.Equals(GroupID))
        {
            m_candidate = UUID.Zero;
            m_lastContact = nowMs;
            return;
        }

        if (!contactGroup.Equals(m_candidate))
        {
            m_candidate = contactGroup;
            m_candidateSince = nowMs;
        }

        if (nowMs - m_candidateSince >= RideOnLimits.StartMs)
        {
            GroupID = contactGroup;
            m_lastContact = nowMs;
            m_candidate = UUID.Zero;
        }
        else if (IsLinked && nowMs - m_lastContact >= RideOnLimits.BreakMs)
        {
            // standing on something else for a moment, the old object not touched for the break time
            GroupID = UUID.Zero;
        }
    }

    public void Clear()
    {
        GroupID = UUID.Zero;
        m_candidate = UUID.Zero;
    }
}
