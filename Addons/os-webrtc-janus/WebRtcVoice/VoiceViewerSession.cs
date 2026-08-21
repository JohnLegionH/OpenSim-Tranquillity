/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using OpenMetaverse;

namespace osWebRtcVoice;

public class VoiceViewerSession : IVoiceViewerSession
{

    // A simple session structure that is used when the connection is actually in the
    //    remote service.
    public VoiceViewerSession(IWebRtcVoiceService pVoiceService, UUID pRegionId, UUID pAgentId)
    {
        RegionId = pRegionId;
        AgentId = pAgentId;
        ViewerSessionID = UUID.Random().ToString();
        VoiceService = pVoiceService;

    }
    public string ViewerSessionID { get; set; }
    public IWebRtcVoiceService VoiceService { get; set; }
    public string VoiceServiceSessionId
    {
        get => throw new System.NotImplementedException();
        set => throw new System.NotImplementedException();
    }
    public UUID RegionId { get; set; }
    public UUID AgentId { get; set; }

    // =====================================================================
    // ViewerSessions hold the connection information for the client connection through to the voice service.
    // This collection is static and is simulator wide so there will be sessions for all regions and all clients.
    public static Dictionary<string, IVoiceViewerSession> ViewerSessions = new Dictionary<string, IVoiceViewerSession>();

    // Agent-keyed membership index: region -> (agent -> refcount). Maintained alongside
    // ViewerSessions in AddViewerSession/RemoveViewerSession under the SAME lock, so it never
    // diverges from the session collection.
    //
    // Why refcounted rather than a bare set: an agent can momentarily hold two sessions in a
    // region (a reconnect/handoff overlaps the old teardown). A set would drop the agent the
    // instant the first session is removed, blinking it out of the matrix while it is still
    // voiced. The refcount keeps the agent a member until the LAST of its sessions leaves.
    //
    // Scoped by RegionId, not global: the query is asked per-region during matrix derivation, and
    // a global "is this agent voiced anywhere" answer would wrongly admit child agents of adjacent
    // regions (common) into this region's matrix.
    private static readonly Dictionary<UUID, Dictionary<UUID, int>> AgentMembershipByRegion
        = new Dictionary<UUID, Dictionary<UUID, int>>();

    // Per-region membership query. O(1), lock-consistent with the session collection. Callers on
    // the matrix-derivation path use this to admit a presence WITHOUT ever iterating ViewerSessions.
    public static bool IsAgentInRegion(UUID pRegionId, UUID pAgentId)
    {
        lock (ViewerSessions)
        {
            return AgentMembershipByRegion.TryGetValue(pRegionId, out Dictionary<UUID, int> agents)
                && agents.ContainsKey(pAgentId);
        }
    }

    // Index maintenance helpers. Both assume the ViewerSessions lock is already held.
    private static void IndexAdd(UUID pRegionId, UUID pAgentId)
    {
        if (!AgentMembershipByRegion.TryGetValue(pRegionId, out Dictionary<UUID, int> agents))
        {
            agents = new Dictionary<UUID, int>();
            AgentMembershipByRegion[pRegionId] = agents;
        }
        agents.TryGetValue(pAgentId, out int count);
        agents[pAgentId] = count + 1;
    }

    private static void IndexRemove(UUID pRegionId, UUID pAgentId)
    {
        if (!AgentMembershipByRegion.TryGetValue(pRegionId, out Dictionary<UUID, int> agents))
            return;
        if (!agents.TryGetValue(pAgentId, out int count))
            return;
        if (count <= 1)
        {
            agents.Remove(pAgentId);
            if (agents.Count == 0)
                AgentMembershipByRegion.Remove(pRegionId);
        }
        else
        {
            agents[pAgentId] = count - 1;
        }
    }
    // Get a viewer session by the viewer session ID
    public static bool TryGetViewerSession(string pViewerSessionId, out IVoiceViewerSession pViewerSession)
    {
        lock (ViewerSessions)
        {
            return ViewerSessions.TryGetValue(pViewerSessionId, out pViewerSession);
        }
    }
    // public static bool TryGetViewerSessionByAgentId(UUID pAgentId, out IVoiceViewerSession pViewerSession)
    public static bool TryGetViewerSessionByAgentId(UUID pAgentId, out IEnumerable<KeyValuePair<string, IVoiceViewerSession>> pViewerSessions)
    {
        lock (ViewerSessions)
        {
            pViewerSessions = ViewerSessions.Where(v => v.Value.AgentId == pAgentId);
            return pViewerSessions.Count() > 0;
        }
    }
    // Get a viewer session by the VoiceService session ID
    public static bool TryGetViewerSessionByVSSessionId(string pVSSessionId, out IVoiceViewerSession pViewerSession)
    {
        lock (ViewerSessions)
        {
            var sessions = ViewerSessions.Where(v => v.Value.VoiceServiceSessionId == pVSSessionId);
            if (sessions.Count() > 0)
            {
                pViewerSession = sessions.First().Value;
                return true;
            }
            pViewerSession = null;
            return false;
        }
    }
    public static void AddViewerSession(IVoiceViewerSession pSession)
    {
        lock (ViewerSessions)
        {
            ViewerSessions[pSession.ViewerSessionID] = pSession;
            IndexAdd(pSession.RegionId, pSession.AgentId);
        }
    }
    public static void RemoveViewerSession(string pSessionId)
    {
        lock (ViewerSessions)
        {
            // Decrement the membership index using the removed session's own region/agent — done
            // before the dictionary removal so the lookup still resolves. A session ID with no
            // entry (double-remove) leaves the index untouched.
            if (ViewerSessions.TryGetValue(pSessionId, out IVoiceViewerSession session))
            {
                ViewerSessions.Remove(pSessionId);
                IndexRemove(session.RegionId, session.AgentId);
            }
        }
    }

    // Update a ViewSession from one ID to another.
    // Remove the old session ID from the ViewerSessions collection, update the
    //     sessionID value in  the IVoiceViewerSession, and add the session back to the
    //     collection.
    // This is used in the kludge to synchronize a region's ViewerSessionID with the
    //     remote VoiceService's session ID.
    public static void UpdateViewerSessionId(IVoiceViewerSession pSession, string pNewSessionId)
    {
        lock (ViewerSessions)
        {
            ViewerSessions.Remove(pSession.ViewerSessionID);
            pSession.ViewerSessionID = pNewSessionId;
            ViewerSessions[pSession.ViewerSessionID] = pSession;
        }
    }

    public Task Shutdown()
    {
        throw new System.NotImplementedException();
    }
}

