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

/// <summary>The connector's authorised scope. Only Estate exists in this plan (the estate voice
/// room); per-parcel scope rides the O-1 delivery gap and is out of scope (build plan §1).</summary>
public enum VoiceConnectorScope
{
    Estate = 0,
}

/// <summary>
/// S-CON-1 (Docs/voice/connector-build-plan.md; brief Amendment 2 D1): one voice-connector policy
/// record, loaded from a [VoiceConnector.&lt;name&gt;] ini section. Identity fields are immutable
/// (the record IS the operator's grant — brief D1: writing the ini is the authorisation); the
/// mutable slots at the bottom are runtime state for later slices (S-CON-2 registration) and are
/// guarded by the owning registry's lock, the A2ASessionRegistry shape (A2ASessionRegistry.cs).
/// </summary>
public sealed class VoiceConnectorRecord
{
    /// <summary>The section-name suffix ([VoiceConnector.Recorder] → "Recorder").</summary>
    public string Name { get; }
    public bool Enabled { get; }
    public string NpcFirstName { get; }
    public string NpcLastName { get; }
    /// <summary>Region-local position the NPC stands at — the parcel identity the visibility
    /// matrix reasons about (brief Amendment 1).</summary>
    public Vector3 Position { get; }
    public VoiceConnectorScope Scope { get; }
    /// <summary>false = recording only; S-CON-2 pushes a moderation mute for the NPC identity at
    /// registration (brief D2 — the mix silences it for every listener, no mixer change).</summary>
    public bool MayInject { get; }
    /// <summary>The authorising principal, as the operator recorded it (brief D1).</summary>
    public string AuthorisedBy { get; }
    /// <summary>S-CON-7: URL the sim POSTs the NPC's chat text to for TTS injection. Null when
    /// the key is absent (optional).</summary>
    public string InjectSourceUrl { get; }
    /// <summary>Optional region-name filter (S-CON-2): a region server hosts several regions and
    /// each gets its own non-shared module instance, so without this an enabled record would
    /// spawn its NPC in EVERY region. Null = every region (single-region instances).</summary>
    public string Region { get; }

    public string NpcFullName => $"{NpcFirstName} {NpcLastName}";

    // ---- Mutable runtime state, S-CON-2 onward. Written ONLY under the owning registry's
    // lock (VoiceConnectorRegistry), never by this class's consumers directly — the
    // A2ASessionRegistry discipline (immutable identity, lock-guarded mutable state).
    /// <summary>The NPC's agent id once created (S-CON-2); UUID.Zero until then. Public setter
    /// for the test fixtures; production writes come only from VoiceConnectorRegistrar.</summary>
    public UUID NpcId { get; set; }
    /// <summary>The registered voice session id once provisioned (S-CON-2); null until then.
    /// Public setter for the test fixtures, as NpcId.</summary>
    public string ViewerSessionId { get; set; }

    // Public so tests (and later slices) can construct records directly; production records
    // still come only from VoiceConnectorRegistry.LoadFrom, which owns every refusal rule.
    public VoiceConnectorRecord(string pName, bool pEnabled, string pFirst, string pLast,
        Vector3 pPosition, VoiceConnectorScope pScope, bool pMayInject, string pAuthorisedBy,
        string pInjectSourceUrl, string pRegion = null)
    {
        Region = pRegion;
        Name = pName;
        Enabled = pEnabled;
        NpcFirstName = pFirst;
        NpcLastName = pLast;
        Position = pPosition;
        Scope = pScope;
        MayInject = pMayInject;
        AuthorisedBy = pAuthorisedBy;
        InjectSourceUrl = pInjectSourceUrl;
        NpcId = UUID.Zero;
        ViewerSessionId = null;
    }
}
