# Playback credential server contract (#153-A0)

The authoritative server-side contract for the two credentials that replace the durable session
token in playback and WebSocket URLs. The decision that produced it — candidates A through D, and
why C wins with B bounded to same-origin — lives in the web repository at
`docs/tesserafin/playback-credential-contract.md`. This document freezes what the **server**
promises: scope, lifetime, renewal, revocation, replay boundary, restart behaviour and the exact
limits of what a single-process server can enforce.

It contains no credential, no URL and no media path.

Nothing here claims #153 is fixed. A0 builds the primitives and their contract. The exposure closes
only when the web consumers stop putting the durable token in URLs, which is a later stage.

## What the inventory measured

`ci/credential-exposure-inventory.py` is a gate, not a report: it fails the build if any named
category resolves to zero hits, because a pattern that silently stops matching while the exposure
survives is the failure this design cannot afford. Twenty-one categories, all populated.

Four measured facts shape everything below.

**1. The query credential is not media-scoped.** `AuthorizationContext` reads `ApiKey` (and, under
`EnableLegacyAuthorization`, `api_key`) from the query string at two lines, before any endpoint is
known, and resolves it against the `Devices` table for *every* route. `ItemsController` and
`VideosController` both carry a bare `[Authorize]`. There is no attribute, policy or endpoint
metadata anywhere in the tree that distinguishes "media bytes" from "general API". **Creating that
distinction is the load-bearing part of A0**, not the minting endpoints.

**2. The HLS surface is real but invisible to the contract.** `DynamicHlsController` and
`HlsSegmentController` are both `[ApiExplorerSettings(IgnoreApi = true)]`. Fourteen routes —
`master.m3u8`, `main.m3u8`, `live.m3u8`, `hls1/{playlistId}/{segmentId}.{container}` and the legacy
`hls/` shapes — serve media and appear nowhere in `openapi/openapi.json`. The capability has to
reach them without any contract change describing them, and the OpenAPI diff for A0 therefore
cannot be read as the list of routes the capability protects.

**3. Sessions are in-process.** `SessionManager` holds `_activeConnections` as a
`ConcurrentDictionary<string, SessionInfo>`. Sessions do not survive a restart and are not shared
between instances. Every lifetime and revocation promise below is bounded by that, and the store
this contract introduces deliberately matches it. A database-backed capability would outlive the
in-memory session it is bound to and could not be validated against it after a restart — durability
the server cannot honour is worse than none.

**4. The web client does not build the credential URL itself.** The only `ApiKey=`/`api_key=`
occurrences under web `src/` are *comments describing* the defect. The construction is inside
`jellyfin-apiclient`, a prebuilt dependency bundle, in `getUrl` and `openWebSocket`. This is why A0
is server-only: there is no web line to edit that would change the transport.

## The two credentials

They are different types with different namespaces, different stores, different lifetimes and
different consumption rules. Neither is ever accepted by `AuthorizationContext`.

### Playback capability

| | |
|---|---|
| query parameter | `playbackCapability` |
| entropy | 256 bits from `RandomNumberGenerator`, base64url, no padding |
| at rest | SHA-256 verifier only; the presented value is never stored, logged or returned twice |
| bound to | user id, device id, session id, play-session id, item id, media-source id, scope set |
| lifetime | **15 minutes** from issue |
| renewal window | the final **5 minutes** only |
| replay boundary | anyone holding it can fetch that item's media, in those scopes, until it expires |

Minting is `POST /Playback/Capabilities`, authenticated by the durable session token **in a header**.
There is no path by which a capability mints or renews another capability, and no path by which a
URL credential mints anything.

**Renewal is deliberately narrow.** Before the last five minutes, renewal is rejected as premature —
otherwise a client could chain renewals continuously and turn a short-lived credential into a
durable one with extra steps. After expiry, renewal is rejected outright: an expired capability is
not resurrectable, and the client must mint a new one with the durable token. There is **no**
fallback to `ApiKey`/`api_key` on expiry, rejection or renewal failure, at any layer.

**Scopes.** A capability carries a set, and each protected route demands one member:

| scope | routes | item-bound |
|---|---|---|
| `Media` | direct stream, `stream.{container}`, universal, every HLS playlist and segment | yes |
| `Subtitles` | subtitle streams and the subtitle `.m3u8` | yes |
| `Attachments` | `/Videos/{videoId}/{mediaSourceId}/Attachments/{index}` | yes |
| `Trickplay` | trickplay tiles and `tiles.m3u8` | yes |
| `Fonts` | `/FallbackFont/Fonts`, `/FallbackFont/Fonts/{name}` | **no** |

`Fonts` is the "narrowed further" case the brief asks for and the reason scope is a set rather than
a single item binding: a fallback font is not an item's media and has no media source, so a
font-scoped capability carries no item binding and grants nothing else. A capability whose scope set
omits the scope a route demands is rejected even when its user, session, item and media source all
match.

### WebSocket ticket

| | |
|---|---|
| query parameter | `webSocketTicket` |
| entropy | 256 bits, same source, separate namespace |
| at rest | SHA-256 verifier only |
| bound to | user id, device id, session id |
| lifetime | **30 seconds** |
| consumption | exactly one *successful* upgrade; removed atomically before the socket is accepted |
| replay | second presentation is rejected, whether or not the first socket is still open |

