# WebRTC voice: security

What the addon protects, which secrets it uses, and what it accepts from whom. Settings are in
[configuration.md](configuration.md).

## Secrets

| Secret | Setting | Rule |
|---|---|---|
| Janus API secret | `[JanusWebRtcVoice] APIToken` | Must equal the gateway's. Use 32 or more random characters; a shorter one logs a warning at start naming the key (never the value or its length) and the service starts as before. |
| Janus admin secret | `[JanusWebRtcVoice] AdminAPIToken` | Same rule. Also used to send visibility and position messages to the mixer. |
| Join-capability secret | `[JanusWebRtcVoice] JoinCapabilitySecret` | Must equal the mixer's. With `JoinCapabilityEnabled` on, a value that is empty or under 32 characters is refused at start with an error, and no capability is minted. |
| Cross-process carrier secret | `[WebRtcVoice] CarrierSecret` | 32 or more characters, the same on every region server. A shorter value is refused at start with an error. Separate from the join-capability secret. |
| Call session service password | `HttpAuthPassword` | 32 or more characters, on the service and on each region server. The service will not load with a shorter one. |
| Connector join-capability secret | `[VoiceConnector.<name>] CapabilitySecret` | 32 or more characters, or it is ignored with a warning. |

Keep every one of these out of version control. The addon never writes a secret to the log: with `MessageDetails` on, the
API and admin secrets are hidden in what is logged, a join capability and its secret are never logged at any level, and a
URL is logged without its query string. A shared secret in the region server's `.ini` is readable by anyone who can read
that file, so restrict it.

## The connection to the gateway

Every request to the gateway carries the API secret or the admin secret. Over plain `http://` those cross the network in
clear. A gateway on another host therefore needs `https://` URLs, or a network that only you control (a private LAN or a
VPN). A gateway reached on the same machine through its loopback address needs neither.

- An `https://` URL is checked like any other HTTPS connection: the certificate must chain to a root the operating system
  trusts and must name the host in the URL. There is no setting that turns the check off. A certificate that fails it is
  refused, nothing is sent to that server, and the log says why (`[JANUS TLS] the certificate presented by ... was rejected`).
- If the gateway's certificate comes from your own CA, or is self-signed, set `JanusGatewayCACertificateFile` to the file
  holding that CA's certificate (PEM or DER). It is trusted alongside the system roots, only for the connection to the
  gateway, and the host name must still match. If the file is missing or unreadable, an error naming the key is logged
  and only the system roots are used.
- Leave `JanusGatewayCACertificateFile` unset when the certificate is from a public CA.
- The admin API controls the gateway. Expose its port only to the region servers.

## The join capability

With `JoinCapabilityEnabled` (slvoice mixer only) the region signs a capability for every join: an HMAC-SHA256 over the
agent, the viewer session, the room, the arming state the region believes current, an issue and an expiry time (60 seconds) and a
one-time nonce. The mixer verifies it. The viewer never sees it: the region's own Janus session performs the join, so the
capability travels from region to mixer only. It is defence in depth against a leaked API secret: a mixer set to require it
refuses a join from a client that has the API secret but cannot sign a capability. A capability admits a join and grants no
audibility on its own.

A connector program, which joins the mixer from outside the region, fetches its capability from the region
instead: `POST /voice/connector/<name>/join-cap` with `Authorization: Bearer <CapabilitySecret>`. Every failure before the
bearer is accepted (unknown name, inactive record, no secret, wrong bearer, wrong method) is an empty 404; after it, an empty
503 when the region cannot mint. The endpoint exists only while a record has a `CapabilitySecret`. Put it behind TLS or a
private network.

With the stock AudioBridge the switch is turned off at start with a warning; the plugin cannot check a capability.

## What each part accepts, and from whom

| Part | Accepts | From | Checked by |
|---|---|---|---|
| Voice provisioning (region HTTP capability) | A voice request for a parcel, call or conference | The viewer of the agent the capability was issued to | The capability URL itself; then admission, below |
| Gateway REST and admin API | Requests from the region | The region server (or Robust, if it hosts the Janus service) | The API and admin secrets |
| Call session service (Robust) | Session operations | Region servers | HTTP basic authentication; the password is at least 32 characters. It is not covered by `ControlPlaneTrustedHosts` |
| Group and conference rings, conference text between processes | A signed grid instant message | Another region server | `CarrierSecret` signature, or in `grid` mode the service |
| Region control-plane endpoints (agent, object and neighbour calls) | Requests from other region servers and the grid server | Addresses in `[Network] ControlPlaneTrustedHosts`, loopback and the machine's own `hostname` | The core server (`Docs/security/Hardening.md`). The voice addon does not read this list |
| Moderation requests | Mute, unmute, mute-all | The viewer | A server-side power check on every request |

### Who is admitted to a voice room

- **Spatial voice.** The parcel comes from the avatar's position for a root agent; the viewer's own parcel id is only a
  hint, logged when it differs, and never used to join another parcel's room. A parcel with voice off, an estate with voice
  off, and an avatar banned from or not allowed on the parcel are refused, on every estate including a TaxFree one. The parcel
  owner, estate managers and administrators are exempt from the ban list. An NPC is refused unless `AllowNpcVoice` is on
  or it is a registered connector.
- **Avatar-to-avatar call.** Only the two parties, holding the call's token, are admitted. A provision that names a call
  that does not exist, or one the agent is not a party to, is refused 403. The token is compared in constant time and is
  never logged.
- **Group call.** The agent must be a member of the group and hold the powers the call needs (`GP_SESSION_JOIN`, and
  `GP_SESSION_VOICE` unless `GroupVoiceRequireVoicePower` is `false`), hold the token, and fit under the seat cap.
  Membership and powers are checked at the call and again at the provision.
- **Conference.** The agent must have been invited and hold the conference's token. Being invited is the membership.
- **Any other `channel_type`** is refused 403. When a call feature is on, a chat session request (start, call, accept) from a
  child agent, which is an avatar's connection to a neighbouring region, is refused 403.
- A removed member is refused every way back into the call (see [moderation.md](moderation.md)).

### Limits that bound abuse

A process or service holds at most `MaxNonSpatialSessions` sessions, one agent may start at most `MaxSessionsPerAgent`, and
"start conference" and "invite" are rate-limited per agent (`ConferenceRequestBurst`, `ConferenceRequestRefillSeconds`). A
request that would ring more new people than the conference has room for is refused whole. Unanswered invitations expire.
A refused provision is answered from a short cache (`RefusalCacheSeconds`) so a viewer that retries at once does not
repeat the checks.
