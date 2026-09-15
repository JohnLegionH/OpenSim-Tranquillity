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

using System.Collections.Concurrent;
using System.Reflection;

using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;

namespace osWebRtcVoice;

// Encapsulization of a Session to the Janus server
public class JanusAudioBridge : JanusPlugin
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly string LogHeader = "[JANUS AUDIO BRIDGE]";

    // Wrapper around the session connection to Janus-gateway. The Janus plugin
    // name is supplied by the caller (from [JanusWebRtcVoice] PluginName) rather
    // than hardcoded, so a config-compatible mixer (e.g. janus.plugin.slvoice)
    // can be selected without a code change. Where it is read (WebRtcJanusService),
    // an unset key defaults to janus.plugin.slvoice since V-1 (janus.plugin.audiobridge before).
    public JanusAudioBridge(JanusSession pSession, string pPluginName) : this(pSession, pPluginName, string.Empty) { }

    /// <param name="pGridId">The grid's identity for non-spatial room numbers (S-A2A-4, O-35): see
    /// <see cref="ReadGridId"/>. Empty keeps the pre-S-A2A-4 (grid-less) derivation.</param>
    public JanusAudioBridge(JanusSession pSession, string pPluginName, string pGridId) : this(pSession, pPluginName, pGridId, false) { }

    /// <param name="pDeclareVisAuthority">Phase 0 slice 0.2: create spatial "local" rooms with vis_authority=true
    /// (nonspatial-phase0-design.md §6.2). False keeps the pre-0.2 create body.</param>
    public JanusAudioBridge(JanusSession pSession, string pPluginName, string pGridId, bool pDeclareVisAuthority) : base(pSession, pPluginName)
    {
        GridId = pGridId ?? string.Empty;
        DeclareVisAuthority = pDeclareVisAuthority;
        // m_log.LogDebug("{0} JanusAudioBridge constructor (plugin={1})", LogHeader, pPluginName);
    }

    /// <summary>The grid id folded into every "multiagent" room number by <see cref="SelectRoom"/>.</summary>
    public string GridId { get; }

    /// <summary>Slice 0.2: whether spatial "local" rooms are declared as having a sim authority.</summary>
    public bool DeclareVisAuthority { get; }

    /// <summary>Slice 0.2 §6.2: only spatial "local" rooms are declared. A2A "multiagent" rooms get no visibility batches,
    /// so declaring them would silence every call once fail-closed is enabled.</summary>
    public static bool ShouldDeclareVisAuthority(bool pDeclare, string pChannelType)
        => pDeclare && string.Equals(pChannelType, "local", StringComparison.Ordinal);

    /// <summary>
    /// The grid identifier for room derivation (S-A2A-4): the region's GatekeeperURI, read through the
    /// same section chain GridInfo uses for Scene.SceneGridInfo ([Const] / [Startup] / [Hypergrid],
    /// then [GatekeeperService] ExternalName, then [GridService] Gatekeeper), so it is the identity
    /// the region already presents on Hypergrid. Stable across restarts and identical for every
    /// region of the grid because it comes from the shared grid config; different per grid by
    /// construction. Normalised (trim, lower-case, no trailing slash) because the value is a hash
    /// input and two regions must not disagree over a spelling. No DNS resolution here (GridInfo
    /// resolves and throws; this must not take the voice service down). Empty when unconfigured.
    /// </summary>
    public static string ReadGridId(IConfigSource pConfig)
    {
        if (pConfig is null)
            return string.Empty;
        string[] sections = ["Const", "Startup", "Hypergrid"];
        string gatekeeper = Util.GetConfigVarFromSections<string>(pConfig, "GatekeeperURI", sections, string.Empty);
        if (string.IsNullOrEmpty(gatekeeper))
            gatekeeper = pConfig.Configs["GatekeeperService"]?.GetString("ExternalName", string.Empty);
        if (string.IsNullOrEmpty(gatekeeper))
            gatekeeper = pConfig.Configs["GridService"]?.GetString("Gatekeeper", string.Empty);
        if (string.IsNullOrEmpty(gatekeeper))
            return string.Empty;
        return gatekeeper.Trim().TrimEnd('/').ToLowerInvariant();
    }

    public override void Dispose()
    {
        if (IsConnected)
        {
            // Close the handle

        }
        base.Dispose();
    }

    public async Task<AudioBridgeResp> SendAudioBridgeMsg(PluginMsgReq pMsg)
    {
        AudioBridgeResp ret = null;
        try
        {
            ret = new AudioBridgeResp(await SendPluginMsg(pMsg));
        }
        catch (Exception e)
        {
            m_log.LogError("{0} SendPluginMsg. Exception {1}", LogHeader, e);
        }
        return ret;
    }

    /// <summary>
    /// Create a room with the given criteria. This talks to Janus to create the room.
    /// If the room with this RoomId already exists, just return it.
    /// Janus could create and return the RoomId but this presumes that the Janus server
    /// is only being used for our voice service.
    /// </summary>
    /// <param name="pRoomId">integer room ID to create</param>
    /// <param name="pSpatial">boolean on whether room will be spatial or non-spatial</param>
    /// <param name="pRoomDesc">added as "description" to the created room</param>
    /// <returns></returns>
    // Create room 'pRoomId', treating an already-existing room (Janus error 486) as
    // reuse. If the first attempt is inconclusive (non-486 error, unexpected return
    // code, or an exception), re-attempt ONCE: under a cross-PROCESS race (another
    // regionserver created the room in the same instant) the room now exists and the
    // re-attempt returns 486 -> reuse. No in-process lock can cover that cross-process
    // case, so this re-check is the load-bearing correctness. Janus keys rooms by
    // number, so re-attempting create cannot produce a duplicate room.
    public Task<JanusRoom> CreateRoom(int pRoomId, bool pSpatial, string pRoomDesc)
        => CreateRoom(pRoomId, pSpatial, pRoomDesc, false);

    /// <param name="pVisAuthority">Slice 0.2: send "vis_authority": true in the create body.</param>
    public async Task<JanusRoom> CreateRoom(int pRoomId, bool pSpatial, string pRoomDesc, bool pVisAuthority)
    {
        JanusRoom ret = await CreateWithRecheck(() => TryCreateRoomOnce(pRoomId, pSpatial, pRoomDesc, pVisAuthority)).ConfigureAwait(false);
        if (ret is null)
        {
            m_log.LogError("{LogHeader} CreateRoom. Room {RoomId} creation failed after re-check", LogHeader, pRoomId);
        }
        return ret;
    }

    // Run a create attempt; if it comes back null (inconclusive), run it once more.
    // Pure orchestration (no Janus dependency) so the retry policy is unit-testable.
    public static async Task<JanusRoom> CreateWithRecheck(Func<Task<JanusRoom>> pAttempt)
    {
        JanusRoom ret = await pAttempt().ConfigureAwait(false);
        if (ret is null)
        {
            // Inconclusive first attempt -> the room may already exist (a cross-process
            // create won the race). Re-attempt; a now-existing room returns 486 -> reuse.
            ret = await pAttempt().ConfigureAwait(false);
        }
        return ret;
    }

    // A single create attempt. Returns a JanusRoom on "created" or 486 (already exists
    // -> reuse); null on any other error / unexpected return / exception (the caller
    // may re-check for a cross-process create).
    private async Task<JanusRoom> TryCreateRoomOnce(int pRoomId, bool pSpatial, string pRoomDesc, bool pVisAuthority)
    {
        JanusRoom ret = null;
        try
        {
            JanusMessageResp resp = await SendPluginMsg(new AudioBridgeCreateRoomReq(pRoomId, pSpatial, pRoomDesc, pVisAuthority));
            AudioBridgeResp abResp = new AudioBridgeResp(resp);

            m_log.LogDebug("{0} CreateRoom. ReturnCode: {1}", LogHeader, abResp.AudioBridgeReturnCode);
            switch (abResp.AudioBridgeReturnCode)
            {
                case "created":
                    ret = new JanusRoom(this, pRoomId);
                    break;
                case "event":
                    if (abResp.AudioBridgeErrorCode == 486)
                    {
                        m_log.LogWarning("{0} CreateRoom. Room {1} already exists. Reusing! {2}", LogHeader, pRoomId, abResp.ToString());
                        // if room already exists, just use it
                        ret = new JanusRoom(this, pRoomId);
                    }
                    else
                    {
                        m_log.LogError("{LogHeader} CreateRoom. XX Room creation inconclusive: {AudioBridgeResponse}", LogHeader, abResp.ToString());
                    }
                    break;
                default:
                    m_log.LogError("{LogHeader} CreateRoom. YY Room creation inconclusive: {AudioBridgeResponse}", LogHeader, abResp.ToString());
                    break;
            }
        }
        catch (Exception e)
        {
            m_log.LogError("{0} CreateRoom. Exception {1}", LogHeader, e);
        }
        return ret;
    }

    public async Task<bool> DestroyRoom(JanusRoom janusRoom)
    {
        bool ret = false;
        try
        {
            JanusMessageResp resp = await SendPluginMsg(new AudioBridgeDestroyRoomReq(janusRoom.RoomId));
            ret = true;
            // Keep the process-wide existence hint consistent if a room is ever destroyed.
            ForgetRoom(janusRoom.RoomId);
        }
        catch (Exception e)
        {
            m_log.LogError("{0} DestroyRoom. Exception {1}", LogHeader, e);
        }
        return ret;
    }

    // Constant used to denote that this is a spatial audio room for the region (as opposed to parcels)
    public const int REGION_ROOM_ID = -999;

    // Room EXISTENCE is grid-global (Janus is the source of truth), so it is tracked
    // PROCESS-WIDE, not per session. Per-session AudioBridge instances keep only handle
    // state (each builds its own JanusRoom bound to its plugin handle to join with).
    // These statics coalesce concurrent creation of the same room number in this process
    // so N same-process racers collapse to ONE Janus create; the cross-PROCESS race is
    // covered by CreateRoom's 486 re-check. Limits: coalescing is per-process only, and
    // _knownRooms is a best-effort hint, invalidated on DestroyRoom and on a JoinRoom
    // failure (see ForgetRoom). If a room is destroyed out-of-band the join fails,
    // ForgetRoom clears the hint, and the viewer's provision retry re-creates the room
    // rather than looping forever skipping the create on a stale hint.
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> _roomCreateLocks = new();
    private static readonly ConcurrentDictionary<int, bool> _knownRooms = new();

    // O-60 (audit W-13): both tables used to grow by one entry per room number ever selected (every parcel,
    // every A2A pair) for the life of the process. Entries idle for RoomCreateLockIdle are swept at most once
    // a minute from SelectRoomCoalesced. Evicting is safe: a forgotten "exists" hint only costs one create
    // that answers 486 (treated as success), and a swept gate is simply re-created on the next select; the
    // one race - a caller that fetched a gate just before it was swept - can at worst issue a duplicate
    // create, which the same 486 re-check absorbs (the cross-process case already relies on it).
    public static readonly TimeSpan RoomCreateLockIdle = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<int, long> _roomLastUseMs = new();
    private static long _lastRoomLockSweepMs = Environment.TickCount64;

    // Calculate a room number for the given parameters. The room number is a hash of the parameters.
    // The attempt is to deterministicly create a room number so all regions will generate the
    //     same room number across sessions and across the grid.
    // getHashCode() is not deterministic across sessions.
    /// <param name="pGridId">Enters ONLY the "multiagent" arm (S-A2A-4). The "local" arm is per
    /// region+parcel and ignores it, so no spatial room number moved when it was introduced.</param>
    public static int CalcRoomNumber(string pGridId, string pRegionId, string pChannelType, int pParcelLocalID, string pChannelID)
    {
        var hasher = new BHasherMdjb2();
        // If there is a channel specified it must be group
        switch (pChannelType)
        {
            case "local":
                // A "local" channel is unique to the region and parcel
                hasher.Add(pRegionId);
                hasher.Add(pChannelType);
                hasher.Add(pParcelLocalID);
                break;
            case "multiagent":
                // A "multiagent" channel is unique to the grid: grid id + channel (the A2A session
                // id) + type. Ledger O-35 ("should add a GridId here") is CLOSED for multiagent by
                // S-A2A-4; it stays OPEN for any future channel_type added below, which must fold
                // the grid id in the same way before it is admitted. An empty grid id (unconfigured
                // grid) reproduces the pre-S-A2A-4 derivation exactly (nothing added); the service
                // warns once at start.
                if (!string.IsNullOrEmpty(pGridId))
                    hasher.Add(pGridId);
                hasher.Add(pChannelID);
                hasher.Add(pChannelType);
                break;
            default:
                throw new Exception("Unknown channel type: " + pChannelType);
        }   
        var hashed = hasher.Finish();
        // The "Abs()" is because Janus room number must be a positive integer
        // And note that this is the BHash.GetHashCode() and not Object.getHashCode().
        int roomNumber = FoldHashToRoom(hashed.GetHashCode());
        return roomNumber;
    }

    // Fold a 32-bit hash to a POSITIVE Janus room number. Math.Abs(int.MinValue) throws
    // OverflowException (there is no positive int.MinValue), and because the hash inputs are stable
    // per agent+parcel, one unlucky combination would crash provisioning on that parcel forever
    // (ledger O-33). Redirect ONLY int.MinValue to a valid positive room (int.MaxValue); Math.Abs is
    // kept verbatim for every other value, so NO existing (region,parcel)->room mapping changes
    // (int.MinValue never yielded a room before -- it threw). Extracted so the guard is unit-testable.
    public static int FoldHashToRoom(int hashCode)
        => hashCode == int.MinValue ? int.MaxValue : Math.Abs(hashCode);
    public async Task<JanusRoom> SelectRoom(string pRegionId, string pChannelType, bool pSpatial, int pParcelLocalID, string pChannelID)
    {
        int roomNumber = CalcRoomNumber(GridId, pRegionId, pChannelType, pParcelLocalID, pChannelID);

        // Should be unique for the given use and channel type
        m_log.LogDebug("{0} SelectRoom: roomNumber={1}", LogHeader, roomNumber);

        string roomDesc = pRegionId + "/" + pChannelType + "/" + pParcelLocalID + "/" + pChannelID;
        bool visAuthority = ShouldDeclareVisAuthority(DeclareVisAuthority, pChannelType);
        // Coalesce concurrent creates of this room number across all per-session bridges
        // in this process. Each session still gets its OWN JanusRoom, bound to this
        // session's plugin handle, to join the (shared) Janus room with.
        return await SelectRoomCoalesced(
            roomNumber,
            () => CreateRoom(roomNumber, pSpatial, roomDesc, visAuthority),
            () => new JanusRoom(this, roomNumber)).ConfigureAwait(false);
    }

    // Process-wide coalescing of room creation by room number. Under the per-room lock:
    // if the room is already known to exist in this process, skip the create and let the
    // caller build a join object (pMakeExistingJoinObject); otherwise create exactly once
    // (pCreate) and record it. Func-based and static so it is unit-testable without Janus.
    public static async Task<JanusRoom> SelectRoomCoalesced(
        int pRoomNumber,
        Func<Task<JanusRoom>> pCreate,
        Func<JanusRoom> pMakeExistingJoinObject)
    {
        long now = Environment.TickCount64;
        MaybeSweepRoomCreateLocks(now);
        _roomLastUseMs[pRoomNumber] = now;
        SemaphoreSlim gate = _roomCreateLocks.GetOrAdd(pRoomNumber, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_knownRooms.ContainsKey(pRoomNumber))
            {
                return pMakeExistingJoinObject();
            }
            JanusRoom created = await pCreate().ConfigureAwait(false);
            if (created is not null)
            {
                _knownRooms[pRoomNumber] = true;
            }
            return created;
        }
        finally
        {
            _roomLastUseMs[pRoomNumber] = Environment.TickCount64;
            gate.Release();
        }
    }

    // O-60 (audit W-13): at most once a minute, drop the gate and the "exists" hint of every room number idle
    // for RoomCreateLockIdle (see the field comment for why eviction is safe).
    private static void MaybeSweepRoomCreateLocks(long pNowMs)
    {
        long last = Interlocked.Read(ref _lastRoomLockSweepMs);
        if (pNowMs - last < 60_000 || Interlocked.CompareExchange(ref _lastRoomLockSweepMs, pNowMs, last) != last)
            return;
        SweepRoomCreateLocks(pNowMs, RoomCreateLockIdle);
    }

    /// Evict every room number whose last select is older than pIdle and whose gate is not held right now.
    /// Returns how many were evicted. Public so the bound is unit-testable with an explicit clock.
    public static int SweepRoomCreateLocks(long pNowMs, TimeSpan pIdle)
    {
        int evicted = 0;
        foreach (KeyValuePair<int, long> kvp in _roomLastUseMs)
        {
            if (pNowMs - kvp.Value < (long)pIdle.TotalMilliseconds)
                continue;
            if (_roomCreateLocks.TryGetValue(kvp.Key, out SemaphoreSlim held) && held.CurrentCount == 0)
                continue;   // a select for this room is in progress
            _roomCreateLocks.TryRemove(kvp.Key, out _);
            _knownRooms.TryRemove(kvp.Key, out _);
            _roomLastUseMs.TryRemove(kvp.Key, out _);
            evicted++;
        }
        return evicted;
    }

    /// Room numbers currently holding a create gate (diagnostics and tests).
    public static int RoomCreateLockCount => _roomCreateLocks.Count;

    /// Whether this process currently holds the "room exists" hint for a room number (diagnostics and tests).
    public static bool IsRoomKnown(int pRoomNumber) => _knownRooms.ContainsKey(pRoomNumber);

    // Drop the process-wide "exists" hint for a room number. Called when a join fails
    // (e.g. the room was destroyed out-of-band) so the next SelectRoom re-creates the
    // room instead of repeatedly trying to join a gone one.
    public static void ForgetRoom(int pRoomNumber)
    {
        _knownRooms.TryRemove(pRoomNumber, out _);
    }

    public override void Handle_Event(JanusMessageResp pResp)
    {
        base.Handle_Event(pResp);
        AudioBridgeResp abResp = new AudioBridgeResp(pResp);
        if (abResp is not null && abResp.AudioBridgeReturnCode == "event")
        {
            // An audio bridge event!
            m_log.LogDebug("{0} Handle_Event. {1}", LogHeader, abResp.ToString());
        }

    }
    public override void Handle_Message(JanusMessageResp pResp)
    {
        base.Handle_Message(pResp);
        AudioBridgeResp abResp = new AudioBridgeResp(pResp);
        if (abResp is not null && abResp.AudioBridgeReturnCode == "event")
        {
            // An audio bridge event!
            m_log.LogDebug("{0} Handle_Event. {1}", LogHeader, abResp.ToString());
        }

    }
}