Minted by `POST /WebSocket/Tickets`, durable token in a header. Valid **only** during a WebSocket
upgrade: `WebSocketManager` is the single consumer. Presented to any HTTP route — media or general —
it is not a token, does not reach `AuthorizationContext`, and yields 401.

Single use is enforced by `ConcurrentDictionary.TryRemove`, which is atomic. That is a real
guarantee inside one process and **only** inside one process; see the limits below.

## Why neither credential can grant general API access

Not by convention, and not by remembering to check. Structurally:

`AuthorizationContext` reads exactly two query keys, `ApiKey` and `api_key`. It is not taught about
`playbackCapability` or `webSocketTicket` and must never be. A capability presented to
`/Items` is therefore not a credential at all — `HasToken` is false, `CustomAuthenticationHandler`
returns `NoResult`, and the request is unauthenticated. The rejection needs no per-endpoint list to
maintain and cannot be defeated by adding a controller.

The converse direction is enforced by a dedicated authentication scheme and policy that only the
media routes carry. A durable session token continues to work on those routes during A0 — legacy
clients are not broken by this stage — but the new types never widen.

**Consequence, stated rather than glossed:** until the web consumers migrate, the durable token is
still accepted in media URLs. A0 adds a narrow door; it does not yet close the wide one.

## Revocation

Every seam already exists in `SessionManager`. No new lifecycle is invented.

| event | seam | effect |
|---|---|---|
| logout | `Logout(string accessToken)`, `Logout(Device)` | every capability and ticket for that session |
| device deletion | `Logout(Device)` via device removal | same |
| password change | `RevokeUserTokens(userId, currentAccessToken)` | every capability and ticket for that user's other sessions |
| session end | `ReportSessionEnded` → `OnSessionEnded` → `SessionEnded` | same |
| play-session end | `OnPlaybackStopped` / play-session termination | **only** that play session's capabilities; other concurrent play sessions of the same user are untouched |

Revocation is by binding, not by enumeration: the store is keyed so that a session id or play-session
id removes exactly its own entries.

## Restart and multi-instance — the limits

Both stores are in-memory, singleton, and die with the process.

* **Restart.** Every capability and ticket is gone. In-flight playback fails its next segment
  request with `PlaybackCapabilityUnknown` and the client mints a new one with its durable token,
  which survives because it is in the database. Sessions are lost on restart today for the same
  reason, so this is the existing behaviour, not a new one.
* **Multiple instances.** Nothing is shared. A capability minted by instance A is unknown to
  instance B, and the single-use ticket guarantee holds per process. This server does not run
  multi-instance today — `_activeConnections` is already per-process, so a load-balanced pair would
  already disagree about sessions. **This contract does not invent distributed state to paper over
  that.** If multi-instance is ever wanted, sessions and capabilities have to move together, and
  that is a different change with a different review.

## Error vocabulary

Deterministic, mapped one-to-one, and never echoing any part of a presented value.

Playback capability — HTTP 401 unless noted:
`PlaybackCapabilityMissing`, `PlaybackCapabilityUnknown`, `PlaybackCapabilityExpired`,
`PlaybackCapabilityRevoked`, `PlaybackCapabilityScopeMismatch` (403),
`PlaybackCapabilityItemMismatch` (403), `PlaybackCapabilityMediaSourceMismatch` (403),
`PlaybackCapabilitySessionMismatch` (403), `PlaybackCapabilityRenewalTooEarly` (400),
`PlaybackCapabilityRenewalAfterExpiry` (401).

WebSocket ticket — HTTP 401: `WebSocketTicketMissing`, `WebSocketTicketUnknown`,
`WebSocketTicketExpired`, `WebSocketTicketAlreadyUsed`, `WebSocketTicketSessionMismatch`,
`WebSocketTicketDeviceMismatch`.

*Unknown* and *expired* are distinct internally because the tests must tell them apart; both answer
401 with no distinguishing body, so a caller cannot use the response to probe which capabilities
exist.

## Candidate B — the bounded same-origin cookie

Retained, and bounded so it can never become a silent fallback.

* **Off by default.** It activates only when the operator sets the configuration flag **and** the
  request's `Origin` matches the server's own origin. Both, not either.
* **Never a fallback.** It is not consulted when a capability is missing, expired, revoked or
  scope-mismatched. A capability failure is answered as a capability failure. A cookie that could
  rescue an expired capability would mask exactly the expiry this design exists to enforce.
* **Account isolation is a browser property, not a server one.** One cookie jar per browser profile,
  so two accounts signed in to one browser collide. That is why it cannot be the primary transport,
  and why it stays off unless an operator accepts it.
* **Cross-origin deployments are excluded by construction.** A separately hosted web client never
  activates it, so third-party cookie rules and `SameSite` cannot silently break playback.

## Testability

`TimeProvider` is already registered as a singleton (`TimeProvider.System`) and
`AdvancingTimeProvider` already exists as the test double. Expiry, the renewal window and ticket
lifetime are read from the injected `TimeProvider` **at validation**, not only at minting — an
expiry test that advances a clock the validator never reads is inert and would pass while proving
nothing. Randomness is behind its own injectable boundary for the same reason.

**No test sleeps.** Not one.

## What A0 does not do

No web runtime migration. No replacement of the web URL builders. No live WebSocket consumer switch.
No removal of the legacy query-token path. No claim that #153 is closed.
