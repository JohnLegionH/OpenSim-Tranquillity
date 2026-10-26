/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;

namespace SLua.ScriptEngine.Tests;

/// <summary>Stands in for what the engine's scripts hold from the region: records every call, and says a rider is still to come while one is pending.</summary>
internal sealed class RecordingGrants : IScriptGrants
{
    private readonly object m_lock = new();
    private readonly List<string> m_calls = new();

    /// <summary>The avatar a crossing object waits for; the wait is on while <see cref="RiderPending"/> is set and this avatar has not arrived.</summary>
    public UUID Rider = UUID.Random();

    public volatile bool RiderPending;

    /// <summary>Forgets the calls so far (adding a test avatar already tells the arrival).</summary>
    public void Clear()
    {
        lock (m_lock) m_calls.Clear();
    }

    public string[] Calls
    {
        get { lock (m_lock) return m_calls.ToArray(); }
    }

    private void Add(string call)
    {
        lock (m_lock) m_calls.Add(call);
    }

    public void ObjectRemoved(SceneObjectGroup group) => Add("removed:" + group.UUID);

    public void OwnerChanged(SceneObjectGroup group) => Add("owner:" + group.UUID);

    public void ControlsReleased(UUID agentId, UUID[] itemIds, bool byRegion) => Add($"controls:{agentId}:{itemIds.Length}:{(byRegion ? "region" : "script")}");

    public void AvatarArrived(UUID agentId) => Add("arrived:" + agentId);

    public void ExperienceLandCheck(UUID agentId) => Add("expland:" + agentId);

    public void ExperienceGrantEnded(UUID itemId, UUID granterId, UUID experienceId) => Add($"expend:{itemId}:{granterId}:{experienceId}");

    public void PermissionsRemoved(UUID itemId) => Add("perms:" + itemId);
}
