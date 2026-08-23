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
    public UUID ClientSessionId { get; set; }

    // =====================================================================
    // ViewerSessions hold the connection information for the client connection through to the voice service.
    // This collection is static and is simulator wide so there will be sessions for all regions and all clients.
    public static Dictionary<string, IVoiceViewerSession> ViewerSessions = new Dictionary<string, IVoiceViewerSession>();

    // Agent-keyed membership index: region -> (agent -> live session list). Maintained alongside
    // ViewerSessions in AddViewerSession/RemoveViewerSession under the SAME lock, so it never
    // diverges from the session collection.
    //
    // Why a list rather than a bare set or a count: an agent can momentarily hold two sessions
    // in a region (a reconnect/handoff overlaps the old teardown). Membership is the list being
    // non-empty, so the agent stays a member until the LAST of its sessions leaves - the old
    // refcount semantics, with the count now derived from the list. The references themselves
    // exist for close-time teardown: CaptureSessionsForClose selects the dying login's sessions
    // here by (region, agent, ClientSessionId) without ever scanning ViewerSessions.
    //
    // Scoped by RegionId, not global: the query is asked per-region during matrix derivation, and
    // a global "is this agent voiced anywhere" answer would wrongly admit child agents of adjacent
    // regions (common) into this region's matrix.
    private static readonly Dictionary<UUID, Dictionary<UUID, List<IVoiceViewerSession>>> AgentMembershipByRegion
        = new Dictionary<UUID, Dictionary<UUID, List<IVoiceViewerSession>>>();

    // Sessions removed from the registry whose voice-service teardown is in flight or has FAILED.
    // The remove-and-forget guard from the teardown review: a failed Janus cleanup must not make
    // the orphan undiscoverable from the sim, so the session parks here - out of every policy,
    // provision, and matrix read, but visible and retryable - instead of vanishing. Entries leave
    // on successful shutdown (CloseCompleted); retries are re-driven by the provision/close hooks
    // in WebRtcVoiceServiceModule; the "show voice closing" console command reads the snapshot.
    private sealed class ClosingInfo
    {
        public long ParkedTick;      // Environment.TickCount64 at capture, for age reporting
        public string LastFailure;   // "<ExceptionType>: <message>" of the last failed attempt; null while in flight
    }
    private static readonly Dictionary<IVoiceViewerSession, ClosingInfo> ClosingSessions
        = new Dictionary<IVoiceViewerSession, ClosingInfo>();

    // Per-region membership query. O(1), lock-consistent with the session collection. Callers on
    // the matrix-derivation path use this to admit a presence WITHOUT ever iterating ViewerSessions.
    public static bool IsAgentInRegion(UUID pRegionId, UUID pAgentId)
    {
        lock (ViewerSessions)
        {
            return AgentMembershipByRegion.TryGetValue(pRegionId, out Dictionary<UUID, List<IVoiceViewerSession>> agents)
                && agents.TryGetValue(pAgentId, out List<IVoiceViewerSession> sessions)
                && sessions.Count > 0;
        }
    }

    // Index maintenance helpers. Both assume the ViewerSessions lock is already held.
    private static void IndexAdd(IVoiceViewerSession pSession)
    {
        if (!AgentMembershipByRegion.TryGetValue(pSession.RegionId, out Dictionary<UUID, List<IVoiceViewerSession>> agents))
        {
            agents = new Dictionary<UUID, List<IVoiceViewerSession>>();
            AgentMembershipByRegion[pSession.RegionId] = agents;
        }
        if (!agents.TryGetValue(pSession.AgentId, out List<IVoiceViewerSession> sessions))
        {
            sessions = new List<IVoiceViewerSession>();
            agents[pSession.AgentId] = sessions;
        }
        // Reference-idempotent, matching the dictionary insert above (idempotent by key): a
        // double AddViewerSession of the same object must not double-count membership.
        if (!sessions.Contains(pSession))
            sessions.Add(pSession);
    }

    private static void IndexRemove(IVoiceViewerSession pSession)
    {
        if (!AgentMembershipByRegion.TryGetValue(pSession.RegionId, out Dictionary<UUID, List<IVoiceViewerSession>> agents))
            return;
        if (!agents.TryGetValue(pSession.AgentId, out List<IVoiceViewerSession> sessions))
            return;
        sessions.Remove(pSession);
        if (sessions.Count == 0)
        {
            agents.Remove(pSession.AgentId);
            if (agents.Count == 0)
                AgentMembershipByRegion.Remove(pSession.RegionId);
        }
    }

    /// Close-time teardown selection (OnClientClosed). Under the one registry lock, removes every
    /// session for this agent in this region whose generation token matches the dying login - or
    /// is UUID.Zero, a failed capture, which can only belong to an already-dead or now-dying login
    /// and is swept rather than stranded - from BOTH the registry and the membership index, parks
    /// it in ClosingSessions, and returns the captured references for the caller's asynchronous
    /// shutdown. After this returns no provision, hangup, or matrix read can find the captured
    /// sessions, and a provision racing the close cannot lose its NEW session to it: a successor
    /// login carries a different ClientSessionId and is never selected.
    public static List<IVoiceViewerSession> CaptureSessionsForClose(UUID pRegionId, UUID pAgentId, UUID pClientSessionId)
    {
        List<IVoiceViewerSession> captured = new List<IVoiceViewerSession>();
        lock (ViewerSessions)
        {
            if (!AgentMembershipByRegion.TryGetValue(pRegionId, out Dictionary<UUID, List<IVoiceViewerSession>> agents)
                || !agents.TryGetValue(pAgentId, out List<IVoiceViewerSession> sessions))
                return captured;

            for (int i = sessions.Count - 1; i >= 0; i--)
            {
                IVoiceViewerSession s = sessions[i];
                if (s.ClientSessionId != pClientSessionId && s.ClientSessionId != UUID.Zero)
                    continue;   // a different login's session (e.g. a racing successor) - never touch it
                sessions.RemoveAt(i);
                ViewerSessions.Remove(s.ViewerSessionID);
                ClosingSessions[s] = new ClosingInfo { ParkedTick = Environment.TickCount64 };
                captured.Add(s);
            }

            if (sessions.Count == 0)
            {
                agents.Remove(pAgentId);
                if (agents.Count == 0)
                    AgentMembershipByRegion.Remove(pRegionId);
            }
        }
        return captured;
    }

    /// A captured session's voice-service teardown succeeded - forget it.
    public static void CloseCompleted(IVoiceViewerSession pSession)
    {
        lock (ViewerSessions)
            ClosingSessions.Remove(pSession);
    }

    /// Retry hook: sessions for this agent still parked in ClosingSessions (teardown failed, or
    /// is still in flight). Returns a snapshot with each entry's age in milliseconds; entries
    /// stay parked until CloseCompleted. Callers (the provision/close hooks) re-drive Shutdown on
    /// these - the Janus shutdown gate serializes a retry racing an in-flight first attempt.
    public static List<(IVoiceViewerSession Session, long AgeMs)> GetClosingSessions(UUID pAgentId)
    {
        List<(IVoiceViewerSession Session, long AgeMs)> result = new List<(IVoiceViewerSession, long)>();
        long now = Environment.TickCount64;
        lock (ViewerSessions)
        {
            foreach (KeyValuePair<IVoiceViewerSession, ClosingInfo> kvp in ClosingSessions)
            {
                if (kvp.Key.AgentId == pAgentId)
                    result.Add((kvp.Key, now - kvp.Value.ParkedTick));
            }
        }
        return result;
    }

    /// Record why a parked session's teardown attempt failed, for the console snapshot and any
    /// later diagnosis. No-op if the session is no longer parked (a racing retry completed it).
    public static void RecordCloseFailure(IVoiceViewerSession pSession, string pReason)
    {
        lock (ViewerSessions)
        {
            if (ClosingSessions.TryGetValue(pSession, out ClosingInfo info))
                info.LastFailure = pReason;
        }
    }

    /// Full read-only snapshot of the closing-set for the "show voice closing" console command:
    /// every parked session across all agents, with age and the last recorded failure (null while
    /// the first attempt is still in flight).
    public static List<(UUID AgentId, string SessionId, long AgeMs, string LastFailure)> GetClosingSnapshot()
    {
        List<(UUID, string, long, string)> result = new List<(UUID, string, long, string)>();
        long now = Environment.TickCount64;
        lock (ViewerSessions)
        {
            foreach (KeyValuePair<IVoiceViewerSession, ClosingInfo> kvp in ClosingSessions)
                result.Add((kvp.Key.AgentId, kvp.Key.ViewerSessionID, now - kvp.Value.ParkedTick, kvp.Value.LastFailure));
        }
        return result;
    }
    // Get a viewer session by the viewer session ID
    public static bool TryGetViewerSession(string pViewerSessionId, out IVoiceViewerSession pViewerSession)
    {
        lock (ViewerSessions)
        {
            return ViewerSessions.TryGetValue(pViewerSessionId, out pViewerSession);
        }
    }
    // TryGetViewerSessionByAgentId was DELETED here: it returned a DEFERRED LINQ query that
    // callers enumerated outside the lock (a torn read / InvalidOperationException waiting to
    // happen), matched across all regions and all generations, and its only caller was the
    // dormant Event_OnRemovePresence handler. Close-time teardown uses CaptureSessionsForClose
    // instead - materialized under the lock, scoped by region and generation.

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
            IndexAdd(pSession);
        }
    }
    public static void RemoveViewerSession(string pSessionId)
    {
        lock (ViewerSessions)
        {
            // Remove the session's reference from the membership index — resolved before the
            // dictionary removal so the lookup still succeeds. A session ID with no entry
            // (double-remove, or a close-capture racing a hangup) leaves the index untouched.
            if (ViewerSessions.TryGetValue(pSessionId, out IVoiceViewerSession session))
            {
                ViewerSessions.Remove(pSessionId);
                IndexRemove(session);
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

