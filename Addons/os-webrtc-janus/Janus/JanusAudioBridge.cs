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
    // can be selected without a code change. Defaults to janus.plugin.audiobridge
    // where it is read (WebRtcJanusService), so behaviour is unchanged if unset.
    public JanusAudioBridge(JanusSession pSession, string pPluginName) : base(pSession, pPluginName)
    {
        // m_log.LogDebug("{0} JanusAudioBridge constructor (plugin={1})", LogHeader, pPluginName);
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
    public async Task<JanusRoom> CreateRoom(int pRoomId, bool pSpatial, string pRoomDesc)
    {
        JanusRoom ret = await CreateWithRecheck(() => TryCreateRoomOnce(pRoomId, pSpatial, pRoomDesc)).ConfigureAwait(false);
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
    private async Task<JanusRoom> TryCreateRoomOnce(int pRoomId, bool pSpatial, string pRoomDesc)
    {
        JanusRoom ret = null;
        try
        {
            JanusMessageResp resp = await SendPluginMsg(new AudioBridgeCreateRoomReq(pRoomId, pSpatial, pRoomDesc));
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

    // Calculate a room number for the given parameters. The room number is a hash of the parameters.
    // The attempt is to deterministicly create a room number so all regions will generate the
    //     same room number across sessions and across the grid.
    // getHashCode() is not deterministic across sessions.
    public static int CalcRoomNumber(string pRegionId, string pChannelType, int pParcelLocalID, string pChannelID)
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
                // A "multiagent" channel is unique to the grid
                // should add a GridId here
                hasher.Add(pChannelID);
                hasher.Add(pChannelType);
                break;
            default:
                throw new Exception("Unknown channel type: " + pChannelType);
        }   
        var hashed = hasher.Finish();
        // The "Abs()" is because Janus room number must be a positive integer
        // And note that this is the BHash.GetHashCode() and not Object.getHashCode().
        int roomNumber = Math.Abs(hashed.GetHashCode());
        return roomNumber;
    }
    public async Task<JanusRoom> SelectRoom(string pRegionId, string pChannelType, bool pSpatial, int pParcelLocalID, string pChannelID)
    {
        int roomNumber = CalcRoomNumber(pRegionId, pChannelType, pParcelLocalID, pChannelID);

        // Should be unique for the given use and channel type
        m_log.LogDebug("{0} SelectRoom: roomNumber={1}", LogHeader, roomNumber);

        string roomDesc = pRegionId + "/" + pChannelType + "/" + pParcelLocalID + "/" + pChannelID;
        // Coalesce concurrent creates of this room number across all per-session bridges
        // in this process. Each session still gets its OWN JanusRoom, bound to this
        // session's plugin handle, to join the (shared) Janus room with.
        return await SelectRoomCoalesced(
            roomNumber,
            () => CreateRoom(roomNumber, pSpatial, roomDesc),
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
            gate.Release();
        }
    }

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
